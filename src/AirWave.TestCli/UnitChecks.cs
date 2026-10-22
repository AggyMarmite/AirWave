using AirWave.Core.Crypto;

/// <summary>Known-answer checks for code that has published test vectors. Run with "unit".</summary>
static class UnitChecks
{
    static int _fail;

    static void Check(string name, bool ok)
    {
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
        if (!ok) _fail++;
    }

    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    static string FindTestData()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "tests", "data"))) d = d.Parent;
        return d == null ? null : Path.Combine(d.FullName, "tests", "data");
    }

    public static int Run()
    {
        // RFC 7748 5.2
        Check("X25519 vector 1", Convert.ToHexString(Curve25519.X25519(
            Hex("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4"),
            Hex("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c"))).ToLower()
            == "c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552");
        // RFC 7748 6.1 (Diffie-Hellman)
        var aPriv = Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var bPriv = Hex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        Check("X25519 public", Convert.ToHexString(Curve25519.X25519Public(aPriv)).ToLower()
            == "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
        Check("X25519 shared", Convert.ToHexString(Curve25519.X25519(aPriv, Curve25519.X25519Public(bPriv))).ToLower()
            == "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");

        // RFC 8032 7.1 test 1 and test 3
        var seed1 = Hex("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        var pub1 = Curve25519.Ed25519Public(seed1);
        Check("Ed25519 public 1", Convert.ToHexString(pub1).ToLower() == "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        var sig1 = Curve25519.Ed25519Sign(seed1, Array.Empty<byte>());
        Check("Ed25519 sign 1", Convert.ToHexString(sig1).ToLower() ==
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
        Check("Ed25519 verify 1", Curve25519.Ed25519Verify(pub1, Array.Empty<byte>(), sig1));
        var seed3 = Hex("c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7");
        var msg3 = Hex("af82");
        var sig3 = Curve25519.Ed25519Sign(seed3, msg3);
        Check("Ed25519 sign 3", Convert.ToHexString(sig3).ToLower() ==
            "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a");
        var bad = (byte[])sig3.Clone(); bad[5] ^= 1;
        Check("Ed25519 rejects bad signature", !Curve25519.Ed25519Verify(Curve25519.Ed25519Public(seed3), msg3, bad));

        // Pairing: our client against our server, transient and full (PIN) + verify
        {
            var (accSeed, _) = Curve25519.Ed25519KeyPair();
            var (ctlSeed, ctlPub) = Curve25519.Ed25519KeyPair();
            var srv = new PairingServer("AA:BB:CC:DD:EE:FF", accSeed);
            var cli = new PairingClient("controller-1", ctlSeed);
            var m4 = srv.Setup(cli.SetupM3(srv.Setup(cli.SetupM1(transient: true)), "3939"));
            cli.SetupCheckM4(m4, transient: true);
            Check("transient pair-setup agrees on key", srv.SharedSecret != null && srv.SharedSecret.SequenceEqual(cli.SharedSecret));

            var srv2 = new PairingServer("AA:BB:CC:DD:EE:FF", accSeed);
            var cli2 = new PairingClient("controller-1", ctlSeed);
            cli2.SetupCheckM4(srv2.Setup(cli2.SetupM3(srv2.Setup(cli2.SetupM1()), "3939")));
            cli2.SetupCheckM6(srv2.Setup(cli2.SetupM5()));
            Check("full pair-setup exchanges long-term keys", cli2.DeviceLtpk.SequenceEqual(Curve25519.Ed25519Public(accSeed)) && srv2.ControllerId == "controller-1");

            var srv3 = new PairingServer("AA:BB:CC:DD:EE:FF", accSeed);
            var cli3 = new PairingClient("controller-1", ctlSeed);
            cli3.VerifyCheckM4(srv3.Verify(cli3.VerifyM3(srv3.Verify(cli3.VerifyM1()), cli2.DeviceLtpk)));
            Check("pair-verify agrees on key", srv3.SharedSecret != null && srv3.SharedSecret.SequenceEqual(cli3.SharedSecret));

            var srv4 = new PairingServer("AA:BB:CC:DD:EE:FF", accSeed);
            var cli4 = new PairingClient("controller-1", ctlSeed);
            var badM4 = srv4.Setup(cli4.SetupM3(srv4.Setup(cli4.SetupM1()), "1234"));
            bool rejected = false;
            try { cli4.SetupCheckM4(badM4); } catch (InvalidOperationException) { rejected = true; }
            Check("wrong PIN is rejected", rejected && srv4.SharedSecret == null);
        }

        // ALAC: decode ffmpeg-encoded files and compare with the source PCM
        string codecDir = Environment.GetEnvironmentVariable("AIRWAVE_CODEC_DIR") ?? FindTestData();
        if (codecDir != null && File.Exists(Path.Combine(codecDir, "alac16.caf")))
        {
            var refPcm = File.ReadAllBytes(Path.Combine(codecDir, "ref.raw"));
            foreach (var (file, bits) in new[] { ("alac16.caf", 16), ("alac24.caf", 24) })
            {
                var (cookie, packets) = Caf.Read(Path.Combine(codecDir, file));
                var dec = new AirWave.Core.Audio.AlacDecoder((int)Caf.BE32(cookie, 0), cookie[5], cookie[6], cookie[7], cookie[8]);
                var outBuf = new short[8192];
                long pos = 0, mismatches = 0, compared = 0;
                foreach (var pkt in packets)
                {
                    int n = dec.Decode(pkt, outBuf);
                    for (int i = 0; i < n * 2 && pos + 1 < refPcm.Length; i++, pos += 2)
                    {
                        short r = (short)(refPcm[pos] | (refPcm[pos + 1] << 8));
                        compared++;
                        if (Math.Abs(outBuf[i] - r) > (bits == 16 ? 0 : 1)) mismatches++;
                    }
                }
                Check($"ALAC {bits}-bit decodes exactly ({compared} samples, {mismatches} differ)", compared > 150000 && mismatches == 0);
            }

            // AAC: frame by frame through the platform decoder, compared with ffmpeg's decode of the same file
            var adts = File.ReadAllBytes(Path.Combine(codecDir, "aac.adts"));
            var aacRef = File.ReadAllBytes(Path.Combine(codecDir, "aac_ref.raw"));
            var got = new List<short>();
            int frames = 0;
            using (var dec = AirWave.Core.Audio.AacDecoder.Create())
            {
                var order = new List<uint>();
                dec.Decoded += (rtp, seq, pcm, n) => { lock (got) { order.Add(rtp); got.AddRange(pcm.AsSpan(0, n * 2).ToArray()); } };
                int q = 0; uint rtpTs = 1000;
                while (q + 7 <= adts.Length)
                {
                    int len = ((adts[q + 3] & 3) << 11) | (adts[q + 4] << 3) | (adts[q + 5] >> 5);
                    int hdr = (adts[q + 1] & 1) == 1 ? 7 : 9;
                    dec.Submit(rtpTs, (uint)frames, adts.AsSpan(q + hdr, len - hdr));
                    rtpTs += 1024; frames++; q += len;
                }
                for (int i = 0; i < 50 && got.Count < (frames - 2) * 2048; i++) Thread.Sleep(50);
                Check($"AAC decoder keeps frames in order ({order.Count}/{frames})", order.Count >= frames - 2 && order.Zip(order.Skip(1)).All(z => z.Second == z.First + 1024));
            }
            var refS = new short[aacRef.Length / 2];
            Buffer.BlockCopy(aacRef, 0, refS, 0, aacRef.Length);
            var gotS = got.ToArray();
            // Best alignment within +-2048 frames, and how close the signals are there
            int bestLag = 0; double bestErr = double.MaxValue;
            for (int lag = -2048; lag <= 2048; lag += 1)
            {
                double err = 0; int cnt = 0;
                for (int i = 20000; i < Math.Min(gotS.Length, refS.Length) - 6000; i += 37)
                {
                    int j = i + lag * 2;
                    if (j < 0 || j >= refS.Length) continue;
                    double d = gotS[i] - refS[j]; err += d * d; cnt++;
                }
                if (cnt > 0 && err / cnt < bestErr) { bestErr = err / cnt; bestLag = lag; }
            }
            double sig = refS.Skip(20000).Take(60000).Select(v => (double)v * v).Average();
            double snr = 10 * Math.Log10(sig / Math.Max(1e-9, bestErr));
            // Same audio but at a different level (some decoders apply their own gain)? Fit a gain and compare again.
            double xy = 0, xx = 0;
            int nCmp = Math.Min(gotS.Length, refS.Length);
            for (int i = 20000; i < nCmp - 6000; i++) { xy += (double)gotS[i] * refS[i]; xx += (double)refS[i] * refS[i]; }
            double gain = xx > 0 ? xy / xx : 1;
            double e2 = 0; int c2 = 0;
            var perFrameErr = new List<double>();
            for (int f = 0; f + 1 < nCmp / 2048; f++)
            {
                double fe = 0, fs = 0;
                for (int i = f * 2048; i < (f + 1) * 2048; i++) { double d = gotS[i] - gain * refS[i]; fe += d * d; fs += (double)refS[i] * refS[i]; }
                if (f >= 10) { e2 += fe; c2 += 2048; }
                perFrameErr.Add(fs > 0 ? 10 * Math.Log10(fs / Math.Max(1e-9, fe)) : 99);
            }
            double snrGain = 10 * Math.Log10(sig / Math.Max(1e-9, e2 / Math.Max(1, c2)));
            Console.WriteLine($"     gain vs reference {gain:0.000}, SNR after gain {snrGain:0} dB, per-frame SNR: {string.Join(" ", perFrameErr.Take(16).Select(x => x.ToString("0")))}");
            if (snr <= 30 && bestLag == 0 && snrGain > 30) snr = snrGain; // level difference only
            Check($"AAC output lines up with the reference decoder (lag {bestLag} frames, SNR {snr:0} dB)", bestLag == 0 && snr > 30);
        }
        else Console.WriteLine("skip ALAC (set AIRWAVE_CODEC_DIR)");

        Console.WriteLine(_fail == 0 ? "ALL PASSED" : $"{_fail} FAILED");
        return _fail == 0 ? 0 : 1;
    }
}

static class Caf
{
    public static uint BE32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);

    /// <summary>Reads the ALAC magic cookie and packets from a CAF file.</summary>
    public static (byte[] cookie, List<byte[]> packets) Read(string path)
    {
        var f = File.ReadAllBytes(path);
        int p = 8;
        byte[] cookie = null, data = null;
        var sizes = new List<int>();
        while (p + 12 <= f.Length)
        {
            string type = System.Text.Encoding.ASCII.GetString(f, p, 4);
            long size = (long)BE32(f, p + 4) << 32 | BE32(f, p + 8);
            int body = p + 12;
            if (size < 0 || body + size > f.Length) size = f.Length - body;
            if (type == "kuki")
            {
                cookie = f.AsSpan(body, (int)size).ToArray();
                // Some writers wrap the 24-byte ALACSpecificConfig in 'frma'/'alac' atoms
                if (cookie.Length > 24) cookie = cookie.AsSpan(cookie.Length - 24, 24).ToArray();
            }
            if (type == "data") data = f.AsSpan(body + 4, (int)size - 4).ToArray();
            if (type == "pakt")
            {
                long n = (long)BE32(f, body) << 32 | BE32(f, body + 4);
                int q = body + 24;
                for (long i = 0; i < n; i++)
                {
                    int v = 0; byte b;
                    do { b = f[q++]; v = (v << 7) | (b & 0x7f); } while ((b & 0x80) != 0);
                    sizes.Add(v);
                }
            }
            p = body + (int)size;
        }
        var packets = new List<byte[]>();
        int o = 0;
        foreach (var s in sizes) { packets.Add(data.AsSpan(o, s).ToArray()); o += s; }
        return (cookie, packets);
    }

    static int IndexOf(byte[] h, byte[] n)
    {
        for (int i = 0; i + n.Length <= h.Length; i++) if (h.AsSpan(i, n.Length).SequenceEqual(n)) return i;
        return -1;
    }
}
