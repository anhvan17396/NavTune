using System.Diagnostics;
using System.Text.Json;
using NavTune.Audio;
using NavTune.Model;
using Xunit.Abstractions;

namespace NavTune.Tests;

/// <summary>Port of the macOS AudioProcessorTests: the Windows DSP must behave exactly the same.</summary>
public unsafe class AudioProcessorTests(ITestOutputHelper output)
{
    private const double SampleRate = 48000;

    /// <summary>Runs a stereo sine through the processor in 512-frame blocks and returns the output peak of the last half.</summary>
    private static float Render(AudioProcessor processor, double frequency, float amplitude, double seconds = 2)
    {
        const int frames = 512;
        var input = new float[frames * 2];
        var outBuffer = new float[frames * 2];
        int blocks = (int)(seconds * SampleRate) / frames;
        float peak = 0;
        long n = 0;
        for (int block = 0; block < blocks; block++)
        {
            for (int f = 0; f < frames; f++, n++)
            {
                float v = amplitude * (float)Math.Sin(2 * Math.PI * frequency * n / SampleRate);
                input[f * 2] = v;
                input[f * 2 + 1] = v;
            }
            fixed (float* i = input)
            fixed (float* o = outBuffer)
                processor.Process(i, o, frames, 2);
            if (block >= blocks / 2)
            {
                foreach (float s in outBuffer)
                {
                    Assert.True(float.IsFinite(s));
                    peak = Math.Max(peak, Math.Abs(s));
                }
            }
        }
        return peak;
    }

    private static AudioProcessor MakeProcessor(SoundSettings settings, bool bypass = false)
    {
        var p = new AudioProcessor();
        p.Prepare(SampleRate);
        p.Update(settings, bypass);
        return p;
    }

    private static double Db(float ratio) => 20 * Math.Log10(ratio);

    [Fact]
    public void FlatSettingsPassSignalUnchanged()
    {
        float peak = Render(MakeProcessor(new SoundSettings()), 1000, 0.5f);
        Assert.True(Math.Abs(Db(peak / 0.5f)) < 0.1);
    }

    [Fact]
    public void BandBoostMatchesGain()
    {
        var s = new SoundSettings();
        s.Gains[5] = 6; // 1 kHz
        float peak = Render(MakeProcessor(s), 1000, 0.1f);
        Assert.True(Math.Abs(Db(peak / 0.1f) - 6) < 0.3);
    }

    [Fact]
    public void BandCutDoesNotAffectFarFrequencies()
    {
        var s = new SoundSettings();
        s.Gains[0] = -12; // 32 Hz
        float peak = Render(MakeProcessor(s), 4000, 0.1f);
        Assert.True(Math.Abs(Db(peak / 0.1f)) < 0.3);
    }

    [Fact]
    public void BassShelfBoostsLows()
    {
        var s = new SoundSettings { Bass = 6 };
        float low = Render(MakeProcessor(s), 30, 0.1f);
        float high = Render(MakeProcessor(s), 5000, 0.1f);
        Assert.True(Math.Abs(Db(low / 0.1f) - 6) < 0.6);
        Assert.True(Math.Abs(Db(high / 0.1f)) < 0.3);
    }

    [Fact]
    public void LimiterPreventsClipping()
    {
        var s = new SoundSettings { Boost = 12 };
        Array.Fill(s.Gains, 12f);
        float peak = Render(MakeProcessor(s), 1000, 0.9f);
        Assert.True(peak <= 1.0f);
    }

    [Fact]
    public void LoudnessLevelerRaisesQuietAudio()
    {
        var s = new SoundSettings { Loudness = 1 };
        float peak = Render(MakeProcessor(s), 1000, 0.01f, seconds: 4);
        Assert.True(peak > 0.01f * 1.5f);
    }

    [Fact]
    public void MonoWidthCollapsesStereo()
    {
        var s = new SoundSettings { Width = 0 };
        // Identical L/R stays identical in mono, so level is unchanged.
        float peak = Render(MakeProcessor(s), 1000, 0.5f);
        Assert.True(Math.Abs(Db(peak / 0.5f)) < 0.1);
    }

    [Fact]
    public void BypassIgnoresSettings()
    {
        var s = new SoundSettings();
        s.Gains[5] = 12;
        float peak = Render(MakeProcessor(s, bypass: true), 1000, 0.5f);
        Assert.True(Math.Abs(Db(peak / 0.5f)) < 0.01);
    }

    [Fact]
    public void PresetsAreWellFormed()
    {
        Assert.Equal(Preset.BuiltIns.Count, Preset.BuiltIns.Select(p => p.Id).Distinct().Count());
        Assert.Equal(24, Preset.BuiltIns.Count);
        foreach (var preset in Preset.BuiltIns)
        {
            Assert.Equal(SoundSettings.BandCount, preset.Gains.Length);
            Assert.All(preset.Gains, g => Assert.True(Math.Abs(g) <= 12));
        }
    }

    [Fact]
    public void PresetHeadroomMatchesTheMacApp()
    {
        // Swift: -(max(0, gains.max()) * 0.6).rounded(), rounding half away from zero.
        Assert.Equal(0f, Preset.BuiltIns.First(p => p.Id == "flat").Preamp);
        Assert.Equal(-4f, Preset.BuiltIns.First(p => p.Id == "bass").Preamp);     // max 7 -> 4.2 -> 4
        Assert.Equal(-3f, Preset.BuiltIns.First(p => p.Id == "rock").Preamp);     // max 5 -> 3.0 -> 3
        Assert.Equal(-4f, Preset.BuiltIns.First(p => p.Id == "metal").Preamp);    // max 6 -> 3.6 -> 4
        Assert.Equal(-1f, Preset.BuiltIns.First(p => p.Id == "night").Preamp);    // max 1 -> 0.6 -> 1
    }

    [Fact]
    public void SettingsDecodeWithMissingKeys()
    {
        var s = JsonSerializer.Deserialize<SoundSettings>("""{"presetID":"rock","gains":[1,2,3,4,5,6,7,8,9,10]}""")!;
        Assert.Equal("rock", s.PresetId);
        Assert.Equal(1f, s.Width);
        Assert.Equal(0f, s.Boost);
    }

    [Fact]
    public void OldSettingsDecodeAsGraphicMode()
    {
        var s = JsonSerializer.Deserialize<SoundSettings>("""{"presetID":"pop","gains":[0,0,0,0,0,0,0,0,0,0],"bass":2}""")!;
        Assert.Equal(EQMode.Graphic, s.Mode);
        Assert.Equal(SoundSettings.DefaultBands().Length, s.Bands.Count);
        Assert.Equal(2f, s.Bass);
    }

    [Fact]
    public void WrongGainCountFallsBackToFlat()
    {
        var s = JsonSerializer.Deserialize<SoundSettings>("""{"gains":[1,2,3]}""")!;
        Assert.Equal(SoundSettings.BandCount, s.Gains.Length);
        Assert.All(s.Gains, g => Assert.Equal(0f, g));
    }

    [Fact]
    public void MacPresetFilesImport()
    {
        // As written by the macOS app's JSONEncoder (UUIDs upper-case, SF Symbol icon names).
        const string json = """
        [{"bands":[{"enabled":true,"frequency":80,"gain":3.5,"id":"8F1C1E8B-2F0A-4C3B-9C1D-3A6E2B7F9D10","kind":"lowShelf","q":0.7071}],
          "gains":[1,2,3,4,5,6,7,8,9,10],"icon":"dial.medium","id":"user.ABC","name":"Tai nghe","preamp":-2}]
        """;
        var presets = JsonSerializer.Deserialize<List<Preset>>(json)!;
        var p = Assert.Single(presets);
        Assert.Equal("Tai nghe", p.Name);
        Assert.False(p.IsBuiltIn);
        Assert.Equal(FilterKind.LowShelf, p.Bands![0].Kind);
        Assert.Equal(3.5f, p.Bands[0].Gain);
        // And they round-trip in the same shape.
        string written = JsonSerializer.Serialize(presets);
        Assert.Contains("\"kind\":\"lowShelf\"", written);
        Assert.Contains("\"presetID\"", JsonSerializer.Serialize(new SoundSettings()));
        Assert.DoesNotContain("nameEN", written);
    }

    [Fact]
    public void ParametricBellBoostsItsFrequency()
    {
        var s = new SoundSettings { Mode = EQMode.Parametric, Bands = [new EQBand { Kind = FilterKind.Peak, Frequency = 2000, Gain = 6, Q = 1 }] };
        float at = Render(MakeProcessor(s), 2000, 0.1f);
        float away = Render(MakeProcessor(s), 100, 0.1f);
        Assert.True(Math.Abs(Db(at / 0.1f) - 6) < 0.3);
        Assert.True(Math.Abs(Db(away / 0.1f)) < 0.3);
    }

    [Fact]
    public void LowCutRemovesRumble()
    {
        var s = new SoundSettings { Mode = EQMode.Parametric, Bands = [new EQBand { Kind = FilterKind.LowCut, Frequency = 200, Q = Biquad.ButterworthQ }] };
        float low = Render(MakeProcessor(s), 30, 0.5f);
        float high = Render(MakeProcessor(s), 3000, 0.5f);
        Assert.True(Db(low / 0.5f) < -24);
        Assert.True(Math.Abs(Db(high / 0.5f)) < 0.3);
    }

    [Fact]
    public void DisabledBandIsTransparent()
    {
        var s = new SoundSettings { Mode = EQMode.Parametric, Bands = [new EQBand { Kind = FilterKind.Peak, Frequency = 1000, Gain = 12, Q = 1, Enabled = false }] };
        float peak = Render(MakeProcessor(s), 1000, 0.5f);
        Assert.True(Math.Abs(Db(peak / 0.5f)) < 0.1);
    }

    [Fact]
    public void ResponseCurveMatchesRenderedGain()
    {
        var s = new SoundSettings();
        s.Gains[3] = -6; // 250 Hz
        double curve = AudioProcessor.Filters(s, SampleRate).Sum(f => f.MagnitudeDB(250, SampleRate));
        Assert.True(Math.Abs(curve + 6) < 0.1);
    }

    [Fact]
    public void ResponseTableMatchesDirectEvaluation()
    {
        var s = new SoundSettings { Gains = [3, -2, 4, 0, -6, 2, 0, 5, -3, 1], Bass = 4 };
        var filters = AudioProcessor.Filters(s, SampleRate);
        var table = new ResponseTable(SampleRate, 40);
        var fast = table.Decibels(filters, 0);
        for (int i = 0; i < table.Frequencies.Length; i++)
        {
            double direct = filters.Sum(f => f.MagnitudeDB(table.Frequencies[i], SampleRate));
            Assert.True(Math.Abs(fast[i] - direct) < 1e-6);
        }
    }

    [Fact]
    public void SpectrumAnalyzerFindsTone()
    {
        var analyzer = new SpectrumAnalyzer(64);
        var samples = Enumerable.Range(0, SpectrumAnalyzer.Size).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * i / SampleRate))).ToArray();
        var bands = analyzer.Analyze(samples, SampleRate);
        int loudest = Array.IndexOf(bands, bands.Max());
        // Band edges are 20 Hz * 1000^(i/64); 1 kHz falls in band 36.
        Assert.InRange(loudest, 35, 37);
        Assert.True(bands[loudest] > 0.8f);
        Assert.True(bands[5] < 0.3f);
    }

    [Fact]
    public void ReenablingBypassedProcessingIsClean()
    {
        var s = new SoundSettings();
        s.Gains[0] = 12;
        var p = MakeProcessor(s, bypass: true);
        Render(p, 1000, 0.5f, seconds: 0.5);
        p.Update(s, bypass: false);
        float peak = Render(p, 1000, 0.5f, seconds: 1);
        Assert.True(peak <= 1);
    }

    [Fact]
    public void LevelsAreReportedWithoutBeingConsumed()
    {
        var p = MakeProcessor(new SoundSettings());
        Render(p, 1000, 0.5f, seconds: 0.5);
        float first = p.CurrentOutputLevel;
        float second = p.CurrentOutputLevel;
        Assert.True(first > 0.4f);
        Assert.Equal(first, second);
        Assert.True(p.CurrentInputLevel > 0.4f);
    }

    [Fact]
    public void PrepareRebuildsCoefficientsForTheNewRate()
    {
        var s = new SoundSettings();
        s.Gains[5] = 6;
        var p = new AudioProcessor();
        p.Prepare(SampleRate);
        p.Update(s, false);
        // The engine reopens at the virtual device's rate after settings were pushed.
        p.Prepare(SampleRate);
        float peak = Render(p, 1000, 0.1f);
        Assert.True(Math.Abs(Db(peak / 0.1f) - 6) < 0.3);
    }

    [Fact]
    public void SilenceInputProducesSilence()
    {
        var p = MakeProcessor(new SoundSettings { Boost = 12, Loudness = 1 });
        var outBuffer = new float[1024];
        fixed (float* o = outBuffer) p.Process(null, o, 512, 2);
        Assert.All(outBuffer, v => Assert.Equal(0f, v));
    }

    /// <summary>Real-time factor of the full chain with every filter active, the leveler on and a boost.</summary>
    [Fact]
    public void ProcessingIsFarFasterThanRealTime()
    {
        var s = new SoundSettings { Gains = [3, -2, 4, 1, -6, 2, -1, 5, -3, 1], Bass = 3, Clarity = 2, Width = 1.3f, Loudness = 0.5f, Boost = 3 };
        var p = MakeProcessor(s);
        const int frames = 512;
        var input = new float[frames * 2];
        var outBuffer = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float v = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * f / SampleRate));
            input[f * 2] = v;
            input[f * 2 + 1] = -v;
        }
        const double seconds = 30;
        int blocks = (int)(seconds * SampleRate) / frames;
        var clock = Stopwatch.StartNew();
        fixed (float* i = input)
        fixed (float* o = outBuffer)
        {
            for (int b = 0; b < blocks; b++) p.Process(i, o, frames, 2);
        }
        double used = clock.Elapsed.TotalSeconds;
        output.WriteLine($"DSP load: {used / seconds * 100:0.000}% of one core ({used / blocks * 1e6:0.0} µs per 512-frame block)");
        Assert.True(float.IsFinite(outBuffer[0]));
        Assert.True(used / seconds < 0.5);
    }
}
