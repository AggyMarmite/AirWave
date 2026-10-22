using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AirWave.Core.Crypto;

/// <summary>
/// SRP-6a client (RFC 5054 3072-bit group, SHA-512) as used by HomeKit / AirPlay 2 pair-setup.
/// For "transient" pairing the password is the fixed PIN 3939 and the resulting session key is
/// the shared secret for all channel encryption.
/// </summary>
public sealed class Srp6aClient
{
    private static readonly BigInteger N = BigInteger.Parse("0" +
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B" +
        "139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485" +
        "B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7EDEE386BFB5A899FA5AE9F24117C4B1F" +
        "E649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F83655D23" +
        "DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32" +
        "905E462E36CE3BE39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF69558" +
        "17183995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33A85521" +
        "ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7ABF5AE8CDB0933D7" +
        "1E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D87602733EC86A64521F2B1817" +
        "7B200CBBE117577A615D6C770988C0BAD946E208E24FA074E5AB3143DB5BFCE0FD108E4B82" +
        "D120A93AD2CAFFFFFFFFFFFFFFFF", System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger G = 5;
    private const int NLen = 384;

    private readonly string _user;
    private readonly byte[] _pass;
    private readonly BigInteger _a;

    public byte[] A { get; }
    public byte[] M1 { get; private set; }
    public byte[] SessionKey { get; private set; }
    private byte[] _expectedM2;

    public Srp6aClient(string user, string password, byte[] privateA = null)
    {
        _user = user;
        _pass = Encoding.UTF8.GetBytes(password);
        byte[] aBytes = privateA ?? RandomNumberGenerator.GetBytes(32);
        _a = FromBytes(aBytes);
        A = ToBytes(BigInteger.ModPow(G, _a, N));
    }

    /// <summary>Processes the accessory's salt and public key B, producing proof M1.</summary>
    public void ProcessChallenge(byte[] salt, byte[] bBytes)
    {
        BigInteger B = FromBytes(bBytes);
        if (B % N == 0) throw new CryptographicException("SRP: invalid B");

        BigInteger k = FromBytes(H(Pad(ToBytes(N)), Pad(ToBytes(G))));
        BigInteger u = FromBytes(H(Pad(A), Pad(bBytes)));
        if (u == 0) throw new CryptographicException("SRP: invalid u");

        byte[] inner = H(Encoding.UTF8.GetBytes(_user), Encoding.UTF8.GetBytes(":"), _pass);
        BigInteger x = FromBytes(H(ToBytes(FromBytes(salt)), inner));

        BigInteger gx = BigInteger.ModPow(G, x, N);
        BigInteger baseVal = (B - k * gx) % N;
        if (baseVal < 0) baseVal += N;
        BigInteger S = BigInteger.ModPow(baseVal, _a + u * x, N);

        SessionKey = H(ToBytes(S));

        byte[] hN = H(ToBytes(N));
        byte[] hG = H(ToBytes(G));
        byte[] hXor = new byte[hN.Length];
        for (int i = 0; i < hN.Length; i++) hXor[i] = (byte)(hN[i] ^ hG[i]);
        byte[] hI = H(Encoding.UTF8.GetBytes(_user));

        M1 = H(hXor, hI, ToBytes(FromBytes(salt)), A, ToBytes(B), SessionKey);
        _expectedM2 = H(A, M1, SessionKey);
    }

    public bool VerifyServerProof(byte[] m2) =>
        m2 != null && _expectedM2 != null && CryptographicOperations.FixedTimeEquals(m2, _expectedM2);

    private static byte[] H(params byte[][] parts)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        foreach (var p in parts) sha.AppendData(p);
        return sha.GetHashAndReset();
    }

    private static byte[] Pad(byte[] v)
    {
        if (v.Length >= NLen) return v;
        var r = new byte[NLen];
        Buffer.BlockCopy(v, 0, r, NLen - v.Length, v.Length);
        return r;
    }

    // Big-endian unsigned <-> BigInteger, minimal length (like BN_bn2bin).
    internal static BigInteger FromBytes(byte[] be) => new(be, isUnsigned: true, isBigEndian: true);
    internal static byte[] ToBytes(BigInteger v) => v.IsZero ? new byte[] { 0 } : v.ToByteArray(isUnsigned: true, isBigEndian: true);
}
