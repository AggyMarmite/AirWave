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
    /// <summary>Send buffered AAC to every speaker that supports it, not just Apple ones (tests).</summary>
    public bool ForceBuffered { get; set; }

    /// <summary>Overrides how each speaker is sent audio (the speaker test tries each way in turn).</summary>
    public Func<SpeakerInfo, StreamStyle?> StyleOverride { get; set; }

    internal StreamStyle StyleFor(SpeakerInfo info, bool ptp)
    {
        var o = StyleOverride?.Invoke(info);
        if (o is StreamStyle st && (ptp || st is StreamStyle.RealtimeAhead or StreamStyle.RealtimeJustInTime)) return st;
        return ptp && WantsBuffered(info) ? StreamStyle.Buffered : StreamStyle.RealtimeAhead;
    }

    /// <summary>Follows the speakers' clocks, for when we let them keep time (<see cref="PtpMaster.Yield"/>).</summary>
    public PtpFollower Follower { get; }

    /// <summary>
    /// Our clock time → the time on the timeline this speaker uses: ours, or (when yielding) the speaker's own.
    /// False if we haven't followed the speaker's clock for long enough yet.
    /// </summary>
    internal bool TryRemoteTime(SpeakerSession s, long localNs, out long remoteNs, out ulong timeline)
    {
        remoteNs = localNs; timeline = Ptp?.ClockId ?? 0;
        if (!SpeakerKeepsTime(s.Info, s.UsesPtp)) return true;
        var off = Follower.GetOffset(s.SpeakerClockId, out long followed);
        if (off == null || followed < 500_000_000L) return false;
        remoteNs = localNs + off.Value;
        timeline = s.SpeakerClockId;
        return true;
    }

    /// <summary>
    /// HomePods keep time themselves and AirWave follows their clock, as a Mac does. Tested on HomePod minis: with
    /// AirWave as the timekeeper they often stay silent. Other speakers keep following AirWave's clock.
    /// </summary>
    public bool HomePodsKeepTime { get; set; } = true;

    /// <summary>Whether this speaker keeps time (we follow its clock) rather than following ours.</summary>
    internal bool SpeakerKeepsTime(SpeakerInfo info, bool usesPtp) =>
        Ptp != null && usesPtp && (Ptp.Yield || (HomePodsKeepTime && info.IsHomePod));

    /// <summary>Apple speakers and TVs get buffered audio, as from an iPhone; others get realtime ALAC.</summary>
    internal bool WantsBuffered(SpeakerInfo info) =>
        info.SupportsBufferedAudio && (ForceBuffered || info.IsHomePod || info.IsAppleTv || info.IsMac);

    /// <summary>How far ahead of the speakers' playback the audio is sent (ms). Bigger copes better with weak Wi-Fi.
    /// With <see cref="AutoDelay"/> on, this is used only for groups that include non-Apple speakers.</summary>
    public int DelayMs { get => _manualDelayMs; set { _manualDelayMs = value; RecomputeDelay(); } }
    private int _manualDelayMs = 1500;

    /// <summary>
    /// Pick the delay from the group: HomePods alone get a short one (they're sent buffered audio over TCP, which
    /// recovers from Wi-Fi hiccups by itself); groups with other speakers use <see cref="DelayMs"/>.
    /// </summary>
    public bool AutoDelay { get => _autoDelay; set { _autoDelay = value; RecomputeDelay(); } }
    private bool _autoDelay;

    /// <summary>Delay used automatically when every speaker playing is sent buffered audio (ms; heard ≈ this + 0.35 s).</summary>
    public const int AutoBufferedDelayMs = 500;

    private volatile int _delayMs = 1500;
    /// <summary>The delay in use now (ms).</summary>
    public int CurrentDelayMs => _delayMs;
    public event Action DelayChanged;

    // Connecting speakers haven't been set up yet: go by what they will get
    private bool GetsBuffered(SpeakerSession x) => x.State is SpeakerState.Idle or SpeakerState.Connecting
        ? StyleFor(x.Info, x.Info.SupportsPtp && Ptp?.IsRunning == true && !ForceNtp) is StreamStyle.Buffered or StreamStyle.BufferedLateAnchor
        : x.Buffered;

    private void RecomputeDelay()
    {
        int d = _manualDelayMs;
        if (_autoDelay)
        {
            var active = _sessions.Values.Where(x => x.State != SpeakerState.Failed).ToList();
            if (active.Count > 0 && active.All(GetsBuffered))
                d = Math.Min(AutoBufferedDelayMs, _manualDelayMs);
        }
        if (d == _delayMs) return;
        int old = _delayMs;
        _delayMs = d;
        // Everyone moves to the new timing: buffered speakers take a new play-at time, realtime ones follow the next sync
        foreach (var s in _sessions.Values) if (s.Buffered) s.NeedsAnchor = true;
        if (_running) Log.Info($"Delay {old} → {d} ms");
        DelayChanged?.Invoke();
    }

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

    /// <summary>Clock time of the start of the timeline (tests).</summary>
    public long StartedAtNs => _t0;

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
        if (enablePtp) { Ptp = new PtpMaster(); Follower = new PtpFollower(Ptp, wake: false); }
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
        if (_sessions.TryGetValue(id, out var s) && s.Buffered) s.NeedsAnchor = true; // buffered speakers take it as a new anchor
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
        RecomputeDelay();
        EnsureRunning();
        bool ok = await s.StartAsync(ct);
        if (!ok) _sessions.TryRemove(new KeyValuePair<string, SpeakerSession>(info.Id, s));
        RecomputeDelay();
        StopIfIdle();
        return s;
    }

    public async Task RemoveSpeakerAsync(string id)
    {
        if (_sessions.TryRemove(id, out var s)) await s.StopAsync();
        RecomputeDelay();
        StopIfIdle();
    }

    public async Task RemoveAllAsync()
    {
        foreach (var id in _sessions.Keys.ToList()) await RemoveSpeakerAsync(id);
    }

    internal void OnSpeakerDropped(SpeakerSession s)
    {
        _sessions.TryRemove(new KeyValuePair<string, SpeakerSession>(s.Info.Id, s));
        RecomputeDelay();
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
                if (s.Buffered) continue; // fed below
                if (s.Style == StreamStyle.RealtimeJustInTime)
                {
                    // Held back and sent as each packet falls due (below)
                    long dueNs = _t0 + PacketNs(_packetIndex) + _delayMs * 1_000_000L;
                    lock (s.JitQueue) s.JitQueue.Enqueue(((byte[])header.Clone(), payload.AsSpan(0, len).ToArray(), seq, rtp, dueNs));
                    continue;
                }
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
            FeedBuffered(pcm, _packetIndex);
            SendJustInTime();
            _packetIndex++;
        }
    }

    // ------------------------------------------------------------------ just-in-time realtime speakers

    /// <summary>
    /// The way another open-source sender plays to HomePod minis: each packet goes out as it's due, with a sync packet
    /// saying "this packet's RTP time is now" (the speaker adds its own latency).
    /// </summary>
    private void SendJustInTime()
    {
        long now = MonoClock.NowNs;
        foreach (var s in _sessions.Values)
        {
            if (s.Style != StreamStyle.RealtimeJustInTime || s.State is not (SpeakerState.Ready or SpeakerState.Streaming)) continue;
            while (true)
            {
                (byte[] Header, byte[] Payload, ushort Seq, uint Rtp, long DueNs) p;
                lock (s.JitQueue) { if (s.JitQueue.Count == 0 || s.JitQueue.Peek().DueNs > now) break; p = s.JitQueue.Dequeue(); }
                bool first = !s.JitStarted;
                if (first || (p.Rtp - s.JitLastSyncRtp) >= Sr)
                {
                    if (!TryRemoteTime(s, now, out long remote, out ulong clock)) { if (first) { lock (s.JitQueue) s.JitQueue.Clear(); } break; }
                    var pkt = new byte[28];
                    pkt[0] = first ? (byte)0x90 : (byte)0x80;
                    pkt[1] = 0xd7;
                    pkt[3] = 0x06;
                    BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(4), p.Rtp);
                    BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(8), (ulong)remote);
                    BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(16), p.Rtp + Spp);
                    BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(20), clock);
                    if (s.ControlEndPoint != null)
                        try { _control.Client.SendTo(pkt, UdpHelpers.ForSocket(_control.Client, s.ControlEndPoint)); } catch { }
                    s.JitLastSyncRtp = p.Rtp;
                }
                if (first) { p.Header[1] = 0xE0; s.JitStarted = true; s.MarkStreaming(); }
                s.SendAudio(p.Header, p.Payload, p.Seq);
            }
        }
    }

    // ------------------------------------------------------------------ buffered (AAC) speakers

    private const long RealtimeLatencyNs = 11025L * 1_000_000_000L / Sr;
    private IAacEncoder _aac;
    private long _aacStartSample, _aacFramesOut;
    private readonly short[] _aacPcm = new short[1024 * 2];
    private int _aacFill;

    /// <summary>
    /// Buffered speakers get the same timeline as the realtime ones, as AAC: RTP time = _rtpBase + sample index,
    /// heard at _t0 + DelayMs + index/44100 — so both kinds play in step. The encoder's start-up delay is taken off.
    /// </summary>
    private void FeedBuffered(short[] pcm, long packetIndex)
    {
        bool any = false;
        foreach (var s in _sessions.Values)
            if (s.Buffered && s.State is SpeakerState.Ready or SpeakerState.Streaming) { any = true; break; }
        if (!any)
        {
            if (_aac != null) { _aac.Dispose(); _aac = null; }
            return;
        }
        long sample = packetIndex * Spp;
        if (_aac == null || sample != _aacStartSample + _aacFramesOut * 1024 + _aacLag + _aacFill)
        {
            // (Re)start the encoder at this point of the timeline (first speaker, or after the timeline jumped)
            _aac?.Dispose();
            try { _aac = AacEncoder.Create(); }
            catch (Exception ex) { Log.Error($"No AAC encoder for HomePods/Apple TVs: {ex.Message}"); return; }
            Log.Debug($"Buffered audio: {_aac.Name}, start-up delay {_aac.DelaySamples} samples");
            _aacStartSample = sample;
            _aacFramesOut = 0;
            _aacFill = 0;
            _aacLag = 0;
        }

        // Anchors first, so a speaker knows the timing before (or as) its first frames arrive
        foreach (var s in _sessions.Values)
        {
            if (!s.Buffered || s.State is not (SpeakerState.Ready or SpeakerState.Streaming) || !s.NeedsAnchor) continue;
            uint aRtp = (uint)(_rtpBase + sample);
            // Realtime speakers add the stream's 11025-sample (250 ms) latency to the sync packets' timing; buffered
            // anchors are exact, so add the same here and both kinds play together
            long aNs = _t0 + PacketNs(packetIndex) + (_delayMs + GetSpeakerOffset(s.Info.Id)) * 1_000_000L + RealtimeLatencyNs;
            if (!s.TryAnchor(aRtp, aNs)) continue; // speaker's clock not followed yet: next time round
            s.NeedsAnchor = false;
            s.MarkStreaming();
        }

        int i = 0;
        while (i < Spp)
        {
            int n = Math.Min(Spp - i, 1024 - _aacFill);
            Array.Copy(pcm, i * 2, _aacPcm, _aacFill * 2, n * 2);
            _aacFill += n; i += n;
            if (_aacFill < 1024) break;
            _aacFill = 0;
            _aacLag += 1024;
            List<byte[]> frames;
            try { frames = _aac.Encode(_aacPcm); }
            catch (Exception ex) { Log.Warn($"AAC encoding failed: {ex.Message}"); _aac.Dispose(); _aac = null; return; }
            foreach (var f in frames)
            {
                _aacLag -= 1024;
                uint frtp = (uint)(_rtpBase + _aacStartSample + _aacFramesOut * 1024 - _aac.DelaySamples);
                _aacFramesOut++;
                foreach (var s in _sessions.Values)
                    if (s.Buffered && s.State == SpeakerState.Streaming) s.SendBuffered(frtp, f);
            }
        }
    }

    // Samples handed to the encoder that haven't come out yet (so the continuity check above can tell a jump)
    private long _aacLag;

    /// <summary>
    /// Sync packet: "RTP time X is heard at clock time T". X is derived from the timeline itself
    /// (not from when we happened to send), so every speaker gets the identical mapping.
    /// </summary>
    private void SendSync(SpeakerSession s, uint nextRtp, bool start)
    {
        if (s.ControlEndPoint == null) return;
        long now = MonoClock.NowNs;
        if (!TryRemoteTime(s, now, out long remoteNow, out ulong timelineClock)) return;
        long delayNs = (_delayMs + GetSpeakerOffset(s.Info.Id)) * 1_000_000L;
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
            BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(8), (ulong)remoteNow);
            BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(16), nextRtp - 11025);
            BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(20), timelineClock);
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
