using System.Diagnostics;
using NavTune.Model;

namespace NavTune;

public static class Program
{
    public const string ShowEventName = @"Local\NavTune.Show";
    public const string QuitEventName = @"Local\NavTune.Quit";

    [STAThread]
    public static int Main(string[] args)
    {
        // Portable or test installs can keep their settings elsewhere.
        if (Environment.GetEnvironmentVariable("NAVTUNE_DATA") is { Length: > 0 } data) SettingsStore.Folder = data;

        if (args.Contains("--quit"))
        {
            // Asks a running NavTune to quit cleanly (gives the default device back), e.g. from an uninstaller.
            try
            {
                using var quit = EventWaitHandle.OpenExisting(QuitEventName);
                quit.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            return 0;
        }
        // Helper modes run before anything else is loaded.
        if (ValueAfter(args, "--spawn-watchdog") is { } owner)
        {
            // Started by NavTune; starts the real watchdog and exits at once, so the watchdog has no living parent:
            // ending NavTune's process tree (Task Manager's End task, taskkill /T) then leaves it running.
            StartHelper($"--watchdog {owner}");
            return 0;
        }
        if (ValueAfter(args, "--watchdog") is { } watched && int.TryParse(watched, out int parent))
            return Recovery.RunWatchdog(parent);
        if (args.Contains("--restore-audio"))
        {
            // A running NavTune owns the system state and gives it back itself when it quits.
            if (Recovery.IsNavTuneRunning()) return 0;
            var changes = Recovery.Load();
            if (!changes.IsEmpty) Recovery.Undo(changes);
            else Recovery.RestoreAtSignIn(false);
            return 0;
        }
        if (ValueAfter(args, "--screenshots") is { } folder)
        {
            // Isolated from a running NavTune: own settings, no audio, no watchdog.
            var preview = new App { ScreenshotFolder = folder, ScreenshotOptions = args };
            preview.InitializeComponent();
            return preview.Run();
        }

        using var instance = new Mutex(true, Recovery.InstanceMutexName, out bool first);
        if (!first)
        {
            // NavTune is already running (probably in the tray): bring its window up instead.
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            return 0;
        }

        StartWatchdog();
        var app = new App { StartHidden = args.Contains("--background") };
        app.InitializeComponent();
        return app.Run();
    }

    private static string? ValueAfter(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Starts the helper that undoes NavTune's system changes if this process ends unexpectedly.</summary>
    private static void StartWatchdog() => StartHelper($"--spawn-watchdog {Environment.ProcessId}");

    private static void StartHelper(string arguments)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is null || !exe.EndsWith("NavTune.exe", StringComparison.OrdinalIgnoreCase)) return;
            using var _ = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"NavTune helper did not start ({arguments}): {e.Message}");
        }
    }
}
