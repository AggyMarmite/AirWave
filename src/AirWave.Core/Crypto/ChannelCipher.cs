using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AirWave.Core.Crypto;

/// <summary>
/// Encrypts/decrypts an AirPlay 2 TCP channel (control RTSP or events) after pairing.
/// Framing: [u16 LE length][ChaCha20-Poly1305 ciphertext][16-byte tag], max 1024 bytes per frame,
/// the length bytes are the AAD, and the nonce is a 64-bit little-endian frame counter at offset 4.
/// </summary>
public sealed class ChannelCipher : IDisposable
{
    private const int MaxFrame = 0x400;
    private readonly ChaCha20Poly1305 _enc;
    private readonly ChaCha20Poly1305 _dec;
    private ulong _encCounter;
    private ulong _decCounter;
    private readonly List<byte> _pending = new();

    private ChannelCipher(byte[] encKey, byte[] decKey)
    {
        _enc = new ChaCha20Poly1305(encKey);
        _dec = new ChaCha20Poly1305(decKey);
    }

    /// <summary>Control channel as seen by the sender (us).</summary>
    public static ChannelCipher ForControl(byte[] sharedSecret) =>
        new(Derive(sharedSecret, "Control-Salt", "Control-Write-Encryption-Key"),
            Derive(sharedSecret, "Control-Salt", "Control-Read-Encryption-Key"));

    /// <summary>Events channel: a reverse connection, so the keys are swapped compared to control.</summary>
    public static ChannelCipher ForEvents(byte[] sharedSecret) =>
        new(Derive(sharedSecret, "Events-Salt", "Events-Read-Encryption-Key"),
            Derive(sharedSecret, "Events-Salt", "Events-Write-Encryption-Key"));

    /// <summary>Control channel as seen by a receiver (the keys are the other way round).</summary>
    public static ChannelCipher ForControlServer(byte[] sharedSecret) =>
        new(Derive(sharedSecret, "Control-Salt", "Control-Read-Encryption-Key"),
            Derive(sharedSecret, "Control-Salt", "Control-Write-Encryption-Key"));

    /// <summary>Events channel as seen by a receiver.</summary>
    public static ChannelCipher ForEventsServer(byte[] sharedSecret) =>
        new(Derive(sharedSecret, "Events-Salt", "Events-Write-Encryption-Key"),
            Derive(sharedSecret, "Events-Salt", "Events-Read-Encryption-Key"));

    public static byte[] Derive(byte[] ikm, string salt, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA512, ikm, 32, Encoding.ASCII.GetBytes(salt), Encoding.ASCII.GetBytes(info));

    public byte[] Encrypt(ReadOnlySpan<byte> plain)
    {
        using var ms = new MemoryStream();
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> tag = stackalloc byte[16];
        Span<byte> lenBytes = stackalloc byte[2];
        int off = 0;
        do
        {
            int len = Math.Min(MaxFrame, plain.Length - off);
            nonce.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _encCounter++);
            BinaryPrimitives.WriteUInt16LittleEndian(lenBytes, (ushort)len);
            var cipher = new byte[len];
            _enc.Encrypt(nonce, plain.Slice(off, len), cipher, tag, lenBytes);
            ms.Write(lenBytes);
            ms.Write(cipher);
            ms.Write(tag);
            off += len;
        } while (off < plain.Length);
        return ms.ToArray();
    }

    /// <summary>Feeds received ciphertext; returns any whole frames' plaintext (possibly empty).</summary>
    public byte[] Decrypt(ReadOnlySpan<byte> incoming)
    {
        _pending.AddRange(incoming.ToArray());
        using var ms = new MemoryStream();
        Span<byte> nonce = stackalloc byte[12];
        while (_pending.Count >= 2)
        {
            int len = _pending[0] | (_pending[1] << 8);
            if (_pending.Count < 2 + len + 16) break;
            byte[] frame = _pending.GetRange(0, 2 + len + 16).ToArray();
            _pending.RemoveRange(0, 2 + len + 16);
            nonce.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _decCounter++);
            var plain = new byte[len];
            _dec.Decrypt(nonce, frame.AsSpan(2, len), frame.AsSpan(2 + len, 16), plain, frame.AsSpan(0, 2));
            ms.Write(plain);
        }
        return ms.ToArray();
    }

    public void Dispose()
    {
        _enc.Dispose();
        _dec.Dispose();
    }
}
