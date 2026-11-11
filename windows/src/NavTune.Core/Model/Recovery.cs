using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using NavTune.Audio;
using NavTune.Interop;

namespace NavTune.Model;

/// <summary>What NavTune changed in Windows and must undo when it stops, even after a crash or a power cut.</summary>
public sealed class SystemChanges
{
    /// <summary>NavTune made (or is making) the virtual device the Windows default. Cleared only once the default is
    /// verified to be back on a real device.</summary>
    public bool OwnsDefault { get; set; }
    public string? VirtualDeviceId { get; set; }
    /// <summary>The real device to give the default back to.</summary>
    public string? RestoreDeviceId { get; set; }
    /// <summary>Apps whose Windows volume or mute NavTune changed (or still has to give back).</summary>
    public List<string> TouchedApps { get; set; } = [];
    /// <summary>Each touched app's mute and volume before NavTune first changed them.</summary>
    public Dictionary<string, AppVolume> OriginalApps { get; set; } = [];

    [JsonIgnore] public bool IsEmpty => !OwnsDefault && TouchedApps.Count == 0;

    /// <summary>What an app goes back to: how NavTune found it, or unmuted at full volume if it never saw it.</summary>
    public (bool Muted, float Volume) OriginalOf(string appId) =>
        OriginalApps.TryGetValue(appId, out var original) ? (original.Muted, original.Volume) : (false, 1f);
}

/// <summary>
/// The Windows counterpart of the macOS guarantee "if NavTune quits or crashes, audio comes back on its own":
/// NavTune records every change it makes to the system before making it, undoes them when it stops, a small watchdog
/// process undoes them if NavTune ends any other way (crash, End task), and a sign-in task covers a power cut.
/// </summary>
public static class Recovery
{
    public const string InstanceMutexName = @"Local\NavTune.Instance";
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RunOnceValue = "NavTuneRestoreAudio";

    public static string StatePath => Path.Combine(SettingsStore.Folder, "system-changes.json");

    public static SystemChanges Load()
    {
        try
        {
            var changes = File.Exists(StatePath)
                ? JsonSerializer.Deserialize<SystemChanges>(File.ReadAllText(StatePath), SettingsStore.Options) ?? new SystemChanges()
                : new SystemChanges();
            changes.TouchedApps ??= [];
            changes.OriginalApps = new Dictionary<string, AppVolume>(changes.OriginalApps ?? [], StringComparer.OrdinalIgnoreCase);
            return changes;
        }
        catch
        {
            return new SystemChanges();
        }
    }

    public static void Save(SystemChanges changes)
    {
        try { SettingsStore.WriteAtomically(StatePath, JsonSerializer.Serialize(changes, SettingsStore.Options)); }
        catch (Exception e) { Trace.TraceWarning($"NavTune could not record system changes: {e.Message}"); }
    }

    /// <summary>
    /// Gives the Windows default (console and multimedia) back to a real device. Returns true, and clears
    /// <see cref="SystemChanges.OwnsDefault"/>, only once neither role is on the virtual device any more; otherwise
    /// the record stays so a later attempt (a device coming back, the next start, the watchdog) can finish the job.
    /// </summary>
    public static bool ReleaseDefault(SystemChanges changes, string? preferredTarget = null)
    {
        if (!changes.OwnsDefault) return true;
        if (changes.VirtualDeviceId is not { } virtualId)
        {
            changes.OwnsDefault = false;
            return true;
        }
        try
        {
            bool OnVirtual() =>
                AudioDevices.DefaultRenderDeviceId(ERole.Console) == virtualId ||
                AudioDevices.DefaultRenderDeviceId(ERole.Multimedia) == virtualId;

            if (OnVirtual())
            {
                var real = AudioDevices.RenderDevices().Where(d => d.IsActive && !d.IsVirtual).ToList();
                string? target = real.FirstOrDefault(d => d.Id == preferredTarget)?.Id
                                 ?? real.FirstOrDefault(d => d.Id == changes.RestoreDeviceId)?.Id
                                 ?? BestRealDevice(real)?.Id;
                if (target is null) return false;
                DefaultDevice.Set(target);
                if (OnVirtual()) return false;
            }
            changes.OwnsDefault = false;
            return true;
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"NavTune could not restore the default device: {e.Message}");
            return false;
        }
    }

    /// <summary>Gives the default device back and restores the apps NavTune muted or turned down.</summary>
    public static void Undo(SystemChanges changes)
    {
        ReleaseDefault(changes);
        if (changes.TouchedApps.Count > 0)
        {
            try
            {
                var apps = changes.TouchedApps.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var restored = AppSessions.Apply(apps.ToDictionary(a => a, changes.OriginalOf, StringComparer.OrdinalIgnoreCase));
                // Apps that aren't running keep their entry: NavTune restores them the next time they play.
                changes.TouchedApps = apps.Where(a => !restored.Contains(a)).ToList();
                foreach (var app in restored) changes.OriginalApps.Remove(app);
            }
            catch (Exception e) { Trace.TraceWarning($"NavTune could not restore app volumes: {e.Message}"); }
        }
        if (!changes.OwnsDefault) RestoreAtSignIn(false);
        Save(changes);
    }

    /// <summary>
    /// While NavTune owns the default device, a one-time sign-in task gives it back in case the PC loses power
    /// (nothing else gets a chance to run then). NavTune removes the task when it gives the default back itself.
    /// </summary>
    public static void RestoreAtSignIn(bool enabled)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is null || !Path.GetFileName(exe).Equals("NavTune.exe", StringComparison.OrdinalIgnoreCase)) return;
            using var key = Registry.CurrentUser.CreateSubKey(RunOnceKey);
            if (enabled) key.SetValue(RunOnceValue, $"\"{exe}\" --restore-audio");
            else key.DeleteValue(RunOnceValue, throwOnMissingValue: false);
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"NavTune could not update the sign-in restore task: {e.Message}");
        }
    }

    /// <summary>The device a user most likely wants when nothing else is known: personal devices before monitors.</summary>
    public static AudioDevice? BestRealDevice(IEnumerable<AudioDevice> devices) => devices
        .Where(d => d.IsActive && !d.IsVirtual)
        .OrderBy(d => d.Transport switch
        {
            DeviceTransport.Bluetooth => 0,
            DeviceTransport.Usb => 1,
            DeviceTransport.BuiltIn => 2,
            DeviceTransport.Other => 3,
            DeviceTransport.Hdmi => 4,
            _ => 5,
        })
        .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
        .FirstOrDefault();

    public static bool IsNavTuneRunning()
    {
        if (!Mutex.TryOpenExisting(InstanceMutexName, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    /// <summary>
    /// Runs as "NavTune.exe --watchdog &lt;pid&gt;": waits for NavTune to end, then undoes what it left behind.
    /// If no real device is available yet (e.g. the only headphones are off), it keeps trying for half an hour.
    /// </summary>
    public static int RunWatchdog(int parentProcessId)
    {
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            parent.WaitForExit();
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        Thread.Sleep(400);
        for (int attempt = 0; attempt < 600; attempt++)
        {
            // A new NavTune took over in the meantime: it owns the system now.
            if (IsNavTuneRunning()) return 0;
            var changes = Load();
            if (changes.IsEmpty) return 0;
            Undo(changes);
            if (!changes.OwnsDefault) return 0;
            Thread.Sleep(3000);
        }
        return 0;
    }
}
