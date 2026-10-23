using System.Net;
using AirWave.Core.Mirroring;
using AirWave.Core.Timing;

/// <summary>
/// "mirror-test ip:port --h264 file [--pin|--password pw] [--audio]": mirrors a pre-encoded H.264 (Annex B) stream to a TV
/// at its natural frame rate, as if it were the screen. Pairs first if needed (asking for the PIN on stdin, or --pin-value).
/// </summary>
static class MirrorTestCommand
{
    public static async Task<int> Run(string[] args)
    {
        var tv = IPEndPoint.Parse(args[1]);
        string h264 = Arg(args, "--h264");
        string password = Arg(args, "--password");
        string pinValue = Arg(args, "--pin-value");
        int fps = int.Parse(Arg(args, "--fps") ?? "30");
        string pairDir = Arg(args, "--pair-dir") ?? Path.Combine(Path.GetTempPath(), "airwave-mirror-test");
        string id = tv.ToString();

        var creds = MirrorPairings.Get(pairDir, id);
        MirrorSession session = null;
        for (int attempt = 0; attempt < 2 && session == null; attempt++)
        {
            try
            {
                session = await MirrorSession.OpenAsync(tv, "Test TV", creds, password, "AirWave Test", args.Contains("--audio"));
            }
            catch (MirrorException ex) when (ex.Problem is MirrorProblem.NeedsPin or MirrorProblem.NeedsPassword or MirrorProblem.PairingLost && attempt == 0)
            {
                Console.WriteLine($"PAIRING needed ({ex.Problem}): {ex.Message}");
                if (ex.Problem == MirrorProblem.NeedsPassword && password == null) { Console.WriteLine("ERR password needed"); return 3; }
                creds = await MirrorSession.PairAsync(tv, "AirWave Test", async () =>
                {
                    if (pinValue != null) { await Task.Delay(300); return pinValue; }
                    Console.Write("PIN shown on the TV: ");
                    return Console.ReadLine();
                }, password);
                MirrorPairings.Save(pairDir, id, creds);
                Console.WriteLine("PAIRED");
            }
        }
        Console.WriteLine($"OPEN display={session.Display} audio={session.HasAudio}");

        // Split the Annex B file into access units (one per frame, SPS/PPS separated out)
        var aus = H264.AccessUnits(File.ReadAllBytes(h264));
        Console.WriteLine($"{aus.Count} frames to send");
        long start = MonoClock.NowNs;
        var silence = new short[352 * 2];
        long audioSent = 0;
        for (int i = 0; i < aus.Count && session.IsOpen; i++)
        {
            long due = start + i * 1_000_000_000L / fps;
            while (MonoClock.NowNs < due)
            {
                // Audio: keep up with real time in 352-frame packets (a quiet tone)
                long audioDue = (MonoClock.NowNs - start) * 44100 / 1_000_000_000L;
                while (session.HasAudio && audioSent + 352 <= audioDue)
                {
                    for (int k = 0; k < 352; k++) silence[2 * k] = silence[2 * k + 1] = (short)(3000 * Math.Sin(2 * Math.PI * 440 * (audioSent + k) / 44100.0));
                    session.SendAudio(silence, start + audioSent * 1_000_000_000L / 44100);
                    audioSent += 352;
                }
                await Task.Delay(2);
            }
            var au = aus[i];
            if (au.Sps != null) session.SetCodecConfig(H264.AvcC(au.Sps, au.Pps), au.Width, au.Height);
            session.SendFrame(au.Avcc, au.Keyframe, due);
        }
        await Task.Delay(1500);
        Console.WriteLine($"SENT {aus.Count} frames, open={session.IsOpen}");
        session.Dispose();
        return 0;
    }

    static string Arg(string[] a, string k) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}
