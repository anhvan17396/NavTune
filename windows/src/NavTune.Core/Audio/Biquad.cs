namespace NavTune.Audio;

/// <summary>Normalized biquad coefficients (RBJ Audio EQ Cookbook).</summary>
public readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2)
{
    public static readonly double ButterworthQ = 1 / Math.Sqrt(2);

    public static Biquad Identity => new(1, 0, 0, 0, 0);

    private static Biquad Normalized(double b0, double b1, double b2, double a0, double a1, double a2) =>
        new(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);

    public static Biquad Peaking(double frequency, double gainDB, double q, double sampleRate)
    {
        if (gainDB == 0 || frequency >= sampleRate * 0.45) return Identity;
        double a = Math.Pow(10, gainDB / 40);
        double w0 = 2 * Math.PI * frequency / sampleRate;
        double alpha = Math.Sin(w0) / (2 * q);
        double cosw = Math.Cos(w0);
        return Normalized(1 + alpha * a, -2 * cosw, 1 - alpha * a, 1 + alpha / a, -2 * cosw, 1 - alpha / a);
    }

    public static Biquad LowShelf(double frequency, double gainDB, double sampleRate, double? q = null)
    {
        if (gainDB == 0) return Identity;
        double a = Math.Pow(10, gainDB / 40);
        double w0 = 2 * Math.PI * frequency / sampleRate;
        double cosw = Math.Cos(w0);
        double k = 2 * Math.Sqrt(a) * Math.Sin(w0) / (2 * (q ?? ButterworthQ));
        return Normalized(
            a * ((a + 1) - (a - 1) * cosw + k),
            2 * a * ((a - 1) - (a + 1) * cosw),
            a * ((a + 1) - (a - 1) * cosw - k),
            (a + 1) + (a - 1) * cosw + k,
            -2 * ((a - 1) + (a + 1) * cosw),
            (a + 1) + (a - 1) * cosw - k);
    }

    public static Biquad HighShelf(double frequency, double gainDB, double sampleRate, double? q = null)
    {
        if (gainDB == 0 || frequency >= sampleRate * 0.45) return Identity;
        double a = Math.Pow(10, gainDB / 40);
        double w0 = 2 * Math.PI * frequency / sampleRate;
        double cosw = Math.Cos(w0);
        double k = 2 * Math.Sqrt(a) * Math.Sin(w0) / (2 * (q ?? ButterworthQ));
        return Normalized(
            a * ((a + 1) + (a - 1) * cosw + k),
            -2 * a * ((a - 1) + (a + 1) * cosw),
            a * ((a + 1) + (a - 1) * cosw - k),
            (a + 1) - (a - 1) * cosw + k,
            2 * ((a - 1) - (a + 1) * cosw),
            (a + 1) - (a - 1) * cosw - k);
    }

    public static Biquad HighPass(double frequency, double sampleRate, double? q = null)
    {
        if (frequency >= sampleRate * 0.45) return Identity;
        double w0 = 2 * Math.PI * frequency / sampleRate;
        double alpha = Math.Sin(w0) / (2 * (q ?? ButterworthQ));
        double cosw = Math.Cos(w0);
        return Normalized((1 + cosw) / 2, -(1 + cosw), (1 + cosw) / 2, 1 + alpha, -2 * cosw, 1 - alpha);
    }

    public static Biquad LowPass(double frequency, double sampleRate, double? q = null)
    {
        if (frequency >= sampleRate * 0.45) return Identity;
        double w0 = 2 * Math.PI * frequency / sampleRate;
        double alpha = Math.Sin(w0) / (2 * (q ?? ButterworthQ));
        double cosw = Math.Cos(w0);
        return Normalized((1 - cosw) / 2, 1 - cosw, (1 - cosw) / 2, 1 + alpha, -2 * cosw, 1 - alpha);
    }

    public bool IsIdentity => B0 == 1 && B1 == 0 && B2 == 0 && A1 == 0 && A2 == 0;

    /// <summary>Power response |H|² given cos/sin of ω and 2ω.</summary>
    public double Power(double cos1, double sin1, double cos2, double sin2)
    {
        double nr = B0 + B1 * cos1 + B2 * cos2, ni = -(B1 * sin1 + B2 * sin2);
        double dr = 1 + A1 * cos1 + A2 * cos2, di = -(A1 * sin1 + A2 * sin2);
        return (nr * nr + ni * ni) / Math.Max(dr * dr + di * di, 1e-30);
    }

    /// <summary>Magnitude response in dB at <paramref name="frequency"/>.</summary>
    public double MagnitudeDB(double frequency, double sampleRate)
    {
        double w = 2 * Math.PI * frequency / sampleRate;
        return 10 * Math.Log10(Math.Max(Power(Math.Cos(w), Math.Sin(w), Math.Cos(2 * w), Math.Sin(2 * w)), 1e-30));
    }
}

/// <summary>
/// Precomputed trigonometry for evaluating an EQ response at log-spaced frequencies,
/// so drawing the curve costs one log10 per point instead of several per filter.
/// </summary>
public sealed class ResponseTable
{
    public double[] Frequencies { get; }
    private readonly double[] cos1, sin1, cos2, sin2;

    public ResponseTable(double sampleRate, int count, double low = 20, double high = 20000)
    {
        Frequencies = new double[count];
        cos1 = new double[count];
        sin1 = new double[count];
        cos2 = new double[count];
        sin2 = new double[count];
        for (int i = 0; i < count; i++)
        {
            double f = low * Math.Pow(high / low, (double)i / (count - 1));
            double w = 2 * Math.PI * f / sampleRate;
            Frequencies[i] = f;
            cos1[i] = Math.Cos(w);
            sin1[i] = Math.Sin(w);
            cos2[i] = Math.Cos(2 * w);
            sin2[i] = Math.Sin(2 * w);
        }
    }

    /// <summary>Total response in dB of the cascade plus a flat <paramref name="gain"/>.</summary>
    public double[] Decibels(IReadOnlyList<Biquad> filters, double gain)
    {
        var active = filters.Where(f => !f.IsIdentity).ToArray();
        var result = new double[Frequencies.Length];
        for (int i = 0; i < result.Length; i++)
        {
            double power = 1;
            foreach (var k in active) power *= k.Power(cos1[i], sin1[i], cos2[i], sin2[i]);
            result[i] = 10 * Math.Log10(Math.Max(power, 1e-30)) + gain;
        }
        return result;
    }
}
