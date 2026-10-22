using AirWave.Core.Timing;

namespace AirWave.Core.Receiver;

/// <summary>
/// The receiver's playback timeline. Decoded audio is stored by RTP timestamp; the sender's anchor
/// ("RTP frame X plays at network time T") plus the followed sender clock tell us which frame is due
/// at any moment on our own clock. Outputs pull audio for the instant it will actually be heard;
/// small drifts are corrected a frame at a time, big jumps by resyncing.
/// </summary>
public sealed class ReceiverPlayer
{
    public const int Rate = 44100;
    private const int ResyncFrames = Rate * 20 / 1000;  // > 20 ms off: jump straight to the right place
    private const double NudgeFrames = Rate * 0.5 / 1000; // smoothed error > 0.5 ms: correct a frame at a time

    private sealed class Block
    {
        public long Rtp;       // unwrapped
        public uint Seq;
        public short[] Pcm;    // interleaved stereo
        public int Frames;
    }

    private readonly object _lock = new();
    private readonly SortedList<long, Block> _blocks = new();
    private readonly PtpFollower _clock;
    private long _rtpBase = -1;          // for unwrapping 32-bit RTP times

    // Anchor
    private bool _hasAnchor;
    private long _anchorRtp;
    private long _anchorRemoteNs;
    private ulong _anchorTimeline;
    private bool _playing;

    // Output cursor
    private long _cursor = long.MinValue;
    private double _err;                 // smoothed (cursor - target) in frames
    private long _lastDiag;
    private float _gain = 1f;

    public float Level { get; private set; }
    public bool IsPlaying => _playing && _hasAnchor;
    /// <summary>Audio is arriving and due to be heard (for the UI).</summary>
    public bool IsActive => IsPlaying && _lastAudioAt > Environment.TickCount64 - 3000;
    private long _lastAudioAt;

    public ReceiverPlayer(PtpFollower clock) { _clock = clock; }

    // ---------------------------------------------------------------- control

    /// <summary>AirPlay volume: 0 (max) down to -30, or -144 for mute.</summary>
    public void SetAirPlayVolume(double v)
    {
        if (v <= -100) { _gain = 0; return; }
        v = Math.Clamp(v, -30, 0);
        // -30..0 mapped to roughly -42..0 dB: half way on the phone's slider is about -20 dB
        _gain = (float)Math.Pow(10, v * 1.4 / 20);
    }

    public void SetAnchor(uint rtp, long remoteNs, ulong timeline)
    {
        lock (_lock)
        {
            _anchorRtp = Unwrap(rtp);
            _anchorRemoteNs = remoteNs;
            _anchorTimeline = timeline;
            _hasAnchor = true;
        }
    }

    public void SetPlaying(bool playing)
    {
        lock (_lock)
        {
            _playing = playing;
            if (!playing) _cursor = long.MinValue;
        }
    }

    public void ClearAnchor()
    {
        lock (_lock) { _hasAnchor = false; _cursor = long.MinValue; }
    }

    /// <summary>Drops everything (new stream, teardown).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _blocks.Clear();
            _rtpBase = -1;
            _hasAnchor = false;
            _playing = false;
            _cursor = long.MinValue;
        }
    }

    /// <summary>Drops queued audio whose sequence number is in [from, until) (23-bit wrap-around); from = null drops everything before until.</summary>
    public void FlushSeq(uint? from, uint until)
    {
        lock (_lock)
        {
            var drop = _blocks.Values.Where(b => from == null ? SeqBefore(b.Seq, until) : !SeqBefore(b.Seq, from.Value) && SeqBefore(b.Seq, until)).Select(b => b.Rtp).ToList();
            foreach (var k in drop) _blocks.Remove(k);
            _cursor = long.MinValue;
        }
    }

    /// <summary>Drops queued audio before the given RTP time (realtime FLUSH).</summary>
    public void FlushBefore(uint rtp)
    {
        lock (_lock)
        {
            long r = Unwrap(rtp);
            foreach (var k in _blocks.Keys.Where(k => k < r).ToList()) _blocks.Remove(k);
            _cursor = long.MinValue;
        }
    }

    internal static bool SeqBefore(uint a, uint b) => ((b - a) & 0x7fffff) is > 0 and < 0x400000;

    // ---------------------------------------------------------------- input

    public void Put(uint rtp, uint seq, short[] pcm, int frames)
    {
        lock (_lock)
        {
            long r = Unwrap(rtp);
            // Drop audio that is already late
            if (_cursor != long.MinValue && r + frames < _cursor) return;
            _blocks[r] = new Block { Rtp = r, Seq = seq, Pcm = pcm, Frames = frames };
            // Keep memory bounded: never hold more than ~60 s
            while (_blocks.Count > 0 && _blocks.Keys[_blocks.Count - 1] - _blocks.Keys[0] > Rate * 60L) _blocks.RemoveAt(0);
        }
    }

    /// <summary>How far (in seconds) queued audio extends beyond what's currently playing (for back-pressure).</summary>
    public double SecondsQueuedAhead
    {
        get
        {
            lock (_lock)
            {
                if (_blocks.Count == 0) return 0;
                var last = _blocks.Values[_blocks.Count - 1];
                long end = last.Rtp + last.Frames;
                long playing = _cursor != long.MinValue ? _cursor : _blocks.Keys[0];
                return Math.Max(0, (end - playing) / (double)Rate);
            }
        }
    }

    private long Unwrap(uint rtp)
    {
        if (_rtpBase < 0) { _rtpBase = rtp; return rtp; }
        long refPoint = _rtpBase;
        long candidate = (refPoint & ~0xFFFFFFFFL) | rtp;
        if (candidate - refPoint > 0x80000000L) candidate -= 0x100000000L;
        else if (refPoint - candidate > 0x80000000L) candidate += 0x100000000L;
        _rtpBase = candidate;
        return candidate;
    }

    // ---------------------------------------------------------------- output

    /// <summary>The (fractional) RTP frame that should be heard at local time <paramref name="localNs"/>, or null if unknown.</summary>
    private double? FrameAt(long localNs)
    {
        if (!_hasAnchor) return null;
        var off = _clock.GetOffset(_anchorTimeline, out long followed);
        if (off == null || followed < 200_000_000L) return null; // need the sender's clock first
        long remote = localNs + off.Value;
        return _anchorRtp + (remote - _anchorRemoteNs) * (double)Rate / 1e9;
    }

    /// <summary>
    /// Fills <paramref name="output"/> (interleaved stereo) with the audio that will be heard starting at
    /// local time <paramref name="localNsOfFirstFrame"/>. Always fills the buffer (silence when nothing is due).
    /// </summary>
    public void Read(long localNsOfFirstFrame, Span<short> output, int frames)
    {
        output[..(frames * 2)].Clear();
        lock (_lock)
        {
            if (!_playing) { Level = 0; return; }
            var target = FrameAt(localNsOfFirstFrame);
            if (target == null) { _cursor = long.MinValue; Level = 0; return; }
            long t = (long)Math.Round(target.Value);
            if (Environment.TickCount64 - _lastDiag > 1000)
            {
                _lastDiag = Environment.TickCount64;
                Log.Debug($"player: offset {_clock.GetOffset(_anchorTimeline)} target {t} cursor {_cursor} err {_err:0.0} blocks {_blocks.Count} first {(_blocks.Count > 0 ? _blocks.Keys[0] : 0)}");
            }

            if (_cursor == long.MinValue || Math.Abs(_cursor - t) > ResyncFrames) { _cursor = t; _err = 0; }
            else
            {
                // Output timing estimates jitter a little; react to the smoothed error, not each reading
                _err = _err * 0.9 + (_cursor - t) * 0.1;
                if (_err < -NudgeFrames) { _cursor++; _err += 1; }      // behind: skip a frame
                else if (_err > NudgeFrames) { _cursor--; _err -= 1; }  // ahead: repeat a frame
            }

            // Drop blocks that are fully in the past
            while (_blocks.Count > 0)
            {
                var b0 = _blocks.Values[0];
                if (b0.Rtp + b0.Frames <= _cursor - Rate) _blocks.RemoveAt(0);
                else break;
            }

            float peak = 0;
            long pos = _cursor;
            int written = 0;
            int idx = FindBlock(pos);
            while (written < frames && idx < _blocks.Count)
            {
                var b = _blocks.Values[idx];
                if (b.Rtp > pos)
                {
                    int gap = (int)Math.Min(frames - written, b.Rtp - pos);
                    written += gap; pos += gap;
                    continue;
                }
                int offset = (int)(pos - b.Rtp);
                if (offset >= b.Frames) { idx++; continue; }
                int n = Math.Min(frames - written, b.Frames - offset);
                for (int i = 0; i < n * 2; i++)
                {
                    float v = b.Pcm[offset * 2 + i] * _gain;
                    output[written * 2 + i] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
                    peak = Math.Max(peak, Math.Abs(v));
                }
                if (n > 0) _lastAudioAt = Environment.TickCount64;
                written += n; pos += n;
                idx++;
            }
            _cursor += frames;
            Level = peak / 32768f;
        }
    }

    /// <summary>Index of the last block starting at or before pos (or 0).</summary>
    private int FindBlock(long pos)
    {
        var keys = _blocks.Keys;
        int lo = 0, hi = keys.Count - 1, ans = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (keys[mid] <= pos) { ans = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return ans;
    }
}
