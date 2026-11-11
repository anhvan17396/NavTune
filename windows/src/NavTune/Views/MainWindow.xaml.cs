using System.ComponentModel;
using System.Windows;
using NavTune.Infrastructure;
using NavTune.Model;
using NavTune.ViewModels;

namespace NavTune.Views;

public partial class MainWindow : Window
{
    /// <summary>A sidebar entry; its page is created the first time it is shown.</summary>
    public sealed class NavPage : ObservableObject
    {
        private readonly string vi, en;
        private readonly Func<FrameworkElement> create;
        private FrameworkElement? page;

        public NavPage(string vi, string en, string glyph, Func<FrameworkElement> create)
        {
            this.vi = vi;
            this.en = en;
            this.create = create;
            Glyph = glyph;
            L10n.Changed += () => Raise(nameof(Title));
        }

        public string Title => L10n.Tr(vi, en);
        public string Glyph { get; }
        public FrameworkElement Page => page ??= create();
    }

    private readonly AppState state;

    public IReadOnlyList<NavPage> Pages { get; } =
    [
        new("Bộ chỉnh âm", "Equalizer", Glyphs.Equalizer, () => new EqualizerPage()),
        new("Hiệu ứng", "Effects", Glyphs.Effects, () => new EffectsPage()),
        new("Ứng dụng", "Apps", Glyphs.Apps, () => new AppsPage()),
        new("Preset", "Presets", Glyphs.Presets, () => new PresetsPage()),
        new("Thiết bị", "Devices", Glyphs.Devices, () => new DevicesPage()),
        new("Cài đặt", "Settings", Glyphs.Settings, () => new SettingsPage()),
    ];

    public MainWindow(AppState state)
    {
        this.state = state;
        InitializeComponent();
        DataContext = state;
        Nav.ItemsSource = Pages;
        Nav.SelectionChanged += (_, _) => PageHost.Content = (Nav.SelectedItem as NavPage)?.Page;
        Nav.SelectedIndex = 0;
        state.Ask = (title, message, ok) => PromptDialog.Ask(this, title, message, ok);
        state.Confirm = (title, message, ok) => PromptDialog.Confirm(this, title, message, ok);
    }

    public void ShowPage(int index) => Nav.SelectedIndex = Math.Clamp(index, 0, Pages.Count - 1);

    /// <summary>Closing the window keeps NavTune running in the tray, like the macOS menu bar app.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (App.Current.IsQuitting) return;
        if (!state.CloseToTray)
        {
            // Quit after this event: WPF refuses to close a window from inside its own Closing handler.
            e.Cancel = true;
            Dispatcher.BeginInvoke(App.Current.Quit);
            return;
        }
        e.Cancel = true;
        Hide();
        if (state.TakeTrayHint())
            App.Current.ShowTrayHint(
                L10n.Tr("NavTune vẫn đang chạy", "NavTune is still running"),
                L10n.Tr("NavTune tiếp tục chỉnh âm ở khay hệ thống. Bấm biểu tượng để chỉnh nhanh, chuột phải để thoát.",
                        "NavTune keeps shaping your sound from the tray. Click the icon for quick controls, right-click to quit."));
    }
}
