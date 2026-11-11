using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NavTune.Audio;
using NavTune.Model;
using NavTune.ViewModels;
using NavTune.Views;

namespace NavTune.Infrastructure;

/// <summary>
/// "NavTune.exe --screenshots &lt;folder&gt; [vi|en] [light|dark]": renders every page to PNG with throwaway settings
/// and without touching any audio device. Used to check the UI and for documentation.
/// </summary>
public static class Screenshots
{
    public static void Run(App app, string folder, string[] options)
    {
        Directory.CreateDirectory(folder);
        SettingsStore.Folder = Path.Combine(Path.GetTempPath(), "NavTune-screenshots-" + Environment.ProcessId);
        AppState.PreviewMode = true;
        if (options.Contains("en")) L10n.Apply(AppLanguage.English);
        else if (options.Contains("vi")) L10n.Apply(AppLanguage.Vietnamese);
        if (options.Contains("dark")) app.ThemeMode = ThemeMode.Dark;
        else if (options.Contains("light")) app.ThemeMode = ThemeMode.Light;
        FluentAccent.Apply(app);
        string suffix = (options.Contains("en") ? "en" : "vi") + "-" + (options.Contains("dark") ? "dark" : options.Contains("light") ? "light" : "system");

        var state = new AppState(app.Dispatcher);
        state.IsVietnamese = !options.Contains("en");
        var window = new MainWindow(state)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();

        Save(window, Path.Combine(folder, $"0-welcome-{suffix}.png"));
        state.CompleteOnboarding();
        state.AutoPreset = true;
        state.PreviewDetected(ContentKind.Rock);
        string[] names = ["1-equalizer", "2-effects", "3-apps", "4-presets", "5-devices", "6-settings"];
        for (int i = 0; i < names.Length; i++)
        {
            window.ShowPage(i);
            Save(window, Path.Combine(folder, $"{names[i]}-{suffix}.png"));
        }
        window.ShowPage(0);
        state.IsParametric = true;
        Save(window, Path.Combine(folder, $"7-parametric-{suffix}.png"));
        state.IsGraphic = true;

        var flyout = new TrayFlyout(state) { Left = -20000, Top = -20000, ShowActivated = false };
        flyout.Show();
        Save(flyout, Path.Combine(folder, $"8-tray-{suffix}.png"));

        flyout.Close();
        window.Hide();
        try { Directory.Delete(SettingsStore.Folder, recursive: true); } catch { }
        app.Shutdown();
    }

    private static void Save(Window window, string path)
    {
        // Let bindings, layout and rendering settle.
        for (int i = 0; i < 3; i++)
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }
        var root = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        int width = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = window.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.White;
            dc.DrawRectangle(background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
