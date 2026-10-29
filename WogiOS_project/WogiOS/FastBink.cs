using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace WogiOS;

// 게임(WarOfGenesis.Assets)의 Bink RDCT 소리 디코더를 그대로 옮겨 적은 것.
// 게임 DLL 은 실행 중에 불러오므로 인터프리터로 돌지만, 이 클래스는 앱 본체에 들어 있어
// 미리 컴파일(AOT)된다. 계산 순서와 방식은 원본과 같다(원본 IL 을 보고 옮김).
// 실패하면 null 을 돌려주고, 그러면 BinkHook 이 원본 디코더(DecodeSlow)로 되돌아간다.
public static class FastBink
{
    internal static readonly int[] CriticalFreqs =
    {
        100, 200, 300, 400, 510, 630, 770, 920, 1080, 1270, 1480, 1720, 2000,
        2320, 2700, 3150, 3700, 4400, 5300, 6400, 7700, 9500, 12000, 15500, 24500
    };

    internal static readonly int[] RleLengths = { 2, 3, 4, 5, 6, 8, 9, 10, 11, 12, 13, 14, 15, 16, 32, 64 };

    // BinkAudio.Decode() 의 몸통. 게임 쪽에서 필요한 값을 인자로 받는다.
    public static short[] Decode(byte[] data, int[] framePos, int sampleRate, int channels,
        int frameCount, int audioTracks, bool useDct)
    {
        if (useDct || data == null || framePos == null || channels < 1) return null;
        try
        {
            var sw = Stopwatch.StartNew();
            var dec = new Rdct(sampleRate, channels);
            var buf = new ShortBuf(1 << 20);
            long total = 0;
            for (int i = 0; i < frameCount; i++)
            {
                int start = framePos[i];
                int end = framePos[i + 1];
                if (start + 4 > end || audioTracks < 1) continue;
                int size = BitConverter.ToInt32(data, start);
                if (size < 4) continue;
                if (start + 4 + size > end) continue;
                total += (long)(BitConverter.ToUInt32(data, start + 4) / 2);
                dec.DecodePacket(data, start + 4, size, buf);
            }
            long n = Math.Min((long)buf.Count, total - total % channels);
            var result = new short[n];
            Array.Copy(buf.Arr, result, (int)n);
            Log("bink fast: " + n + " samples, " + sampleRate + "Hz x" + channels + ", " + sw.ElapsedMilliseconds + " ms");
            return result;
        }
        catch (Exception ex)
        {
            Log("bink fast FAILED, falling back: " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    static void Log(string s)
    {
        try { WogShim.Host.Log(s); } catch (Exception) { }
    }

    sealed class ShortBuf
    {
        public short[] Arr;
        public int Count;
        public ShortBuf(int cap) { Arr = new short[cap]; }
        public void Add(short v)
        {
            if (Count == Arr.Length) Array.Resize(ref Arr, Arr.Length * 2);
            Arr[Count++] = v;
        }
    }

    struct BitReader
    {
        readonly byte[] _d;
        readonly int _off;
        readonly int _len;
        long _pos;

        public BitReader(byte[] d, int off, int len) { _d = d; _off = off; _len = len; _pos = 0; }

        public long BitsLeft { get { return (long)_len * 8 - _pos; } }
        public void Skip(int n) { _pos += n; }
        public void AlignTo32() { _pos += (-_pos) & 31L; }

        public uint Read(int n)
        {
            if (n == 0) return 0;
            ulong v = 0;
            int byteIdx = (int)(_pos >> 3);
            int shift = (int)(_pos & 7);
            for (int i = 0; i < 5; i++)
            {
                int p = byteIdx + i;
                if (p < _len) v |= (ulong)_d[_off + p] << (8 * i);
            }
            _pos += n;
            return (uint)((v >> shift) & ((1UL << (n & 63)) - 1UL));
        }
    }

    sealed class Fft
    {
        sealed class Tables { public int[] Rev; public double[] Cos; public double[] Sin; }
        static readonly Dictionary<int, Tables> Cache = new Dictionary<int, Tables>();

        readonly int _n;
        readonly int[] _rev;
        readonly double[] _cos;
        readonly double[] _sin;
        readonly double[] _re;
        readonly double[] _im;

        Fft(int n, Tables t)
        {
            _n = n; _rev = t.Rev; _cos = t.Cos; _sin = t.Sin;
            _re = new double[n]; _im = new double[n];
        }

        public static Fft Get(int n)
        {
            Tables t;
            lock (Cache)
            {
                if (!Cache.TryGetValue(n, out t))
                {
                    t = Build(n);
                    Cache[n] = t;
                }
            }
            return new Fft(n, t);
        }

        static Tables Build(int n)
        {
            int bits = BitOperations.Log2((uint)n);
            var rev = new int[n];
            for (int i = 0; i < n; i++)
            {
                int r = 0;
                for (int j = 0; j < bits; j++)
                    r |= ((i >> (j & 31)) & 1) << ((bits - 1 - j) & 31);
                rev[i] = r;
            }
            var cos = new double[n / 2];
            var sin = new double[n / 2];
            for (int k = 0; k < n / 2; k++)
            {
                cos[k] = Math.Cos(6.283185307179586 * (double)k / (double)n);
                sin[k] = Math.Sin(6.283185307179586 * (double)k / (double)n);
            }
            return new Tables { Rev = rev, Cos = cos, Sin = sin };
        }

        public void InverseRdft(float[] c)
        {
            int n = _n;
            int half = n / 2;
            for (int i = 0; i < half; i++)
            {
                _re[i] = c[2 * i];
                _im[i] = c[2 * i + 1];
            }
            Array.Clear(_re, half, half);
            Array.Clear(_im, half, half);
            Transform();
            double s0 = (double)c[0] * 0.5;
            double s1 = (double)c[1] * 0.5;
            for (int i = 0; i < n; i++)
                c[i] = (float)((_re[i] - s0) + (((i & 1) != 0) ? -s1 : s1));
        }

        void Transform()
        {
            int n = _n;
            double[] re = _re, im = _im, cos = _cos, sin = _sin;
            int[] rev = _rev;
            for (int i = 0; i < n; i++)
            {
                int j = rev[i];
                if (j > i)
                {
                    double a = re[j]; double b = re[i];
                    re[i] = a; re[j] = b;
                    a = im[j]; b = im[i];
                    im[i] = a; im[j] = b;
                }
            }
            for (int size = 2; size <= n; size <<= 1)
            {
                int half = size >> 1;
                int step = n / size;
                for (int start = 0; start < n; start += size)
                {
                    for (int j = 0; j < half; j++)
                    {
                        double wr = cos[j * step];
                        double wi = -sin[j * step];
                        int a = start + j;
                        int b = a + half;
                        double tr = re[b] * wr - im[b] * wi;
                        double ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                    }
                }
            }
        }
    }

    sealed class Rdct
    {
        readonly int _frameLen;
        readonly int _overlapLen;
        readonly int _numBands;
        readonly int[] _bands = new int[26];
        readonly float[] _quantTable = new float[96];
        readonly float _root;
        readonly float[] _previous;
        readonly float[] _coeffs;
        readonly float[] _quant = new float[25];
        readonly Fft _fft;
        bool _first = true;

        public Rdct(int sampleRate, int channels)
        {
            int bits = sampleRate >= 22050 ? (sampleRate >= 44100 ? 11 : 10) : 9;
            int rate = sampleRate * channels;
            bits += BitOperations.Log2((uint)channels);
            _frameLen = 1 << (bits & 31);
            _overlapLen = _frameLen / 16;
            int half = (rate + 1) / 2;
            _root = (float)(2.0 / (Math.Sqrt((double)_frameLen) * 32768.0));
            for (int i = 0; i < 96; i++)
                _quantTable[i] = MathF.Exp((float)i * 0.15289165f) * _root;

            int nb = 1;
            while (nb < 25)
            {
                if (half <= CriticalFreqs[nb - 1]) break;
                nb++;
            }
            _numBands = nb;
            _bands[0] = 2;
            for (int i = 1; i < nb; i++)
                _bands[i] = (CriticalFreqs[i - 1] * _frameLen / half) & -2;
            _bands[nb] = _frameLen;

            _previous = new float[_overlapLen];
            _coeffs = new float[_frameLen];
            _fft = Fft.Get(_frameLen);
        }

        public void DecodePacket(byte[] data, int off, int len, ShortBuf output)
        {
            var r = new BitReader(data, off, len);
            r.Skip(32);
            while (r.BitsLeft >= (long)(58 + _numBands * 8))
            {
                if (!DecodeBlock(ref r)) return;
                int n = _frameLen - _overlapLen;
                float[] c = _coeffs;
                for (int i = 0; i < n; i++)
                    output.Add(ToShort(c[i] * 32768f));
                r.AlignTo32();
            }
        }

        static short ToShort(float v)
        {
            float x = MathF.Round(v);
            if (x < -32768f) x = -32768f;
            else if (x > 32767f) x = 32767f;
            return (short)(int)x;
        }

        static float ReadFloat(ref BitReader r)
        {
            int e = (int)r.Read(5);
            float v = MathF.ScaleB((float)r.Read(23), e - 23);
            return r.Read(1) == 0 ? v : -v;
        }

        bool DecodeBlock(ref BitReader r)
        {
            float[] c = _coeffs;
            c[0] = ReadFloat(ref r) * _root;
            c[1] = ReadFloat(ref r) * _root;
            for (int b = 0; b < _numBands; b++)
                _quant[b] = _quantTable[Math.Min((int)r.Read(8), 95)];

            int k = 0;
            int i = 2;
            float q = _quant[0];
            while (i < _frameLen)
            {
                if (r.BitsLeft < 5) return false;
                int j = r.Read(1) == 0 ? i + 8 : i + RleLengths[(int)r.Read(4)] * 8;
                j = Math.Min(j, _frameLen);
                int width = (int)r.Read(4);
                if (width == 0)
                {
                    Array.Clear(c, i, j - i);
                    i = j;
                    while (_bands[k] < i) q = _quant[k++];
                }
                else
                {
                    while (i < j)
                    {
                        if (_bands[k] == i) q = _quant[k++];
                        uint v = r.Read(width);
                        if (v != 0)
                            c[i] = r.Read(1) == 0 ? q * (float)v : -q * (float)v;
                        else
                            c[i] = 0f;
                        i++;
                    }
                }
            }

            _fft.InverseRdft(c);
            int ov = _overlapLen;
            if (!_first)
            {
                for (int n = 0; n < ov; n++)
                    c[n] = (_previous[n] * (float)(ov - n) + c[n] * (float)n) / (float)ov;
            }
            Array.Copy(c, _frameLen - ov, _previous, 0, ov);
            _first = false;
            return true;
        }
    }
}
