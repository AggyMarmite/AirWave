using System.Numerics;

namespace AirWave.Core.Audio;

/// <summary>
/// Apple Lossless (ALAC) decoder for the stereo streams AirPlay senders use.
/// C# port of David Hammerton's ALAC decoder (MIT licence) as used by Shairport Sync.
/// Output is interleaved stereo 16-bit (24-bit sources are reduced to 16 bits).
/// </summary>
public sealed class AlacDecoder
{
    private readonly int _maxFrames;
    private readonly int _sampleSize;
    private readonly int _riceHistoryMult, _riceInitialHistory, _riceKModifier;

    private readonly int[] _errA, _errB, _outA, _outB, _uncA, _uncB;
    private readonly short[] _coefA = new short[32], _coefB = new short[32];

    private byte[] _in;
    private int _pos, _bitAcc;

    /// <param name="framesPerPacket">Samples per channel per packet (352 for AirPlay).</param>
    /// <param name="sampleSize">16 or 24.</param>
    public AlacDecoder(int framesPerPacket = 352, int sampleSize = 16, int riceHistoryMult = 40, int riceInitialHistory = 10, int riceKModifier = 14)
    {
        _maxFrames = framesPerPacket;
        _sampleSize = sampleSize;
        _riceHistoryMult = riceHistoryMult;
        _riceInitialHistory = riceInitialHistory;
        _riceKModifier = riceKModifier;
        int n = Math.Max(framesPerPacket, 4096);
        _errA = new int[n]; _errB = new int[n]; _outA = new int[n]; _outB = new int[n]; _uncA = new int[n]; _uncB = new int[n];
    }

    /// <summary>Decodes one packet into <paramref name="output"/> (interleaved stereo). Returns frames decoded.</summary>
    public int Decode(ReadOnlySpan<byte> packet, short[] output)
    {
        // Pad so the bit reader can safely look a few bytes ahead
        if (_in == null || _in.Length < packet.Length + 8) _in = new byte[packet.Length + 64];
        packet.CopyTo(_in);
        Array.Clear(_in, packet.Length, Math.Min(8, _in.Length - packet.Length));
        _pos = 0; _bitAcc = 0;

        int channels = (int)ReadBits(3);
        int frames = _maxFrames;
        if (channels > 1) return 0;

        ReadBits(4);
        ReadBits(12);
        int hasSize = (int)ReadBits(1);
        int uncompressedBytes = (int)ReadBits(2);
        int notCompressed = (int)ReadBits(1);
        if (hasSize != 0) frames = (int)ReadBits(32);
        if (frames <= 0 || frames > _errA.Length || output.Length < frames * 2) return 0;

        if (channels == 0) // mono: duplicate into both channels
        {
            int readSize = _sampleSize - uncompressedBytes * 8;
            if (notCompressed == 0)
            {
                ReadBits(8); ReadBits(8);
                int predType = (int)ReadBits(4), predQuant = (int)ReadBits(4);
                int riceMod = (int)ReadBits(3), coefNum = (int)ReadBits(5);
                for (int i = 0; i < coefNum; i++) _coefA[i] = (short)ReadBits(16);
                if (uncompressedBytes != 0)
                    for (int i = 0; i < frames; i++) _uncA[i] = (int)ReadBits(uncompressedBytes * 8);
                RiceDecode(_errA, frames, readSize, riceMod * _riceHistoryMult / 4, (1 << _riceKModifier) - 1);
                if (predType == 0) PredictorFir(_errA, _outA, frames, readSize, _coefA, coefNum, predQuant);
            }
            else
            {
                ReadUncompressed(_outA, null, frames);
                uncompressedBytes = 0;
            }
            for (int i = 0; i < frames; i++)
            {
                short s = To16(Combine(_outA[i], _uncA[i], uncompressedBytes));
                output[2 * i] = s; output[2 * i + 1] = s;
            }
            return frames;
        }

        // stereo
        {
            int readSize = _sampleSize - uncompressedBytes * 8 + 1;
            int shift = 0, leftWeight = 0;
            if (notCompressed == 0)
            {
                shift = (int)ReadBits(8);
                leftWeight = (int)ReadBits(8);
                int predTypeA = (int)ReadBits(4), predQuantA = (int)ReadBits(4);
                int riceModA = (int)ReadBits(3), coefNumA = (int)ReadBits(5);
                for (int i = 0; i < coefNumA; i++) _coefA[i] = (short)ReadBits(16);
                int predTypeB = (int)ReadBits(4), predQuantB = (int)ReadBits(4);
                int riceModB = (int)ReadBits(3), coefNumB = (int)ReadBits(5);
                for (int i = 0; i < coefNumB; i++) _coefB[i] = (short)ReadBits(16);
                if (uncompressedBytes != 0)
                    for (int i = 0; i < frames; i++)
                    {
                        _uncA[i] = (int)ReadBits(uncompressedBytes * 8);
                        _uncB[i] = (int)ReadBits(uncompressedBytes * 8);
                    }
                RiceDecode(_errA, frames, readSize, riceModA * _riceHistoryMult / 4, (1 << _riceKModifier) - 1);
                if (predTypeA == 0) PredictorFir(_errA, _outA, frames, readSize, _coefA, coefNumA, predQuantA);
                RiceDecode(_errB, frames, readSize, riceModB * _riceHistoryMult / 4, (1 << _riceKModifier) - 1);
                if (predTypeB == 0) PredictorFir(_errB, _outB, frames, readSize, _coefB, coefNumB, predQuantB);
            }
            else
            {
                ReadUncompressed(_outA, _outB, frames);
                uncompressedBytes = 0;
            }

            for (int i = 0; i < frames; i++)
            {
                int left, right;
                if (leftWeight != 0)
                {
                    int mid = _outA[i], diff = _outB[i];
                    right = mid - ((diff * leftWeight) >> shift);
                    left = right + diff;
                }
                else { left = _outA[i]; right = _outB[i]; }
                output[2 * i] = To16(Combine(left, _uncA[i], uncompressedBytes));
                output[2 * i + 1] = To16(Combine(right, _uncB[i], uncompressedBytes));
            }
            return frames;
        }
    }

    private void ReadUncompressed(int[] a, int[] b, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            a[i] = ReadSample();
            if (b != null) b[i] = ReadSample();
        }
    }

    private int ReadSample()
    {
        if (_sampleSize <= 16)
            return SignExtend((int)ReadBits(_sampleSize), _sampleSize);
        int v = (int)ReadBits(16) << (_sampleSize - 16);
        v |= (int)ReadBits(_sampleSize - 16);
        return SignExtend(v, _sampleSize);
    }

    private static int Combine(int sample, int unc, int uncBytes)
    {
        if (uncBytes == 0) return sample;
        int mask = (int)~(0xFFFFFFFFu << (uncBytes * 8));
        return (sample << (uncBytes * 8)) | (unc & mask);
    }

    private short To16(int v) => _sampleSize > 16 ? (short)(v >> (_sampleSize - 16)) : (short)v;

    // ---------------------------------------------------------------- bit reading

    private uint ReadBits16(int bits)
    {
        uint r = (uint)((_in[_pos] << 16) | (_in[_pos + 1] << 8) | _in[_pos + 2]);
        r <<= _bitAcc;
        r &= 0x00ffffff;
        r >>= 24 - bits;
        int acc = _bitAcc + bits;
        _pos += acc >> 3;
        _bitAcc = acc & 7;
        if (_pos > _in.Length - 3) _pos = _in.Length - 3; // corrupt packet: stop advancing
        return r;
    }

    private uint ReadBits(int bits)
    {
        if (bits == 0) return 0;
        uint r = 0;
        if (bits > 16)
        {
            bits -= 16;
            r = ReadBits16(16) << bits;
        }
        return r | ReadBits16(bits);
    }

    private int ReadBit()
    {
        int r = (_in[_pos] << _bitAcc) >> 7 & 1;
        int acc = _bitAcc + 1;
        _pos += acc / 8;
        _bitAcc = acc % 8;
        if (_pos > _in.Length - 3) _pos = _in.Length - 3;
        return r;
    }

    private void UnreadBits(int bits)
    {
        int acc = _bitAcc - bits;
        _pos += acc >> 3;
        _bitAcc = acc & 7;
    }

    // ---------------------------------------------------------------- entropy + prediction

    private const int RiceThreshold = 8;

    private static int Clz(int x) => BitOperations.LeadingZeroCount((uint)x);

    private int EntropyDecodeValue(int readSampleSize, int k, int kModMask)
    {
        int x = 0;
        while (x <= RiceThreshold && ReadBit() != 0) x++;
        if (x > RiceThreshold)
        {
            int value = (int)ReadBits(readSampleSize);
            value &= (int)(0xffffffffu >> (32 - readSampleSize));
            return value;
        }
        if (k != 1)
        {
            int extra = (int)ReadBits(k);
            x *= ((1 << k) - 1) & kModMask;
            if (extra > 1) x += extra - 1;
            else UnreadBits(1);
        }
        return x;
    }

    private void RiceDecode(int[] output, int size, int readSampleSize, int historyMult, int kModMask)
    {
        int history = _riceInitialHistory;
        int signModifier = 0;
        for (int n = 0; n < size; n++)
        {
            int k = 31 - _riceKModifier - Clz((history >> 9) + 3);
            k = k < 0 ? k + _riceKModifier : _riceKModifier;

            int decoded = EntropyDecodeValue(readSampleSize, k, unchecked((int)0xFFFFFFFF)) + signModifier;
            int final = (decoded + 1) / 2;
            if ((decoded & 1) != 0) final = -final;
            output[n] = final;
            signModifier = 0;

            history += decoded * historyMult - ((history * historyMult) >> 9);
            if (decoded > 0xFFFF) history = 0xFFFF;

            if (history < 128 && n + 1 < size)
            {
                signModifier = 1;
                k = Clz(history) + ((history + 16) / 64) - 24;
                int block = EntropyDecodeValue(16, k, kModMask);
                if (block > 0)
                {
                    block = Math.Min(block, size - n - 1);
                    Array.Clear(output, n + 1, block);
                    n += block;
                }
                if (block > 0xFFFF) signModifier = 0;
                history = 0;
            }
        }
    }

    private static int SignExtend(int v, int bits) => (v << (32 - bits)) >> (32 - bits);

    private static void PredictorFir(int[] err, int[] outBuf, int size, int readSampleSize, short[] coef, int coefNum, int quant)
    {
        outBuf[0] = err[0];
        if (coefNum == 0)
        {
            if (size > 1) Array.Copy(err, 1, outBuf, 1, size - 1);
            return;
        }
        if (coefNum == 0x1f)
        {
            for (int i = 0; i < size - 1; i++)
                outBuf[i + 1] = SignExtend(outBuf[i] + err[i + 1], readSampleSize);
            return;
        }
        for (int i = 0; i < coefNum && i + 1 < size; i++)
            outBuf[i + 1] = SignExtend(outBuf[i] + err[i + 1], readSampleSize);

        int b = 0; // "buffer_out" pointer offset
        for (int i = coefNum + 1; i < size; i++)
        {
            int sum = 0;
            int errVal = err[i];
            for (int j = 0; j < coefNum; j++)
                sum += (outBuf[b + coefNum - j] - outBuf[b]) * coef[j];
            int outVal = (1 << (quant - 1)) + sum;
            outVal >>= quant;
            outVal = outVal + outBuf[b] + errVal;
            outVal = SignExtend(outVal, readSampleSize);
            outBuf[b + coefNum + 1] = outVal;

            if (errVal > 0)
            {
                int p = coefNum - 1;
                while (p >= 0 && errVal > 0)
                {
                    int val = outBuf[b] - outBuf[b + coefNum - p];
                    int sign = Math.Sign(val);
                    coef[p] -= (short)sign;
                    val *= sign;
                    errVal -= (val >> quant) * (coefNum - p);
                    p--;
                }
            }
            else if (errVal < 0)
            {
                int p = coefNum - 1;
                while (p >= 0 && errVal < 0)
                {
                    int val = outBuf[b] - outBuf[b + coefNum - p];
                    int sign = -Math.Sign(val);
                    coef[p] -= (short)sign;
                    val *= sign;
                    errVal -= (val >> quant) * (coefNum - p);
                    p--;
                }
            }
            b++;
        }
    }
}
