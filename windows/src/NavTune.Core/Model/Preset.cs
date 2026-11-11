using System.Text.Json.Serialization;
using static NavTune.Model.L10n;

namespace NavTune.Model;

/// <summary>
/// An EQ curve. Gains in dB for bands 32, 64, 125, 250, 500, 1k, 2k, 4k, 8k, 16k Hz;
/// user presets saved from the parametric editor also carry <see cref="Bands"/>.
/// The JSON shape (and the SF Symbol icon names) match the macOS app, so exported files work on both.
/// </summary>
public sealed class Preset
{
    public const string UserPrefix = "user.";
    public const string CustomId = "custom";

    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("icon")] public string Icon { get; set; } = "star";
    [JsonPropertyName("gains")] public float[] Gains { get; set; } = new float[SoundSettings.BandCount];
    [JsonPropertyName("preamp")] public float Preamp { get; set; }

    [JsonPropertyName("nameEN"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NameEN { get; set; }

    [JsonPropertyName("bands"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<EQBand>? Bands { get; set; }

    [JsonIgnore] public bool IsBuiltIn => !Id.StartsWith(UserPrefix, StringComparison.Ordinal);
    [JsonIgnore] public bool IsParametric => Bands is not null;
    [JsonIgnore] public string DisplayName => Tr(Name, NameEN ?? Name);

    public static Preset User(string name, float[] gains, float preamp, IEnumerable<EQBand>? bands = null) => new()
    {
        Id = UserPrefix + Guid.NewGuid().ToString().ToUpperInvariant(),
        Name = name,
        Icon = bands is null ? "star" : "dial.medium",
        Gains = (float[])gains.Clone(),
        Preamp = preamp,
        Bands = bands?.Select(b => b.Clone()).ToList(),
    };

    /// <summary>Built-in presets get automatic headroom so they don't constantly hit the limiter.</summary>
    private static Preset BuiltIn(string id, string name, string nameEN, string icon, float[] gains)
    {
        float boost = Math.Max(0, gains.Max());
        return new Preset
        {
            Id = id,
            Name = name,
            NameEN = nameEN,
            Icon = icon,
            Gains = gains,
            Preamp = -MathF.Round(boost * 0.6f, MidpointRounding.AwayFromZero),
        };
    }

    public static readonly IReadOnlyList<Preset> BuiltIns =
    [
        BuiltIn("flat", "Gốc", "Flat", "minus", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        BuiltIn("vpop", "V-Pop", "V-Pop", "music.mic", [1, 2, 2, 1, 0, 1, 2, 2, 2, 2]),
        BuiltIn("pop", "Pop", "Pop", "star", [-1, 1, 3, 4, 2, 0, -1, 0, 1, 2]),
        BuiltIn("ballad", "Ballad", "Ballad", "music.note", [2, 2, 1, 0, 1, 2, 2, 1, 1, 1]),
        BuiltIn("bolero", "Bolero / Trữ tình", "Bolero", "heart", [2, 2, 1, 0, 1, 2, 3, 2, 1, 0]),
        BuiltIn("rock", "Rock", "Rock", "guitars", [5, 4, 3, 1, -1, -1, 1, 3, 4, 5]),
        BuiltIn("metal", "Metal", "Metal", "flame", [6, 5, 2, 0, -2, -1, 2, 4, 5, 5]),
        BuiltIn("edm", "EDM", "EDM", "bolt", [6, 5, 3, 0, -1, 0, 1, 2, 4, 5]),
        BuiltIn("dance", "Dance / Remix", "Dance / Remix", "figure.dance", [5, 4, 2, 0, 0, 1, 1, 2, 3, 4]),
        BuiltIn("rap", "Rap / Hip-hop", "Rap / Hip-hop", "mic", [6, 5, 3, 1, -1, -1, 1, 1, 2, 3]),
        BuiltIn("rnb", "R&B", "R&B", "music.quarternote.3", [4, 5, 3, 1, -1, 0, 1, 1, 2, 3]),
        BuiltIn("kpop", "K-Pop", "K-Pop", "sparkles", [3, 3, 1, 0, 0, 1, 2, 3, 3, 3]),
        BuiltIn("lofi", "Lo-fi / Chill", "Lo-fi / Chill", "cloud.moon", [3, 3, 2, 1, 0, -1, -2, -2, -3, -4]),
        BuiltIn("jazz", "Jazz", "Jazz", "music.note.list", [3, 2, 1, 2, -1, -1, 0, 1, 2, 3]),
        BuiltIn("classical", "Cổ điển", "Classical", "building.columns", [4, 3, 2, 1, -1, -1, 0, 2, 3, 4]),
        BuiltIn("acoustic", "Acoustic", "Acoustic", "leaf", [3, 3, 2, 1, 1, 1, 2, 2, 2, 1]),
        BuiltIn("piano", "Piano", "Piano", "pianokeys", [2, 1, 0, 1, 2, 1, 2, 3, 2, 1]),
        BuiltIn("vocal", "Giọng hát", "Vocal", "person.wave.2", [-2, -1, 0, 2, 4, 4, 3, 1, 0, -1]),
        BuiltIn("bass", "Tăng Bass", "Bass Boost", "speaker.wave.3", [7, 6, 4, 2, 0, 0, 0, 0, 0, 0]),
        BuiltIn("treble", "Tăng Treble", "Treble Boost", "waveform.path", [0, 0, 0, 0, 0, 1, 2, 4, 5, 6]),
        BuiltIn("movie", "Xem phim", "Movies", "film", [4, 3, 1, 0, 1, 2, 2, 2, 2, 2]),
        BuiltIn("game", "Chơi game", "Gaming", "gamecontroller", [3, 2, 0, 0, 1, 2, 3, 3, 2, 1]),
        BuiltIn("podcast", "Podcast / Lời nói", "Podcast / Speech", "waveform", [-4, -2, 0, 2, 3, 3, 2, 1, 0, -2]),
        BuiltIn("night", "Nghe đêm", "Late Night", "moon.stars", [-3, -2, -1, 0, 1, 1, 1, 0, -1, -2]),
    ];
}
