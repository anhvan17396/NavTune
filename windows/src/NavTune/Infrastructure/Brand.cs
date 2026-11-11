using System.Windows;
using System.Windows.Media;
using NavTune.Audio;
using static NavTune.Model.L10n;

namespace NavTune.Infrastructure;

/// <summary>NavTune's colors, shared by XAML (Themes/Brand.xaml) and the controls that draw themselves.</summary>
public static class Brand
{
    public static readonly Color Accent = Color.FromRgb(97, 92, 250);
    public static readonly Color Cyan = Color.FromRgb(5, 158, 250);
    public static readonly Color Indigo = Color.FromRgb(92, 69, 245);
    public static readonly Color Purple = Color.FromRgb(158, 41, 219);

    public static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Accent));

    public static readonly Brush Gradient = Freeze(new LinearGradientBrush(
        [new GradientStop(Cyan, 0), new GradientStop(Indigo, 0.5), new GradientStop(Purple, 1)],
        new Point(0, 0), new Point(1, 1)));

    public static readonly Brush HorizontalGradient = Freeze(new LinearGradientBrush(
        [new GradientStop(Cyan, 0), new GradientStop(Purple, 1)], new Point(0, 0.5), new Point(1, 0.5)));

    public static readonly Brush GraphBackground = Freeze(new LinearGradientBrush(
        Color.FromRgb(15, 18, 36), Color.FromRgb(28, 18, 51), 90));

    public static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// <summary>Segoe Fluent Icons glyphs standing in for the SF Symbols the macOS app uses.</summary>
public static class Glyphs
{
    public const string Equalizer = "";
    public const string Effects = "";
    public const string Apps = "";
    public const string Presets = "";
    public const string Devices = "";
    public const string Settings = "";
    public const string Compare = "";
    public const string Bass = "";
    public const string Clarity = "";
    public const string Space = "";
    public const string Dial = "";
    public const string Boost = "";
    public const string Tips = "";
    public const string Reset = "";
    public const string Save = "";
    public const string Import = "";
    public const string Export = "";
    public const string Add = "";
    public const string Delete = "";
    public const string Check = "";
    public const string Warning = "";
    public const string Mute = "";
    public const string Volume = "";
    public const string Wave = "";
    public const string Info = "";
    public const string Shield = "";
    public const string Power = "";
    public const string Globe = "";
    public const string StarFill = "";
    public const string Headphones = "";
    public const string Speakers = "";
    public const string Display = "";
    public const string Usb = "";
    public const string Bluetooth = "";
    public const string VirtualDevice = "";
    public const string Music = "";
    public const string Close = "";

    /// <summary>Maps the SF Symbol names stored in presets (shared with the macOS app) to Fluent glyphs.</summary>
    public static string ForSymbol(string? symbol) => symbol switch
    {
        "minus" => "",
        "music.mic" => "",
        "star" => "",
        "music.note" => "",
        "heart" => "",
        "guitars" => "",
        "flame" => "",
        "bolt" => "",
        "figure.dance" => "",
        "mic" => "",
        "music.quarternote.3" => "",
        "sparkles" => "",
        "cloud.moon" => "",
        "music.note.list" => "",
        "building.columns" => "",
        "leaf" => "",
        "pianokeys" => "",
        "person.wave.2" => "",
        "speaker.wave.3" => "",
        "waveform.path" => "",
        "film" => "",
        "gamecontroller" => "",
        "waveform" => "",
        "moon.stars" => "",
        "dial.medium" => "",
        _ => "",
    };

    public static string ForDevice(AudioDevice? device) => device switch
    {
        null => Mute,
        { IsVirtual: true } => VirtualDevice,
        { IsHeadphones: true } => Headphones,
        { Transport: DeviceTransport.Hdmi } => Display,
        { Transport: DeviceTransport.Usb } => Usb,
        _ => Speakers,
    };

    public static string Kind(AudioDevice? device) => device?.Transport switch
    {
        null => "—",
        DeviceTransport.Bluetooth => "Bluetooth",
        DeviceTransport.BuiltIn => Tr("Loa / jack tích hợp", "Built-in"),
        DeviceTransport.Usb => "USB",
        DeviceTransport.Hdmi => "HDMI / DisplayPort",
        DeviceTransport.Spdif => "S/PDIF",
        DeviceTransport.Virtual => Tr("Thiết bị ảo", "Virtual"),
        _ => Tr("Khác", "Other"),
    };
}

/// <summary>Draws the NavTune icon (gradient tile with EQ sliders), the same design as scripts/make_icon.swift.</summary>
public static class BrandIcon
{
    public static void Draw(DrawingContext dc, double size, bool enabled = true)
    {
        bool small = size < 40;
        double inset = small ? 0 : size * 0.035;
        var body = new Rect(inset, inset, size - 2 * inset, size - 2 * inset);
        double radius = body.Width * 0.225;
        Brush fill = enabled
            ? new LinearGradientBrush([new GradientStop(Brand.Cyan, 0), new GradientStop(Brand.Indigo, 0.5), new GradientStop(Brand.Purple, 1)],
                new Point(0.15, 0), new Point(0.85, 1))
            : new LinearGradientBrush(Color.FromRgb(140, 140, 150), Color.FromRgb(88, 88, 98), 60);
        dc.DrawRoundedRectangle(fill, null, body, radius, radius);

        double[] knobs = small ? [0.40, 0.74, 0.52] : [0.38, 0.68, 0.50, 0.80, 0.44];
        double first = small ? 0.27 : 0.22, step = small ? 0.23 : 0.14;
        double rail = body.Width * (small ? 0.085 : 0.036);
        double knob = body.Width * (small ? 0.23 : 0.121);
        double top = body.Top + body.Height * 0.2, bottom = body.Top + body.Height * 0.8;
        var railPen = new Pen(new SolidColorBrush(Color.FromArgb(72, 255, 255, 255)), rail) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var fillPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), rail) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        for (int i = 0; i < knobs.Length; i++)
        {
            double x = body.Left + body.Width * (first + step * i);
            double y = bottom - (bottom - top) * knobs[i];
            dc.DrawLine(railPen, new Point(x, top), new Point(x, bottom));
            dc.DrawLine(fillPen, new Point(x, y), new Point(x, bottom));
            dc.DrawEllipse(Brushes.White, null, new Point(x, y), knob / 2, knob / 2);
        }
    }

    /// <summary>The icon as a vector image, for XAML ({x:Static}).</summary>
    public static ImageSource AppImage { get; } = Image();

    /// <summary>A resolution-independent image of the icon for the window, sidebar and about box.</summary>
    public static ImageSource Image(bool enabled = true)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open()) Draw(dc, 256, enabled);
        return Brand.Freeze(new DrawingImage(group));
    }
}
