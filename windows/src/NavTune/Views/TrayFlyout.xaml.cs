using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using NavTune.ViewModels;

namespace NavTune.Views;

/// <summary>Quick controls that open from the tray icon, like the macOS menu bar window.</summary>
public partial class TrayFlyout : Window
{
    private readonly AppState state;
    private DateTime hiddenAt;
    private bool watching;

    public TrayFlyout(AppState state)
    {
        this.state = state;
        InitializeComponent();
        DataContext = state;
        Deactivated += (_, _) =>
        {
            hiddenAt = DateTime.UtcNow;
            Hide();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible == watching) return;
            watching = IsVisible;
            state.WatchApps(watching);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Hide();
        };
        AddHandler(PreviewMouseWheelEvent, new MouseWheelEventHandler(ScrollSideways), true);
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int round = 2; // DWMWCP_ROUND (Windows 11)
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        };
    }

    /// <summary>True right after the flyout closed because the user clicked elsewhere (possibly on the tray icon).</summary>
    public bool JustClosed => DateTime.UtcNow - hiddenAt < TimeSpan.FromMilliseconds(400);

    /// <summary>Shows the flyout next to the tray icon (physical pixels), on the side away from the taskbar.</summary>
    public void ShowNear(Int32Rect? anchor)
    {
        Left = -32000;
        Top = -32000;
        Show();
        UpdateLayout();
        var hwnd = new WindowInteropHelper(this).Handle;
        GetCursorPos(out var cursor);
        var point = anchor is { } a ? new Point32 { X = a.X + a.Width / 2, Y = a.Y + a.Height / 2 } : cursor;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromPoint(point, 2), ref info);
        GetWindowRect(hwnd, out var rect);
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        var work = info.Work;
        int margin = (int)(12 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        int x, y;
        if (point.Y >= work.Bottom || point.Y < work.Top)
        {
            // Taskbar at the bottom (usual) or top.
            x = Math.Clamp(point.X - w / 2, work.Left + margin, work.Right - w - margin);
            y = point.Y >= work.Bottom ? work.Bottom - h - margin : work.Top + margin;
        }
        else
        {
            // Taskbar on the left or right.
            x = point.X < work.Left ? work.Left + margin : work.Right - w - margin;
            y = Math.Clamp(point.Y - h / 2, work.Top + margin, work.Bottom - h - margin);
        }
        SetWindowPos(hwnd, new IntPtr(-1), x, y, 0, 0, 0x0001 /* SWP_NOSIZE */);
        Activate();
        Focus();
    }

    private void ScrollSideways(object sender, MouseWheelEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer { HorizontalScrollBarVisibility: ScrollBarVisibility.Hidden } scroller)
            {
                scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta / 2.0);
                e.Handled = true;
                return;
            }
        }
    }

    private void OnOpen(object sender, RoutedEventArgs e) => App.Current.ShowMainWindow();
    private void OnQuit(object sender, RoutedEventArgs e) => App.Current.Quit();

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point32 point, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
