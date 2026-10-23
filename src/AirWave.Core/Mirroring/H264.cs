namespace AirWave.Core.Mirroring;

/// <summary>
/// H.264 byte-format helpers: encoders emit Annex B (start codes, SPS/PPS inline); the mirroring data
/// channel wants AVCC frames (4-byte lengths) with SPS/PPS separately as an avcC record.
/// </summary>
public static class H264
{
    public const int NalSlice = 1, NalIdr = 5, NalSei = 6, NalSps = 7, NalPps = 8, NalAud = 9;

    public sealed class AccessUnit
    {
        public byte[] Avcc;        // slice (and SEI) NALs, length-prefixed
        public bool Keyframe;
        public byte[] Sps, Pps;    // set when this unit carried new parameter sets
        public int Width, Height;
    }

    public static int Type(ReadOnlySpan<byte> nal) => nal[0] & 0x1f;

    /// <summary>Splits Annex B into NAL units (without start codes).</summary>
    public static List<byte[]> SplitAnnexB(ReadOnlySpan<byte> s)
    {
        var nals = new List<byte[]>();
        int start = -1, i = 0;
        while (i + 3 <= s.Length)
        {
            if (s[i] == 0 && s[i + 1] == 0 && s[i + 2] == 1)
            {
                if (start >= 0) Add(nals, s, start, i);
                start = i + 3; i += 3;
            }
            else i++;
        }
        if (start >= 0) Add(nals, s, start, s.Length);
        return nals;
    }

    private static void Add(List<byte[]> into, ReadOnlySpan<byte> s, int start, int end)
    {
        while (end > start && s[end - 1] == 0) end--;
        if (end > start) into.Add(s[start..end].ToArray());
    }

    /// <summary>One encoded frame's NAL units -> an access unit for the data channel.</summary>
    public static AccessUnit FromNals(IEnumerable<byte[]> nals)
    {
        var au = new AccessUnit();
        var ms = new MemoryStream();
        foreach (var n in nals)
        {
            int t = Type(n);
            if (t == NalSps) { au.Sps = n; (au.Width, au.Height) = SpsSize(n); continue; }
            if (t == NalPps) { au.Pps = n; continue; }
            if (t == NalAud) continue;
            if (t == NalIdr) au.Keyframe = true;
            Span<byte> len = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, n.Length);
            ms.Write(len);
            ms.Write(n);
        }
        au.Avcc = ms.ToArray();
        return au;
    }

    /// <summary>Splits a whole Annex B stream into frames (a new frame starts at each slice with first_mb_in_slice = 0).</summary>
    public static List<AccessUnit> AccessUnits(byte[] annexB)
    {
        var result = new List<AccessUnit>();
        var cur = new List<byte[]>();
        bool curHasSlice = false;
        foreach (var n in SplitAnnexB(annexB))
        {
            int t = Type(n);
            bool slice = t == NalSlice || t == NalIdr;
            bool firstMb = slice && n.Length > 1 && (n[1] & 0x80) != 0; // first_mb_in_slice == 0 (ue(v) "1")
            bool startsNew = curHasSlice && (t == NalAud || t == NalSps || t == NalPps || t == NalSei || firstMb);
            if (startsNew) { result.Add(FromNals(cur)); cur = new(); curHasSlice = false; }
            cur.Add(n);
            if (slice) curHasSlice = true;
        }
        if (curHasSlice) result.Add(FromNals(cur));
        return result;
    }

    /// <summary>An avcC record with one SPS and one PPS and 4-byte NAL lengths.</summary>
    public static byte[] AvcC(byte[] sps, byte[] pps)
    {
        var ms = new MemoryStream();
        ms.WriteByte(1); ms.WriteByte(sps[1]); ms.WriteByte(sps[2]); ms.WriteByte(sps[3]);
        ms.WriteByte(0xFF); ms.WriteByte(0xE1);
        ms.WriteByte((byte)(sps.Length >> 8)); ms.WriteByte((byte)sps.Length); ms.Write(sps);
        ms.WriteByte(1);
        ms.WriteByte((byte)(pps.Length >> 8)); ms.WriteByte((byte)pps.Length); ms.Write(pps);
        return ms.ToArray();
    }

    /// <summary>Picture size from an SPS (handles cropping; enough of the syntax for common encoders).</summary>
    public static (int w, int h) SpsSize(byte[] sps)
    {
        // Remove emulation-prevention bytes
        var rbsp = new List<byte>();
        for (int i = 0; i < sps.Length; i++)
        {
            if (i >= 2 && sps[i] == 3 && sps[i - 1] == 0 && sps[i - 2] == 0) continue;
            rbsp.Add(sps[i]);
        }
        var r = new Bits(rbsp.ToArray(), 8);
        int profile = (int)r.U(8); r.U(16); r.Ue();
        int chroma = 1;
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            chroma = (int)r.Ue();
            if (chroma == 3) r.U(1);
            r.Ue(); r.Ue(); r.U(1);
            if (r.U(1) == 1)
                for (int i = 0; i < (chroma != 3 ? 8 : 12); i++)
                    if (r.U(1) == 1)
                    {
                        int size = i < 6 ? 16 : 64, last = 8, next = 8;
                        for (int j = 0; j < size; j++) { if (next != 0) next = (int)((last + r.Se() + 256) % 256); last = next == 0 ? last : next; }
                    }
        }
        r.Ue();
        long pocType = r.Ue();
        if (pocType == 0) r.Ue();
        else if (pocType == 1) { r.U(1); r.Se(); r.Se(); long n = r.Ue(); for (int i = 0; i < n; i++) r.Se(); }
        r.Ue(); r.U(1);
        long wMbs = r.Ue() + 1, hMapUnits = r.Ue() + 1;
        int frameMbsOnly = (int)r.U(1);
        if (frameMbsOnly == 0) r.U(1);
        r.U(1);
        long cl = 0, cr = 0, ct = 0, cb = 0;
        if (r.U(1) == 1) { cl = r.Ue(); cr = r.Ue(); ct = r.Ue(); cb = r.Ue(); }
        int subW = chroma is 1 or 2 ? 2 : 1, subH = chroma == 1 ? 2 : 1;
        if (chroma == 0) { subW = 1; subH = 1; }
        long w = wMbs * 16 - (cl + cr) * subW;
        long h = (2 - frameMbsOnly) * hMapUnits * 16 - (ct + cb) * subH * (2 - frameMbsOnly);
        return ((int)w, (int)h);
    }

    private sealed class Bits
    {
        private readonly byte[] _b; private int _p;
        public Bits(byte[] b, int startBit) { _b = b; _p = startBit; }
        public long U(int n) { long v = 0; for (int i = 0; i < n; i++) { int byteI = _p >> 3; int bit = byteI < _b.Length ? (_b[byteI] >> (7 - (_p & 7))) & 1 : 0; v = (v << 1) | (long)bit; _p++; } return v; }
        public long Ue() { int z = 0; while (U(1) == 0 && z < 32) z++; return (1L << z) - 1 + U(z); }
        public long Se() { long k = Ue(); return (k & 1) != 0 ? (k + 1) / 2 : -(k / 2); }
    }
}
