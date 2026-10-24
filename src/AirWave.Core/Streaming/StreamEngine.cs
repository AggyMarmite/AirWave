using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AirWave.Core.Audio;
using AirWave.Core.Discovery;
using AirWave.Core.Timing;

namespace AirWave.Core.Streaming;

/// <summary>
/// The multi-room streamer. One audio timeline is produced and every speaker gets exactly the
/// same RTP packets (each encrypted with that speaker's own key) plus sync packets that tie
/// RTP time to the shared clock. Because all speakers follow the same clock (via NTP replies or
/// our PTP master) and the same RTP-time → clock mapping, they play in step with each other.
/// </summary>
public sealed class StreamEngine : IAsyncDisposable
{
    private const int Spp = SpeakerSession.SamplesPerPacket;
    private const int Sr = SpeakerSession.SampleRate;
    private const int HistorySize = 1024; // ~8 s of packets kept for resend requests

    private readonly ConcurrentDictionary<string, SpeakerSession> _sessions = new();
    private readonly UdpClient _control;
    private readonly CancellationTokenSource _cts = new();
    private Thread _thread;
    private volatile bool _running;
    private readonly object _startLock = new();

    // RTP timeline
    private uint _rtpBase;
    private ushort _seqBase;
    private uint _ssrc;
    private int _controlLogged;
    private long _lastDiagIndex;
    private long _t0;
    private long _packetIndex;

    private readonly byte[][] _histPayload = new byte[HistorySize][];
    private readonly int[] _histLen = new int[HistorySize];
    private readonly byte[][] _histHeader = new byte[HistorySize][];
    private readonly ushort[] _histSeq = new ushort[HistorySize];

    public NtpTimingServer Ntp { get; }
    public PtpMaster Ptp { get; }
    public int ControlPort { get; }
    public string ClientName { get; set; } = Environment.MachineName;
    public string DacpId { get; }
    public uint ActiveRemote { get; }
    public string DeviceIdColon { get; }
    public string GroupUuid { get; } = Guid.NewGuid().ToString().ToUpperInvariant();
    public string PtpClockUuid { get; } = Guid.NewGuid().ToString().ToUpperInvariant();
    public bool ForceNtp { get; set; }

    /// <summary>How far ahead of the speakers' playback the audio is sent (ms). Bigger copes better with weak Wi-Fi.</summary>
    public int DelayMs { get; set; } = 1500;

    private IAudioSource _source;
    public IAudioSource Source
    {
        get => _source;
        set
        {
            var old = _source;
            _source = value;
            if (_running) { old?.Stop(); value?.Start(); }
        }
    }

    public bool IsStreaming => _running;

    // Test hook: drop this fraction of first-time audio sends to exercise the resend path (AIRWAVE_TEST_DROP=0.03)
    private static readonly double TestDrop = double.TryParse(Environment.GetEnvironmentVariable("AIRWAVE_TEST_DROP"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
    private readonly Random _rng = new();
    public IReadOnlyCollection<SpeakerSession> Sessions => _sessions.Values.ToList();

    public event Action<SpeakerSession> SpeakerChanged;

    public StreamEngine(bool enablePtp = true)
    {
        var id = RandomNumberGenerator.GetBytes(8);
        DacpId = Convert.ToHexString(id);
        ActiveRemote = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4));
        DeviceIdColon = string.Join(":", id.Take(6).Select(b => b.ToString("X2")));

        _control = UdpHelpers.CreateDualMode(0);
        ControlPort = ((IPEndPoint)_control.Client.LocalEndPoint).Port;
        Ntp = new NtpTimingServer();
        if (enablePtp) Ptp = new PtpMaster();
        _ = Task.Run(ControlLoop);
    }

    // ------------------------------------------------------------------ speakers

    private readonly ConcurrentDictionary<string, int> _offsets = new();

    /// <summary>
    /// Per-speaker timing nudge in milliseconds: positive plays that speaker later, negative earlier
    /// (for speakers or soundbars that add their own delay). Takes effect within a second, even mid-song.
    /// </summary>
    public void SetSpeakerOffset(string id, int ms)
    {
        ms = Math.Clamp(ms, -MaxOffsetMs, MaxOffsetMs);
        if (ms == 0) _offsets.TryRemove(id, out _); else _offsets[id] = ms;
    }

    public int GetSpeakerOffset(string id) => _offsets.TryGetValue(id, out var ms) ? ms : 0;
    public const int MaxOffsetMs = 500;

    public SpeakerSession GetSession(string id) => _sessions.TryGetValue(id, out var s) ? s : null;

    /// <summary>Adds a speaker to the group (joins in-sync even while others are already playing).</summary>
    public async Task<SpeakerSession> AddSpeakerAsync(SpeakerInfo info, double volume, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(info.Id, out var existing) && existing.State is SpeakerState.Connecting or SpeakerState.Ready or SpeakerState.Streaming)
            return existing;
        var s = new SpeakerSession(this, info, volume);
        s.StateChanged += x => SpeakerChanged?.Invoke(x);
        _sessions[info.Id] = s;
        EnsureRunning();
        bool ok = await s.StartAsync(ct);
        if (!ok) _sessions.TryRemove(new KeyValuePair<string, SpeakerSession>(info.Id, s));
        StopIfIdle();
        return s;
    }

    public async Task RemoveSpeakerAsync(string id)
    {
        if (_sessions.TryRemove(id, out var s)) await s.StopAsync();
        StopIfIdle();
    }

    public async Task RemoveAllAsync()
    {
        foreach (var id in _sessions.Keys.ToList()) await RemoveSpeakerAsync(id);
    }

    internal void OnSpeakerDropped(SpeakerSession s)
    {
        _sessions.TryRemove(new KeyValuePair<string, SpeakerSession>(s.Info.Id, s));
        StopIfIdle();
    }

    // ------------------------------------------------------------------ scheduler

    private void EnsureRunning()
    {
        lock (_startLock)
        {
            if (_running) return;
            _rtpBase = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4));
            _seqBase = BinaryPrimitives.ReadUInt16BigEndian(RandomNumberGenerator.GetBytes(2));
            // AirPlay 2 senders using PTP timing leave the SSRC at zero (as OwnTone does, and Apple devices expect);
            // only the older NTP-timed sessions use a random one
            _ssrc = Ptp?.IsRunning == true && !ForceNtp ? 0u : BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4));
            _packetIndex = 0;
            Array.Clear(_histPayload);
            _t0 = MonoClock.NowNs;
            _running = true;
            _source?.Start();
            _thread = new Thread(Run) { IsBackground = true, Name = "AirWave streamer", Priority = ThreadPriority.Highest };
            _thread.Start();
            Log.Info("Streaming started");
        }
    }

    private void StopIfIdle()
    {
        lock (_startLock)
        {
            if (!_running || !_sessions.IsEmpty) return;
            _running = false;
            _source?.Stop();
            Log.Info("Streaming stopped (no speakers)");
        }
    }

    private static long PacketNs(long index) => index * Spp * 1_000_000_000L / Sr;

    private void Run()
    {
        var pcm = new short[Spp * 2];
        var payload = new byte[AlacEncoder.MaxSize(Spp)];
        var header = new byte[12];
        long lastSyncIndex = -1000;

        while (_running && !_cts.IsCancellationRequested)
        {
            long due = _t0 + PacketNs(_packetIndex);
            long now = MonoClock.NowNs;
            long wait = due - now;
            if (wait > 1_500_000) { Thread.Sleep((int)Math.Min(wait / 1_000_000 - 1, 20)); continue; }
            if (wait > 0) { Thread.SpinWait(200); continue; }

            if (-wait > 500_000_000L)
            {
                // We fell far behind (PC was asleep, debugger, ...): jump the timeline forward and resync everyone
                long skip = (-wait) / PacketNs(1);
                _packetIndex += skip;
                Log.Warn($"Streamer fell behind by {-wait / 1_000_000} ms, skipping ahead");
                foreach (var s in _sessions.Values) s.NeedsStart = true;
                continue;
            }

            var src = _source;
            if (src != null) src.Read(pcm, Spp); else Array.Clear(pcm);
            int len = AlacEncoder.EncodeStereo16(pcm, Spp, Spp, payload);

            ushort seq = (ushort)(_seqBase + _packetIndex);
            uint rtp = (uint)(_rtpBase + _packetIndex * Spp);
            header[0] = 0x80;
            header[1] = 0x60;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), seq);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), rtp);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), _ssrc);

            int h = seq % HistorySize;
            _histPayload[h] = payload.AsSpan(0, len).ToArray();
            _histLen[h] = len;
            _histHeader[h] = (byte[])header.Clone();
            _histSeq[h] = seq;

            if (Log.MinLevel <= LogLevel.Debug && _packetIndex - _lastDiagIndex >= 10 * Sr / Spp && Ptp != null)
            {
                _lastDiagIndex = _packetIndex;
                foreach (var d in _sessions.Values)
                    if (d.Address != null) Log.Debug($"{d.Info.Name}: {d.State}, {(d.UsesPtp ? "PTP" : "NTP")}; PTP from it: {Ptp.DescribePeer(d.Address)}");
            }
            bool periodicSync = _packetIndex - lastSyncIndex >= Sr / Spp; // about once a second
            if (periodicSync) lastSyncIndex = _packetIndex;

            foreach (var s in _sessions.Values)
            {
                if (s.State is not (SpeakerState.Ready or SpeakerState.Streaming)) continue;
                if (s.NeedsStart)
                {
                    s.NeedsStart = false;
                    SendSync(s, rtp, start: true);
                    header[1] = 0xE0; // marker bit: first packet for this speaker
                    s.SendAudio(header, payload.AsSpan(0, len), seq);
                    header[1] = 0x60;
                    s.MarkStreaming();
                    continue;
                }
                if (periodicSync) SendSync(s, rtp, start: false);
                if (TestDrop > 0 && _rng.NextDouble() < TestDrop) continue;
                s.SendAudio(header, payload.AsSpan(0, len), seq);
            }
            _packetIndex++;
        }
    }

    /// <summary>
    /// Sync packet: "RTP time X is heard at clock time T". X is derived from the timeline itself
    /// (not from when we happened to send), so every speaker gets the identical mapping.
    /// </summary>
    private void SendSync(SpeakerSession s, uint nextRtp, bool start)
    {
        if (s.ControlEndPoint == null) return;
        long now = MonoClock.NowNs;
        long delayNs = (DelayMs + GetSpeakerOffset(s.Info.Id)) * 1_000_000L;
        // The sample that should be playing right now
        long elapsed = now - _t0 - delayNs;
        uint curRtp = (uint)(_rtpBase + (elapsed * Sr / 1_000_000_000L));

        byte[] pkt;
        if (s.UsesPtp)
        {
            pkt = new byte[28];
            pkt[0] = start ? (byte)0x90 : (byte)0x80;
            pkt[1] = 0xd7;
            pkt[3] = 0x06;
            BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(4), curRtp);
            BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(8), (ulong)now);
            BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(16), nextRtp - 11025);
            BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(20), Ptp.ClockId);
        }
        else
        {
            pkt = new byte[20];
            pkt[0] = start ? (byte)0x90 : (byte)0x80;
            pkt[1] = 0xd4;
            pkt[3] = 0x07;
            BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(4), curRtp);
            BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(8), MonoClock.ToNtp(now));
            BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(16), nextRtp);
        }
        try { _control.Client.SendTo(pkt, UdpHelpers.ForSocket(_control.Client, s.ControlEndPoint)); }
        catch (Exception ex) { Log.Debug($"sync to {s.Info.Name}: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ resends

    private async Task ControlLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _control.ReceiveAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            var b = r.Buffer;
            if (TestDrop > 0 || (Log.MinLevel <= LogLevel.Debug && _controlLogged++ < 30)) Log.Debug($"control packet from {r.RemoteEndPoint}: {Convert.ToHexString(b.AsSpan(0, Math.Min(8, b.Length)))}");
            if (b.Length < 8 || b[0] != 0x80 || b[1] != 0xd5) continue;
            ushort first = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(4));
            ushort count = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(6));
            var from = UdpHelpers.Normalize(r.RemoteEndPoint).Address;
            var s = _sessions.Values.FirstOrDefault(x => x.Address != null && (x.Address.IsIPv4MappedToIPv6 ? x.Address.MapToIPv4() : x.Address).Equals(from));
            if (s == null) continue;
            Log.Debug($"{s.Info.Name}: resend {count} packet(s) from {first}");
            for (int i = 0; i < count && i < 256; i++)
            {
                ushort seq = (ushort)(first + i);
                int h = seq % HistorySize;
                var p = _histPayload[h];
                if (p == null || _histSeq[h] != seq) continue;
                s.SendAudio(_histHeader[h], p.AsSpan(0, _histLen[h]), seq);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await RemoveAllAsync();
        _running = false;
        _cts.Cancel();
        _control.Dispose();
        Ntp.Dispose();
        Ptp?.Dispose();
        _source?.Dispose();
    }
}
