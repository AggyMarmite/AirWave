namespace AirWave.Core.Crypto;

/// <summary>HomeKit TLV8 encoding: type(1) length(1) value, values over 255 bytes split into consecutive items.</summary>
public static class Tlv8
{
    public const byte Method = 0, Identifier = 1, Salt = 2, PublicKey = 3, Proof = 4,
        EncryptedData = 5, State = 6, Error = 7, Signature = 10, Flags = 19;

    public static byte[] Encode(params (byte type, byte[] value)[] items)
    {
        using var ms = new MemoryStream();
        foreach (var (type, value) in items)
        {
            if (value.Length == 0) { ms.WriteByte(type); ms.WriteByte(0); continue; }
            for (int off = 0; off < value.Length; off += 255)
            {
                int len = Math.Min(255, value.Length - off);
                ms.WriteByte(type);
                ms.WriteByte((byte)len);
                ms.Write(value, off, len);
            }
        }
        return ms.ToArray();
    }

    public static Dictionary<byte, byte[]> Decode(byte[] data)
    {
        var result = new Dictionary<byte, byte[]>();
        int i = 0;
        byte lastType = 0xff;
        while (i + 2 <= data.Length)
        {
            byte type = data[i];
            int len = data[i + 1];
            i += 2;
            if (i + len > data.Length) break;
            var chunk = data.AsSpan(i, len).ToArray();
            i += len;
            if (type == lastType && result.TryGetValue(type, out var prev))
                result[type] = prev.Concat(chunk).ToArray(); // continuation of a long value
            else
                result[type] = chunk;
            lastType = type;
        }
        return result;
    }
}
