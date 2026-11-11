using System.Text.Json.Serialization;

namespace NavTune.Model;

/// <summary>
/// How NavTune treats one app on Windows. Apps are identified by their executable name ("chrome.exe"),
/// so every process of a multi-process app (browsers, Electron apps) shares one rule.
/// </summary>
public sealed class AppRule : IEquatable<AppRule>
{
    /// <summary>Lower-case executable file name.</summary>
    [JsonPropertyName("appId")] public string AppId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Silenced on the speakers and headphones while NavTune is on.</summary>
    [JsonPropertyName("muted")] public bool Muted { get; set; }
    /// <summary>App volume 0...1 (Windows per-app volume); 1 leaves the app as it is.</summary>
    [JsonPropertyName("volume")] public float Volume { get; set; } = 1;

    [JsonIgnore] public bool IsNeutral => !Muted && Volume >= 0.999f;

    public AppRule Clone() => (AppRule)MemberwiseClone();

    public bool Equals(AppRule? other) =>
        other is not null && AppId == other.AppId && Name == other.Name && Muted == other.Muted && Volume == other.Volume;

    public override bool Equals(object? obj) => Equals(obj as AppRule);
    public override int GetHashCode() => AppId.GetHashCode(StringComparison.Ordinal);
}
