using System.Diagnostics;
using NavTune.Model;

namespace NavTune.Audio;

/// <summary>
/// What is playing, as far as the sound classifier can tell; each kind maps to the closest built-in preset.
/// Same kinds and presets as the macOS version.
/// </summary>
public enum ContentKind { Speech, Rap, Rock, Electronic, Classical, Jazz, Piano, Acoustic, Pop }

public static class ContentKinds
{
    public static string PresetId(this ContentKind kind) => kind switch
    {
        ContentKind.Speech => "podcast",
        ContentKind.Rap => "rap",
        ContentKind.Rock => "rock",
        ContentKind.Electronic => "edm",
        ContentKind.Classical => "classical",
        ContentKind.Jazz => "jazz",
        ContentKind.Piano => "piano",
        ContentKind.Acoustic => "acoustic",
        _ => "pop",
    };

    public static string Title(this ContentKind kind) => kind switch
    {
        ContentKind.Speech => L10n.Tr("Lời nói / Podcast", "Speech / Podcast"),
        ContentKind.Rap => L10n.Tr("Rap", "Rap"),
        ContentKind.Rock => L10n.Tr("Guitar điện / Rock", "Electric guitar / Rock"),
        ContentKind.Electronic => L10n.Tr("Nhạc điện tử", "Electronic"),
        ContentKind.Classical => L10n.Tr("Dàn nhạc / Cổ điển", "Orchestral / Classical"),
        ContentKind.Jazz => L10n.Tr("Kèn / Jazz", "Horns / Jazz"),
        ContentKind.Piano => L10n.Tr("Piano", "Piano"),
        ContentKind.Acoustic => L10n.Tr("Guitar mộc / Acoustic", "Acoustic guitar"),
        _ => L10n.Tr("Nhạc có lời / Pop", "Vocal music / Pop"),
    };
}

/// <summary>
/// One analysis window's verdict, plus the strongest classes for the log. <paramref name="Hint"/> is the style
/// with at least a faint sign, even when too faint to decide on. <paramref name="IsPause"/> marks that nothing
/// played for a moment (Windows sends no audio at all then), e.g. between two songs.
/// </summary>
public sealed record ContentResult(ContentKind? Kind, string Summary, bool IsPause = false, ContentKind? Hint = null);

/// <summary>
/// When to switch presets, given one <see cref="ContentResult"/> about every 1.5 s. As on macOS, a kind must be
/// heard three times in a row (about 4–5 s). YAMNet needs longer than Apple's classifier to be sure of a style,
/// so two things are added. Pop, the verdict when nothing stands out yet, needs six in a row (about 9 s), so a song
/// doesn't pass through pop before its style is recognized. And a style once found (rock, piano…) is held through
/// the quieter passages of the same song, where the verdict falls back to pop: it changes when another style or
/// speech is heard, after a pause, or after about 30 s without even a faint sign of it.
/// </summary>
public sealed class AutoPresetPolicy
{
    public const int Confirmations = 3;
    public const int PopConfirmations = 6;
    public const int PopToRelease = 20;

    private ContentKind? pending, held;
    private int count, popRun, quietRun;

    /// <summary>Forgets the streaks, e.g. when playback starts again after standby.</summary>
    public void Reset()
    {
        pending = held = null;
        count = popRun = quietRun = 0;
    }

    /// <summary>The kind whose preset should now be used, or null to leave things as they are.</summary>
    public ContentKind? Next(ContentResult result)
    {
        if (result.IsPause)
        {
            held = null;
            return null;
        }
        if (result.Kind is not { } kind)
        {
            // Neither music nor speech (silence, a fade-out, other sounds): a new song may follow.
            if (++quietRun >= 2) held = null;
            return null;
        }
        quietRun = 0;
        if (kind == ContentKind.Pop && held is { } style)
        {
            // A faint sign of the held style means a quieter passage of the same song.
            popRun = result.Hint == style ? 0 : popRun + 1;
            if (popRun < PopToRelease) return null;
        }
        else if (kind != ContentKind.Pop)
        {
            popRun = 0;
        }

        if (kind == pending)
        {
            count++;
        }
        else
        {
            pending = kind;
            count = 1;
        }
        if (count < (kind == ContentKind.Pop ? PopConfirmations : Confirmations)) return null;
        held = kind is ContentKind.Pop or ContentKind.Speech ? null : kind;
        return kind;
    }
}

/// <summary>
/// Turns a stream of mono audio into a <see cref="ContentResult"/> about every 1.5 s, the way NavTune for macOS uses
/// SoundAnalysis: YAMNet scores the latest 0.975 s every 0.75 s, each result averages two of those, and scores are
/// smoothed from one result to the next (60 % previous, 40 % new) before <see cref="Classify"/> picks a kind.
/// </summary>
public sealed class ContentAnalyzer
{
    public const int PatchHop = YamNet.SampleRate * 3 / 4;
    private const int PatchesPerResult = 2;

    // Apple's sound classifier knows instruments and voice types; YAMNet knows those (as AudioSet classes) and genres
    // too, so each kind also listens for its genres.
    internal static readonly string[] MusicLabels = ["Music"];
    internal static readonly string[] SpeechLabels = ["Speech"];
    internal static readonly string[] VoiceLabels = ["Singing", "Rapping"];
    internal static readonly (ContentKind Kind, string[] Labels)[] Candidates =
    [
        (ContentKind.Rap, ["Rapping", "Hip hop music"]),
        (ContentKind.Rock, ["Electric guitar", "Rock music", "Heavy metal", "Punk rock", "Grunge", "Progressive rock",
                            "Rock and roll", "Psychedelic rock"]),
        (ContentKind.Electronic, ["Synthesizer", "Scratching (performance technique)", "Electronic music", "House music",
                                  "Techno", "Dubstep", "Drum and bass", "Electronica", "Electronic dance music", "Trance music"]),
        (ContentKind.Classical, ["Orchestra", "Violin, fiddle", "Cello", "Bowed string instrument", "String section", "Harp",
                                 "Classical music", "Opera"]),
        (ContentKind.Jazz, ["Saxophone", "Trumpet", "Brass instrument", "Double bass", "Vibraphone", "Jazz", "Swing music"]),
        (ContentKind.Piano, ["Piano", "Electric piano"]),
        (ContentKind.Acoustic, ["Acoustic guitar", "Ukulele", "Strum", "Folk music", "Bluegrass"]),
    ];

    /// <summary>Every class name the rules use, to check them against the model.</summary>
    internal static IEnumerable<string> AllLabels =>
        MusicLabels.Concat(SpeechLabels).Concat(VoiceLabels).Concat(Candidates.SelectMany(c => c.Labels)).Distinct();

    /// <summary>
    /// How sure the strongest kind must be. Apple's classifier needs 0.25 on one instrument; YAMNet spreads a song
    /// over related classes (Rock music, Grunge, Electric guitar…) with lower scores, so a kind's labels are combined
    /// as a noisy OR and the bar is lower. Measured on real recordings: a rock song 0.18, an acoustic one 0.17,
    /// unrelated kinds 0.00–0.06.
    /// </summary>
    internal const double MinEvidence = 0.15;

    /// <summary>A third of <see cref="MinEvidence"/>: enough to keep a style found earlier in the song.</summary>
    internal const double HintEvidence = MinEvidence / 3;

    /// <summary>
    /// The macOS rules: nothing unless music or speech is clearly there; speech when it outweighs music and nobody
    /// sings or raps; else the instrument or genre heard most clearly; else pop.
    /// </summary>
    public static ContentKind? Classify(Func<string, double> score) => Classify(score, out _);

    /// <inheritdoc cref="Classify(Func{string, double})"/>
    /// <param name="hint">The strongest style if there is at least a faint sign of it (see <see cref="ContentResult.Hint"/>).</param>
    public static ContentKind? Classify(Func<string, double> score, out ContentKind? hint)
    {
        hint = null;
        double Max(string[] labels) => labels.Max(score);

        double music = Max(MusicLabels);
        double speech = Max(SpeechLabels);
        if (Math.Max(music, speech) <= 0.2) return null;
        if (speech > music * 1.5 && Max(VoiceLabels) < 0.3) return ContentKind.Speech;

        var best = Candidates
            .Select(c => (c.Kind, Evidence: 1 - c.Labels.Aggregate(1.0, (p, label) => p * (1 - Math.Clamp(score(label), 0, 1)))))
            .MaxBy(c => c.Evidence);
        if (best.Evidence >= HintEvidence) hint = best.Kind;
        return best.Evidence > MinEvidence ? best.Kind : ContentKind.Pop;
    }

    private readonly YamNet model;
    private readonly Resampler resampler;
    private readonly Dictionary<string, int> labels;
    private readonly float[] window = new float[YamNet.PatchSamples];
    private readonly float[] ordered = new float[YamNet.PatchSamples];
    private readonly float[] patchSum = new float[YamNet.ClassCount];
    private readonly float[] smoothed = new float[YamNet.ClassCount];
    private float[] converted = new float[4096];
    private int write, filled, sincePatch, patches;

    public ContentAnalyzer(YamNet model, int sampleRate)
    {
        this.model = model;
        resampler = new Resampler(sampleRate, YamNet.SampleRate);
        labels = AllLabels.ToDictionary(l => l, l => model.IndexOf(l) is >= 0 and var i
            ? i : throw new InvalidOperationException($"YAMNet has no class named {l}."));
    }

    public int SampleRate => resampler.InputRate;

    /// <summary>Time spent in YAMNet so far, for the log.</summary>
    public TimeSpan ModelTime { get; private set; }
    public int Analyses { get; private set; }

    /// <summary>Drops the audio heard so far (after a gap, so no analysis straddles it); keeps the smoothed scores.</summary>
    public void ClearWindow()
    {
        resampler.Reset();
        filled = sincePatch = 0;
    }

    /// <summary>Feeds mono audio at <see cref="SampleRate"/>; returns the results that fell due (usually none or one).</summary>
    public List<ContentResult> Feed(ReadOnlySpan<float> samples)
    {
        var results = new List<ContentResult>();
        int needed = resampler.MaxOutput(samples.Length);
        if (converted.Length < needed) converted = new float[needed];
        int count = resampler.Process(samples, converted);
        for (int i = 0; i < count; i++)
        {
            window[write] = converted[i];
            write = (write + 1) % window.Length;
            if (filled < window.Length) filled++;
            sincePatch++;
            if (filled == window.Length && sincePatch >= PatchHop)
            {
                sincePatch = 0;
                if (Analyze() is { } result) results.Add(result);
            }
        }
        return results;
    }

    private ContentResult? Analyze()
    {
        // The ring, oldest sample first.
        int tail = window.Length - write;
        Array.Copy(window, write, ordered, 0, tail);
        Array.Copy(window, 0, ordered, tail, write);

        long start = Stopwatch.GetTimestamp();
        var scores = model.Classify(ordered);
        ModelTime += Stopwatch.GetElapsedTime(start);
        Analyses++;

        for (int k = 0; k < scores.Length; k++) patchSum[k] += scores[k];
        if (++patches < PatchesPerResult) return null;
        for (int k = 0; k < scores.Length; k++)
        {
            smoothed[k] = smoothed[k] * 0.6f + patchSum[k] / patches * 0.4f;
            patchSum[k] = 0;
        }
        patches = 0;

        var kind = Classify(label => smoothed[labels[label]], out var hint);
        var top = Enumerable.Range(0, smoothed.Length).OrderByDescending(k => smoothed[k]).Take(5)
            .Select(k => $"{model.ClassNames[k]} {smoothed[k]:0.00}");
        return new ContentResult(kind, string.Join(", ", top), Hint: hint);
    }
}

/// <summary>
/// Listens to the unprocessed input of an <see cref="AudioProcessor"/> on a background thread and reports what
/// is playing. Loads YAMNet the first time it starts. Results come on that thread, tagged with the generation
/// <see cref="Start"/> returned, so a listener can drop those from before a restart.
/// </summary>
public sealed class ContentDetector : IDisposable
{
    /// <summary>Raised on the detector thread after each analysis window, about every 1.5 s while audio plays.</summary>
    public event Action<int, ContentResult>? Detected;

    /// <summary>Diagnostics for the log, raised on the detector thread.</summary>
    public event Action<string>? Message;

    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private Thread? thread;
    private AudioProcessor? source;
    private int generation;
    private bool running, unload, disposed;
    private volatile bool paused;

    /// <summary>While paused (comparing with the original sound), nothing is analyzed.</summary>
    public bool Paused
    {
        get => paused;
        set => paused = value;
    }

    /// <summary>Starts listening afresh (forgetting what was heard) and returns the generation of its results.</summary>
    public int Start(AudioProcessor processor)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            source = processor;
            running = true;
            generation++;
            if (thread is null)
            {
                thread = new Thread(Run) { IsBackground = true, Name = "NavTune content detector", Priority = ThreadPriority.BelowNormal };
                thread.Start();
            }
            wake.Set();
            return generation;
        }
    }

    /// <summary>Stops listening; <paramref name="releaseModel"/> also frees the model's memory.</summary>
    public void Stop(bool releaseModel = false)
    {
        lock (gate)
        {
            if (disposed) return;
            running = false;
            unload |= releaseModel;
            generation++;
            wake.Set();
        }
    }

    public void Dispose()
    {
        Thread? worker;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            worker = thread;
            wake.Set();
        }
        worker?.Join(2000);
    }

    private void Run()
    {
        YamNet? model = null;
        ContentAnalyzer? analyzer = null;
        bool failed = false, gap = false, wasPaused = false;
        int seen = 0;
        long position = 0, lastAudio = 0;
        var chunk = new float[8192];
        var reported = TimeSpan.Zero;

        while (true)
        {
            AudioProcessor? processor;
            int current;
            lock (gate)
            {
                if (disposed) return;
                processor = source;
                current = generation;
                if (!running && unload)
                {
                    model = null;
                    analyzer = null;
                    unload = false;
                }
                if (!running) processor = null;
            }
            if (processor is null || failed)
            {
                wake.WaitOne();
                continue;
            }

            try
            {
                int rate = (int)Math.Round(processor.SampleRate);
                if (current != seen || analyzer is null || analyzer.SampleRate != rate)
                {
                    if (model is null)
                    {
                        long start = Stopwatch.GetTimestamp();
                        model = YamNet.Load();
                        Message?.Invoke($"Content detection: YAMNet loaded in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:0} ms");
                    }
                    analyzer = new ContentAnalyzer(model, rate);
                    position = processor.InputWritePosition;
                    lastAudio = Environment.TickCount64;
                    seen = current;
                    gap = false;
                }

                if (paused)
                {
                    position = processor.InputWritePosition;
                    wasPaused = true;
                }
                else
                {
                    if (wasPaused)
                    {
                        // Don't join what was heard before the comparison with what plays now.
                        analyzer.ClearWindow();
                        wasPaused = false;
                    }
                    int count;
                    bool heard = false;
                    while ((count = processor.ReadInput(ref position, chunk)) > 0)
                    {
                        heard = true;
                        foreach (var result in analyzer.Feed(chunk.AsSpan(0, count)))
                            if (Volatile.Read(ref generation) == current) Detected?.Invoke(current, result);
                    }
                    long now = Environment.TickCount64;
                    if (heard)
                    {
                        lastAudio = now;
                        gap = false;
                    }
                    else if (!gap && now - lastAudio > 1000)
                    {
                        // Nothing plays (Windows sends no packets then): the next analysis must not straddle the silence.
                        analyzer.ClearWindow();
                        gap = true;
                        if (Volatile.Read(ref generation) == current)
                            Detected?.Invoke(current, new ContentResult(null, "pause", IsPause: true));
                    }
                    if (analyzer.ModelTime - reported >= TimeSpan.FromMinutes(1) || (reported == TimeSpan.Zero && analyzer.Analyses == 4))
                    {
                        reported = analyzer.ModelTime;
                        Message?.Invoke($"Content detection: {analyzer.ModelTime.TotalMilliseconds / analyzer.Analyses:0.0} ms per analysis");
                    }
                }
            }
            catch (Exception e)
            {
                // A broken model file must not take anything else down; detection just stays off.
                failed = true;
                Message?.Invoke($"Content detection stopped: {e}");
            }
            wake.WaitOne(200);
        }
    }
}
