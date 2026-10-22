using System.Text;

namespace AirWave.Core.Crypto;

/// <summary>
/// The controller side of full HomeKit pairing (PIN pair-setup with long-term keys, then pair-verify),
/// needed for devices such as Apple TV that won't accept transient pairing for everything.
/// Drive it by passing each response from the device to the next step.
/// </summary>
public sealed class PairingClient
{
    private readonly string _controllerId;
    private readonly byte[] _ltsk, _ltpk;
    private Srp6aClient _srp;
    private byte[] _verifyPriv, _verifyPub, _verifyShared, _deviceVerifyPub;

    /// <summary>The device's long-term public key and id, learned in pair-setup (save these with our keys).</summary>
    public byte[] DeviceLtpk { get; private set; }
    public string DeviceId { get; private set; }
    public byte[] SharedSecret { get; private set; }
    public byte[] SetupSessionKey => _srp?.SessionKey;

    public PairingClient(string controllerId, byte[] ltsk)
    {
        _controllerId = controllerId;
        _ltsk = ltsk;
        _ltpk = Curve25519.Ed25519Public(ltsk);
    }

    // ---------------------------------------------------------------- pair-setup (PIN)

    public byte[] SetupM1(bool transient = false) =>
        transient
            ? Tlv8.Encode((Tlv8.Method, new byte[] { 0 }), (Tlv8.State, new byte[] { 1 }), (Tlv8.Flags, new byte[] { 0x10 }))
            : Tlv8.Encode((Tlv8.Method, new byte[] { 0 }), (Tlv8.State, new byte[] { 1 }));

    public byte[] SetupM3(byte[] m2, string pin)
    {
        var t = Tlv8.Decode(m2);
        ThrowIfError(t, "pair-setup M2");
        _srp = new Srp6aClient("Pair-Setup", pin);
        _srp.ProcessChallenge(t[Tlv8.Salt], t[Tlv8.PublicKey]);
        return Tlv8.Encode((Tlv8.State, new byte[] { 3 }), (Tlv8.PublicKey, _srp.A), (Tlv8.Proof, _srp.M1));
    }

    /// <summary>Checks M4. For transient pairing this is the last step and <see cref="SharedSecret"/> is set.</summary>
    public void SetupCheckM4(byte[] m4, bool transient = false)
    {
        var t = Tlv8.Decode(m4);
        ThrowIfError(t, "pair-setup M4 (wrong PIN?)");
        if (!_srp.VerifyServerProof(t.GetValueOrDefault(Tlv8.Proof)))
            throw new InvalidOperationException("pair-setup: device proof didn't check out");
        if (transient) SharedSecret = _srp.SessionKey;
    }

    public byte[] SetupM5()
    {
        byte[] K = _srp.SessionKey;
        byte[] x = ChannelCipher.Derive(K, "Pair-Setup-Controller-Sign-Salt", "Pair-Setup-Controller-Sign-Info");
        byte[] id = Encoding.UTF8.GetBytes(_controllerId);
        byte[] sig = Curve25519.Ed25519Sign(_ltsk, x.Concat(id).Concat(_ltpk).ToArray());
        byte[] inner = Tlv8.Encode((Tlv8.Identifier, id), (Tlv8.PublicKey, _ltpk), (Tlv8.Signature, sig));
        byte[] key = ChannelCipher.Derive(K, "Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info");
        return Tlv8.Encode((Tlv8.State, new byte[] { 5 }), (Tlv8.EncryptedData, PairingServer.Seal(key, "PS-Msg05", inner)));
    }

    public void SetupCheckM6(byte[] m6)
    {
        var t = Tlv8.Decode(m6);
        ThrowIfError(t, "pair-setup M6");
        byte[] K = _srp.SessionKey;
        byte[] key = ChannelCipher.Derive(K, "Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info");
        var plain = PairingServer.Open(key, "PS-Msg06", t[Tlv8.EncryptedData]) ?? throw new InvalidOperationException("pair-setup M6: decrypt failed");
        var sub = Tlv8.Decode(plain);
        byte[] id = sub[Tlv8.Identifier], ltpk = sub[Tlv8.PublicKey], sig = sub[Tlv8.Signature];
        byte[] x = ChannelCipher.Derive(K, "Pair-Setup-Accessory-Sign-Salt", "Pair-Setup-Accessory-Sign-Info");
        if (!Curve25519.Ed25519Verify(ltpk, x.Concat(id).Concat(ltpk).ToArray(), sig))
            throw new InvalidOperationException("pair-setup M6: device signature invalid");
        DeviceLtpk = ltpk;
        DeviceId = Encoding.UTF8.GetString(id);
    }

    // ---------------------------------------------------------------- pair-verify

    public byte[] VerifyM1()
    {
        (_verifyPriv, _verifyPub) = Curve25519.X25519KeyPair();
        return Tlv8.Encode((Tlv8.State, new byte[] { 1 }), (Tlv8.PublicKey, _verifyPub));
    }

    /// <param name="knownDeviceLtpk">The device's long-term key from an earlier pair-setup, to check it's the same device (null to skip).</param>
    public byte[] VerifyM3(byte[] m2, byte[] knownDeviceLtpk)
    {
        var t = Tlv8.Decode(m2);
        ThrowIfError(t, "pair-verify M2");
        _deviceVerifyPub = t[Tlv8.PublicKey];
        _verifyShared = Curve25519.X25519(_verifyPriv, _deviceVerifyPub);
        byte[] key = ChannelCipher.Derive(_verifyShared, "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info");
        var plain = PairingServer.Open(key, "PV-Msg02", t[Tlv8.EncryptedData]) ?? throw new InvalidOperationException("pair-verify M2: decrypt failed");
        var sub = Tlv8.Decode(plain);
        if (knownDeviceLtpk != null)
        {
            byte[] devId = sub[Tlv8.Identifier], sig = sub[Tlv8.Signature];
            if (!Curve25519.Ed25519Verify(knownDeviceLtpk, _deviceVerifyPub.Concat(devId).Concat(_verifyPub).ToArray(), sig))
                throw new InvalidOperationException("pair-verify: device signature invalid");
        }
        byte[] id = Encoding.UTF8.GetBytes(_controllerId);
        byte[] mySig = Curve25519.Ed25519Sign(_ltsk, _verifyPub.Concat(id).Concat(_deviceVerifyPub).ToArray());
        byte[] inner = Tlv8.Encode((Tlv8.Identifier, id), (Tlv8.Signature, mySig));
        return Tlv8.Encode((Tlv8.State, new byte[] { 3 }), (Tlv8.EncryptedData, PairingServer.Seal(key, "PV-Msg03", inner)));
    }

    public void VerifyCheckM4(byte[] m4)
    {
        ThrowIfError(Tlv8.Decode(m4), "pair-verify M4");
        SharedSecret = _verifyShared;
    }

    private static void ThrowIfError(Dictionary<byte, byte[]> t, string what)
    {
        if (t.TryGetValue(Tlv8.Error, out var e))
            throw new InvalidOperationException($"{what}: device returned error {(e.Length > 0 ? e[0] : 0)}");
    }
}
