using System.Text.Json;
using System.Text.Json.Serialization;
using NavTune.Audio;

namespace NavTune.Model;

/// <summary>Everything NavTune remembers between runs (the macOS app keeps the same things in UserDefaults).</summary>
public sealed class AppConfig
{
    public const string GlobalSoundKey = "global";

    public bool IsOn { get; set; } = true;
    public bool HasOnboarded { get; set; }
    /// <summary>Output device chosen in NavTune; null follows the Windows default device.</summary>
    public string? PreferredDeviceId { get; set; }
    public bool PerDeviceProfiles { get; set; } = true;
    public string Language { get; set; } = "system";
    /// <summary>Virtual device to capture from; null picks the best one found.</summary>
    public string? VirtualDeviceId { get; set; }
    /// <summary>The real device that was last the Windows default, to return to when it comes back.</summary>
    public string? LastRealDefaultId { get; set; }
    public LatencyMode Latency { get; set; } = LatencyMode.Balanced;
    public bool CloseToTray { get; set; } = true;
    /// <summary>Picks the preset that fits what is playing (speech, rock, piano…), like "Auto preset by content" on macOS.</summary>
    public bool AutoPreset { get; set; }
    public bool TrayHintShown { get; set; }
    public List<Preset> UserPresets { get; set; } = [];
    public List<AppRule> AppRules { get; set; } = [];
    /// <summary>Sound settings per output device id, or under <see cref="GlobalSoundKey"/>.</summary>
    public Dictionary<string, SoundSettings> Sound { get; set; } = [];
}

/// <summary>Reads and writes %APPDATA%\NavTune\settings.json atomically.</summary>
public static class SettingsStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NavTune");

    public static string SettingsPath => Path.Combine(Folder, "settings.json");

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppConfig();
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(SettingsPath), Options) ?? new AppConfig();
            config.UserPresets ??= [];
            config.AppRules ??= [];
            config.Sound ??= [];
            config.UserPresets.RemoveAll(p => p is null || p.Gains is null || p.Gains.Length != SoundSettings.BandCount);
            config.AppRules.RemoveAll(r => r is null || string.IsNullOrEmpty(r.AppId));
            return config;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Keep the unreadable file for inspection and start fresh rather than refusing to run.
            try { File.Copy(SettingsPath, SettingsPath + ".broken", overwrite: true); } catch { }
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config) => WriteAtomically(SettingsPath, JsonSerializer.Serialize(config, Options));

    public static void WriteAtomically(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}
