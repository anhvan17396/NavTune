using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace NavTune.Audio;

/// <summary>
/// YAMNet, Google's AudioSet sound classifier (521 classes: speech, music, instruments, genres, everyday sounds),
/// ported from the official features.py and yamnet.py to plain C#, so it runs on the CPU with nothing else installed.
/// It stands in for Apple's built-in SoundAnalysis classifier that the macOS version uses.
/// The weights (Resources/yamnet.bin) were made from Google's yamnet.h5 by tools/yamnet/convert_yamnet.py,
/// with every batch norm folded into its convolution and kernels stored as float16.
/// One analysis of 0.975 s of audio costs about 67 million multiply-adds. Not thread-safe.
/// </summary>
public sealed class YamNet
{
    public const int SampleRate = 16000;
    public const int PatchFrames = 96;
    public const int MelBands = 64;
    public const int ClassCount = 521;
    private const int Window = 400, Hop = 160, FftSize = 512, Bins = FftSize / 2 + 1;
    private const double MelMinHz = 125, MelMaxHz = 7500, LogOffset = 0.001;
    private const int Embedding = 1024;

    /// <summary>Samples one classification looks at: 96 frames of 25 ms, 10 ms apart (0.975 s at 16 kHz).</summary>
    public const int PatchSamples = (PatchFrames - 1) * Hop + Window;

    // yamnet.py: layer1 is a full 3x3 convolution, layer2...layer14 are depthwise separable.
    private static readonly (int Stride, int Filters)[] LayerDefs =
        [(2, 32), (1, 64), (2, 128), (1, 128), (2, 256), (1, 256), (2, 512),
         (1, 512), (1, 512), (1, 512), (1, 512), (1, 512), (2, 1024), (1, 1024)];

    private sealed record Separable(int Stride, int Inputs, int Outputs,
        float[] Depthwise, float[] DepthwiseBias, float[] Pointwise, float[] PointwiseBias);

    private readonly float[] conv1, conv1Bias, logits, logitsBias;
    private readonly Separable[] layers;
    private readonly Dictionary<string, int> classIndex;

    // Scratch, reused by every call.
    private readonly double[] re = new double[FftSize], im = new double[FftSize], magnitude = new double[Bins];
    private readonly float[] features = new float[PatchFrames * MelBands];
    private readonly float[] ping, pong;
    private readonly float[] embedding = new float[Embedding];

    public IReadOnlyList<string> ClassNames { get; }

    /// <summary>Loads the model embedded in NavTune.Core (about 15 MB in memory).</summary>
    public static YamNet Load()
    {
        var assembly = typeof(YamNet).Assembly;
        using var weights = assembly.GetManifestResourceStream("yamnet.bin")
            ?? throw new FileNotFoundException("The yamnet.bin resource is missing.");
        using var classMap = assembly.GetManifestResourceStream("yamnet_class_map.csv")
            ?? throw new FileNotFoundException("The yamnet_class_map.csv resource is missing.");
        return new YamNet(weights, classMap);
    }

    public YamNet(Stream weights, Stream classMap)
    {
        var tensors = ReadTensors(weights);
        float[] Take(string name, params int[] shape)
        {
            if (!tensors.TryGetValue(name, out var t)) throw new InvalidDataException($"YAMNet weights lack {name}.");
            if (!t.Shape.SequenceEqual(shape))
                throw new InvalidDataException($"YAMNet {name} is [{string.Join(',', t.Shape)}], expected [{string.Join(',', shape)}].");
            return t.Values;
        }

        conv1 = Take("conv1.w", 3, 3, LayerDefs[0].Filters);
        conv1Bias = Take("conv1.b", LayerDefs[0].Filters);
        layers = new Separable[LayerDefs.Length - 1];
        int channels = LayerDefs[0].Filters;
        for (int i = 1; i < LayerDefs.Length; i++)
        {
            var (stride, filters) = LayerDefs[i];
            string prefix = $"l{i + 1}";
            layers[i - 1] = new Separable(stride, channels, filters,
                Take($"{prefix}.dw.w", 3, 3, channels), Take($"{prefix}.dw.b", channels),
                Take($"{prefix}.pw.w", channels, filters), Take($"{prefix}.pw.b", filters));
            channels = filters;
        }
        logits = Take("logits.w", Embedding, ClassCount);
        logitsBias = Take("logits.b", ClassCount);

        var names = ReadClassNames(classMap);
        ClassNames = names;
        classIndex = [];
        for (int i = 0; i < names.Count; i++) classIndex.TryAdd(names[i], i);

        // The largest activation: 48 x 32 pixels x 64 channels, after layer2.
        int largest = 0, h = PatchFrames, w = MelBands;
        foreach (var (stride, filters) in LayerDefs)
        {
            h = (h + stride - 1) / stride;
            w = (w + stride - 1) / stride;
            largest = Math.Max(largest, h * w * filters);
        }
        ping = new float[largest];
        pong = new float[largest];
    }

    /// <summary>The index of an AudioSet class by its display name, e.g. "Electric guitar"; -1 if unknown.</summary>
    public int IndexOf(string className) => classIndex.TryGetValue(className, out int i) ? i : -1;

    /// <summary>
    /// Scores the last <see cref="PatchSamples"/> samples of 16 kHz mono audio (-1...1).
    /// Each of the 521 scores is an independent probability (0...1) that the sound is present.
    /// </summary>
    public float[] Classify(ReadOnlySpan<float> samples)
    {
        if (samples.Length < PatchSamples) throw new ArgumentException($"Needs at least {PatchSamples} samples.", nameof(samples));
        ComputeFeatures(samples[^PatchSamples..]);
        return Infer();
    }

    /// <summary>The log mel spectrogram [96 frames][64 bands] the network sees, for tests.</summary>
    internal float[] LogMel(ReadOnlySpan<float> samples)
    {
        ComputeFeatures(samples[^PatchSamples..]);
        return (float[])features.Clone();
    }

    // MARK: - Features (features.py)

    private static readonly double[] Hann = Enumerable.Range(0, Window)
        .Select(n => 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / Window)).ToArray(); // periodic, like tf.signal.stft
    private static readonly double[] TwiddleCos = Enumerable.Range(0, FftSize / 2).Select(k => Math.Cos(2 * Math.PI * k / FftSize)).ToArray();
    private static readonly double[] TwiddleSin = Enumerable.Range(0, FftSize / 2).Select(k => Math.Sin(2 * Math.PI * k / FftSize)).ToArray();
    private static readonly int[] BitReversed = Enumerable.Range(0, FftSize).Select(Reverse).ToArray();
    private static readonly (int Start, double[] Weights)[] MelFilters = BuildMelFilters();

    private static int Reverse(int i)
    {
        int bits = BitOperations.Log2(FftSize), r = 0;
        for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
        return r;
    }

    /// <summary>tf.signal.linear_to_mel_weight_matrix: triangles on the HTK mel scale, DC bin left out.</summary>
    private static (int, double[])[] BuildMelFilters()
    {
        static double Mel(double hz) => 1127.0 * Math.Log(1.0 + hz / 700.0);
        double low = Mel(MelMinHz), high = Mel(MelMaxHz);
        var edges = Enumerable.Range(0, MelBands + 2).Select(i => low + (high - low) * i / (MelBands + 1)).ToArray();
        var filters = new (int, double[])[MelBands];
        for (int m = 0; m < MelBands; m++)
        {
            var weights = new double[Bins];
            int first = -1, last = -1;
            for (int k = 1; k < Bins; k++)
            {
                double mel = Mel(k * (SampleRate / 2.0) / (Bins - 1));
                double rising = (mel - edges[m]) / (edges[m + 1] - edges[m]);
                double falling = (edges[m + 2] - mel) / (edges[m + 2] - edges[m + 1]);
                weights[k] = Math.Max(0, Math.Min(rising, falling));
                if (weights[k] > 0)
                {
                    if (first < 0) first = k;
                    last = k;
                }
            }
            filters[m] = first < 0 ? (0, []) : (first, weights[first..(last + 1)]);
        }
        return filters;
    }

    /// <summary>log(|STFT| x mel + 0.001): 25 ms periodic-Hann frames every 10 ms, 512-point FFT.</summary>
    private void ComputeFeatures(ReadOnlySpan<float> samples)
    {
        for (int t = 0; t < PatchFrames; t++)
        {
            var frame = samples.Slice(t * Hop, Window);
            for (int i = 0; i < Window; i++) re[i] = frame[i] * Hann[i];
            Array.Clear(re, Window, FftSize - Window);
            Array.Clear(im);
            Fft();
            for (int k = 0; k < Bins; k++) magnitude[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            for (int m = 0; m < MelBands; m++)
            {
                var (start, weights) = MelFilters[m];
                double sum = 0;
                for (int j = 0; j < weights.Length; j++) sum += magnitude[start + j] * weights[j];
                features[t * MelBands + m] = (float)Math.Log(sum + LogOffset);
            }
        }
    }

    private void Fft()
    {
        for (int i = 0; i < FftSize; i++)
        {
            int j = BitReversed[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int size = 2; size <= FftSize; size <<= 1)
        {
            int half = size >> 1, step = FftSize / size;
            for (int start = 0; start < FftSize; start += size)
            {
                for (int k = 0; k < half; k++)
                {
                    double wr = TwiddleCos[k * step], wi = -TwiddleSin[k * step];
                    int a = start + k, b = a + half;
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

    // MARK: - Network (yamnet.py)

    private float[] Infer()
    {
        int h = PatchFrames, w = MelBands;
        Conv1(features, h, w, ping, out h, out w);
        var input = ping;
        var output = pong;
        foreach (var layer in layers)
        {
            Depthwise(input, h, w, layer.Inputs, layer.Stride, layer.Depthwise, layer.DepthwiseBias, output, out h, out w);
            (input, output) = (output, input);
            Pointwise(input, h * w, layer.Inputs, layer.Pointwise, layer.PointwiseBias, layer.Outputs, output);
            (input, output) = (output, input);
        }

        // Global average pooling, then the 521 logits and a sigmoid each.
        int pixels = h * w;
        Array.Clear(embedding);
        for (int p = 0; p < pixels; p++)
            for (int c = 0; c < Embedding; c++) embedding[c] += input[p * Embedding + c];
        for (int c = 0; c < Embedding; c++) embedding[c] /= pixels;

        var scores = (float[])logitsBias.Clone();
        for (int i = 0; i < Embedding; i++)
        {
            float e = embedding[i];
            if (e == 0) continue; // ReLU output: usually many zeros
            Axpy(e, logits.AsSpan(i * ClassCount, ClassCount), scores);
        }
        for (int k = 0; k < ClassCount; k++) scores[k] = 1f / (1f + MathF.Exp(-scores[k]));
        return scores;
    }

    /// <summary>TensorFlow "same" padding for a 3-tap kernel: output size and the padding before.</summary>
    private static (int Size, int Before) Same(int size, int stride)
    {
        int output = (size + stride - 1) / stride;
        return (output, Math.Max((output - 1) * stride + 3 - size, 0) / 2);
    }

    /// <summary>layer1: 3x3 convolution of the single-channel spectrogram, stride 2, plus bias and ReLU.</summary>
    private void Conv1(float[] input, int h, int w, float[] output, out int oh, out int ow)
    {
        int c = conv1Bias.Length, vw = Vector<float>.Count;
        (oh, int top) = Same(h, 2);
        (ow, int left) = Same(w, 2);
        ref float k = ref MemoryMarshal.GetArrayDataReference(conv1);
        ref float b = ref MemoryMarshal.GetArrayDataReference(conv1Bias);
        ref float o = ref MemoryMarshal.GetArrayDataReference(output);
        for (int y = 0; y < oh; y++)
        {
            int y0 = y * 2 - top;
            for (int x = 0; x < ow; x++)
            {
                int x0 = x * 2 - left;
                nuint at = (nuint)((y * ow + x) * c);
                for (int ch = 0; ch < c; ch += vw)
                {
                    var acc = Vector.LoadUnsafe(ref b, (nuint)ch);
                    for (int ky = Math.Max(0, -y0); ky < Math.Min(3, h - y0); ky++)
                        for (int kx = Math.Max(0, -x0); kx < Math.Min(3, w - x0); kx++)
                            acc += new Vector<float>(input[(y0 + ky) * w + x0 + kx]) * Vector.LoadUnsafe(ref k, (nuint)((ky * 3 + kx) * c + ch));
                    Vector.Max(acc, Vector<float>.Zero).StoreUnsafe(ref o, at + (nuint)ch);
                }
            }
        }
    }

    /// <summary>3x3 depthwise convolution (one filter per channel) plus bias and ReLU. Layout [y][x][channel].</summary>
    private static void Depthwise(float[] input, int h, int w, int c, int stride, float[] kernel, float[] bias,
                                  float[] output, out int oh, out int ow)
    {
        int vw = Vector<float>.Count;
        (oh, int top) = Same(h, stride);
        (ow, int left) = Same(w, stride);
        ref float i = ref MemoryMarshal.GetArrayDataReference(input);
        ref float k = ref MemoryMarshal.GetArrayDataReference(kernel);
        ref float b = ref MemoryMarshal.GetArrayDataReference(bias);
        ref float o = ref MemoryMarshal.GetArrayDataReference(output);
        for (int y = 0; y < oh; y++)
        {
            int y0 = y * stride - top;
            int kyStart = Math.Max(0, -y0), kyEnd = Math.Min(3, h - y0);
            for (int x = 0; x < ow; x++)
            {
                int x0 = x * stride - left;
                int kxStart = Math.Max(0, -x0), kxEnd = Math.Min(3, w - x0);
                nuint at = (nuint)((y * ow + x) * c);
                for (int ch = 0; ch < c; ch += vw)
                {
                    var acc = Vector.LoadUnsafe(ref b, (nuint)ch);
                    for (int ky = kyStart; ky < kyEnd; ky++)
                        for (int kx = kxStart; kx < kxEnd; kx++)
                            acc += Vector.LoadUnsafe(ref i, (nuint)((((y0 + ky) * w) + x0 + kx) * c + ch))
                                 * Vector.LoadUnsafe(ref k, (nuint)((ky * 3 + kx) * c + ch));
                    Vector.Max(acc, Vector<float>.Zero).StoreUnsafe(ref o, at + (nuint)ch);
                }
            }
        }
    }

    /// <summary>
    /// 1x1 convolution plus bias and ReLU: output[pixels x cout] = input[pixels x cin] x kernel[cin x cout].
    /// Six pixels by two vectors of outputs at a time, so each weight loaded feeds twelve multiply-adds.
    /// </summary>
    private static void Pointwise(float[] input, int pixels, int cin, float[] kernel, float[] bias, int cout, float[] output)
    {
        int vw = Vector<float>.Count;
        ref float x = ref MemoryMarshal.GetArrayDataReference(input);
        ref float k = ref MemoryMarshal.GetArrayDataReference(kernel);
        ref float b = ref MemoryMarshal.GetArrayDataReference(bias);
        ref float o = ref MemoryMarshal.GetArrayDataReference(output);
        var zero = Vector<float>.Zero;
        int p = 0;
        for (; p + 6 <= pixels; p += 6)
        {
            nuint r0 = (nuint)(p * cin), r1 = r0 + (nuint)cin, r2 = r1 + (nuint)cin,
                  r3 = r2 + (nuint)cin, r4 = r3 + (nuint)cin, r5 = r4 + (nuint)cin;
            for (int co = 0; co < cout; co += 2 * vw)
            {
                var b0 = Vector.LoadUnsafe(ref b, (nuint)co);
                var b1 = Vector.LoadUnsafe(ref b, (nuint)(co + vw));
                Vector<float> a00 = b0, a01 = b1, a10 = b0, a11 = b1, a20 = b0, a21 = b1,
                              a30 = b0, a31 = b1, a40 = b0, a41 = b1, a50 = b0, a51 = b1;
                nuint wi = (nuint)co;
                for (nuint ci = 0; ci < (nuint)cin; ci++, wi += (nuint)cout)
                {
                    var w0 = Vector.LoadUnsafe(ref k, wi);
                    var w1 = Vector.LoadUnsafe(ref k, wi + (nuint)vw);
                    var v = new Vector<float>(Unsafe.Add(ref x, r0 + ci));
                    a00 = Fma(v, w0, a00); a01 = Fma(v, w1, a01);
                    v = new Vector<float>(Unsafe.Add(ref x, r1 + ci));
                    a10 = Fma(v, w0, a10); a11 = Fma(v, w1, a11);
                    v = new Vector<float>(Unsafe.Add(ref x, r2 + ci));
                    a20 = Fma(v, w0, a20); a21 = Fma(v, w1, a21);
                    v = new Vector<float>(Unsafe.Add(ref x, r3 + ci));
                    a30 = Fma(v, w0, a30); a31 = Fma(v, w1, a31);
                    v = new Vector<float>(Unsafe.Add(ref x, r4 + ci));
                    a40 = Fma(v, w0, a40); a41 = Fma(v, w1, a41);
                    v = new Vector<float>(Unsafe.Add(ref x, r5 + ci));
                    a50 = Fma(v, w0, a50); a51 = Fma(v, w1, a51);
                }
                nuint at = (nuint)(p * cout + co), row = (nuint)cout, second = (nuint)vw;
                Vector.Max(a00, zero).StoreUnsafe(ref o, at); Vector.Max(a01, zero).StoreUnsafe(ref o, at + second);
                Vector.Max(a10, zero).StoreUnsafe(ref o, at + row); Vector.Max(a11, zero).StoreUnsafe(ref o, at + row + second);
                Vector.Max(a20, zero).StoreUnsafe(ref o, at + 2 * row); Vector.Max(a21, zero).StoreUnsafe(ref o, at + 2 * row + second);
                Vector.Max(a30, zero).StoreUnsafe(ref o, at + 3 * row); Vector.Max(a31, zero).StoreUnsafe(ref o, at + 3 * row + second);
                Vector.Max(a40, zero).StoreUnsafe(ref o, at + 4 * row); Vector.Max(a41, zero).StoreUnsafe(ref o, at + 4 * row + second);
                Vector.Max(a50, zero).StoreUnsafe(ref o, at + 5 * row); Vector.Max(a51, zero).StoreUnsafe(ref o, at + 5 * row + second);
            }
        }
        for (; p < pixels; p++)
        {
            nuint r = (nuint)(p * cin);
            for (int co = 0; co < cout; co += vw)
            {
                var acc = Vector.LoadUnsafe(ref b, (nuint)co);
                nuint wi = (nuint)co;
                for (nuint ci = 0; ci < (nuint)cin; ci++, wi += (nuint)cout)
                    acc = Fma(new Vector<float>(Unsafe.Add(ref x, r + ci)), Vector.LoadUnsafe(ref k, wi), acc);
                Vector.Max(acc, zero).StoreUnsafe(ref o, (nuint)(p * cout + co));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<float> Fma(Vector<float> a, Vector<float> b, Vector<float> c) => Vector.MultiplyAddEstimate(a, b, c);

    private static void Axpy(float a, ReadOnlySpan<float> x, Span<float> y)
    {
        int vw = Vector<float>.Count, i = 0;
        var va = new Vector<float>(a);
        for (; i + vw <= x.Length; i += vw)
            Fma(va, new Vector<float>(x[i..]), new Vector<float>(y[i..])).CopyTo(y[i..]);
        for (; i < x.Length; i++) y[i] += a * x[i];
    }

    // MARK: - Files

    private sealed record Tensor(int[] Shape, float[] Values);

    /// <summary>
    /// "NTYAMNET", u32 version 1, u32 count, then per tensor: u8 name length, name, u8 type (1 = float16, 2 = float32),
    /// u8 rank, u32 dims[rank], little-endian data. See tools/yamnet/convert_yamnet.py.
    /// </summary>
    private static Dictionary<string, Tensor> ReadTensors(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "NTYAMNET" || reader.ReadUInt32() != 1)
            throw new InvalidDataException("Not a NavTune YAMNet weights file.");
        int count = checked((int)reader.ReadUInt32());
        var tensors = new Dictionary<string, Tensor>(count);
        for (int t = 0; t < count; t++)
        {
            string name = Encoding.ASCII.GetString(reader.ReadBytes(reader.ReadByte()));
            byte type = reader.ReadByte();
            var shape = new int[reader.ReadByte()];
            long size = 1;
            for (int d = 0; d < shape.Length; d++) size *= shape[d] = checked((int)reader.ReadUInt32());
            var values = new float[checked((int)size)];
            switch (type)
            {
                case 1:
                    var halves = reader.ReadBytes(values.Length * 2);
                    if (halves.Length != values.Length * 2) throw new EndOfStreamException();
                    for (int i = 0; i < values.Length; i++)
                        values[i] = (float)BitConverter.UInt16BitsToHalf((ushort)(halves[2 * i] | halves[2 * i + 1] << 8));
                    break;
                case 2:
                    var singles = reader.ReadBytes(values.Length * 4);
                    if (singles.Length != values.Length * 4) throw new EndOfStreamException();
                    for (int i = 0; i < values.Length; i++) values[i] = BitConverter.ToSingle(singles, 4 * i);
                    break;
                default:
                    throw new InvalidDataException($"Unknown tensor type {type} for {name}.");
            }
            tensors[name] = new Tensor(shape, values);
        }
        return tensors;
    }

    /// <summary>yamnet_class_map.csv: index,mid,display_name (names with commas are quoted).</summary>
    private static List<string> ReadClassNames(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        reader.ReadLine();
        var names = new List<string>(ClassCount);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            var fields = new List<string>(3);
            var field = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (quoted && ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = !quoted;
                else if (ch == ',' && !quoted) { fields.Add(field.ToString()); field.Clear(); }
                else field.Append(ch);
            }
            fields.Add(field.ToString());
            if (fields.Count != 3) throw new InvalidDataException($"Bad class map line: {line}");
            names.Add(fields[2]);
        }
        if (names.Count != ClassCount) throw new InvalidDataException($"The class map has {names.Count} classes, expected {ClassCount}.");
        return names;
    }
}
