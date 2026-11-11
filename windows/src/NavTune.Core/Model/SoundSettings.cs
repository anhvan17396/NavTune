using System.Text.Json.Serialization;
using NavTune.Audio;
using static NavTune.Model.L10n;

namespace NavTune.Model;

[JsonConverter(typeof(JsonStringEnumConverter<EQMode>))]
public enum EQMode
{
    [JsonStringEnumMemberName("graphic")] Graphic,
    [JsonStringEnumMemberName("parametric")] Parametric,
}

[JsonConverter(typeof(JsonStringEnumConverter<FilterKind>))]
public enum FilterKind
{
    [JsonStringEnumMemberName("peak")] Peak,
    [JsonStringEnumMemberName("lowShelf")] LowShelf,
    [JsonStringEnumMemberName("highShelf")] HighShelf,
    [JsonStringEnumMemberName("lowCut")] LowCut,
    [JsonStringEnumMemberName("highCut")] HighCut,
}

public static class FilterKindExtensions
{
    public static string Title(this FilterKind kind) => kind switch
    {
        FilterKind.Peak => Tr("Chuông (Peak)", "Bell (Peak)"),
        FilterKind.LowShelf => Tr("Kệ trầm (Low shelf)", "Low shelf"),
        FilterKind.HighShelf => Tr("Kệ cao (High shelf)", "High shelf"),
        FilterKind.LowCut => Tr("Cắt trầm (Low cut)", "Low cut"),
        _ => Tr("Cắt cao (High cut)", "High cut"),
    };

    public static bool HasGain(this FilterKind kind) => kind is not (FilterKind.LowCut or FilterKind.HighCut);
}

/// <summary>One band of the parametric equalizer. JSON matches the macOS app, so presets move between them.</summary>
public sealed class EQBand : IEquatable<EQBand>
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("kind")] public FilterKind Kind { get; set; } = FilterKind.Peak;
    [JsonPropertyName("frequency")] public double Frequency { get; set; } = 1000;
    [JsonPropertyName("gain")] public float Gain { get; set; }
    [JsonPropertyName("q")] public double Q { get; set; } = 1;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    public EQBand Clone() => (EQBand)MemberwiseClone();

    public Biquad ToBiquad(double sampleRate)
    {
        if (!Enabled) return Biquad.Identity;
        double gainDB = Gain;
        return Kind switch
        {
            FilterKind.Peak => Biquad.Peaking(Frequency, gainDB, Q, sampleRate),
            FilterKind.LowShelf => Biquad.LowShelf(Frequency, gainDB, sampleRate, Q),
            FilterKind.HighShelf => Biquad.HighShelf(Frequency, gainDB, sampleRate, Q),
            FilterKind.LowCut => Biquad.HighPass(Frequency, sampleRate, Q),
            _ => Biquad.LowPass(Frequency, sampleRate, Q),
        };
    }

    public bool Equals(EQBand? other) =>
        other is not null && Id == other.Id && Kind == other.Kind && Frequency == other.Frequency &&
        Gain == other.Gain && Q == other.Q && Enabled == other.Enabled;

    public override bool Equals(object? obj) => Equals(obj as EQBand);
    public override int GetHashCode() => Id.GetHashCode();
}

/// <summary>Everything that shapes the sound; stored per output device.</summary>
public sealed class SoundSettings : IEquatable<SoundSettings>, IJsonOnDeserialized
{
    public static readonly int BandCount = AudioProcessor.Frequencies.Length;
    public const int MaxParametricBands = 10;

    public static EQBand[] DefaultBands() =>
    [
        new() { Kind = FilterKind.LowShelf, Frequency = 80, Q = Biquad.ButterworthQ },
        new() { Kind = FilterKind.Peak, Frequency = 250, Q = 1 },
        new() { Kind = FilterKind.Peak, Frequency = 1000, Q = 1 },
        new() { Kind = FilterKind.Peak, Frequency = 3000, Q = 1 },
        new() { Kind = FilterKind.Peak, Frequency = 6000, Q = 1.2 },
        new() { Kind = FilterKind.HighShelf, Frequency = 10000, Q = Biquad.ButterworthQ },
    ];

    [JsonPropertyName("presetID")] public string PresetId { get; set; } = "flat";
    [JsonPropertyName("mode")] public EQMode Mode { get; set; } = EQMode.Graphic;
    [JsonPropertyName("gains")] public float[] Gains { get; set; } = new float[BandCount];
    [JsonPropertyName("bands")] public List<EQBand> Bands { get; set; } = [.. DefaultBands()];
    [JsonPropertyName("preamp")] public float Preamp { get; set; }
    /// <summary>Low shelf at 90 Hz, dB.</summary>
    [JsonPropertyName("bass")] public float Bass { get; set; }
    /// <summary>High shelf at 9 kHz, dB.</summary>
    [JsonPropertyName("clarity")] public float Clarity { get; set; }
    /// <summary>Stereo width: 0 = mono, 1 = original, 2 = extra wide.</summary>
    [JsonPropertyName("width")] public float Width { get; set; } = 1;
    /// <summary>Loudness leveler amount, 0...1.</summary>
    [JsonPropertyName("loudness")] public float Loudness { get; set; }
    /// <summary>Output boost, dB (protected by the limiter).</summary>
    [JsonPropertyName("boost")] public float Boost { get; set; }

    /// <summary>Repairs settings written by older versions or edited by hand.</summary>
    public void OnDeserialized()
    {
        if (Gains is null || Gains.Length != BandCount) Gains = new float[BandCount];
        Bands ??= [.. DefaultBands()];
        if (Bands.Count > MaxParametricBands) Bands = Bands.Take(MaxParametricBands).ToList();
        Bands.RemoveAll(b => b is null);
        PresetId ??= "flat";
    }

    public SoundSettings Clone()
    {
        var copy = (SoundSettings)MemberwiseClone();
        copy.Gains = (float[])Gains.Clone();
        copy.Bands = Bands.Select(b => b.Clone()).ToList();
        return copy;
    }

    public void ResetEffects()
    {
        Bass = 0;
        Clarity = 0;
        Width = 1;
        Loudness = 0;
        Boost = 0;
    }

    public bool Equals(SoundSettings? other) =>
        other is not null && PresetId == other.PresetId && Mode == other.Mode &&
        Gains.AsSpan().SequenceEqual(other.Gains) && Bands.SequenceEqual(other.Bands) &&
        Preamp == other.Preamp && Bass == other.Bass && Clarity == other.Clarity &&
        Width == other.Width && Loudness == other.Loudness && Boost == other.Boost;

    public override bool Equals(object? obj) => Equals(obj as SoundSettings);
    public override int GetHashCode() => HashCode.Combine(PresetId, Mode, Preamp, Bass, Clarity, Width, Loudness, Boost);
}
