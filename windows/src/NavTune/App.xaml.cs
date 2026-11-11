using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using NavTune.Infrastructure;
using NavTune.ViewModels;
using NavTune.Views;
using static NavTune.Model.L10n;

namespace NavTune;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public bool StartHidden { get; init; }
    public string? ScreenshotFolder { get; init; }
    public string[] ScreenshotOptions { get; init; } = [];
    public bool IsQuitting { get; private set; }
    public AppState State { get; private set; } = null!;

    private MainWindow? window;
    private TrayFlyout? flyout;
    private TrayIcon? tray;
    private IntPtr iconOn, iconOff;
    private EventWaitHandle? showSignal, quitSignal;
    private RegisteredWaitHandle? showWait, quitWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error($"Fatal: {args.ExceptionObject}");
            try { State?.Shutdown(); } catch { }
        };

        FluentAccent.Attach(this);
        if (ScreenshotFolder is not null)
        {
            Screenshots.Run(this, ScreenshotFolder, ScreenshotOptions);
            return;
        }

        Log.Info($"NavTune {AppState.Version} starting (Windows {Environment.OSVersion.Version})");
        State = new AppState(Dispatcher);
        window = new MainWindow(State);
        flyout = new TrayFlyout(State);
        CreateTray();
        State.PropertyChanged += OnStateChanged;

        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, -1, false);
        quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEventName);
        quitWait = ThreadPool.RegisterWaitForSingleObject(quitSignal, (_, _) => Dispatcher.BeginInvoke(Quit), null, -1, true);
        SessionEnding += (_, _) => Quit();

        if (!StartHidden || !State.HasOnboarded) ShowMainWindow();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(AppState.IsOn) or nameof(AppState.StatusText) or nameof(AppState.CurrentPresetName))
            UpdateTray();
    }

    public void ShowMainWindow()
    {
        if (IsQuitting || window is null) return;
        flyout?.Hide();
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    public void Quit()
    {
        if (IsQuitting) return;
        IsQuitting = true;
        Log.Info("Quitting");
        // Give Windows its default device and app volumes back first; nothing below may get in the way of that.
        Safely(() => State?.Shutdown());
        Safely(() => tray?.Dispose());
        Safely(() => flyout?.Close());
        Safely(() => window?.Close());
        IconFactory.Destroy(iconOn);
        IconFactory.Destroy(iconOff);
        showWait?.Unregister(null);
        quitWait?.Unregister(null);
        showSignal?.Dispose();
        quitSignal?.Dispose();
        Shutdown();
    }

    private static void Safely(Action action)
    {
        try { action(); }
        catch (Exception e) { Log.Error($"While quitting: {e}"); }
    }

    public void ShowTrayHint(string title, string text) => tray?.ShowBalloon(title, text);

    // MARK: - Tray

    private void CreateTray()
    {
        int size = IconFactory.TraySize;
        iconOn = IconFactory.Create(size, enabled: true);
        iconOff = IconFactory.Create(size, enabled: false);
        tray = new TrayIcon(State.IsOn ? iconOn : iconOff, TrayTooltip());
        tray.Activated += ToggleFlyout;
        tray.MenuRequested += ShowTrayMenu;
        tray.BalloonClicked += ShowMainWindow;
    }

    private void UpdateTray() => tray?.Update(State.IsOn ? iconOn : iconOff, TrayTooltip());

    private string TrayTooltip() => State.IsOn
        ? $"NavTune · {State.StatusText} · {State.CurrentPresetName}"
        : $"NavTune · {State.StatusText}";

    private void ToggleFlyout()
    {
        if (flyout is null) return;
        if (flyout.IsVisible)
        {
            flyout.Hide();
            return;
        }
        // The click that closed the flyout (by taking focus) must not reopen it.
        if (flyout.JustClosed) return;
        flyout.ShowNear(tray?.Bounds());
    }

    private void ShowTrayMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(Item(Tr("Mở NavTune", "Open NavTune"), ShowMainWindow, bold: true));
        menu.Items.Add(new Separator());
        var on = Item(Tr("Bật NavTune", "NavTune on"), () => State.IsOn = !State.IsOn);
        on.IsCheckable = true;
        on.IsChecked = State.IsOn;
        menu.Items.Add(on);
        var original = Item(Tr("Nghe bản gốc (so sánh)", "Original sound (compare)"), () => State.Bypass = !State.Bypass);
        original.IsCheckable = true;
        original.IsChecked = State.Bypass;
        original.IsEnabled = State.IsOn;
        menu.Items.Add(original);
        var presets = new MenuItem { Header = Tr("Preset", "Preset") };
        foreach (var preset in State.Presets)
        {
            var item = Item(preset.Name, () => State.Apply(preset.Preset));
            item.IsCheckable = true;
            item.IsChecked = preset.IsSelected;
            presets.Items.Add(item);
        }
        presets.IsEnabled = State.IsOn;
        menu.Items.Add(presets);
        var auto = Item(Tr("Tự chọn preset theo nội dung", "Auto preset by content"), () => State.AutoPreset = !State.AutoPreset);
        auto.IsCheckable = true;
        auto.IsChecked = State.AutoPreset;
        auto.IsEnabled = State.IsOn;
        menu.Items.Add(auto);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Tr("Thoát NavTune", "Quit NavTune"), Quit));
        menu.IsOpen = true;
        // Tray menus must own the foreground, or they don't close when the user clicks elsewhere.
        if (PresentationSource.FromVisual(menu) is HwndSource source) SetForegroundWindow(source.Handle);
    }

    private static MenuItem Item(string header, Action action, bool bold = false)
    {
        var item = new MenuItem { Header = header };
        if (bold) item.FontWeight = FontWeights.SemiBold;
        item.Click += (_, _) => action();
        return item;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error($"Unhandled: {e.Exception}");
        // A glitch in the UI must not take the sound down with it.
        e.Handled = !IsQuitting;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
