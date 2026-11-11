using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NavTune.Audio;
using NavTune.Infrastructure;
using NavTune.Interop;
using NavTune.Model;
using static NavTune.Model.L10n;

namespace NavTune.ViewModels;

public enum EngineIssue
{
    None,
    NoVirtualDevice,
    VirtualDeviceBusy,
    NoOutputDevice,
    StartFailed,
    ConnectionLost,
    /// <summary>Another app keeps changing the Windows default device back.</summary>
    DefaultContested,
}

/// <summary>
/// NavTune's state and behaviour (the Windows port of the macOS AppState). Lives on the UI thread.
///
/// While NavTune is on, the virtual device is the Windows default: every app plays into it, the engine captures it,
/// processes the sound and plays it on the real output device. Turning NavTune off (or quitting) gives the default
/// back to that real device.
/// </summary>
public sealed class AppState : ObservableObject
{
    public const string CableUrl = "https://vb-audio.com/Cable/";

    /// <summary>Screenshot mode: the UI works on throwaway settings and no audio device is touched.</summary>
    public static bool PreviewMode { get; set; }

    private readonly Dispatcher dispatcher;
    private readonly AppConfig config;
    private readonly SystemChanges changes;
    private readonly AudioEngine engine = new();
    private readonly SpectrumModel spectrum = new();
    private readonly ContentDetector detector = new();
    private readonly DispatcherTimer saveTimer, healthTimer, devicesTimer, appsTimer, rulesTimer;
    private DeviceMonitor? deviceMonitor;
    private SessionWatcher? sessionWatcher;
    private VolumeMirror? volumeMirror;

    private SoundSettings sound = new();
    private bool bypass;
    private EngineState engineState = EngineState.Off;
    private EngineIssue issue;
    private string? issueDetail;
    private AudioDevice? activeDevice, virtualDevice;
    private List<AudioDevice> devices = [];
    private int recoveryAttempts;
    /// <summary>The engine run whose events are current; events from earlier runs are dropped.</summary>
    private int engineRunId;
    private DateTime ignoreDefaultChangesUntil;
    /// <summary>When NavTune had to take the default back from someone else, to notice a tug of war.</summary>
    private readonly Queue<DateTime> defaultRetakes = new();
    private HashSet<string> watchedDeviceIds = [];
    private int watcherSequence;
    private bool shuttingDown;
    private double sampleRate;
    private ParametricBandItem? selectedBand;
    private int appsViewers;
    private int detectorGeneration;
    private ContentKind? detectedContent;
    private readonly AutoPresetPolicy autoPolicy = new();
    private DispatcherTimer? transition;
    private float[] transitionFrom = [], transitionTo = [];
    private float transitionFromPreamp, transitionToPreamp;
    private int transitionStep;

    public AppState(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        config = SettingsStore.Load();
        changes = Recovery.Load();
        L10n.Apply(L10n.ParseLanguage(config.Language));
        L10n.Changed += OnLanguageChanged;

        saveTimer = Timer(TimeSpan.FromSeconds(0.5), Save, once: true);
        healthTimer = Timer(TimeSpan.FromSeconds(2), HealthCheck);
        devicesTimer = Timer(TimeSpan.FromMilliseconds(300), DevicesChanged, once: true);
        appsTimer = Timer(TimeSpan.FromSeconds(1), RefreshApps);
        rulesTimer = Timer(TimeSpan.FromMilliseconds(150), () => { ApplyAppRules(); if (appsViewers > 0) RefreshApps(); }, once: true);

        GraphicBands = new(Enumerable.Range(0, SoundSettings.BandCount).Select(i => new GraphicBandItem(this, i)));
        BuiltInPresets = new(Preset.BuiltIns.Select(p => new PresetItem(this, p)));
        Languages = Enum.GetValues<AppLanguage>().Select(l => new LanguageOption(l)).ToList();
        RebuildPresets();

        ToggleCommand = new RelayCommand(() => IsOn = !IsOn);
        BypassCommand = new RelayCommand(() => { if (IsOn) Bypass = !Bypass; });
        RetryCommand = new RelayCommand(() => RestartEngine());
        InstallCableCommand = new RelayCommand(() => Shell.Open(CableUrl));
        ResetEqCommand = new RelayCommand(ResetEQ);
        ResetEffectsCommand = new RelayCommand(ResetEffects);
        AddBandCommand = new RelayCommand(() => AddBand(), _ => Sound.Bands.Count < SoundSettings.MaxParametricBands);
        RemoveBandCommand = new RelayCommand(() => { if (SelectedBand is { } b) RemoveBand(b.Id); }, _ => Sound.Bands.Count > 1);
        SelectBandCommand = new RelayCommand(p => { if (p is ParametricBandItem item) SelectedBand = item; });

        engine.StateChanged += e => dispatcher.BeginInvoke(() =>
        {
            if (e.RunId == engineRunId) OnEngineEvent(e);
        });
        detector.Detected += (generation, result) => dispatcher.BeginInvoke(() => OnContentDetected(generation, result));
        detector.Message += Log.Info;

        RefreshDevices();
        activeDevice = ResolveActiveDevice();
        virtualDevice = AudioDevices.PickVirtualDevice(devices, config.VirtualDeviceId);
        LoadSound();
        // Left over from a crash while NavTune was off: put Windows back the way it was.
        if (!ShouldRun && !changes.IsEmpty && !PreviewMode) Recovery.Undo(changes);
        try
        {
            deviceMonitor = new DeviceMonitor();
            deviceMonitor.DevicesChanged += () => dispatcher.BeginInvoke(ScheduleDevicesChanged);
            deviceMonitor.DefaultChanged += (role, id) =>
            {
                if (role == ERole.Multimedia) dispatcher.BeginInvoke(() => OnDefaultChanged(id));
            };
        }
        catch (Exception e)
        {
            Log.Error($"Device notifications unavailable: {e.Message}");
        }
        RestartSessionWatcher();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        if (LaunchAtLogin) AutoStart.Refresh();
        RestartEngine();
    }

    private DispatcherTimer Timer(TimeSpan interval, Action tick, bool once = false)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = interval };
        timer.Tick += (_, _) =>
        {
            if (once) timer.Stop();
            tick();
        };
        return timer;
    }

    // MARK: - Collections and commands

    public ObservableCollection<GraphicBandItem> GraphicBands { get; }
    public ObservableCollection<ParametricBandItem> ParametricBands { get; } = [];
    public ObservableCollection<PresetItem> Presets { get; } = [];
    public ObservableCollection<PresetItem> UserPresets { get; } = [];
    public ObservableCollection<PresetItem> BuiltInPresets { get; }
    public ObservableCollection<DeviceItem> OutputDevices { get; } = [];
    public ObservableCollection<VirtualDeviceItem> VirtualDevices { get; } = [];
    public ObservableCollection<AppItem> Apps { get; } = [];
    public ObservableCollection<AppItem> QuickMuteApps { get; } = [];
    public IReadOnlyList<LanguageOption> Languages { get; }
    public IReadOnlyList<FilterKindOption> FilterKinds => FilterKindOption.All;

    public ICommand ToggleCommand { get; }
    public ICommand BypassCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand InstallCableCommand { get; }
    public ICommand ResetEqCommand { get; }
    public ICommand ResetEffectsCommand { get; }
    public ICommand AddBandCommand { get; }
    public ICommand RemoveBandCommand { get; }
    public ICommand SelectBandCommand { get; }

    public ICommand SavePresetCommand => new RelayCommand(() =>
    {
        string? name = Ask?.Invoke(Tr("Lưu preset", "Save preset"), Tr("Lưu đường EQ hiện tại để dùng lại sau.", "Save the current EQ curve for later."), Tr("Lưu", "Save"));
        if (name is not null) SaveCurrentAsPreset(name);
    });

    public ICommand ImportPresetsCommand => new RelayCommand(() =>
    {
        var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json", Title = Tr("Nhập preset", "Import presets") };
        if (dialog.ShowDialog() != true) return;
        try
        {
            int count = ImportPresets(dialog.FileName);
            PresetMessage = Tr($"Đã nhập {count} preset.", $"Imported {count} presets.");
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            PresetMessage = Tr("File không hợp lệ.", "Invalid file.");
        }
    });

    public ICommand ExportPresetsCommand => new RelayCommand(() =>
    {
        var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "NavTune-presets.json", Title = Tr("Xuất preset", "Export presets") };
        if (dialog.ShowDialog() != true) return;
        try
        {
            ExportPresets(dialog.FileName);
            PresetMessage = Tr($"Đã xuất {config.UserPresets.Count} preset.", $"Exported {config.UserPresets.Count} presets.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            PresetMessage = Tr("Không xuất được: ", "Export failed: ") + e.Message;
        }
    }, _ => HasUserPresets);

    public ICommand ResetAllSoundCommand => new RelayCommand(() =>
    {
        if (Confirm?.Invoke(Tr("Khôi phục cài đặt âm thanh?", "Reset sound settings?"),
                Tr("EQ và hiệu ứng của mọi thiết bị sẽ về mặc định. Preset của anh vẫn được giữ.", "EQ and effects for every device go back to default. Your presets are kept."),
                Tr("Khôi phục", "Reset")) == true)
            ResetAllSound();
    });

    public ICommand ShowWelcomeCommand => new RelayCommand(ShowWelcome);
    public ICommand CompleteOnboardingCommand => new RelayCommand(CompleteOnboarding);
    public ICommand OpenWebsiteCommand => new RelayCommand(() => Shell.Open("https://navtools.vn"));
    public ICommand OpenLogCommand => new RelayCommand(() => Shell.Open(System.IO.Path.GetDirectoryName(Log.Path)!));
    public ICommand OpenSoundSettingsCommand => new RelayCommand(() => Shell.Open("ms-settings:sound"));

    /// <summary>Asks for a text (title, message, OK label); set by the UI.</summary>
    public Func<string, string, string, string?>? Ask { get; set; }

    /// <summary>Asks for a confirmation (title, message, OK label); set by the UI.</summary>
    public Func<string, string, string, bool>? Confirm { get; set; }

    private string? presetMessage;

    /// <summary>Result of the last import or export.</summary>
    public string? PresetMessage
    {
        get => presetMessage;
        private set => SetProperty(ref presetMessage, value);
    }

    /// <summary>Raised when the sound settings change, so graphs redraw the EQ curve.</summary>
    public event Action? SoundChanged;

    // MARK: - On / off

    private bool ShouldRun => config.HasOnboarded && config.IsOn;

    /// <summary>On means processing; like on macOS, NavTune stays off until the welcome screen is done.</summary>
    public bool IsOn
    {
        get => config.IsOn && config.HasOnboarded;
        set
        {
            if (value && !config.HasOnboarded)
            {
                CompleteOnboarding();
                return;
            }
            if (config.IsOn == value) return;
            config.IsOn = value;
            if (!value && bypass) Bypass = false;
            ScheduleSave();
            Raise();
            RestartEngine();
        }
    }

    public bool Bypass
    {
        get => bypass;
        set
        {
            if (!SetProperty(ref bypass, value)) return;
            PushParams();
            // Like macOS: nothing is analyzed while comparing with the original sound.
            detector.Paused = value;
            RaiseStatus();
        }
    }

    public bool HasOnboarded => config.HasOnboarded;

    private bool welcomeRequested;

    /// <summary>The welcome screen shows on first run, or when asked for again from Settings (sound keeps playing).</summary>
    public bool IsWelcomeVisible => !config.HasOnboarded || welcomeRequested;

    public void ShowWelcome()
    {
        welcomeRequested = true;
        Raise(nameof(IsWelcomeVisible));
    }

    public void CompleteOnboarding()
    {
        bool wasRunning = ShouldRun;
        welcomeRequested = false;
        config.HasOnboarded = true;
        config.IsOn = true;
        ScheduleSave();
        Raise(nameof(HasOnboarded), nameof(IsOn), nameof(IsWelcomeVisible));
        if (!wasRunning || !engine.IsActive) RestartEngine();
    }

    public EngineState EngineState
    {
        get => engineState;
        private set
        {
            if (!SetProperty(ref engineState, value)) return;
            Raise(nameof(IsProcessing), nameof(IsActive));
            RaiseStatus();
            // Listens only while sound is processed; starts afresh after standby or a restart, like macOS.
            ResetDetector();
        }
    }

    public bool IsActive => engineState != EngineState.Off;
    public bool IsProcessing => engineState == EngineState.Running;

    public double SampleRate
    {
        get => sampleRate;
        private set
        {
            if (SetProperty(ref sampleRate, value)) Raise(nameof(SampleRateText), nameof(LatencyText));
        }
    }

    // MARK: - Status

    public string StatusText =>
        !IsOn ? Tr("Đang tắt", "Off")
        : issue != EngineIssue.None ? Tr("Có lỗi", "Error")
        : bypass ? Tr("Bản gốc", "Original")
        : engineState == EngineState.Standby ? Tr("Chờ âm thanh", "Standby")
        : engineState == EngineState.Running ? Tr("Đang hoạt động", "Active")
        : Tr("Đang khởi động", "Starting");

    public Color StatusColor =>
        !IsOn ? Color.FromRgb(138, 138, 150)
        : issue != EngineIssue.None ? Color.FromRgb(240, 140, 30)
        : bypass ? Color.FromRgb(40, 130, 240)
        : engineState == EngineState.Standby ? Color.FromRgb(20, 170, 170)
        : engineState == EngineState.Running ? Color.FromRgb(47, 179, 107)
        : Color.FromRgb(138, 138, 150);

    public string StatusHelp =>
        !IsOn ? Tr("NavTune đang tắt", "NavTune is off")
        : issue != EngineIssue.None ? IssueText ?? ""
        : bypass ? Tr("Đang nghe âm thanh gốc để so sánh", "Playing the original sound for comparison")
        : engineState == EngineState.Standby
            ? Tr("Không có gì đang phát nên NavTune tạm nghỉ: không tốn CPU, loa và tai nghe được nghỉ. Tự chạy lại ngay khi có âm thanh.",
                 "Nothing is playing, so NavTune is resting: no CPU use, and your speakers and headphones can sleep. It resumes instantly when sound plays.")
        : engineState == EngineState.Running ? Tr("NavTune đang xử lý âm thanh", "NavTune is processing audio")
        : "";

    public EngineIssue Issue => issue;
    public bool HasIssue => issue != EngineIssue.None;
    public bool CanInstallCable => issue == EngineIssue.NoVirtualDevice;
    public bool CanRetry => issue is EngineIssue.NoVirtualDevice or EngineIssue.VirtualDeviceBusy or EngineIssue.StartFailed
        or EngineIssue.ConnectionLost or EngineIssue.DefaultContested;

    public string? IssueText
    {
        get
        {
            string device = activeDevice?.Name ?? "";
            return issue switch
            {
                EngineIssue.NoVirtualDevice => Tr(
                    "Chưa có thiết bị âm thanh ảo để NavTune nhận âm thanh của mọi ứng dụng. Hãy cài VB-CABLE (miễn phí), sau đó bấm “Thử lại”. Máy đã cài FxSound thì NavTune dùng được thiết bị ảo của FxSound.",
                    "NavTune needs a virtual audio device to receive the sound of every app. Install VB-CABLE (free), then click “Try again”. If FxSound is installed, NavTune can use FxSound's virtual device."),
                EngineIssue.VirtualDeviceBusy => Tr(
                    $"{issueDetail} đang chạy. Hai ứng dụng chỉnh âm chạy cùng lúc sẽ tranh nhau loa mặc định và phát tiếng hai lần. Hãy thoát {issueDetail} (chuột phải biểu tượng ở khay hệ thống → Exit), NavTune sẽ tự bật lại.",
                    $"{issueDetail} is running. Two sound enhancers at once fight over the default speaker and play everything twice. Quit {issueDetail} (right-click its tray icon → Exit) and NavTune turns back on by itself."),
                EngineIssue.DefaultContested => Tr(
                    "Một ứng dụng khác liên tục đổi loa mặc định của Windows (ví dụ Voicemeeter, SoundSwitch hay một ứng dụng chỉnh âm khác), nên NavTune đã tạm dừng. Hãy tắt ứng dụng đó rồi bấm “Thử lại”.",
                    "Another app keeps changing the Windows default speaker (Voicemeeter, SoundSwitch or another sound enhancer, for example), so NavTune paused. Close that app, then click “Try again”."),
                EngineIssue.NoOutputDevice => Tr("Không tìm thấy loa hoặc tai nghe nào để phát âm thanh.", "No speakers or headphones found to play sound."),
                EngineIssue.StartFailed => Tr(
                    $"Không khởi động được ({issueDetail}). Thiết bị “{device}” có thể chưa sẵn sàng hoặc đang bị ứng dụng khác chiếm riêng.",
                    $"Couldn't start ({issueDetail}). “{device}” may not be ready or may be held exclusively by another app."),
                EngineIssue.ConnectionLost => Tr($"Mất kết nối với “{device}”. Hãy tắt rồi bật lại NavTune.", $"Lost connection to “{device}”. Turn NavTune off and on again."),
                _ => null,
            };
        }
    }

    private void SetIssue(EngineIssue next, string? detail = null)
    {
        if (issue == next && issueDetail == detail) return;
        issue = next;
        issueDetail = detail;
        if (next != EngineIssue.None) Log.Info($"Issue: {next} {detail}");
        Raise(nameof(Issue), nameof(HasIssue), nameof(IssueText), nameof(CanInstallCable), nameof(CanRetry));
        RaiseStatus();
    }

    private void RaiseStatus() => Raise(nameof(StatusText), nameof(StatusColor), nameof(StatusHelp));

    // MARK: - Live data for meters and graphs (polled per frame, not observed)

    public float OutputLevel => IsProcessing ? engine.Processor.CurrentOutputLevel : 0;

    public float[] SpectrumBands(long timestamp) =>
        spectrum.Bands(timestamp, IsProcessing ? engine.Processor : null, sampleRate);

    // MARK: - Sound settings

    public SoundSettings Sound => sound;

    private void SetSound(SoundSettings value)
    {
        if (sound.Equals(value)) return;
        sound = value;
        PushParams();
        StoreSound();
        OnSoundChanged();
    }

    /// <summary>Changes that keep the preset (effects, input gain).</summary>
    private void Mutate(Action<SoundSettings> change)
    {
        var next = sound.Clone();
        change(next);
        SetSound(next);
    }

    /// <summary>A manual EQ change: marks the curve as custom and turns off automatic presets.</summary>
    private void Edit(Action<SoundSettings> change)
    {
        StopTransition();
        if (AutoPreset) AutoPreset = false;
        Mutate(s =>
        {
            change(s);
            s.PresetId = Preset.CustomId;
        });
    }

    private void OnSoundChanged()
    {
        foreach (var band in GraphicBands) band.Refresh();
        SyncParametricBands();
        foreach (var preset in Presets) preset.Refresh();
        Raise(nameof(Sound), nameof(Preamp), nameof(Bass), nameof(Clarity), nameof(Width), nameof(Loudness), nameof(Boost),
              nameof(PreampText), nameof(BassText), nameof(ClarityText), nameof(WidthText), nameof(LoudnessText), nameof(BoostText),
              nameof(IsParametric), nameof(IsGraphic), nameof(CurrentPresetName));
        SoundChanged?.Invoke();
    }

    public double Preamp
    {
        get => sound.Preamp;
        set
        {
            StopTransition();
            Mutate(s => s.Preamp = Format.Snap(value, 0.5, -12, 6));
        }
    }
    public double Bass { get => sound.Bass; set => Mutate(s => s.Bass = Format.Snap(value, 0.5, -12, 12)); }
    public double Clarity { get => sound.Clarity; set => Mutate(s => s.Clarity = Format.Snap(value, 0.5, -12, 12)); }
    public double Width { get => sound.Width; set => Mutate(s => s.Width = Format.Snap(value, 0.05, 0, 2)); }
    public double Loudness { get => sound.Loudness; set => Mutate(s => s.Loudness = Format.Snap(value, 0.05, 0, 1)); }
    public double Boost { get => sound.Boost; set => Mutate(s => s.Boost = Format.Snap(value, 0.5, 0, 12)); }

    public string PreampText => Format.DB(sound.Preamp);
    public string BassText => Format.DB(sound.Bass);
    public string ClarityText => Format.DB(sound.Clarity);
    public string WidthText => Format.Percent(sound.Width);
    public string LoudnessText => Format.Percent(sound.Loudness);
    public string BoostText => Format.DB(sound.Boost);

    public bool IsParametric
    {
        get => sound.Mode == EQMode.Parametric;
        set => SetMode(value ? EQMode.Parametric : EQMode.Graphic);
    }

    public bool IsGraphic
    {
        get => sound.Mode == EQMode.Graphic;
        set { if (value) SetMode(EQMode.Graphic); }
    }

    public string CurrentPresetName =>
        Presets.FirstOrDefault(p => p.Id == sound.PresetId)?.Preset.DisplayName ?? Tr("Tuỳ chỉnh", "Custom");

    public void SetGain(int band, double value)
    {
        float gain = Format.Snap(value, 0.5, -12, 12);
        if (sound.Gains[band] == gain) return;
        Edit(s => s.Gains[band] = gain);
    }

    public void SetMode(EQMode mode)
    {
        if (sound.Mode == mode) return;
        Edit(s => s.Mode = mode);
    }

    public void UpdateBand(Guid id, Action<EQBand> change)
    {
        int index = sound.Bands.FindIndex(b => b.Id == id);
        if (index < 0) return;
        var probe = sound.Bands[index].Clone();
        change(probe);
        if (probe.Equals(sound.Bands[index])) return;
        Edit(s => change(s.Bands[index]));
    }

    /// <summary>Adds a bell filter in the widest gap of the spectrum and selects it.</summary>
    public void AddBand()
    {
        if (sound.Bands.Count >= SoundSettings.MaxParametricBands) return;
        var used = new[] { 20.0 }.Concat(sound.Bands.Select(b => b.Frequency).Order()).Append(20000).Select(Math.Log10).ToArray();
        (double width, double center) gap = (0, 3);
        for (int i = 0; i + 1 < used.Length; i++)
            if (used[i + 1] - used[i] > gap.width) gap = (used[i + 1] - used[i], (used[i] + used[i + 1]) / 2);
        var band = new EQBand { Kind = FilterKind.Peak, Frequency = Math.Round(Math.Pow(10, gap.center)), Q = 1 };
        Edit(s => s.Bands.Add(band));
        SelectedBand = ParametricBands.FirstOrDefault(b => b.Id == band.Id);
    }

    public void RemoveBand(Guid id)
    {
        if (sound.Bands.Count <= 1) return;
        Edit(s => s.Bands.RemoveAll(b => b.Id == id));
        SelectedBand = ParametricBands.FirstOrDefault();
    }

    public ParametricBandItem? SelectedBand
    {
        get => selectedBand;
        set
        {
            value ??= ParametricBands.FirstOrDefault();
            if (selectedBand == value) return;
            if (selectedBand is not null) selectedBand.IsSelected = false;
            selectedBand = value;
            if (selectedBand is not null) selectedBand.IsSelected = true;
            Raise();
            SoundChanged?.Invoke();
        }
    }

    private void SyncParametricBands()
    {
        var ids = sound.Bands.Select(b => b.Id).ToList();
        if (!ids.SequenceEqual(ParametricBands.Select(b => b.Id)))
        {
            Guid? selected = selectedBand?.Id;
            ParametricBands.Clear();
            foreach (var id in ids) ParametricBands.Add(new ParametricBandItem(this, id));
            selectedBand = null;
            SelectedBand = ParametricBands.FirstOrDefault(b => b.Id == selected) ?? ParametricBands.FirstOrDefault();
        }
        foreach (var band in ParametricBands) band.Refresh();
    }

    /// <summary>
    /// Applies a preset. Choosing one by hand turns automatic presets off; <paramref name="automatic"/> (content
    /// detection) keeps them on and glides to the new curve, like macOS.
    /// </summary>
    public void Apply(Preset preset, bool automatic = false)
    {
        if (!automatic && AutoPreset) AutoPreset = false;
        StopTransition();
        if (automatic && sound.Mode == EQMode.Graphic && preset.Bands is null)
        {
            Mutate(s => s.PresetId = preset.Id);
            StartTransition(preset.Gains, preset.Preamp);
            return;
        }
        Mutate(s =>
        {
            s.PresetId = preset.Id;
            s.Gains = (float[])preset.Gains.Clone();
            s.Preamp = preset.Preamp;
            if (preset.Bands is { } bands)
            {
                s.Mode = EQMode.Parametric;
                s.Bands = bands.Select(b => b.Clone()).ToList();
            }
            else
            {
                s.Mode = EQMode.Graphic;
            }
        });
    }

    public void ApplyPreset(string id)
    {
        if (Presets.FirstOrDefault(p => p.Id == id) is { } item) Apply(item.Preset);
    }

    public void ResetEQ()
    {
        if (sound.Mode == EQMode.Parametric) Edit(s => s.Bands = [.. SoundSettings.DefaultBands()]);
        else Apply(Preset.BuiltIns[0]);
    }

    public void ResetEffects() => Mutate(s => s.ResetEffects());

    public void ResetAllSound()
    {
        StopTransition();
        config.Sound.Clear();
        sound = new SoundSettings();
        PushParams();
        StoreSound();
        OnSoundChanged();
        RefreshDeviceItems();
    }

    private void PushParams() => engine.Processor.Update(sound, bypass);

    private void StartTransition(float[] gains, float preamp)
    {
        transitionFrom = (float[])sound.Gains.Clone();
        transitionTo = (float[])gains.Clone();
        transitionFromPreamp = sound.Preamp;
        transitionToPreamp = preamp;
        transitionStep = 0;
        transition ??= Timer(TimeSpan.FromSeconds(1.0 / 30), TransitionTick);
        transition.Start();
    }

    /// <summary>36 steps at 30 Hz (1.2 s) with smoothstep easing, as on macOS.</summary>
    private void TransitionTick()
    {
        const int steps = 36;
        transitionStep++;
        float t = (float)transitionStep / steps;
        float eased = t * t * (3 - 2 * t);
        Mutate(s =>
        {
            for (int i = 0; i < s.Gains.Length && i < transitionTo.Length; i++)
                s.Gains[i] = transitionFrom[i] + (transitionTo[i] - transitionFrom[i]) * eased;
            s.Preamp = transitionFromPreamp + (transitionToPreamp - transitionFromPreamp) * eased;
        });
        if (transitionStep >= steps) StopTransition();
    }

    private void StopTransition() => transition?.Stop();

    // MARK: - Auto preset by content

    /// <summary>Listens to what plays (speech, guitar, synth, piano, orchestra…) and picks the matching preset.</summary>
    public bool AutoPreset
    {
        get => config.AutoPreset;
        set
        {
            if (config.AutoPreset == value) return;
            config.AutoPreset = value;
            ScheduleSave();
            DetectedContent = null;
            Raise();
            ResetDetector(releaseModel: !value);
        }
    }

    public ContentKind? DetectedContent
    {
        get => detectedContent;
        private set
        {
            if (SetProperty(ref detectedContent, value)) Raise(nameof(DetectedContentText), nameof(IsListening));
        }
    }

    /// <summary>Screenshot mode: shows a detection without listening to anything.</summary>
    internal void PreviewDetected(ContentKind kind)
    {
        if (PreviewMode) DetectedContent = kind;
    }

    /// <summary>True until something recognizable has been heard.</summary>
    public bool IsListening => detectedContent is null;

    public string DetectedContentText => detectedContent?.Title() ?? Tr("Đang nghe…", "Listening…");

    private void ResetDetector(bool releaseModel = false)
    {
        autoPolicy.Reset();
        if (AutoPreset && IsProcessing && !PreviewMode && !shuttingDown)
        {
            detector.Paused = bypass;
            detectorGeneration = detector.Start(engine.Processor);
        }
        else
        {
            detector.Stop(releaseModel);
        }
    }

    /// <summary>Switches preset once the same kind is heard three times in a row (about 4–5 s), like macOS.</summary>
    private void OnContentDetected(int generation, ContentResult result)
    {
        if (generation != detectorGeneration || !AutoPreset || !IsProcessing) return;
        if (autoPolicy.Next(result) is not { } kind) return;
        DetectedContent = kind;
        if (sound.PresetId != kind.PresetId() && Preset.BuiltIns.FirstOrDefault(p => p.Id == kind.PresetId()) is { } preset)
        {
            Log.Info($"Auto preset: {preset.Id} ({result.Summary})");
            Apply(preset, automatic: true);
        }
    }

    // MARK: - Per-device settings

    private string SoundKey => config.PerDeviceProfiles && activeDevice is not null ? activeDevice.Id : AppConfig.GlobalSoundKey;

    public bool HasSavedSettings(AudioDevice device) => config.Sound.ContainsKey(device.Id);

    private void LoadSound()
    {
        StopTransition();
        sound = config.Sound.TryGetValue(SoundKey, out var saved) ? saved.Clone() : new SoundSettings();
        PushParams();
        OnSoundChanged();
    }

    private void StoreSound()
    {
        config.Sound[SoundKey] = sound.Clone();
        ScheduleSave();
    }

    public bool PerDeviceProfiles
    {
        get => config.PerDeviceProfiles;
        set
        {
            if (config.PerDeviceProfiles == value) return;
            config.PerDeviceProfiles = value;
            ScheduleSave();
            Raise();
            LoadSound();
            RefreshDeviceItems();
        }
    }

    // MARK: - User presets

    private void RebuildPresets()
    {
        UserPresets.Clear();
        foreach (var preset in config.UserPresets) UserPresets.Add(new PresetItem(this, preset));
        Presets.Clear();
        foreach (var item in BuiltInPresets.Concat(UserPresets)) Presets.Add(item);
        Raise(nameof(HasUserPresets), nameof(CurrentPresetName));
    }

    public bool HasUserPresets => config.UserPresets.Count > 0;

    public void SaveCurrentAsPreset(string name)
    {
        string trimmed = name.Trim();
        var preset = Preset.User(trimmed.Length == 0 ? Tr("Preset của tôi", "My preset") : trimmed,
            sound.Gains, sound.Preamp, sound.Mode == EQMode.Parametric ? sound.Bands : null);
        config.UserPresets.Add(preset);
        RebuildPresets();
        Mutate(s => s.PresetId = preset.Id);
        ScheduleSave();
    }

    public void RenamePreset(string id, string name)
    {
        if (config.UserPresets.FirstOrDefault(p => p.Id == id) is not { } preset || preset.Name == name) return;
        preset.Name = name;
        ScheduleSave();
        Raise(nameof(CurrentPresetName));
    }

    public void DeletePreset(string id)
    {
        if (config.UserPresets.RemoveAll(p => p.Id == id) == 0) return;
        RebuildPresets();
        if (sound.PresetId == id) Mutate(s => s.PresetId = Preset.CustomId);
        ScheduleSave();
    }

    public void ExportPresets(string path)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(config.UserPresets, options));
    }

    /// <summary>Imports presets exported by NavTune on Windows or macOS. Returns how many were added.</summary>
    public int ImportPresets(string path)
    {
        var imported = (JsonSerializer.Deserialize<List<Preset>>(File.ReadAllText(path)) ?? [])
            .Where(p => p?.Gains is { Length: var n } && n == SoundSettings.BandCount)
            .Select(p => Preset.User(string.IsNullOrWhiteSpace(p.Name) ? Tr("Preset nhập", "Imported preset") : p.Name, p.Gains, p.Preamp, p.Bands))
            .ToList();
        config.UserPresets.AddRange(imported);
        RebuildPresets();
        ScheduleSave();
        return imported.Count;
    }

    // MARK: - Devices

    public AudioDevice? ActiveDevice => activeDevice;
    public string ActiveDeviceName => activeDevice?.Name ?? Tr("Không có thiết bị", "No device");
    public string ActiveDeviceGlyph => Glyphs.ForDevice(activeDevice);
    public string ActiveDeviceKind => Glyphs.Kind(activeDevice);
    public bool ActiveDeviceIsBluetooth => activeDevice?.IsBluetooth == true;
    public AudioDevice? VirtualDevice => virtualDevice;
    public string VirtualDeviceName => virtualDevice?.Name ?? Tr("Chưa có", "None");
    public bool HasVirtualDevice => virtualDevice is not null;

    public string SampleRateText => sampleRate > 0 ? $"{sampleRate / 1000:0.0} kHz" : "—";

    public string LatencyText => engine.Info is { } info && IsActive ? $"≈ {info.LatencyMs:0} ms" : "—";

    public string? PreferredDeviceId
    {
        get => config.PreferredDeviceId;
        set
        {
            if (config.PreferredDeviceId == value) return;
            config.PreferredDeviceId = value;
            ScheduleSave();
            Raise();
            Raise(nameof(SelectedOutput));
            foreach (var item in OutputDevices) item.Refresh();
            DevicesChanged();
        }
    }

    /// <summary>The output list entry that is chosen (the first entry follows the Windows default).</summary>
    public DeviceItem? SelectedOutput
    {
        get => OutputDevices.FirstOrDefault(i => i.Id == config.PreferredDeviceId) ?? OutputDevices.FirstOrDefault();
        set
        {
            if (value is not null) PreferredDeviceId = value.Id;
        }
    }

    public string? VirtualDeviceId
    {
        get => virtualDevice?.Id ?? config.VirtualDeviceId;
        set
        {
            if (value is null || config.VirtualDeviceId == value) return;
            config.VirtualDeviceId = value;
            ScheduleSave();
            Raise();
            RestartEngine();
        }
    }

    public LatencyMode Latency
    {
        get => config.Latency;
        set
        {
            if (config.Latency == value) return;
            config.Latency = value;
            ScheduleSave();
            Raise();
            RestartEngine();
        }
    }

    private void RefreshDevices()
    {
        try { devices = AudioDevices.RenderDevices(); }
        catch (Exception e)
        {
            Log.Error($"Listing devices failed: {e.Message}");
            devices = [];
        }
        var real = devices.Where(d => d.IsActive && !d.IsVirtual).ToList();
        var wanted = new List<AudioDevice?> { null };
        wanted.AddRange(real);
        if (!wanted.Select(d => d?.Id).SequenceEqual(OutputDevices.Select(i => i.Id)) ||
            !wanted.Select(d => d?.Name).SequenceEqual(OutputDevices.Select(i => i.Device?.Name)))
        {
            OutputDevices.Clear();
            foreach (var device in wanted) OutputDevices.Add(new DeviceItem(this, device));
            Raise(nameof(SelectedOutput));
        }
        var virtuals = devices.Where(d => d.IsActive && d.IsVirtual).ToList();
        if (!virtuals.Select(d => d.Id).SequenceEqual(VirtualDevices.Select(v => v.Id)))
        {
            VirtualDevices.Clear();
            foreach (var device in virtuals) VirtualDevices.Add(new VirtualDeviceItem(device));
        }
    }

    private void RefreshDeviceItems()
    {
        foreach (var item in OutputDevices) item.Refresh();
    }

    /// <summary>The chosen device, else the real Windows default, else the last one the user had, else the best one.</summary>
    private AudioDevice? ResolveActiveDevice()
    {
        var real = devices.Where(d => d.IsActive && !d.IsVirtual).ToList();
        if (config.PreferredDeviceId is { } preferred && real.FirstOrDefault(d => d.Id == preferred) is { } chosen) return chosen;
        string? current = AudioDevices.DefaultRenderDeviceId();
        if (current is not null && real.FirstOrDefault(d => d.Id == current) is { } windowsDefault) return windowsDefault;
        if (config.LastRealDefaultId is { } last && real.FirstOrDefault(d => d.Id == last) is { } previous) return previous;
        return Recovery.BestRealDevice(real);
    }

    private void ScheduleDevicesChanged()
    {
        devicesTimer.Stop();
        devicesTimer.Start();
    }

    private void DevicesChanged()
    {
        if (shuttingDown) return;
        RefreshDevices();
        var nextVirtual = AudioDevices.PickVirtualDevice(devices, config.VirtualDeviceId);
        var nextActive = ResolveActiveDevice();
        bool outputChanged = nextActive?.Id != activeDevice?.Id;
        bool virtualChanged = nextVirtual?.Id != virtualDevice?.Id;
        if (virtualChanged && !ShouldRun)
        {
            virtualDevice = nextVirtual;
            Raise(nameof(VirtualDevice), nameof(VirtualDeviceName), nameof(HasVirtualDevice), nameof(VirtualDeviceId), nameof(OnboardingNote));
        }
        if (outputChanged)
        {
            Log.Info($"Output device: {activeDevice?.Name ?? "none"} -> {nextActive?.Name ?? "none"}");
            activeDevice = nextActive;
            LoadSound();
            RaiseDevice();
        }
        RefreshDeviceItems();
        // Session notifications only cover the devices that existed when the watcher started.
        if (!devices.Where(d => d.IsActive).Select(d => d.Id).ToHashSet().SetEquals(watchedDeviceIds)) RestartSessionWatcher();
        if (outputChanged || virtualChanged ||
            (ShouldRun && !engine.IsActive && issue is EngineIssue.NoVirtualDevice or EngineIssue.NoOutputDevice))
            RestartEngine();
        else if (ShouldRun && engine.IsActive)
            TakeSystemDefault();
        else if (changes.OwnsDefault && !(ShouldRun && engine.IsActive))
            ReleaseSystemDefault(); // e.g. the real device NavTune had to give the default back to has just returned
    }

    private void RaiseDevice() =>
        Raise(nameof(ActiveDevice), nameof(ActiveDeviceName), nameof(ActiveDeviceGlyph), nameof(ActiveDeviceKind),
              nameof(ActiveDeviceIsBluetooth), nameof(IssueText));

    /// <summary>The Windows default changed: the user picked a device in Windows, or Windows switched by itself.</summary>
    private void OnDefaultChanged(string? id)
    {
        if (shuttingDown || id is null) return;
        var device = devices.FirstOrDefault(d => d.Id == id) ?? AudioDevices.Find(id);
        if (device is null || device.IsVirtual)
        {
            // Our own switch, or Windows fell back to the virtual device after the real one disappeared.
            ScheduleDevicesChanged();
            return;
        }
        config.LastRealDefaultId = id;
        ScheduleSave();
        // When NavTune is on this moves playback (through NavTune) to the device the user just picked;
        // the echo of NavTune giving the default back when it turns off changes nothing here.
        DevicesChanged();
    }

    // MARK: - Engine

    private void RestartEngine(bool isRecovery = false)
    {
        if (shuttingDown) return;
        if (PreviewMode)
        {
            EngineState = ShouldRun ? EngineState.Standby : EngineState.Off;
            SampleRate = 48000;
            RaiseStatus();
            return;
        }
        StopEngine();
        if (!isRecovery) recoveryAttempts = 0;
        SetIssue(EngineIssue.None);
        try
        {
            if (!ShouldRun)
            {
                ReleaseSystemDefault();
                return;
            }
            RefreshDevices();
            virtualDevice = AudioDevices.PickVirtualDevice(devices, config.VirtualDeviceId);
            Raise(nameof(VirtualDevice), nameof(VirtualDeviceName), nameof(HasVirtualDevice), nameof(VirtualDeviceId), nameof(OnboardingNote));
            if (virtualDevice is null)
            {
                ReleaseSystemDefault();
                SetIssue(EngineIssue.NoVirtualDevice);
                return;
            }
            if (VirtualDeviceOwner(virtualDevice) is { } owner)
            {
                ReleaseSystemDefault();
                SetIssue(EngineIssue.VirtualDeviceBusy, owner);
                return;
            }
            if (activeDevice is null)
            {
                SetIssue(EngineIssue.NoOutputDevice);
                return;
            }
            PushParams();
            engine.Start(virtualDevice.Id, activeDevice.Id, config.Latency);
            engineRunId = engine.RunId;
            SampleRate = engine.Info?.SampleRate ?? 0;
            TakeSystemDefault();
            try { volumeMirror = new VolumeMirror(virtualDevice.Id, activeDevice.Id, action => dispatcher.BeginInvoke(action)); }
            catch (Exception e) { Log.Error($"Volume sync unavailable: {e.Message}"); }
            EngineState = engine.State;
            Log.Info($"Started: {virtualDevice.Name} -> {activeDevice.Name}, {engine.Info}");
        }
        catch (Exception e)
        {
            // Whatever went wrong, never leave Windows pointing at a virtual device nobody is listening to.
            Log.Error($"Start failed on {activeDevice?.Name}: {e}");
            try { StopEngine(); } catch (Exception stop) { Log.Error($"Stopping after a failed start: {stop.Message}"); }
            ReleaseSystemDefault();
            SetIssue(EngineIssue.StartFailed, e is AudioException audio ? $"0x{audio.HResult32:X8}" : e.Message);
        }
        finally
        {
            ApplyAppRules();
            UpdateTimers();
            Raise(nameof(LatencyText));
            RaiseStatus();
        }
    }

    private void StopEngine()
    {
        engineRunId = 0;
        engine.Stop();
        volumeMirror?.Dispose();
        volumeMirror = null;
        spectrum.Clear();
        EngineState = EngineState.Off;
    }

    private void OnEngineEvent(EngineEvent e)
    {
        if (shuttingDown) return;
        if (e.Error is null)
        {
            EngineState = e.State;
            if (e.State == EngineState.Running) recoveryAttempts = 0;
            return;
        }
        Log.Error($"Engine stopped: {e.Error.Message}");
        StopEngine();
        recoveryAttempts++;
        if (recoveryAttempts <= 3)
        {
            var retry = Timer(TimeSpan.FromSeconds(recoveryAttempts), () => { if (ShouldRun && !engine.IsActive) RestartEngine(isRecovery: true); }, once: true);
            retry.Start();
        }
        else
        {
            ReleaseSystemDefault();
            SetIssue(EngineIssue.ConnectionLost);
        }
    }

    /// <summary>
    /// FxSound takes over the default speaker for its own virtual device; with both running they would fight over it
    /// (and with the same device, play everything twice). So any running FxSound counts as a conflict.
    /// </summary>
    private static string? VirtualDeviceOwner(AudioDevice device)
    {
        var processes = Process.GetProcessesByName("FxSound");
        bool running = processes.Length > 0;
        foreach (var p in processes) p.Dispose();
        return running ? "FxSound" : null;
    }

    /// <summary>Makes the virtual device the Windows default so every app plays into NavTune.</summary>
    private void TakeSystemDefault()
    {
        if (virtualDevice is null || activeDevice is null) return;
        // Record the intent before touching Windows, so a crash in between can still be undone.
        changes.OwnsDefault = true;
        changes.VirtualDeviceId = virtualDevice.Id;
        changes.RestoreDeviceId = activeDevice.Id;
        Recovery.Save(changes);
        Recovery.RestoreAtSignIn(true);
        try
        {
            string? current = AudioDevices.DefaultRenderDeviceId(ERole.Multimedia);
            string? console = AudioDevices.DefaultRenderDeviceId(ERole.Console);
            if (current == virtualDevice.Id && console == virtualDevice.Id) return;
            if (current is not null && devices.Any(d => d.Id == current && !d.IsVirtual)) config.LastRealDefaultId = current;
            if (IsDefaultContested())
            {
                Log.Info("Another app keeps changing the default device; pausing");
                StopEngine();
                ReleaseSystemDefault();
                SetIssue(EngineIssue.DefaultContested);
                return;
            }
            ignoreDefaultChangesUntil = DateTime.UtcNow.AddSeconds(2);
            DefaultDevice.Set(virtualDevice.Id);
        }
        catch (Exception e)
        {
            Log.Error($"Could not set the default device: {e.Message}");
        }
    }

    /// <summary>True when NavTune has had to take the default back more than six times in half a minute.</summary>
    private bool IsDefaultContested()
    {
        var now = DateTime.UtcNow;
        while (defaultRetakes.Count > 0 && now - defaultRetakes.Peek() > TimeSpan.FromSeconds(30)) defaultRetakes.Dequeue();
        defaultRetakes.Enqueue(now);
        return defaultRetakes.Count > 6;
    }

    /// <summary>
    /// Gives the default back to the real device NavTune was playing on. If that can't be done yet (no real device,
    /// or Windows refused), the record stays and the next device change, start or the watchdog finishes the job.
    /// </summary>
    private void ReleaseSystemDefault()
    {
        if (!changes.OwnsDefault) return;
        ignoreDefaultChangesUntil = DateTime.UtcNow.AddSeconds(2);
        if (Recovery.ReleaseDefault(changes, activeDevice?.Id))
        {
            Recovery.RestoreAtSignIn(false);
            releasePending = false;
        }
        else if (!releasePending)
        {
            releasePending = true;
            Log.Info("Default device not given back yet (no real device available); will retry");
        }
        Recovery.Save(changes);
    }

    private bool releasePending;

    private void UpdateTimers()
    {
        if (ShouldRun) healthTimer.Start();
        else healthTimer.Stop();
    }

    /// <summary>Catches what notifications can miss: FxSound starting or quitting, or the default being changed.</summary>
    private void HealthCheck()
    {
        if (!ShouldRun || shuttingDown) return;
        if (changes.OwnsDefault && !engine.IsActive) ReleaseSystemDefault();
        if (issue is EngineIssue.VirtualDeviceBusy or EngineIssue.NoVirtualDevice)
        {
            RefreshDevices();
            var candidate = AudioDevices.PickVirtualDevice(devices, config.VirtualDeviceId);
            if (candidate is not null && VirtualDeviceOwner(candidate) is null) RestartEngine();
            return;
        }
        if (!engine.IsActive || virtualDevice is null) return;
        if (VirtualDeviceOwner(virtualDevice) is { } owner)
        {
            StopEngine();
            ReleaseSystemDefault();
            SetIssue(EngineIssue.VirtualDeviceBusy, owner);
            return;
        }
        if (DateTime.UtcNow > ignoreDefaultChangesUntil && AudioDevices.DefaultRenderDeviceId() is { } current && current != virtualDevice.Id)
            OnDefaultChanged(current);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        dispatcher.BeginInvoke(() =>
        {
            var wake = Timer(TimeSpan.FromSeconds(1.5), () =>
            {
                DevicesChanged();
                if (ShouldRun) RestartEngine();
            }, once: true);
            wake.Start();
        });
    }

    // MARK: - Per-app sound

    public AppRule? RuleFor(string appId) => config.AppRules.FirstOrDefault(r => r.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase));

    public void SetAppMuted(string appId, string name, bool muted) =>
        UpdateRule(appId, name, rule => rule.Muted = muted);

    public void SetAppVolume(string appId, string name, float volume) =>
        UpdateRule(appId, name, rule => rule.Volume = Math.Clamp(volume, 0, 1));

    private void UpdateRule(string appId, string name, Action<AppRule> change)
    {
        var rule = RuleFor(appId)?.Clone() ?? new AppRule { AppId = appId, Name = name };
        change(rule);
        config.AppRules.RemoveAll(r => r.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase));
        if (!rule.IsNeutral) config.AppRules.Add(rule);
        ScheduleSave();
        ApplyAppRules();
        foreach (var app in Apps.Where(a => a.AppId == appId)) app.Refresh();
        RefreshQuickMute();
    }

    /// <summary>
    /// Applies the per-app mute and volume while NavTune is on; when it is off (or quitting), apps it changed
    /// go back to normal, like on macOS where turning NavTune off unmutes everything.
    /// </summary>
    private void ApplyAppRules(bool releasing = false)
    {
        if (PreviewMode || (shuttingDown && !releasing)) return;
        bool active = ShouldRun && !releasing;
        var ruled = active ? config.AppRules.Select(r => r.AppId).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        var targets = new Dictionary<string, (bool Muted, float Volume)>(StringComparer.OrdinalIgnoreCase);
        if (active)
            foreach (var rule in config.AppRules) targets[rule.AppId] = (rule.Muted, rule.Volume);
        // Apps NavTune changed before that no longer have a rule (or NavTune is off): back to how they were.
        var restore = changes.TouchedApps.Where(a => !ruled.Contains(a)).ToList();
        foreach (var app in restore) targets.TryAdd(app, changes.OriginalOf(app));
        // Ruled apps count as touched right away, so a crash still restores them.
        changes.TouchedApps = ruled.Union(restore, StringComparer.OrdinalIgnoreCase).ToList();
        Recovery.Save(changes);
        if (targets.Count == 0) return;

        var job = new RulesJob(++rulesSequence, targets, ruled, restore,
            new Dictionary<string, AppVolume>(changes.OriginalApps, StringComparer.OrdinalIgnoreCase));
        if (releasing)
        {
            // Let a change still in flight finish first, so it can't undo the release afterwards.
            lock (rulesGate) pendingRulesJob = null;
            try { rulesWorker.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
            FinishRules(job, AppSessions.Apply(job.Targets, job.Originals));
            return;
        }
        // One worker applies changes in order, always the newest state: quick toggles and slider drags can't race.
        lock (rulesGate)
        {
            pendingRulesJob = job;
            rulesWorker = rulesWorker.ContinueWith(_ => RunPendingRules(), TaskScheduler.Default);
        }
    }

    private sealed record RulesJob(int Sequence, Dictionary<string, (bool Muted, float Volume)> Targets,
        HashSet<string> Ruled, List<string> Restore, Dictionary<string, AppVolume> Originals);

    private readonly object rulesGate = new();
    private RulesJob? pendingRulesJob;
    private Task rulesWorker = Task.CompletedTask;
    private int rulesSequence;

    private void RunPendingRules()
    {
        RulesJob? job;
        lock (rulesGate)
        {
            job = pendingRulesJob;
            pendingRulesJob = null;
        }
        if (job is null) return;
        try
        {
            var applied = AppSessions.Apply(job.Targets, job.Originals);
            dispatcher.BeginInvoke(() =>
            {
                if (job.Sequence == rulesSequence) FinishRules(job, applied);
            });
        }
        catch (Exception e)
        {
            Log.Error($"Applying app rules failed: {e.Message}");
        }
    }

    /// <summary>
    /// Records what a rules pass did. Windows remembers an app's mute and volume for its next launch, so an app that
    /// isn't running now keeps its entry and is restored as soon as it plays again (session notifications call back).
    /// </summary>
    private void FinishRules(RulesJob job, HashSet<string> applied)
    {
        foreach (var (app, original) in job.Originals) changes.OriginalApps.TryAdd(app, original);
        foreach (var app in job.Restore.Where(applied.Contains)) changes.OriginalApps.Remove(app);
        changes.TouchedApps = job.Ruled.Union(job.Restore.Where(a => !applied.Contains(a)), StringComparer.OrdinalIgnoreCase).ToList();
        Recovery.Save(changes);
    }

    /// <summary>(Re)starts session notifications for the current devices, so rules apply the moment an app plays.</summary>
    private void RestartSessionWatcher()
    {
        if (PreviewMode || shuttingDown) return;
        var old = sessionWatcher;
        sessionWatcher = null;
        watchedDeviceIds = devices.Where(d => d.IsActive).Select(d => d.Id).ToHashSet();
        int sequence = ++watcherSequence;
        // Session notifications are only delivered to clients that register from the multithreaded apartment.
        Task.Run(() =>
        {
            old?.Dispose();
            try
            {
                var watcher = new SessionWatcher();
                watcher.SessionCreated += () => dispatcher.BeginInvoke(() =>
                {
                    rulesTimer.Stop();
                    rulesTimer.Start();
                });
                dispatcher.BeginInvoke(() =>
                {
                    // Only the newest watcher is kept; one created for an older device list is let go.
                    if (shuttingDown || sequence != watcherSequence)
                    {
                        Task.Run(watcher.Dispose);
                        return;
                    }
                    var previous = sessionWatcher;
                    sessionWatcher = watcher;
                    if (previous is not null) Task.Run(previous.Dispose);
                });
            }
            catch (Exception e)
            {
                Log.Error($"Session notifications unavailable: {e.Message}");
            }
        });
    }

    /// <summary>Views that show the app list call this while visible; the list refreshes once a second meanwhile.</summary>
    public void WatchApps(bool watching)
    {
        appsViewers = Math.Max(0, appsViewers + (watching ? 1 : -1));
        if (appsViewers > 0)
        {
            RefreshApps();
            appsTimer.Start();
        }
        else
        {
            appsTimer.Stop();
        }
    }

    public void RefreshApps()
    {
        List<AudioApp> live;
        try { live = AppSessions.List(); }
        catch (Exception e)
        {
            Log.Error($"Listing apps failed: {e.Message}");
            live = [];
        }
        var entries = live.Select(a => (a.AppId, a.Name, a.ExePath, a.IsPlaying, a.IsRunning)).ToList();
        foreach (var rule in config.AppRules.Where(r => !live.Any(a => a.AppId.Equals(r.AppId, StringComparison.OrdinalIgnoreCase))))
            entries.Add((rule.AppId, rule.Name, null, false, false));

        // Update in place so rows (and their sliders) don't flicker while the list refreshes.
        for (int i = Apps.Count - 1; i >= 0; i--)
            if (!entries.Any(e => e.AppId.Equals(Apps[i].AppId, StringComparison.OrdinalIgnoreCase))) Apps.RemoveAt(i);
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var item = Apps.FirstOrDefault(a => a.AppId.Equals(entry.AppId, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                item = new AppItem(this, entry.AppId, entry.Name, entry.ExePath);
                Apps.Insert(Math.Min(i, Apps.Count), item);
            }
            else
            {
                int at = Apps.IndexOf(item);
                if (at != i && i < Apps.Count) Apps.Move(at, i);
                item.Rename(entry.Name);
            }
            item.IsPlaying = entry.IsPlaying;
            item.IsRunning = entry.IsRunning;
        }
        Raise(nameof(HasApps));
        RefreshQuickMute();
    }

    public bool HasApps => Apps.Count > 0;

    private void RefreshQuickMute()
    {
        var wanted = Apps.Where(a => a.IsPlaying || a.IsMuted).ToList();
        if (wanted.SequenceEqual(QuickMuteApps)) return;
        QuickMuteApps.Clear();
        foreach (var app in wanted) QuickMuteApps.Add(app);
        Raise(nameof(HasQuickMuteApps));
    }

    public bool HasQuickMuteApps => QuickMuteApps.Count > 0;

    // MARK: - App behaviour

    public IReadOnlyList<LanguageOption> LanguageOptions => Languages;

    public LanguageOption Language
    {
        get => Languages.First(l => l.Language == L10n.ParseLanguage(config.Language));
        set
        {
            if (value is null || config.Language == value.Language.Code()) return;
            config.Language = value.Language.Code();
            ScheduleSave();
            L10n.Apply(value.Language);
            Raise();
        }
    }

    /// <summary>Picks Vietnamese or English directly (onboarding).</summary>
    public bool IsVietnamese
    {
        get => L10n.IsVietnamese;
        set => Language = Languages.First(l => l.Language == (value ? AppLanguage.Vietnamese : AppLanguage.English));
    }

    private void OnLanguageChanged()
    {
        RaiseAll();
        foreach (var preset in Presets) preset.Refresh();
        foreach (var device in OutputDevices) device.Refresh();
        foreach (var app in Apps) app.Refresh();
        foreach (var band in ParametricBands) band.Refresh();
    }

    public bool LaunchAtLogin
    {
        get
        {
            try { return AutoStart.IsEnabled; }
            catch { return false; }
        }
        set
        {
            try { AutoStart.Set(value); }
            catch (Exception e) { Log.Error($"Launch at login: {e.Message}"); }
            Raise();
        }
    }

    public bool CloseToTray
    {
        get => config.CloseToTray;
        set
        {
            if (config.CloseToTray == value) return;
            config.CloseToTray = value;
            ScheduleSave();
            Raise();
        }
    }

    /// <summary>True the first time only: the window was closed to the tray and the user should be told.</summary>
    public bool TakeTrayHint()
    {
        if (config.TrayHintShown) return false;
        config.TrayHintShown = true;
        ScheduleSave();
        return true;
    }

    public static string Version => typeof(AppState).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.3.0";

    public string VersionText => Tr($"Phiên bản {Version} cho Windows", $"Version {Version} for Windows");

    /// <summary>What the welcome screen says about the virtual device NavTune will use.</summary>
    public string OnboardingNote => virtualDevice is { } device
        ? Tr($"NavTune đặt thiết bị ảo “{device.Name}” làm loa mặc định để nhận âm thanh của mọi ứng dụng, xử lý rồi phát ra loa hoặc tai nghe của anh. Tắt NavTune là Windows trở lại như cũ. Âm thanh chỉ xử lý trên máy, không ghi lại.",
             $"NavTune makes the virtual device “{device.Name}” the default speaker to receive every app's sound, processes it and plays it on your speakers or headphones. Turn NavTune off and Windows is back to normal. Audio is processed only on this PC and never recorded.")
        : Tr("NavTune cần một thiết bị âm thanh ảo để nhận âm thanh của mọi ứng dụng. Hãy cài VB-CABLE (miễn phí, chỉ vài giây), rồi bấm “Bắt đầu sử dụng”.",
             "NavTune needs a virtual audio device to receive every app's sound. Install VB-CABLE (free, takes seconds), then click “Get started”.");

    // MARK: - Persistence and shutdown

    private void ScheduleSave()
    {
        saveTimer.Stop();
        saveTimer.Start();
    }

    private void Save()
    {
        try { SettingsStore.Save(config); }
        catch (Exception e) { Log.Error($"Saving settings failed: {e.Message}"); }
    }

    /// <summary>Stops the engine and gives Windows back its default device and app volumes.</summary>
    public void Shutdown()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        StopTransition();
        detector.Dispose();
        saveTimer.Stop();
        healthTimer.Stop();
        appsTimer.Stop();
        rulesTimer.Stop();
        devicesTimer.Stop();
        Save();
        try { StopEngine(); } catch (Exception e) { Log.Error($"Stopping: {e.Message}"); }
        try { ReleaseSystemDefault(); } catch (Exception e) { Log.Error($"Giving the default device back: {e.Message}"); }
        try { ApplyAppRules(releasing: true); } catch (Exception e) { Log.Error($"Restoring apps: {e.Message}"); }
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        deviceMonitor?.Dispose();
        var watcher = sessionWatcher;
        if (watcher is not null) Task.Run(watcher.Dispose).Wait(TimeSpan.FromSeconds(1));
        Log.Info("Shut down");
    }
}
