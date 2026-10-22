using System.Numerics;
using System.Security.Cryptography;

namespace AirWave.Core.Crypto;

/// <summary>
/// X25519 key agreement (RFC 7748) and Ed25519 signatures (RFC 8032), which .NET doesn't include.
/// Straightforward BigInteger implementations: only a handful of operations happen per pairing,
/// so clarity matters more than speed here. Checked against the RFC test vectors.
/// </summary>
public static class Curve25519
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * Inv(121666));
    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly Point B = MakeBase();

    // ------------------------------------------------------------------ X25519

    public static (byte[] priv, byte[] pub) X25519KeyPair()
    {
        var priv = RandomNumberGenerator.GetBytes(32);
        return (priv, X25519(priv, BasePoint9()));
    }

    public static byte[] X25519Public(byte[] priv) => X25519(priv, BasePoint9());

    private static byte[] BasePoint9() { var b = new byte[32]; b[0] = 9; return b; }

    public static byte[] X25519(byte[] scalar, byte[] uBytes)
    {
        var k = (byte[])scalar.Clone();
        k[0] &= 248; k[31] &= 127; k[31] |= 64;
        BigInteger kn = FromLe(k);
        var u = (byte[])uBytes.Clone();
        u[31] &= 127;
        BigInteger x1 = FromLe(u) % P;

        BigInteger x2 = 1, z2 = 0, x3 = x1, z3 = 1;
        int swap = 0;
        for (int t = 254; t >= 0; t--)
        {
            int kt = (int)((kn >> t) & 1);
            swap ^= kt;
            if (swap == 1) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }
            swap = kt;
            BigInteger A = Mod(x2 + z2), AA = Mod(A * A), Bv = Mod(x2 - z2), BB = Mod(Bv * Bv), E = Mod(AA - BB);
            BigInteger C = Mod(x3 + z3), Dv = Mod(x3 - z3), DA = Mod(Dv * A), CB = Mod(C * Bv);
            x3 = Mod((DA + CB) * (DA + CB));
            z3 = Mod(x1 * Mod((DA - CB) * (DA - CB)));
            x2 = Mod(AA * BB);
            z2 = Mod(E * (AA + 121665 * E));
        }
        if (swap == 1) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }
        return ToLe(Mod(x2 * Inv(z2)), 32);
    }

    // ------------------------------------------------------------------ Ed25519

    public static (byte[] seed, byte[] pub) Ed25519KeyPair()
    {
        var seed = RandomNumberGenerator.GetBytes(32);
        return (seed, Ed25519Public(seed));
    }

    public static byte[] Ed25519Public(byte[] seed)
    {
        var (a, _) = ExpandSeed(seed);
        return Encode(Mul(a, B));
    }

    public static byte[] Ed25519Sign(byte[] seed, byte[] message)
    {
        var (a, prefix) = ExpandSeed(seed);
        byte[] pub = Encode(Mul(a, B));
        BigInteger r = FromLe(Sha512(prefix, message)) % L;
        byte[] R = Encode(Mul(r, B));
        BigInteger h = FromLe(Sha512(R, pub, message)) % L;
        BigInteger s = (r + h * a) % L;
        var sig = new byte[64];
        Buffer.BlockCopy(R, 0, sig, 0, 32);
        Buffer.BlockCopy(ToLe(s, 32), 0, sig, 32, 32);
        return sig;
    }

    public static bool Ed25519Verify(byte[] pub, byte[] message, byte[] sig)
    {
        try
        {
            if (pub?.Length != 32 || sig?.Length != 64) return false;
            var A = Decode(pub);
            if (A == null) return false;
            var Rb = sig.AsSpan(0, 32).ToArray();
            var R = Decode(Rb);
            if (R == null) return false;
            BigInteger s = FromLe(sig.AsSpan(32, 32).ToArray());
            if (s >= L) return false;
            BigInteger h = FromLe(Sha512(Rb, pub, message)) % L;
            var sB = Mul(s, B);
            var hA = Mul(h, A);
            return Equal(sB, Add(R, hA));
        }
        catch { return false; }
    }

    private static (BigInteger a, byte[] prefix) ExpandSeed(byte[] seed)
    {
        var h = SHA512.HashData(seed);
        var aBytes = h.AsSpan(0, 32).ToArray();
        aBytes[0] &= 248; aBytes[31] &= 127; aBytes[31] |= 64;
        return (FromLe(aBytes), h.AsSpan(32, 32).ToArray());
    }

    // Extended coordinates (X, Y, Z, T), x = X/Z, y = Y/Z, xy = T/Z
    private sealed record Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    private static Point MakeBase()
    {
        BigInteger y = Mod(4 * Inv(5));
        BigInteger x = RecoverX(y, 0);
        return new Point(x, y, 1, Mod(x * y));
    }

    private static Point Add(Point p, Point q)
    {
        BigInteger a = Mod((p.Y - p.X) * (q.Y - q.X));
        BigInteger b = Mod((p.Y + p.X) * (q.Y + q.X));
        BigInteger c = Mod(2 * p.T * q.T * D);
        BigInteger d = Mod(2 * p.Z * q.Z);
        BigInteger e = b - a, f = d - c, g = d + c, h = b + a;
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point Mul(BigInteger s, Point p)
    {
        var q = new Point(0, 1, 1, 0);
        while (s > 0)
        {
            if (!s.IsEven) q = Add(q, p);
            p = Add(p, p);
            s >>= 1;
        }
        return q;
    }

    private static bool Equal(Point p, Point q) =>
        Mod(p.X * q.Z - q.X * p.Z) == 0 && Mod(p.Y * q.Z - q.Y * p.Z) == 0;

    private static byte[] Encode(Point p)
    {
        BigInteger zi = Inv(p.Z);
        BigInteger x = Mod(p.X * zi), y = Mod(p.Y * zi);
        var b = ToLe(y, 32);
        if (!x.IsEven) b[31] |= 0x80;
        return b;
    }

    private static Point Decode(byte[] s)
    {
        var b = (byte[])s.Clone();
        int sign = b[31] >> 7;
        b[31] &= 0x7f;
        BigInteger y = FromLe(b);
        if (y >= P) return null;
        BigInteger x = RecoverX(y, sign);
        if (x < 0) return null;
        return new Point(x, y, 1, Mod(x * y));
    }

    private static BigInteger RecoverX(BigInteger y, int sign)
    {
        BigInteger x2 = Mod((y * y - 1) * Inv(D * y * y + 1));
        if (x2 == 0) return sign == 1 ? -1 : 0;
        BigInteger x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (Mod(x * x - x2) != 0) x = Mod(x * SqrtM1);
        if (Mod(x * x - x2) != 0) return -1;
        if ((int)(x & 1) != sign) x = P - x;
        return x;
    }

    // ------------------------------------------------------------------ helpers

    private static BigInteger Mod(BigInteger a) { var r = a % P; return r < 0 ? r + P : r; }
    private static BigInteger Inv(BigInteger a) => BigInteger.ModPow(Mod(a), P - 2, P);
    private static BigInteger FromLe(byte[] le) => new(le, isUnsigned: true, isBigEndian: false);

    private static byte[] ToLe(BigInteger v, int len)
    {
        var raw = v.ToByteArray(isUnsigned: true, isBigEndian: false);
        var r = new byte[len];
        Buffer.BlockCopy(raw, 0, r, 0, Math.Min(len, raw.Length));
        return r;
    }

    private static byte[] Sha512(params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        foreach (var p in parts) h.AppendData(p);
        return h.GetHashAndReset();
    }
}
