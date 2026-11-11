using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NavTune.Infrastructure;

/// <summary>
/// The notification-area icon (the Windows counterpart of the macOS menu bar item), via Shell_NotifyIcon.
/// A hidden window receives its messages; the icon comes back by itself if Explorer restarts.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 0x21; // WM_APP + n
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const int NIIF_INFO = 0x1, NIIF_LARGE_ICON = 0x20;
    private const int NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401, NIN_BALLOONUSERCLICK = 0x405;
    private const int WM_CONTEXTMENU = 0x7B;

    private readonly HwndSource window;
    private readonly int taskbarCreated;
    private NotifyIconData data;
    private bool added;

    /// <summary>Left click or Enter on the icon.</summary>
    public event Action? Activated;
    /// <summary>Right click or the menu key on the icon.</summary>
    public event Action? MenuRequested;
    public event Action? BalloonClicked;

    public IntPtr Handle => window.Handle;

    public TrayIcon(IntPtr icon, string tooltip)
    {
        // A real (never shown) top-level window, so menus can take the foreground and close properly.
        window = new HwndSource(new HwndSourceParameters("NavTuneTray")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
        });
        window.AddHook(WndProc);
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        data = new NotifyIconData
        {
            Size = Marshal.SizeOf<NotifyIconData>(),
            Window = window.Handle,
            Id = 1,
            Flags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            CallbackMessage = CallbackMessage,
            Icon = icon,
            Tip = Clip(tooltip, 127),
            Info = "",
            InfoTitle = "",
            Version = 4, // NOTIFYICON_VERSION_4
        };
        Add();
    }

    private void Add()
    {
        var d = data;
        added = Shell_NotifyIcon(NIM_ADD, ref d);
        if (added) Shell_NotifyIcon(NIM_SETVERSION, ref d);
    }

    public void Update(IntPtr icon, string tooltip)
    {
        data.Icon = icon;
        data.Tip = Clip(tooltip, 127);
        data.Flags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        var d = data;
        if (!Shell_NotifyIcon(NIM_MODIFY, ref d)) Add();
    }

    public void ShowBalloon(string title, string text)
    {
        var d = data;
        d.Flags = NIF_INFO;
        d.InfoTitle = Clip(title, 63);
        d.Info = Clip(text, 255);
        d.InfoFlags = NIIF_INFO | NIIF_LARGE_ICON;
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    /// <summary>The icon's rectangle in physical screen pixels, if Windows can tell.</summary>
    public Int32Rect? Bounds()
    {
        var id = new NotifyIconIdentifier { Size = Marshal.SizeOf<NotifyIconIdentifier>(), Window = window.Handle, Id = 1 };
        return Shell_NotifyIconGetRect(ref id, out var rect) == 0
            ? new Int32Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == taskbarCreated)
        {
            Add();
            return IntPtr.Zero;
        }
        if (msg != CallbackMessage) return IntPtr.Zero;
        switch ((int)(lParam.ToInt64() & 0xFFFF))
        {
            case NIN_SELECT:
            case NIN_KEYSELECT:
                Activated?.Invoke();
                break;
            case WM_CONTEXTMENU:
                MenuRequested?.Invoke();
                break;
            case NIN_BALLOONUSERCLICK:
                BalloonClicked?.Invoke();
                break;
        }
        handled = true;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (added)
        {
            var d = data;
            Shell_NotifyIcon(NIM_DELETE, ref d);
            added = false;
        }
        window.Dispose();
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public int Id;
        public int Flags;
        public int CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public int State;
        public int StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public int Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public int InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public int Size;
        public IntPtr Window;
        public int Id;
        public Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect32 rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);
}

/// <summary>Native icon handles drawn from <see cref="BrandIcon"/> (tray icon on and off).</summary>
public static class IconFactory
{
    /// <summary>The small-icon size Windows uses in the tray at the system DPI.</summary>
    public static int TraySize
    {
        get
        {
            try { return Math.Max(16, GetSystemMetricsForDpi(49 /* SM_CXSMICON */, GetDpiForSystem())); }
            catch (EntryPointNotFoundException) { return 16; }
        }
    }

    public static IntPtr Create(int size, bool enabled)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) BrandIcon.Draw(dc, size, enabled);
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[size * size * 4];
        bitmap.CopyPixels(pixels, size * 4, 0);
        // Icons want straight (not premultiplied) alpha.
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            if (a is 0 or 255) continue;
            pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / a);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
        }
        var header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = size,
            Height = -size, // top-down
            Planes = 1,
            BitCount = 32,
        };
        IntPtr color = CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        var maskBits = new byte[(size + 15) / 16 * 2 * size];
        IntPtr mask = CreateBitmap(size, size, 1, 1, maskBits);
        var info = new IconInfo { IsIcon = true, Mask = mask, Color = color };
        IntPtr icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    public static void Destroy(IntPtr icon)
    {
        if (icon != IntPtr.Zero) DestroyIcon(icon);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, byte[] bits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref IconInfo info);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
