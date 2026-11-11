using System.Numerics;
using System.Runtime.InteropServices;

namespace NavTune.Audio;

/// <summary>
/// Streaming mono sample-rate converter: a Kaiser-windowed sinc evaluated at exact rational phases
/// (e.g. 160 phases for 44.1 → 16 kHz). Used to feed the content classifier, so the anti-alias filter
/// follows resampy's "kaiser_best" roll-off, as YAMNet's training data was converted with it.
/// Feeding audio in any chunk sizes gives the same output as feeding it all at once.
/// </summary>
public sealed class Resampler
{
    private const double Rolloff = 0.9475937; // resampy kaiser_best: pass band edge relative to the lower Nyquist
    private const int ZeroCrossings = 32;     // per side
    private const double Beta = 9.0;          // Kaiser window: about 90 dB stop band

    private readonly int up, down, half, width;
    private readonly float[] table; // [phase][width]
    private float[] buffer;
    private int length; // valid samples in buffer
    private int index;  // buffer index of the input sample at or just before the next output
    private int phase;  // the next output sits phase / up samples after index

    public int InputRate { get; }
    public int OutputRate { get; }

    public Resampler(int inputRate, int outputRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputRate);
        InputRate = inputRate;
        OutputRate = outputRate;
        int gcd = (int)BigInteger.GreatestCommonDivisor(inputRate, outputRate);
        up = outputRate / gcd;
        down = inputRate / gcd;

        // Cut-off in cycles per input sample, just under the lower of the two Nyquist frequencies.
        double cutoff = Rolloff * 0.5 * Math.Min(inputRate, outputRate) / inputRate;
        half = (int)Math.Ceiling(ZeroCrossings / (2 * cutoff));
        width = 2 * half;
        table = new float[up * width];
        double norm = BesselI0(Beta);
        for (int p = 0; p < up; p++)
        {
            double fraction = (double)p / up;
            double sum = 0;
            var taps = table.AsSpan(p * width, width);
            for (int j = 0; j < width; j++)
            {
                double t = j - (half - 1) - fraction; // distance from the output instant, in input samples
                double x = t / half;
                double window = Math.Abs(x) >= 1 ? 0 : BesselI0(Beta * Math.Sqrt(1 - x * x)) / norm;
                double v = 2 * cutoff * Sinc(2 * cutoff * t) * window;
                taps[j] = (float)v;
                sum += v;
            }
            // Unity gain at DC for every phase, so a constant input comes out constant.
            for (int j = 0; j < width; j++) taps[j] = (float)(taps[j] / sum);
        }

        buffer = new float[Math.Max(4096, 2 * width)];
        Reset();
    }

    /// <summary>Most samples <see cref="Process"/> can return for <paramref name="inputCount"/> new input samples.</summary>
    public int MaxOutput(int inputCount) => (int)((long)(inputCount + width) * up / down) + 2;

    /// <summary>Forgets earlier audio, as if the stream started again (after silence before it).</summary>
    public void Reset()
    {
        // half - 1 samples of silence before the first input sample.
        length = half - 1;
        Array.Clear(buffer, 0, length);
        index = half - 1;
        phase = 0;
    }

    /// <summary>Converts the next chunk of the stream; returns how many output samples were written.</summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (length + input.Length > buffer.Length)
            Array.Resize(ref buffer, Math.Max(buffer.Length * 2, length + input.Length));
        input.CopyTo(buffer.AsSpan(length));
        length += input.Length;

        int produced = 0;
        // Each output needs buffer[index - half + 1 ... index + half].
        while (index + half < length && produced < output.Length)
        {
            output[produced++] = Dot(buffer.AsSpan(index - half + 1, width), table.AsSpan(phase * width, width));
            phase += down;
            index += phase / up;
            phase %= up;
        }

        int consumed = Math.Min(index - half + 1, length);
        if (consumed > 0)
        {
            buffer.AsSpan(consumed, length - consumed).CopyTo(buffer);
            length -= consumed;
            index -= consumed;
        }
        return produced;
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = Vector<float>.Zero;
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        for (int v = 0; v < va.Length; v++) sum += va[v] * vb[v];
        float total = Vector.Sum(sum);
        for (int i = va.Length * Vector<float>.Count; i < a.Length; i++) total += a[i] * b[i];
        return total;
    }

    private static double Sinc(double x) => x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 64; k++)
        {
            term *= q / (k * k);
            sum += term;
            if (term < sum * 1e-17) break;
        }
        return sum;
    }
}
