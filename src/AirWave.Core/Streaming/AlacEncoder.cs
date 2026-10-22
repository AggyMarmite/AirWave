namespace AirWave.Core.Streaming;

/// <summary>
/// Packs 16-bit stereo PCM into an ALAC frame using ALAC's "escape" (uncompressed) mode.
/// Lossless and nearly free on the CPU; the bitrate (~1.4 Mbit/s) is what AirPlay speakers
/// already handle from Apple devices.
/// </summary>
public static class AlacEncoder
{
    /// <summary>Encodes interleaved stereo samples (frames = samples.Length / 2) into <paramref name="dst"/>; returns bytes written.</summary>
    public static int EncodeStereo16(ReadOnlySpan<short> samples, int frames, int framesPerPacket, Span<byte> dst)
    {
        var w = new BitWriter(dst);
        w.Write(1, 3);   // element: CPE (stereo pair)
        w.Write(0, 4);   // element instance tag
        w.Write(0, 12);  // unused
        bool partial = frames != framesPerPacket;
        w.Write(partial ? 1u : 0u, 1); // partial frame: sample count follows
        w.Write(0, 2);   // bytes shifted
        w.Write(1, 1);   // escape flag: uncompressed samples follow
        if (partial) w.Write((uint)frames, 32);
        for (int i = 0; i < frames * 2; i++) w.Write((ushort)samples[i], 16);
        w.Write(7, 3);   // ID_END
        return w.BytesUsed;
    }

    /// <summary>Worst-case size of one encoded packet.</summary>
    public static int MaxSize(int frames) => 8 + frames * 4 + 4;

    private ref struct BitWriter
    {
        private readonly Span<byte> _buf;
        private int _bit;

        public BitWriter(Span<byte> buf)
        {
            _buf = buf;
            _bit = 0;
            buf.Clear();
        }

        public void Write(uint value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                if (((value >> i) & 1) != 0) _buf[_bit >> 3] |= (byte)(0x80 >> (_bit & 7));
                _bit++;
            }
        }

        public int BytesUsed => (_bit + 7) >> 3;
    }
}
