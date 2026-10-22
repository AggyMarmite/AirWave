using System.Security.Cryptography;
using System.Text;

namespace AirWave.Core.Crypto;

/// <summary>
/// The accessory side of HomeKit pairing as AirPlay 2 receivers do it:
/// pair-setup (transient: SRP only, or full with long-term key exchange) and pair-verify
/// (X25519 + Ed25519). One instance per RTSP connection. When <see cref="SharedSecret"/> becomes
/// non-null, the connection switches to encrypted mode after the current response.
/// </summary>
public sealed class PairingServer
{
    public const string DefaultPin = "3939";
    private readonly string _accessoryId;
    private readonly byte[] _ltsk;   // our Ed25519 seed (long-term secret)
    private readonly byte[] _ltpk;   // our Ed25519 public key
    private readonly string _pin;

    private Srp6aServer _srp;
    private bool _transient;
    private byte[] _verifyOurPriv, _verifyOurPub, _verifyTheirPub, _verifyShared;

    public byte[] SharedSecret { get; private set; }
    public bool IsTransient => _transient;
    public string ControllerId { get; private set; }

    public PairingServer(string accessoryId, byte[] ltsk, string pin = DefaultPin)
    {
        _accessoryId = accessoryId;
        _ltsk = ltsk;
        _ltpk = Curve25519.Ed25519Public(ltsk);
        _pin = pin;
    }

    // ---------------------------------------------------------------- pair-setup

    public byte[] Setup(byte[] body)
    {
        var tlv = Tlv8.Decode(body);
        int state = tlv.TryGetValue(Tlv8.State, out var st) && st.Length == 1 ? st[0] : 0;
        switch (state)
        {
            case 1:
                _transient = tlv.TryGetValue(Tlv8.Flags, out var fl) && fl.Length >= 1 && (fl[0] & 0x10) != 0;
                _srp = new Srp6aServer("Pair-Setup", _pin);
                return Tlv8.Encode((Tlv8.State, new byte[] { 2 }), (Tlv8.Salt, _srp.Salt), (Tlv8.PublicKey, _srp.B));

            case 3:
                if (_srp == null || !tlv.TryGetValue(Tlv8.PublicKey, out var a) || !tlv.TryGetValue(Tlv8.Proof, out var m1))
                    return Error(4, 2);
                var m2 = _srp.VerifyClient(a, m1);
                if (m2 == null) return Error(4, 2); // authentication error (wrong PIN)
                if (_transient) SharedSecret = _srp.SessionKey;
                return Tlv8.Encode((Tlv8.State, new byte[] { 4 }), (Tlv8.Proof, m2));

            case 5:
                return SetupM5(tlv);

            default:
                return Error((byte)(state + 1), 1);
        }
    }

    private byte[] SetupM5(Dictionary<byte, byte[]> tlv)
    {
        if (_srp?.SessionKey == null || !tlv.TryGetValue(Tlv8.EncryptedData, out var enc) || enc.Length < 16)
            return Error(6, 2);
        byte[] K = _srp.SessionKey;
        byte[] key = ChannelCipher.Derive(K, "Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info");
        byte[] plain = Open(key, "PS-Msg05", enc);
        if (plain == null) return Error(6, 2);
        var sub = Tlv8.Decode(plain);
        if (!sub.TryGetValue(Tlv8.Identifier, out var id) || !sub.TryGetValue(Tlv8.PublicKey, out var ltpk) || !sub.TryGetValue(Tlv8.Signature, out var sig))
            return Error(6, 2);
        byte[] ctrlX = ChannelCipher.Derive(K, "Pair-Setup-Controller-Sign-Salt", "Pair-Setup-Controller-Sign-Info");
        if (!Curve25519.Ed25519Verify(ltpk, Concat(ctrlX, id, ltpk), sig)) return Error(6, 2);
        ControllerId = Encoding.UTF8.GetString(id);

        byte[] accX = ChannelCipher.Derive(K, "Pair-Setup-Accessory-Sign-Salt", "Pair-Setup-Accessory-Sign-Info");
        byte[] accId = Encoding.UTF8.GetBytes(_accessoryId);
        byte[] accSig = Curve25519.Ed25519Sign(_ltsk, Concat(accX, accId, _ltpk));
        byte[] inner = Tlv8.Encode((Tlv8.Identifier, accId), (Tlv8.PublicKey, _ltpk), (Tlv8.Signature, accSig));
        return Tlv8.Encode((Tlv8.State, new byte[] { 6 }), (Tlv8.EncryptedData, Seal(key, "PS-Msg06", inner)));
    }

    // ---------------------------------------------------------------- pair-verify

    public byte[] Verify(byte[] body)
    {
        var tlv = Tlv8.Decode(body);
        int state = tlv.TryGetValue(Tlv8.State, out var st) && st.Length == 1 ? st[0] : 0;
        if (state == 1)
        {
            if (!tlv.TryGetValue(Tlv8.PublicKey, out var theirPub) || theirPub.Length != 32) return Error(2, 2);
            _verifyTheirPub = theirPub;
            (_verifyOurPriv, _verifyOurPub) = Curve25519.X25519KeyPair();
            _verifyShared = Curve25519.X25519(_verifyOurPriv, theirPub);
            byte[] accId = Encoding.UTF8.GetBytes(_accessoryId);
            byte[] sig = Curve25519.Ed25519Sign(_ltsk, Concat(_verifyOurPub, accId, theirPub));
            byte[] inner = Tlv8.Encode((Tlv8.Identifier, accId), (Tlv8.Signature, sig));
            byte[] key = ChannelCipher.Derive(_verifyShared, "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info");
            return Tlv8.Encode((Tlv8.State, new byte[] { 2 }), (Tlv8.PublicKey, _verifyOurPub), (Tlv8.EncryptedData, Seal(key, "PV-Msg02", inner)));
        }
        if (state == 3)
        {
            if (_verifyShared == null || !tlv.TryGetValue(Tlv8.EncryptedData, out var enc)) return Error(4, 2);
            byte[] key = ChannelCipher.Derive(_verifyShared, "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info");
            byte[] plain = Open(key, "PV-Msg03", enc);
            if (plain == null) return Error(4, 2);
            // Anyone on the network may play (like a speaker set to "Everyone"), so the controller's
            // long-term key isn't checked against a stored list.
            var sub = Tlv8.Decode(plain);
            if (sub.TryGetValue(Tlv8.Identifier, out var id)) ControllerId = Encoding.UTF8.GetString(id);
            SharedSecret = _verifyShared;
            return Tlv8.Encode((Tlv8.State, new byte[] { 4 }));
        }
        return Error((byte)(state + 1), 1);
    }

    // ---------------------------------------------------------------- helpers

    private static byte[] Error(byte state, byte code) => Tlv8.Encode((Tlv8.State, new[] { state }), (Tlv8.Error, new[] { code }));

    internal static byte[] Seal(byte[] key, string nonceText, byte[] plain)
    {
        var nonce = NonceFor(nonceText);
        var output = new byte[plain.Length + 16];
        using var c = new ChaCha20Poly1305(key);
        c.Encrypt(nonce, plain, output.AsSpan(0, plain.Length), output.AsSpan(plain.Length, 16));
        return output;
    }

    internal static byte[] Open(byte[] key, string nonceText, byte[] sealedData)
    {
        if (sealedData.Length < 16) return null;
        var nonce = NonceFor(nonceText);
        var plain = new byte[sealedData.Length - 16];
        try
        {
            using var c = new ChaCha20Poly1305(key);
            c.Decrypt(nonce, sealedData.AsSpan(0, plain.Length), sealedData.AsSpan(plain.Length, 16), plain);
            return plain;
        }
        catch (CryptographicException) { return null; }
    }

    private static byte[] NonceFor(string s)
    {
        var n = new byte[12];
        var b = Encoding.ASCII.GetBytes(s);
        Buffer.BlockCopy(b, 0, n, 12 - b.Length, b.Length);
        return n;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
