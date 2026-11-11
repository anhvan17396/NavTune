using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace NavTune.Infrastructure;

/// <summary>Number formats shared with the macOS app ("+3.0 dB", "120%", "1k", "2.50 kHz").</summary>
public static class Format
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string DB(double v) => v.ToString("+0.0;-0.0;+0.0", Invariant) + " dB";
    public static string Signed(double v) => v.ToString("+0.0;-0.0;+0.0", Invariant);
    public static string Percent(double v) => $"{Math.Round(v * 100, MidpointRounding.AwayFromZero):0}%";

    public static string Frequency(double f) => f >= 1000 ? $"{(int)(f / 1000)}k" : $"{(int)f}";

    public static string Hertz(double f) => f >= 1000
        ? (f / 1000).ToString(f >= 10000 ? "0.0" : "0.00", Invariant) + " kHz"
        : $"{(int)Math.Round(f, MidpointRounding.AwayFromZero)} Hz";

    /// <summary>Rounds to a slider step the way the macOS app does (half away from zero), then clamps.</summary>
    public static float Snap(double value, double step, double min, double max) =>
        (float)Math.Clamp(Math.Round(value / step, MidpointRounding.AwayFromZero) * step, min, max);
}

/// <summary>"Launch at login": the per-user Run key, pointing at this executable.</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NavTune";

    private static string Command => $"\"{Environment.ProcessPath}\" --background";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Keeps the entry pointing at the current executable if the app was moved.</summary>
    public static void Refresh()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string value && value != Command) key.SetValue(ValueName, Command);
    }
}

/// <summary>Plain-text activity log in %LOCALAPPDATA%\NavTune (the macOS app logs to the unified log).</summary>
public static class Log
{
    private static readonly object gate = new();
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NavTune", "navtune.log");

    public static void Info(string message) => Write("INFO ", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
        Trace.WriteLine(line);
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > 1_000_000) File.Move(Path, Path + ".old", overwrite: true);
                File.AppendAllText(Path, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never break the app.
            }
        }
    }
}

public static class Shell
{
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) { Log.Error($"Could not open {target}: {e.Message}"); }
    }
}
