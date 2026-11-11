using NavTune.Audio;
using NavTune.Model;

namespace NavTune.Tests;

public class ContentDetectorTests
{
    private static readonly Lazy<YamNet> Model = new(YamNet.Load);

    private static ContentKind? Classify(Dictionary<string, double> scores) =>
        ContentAnalyzer.Classify(label => scores.GetValueOrDefault(label));

    [Fact]
    public void ClassifiesLikeTheMacVersion()
    {
        // The macOS ContentDetector tests, with Apple's labels translated to AudioSet's.
        Assert.Equal(ContentKind.Speech, Classify(new() { ["Speech"] = 0.9, ["Music"] = 0.1 }));
        Assert.Equal(ContentKind.Rock, Classify(new() { ["Music"] = 0.9, ["Electric guitar"] = 0.6 }));
        Assert.Equal(ContentKind.Electronic, Classify(new() { ["Music"] = 0.9, ["Synthesizer"] = 0.5 }));
        Assert.Equal(ContentKind.Pop, Classify(new() { ["Music"] = 0.8, ["Singing"] = 0.7 }));
        Assert.Null(Classify(new() { ["Silence"] = 0.9 }));
        // Singing over talk is still music; YAMNet's genres count like instruments.
        Assert.Equal(ContentKind.Pop, Classify(new() { ["Speech"] = 0.8, ["Music"] = 0.4, ["Singing"] = 0.5 }));
        Assert.Equal(ContentKind.Rap, Classify(new() { ["Music"] = 0.9, ["Hip hop music"] = 0.5, ["Speech"] = 0.3 }));
        Assert.Equal(ContentKind.Classical, Classify(new() { ["Music"] = 0.9, ["Classical music"] = 0.4, ["Piano"] = 0.3 }));
        // YAMNet's weaker, spread-out evidence: related labels add up (a real rock song) ...
        Assert.Equal(ContentKind.Rock, Classify(new() { ["Music"] = 0.91, ["Rock music"] = 0.05, ["Grunge"] = 0.03,
            ["Progressive rock"] = 0.03, ["Heavy metal"] = 0.03, ["Electric guitar"] = 0.03, ["Punk rock"] = 0.02 }));
        // ...but a hint of one is not enough.
        Assert.Equal(ContentKind.Pop, Classify(new() { ["Music"] = 0.9, ["Synthesizer"] = 0.05, ["Electronic music"] = 0.04 }));
    }

    // MARK: - When to switch

    private static List<ContentKind?> Feed(AutoPresetPolicy policy, params ContentKind?[] kinds) =>
        kinds.Select(k => policy.Next(new ContentResult(k, ""))).ToList();

    [Fact]
    public void SwitchesAfterThreeInARowLikeTheMacVersion()
    {
        var policy = new AutoPresetPolicy();
        Assert.Equal([null, null, ContentKind.Speech], Feed(policy, ContentKind.Speech, ContentKind.Speech, ContentKind.Speech));
        // Unsure windows don't count, and don't break a streak either.
        Assert.Equal([null, null, null, ContentKind.Jazz], Feed(policy, ContentKind.Jazz, null, ContentKind.Jazz, ContentKind.Jazz));
    }

    [Fact]
    public void HoldsAStyleThroughTheQuieterPartsOfASong()
    {
        // rock-1 as heard: Rock in the loud parts, Pop (nothing stands out) in between, 1.5 s per result.
        var policy = new AutoPresetPolicy();
        var r = ContentKind.Rock;
        var p = ContentKind.Pop;
        var decisions = Feed(policy, p, p, p, p, p, r, r, r, p, p, r, r, r, r, r, r, r, r, p, p, p, p, p, p, p, p, p, p, p, p, r, r);
        var switches = decisions.Where(d => d is not null).Distinct().ToList();
        // Straight to Rock (no detour through pop while listening), then Rock for the rest of the song.
        Assert.Equal([ContentKind.Rock], switches);
        Assert.Equal(ContentKind.Rock, decisions.Last(d => d is not null));
    }

    [Fact]
    public void LetsGoOfAStyleWhenSomethingElsePlays()
    {
        var r = ContentKind.Rock;
        var p = ContentKind.Pop;

        // Another style, or speech, takes over as usual.
        var policy = new AutoPresetPolicy();
        Feed(policy, r, r, r);
        Assert.Equal(ContentKind.Piano, Feed(policy, ContentKind.Piano, p, ContentKind.Piano, ContentKind.Piano).Last());
        Assert.Equal(ContentKind.Speech, Feed(policy, ContentKind.Speech, ContentKind.Speech, ContentKind.Speech).Last());

        // A pause between songs: the next one starts afresh.
        policy = new AutoPresetPolicy();
        Feed(policy, r, r, r);
        Assert.Null(policy.Next(new ContentResult(null, "pause", IsPause: true)));
        Assert.Equal(p, Feed(policy, p, p, p, p, p, p).Last());

        // No pause (a playlist that crossfades): pop after about 30 s without any sign of rock.
        policy = new AutoPresetPolicy();
        Feed(policy, r, r, r);
        var decisions = Feed(policy, Enumerable.Repeat<ContentKind?>(p, 30).ToArray());
        int switchedAt = decisions.FindIndex(d => d == p);
        Assert.InRange(switchedAt, AutoPresetPolicy.PopToRelease, 26);

        // ...but not while there is still a faint sign of it (edm-1: electronic evidence 0.05-0.1 for 40 s).
        policy = new AutoPresetPolicy();
        Feed(policy, ContentKind.Electronic, ContentKind.Electronic, ContentKind.Electronic);
        var faint = Enumerable.Range(0, 40).Select(_ => policy.Next(new ContentResult(p, "", Hint: ContentKind.Electronic)));
        Assert.All(faint, d => Assert.Null(d));
    }

    [Fact]
    public void EveryLabelAndPresetExists()
    {
        foreach (var label in ContentAnalyzer.AllLabels) Assert.True(Model.Value.IndexOf(label) >= 0, label);
        foreach (var kind in Enum.GetValues<ContentKind>())
            Assert.Contains(Preset.BuiltIns, p => p.Id == kind.PresetId());
    }

    // MARK: - YAMNet port, checked against the NumPy port of Google's model (tools/yamnet/convert_yamnet.py)

    private static float[] Signal(string name)
    {
        var x = new float[YamNet.PatchSamples];
        long state = 12345;
        for (int n = 0; n < x.Length; n++)
        {
            double v;
            switch (name)
            {
                case "Sine440":
                    v = 0.5 * Math.Sin(2 * Math.PI * 440 * n / 16000.0);
                    break;
                case "Noise":
                    state = (state * 1103515245 + 12345) % 2147483648;
                    v = (state / 2147483648.0 * 2 - 1) * 0.3;
                    break;
                default:
                    v = 0;
                    for (int k = 1; k <= 20; k++) v += 0.3 / k * Math.Sin(2 * Math.PI * 220 * k * n / 16000.0);
                    v *= 0.5 + 0.5 * Math.Sin(2 * Math.PI * 3 * n / 16000.0);
                    break;
            }
            x[n] = (float)v;
        }
        return x;
    }

    [Theory]
    [InlineData("Sine440", -4.210294, -0.360397, -5.130275, -6.899401)]
    [InlineData("Noise", 0.683369, -0.172508, 0.962463, 2.620111)]
    [InlineData("Buzz", 0.806158, -0.372978, -0.122702, -6.901283)]
    public void LogMelMatchesReference(string signal, double f0x0, double f10x5, double f50x20, double f95x63)
    {
        var features = Model.Value.LogMel(Signal(signal));
        Assert.Equal(f0x0, features[0 * 64 + 0], 0.001);
        Assert.Equal(f10x5, features[10 * 64 + 5], 0.001);
        Assert.Equal(f50x20, features[50 * 64 + 20], 0.001);
        Assert.Equal(f95x63, features[95 * 64 + 63], 0.001);
    }

    public static TheoryData<string, int[], float[]> ReferenceScores => new()
    {
        // Telephone, Dial tone, Alarm, Busy signal, Sidetone
        { "Sine440", [383, 387, 382, 388, 512], [0.996596f, 0.989959f, 0.977477f, 0.966491f, 0.047968f] },
        // Waterfall, Water, White noise, Gurgling, Spray
        { "Noise", [287, 282, 514, 291, 447], [0.655641f, 0.475688f, 0.245613f, 0.156607f, 0.154301f] },
        // Music, Musical instrument, Synthesizer, Sampler, Keyboard (musical)
        { "Buzz", [132, 133, 153, 154, 147], [0.795101f, 0.194143f, 0.192394f, 0.163928f, 0.088475f] },
    };

    [Theory]
    [MemberData(nameof(ReferenceScores))]
    public void ScoresMatchReference(string signal, int[] classes, float[] expected)
    {
        var scores = Model.Value.Classify(Signal(signal));
        var top = Enumerable.Range(0, scores.Length).OrderByDescending(k => scores[k]).Take(classes.Length).ToArray();
        Assert.Equal(classes, top);
        for (int i = 0; i < classes.Length; i++) Assert.Equal(expected[i], scores[classes[i]], 0.002f);
    }

    [Fact]
    public void SilenceIsSilence()
    {
        // yamnet_test.py testZeros.
        var scores = Model.Value.Classify(new float[YamNet.PatchSamples]);
        Assert.Equal("Silence", Model.Value.ClassNames[Array.IndexOf(scores, scores.Max())]);
    }

    // MARK: - Resampler

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(96000)]
    [InlineData(22050)]
    public void ResamplesToSixteenKilohertz(int rate)
    {
        var resampler = new Resampler(rate, 16000);
        var input = Enumerable.Range(0, rate).Select(n => (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * n / rate))).ToArray();
        var output = new float[resampler.MaxOutput(input.Length)];
        int count = resampler.Process(input, output);
        Assert.InRange(count, 15800, 16000);
        // Output n is the input at time n / 16000 s, once the filter has real audio on both sides.
        for (int n = 200; n < count; n++)
            Assert.Equal(0.5 * Math.Sin(2 * Math.PI * 1000 * n / 16000.0), output[n], 0.002);
    }

    [Fact]
    public void FiltersOutWhatSixteenKilohertzCannotHold()
    {
        var resampler = new Resampler(48000, 16000);
        var input = Enumerable.Range(0, 48000).Select(n => (float)(0.5 * Math.Sin(2 * Math.PI * 9500 * n / 48000.0))).ToArray();
        var output = new float[resampler.MaxOutput(input.Length)];
        int count = resampler.Process(input, output);
        double rms = Math.Sqrt(output.Skip(200).Take(count - 200).Average(v => (double)v * v));
        Assert.True(rms < 0.001, $"9.5 kHz leaked through at {rms}");
    }

    [Fact]
    public void ChunksDoNotChangeTheResult()
    {
        var input = Enumerable.Range(0, 30000).Select(n => (float)Math.Sin(n * 0.37) * 0.4f).ToArray();
        var whole = new Resampler(44100, 16000);
        var expected = new float[whole.MaxOutput(input.Length)];
        int expectedCount = whole.Process(input, expected);

        var chunked = new Resampler(44100, 16000);
        var actual = new List<float>();
        var random = new Random(7);
        var buffer = new float[chunked.MaxOutput(2000)];
        for (int at = 0; at < input.Length;)
        {
            int size = Math.Min(random.Next(1, 2000), input.Length - at);
            int count = chunked.Process(input.AsSpan(at, size), buffer);
            actual.AddRange(buffer.Take(count));
            at += size;
        }
        Assert.Equal(expectedCount, actual.Count);
        for (int i = 0; i < expectedCount; i++) Assert.Equal(expected[i], actual[i], 1e-6f);
    }

    // MARK: - Analyzer and processor

    [Fact]
    public void ReportsAboutEveryOneAndAHalfSeconds()
    {
        var analyzer = new ContentAnalyzer(Model.Value, 48000);
        var tone = Signal("Buzz");
        var results = new List<ContentResult>();
        // 4 s of the buzz at 48 kHz (each 16 kHz sample repeated three times is close enough), fed in 10 ms packets.
        var stream = Enumerable.Range(0, 4 * 48000).Select(i => tone[(i / 3) % tone.Length]).ToArray();
        for (int at = 0; at < stream.Length; at += 480) results.AddRange(analyzer.Feed(stream.AsSpan(at, 480)));
        // The first result after 0.975 + 0.75 s, then every 1.5 s.
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.NotNull(r.Kind));
        Assert.Contains("Music", results[0].Summary);
    }

    [Fact]
    public void SilenceGivesNoVerdict()
    {
        var analyzer = new ContentAnalyzer(Model.Value, 44100);
        var results = analyzer.Feed(new float[4 * 44100]);
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Null(r.Kind));
    }

    /// <summary>The audio thread feeds the processor in 10 ms packets; the detector thread must keep up and report.</summary>
    [Fact]
    public unsafe void DetectorReportsFromItsOwnThread()
    {
        var processor = new AudioProcessor();
        processor.Prepare(48000);
        processor.Update(new SoundSettings(), false);
        using var detector = new ContentDetector();
        var results = new System.Collections.Concurrent.ConcurrentQueue<(int Generation, ContentResult Result)>();
        detector.Detected += (g, r) => results.Enqueue((g, r));

        var tone = Signal("Buzz");
        long played = 0;
        var packet = new float[2 * 480];
        var output = new float[packet.Length];
        void Play(double seconds)
        {
            // About three times real time: the 1.4 s input ring still holds everything between two reads.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long packets = (long)(seconds * 100);
            for (long p = 0; p < packets; p++)
            {
                for (int f = 0; f < 480; f++)
                {
                    float v = tone[(int)((played + f) / 3 % tone.Length)];
                    packet[2 * f] = packet[2 * f + 1] = v;
                }
                played += 480;
                fixed (float* i = packet)
                fixed (float* o = output)
                    processor.Process(i, o, 480, 2);
                while (clock.Elapsed.TotalMilliseconds < p * 10 / 3.0) Thread.Sleep(1);
            }
        }

        int generation = detector.Start(processor);
        Play(4.0);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (results.Count < 2 && DateTime.UtcNow < deadline) Thread.Sleep(50);
        Assert.True(results.Count >= 2, $"{results.Count} results");
        Assert.All(results, r => Assert.Equal(generation, r.Generation));
        Assert.All(results, r => Assert.NotNull(r.Result.Kind));

        // Paused (comparing with the original sound): nothing is analyzed.
        detector.Paused = true;
        Thread.Sleep(300);
        results.Clear();
        Play(3.0);
        Thread.Sleep(600);
        Assert.Empty(results);

        // Stopped: nothing either, and a restart is a new generation.
        detector.Paused = false;
        detector.Stop();
        Play(3.0);
        Thread.Sleep(600);
        Assert.Empty(results);
        Assert.NotEqual(generation, detector.Start(processor));
    }

    [Fact]
    public unsafe void DetectorHearsTheUnprocessedInput()
    {
        var processor = new AudioProcessor();
        processor.Prepare(48000);
        var sound = new SoundSettings { Preamp = 6, Boost = 6 };
        processor.Update(sound, false);
        var input = new float[2 * 480];
        for (int f = 0; f < 480; f++)
        {
            input[2 * f] = 0.5f;
            input[2 * f + 1] = 0.1f;
        }
        var output = new float[input.Length];
        long position = processor.InputWritePosition;
        fixed (float* i = input)
        fixed (float* o = output)
            processor.Process(i, o, 480, 2);

        var heard = new float[1000];
        int count = processor.ReadInput(ref position, heard);
        Assert.Equal(480, count);
        Assert.All(heard.Take(count), v => Assert.Equal(0.3f, v, 1e-6f));
        Assert.Equal(0, processor.ReadInput(ref position, heard));
        // ...while what is played is louder (+6 dB input gain).
        Assert.True(Math.Abs(output[2 * 479] - 0.5f) > 0.05f, $"output {output[2 * 479]}");
    }
}
