using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NavTune.Interop;

namespace NavTune.Audio;

/// <summary>An app that has audio sessions, grouped by executable (all of Chrome's processes are one app).</summary>
public sealed record AudioApp(string AppId, string Name, string? ExePath, bool IsPlaying, bool IsRunning);

/// <summary>An app's Windows mute and volume, as NavTune found them before changing them.</summary>
public sealed record AppVolume(bool Muted, float Volume);

/// <summary>
/// Windows per-app audio: lists the apps that use audio and sets their volume and mute through the same session
/// controls as the Windows volume mixer.
/// </summary>
public static class AppSessions
{
    private static readonly Dictionary<int, (string AppId, string Name, string? Path)> processCache = [];
    private static readonly object cacheLock = new();
    private static readonly int ownProcessId = Environment.ProcessId;

    private sealed record Session(int ProcessId, string AppId, string Name, string? Path, AudioSessionState State);

    /// <summary>Apps with a live audio session on any active playback device, playing ones first.</summary>
    public static List<AudioApp> List()
    {
        var apps = new Dictionary<string, AudioApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in Enumerate(null, null))
        {
            bool playing = session.State == AudioSessionState.Active;
            if (apps.TryGetValue(session.AppId, out var existing))
            {
                if (playing && !existing.IsPlaying) apps[session.AppId] = existing with { IsPlaying = true };
            }
            else
            {
                apps[session.AppId] = new AudioApp(session.AppId, session.Name, session.Path, playing, true);
            }
        }
        return apps.Values
            .OrderByDescending(a => a.IsPlaying)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Sets mute and volume for every session of the given apps; apps not listed are left alone.
    /// Returns the apps that had a session to apply it to (Windows remembers a session's volume and mute for the
    /// app's next launch, so an app that isn't running can only be fixed once it plays again).
    /// When <paramref name="originals"/> is given, an app's mute and volume are recorded there before NavTune first
    /// changes them, so they can be put back exactly.
    /// </summary>
    public static HashSet<string> Apply(IReadOnlyDictionary<string, (bool Muted, float Volume)> apps,
        IDictionary<string, AppVolume>? originals = null)
    {
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (apps.Count == 0) return applied;
        Enumerate(null, (session, volume) =>
        {
            if (!apps.TryGetValue(session.AppId, out var target)) return;
            if (volume.GetMute(out bool muted) < 0 || volume.GetMasterVolume(out float level) < 0) return;
            if (originals is not null && !originals.ContainsKey(session.AppId)) originals[session.AppId] = new AppVolume(muted, level);
            var context = Guid.Empty;
            bool ok = true;
            if (muted != target.Muted) ok &= volume.SetMute(target.Muted, ref context) >= 0;
            if (Math.Abs(level - target.Volume) > 0.001f) ok &= volume.SetMasterVolume(Math.Clamp(target.Volume, 0, 1), ref context) >= 0;
            if (ok) applied.Add(session.AppId);
        });
        return applied;
    }

    private static List<Session> Enumerate(string? deviceId, Action<Session, ISimpleAudioVolume>? visit)
    {
        var result = new List<Session>();
        var enumerator = AudioDevices.CreateEnumerator();
        try
        {
            if (enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceState.Active, out var collection) < 0) return result;
            try
            {
                collection.GetCount(out int count);
                var seenPids = new HashSet<int>();
                for (int i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) < 0) continue;
                    try
                    {
                        if (deviceId is not null && (device.GetId(out var id) < 0 || id != deviceId)) continue;
                        VisitDevice(device, result, seenPids, visit);
                    }
                    finally { AudioDevices.Release(device); }
                }
                PruneCache(seenPids);
            }
            finally { AudioDevices.Release(collection); }
        }
        finally { AudioDevices.Release(enumerator); }
        return result;
    }

    private static void VisitDevice(IMMDevice device, List<Session> result, HashSet<int> seenPids, Action<Session, ISimpleAudioVolume>? visit)
    {
        var iid = Iids.AudioSessionManager2;
        if (device.Activate(ref iid, NativeMethods.CLSCTX_ALL, IntPtr.Zero, out var managerObject) < 0) return;
        var manager = (IAudioSessionManager2)managerObject;
        try
        {
            if (manager.GetSessionEnumerator(out var sessions) < 0) return;
            try
            {
                sessions.GetCount(out int count);
                for (int s = 0; s < count; s++)
                {
                    if (sessions.GetSession(s, out var control) < 0) continue;
                    try
                    {
                        if (control.IsSystemSoundsSession() == Hr.S_OK) continue;
                        if (control.GetState(out var state) < 0 || state == AudioSessionState.Expired) continue;
                        if (control.GetProcessId(out uint pid) < 0 || pid == 0 || pid == ownProcessId) continue;
                        seenPids.Add((int)pid);
                        var (appId, name, path) = Describe((int)pid);
                        if (appId.Length == 0) continue;
                        var session = new Session((int)pid, appId, name, path, state);
                        result.Add(session);
                        if (visit is not null && control is ISimpleAudioVolume volume) visit(session, volume);
                    }
                    finally { AudioDevices.Release(control); }
                }
            }
            finally { AudioDevices.Release(sessions); }
        }
        finally { AudioDevices.Release(manager); }
    }

    /// <summary>Executable name (the app id), a friendly name and the path of a process; cached per process.</summary>
    public static (string AppId, string Name, string? Path) Describe(int pid)
    {
        lock (cacheLock)
        {
            if (processCache.TryGetValue(pid, out var cached)) return cached;
        }
        string? path = ProcessPath(pid);
        string appId, name;
        if (path is not null)
        {
            appId = System.IO.Path.GetFileName(path).ToLowerInvariant();
            name = FriendlyName(path) ?? System.IO.Path.GetFileNameWithoutExtension(path);
        }
        else
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                appId = process.ProcessName.ToLowerInvariant() + ".exe";
                name = process.ProcessName;
            }
            catch
            {
                appId = "";
                name = "";
            }
        }
        var entry = (appId, name, path);
        lock (cacheLock) processCache[pid] = entry;
        return entry;
    }

    private static void PruneCache(HashSet<int> alive)
    {
        lock (cacheLock)
        {
            foreach (int pid in processCache.Keys.Where(p => !alive.Contains(p)).ToList()) processCache.Remove(pid);
        }
    }

    private static string? FriendlyName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            string? description = info.FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description) && description.Length <= 48) return description;
            string? product = info.ProductName?.Trim();
            return string.IsNullOrEmpty(product) ? null : product;
        }
        catch
        {
            return null;
        }
    }

    private static string? ProcessPath(int pid)
    {
        IntPtr handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>Calls back whenever an app opens an audio session on any playback device, so rules apply at once.</summary>
[ComVisible(true)]
public sealed class SessionWatcher : IAudioSessionNotification, IDisposable
{
    private readonly List<IAudioSessionManager2> managers = [];

    public event Action? SessionCreated;

    public SessionWatcher()
    {
        var enumerator = AudioDevices.CreateEnumerator();
        try
        {
            if (enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceState.Active, out var collection) < 0) return;
            try
            {
                collection.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) < 0) continue;
                    try
                    {
                        var iid = Iids.AudioSessionManager2;
                        if (device.Activate(ref iid, NativeMethods.CLSCTX_ALL, IntPtr.Zero, out var o) < 0) continue;
                        var manager = (IAudioSessionManager2)o;
                        // Windows only notifies clients that have enumerated the sessions once.
                        if (manager.GetSessionEnumerator(out var sessions) >= 0) AudioDevices.Release(sessions);
                        if (manager.RegisterSessionNotification(this) >= 0) managers.Add(manager);
                        else AudioDevices.Release(manager);
                    }
                    finally { AudioDevices.Release(device); }
                }
            }
            finally { AudioDevices.Release(collection); }
        }
        finally { AudioDevices.Release(enumerator); }
    }

    int IAudioSessionNotification.OnSessionCreated(IntPtr newSession)
    {
        SessionCreated?.Invoke();
        return 0;
    }

    public void Dispose()
    {
        foreach (var manager in managers)
        {
            manager.UnregisterSessionNotification(this);
            AudioDevices.Release(manager);
        }
        managers.Clear();
    }
}
