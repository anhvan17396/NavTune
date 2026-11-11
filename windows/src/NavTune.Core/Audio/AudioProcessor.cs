using NavTune.Model;

namespace NavTune.Audio;

/// <summary>
/// Real-time DSP chain, ported from the macOS app:
/// input gain -> EQ (10 graphic or up to 10 parametric bands) -> bass shelf -> clarity shelf
/// -> stereo width -> loudness leveler -> boost -> limiter.
///
/// The audio thread never allocates, never blocks and never takes a lock: new settings arrive as an immutable
/// snapshot published with a volatile write. Audio is processed block-wise in planar buffers, and filters left
/// at 0 dB are skipped entirely.
/// </summary>
public sealed unsafe class AudioProcessor
{
    public static readonly double[] Frequencies = [32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    public const double BandQ = 1.4;
    public const double BassFrequency = 90;
    public const double ClarityFrequency = 9000;
    /// <summary>Graphic or parametric EQ slots, then bass and clarity shelves.</summary>
    public static readonly int EqSlots = Math.Max(Frequencies.Length, SoundSettings.MaxParametricBands);
    public static readonly int FilterCount = EqSlots + 2;
    /// <summary>~1.4 s at 48 kHz: enough for the spectrum.</summary>
    public const int SpectrumCapacity = 65536;

    public const int MaxChannels = 8;
    private const int MaxFrames = 16384;
    /// <summary>The leveler's gain computer runs once per this many frames and is interpolated in between.</summary>
    private const int ControlInterval = 16;
    private const float Ceiling = 0.95f;

    public static Biquad[] Filters(SoundSettings settings, double sampleRate)
    {
        var filters = new List<Biquad>(FilterCount);
        if (settings.Mode == EQMode.Graphic)
        {
            for (int i = 0; i < Frequencies.Length; i++)
                filters.Add(Biquad.Peaking(Frequencies[i], settings.Gains[i], BandQ, sampleRate));
        }
        else
        {
            foreach (var band in settings.Bands.Take(EqSlots)) filters.Add(band.ToBiquad(sampleRate));
        }
        while (filters.Count < EqSlots) filters.Add(Biquad.Identity);
        filters.Add(Biquad.LowShelf(BassFrequency, settings.Bass, sampleRate));
        filters.Add(Biquad.HighShelf(ClarityFrequency, settings.Clarity, sampleRate));
        return [.. filters];
    }

    private sealed class Params
    {
        public required Biquad[] Filters;
        public float InputGain = 1, OutputGain = 1, Width = 1, Loudness;
        public bool Bypass;
    }

    private Params pending = new() { Filters = Identities(), Bypass = false };
    private Params? applied;
    private SoundSettings? lastSettings;
    private bool lastBypass;
    private readonly object updateLock = new();

    // Audio-thread state, preallocated (pinned) so rendering never allocates.
    private readonly Biquad[] coeffs = Identities();
    private readonly int[] activeFilters = new int[FilterCount];
    private int activeCount;
    /// <summary>Filter state laid out as [channel][filter][z1, z2].</summary>
    private readonly double[] state = GC.AllocateArray<double>(MaxChannels * FilterCount * 2, pinned: true);
    /// <summary>Planar samples: channel c starts at c * MaxFrames.</summary>
    private readonly float[] scratch = GC.AllocateArray<float>(MaxChannels * MaxFrames, pinned: true);
    private readonly float[] ring = GC.AllocateArray<float>(SpectrumCapacity, pinned: true);
    private long ringWrite;
    /// <summary>The unprocessed input (mono), for content detection: what plays, before NavTune's own EQ colors it.</summary>
    private readonly float[] inputRing = GC.AllocateArray<float>(SpectrumCapacity, pinned: true);
    private long inputWrite;
    private long callbacks;

    private Params target = new() { Filters = Identities() };
    private bool wasBypassed = true;
    private float inputGain = 1, outputGain = 1, width = 1;
    private float envelope, levelerGain = 1, levelerStep, limiterGain = 1;
    private float inputLevel, outputLevel;
    private float publishedInputLevel, publishedOutputLevel;

    // Constants derived from the sample rate and block size.
    private float attack, release, limiterRelease;
    private float smoothingFrames = 960, levelFrames = 14400;
    private int blockFrames;
    private float blockSmoothing, blockLevelDecay;

    public double SampleRate { get; private set; } = 48000;

    private static Biquad[] Identities() => Enumerable.Repeat(Biquad.Identity, FilterCount).ToArray();

    /// <summary>Call only while no audio thread is running this processor.</summary>
    public void Prepare(double sampleRate)
    {
        SampleRate = sampleRate;
        static float Coefficient(double seconds, double rate) => (float)(1 - Math.Exp(-1 / (seconds * rate)));
        attack = Coefficient(0.005, sampleRate);
        release = Coefficient(0.3, sampleRate);
        limiterRelease = Coefficient(0.15, sampleRate);
        smoothingFrames = (float)(0.02 * sampleRate);
        levelFrames = (float)(0.3 * sampleRate);
        blockFrames = 0;
        wasBypassed = true;
        inputLevel = outputLevel = 0;
        Volatile.Write(ref publishedInputLevel, 0);
        Volatile.Write(ref publishedOutputLevel, 0);
        activeCount = 0;
        Array.Fill(coeffs, Biquad.Identity);
        applied = null;
        // Coefficients depend on the sample rate: rebuild them from the last settings.
        lock (updateLock)
        {
            if (lastSettings is not null) Publish(lastSettings, lastBypass);
        }
    }

    /// <summary>Hands new settings to the audio thread. Safe from any thread.</summary>
    public void Update(SoundSettings settings, bool bypass)
    {
        lock (updateLock)
        {
            lastSettings = settings.Clone();
            lastBypass = bypass;
            Publish(lastSettings, bypass);
        }
    }

    private void Publish(SoundSettings settings, bool bypass)
    {
        var next = new Params
        {
            Filters = Filters(settings, SampleRate),
            InputGain = MathF.Pow(10, settings.Preamp / 20),
            OutputGain = MathF.Pow(10, settings.Boost / 20),
            Width = settings.Width,
            Loudness = settings.Loudness,
            Bypass = bypass,
        };
        Volatile.Write(ref pending, next);
    }

    public long CallbackCount => Interlocked.Read(ref callbacks);

    /// <summary>Smoothed peak level (0...1) of the incoming audio. Non-destructive, safe from any thread.</summary>
    public float CurrentInputLevel => Volatile.Read(ref publishedInputLevel);

    /// <summary>Smoothed peak level (0...1) of the processed audio.</summary>
    public float CurrentOutputLevel => Volatile.Read(ref publishedOutputLevel);

    public long SampleWritePosition => Volatile.Read(ref ringWrite);

    public long InputWritePosition => Volatile.Read(ref inputWrite);

    /// <summary>
    /// Copies the unprocessed mono input written since <paramref name="position"/> (at most the buffer's length)
    /// and moves <paramref name="position"/> past it. A reader that fell more than the ring behind skips ahead.
    /// </summary>
    public int ReadInput(ref long position, Span<float> buffer)
    {
        long end = Volatile.Read(ref inputWrite);
        long start = Math.Clamp(position, end - SpectrumCapacity, end);
        int count = (int)Math.Min(end - start, buffer.Length);
        const int mask = SpectrumCapacity - 1;
        for (int i = 0; i < count; i++) buffer[i] = inputRing[(int)((start + i) & mask)];
        position = start + count;
        return count;
    }

    /// <summary>Forgets the meters, e.g. when the engine goes to standby.</summary>
    public void ResetLevels()
    {
        inputLevel = outputLevel = 0;
        Volatile.Write(ref publishedInputLevel, 0);
        Volatile.Write(ref publishedOutputLevel, 0);
    }

    /// <summary>Copies the most recent processed mono samples, e.g. for the spectrum analyzer.</summary>
    public void CopyRecentSamples(Span<float> buffer)
    {
        int count = Math.Min(buffer.Length, SpectrumCapacity);
        long end = Volatile.Read(ref ringWrite);
        const int mask = SpectrumCapacity - 1;
        for (int i = 0; i < count; i++) buffer[i] = ring[(int)((end - count + i) & mask)];
    }

    // MARK: - Audio thread

    private void PullParams()
    {
        var next = Volatile.Read(ref pending);
        if (ReferenceEquals(next, applied)) return;
        activeCount = 0;
        for (int i = 0; i < FilterCount; i++)
        {
            var k = next.Filters[i];
            if (k.IsIdentity)
            {
                // A bypassed filter must start from silence when it comes back.
                if (!coeffs[i].IsIdentity) ClearState(i);
            }
            else
            {
                activeFilters[activeCount++] = i;
            }
            coeffs[i] = k;
        }
        target = next;
        applied = next;
    }

    /// <summary>
    /// Processes interleaved float frames. <paramref name="input"/> may be null for silence and may equal
    /// <paramref name="output"/>. Returns the peak of the unprocessed input.
    /// </summary>
    public float Process(float* input, float* output, int frames, int channels)
    {
        if (frames <= 0 || channels <= 0) return 0;
        float peakIn = 0;
        int done = 0;
        while (done < frames)
        {
            int n = Math.Min(frames - done, MaxFrames);
            int offset = done * channels;
            peakIn = Math.Max(peakIn, ProcessBlock(input is null ? null : input + offset, output + offset, n, channels));
            done += n;
        }
        return peakIn;
    }

    private float ProcessBlock(float* input, float* output, int frames, int stride)
    {
        Interlocked.Increment(ref callbacks);
        PullParams();
        int channels = Math.Min(stride, MaxChannels);
        if (frames != blockFrames) UpdateBlockConstants(frames);

        fixed (float* s = scratch)
        {
            for (int c = 0; c < channels; c++)
            {
                float* dst = s + c * MaxFrames;
                if (input is null)
                {
                    new Span<float>(dst, frames).Clear();
                }
                else
                {
                    for (int f = 0; f < frames; f++) dst[f] = input[f * stride + c];
                }
            }
            float inPeak = Peak(s, frames, channels);
            WriteRing(inputRing, ref inputWrite, s, frames, channels);

            if (target.Bypass)
            {
                wasBypassed = true;
            }
            else
            {
                if (wasBypassed)
                {
                    Array.Clear(state);
                    envelope = 0;
                    levelerGain = 1;
                    levelerStep = 0;
                    limiterGain = 1;
                    wasBypassed = false;
                }
                ApplyInputGain(s, frames, channels);
                for (int i = 0; i < activeCount; i++) RunFilter(s, activeFilters[i], frames, channels);
                if (channels == 2) ApplyWidth(s, frames);
                ApplyDynamics(s, frames, channels);
            }

            inputLevel = Math.Max(inPeak, inputLevel * blockLevelDecay);
            outputLevel = Math.Max(Peak(s, frames, channels), outputLevel * blockLevelDecay);
            Volatile.Write(ref publishedInputLevel, inputLevel);
            Volatile.Write(ref publishedOutputLevel, outputLevel);

            WriteRing(ring, ref ringWrite, s, frames, channels);

            for (int c = 0; c < stride; c++)
            {
                if (c < channels)
                {
                    float* src = s + c * MaxFrames;
                    for (int f = 0; f < frames; f++) output[f * stride + c] = src[f];
                }
                else
                {
                    for (int f = 0; f < frames; f++) output[f * stride + c] = 0;
                }
            }
            return inPeak;
        }
    }

    private void UpdateBlockConstants(int frames)
    {
        blockFrames = frames;
        blockSmoothing = 1 - MathF.Exp(-frames / smoothingFrames);
        blockLevelDecay = MathF.Exp(-frames / levelFrames);
    }

    /// <summary>One-pole smoothing evaluated per block; returns the ramp to apply across this block.</summary>
    private (float Start, float Step) Ramp(ref float value, float goal, int frames)
    {
        float start = value;
        value = MathF.Abs(goal - start) < 1e-6f ? goal : start + (goal - start) * blockSmoothing;
        return (start, (value - start) / frames);
    }

    private static float Peak(float* s, int frames, int channels)
    {
        float result = 0;
        for (int c = 0; c < channels; c++)
        {
            float* x = s + c * MaxFrames;
            for (int f = 0; f < frames; f++)
            {
                float a = MathF.Abs(x[f]);
                if (a > result) result = a;
            }
        }
        return result;
    }

    private void ApplyInputGain(float* s, int frames, int channels)
    {
        var (start, step) = Ramp(ref inputGain, target.InputGain, frames);
        if (start == 1 && step == 0) return;
        for (int c = 0; c < channels; c++)
        {
            float* x = s + c * MaxFrames;
            float g = start;
            for (int f = 0; f < frames; f++)
            {
                x[f] *= g;
                g += step;
            }
        }
    }

    /// <summary>Runs one biquad (transposed direct form II) over every channel with its state held in registers.</summary>
    private void RunFilter(float* s, int index, int frames, int channels)
    {
        var k = coeffs[index];
        double b0 = k.B0, b1 = k.B1, b2 = k.B2, a1 = k.A1, a2 = k.A2;
        fixed (double* st = state)
        {
            for (int c = 0; c < channels; c++)
            {
                float* x = s + c * MaxFrames;
                double* z = st + (c * FilterCount + index) * 2;
                double z1 = z[0], z2 = z[1];
                for (int f = 0; f < frames; f++)
                {
                    double input = x[f];
                    double y = b0 * input + z1;
                    z1 = b1 * input - a1 * y + z2;
                    z2 = b2 * input - a2 * y;
                    x[f] = (float)y;
                }
                // Flush denormals so silence never slows the filter down.
                z[0] = Math.Abs(z1) < 1e-25 ? 0 : z1;
                z[1] = Math.Abs(z2) < 1e-25 ? 0 : z2;
            }
        }
    }

    private void ApplyWidth(float* s, int frames)
    {
        var (start, step) = Ramp(ref width, target.Width, frames);
        if (start == 1 && step == 0) return;
        float* left = s, right = s + MaxFrames;
        float w = start;
        for (int f = 0; f < frames; f++)
        {
            float mid = (left[f] + right[f]) * 0.5f;
            float side = (left[f] - right[f]) * 0.5f * w;
            left[f] = mid + side;
            right[f] = mid - side;
            w += step;
        }
    }

    /// <summary>Loudness leveler, output boost and peak limiter, linked across channels to keep the stereo image.</summary>
    private void ApplyDynamics(float* s, int frames, int channels)
    {
        var (gain, step) = Ramp(ref outputGain, target.OutputGain, frames);
        float loudness = target.Loudness;
        bool leveling = loudness > 0.01f;
        float threshold = -10 - 20 * loudness;
        float slope = 1 - 1 / (1 + 3 * loudness);
        float makeup = -threshold * slope * 0.5f;

        for (int f = 0; f < frames; f++)
        {
            float peak = 0;
            for (int c = 0; c < channels; c++)
            {
                float a = MathF.Abs(s[c * MaxFrames + f]);
                if (a > peak) peak = a;
            }

            if (leveling)
            {
                envelope += (peak - envelope) * (peak > envelope ? attack : release);
                if (f % ControlInterval == 0)
                {
                    float level = 20 * MathF.Log10(envelope + 1e-9f);
                    float goal = MathF.Pow(10, (makeup - Math.Max(0, level - threshold) * slope) / 20);
                    levelerStep = (goal - levelerGain) / ControlInterval;
                }
                levelerGain += levelerStep;
            }
            else if (levelerGain != 1)
            {
                levelerGain = MathF.Abs(levelerGain - 1) < 1e-5f ? 1 : levelerGain + (1 - levelerGain) * limiterRelease;
            }

            float total = gain * levelerGain;
            float output = peak * total;
            float limit = output > Ceiling ? Ceiling / output : 1;
            if (limit < limiterGain)
                limiterGain = limit;
            else
                limiterGain += (limit - limiterGain) * limiterRelease;
            float appliedGain = total * limiterGain;
            for (int c = 0; c < channels; c++)
            {
                ref float x = ref s[c * MaxFrames + f];
                x = Math.Clamp(x * appliedGain, -1f, 1f);
            }
            gain += step;
        }
    }

    /// <summary>Appends the block, mixed to mono, to one of the sample rings.</summary>
    private static void WriteRing(float[] target, ref long position, float* s, int frames, int channels)
    {
        const int mask = SpectrumCapacity - 1;
        long write = position;
        float scale = 1f / channels;
        fixed (float* r = target)
        {
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += s[c * MaxFrames + f];
                r[(int)(write & mask)] = sum * scale;
                write++;
            }
        }
        Volatile.Write(ref position, write);
    }

    private void ClearState(int filter)
    {
        for (int c = 0; c < MaxChannels; c++)
        {
            int z = (c * FilterCount + filter) * 2;
            state[z] = 0;
            state[z + 1] = 0;
        }
    }
}
