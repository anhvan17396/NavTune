namespace NavTune.Audio;

/// <summary>
/// FFT of the most recent output, grouped into log-spaced bands from 20 Hz to 20 kHz.
/// All buffers and band ranges are allocated once and reused every frame.
/// </summary>
public sealed class SpectrumAnalyzer
{
    public const int Size = 2048;
    private const int Log2Size = 11;

    private readonly float[] window = new float[Size];
    private readonly float[] re = new float[Size];
    private readonly float[] im = new float[Size];
    private readonly float[] cosTable = new float[Size / 2];
    private readonly float[] sinTable = new float[Size / 2];
    private readonly int[] bitReverse = new int[Size];
    private readonly float[] levels;
    private (int First, int Count)[] bins = [];
    private double binsSampleRate;

    /// <summary>
    /// Converts FFT power to dBFS. Matches the macOS analyzer (normalized Hann window, vDSP scaling), where a
    /// full-scale sine reads about +4.3 dB, so both apps draw the same picture.
    /// </summary>
    private static readonly float OffsetDB = (float)(20 * Math.Log10(Size / 4.0) - 20 * Math.Log10(2 * Math.Sqrt(2.0 / 3.0)));

    public int BandCount { get; }

    public SpectrumAnalyzer(int bandCount)
    {
        BandCount = bandCount;
        levels = new float[bandCount];
        for (int i = 0; i < Size; i++)
        {
            window[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / Size)));
            int r = 0;
            for (int b = 0; b < Log2Size; b++) r |= ((i >> b) & 1) << (Log2Size - 1 - b);
            bitReverse[i] = r;
        }
        for (int i = 0; i < Size / 2; i++)
        {
            cosTable[i] = (float)Math.Cos(2 * Math.PI * i / Size);
            sinTable[i] = (float)-Math.Sin(2 * Math.PI * i / Size);
        }
    }

    /// <summary>Returns band levels normalized to 0...1 (-80...0 dBFS). The array is reused by the next call.</summary>
    public float[] Analyze(ReadOnlySpan<float> samples, double sampleRate)
    {
        if (sampleRate != binsSampleRate) ComputeBins(sampleRate);
        for (int i = 0; i < Size; i++)
        {
            int j = bitReverse[i];
            re[j] = i < samples.Length ? samples[i] * window[i] : 0;
            im[j] = 0;
        }
        Fft();
        for (int band = 0; band < bins.Length; band++)
        {
            var (first, count) = bins[band];
            float peak = 0;
            for (int k = first; k < first + count; k++)
            {
                float p = re[k] * re[k] + im[k] * im[k];
                if (p > peak) peak = p;
            }
            float db = 10 * MathF.Log10(peak + 1e-18f) - OffsetDB;
            levels[band] = Math.Clamp((db + 80) / 80, 0, 1);
        }
        return levels;
    }

    /// <summary>In-place iterative radix-2 FFT on bit-reversed input.</summary>
    private void Fft()
    {
        for (int size = 2; size <= Size; size <<= 1)
        {
            int half = size >> 1;
            int step = Size / size;
            for (int start = 0; start < Size; start += size)
            {
                for (int k = 0; k < half; k++)
                {
                    float wr = cosTable[k * step], wi = sinTable[k * step];
                    int a = start + k, b = a + half;
                    float tr = wr * re[b] - wi * im[b];
                    float ti = wr * im[b] + wi * re[b];
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }

    private void ComputeBins(double sampleRate)
    {
        binsSampleRate = sampleRate;
        int half = Size / 2;
        double binWidth = sampleRate / Size;
        bins = new (int, int)[BandCount];
        for (int band = 0; band < BandCount; band++)
        {
            double lo = 20 * Math.Pow(1000, (double)band / BandCount);
            double hi = 20 * Math.Pow(1000, (double)(band + 1) / BandCount);
            int first = Math.Clamp((int)(lo / binWidth), 1, half - 1);
            int last = Math.Clamp((int)(hi / binWidth), first, half - 1);
            bins[band] = (first, last - first + 1);
        }
    }
}

/// <summary>Spectrum for the graphs, computed on demand when a visible graph asks for a frame (nothing runs otherwise).</summary>
public sealed class SpectrumModel
{
    public const int BandCount = 64;

    private readonly SpectrumAnalyzer analyzer = new(BandCount);
    private readonly float[] samples = new float[SpectrumAnalyzer.Size];
    private readonly float[] bands = new float[BandCount];
    private long lastUpdate = long.MinValue;

    /// <summary>Several graphs may draw the same frame (window + tray flyout); the FFT runs once per frame.</summary>
    public float[] Bands(long timestamp, AudioProcessor? processor, double sampleRate)
    {
        double elapsed = lastUpdate == long.MinValue ? 1 : (timestamp - lastUpdate) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (elapsed <= 0.012 && elapsed >= 0) return bands;
        lastUpdate = timestamp;

        if (processor is null || sampleRate <= 0)
        {
            Array.Clear(bands);
            return bands;
        }
        processor.CopyRecentSamples(samples);
        var fresh = analyzer.Analyze(samples, sampleRate);
        float fall = (float)Math.Clamp(elapsed, 0, 0.1) * 0.75f;
        for (int i = 0; i < BandCount; i++) bands[i] = Math.Max(fresh[i], bands[i] - fall);
        return bands;
    }

    public void Clear() => Array.Clear(bands);
}
