using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using static AirWave.Core.Crypto.Srp6aClient;

namespace AirWave.Core.Crypto;

/// <summary>
/// SRP-6a verifier side (RFC 5054 3072-bit group, SHA-512), the accessory half of HomeKit pair-setup.
/// Uses exactly the same hashing conventions as <see cref="Srp6aClient"/>, which is proven against real speakers.
/// </summary>
public sealed class Srp6aServer
{
    private readonly byte[] _salt;
    private readonly BigInteger _v;
    private readonly BigInteger _b;
    private readonly string _user;

    public byte[] Salt => _salt;
    public byte[] B { get; }
    public byte[] SessionKey { get; private set; }

    public Srp6aServer(string user, string password, byte[] salt = null, byte[] privateB = null)
    {
        _user = user;
        _salt = salt ?? RandomNumberGenerator.GetBytes(16);
        byte[] inner = H(Encoding.UTF8.GetBytes(user), Encoding.UTF8.GetBytes(":"), Encoding.UTF8.GetBytes(password));
        BigInteger x = FromBytes(H(ToBytes(FromBytes(_salt)), inner));
        _v = BigInteger.ModPow(G, x, N);
        _b = FromBytes(privateB ?? RandomNumberGenerator.GetBytes(32));
        BigInteger k = FromBytes(H(Pad(ToBytes(N)), Pad(ToBytes(G))));
        B = ToBytes((k * _v + BigInteger.ModPow(G, _b, N)) % N);
    }

    /// <summary>Checks the client's proof; on success returns the server proof M2 (else null).</summary>
    public byte[] VerifyClient(byte[] aBytes, byte[] m1)
    {
        BigInteger A = FromBytes(aBytes);
        if (A % N == 0) return null;
        BigInteger u = FromBytes(H(Pad(aBytes), Pad(B)));
        BigInteger S = BigInteger.ModPow(A * BigInteger.ModPow(_v, u, N) % N, _b, N);
        byte[] K = H(ToBytes(S));

        byte[] hN = H(ToBytes(N)), hG = H(ToBytes(G));
        var hXor = new byte[hN.Length];
        for (int i = 0; i < hN.Length; i++) hXor[i] = (byte)(hN[i] ^ hG[i]);
        byte[] expected = H(hXor, H(Encoding.UTF8.GetBytes(_user)), ToBytes(FromBytes(_salt)), aBytes, B, K);
        if (m1 == null || !CryptographicOperations.FixedTimeEquals(expected, m1)) return null;
        SessionKey = K;
        return H(aBytes, m1, K);
    }
}
