using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NavTune.Audio;
using NavTune.Infrastructure;
using NavTune.Model;
using static NavTune.Model.L10n;

namespace NavTune.ViewModels;

/// <summary>One slider of the 10-band graphic EQ.</summary>
public sealed class GraphicBandItem(AppState state, int index) : ObservableObject
{
    public int Index { get; } = index;
    public string FrequencyLabel { get; } = Format.Frequency(AudioProcessor.Frequencies[index]);

    public double Gain
    {
        get => state.Sound.Gains[Index];
        set => state.SetGain(Index, value);
    }

    public string GainText => Format.Signed(state.Sound.Gains[Index]);
    public bool IsZero => state.Sound.Gains[Index] == 0;

    public void Refresh() => Raise(nameof(Gain), nameof(GainText), nameof(IsZero));
}

/// <summary>One band of the parametric EQ, edited from the graph and the inspector.</summary>
public sealed class ParametricBandItem(AppState state, Guid id) : ObservableObject
{
    private bool isSelected;

    public Guid Id { get; } = id;
    private EQBand Band => state.Sound.Bands.FirstOrDefault(b => b.Id == Id) ?? new EQBand();

    public int Number => state.Sound.Bands.FindIndex(b => b.Id == Id) + 1;
    public FilterKind Kind
    {
        get => Band.Kind;
        set => state.UpdateBand(Id, b => b.Kind = value);
    }
    public bool Enabled
    {
        get => Band.Enabled;
        set => state.UpdateBand(Id, b => b.Enabled = value);
    }
    public bool HasGain => Band.Kind.HasGain();
    public double Gain
    {
        get => Band.Gain;
        set => state.UpdateBand(Id, b => b.Gain = Format.Snap(value, 0.5, -12, 12));
    }
    /// <summary>Slider position 0...1 for 20 Hz...20 kHz on a log scale.</summary>
    public double FrequencyPosition
    {
        get => Math.Log10(Band.Frequency / 20) / 3;
        set => state.UpdateBand(Id, b => b.Frequency = Math.Round(20 * Math.Pow(1000, Math.Clamp(value, 0, 1))));
    }
    /// <summary>Slider position 0...1 for Q 0.1...10 on a log scale.</summary>
    public double QPosition
    {
        get => Math.Log10(Band.Q / 0.1) / 2;
        set => state.UpdateBand(Id, b => b.Q = Math.Round(0.1 * Math.Pow(100, Math.Clamp(value, 0, 1)), 2));
    }

    public string FrequencyShort => Format.Frequency(Band.Frequency);
    public string FrequencyText => Format.Hertz(Band.Frequency);
    public string GainText => HasGain ? Format.DB(Band.Gain) : "—";
    public string QText => Band.Q.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    public string Tooltip => $"{Band.Kind.Title()} · {Format.Hertz(Band.Frequency)}";

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    public void Refresh() => RaiseAll();
}

public sealed record FilterKindOption(FilterKind Kind)
{
    public string Title => Kind.Title();
    public static IReadOnlyList<FilterKindOption> All { get; } = Enum.GetValues<FilterKind>().Select(k => new FilterKindOption(k)).ToList();
}

public sealed class PresetItem : ObservableObject
{
    private readonly AppState state;

    public PresetItem(AppState state, Preset preset)
    {
        this.state = state;
        Preset = preset;
        ApplyCommand = new RelayCommand(() => state.Apply(Preset));
        DeleteCommand = new RelayCommand(() => state.DeletePreset(Preset.Id));
    }

    public Preset Preset { get; }
    public string Id => Preset.Id;
    public string Glyph => Glyphs.ForSymbol(Preset.Icon);
    public bool IsUser => !Preset.IsBuiltIn;
    public bool IsParametric => Preset.IsParametric;
    public IReadOnlyList<float> Gains => Preset.Gains;
    public bool IsSelected => state.Sound.PresetId == Preset.Id;

    /// <summary>Display name; user presets can be renamed in place.</summary>
    public string Name
    {
        get => Preset.DisplayName;
        set
        {
            if (!IsUser || string.IsNullOrWhiteSpace(value)) return;
            state.RenamePreset(Preset.Id, value.Trim());
            Raise();
        }
    }

    public ICommand ApplyCommand { get; }
    public ICommand DeleteCommand { get; }

    public void Refresh() => RaiseAll();
}

/// <summary>An entry of the output device list; <see cref="Device"/> null means "follow the Windows default".</summary>
public sealed class DeviceItem(AppState state, AudioDevice? device) : ObservableObject
{
    public AudioDevice? Device { get; } = device;
    public string? Id => Device?.Id;
    public string Name => Device?.Name ?? Tr("Theo thiết bị mặc định của Windows", "Follow the Windows default device");
    public string Kind => Device is null ? "" : Glyphs.Kind(Device);
    public string Glyph => Device is null ? Glyphs.Settings : Glyphs.ForDevice(Device);
    public bool HasSavedSettings => Device is not null && state.PerDeviceProfiles && state.HasSavedSettings(Device);

    public bool IsSelected
    {
        get => state.PreferredDeviceId == Id;
        set
        {
            if (value) state.PreferredDeviceId = Id;
        }
    }

    public void Refresh() => RaiseAll();
}

public sealed class VirtualDeviceItem(AudioDevice device)
{
    public AudioDevice Device { get; } = device;
    public string Id => Device.Id;
    public string Name => Device.Name;
}

public sealed class LanguageOption : ObservableObject
{
    public LanguageOption(AppLanguage language)
    {
        Language = language;
        L10n.Changed += () => Raise(nameof(Title));
    }

    public AppLanguage Language { get; }
    public string Title => Language.Title();
}

/// <summary>An app in the per-app list.</summary>
public sealed class AppItem : ObservableObject
{
    private readonly AppState state;
    private bool isPlaying, isRunning;

    public AppItem(AppState state, string appId, string name, string? path)
    {
        this.state = state;
        AppId = appId;
        Name = name;
        Icon = AppIcons.For(path);
        ToggleMuteCommand = new RelayCommand(() => IsMuted = !IsMuted);
    }

    public string AppId { get; }
    public string Name { get; private set; }
    public ImageSource? Icon { get; }

    public bool IsPlaying
    {
        get => isPlaying;
        set
        {
            if (SetProperty(ref isPlaying, value)) Raise(nameof(StatusText), nameof(StatusBrush));
        }
    }

    public bool IsRunning
    {
        get => isRunning;
        set
        {
            if (SetProperty(ref isRunning, value)) Raise(nameof(StatusText), nameof(StatusBrush));
        }
    }

    public bool IsMuted
    {
        get => state.RuleFor(AppId)?.Muted ?? false;
        set => state.SetAppMuted(AppId, Name, value);
    }

    /// <summary>Windows app volume in percent.</summary>
    public double Volume
    {
        get => (state.RuleFor(AppId)?.Volume ?? 1) * 100;
        set => state.SetAppVolume(AppId, Name, (float)Math.Round(value) / 100);
    }

    public string VolumeText => $"{Volume:0}%";

    public string StatusText => IsMuted
        ? (IsPlaying ? Tr("Đang phát, đã tắt tiếng ở loa", "Playing, muted on speakers") : Tr("Đã tắt tiếng ở loa", "Muted on speakers"))
        : IsPlaying ? Tr("Đang phát", "Playing")
        : IsRunning ? Tr("Đang mở", "Running") : Tr("Chưa mở", "Not running");

    public Brush StatusBrush => IsMuted
        ? (Brush)Application.Current.Resources["BrandRedBrush"]
        : IsPlaying ? (Brush)Application.Current.Resources["BrandGreenBrush"]
        : (Brush)Application.Current.FindResource("TextFillColorSecondaryBrush");

    public string MuteTooltip => IsMuted
        ? Tr($"Bật lại tiếng {Name} ở loa", $"Unmute {Name} on speakers")
        : Tr($"Tắt tiếng {Name} ở loa", $"Mute {Name} on speakers");

    public ICommand ToggleMuteCommand { get; }

    public void Rename(string name)
    {
        if (name == Name || string.IsNullOrEmpty(name)) return;
        Name = name;
        Raise(nameof(Name));
    }

    public void Refresh() => RaiseAll();
}

/// <summary>Executable icons for the app list, cached by path.</summary>
public static class AppIcons
{
    private static readonly Dictionary<string, ImageSource?> cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? For(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (cache.TryGetValue(path, out var cached)) return cached;
        ImageSource? image = null;
        var info = new ShFileInfo();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), 0x100 /* SHGFI_ICON */) != IntPtr.Zero && info.Icon != IntPtr.Zero)
        {
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                image = source;
            }
            finally { DestroyIcon(info.Icon); }
        }
        cache[path] = image;
        return image;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
