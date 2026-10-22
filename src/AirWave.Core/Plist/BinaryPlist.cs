using System.Buffers.Binary;
using System.Text;

namespace AirWave.Core.Plist;

/// <summary>
/// Minimal Apple binary property list (bplist00) reader and writer.
/// Values map to: Dictionary&lt;string, object&gt;, List&lt;object&gt;, string, long, ulong, double, bool, byte[], DateTime.
/// </summary>
public static class BinaryPlist
{
    // ----------------------------------------------------------------- writing

    private sealed class Node
    {
        public object Value;          // scalar value (already normalised) or null for containers
        public byte Kind;             // 0 null,1 bool,2 int,3 uint,4 real,5 data,6 string,7 date,8 array,9 dict
        public List<Node> Children;   // array items, or dict keys followed by values
        public int Index;
    }

    public static byte[] Write(object root)
    {
        var all = new List<Node>();
        Build(root, all);

        int refSize = all.Count < 256 ? 1 : all.Count < 65536 ? 2 : 4;
        using var ms = new MemoryStream();
        ms.Write("bplist00"u8);
        var offsets = new long[all.Count];
        foreach (var n in all)
        {
            offsets[n.Index] = ms.Position;
            WriteNode(ms, n, refSize);
        }
        long offsetTableStart = ms.Position;
        int offSize = offsetTableStart < 256 ? 1 : offsetTableStart < 65536 ? 2 : offsetTableStart < 4294967296L ? 4 : 8;
        foreach (var o in offsets) WriteSized(ms, (ulong)o, offSize);

        Span<byte> trailer = stackalloc byte[32];
        trailer.Clear();
        trailer[6] = (byte)offSize;
        trailer[7] = (byte)refSize;
        BinaryPrimitives.WriteUInt64BigEndian(trailer[8..], (ulong)all.Count);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[16..], 0);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[24..], (ulong)offsetTableStart);
        ms.Write(trailer);
        return ms.ToArray();
    }

    private static Node Build(object o, List<Node> all)
    {
        var n = new Node { Index = all.Count };
        all.Add(n);
        switch (o)
        {
            case null: n.Kind = 0; break;
            case bool b: n.Kind = 1; n.Value = b; break;
            case int i: n.Kind = 2; n.Value = (long)i; break;
            case long l: n.Kind = 2; n.Value = l; break;
            case uint u: n.Kind = 2; n.Value = (long)u; break;
            case ushort us: n.Kind = 2; n.Value = (long)us; break;
            case byte by: n.Kind = 2; n.Value = (long)by; break;
            case ulong ul: n.Kind = ul <= long.MaxValue ? (byte)2 : (byte)3; n.Value = ul <= long.MaxValue ? (object)(long)ul : ul; break;
            case double d: n.Kind = 4; n.Value = d; break;
            case float f: n.Kind = 4; n.Value = (double)f; break;
            case byte[] data: n.Kind = 5; n.Value = data; break;
            case string s: n.Kind = 6; n.Value = s; break;
            case DateTime dt: n.Kind = 7; n.Value = dt; break;
            case Dictionary<string, object> dict:
                n.Kind = 9;
                n.Children = new List<Node>();
                foreach (var kv in dict) n.Children.Add(Build(kv.Key, all));
                foreach (var kv in dict) n.Children.Add(Build(kv.Value, all));
                break;
            case System.Collections.IEnumerable list:
                n.Kind = 8;
                n.Children = new List<Node>();
                foreach (var item in list) n.Children.Add(Build(item, all));
                break;
            default:
                throw new NotSupportedException($"plist: can't write {o.GetType().Name}");
        }
        return n;
    }

    private static void WriteNode(Stream s, Node n, int refSize)
    {
        switch (n.Kind)
        {
            case 0: s.WriteByte(0x00); break;
            case 1: s.WriteByte((bool)n.Value ? (byte)0x09 : (byte)0x08); break;
            case 2:
            {
                long v = (long)n.Value;
                if (v < 0) { s.WriteByte(0x13); WriteSized(s, (ulong)v, 8); }
                else if (v <= 0xff) { s.WriteByte(0x10); s.WriteByte((byte)v); }
                else if (v <= 0xffff) { s.WriteByte(0x11); WriteSized(s, (ulong)v, 2); }
                else if (v <= 0xffffffffL) { s.WriteByte(0x12); WriteSized(s, (ulong)v, 4); }
                else { s.WriteByte(0x13); WriteSized(s, (ulong)v, 8); }
                break;
            }
            case 3: s.WriteByte(0x14); WriteSized(s, 0, 8); WriteSized(s, (ulong)n.Value, 8); break;
            case 4:
            {
                s.WriteByte(0x23);
                Span<byte> rb = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(rb, (double)n.Value);
                s.Write(rb);
                break;
            }
            case 5:
            {
                var data = (byte[])n.Value;
                WriteMarker(s, 0x4, data.Length);
                s.Write(data);
                break;
            }
            case 6:
            {
                var str = (string)n.Value;
                if (str.All(c => c < 128))
                {
                    WriteMarker(s, 0x5, str.Length);
                    s.Write(Encoding.ASCII.GetBytes(str));
                }
                else
                {
                    WriteMarker(s, 0x6, str.Length);
                    s.Write(Encoding.BigEndianUnicode.GetBytes(str));
                }
                break;
            }
            case 7:
            {
                s.WriteByte(0x33);
                Span<byte> db = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(db, (((DateTime)n.Value).ToUniversalTime() - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
                s.Write(db);
                break;
            }
            case 8:
                WriteMarker(s, 0xA, n.Children.Count);
                foreach (var c in n.Children) WriteSized(s, (ulong)c.Index, refSize);
                break;
            case 9:
                WriteMarker(s, 0xD, n.Children.Count / 2);
                foreach (var c in n.Children) WriteSized(s, (ulong)c.Index, refSize);
                break;
        }
    }

    private static void WriteMarker(Stream s, int type, int count)
    {
        if (count < 15) { s.WriteByte((byte)((type << 4) | count)); return; }
        s.WriteByte((byte)((type << 4) | 0xF));
        if (count <= 0xff) { s.WriteByte(0x10); s.WriteByte((byte)count); }
        else if (count <= 0xffff) { s.WriteByte(0x11); WriteSized(s, (ulong)count, 2); }
        else { s.WriteByte(0x12); WriteSized(s, (ulong)count, 4); }
    }

    private static void WriteSized(Stream s, ulong v, int size)
    {
        for (int i = size - 1; i >= 0; i--) s.WriteByte((byte)(v >> (8 * i)));
    }

    // ----------------------------------------------------------------- reading

    public static object Read(byte[] data)
    {
        if (data == null || data.Length < 40 || Encoding.ASCII.GetString(data, 0, 6) != "bplist")
            throw new FormatException("Not a binary plist");
        var t = data.AsSpan(data.Length - 32);
        int offSize = t[6], refSize = t[7];
        long numObjects = (long)BinaryPrimitives.ReadUInt64BigEndian(t[8..]);
        long top = (long)BinaryPrimitives.ReadUInt64BigEndian(t[16..]);
        long tableOff = (long)BinaryPrimitives.ReadUInt64BigEndian(t[24..]);
        var offsets = new long[numObjects];
        for (long i = 0; i < numObjects; i++) offsets[i] = (long)ReadSized(data, tableOff + i * offSize, offSize);
        return ReadObject(data, offsets, refSize, top, 0);
    }

    private static object ReadObject(byte[] d, long[] offsets, int refSize, long idx, int depth)
    {
        if (depth > 64) throw new FormatException("plist too deep");
        long p = offsets[idx];
        byte marker = d[p];
        int type = marker >> 4, info = marker & 0xF;
        switch (type)
        {
            case 0x0:
                return info switch { 0x8 => false, 0x9 => true, _ => null };
            case 0x1:
            {
                int n = 1 << info;
                if (n == 16) return (long)ReadSized(d, p + 9, 8); // 128-bit: low 64 bits
                ulong v = ReadSized(d, p + 1, n);
                return n == 8 ? (long)v : (long)v;
            }
            case 0x2:
                return info == 2 ? BinaryPrimitives.ReadSingleBigEndian(d.AsSpan((int)p + 1)) : BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan((int)p + 1));
            case 0x3:
                return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan((int)p + 1)));
            case 0x4:
            {
                var (len, start) = ReadCount(d, p, info);
                return d.AsSpan((int)start, (int)len).ToArray();
            }
            case 0x5:
            {
                var (len, start) = ReadCount(d, p, info);
                return Encoding.ASCII.GetString(d, (int)start, (int)len);
            }
            case 0x6:
            {
                var (len, start) = ReadCount(d, p, info);
                return Encoding.BigEndianUnicode.GetString(d, (int)start, (int)len * 2);
            }
            case 0x8:
                return d.AsSpan((int)p + 1, info + 1).ToArray(); // UID, rare
            case 0xA:
            {
                var (len, start) = ReadCount(d, p, info);
                var list = new List<object>((int)len);
                for (long i = 0; i < len; i++)
                    list.Add(ReadObject(d, offsets, refSize, (long)ReadSized(d, start + i * refSize, refSize), depth + 1));
                return list;
            }
            case 0xD:
            {
                var (len, start) = ReadCount(d, p, info);
                var dict = new Dictionary<string, object>((int)len);
                for (long i = 0; i < len; i++)
                {
                    var k = ReadObject(d, offsets, refSize, (long)ReadSized(d, start + i * refSize, refSize), depth + 1) as string;
                    var v = ReadObject(d, offsets, refSize, (long)ReadSized(d, start + (len + i) * refSize, refSize), depth + 1);
                    if (k != null) dict[k] = v;
                }
                return dict;
            }
            default:
                throw new FormatException($"plist: unsupported marker 0x{marker:x2}");
        }
    }

    private static (long len, long start) ReadCount(byte[] d, long p, int info)
    {
        if (info != 0xF) return (info, p + 1);
        int n = 1 << (d[p + 1] & 0xF);
        return ((long)ReadSized(d, p + 2, n), p + 2 + n);
    }

    private static ulong ReadSized(byte[] d, long p, int size)
    {
        ulong v = 0;
        for (int i = 0; i < size; i++) v = (v << 8) | d[p + i];
        return v;
    }

    // ----------------------------------------------------------------- helpers

    public static long GetLong(this Dictionary<string, object> d, string key, long def = 0) =>
        d != null && d.TryGetValue(key, out var v) ? v switch { long l => l, bool b => b ? 1 : 0, double x => (long)x, _ => def } : def;

    public static string GetString(this Dictionary<string, object> d, string key) =>
        d != null && d.TryGetValue(key, out var v) ? v as string : null;
}
