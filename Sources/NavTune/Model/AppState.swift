import AppKit
import CoreAudio
import Observation
import ServiceManagement
import SwiftUI

enum EngineState {
    case off
    /// Processing audio.
    case running
    /// Set up but paused because nothing is playing: no CPU use, and the Mac and headphones can sleep.
    case standby
}

@MainActor
@Observable
final class AppState {
    static let shared = AppState()

    private enum Keys {
        static let isOn = "isOn"
        static let onboarded = "hasOnboarded"
        static let preferredDevice = "preferredDeviceUID"
        static let perDevice = "perDeviceProfiles"
        static let showInDock = "showInDock"
        static let userPresets = "userPresets"
        static let appRules = "appRules"
        static let autoPreset = "autoPreset"
        static let settingsPrefix = "settings."
    }

    /// How long nothing must play before NavTune goes to standby.
    private static let idleDelay: TimeInterval = 10

    var isOn: Bool {
        didSet {
            guard isOn != oldValue else { return }
            defaults.set(isOn, forKey: Keys.isOn)
            restartEngine()
        }
    }
    var bypass = false {
        didSet {
            guard bypass != oldValue else { return }
            pushParams()
            applyRouting()
        }
    }
    var settings = SoundSettings() {
        didSet {
            guard settings != oldValue else { return }
            pushParams()
            scheduleSave()
        }
    }
    var preferredDeviceUID: String? {
        didSet {
            guard preferredDeviceUID != oldValue else { return }
            defaults.set(preferredDeviceUID, forKey: Keys.preferredDevice)
            devicesChanged()
        }
    }
    var perDeviceProfiles: Bool {
        didSet {
            guard perDeviceProfiles != oldValue else { return }
            defaults.set(perDeviceProfiles, forKey: Keys.perDevice)
            loadSettings()
        }
    }
    var showInDock: Bool {
        didSet {
            guard showInDock != oldValue else { return }
            defaults.set(showInDock, forKey: Keys.showInDock)
            applyActivationPolicy()
        }
    }
    var hasOnboarded: Bool {
        didSet { defaults.set(hasOnboarded, forKey: Keys.onboarded) }
    }
    var autoPreset: Bool {
        didSet {
            guard autoPreset != oldValue else { return }
            defaults.set(autoPreset, forKey: Keys.autoPreset)
            if detectedContent != nil { detectedContent = nil }
            resetDetector()
        }
    }
    var language: AppLanguage {
        didSet {
            guard language != oldValue else { return }
            defaults.set(language.rawValue, forKey: L10n.defaultsKey)
            L10n.shared.isVietnamese = L10n.resolve(language)
        }
    }

    private(set) var engineState = EngineState.off
    private(set) var devices: [AudioDevice] = []
    private(set) var activeDevice: AudioDevice?
    private(set) var userPresets: [Preset] = [] {
        didSet { allPresets = Preset.builtIns + userPresets }
    }
    private(set) var allPresets = Preset.builtIns
    private(set) var appRules: [AppRule] = []
    private(set) var audioApps: [AudioApp] = []
    private(set) var detectedContent: ContentKind?
    private(set) var sampleRate: Double = 0
    private(set) var errorMessage: String?
    private(set) var noSignal = false
    private(set) var launchAtLogin = SMAppService.mainApp.status == .enabled

    /// Set by the menu bar icon so the app can open its window when relaunched from Finder.
    @ObservationIgnored var openMainWindow: (() -> Void)?

    private let router = AudioRouter()
    private let detector = ContentDetector()
    private let activity = ActivityMonitor()
    private let spectrum = SpectrumModel()
    private let defaults = UserDefaults.standard

    @ObservationIgnored private var processes: [AudioProcess] = []
    /// Processes NavTune doesn't process: apps set to "untouched" or muted.
    @ObservationIgnored private var ignoredProcesses: Set<AudioObjectID> = []
    @ObservationIgnored private var monitorTimer: Timer?
    @ObservationIgnored private var healthTimer: Timer?
    @ObservationIgnored private var idleWork: DispatchWorkItem?
    @ObservationIgnored private var saveWork: DispatchWorkItem?
    @ObservationIgnored private var processRefresh: DispatchWorkItem?
    @ObservationIgnored private var lastCallbackCount: UInt64 = 0
    @ObservationIgnored private var silentTicks = 0
    @ObservationIgnored private var healthTicks = 0
    @ObservationIgnored private var recoveryAttempts = 0
    @ObservationIgnored private var detectorPosition = 0
    @ObservationIgnored private var detectorBuffer = [Float](repeating: 0, count: AudioProcessor.spectrumCapacity)
    @ObservationIgnored private var pendingContent: ContentKind?
    @ObservationIgnored private var pendingCount = 0
    @ObservationIgnored private var transition: Timer?
    @ObservationIgnored private var transitionStart = SoundSettings()
    @ObservationIgnored private var transitionTarget = SoundSettings()
    @ObservationIgnored private var transitionStep = 0

    private init() {
        let onboarded = defaults.bool(forKey: Keys.onboarded)
        hasOnboarded = onboarded
        isOn = onboarded && (defaults.object(forKey: Keys.isOn) as? Bool ?? true)
        preferredDeviceUID = defaults.string(forKey: Keys.preferredDevice)
        perDeviceProfiles = defaults.object(forKey: Keys.perDevice) as? Bool ?? true
        showInDock = defaults.bool(forKey: Keys.showInDock)
        autoPreset = defaults.bool(forKey: Keys.autoPreset)
        language = AppLanguage(rawValue: defaults.string(forKey: L10n.defaultsKey) ?? "") ?? .system

        userPresets = decode([Preset].self, key: Keys.userPresets) ?? []
        allPresets = Preset.builtIns + userPresets
        appRules = decode([AppRule].self, key: Keys.appRules) ?? []
        devices = AudioDevice.outputDevices()
        activeDevice = resolveActiveDevice()
        detector.onResult = { [weak self] kind in self?.handleDetection(kind) }
        activity.onChange = { [weak self] in self?.activityChanged() }
        loadSettings()
        refreshProcesses()
        restartEngine()
        observeSystem()
    }

    /// The engine is set up (processing or in standby).
    var isActive: Bool { engineState != .off }

    var isProcessing: Bool { engineState == .running }

    var currentPresetName: String {
        allPresets.first { $0.id == settings.presetID }?.displayName ?? tr("Tuỳ chỉnh", "Custom")
    }

    /// Smoothed output level of the loudest chain, for meters. Not observed: meters poll it per frame.
    var outputLevel: Float {
        isProcessing ? router.processors.map(\.currentOutputLevel).max() ?? 0 : 0
    }

    /// Spectrum bands for the frame at `date`. Not observed: graphs poll it per frame while visible.
    func spectrumBands(at date: Date) -> [Float] {
        let source = isProcessing ? router.processors.max { $0.currentOutputLevel < $1.currentOutputLevel } : nil
        return spectrum.bands(at: date, from: source, sampleRate: sampleRate)
    }

    func completeOnboarding() {
        hasOnboarded = true
        isOn = true
    }

    func shutdown() {
        flushSave()
        detector.stop()
        router.stop()
        router.unmuteAll()
    }

    // MARK: - EQ & effects

    /// `automatic` is used by content detection: it glides to the new curve and keeps auto mode on.
    func apply(_ preset: Preset, automatic: Bool = false) {
        if !automatic && autoPreset { autoPreset = false }
        stopTransition()
        var target = settings
        target.presetID = preset.id
        target.gains = preset.gains
        target.preamp = preset.preamp
        if let bands = preset.bands {
            target.mode = .parametric
            target.bands = bands
        } else {
            target.mode = .graphic
        }
        if automatic && settings.mode == .graphic {
            startTransition(to: target)
        } else {
            settings = target
        }
    }

    func applyPreset(id: String) {
        if let preset = allPresets.first(where: { $0.id == id }) { apply(preset) }
    }

    func setGain(_ value: Float, band: Int) {
        edit { $0.gains[band] = value }
    }

    func setMode(_ mode: EQMode) {
        guard settings.mode != mode else { return }
        edit { $0.mode = mode }
    }

    func updateBand(id: EQBand.ID, _ change: (inout EQBand) -> Void) {
        guard let index = settings.bands.firstIndex(where: { $0.id == id }) else { return }
        edit { change(&$0.bands[index]) }
    }

    /// Adds a bell filter in the widest gap of the spectrum and returns its id.
    @discardableResult
    func addBand() -> EQBand.ID? {
        guard settings.bands.count < SoundSettings.maxParametricBands else { return nil }
        let used = ([20.0] + settings.bands.map(\.frequency).sorted() + [20000.0]).map(log10)
        var gap = (width: 0.0, center: 3.0)
        for (a, b) in zip(used, used.dropFirst()) where b - a > gap.width {
            gap = (b - a, (a + b) / 2)
        }
        let band = EQBand(kind: .peak, frequency: pow(10, gap.center).rounded(), q: 1)
        edit { $0.bands.append(band) }
        return band.id
    }

    func removeBand(id: EQBand.ID) {
        edit { $0.bands.removeAll { $0.id == id } }
    }

    func resetEQ() {
        if settings.mode == .parametric {
            edit { $0.bands = SoundSettings.defaultBands }
        } else {
            apply(Preset.builtIns[0])
        }
    }

    func resetEffects() {
        settings.resetEffects()
    }

    func resetAllSound() {
        saveWork?.cancel()
        saveWork = nil
        for key in defaults.dictionaryRepresentation().keys where key.hasPrefix(Keys.settingsPrefix) {
            defaults.removeObject(forKey: key)
        }
        settings = SoundSettings()
    }

    /// A manual EQ change: marks the curve as custom and turns off automatic presets.
    private func edit(_ change: (inout SoundSettings) -> Void) {
        stopTransition()
        if autoPreset { autoPreset = false }
        var s = settings
        change(&s)
        s.presetID = Preset.customID
        settings = s
    }

    private func startTransition(to target: SoundSettings) {
        transitionStart = settings
        transitionTarget = target
        transitionStep = 0
        transition = Timer.scheduledTimer(withTimeInterval: 1.0 / 30, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.transitionTick() }
        }
    }

    private func transitionTick() {
        let steps = 36
        transitionStep += 1
        let t = Float(transitionStep) / Float(steps)
        let eased = t * t * (3 - 2 * t)
        var s = transitionTarget
        s.gains = zip(transitionStart.gains, transitionTarget.gains).map { $0 + ($1 - $0) * eased }
        s.preamp = transitionStart.preamp + (transitionTarget.preamp - transitionStart.preamp) * eased
        settings = s
        if transitionStep >= steps { stopTransition() }
    }

    private func stopTransition() {
        transition?.invalidate()
        transition = nil
    }

    // MARK: - Content detection

    private func resetDetector() {
        pendingContent = nil
        pendingCount = 0
        detectorPosition = router.main.processor.sampleWritePosition
        if autoPreset && isProcessing {
            detector.reset(sampleRate: sampleRate)
        } else {
            detector.stop()
        }
    }

    private func feedDetector() {
        guard autoPreset, isProcessing, !bypass else { return }
        let count = router.main.processor.readSamples(from: &detectorPosition, into: &detectorBuffer)
        if count > 0 { detector.feed(Array(detectorBuffer[0..<count])) }
    }

    /// Switches preset only after the same kind is detected three windows in a row (~4–5 s).
    private func handleDetection(_ kind: ContentKind?) {
        guard autoPreset, let kind else { return }
        if kind == pendingContent {
            pendingCount += 1
        } else {
            pendingContent = kind
            pendingCount = 1
        }
        guard pendingCount >= 3 else { return }
        if detectedContent != kind { detectedContent = kind }
        if settings.presetID != kind.presetID, let preset = Preset.builtIns.first(where: { $0.id == kind.presetID }) {
            apply(preset, automatic: true)
        }
    }

    // MARK: - User presets

    func saveCurrentAsPreset(name: String) {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        let preset = Preset.user(name: trimmed.isEmpty ? tr("Preset của tôi", "My preset") : trimmed,
                                 gains: settings.gains, preamp: settings.preamp,
                                 bands: settings.mode == .parametric ? settings.bands : nil)
        userPresets.append(preset)
        saveUserPresets()
        settings.presetID = preset.id
    }

    func renamePreset(id: String, to name: String) {
        guard let index = userPresets.firstIndex(where: { $0.id == id }) else { return }
        userPresets[index].name = name
        saveUserPresets()
    }

    func deletePreset(id: String) {
        userPresets.removeAll { $0.id == id }
        saveUserPresets()
        if settings.presetID == id { settings.presetID = Preset.customID }
    }

    func exportPresets(to url: URL) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(userPresets).write(to: url)
    }

    /// Returns the number of presets imported.
    func importPresets(from url: URL) throws -> Int {
        let imported = try JSONDecoder().decode([Preset].self, from: Data(contentsOf: url))
            .filter { $0.gains.count == SoundSettings.bandCount }
            .map { Preset.user(name: $0.name, gains: $0.gains, preamp: $0.preamp, bands: $0.bands) }
        userPresets += imported
        saveUserPresets()
        return imported.count
    }

    private func saveUserPresets() {
        encode(userPresets, key: Keys.userPresets)
    }

    // MARK: - Per-app rules

    /// Apps currently using audio, plus apps that have a rule but aren't running.
    var appEntries: [AudioApp] {
        var entries = audioApps
        for rule in appRules where !entries.contains(where: { $0.bundleID == rule.bundleID }) {
            entries.append(AudioApp(bundleID: rule.bundleID, name: rule.name,
                                    icon: AudioProcesses.icon(for: rule.bundleID), isPlaying: false, isRunning: false))
        }
        return entries
    }

    func rule(for bundleID: String) -> AppRule? {
        appRules.first { $0.bundleID == bundleID }
    }

    func setRule(_ rule: AppRule) {
        if let index = appRules.firstIndex(where: { $0.bundleID == rule.bundleID }) {
            guard appRules[index] != rule else { return }
            appRules[index] = rule
        } else {
            appRules.append(rule)
        }
        rulesChanged()
    }

    func removeRule(bundleID: String) {
        appRules.removeAll { $0.bundleID == bundleID }
        rulesChanged()
    }

    /// Sets how an app is handled; nil means the main EQ.
    func setRuleMode(_ mode: AppRule.Mode?, for app: AudioApp) {
        switch mode {
        case nil:
            removeRule(bundleID: app.bundleID)
        case .mute:
            setMuted(true, app: app)
        case let mode?:
            var r = rule(for: app.bundleID) ?? AppRule(bundleID: app.bundleID, name: app.name)
            r.mode = mode
            r.modeBeforeMute = nil
            setRule(r)
        }
    }

    func isMuted(_ bundleID: String) -> Bool {
        rule(for: bundleID)?.mode == .mute
    }

    /// Mutes an app on the speakers, or unmutes it back to how it was handled before.
    func setMuted(_ muted: Bool, app: AudioApp) {
        let existing = rule(for: app.bundleID)
        if muted {
            setRule(AppRule.muting(existing, bundleID: app.bundleID, name: app.name))
        } else if let existing, existing.mode == .mute {
            if let restored = existing.unmuted() {
                setRule(restored)
            } else {
                removeRule(bundleID: app.bundleID)
            }
        }
    }

    /// Apps for the quick mute buttons: those playing now, plus those muted.
    var quickMuteApps: [AudioApp] {
        appEntries.filter { $0.isPlaying || isMuted($0.bundleID) }
    }

    private func rulesChanged() {
        encode(appRules, key: Keys.appRules)
        updateIgnoredProcesses()
        applyRouting()
        activityChanged()
    }

    private func soundSettings(for rule: AppRule) -> SoundSettings {
        var s = SoundSettings()
        if let preset = allPresets.first(where: { $0.id == rule.presetID }) {
            s.presetID = preset.id
            s.gains = preset.gains
            s.preamp = preset.preamp
            if let bands = preset.bands {
                s.mode = .parametric
                s.bands = bands
            }
        }
        if rule.volume >= 0 {
            s.boost = rule.volume
        } else {
            s.preamp += rule.volume
        }
        return s
    }

    /// Re-reads the process list (called when Core Audio reports a change), then re-routes.
    private func refreshProcesses() {
        let current = AudioProcesses.current()
        guard current != processes else { return }
        processes = current
        updateIgnoredProcesses()
        activity.track(current.map(\.id))
        updateAudioApps()
        applyRouting()
    }

    private func updateIgnoredProcesses() {
        let ignored = Set(appRules.filter { $0.mode != .preset }.map(\.bundleID))
        ignoredProcesses = Set(processes.filter { $0.ownerBundleID.map(ignored.contains) ?? false }.map(\.id))
    }

    private func updateAudioApps() {
        let apps = AudioProcesses.apps(from: processes, playing: activity.playing)
        if apps.map(\.bundleID) != audioApps.map(\.bundleID) || apps.map(\.isPlaying) != audioApps.map(\.isPlaying) {
            audioApps = apps
        }
    }

    /// Refreshes at once, so a muted app's new audio process is silenced before it is heard,
    /// then once more shortly after to pick up the rest of a burst of changes.
    private func scheduleProcessRefresh() {
        guard processRefresh == nil else { return }
        refreshProcesses()
        let work = DispatchWorkItem { [weak self] in
            MainActor.assumeIsolated {
                self?.processRefresh = nil
                self?.refreshProcesses()
            }
        }
        processRefresh = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15, execute: work)
    }

    /// Also runs while the engine is stopped, so muted apps stay muted; turning NavTune off unmutes them.
    private func applyRouting() {
        router.route(rules: isOn ? appRules : [], processes: processes, bypass: bypass) { [unowned self] in
            soundSettings(for: $0)
        }
    }

    // MARK: - Standby

    /// Audio NavTune would process is playing (apps set to "untouched" or muted don't count).
    private var isAudioPlaying: Bool {
        !activity.playing.subtracting(ignoredProcesses).isEmpty
    }

    private func activityChanged() {
        updateAudioApps()
        guard isActive else { return }
        if isAudioPlaying {
            idleWork?.cancel()
            idleWork = nil
            if engineState == .standby { wake() }
        } else {
            scheduleStandby()
        }
    }

    private func scheduleStandby() {
        guard idleWork == nil, engineState == .running else { return }
        let work = DispatchWorkItem { [weak self] in
            MainActor.assumeIsolated { self?.enterStandby() }
        }
        idleWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.idleDelay, execute: work)
    }

    private func enterStandby() {
        idleWork = nil
        guard engineState == .running, !isAudioPlaying else { return }
        router.suspend()
        engineState = .standby
        detector.stop()
        if noSignal { noSignal = false }
        updateTimers()
        Log.engine.notice("Standby: nothing is playing")
    }

    private func wake() {
        do {
            try router.resume()
            engineState = .running
            lastCallbackCount = router.main.processor.callbackCount
            silentTicks = 0
            resetDetector()
            updateTimers()
            Log.engine.notice("Resumed from standby")
        } catch {
            Log.engine.error("Resume failed: \(error.localizedDescription, privacy: .public)")
            restartEngine(isRecovery: true)
        }
    }

    // MARK: - Per-device settings

    private var settingsKey: String {
        if perDeviceProfiles, let uid = activeDevice?.uid { return Keys.settingsPrefix + uid }
        return Keys.settingsPrefix + "global"
    }

    func hasSavedSettings(for device: AudioDevice) -> Bool {
        defaults.data(forKey: Keys.settingsPrefix + device.uid) != nil
    }

    private func loadSettings() {
        flushSave()
        stopTransition()
        settings = decode(SoundSettings.self, key: settingsKey) ?? SoundSettings()
        saveWork?.cancel()
        saveWork = nil
        pushParams()
    }

    /// Saves once dragging pauses instead of on every slider tick. Key and value are captured now,
    /// so a device switch in between can't write one device's settings under another's key.
    private func scheduleSave() {
        saveWork?.cancel()
        let key = settingsKey
        let value = settings
        let work = DispatchWorkItem { [weak self] in
            MainActor.assumeIsolated {
                self?.encode(value, key: key)
                self?.saveWork = nil
            }
        }
        saveWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5, execute: work)
    }

    private func flushSave() {
        guard let work = saveWork else { return }
        work.perform()
        work.cancel()
        saveWork = nil
    }

    private func pushParams() {
        router.main.processor.update(settings: settings, bypass: bypass)
    }

    private func decode<T: Decodable>(_ type: T.Type, key: String) -> T? {
        defaults.data(forKey: key).flatMap { try? JSONDecoder().decode(type, from: $0) }
    }

    private func encode<T: Encodable>(_ value: T, key: String) {
        if let data = try? JSONEncoder().encode(value) { defaults.set(data, forKey: key) }
    }

    // MARK: - App behaviour

    func setLaunchAtLogin(_ enabled: Bool) {
        do {
            if enabled {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
        } catch {
            errorMessage = tr("Không thay đổi được chế độ khởi động cùng máy: ", "Couldn't change launch at login: ")
                + error.localizedDescription
        }
        launchAtLogin = SMAppService.mainApp.status == .enabled
    }

    func applyActivationPolicy() {
        NSApp.setActivationPolicy(showInDock ? .regular : .accessory)
    }

    func openPrivacySettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
            NSWorkspace.shared.open(url)
        }
    }

    // MARK: - Devices & engine

    private func resolveActiveDevice() -> AudioDevice? {
        if let uid = preferredDeviceUID, let device = devices.first(where: { $0.uid == uid }) {
            return device
        }
        guard let id = try? AudioObjectID.defaultOutputDevice(), id != .unknown else { return nil }
        return devices.first { $0.id == id } ?? AudioDevice(id: id)
    }

    private func devicesChanged() {
        let list = AudioDevice.outputDevices()
        if list != devices { devices = list }
        let newDevice = resolveActiveDevice()
        guard newDevice?.id != activeDevice?.id else { return }
        flushSave()
        activeDevice = newDevice
        loadSettings()
        restartEngine()
    }

    private func restartEngine(isRecovery: Bool = false) {
        router.stop()
        idleWork?.cancel()
        idleWork = nil
        engineState = .off
        if !isRecovery { recoveryAttempts = 0 }
        if errorMessage != nil { errorMessage = nil }
        if noSignal { noSignal = false }
        silentTicks = 0
        defer {
            applyRouting()
            resetDetector()
            updateTimers()
        }
        guard isOn else { return }
        guard let device = activeDevice else {
            errorMessage = tr("Không tìm thấy thiết bị phát âm thanh.", "No audio output device found.")
            return
        }
        do {
            try router.start(device: device.id)
            if sampleRate != router.sampleRate { sampleRate = router.sampleRate }
            pushParams()
            lastCallbackCount = router.main.processor.callbackCount
            engineState = .running
            if !isAudioPlaying { scheduleStandby() }
            Log.engine.notice("Started on \(device.name, privacy: .public) at \(Int(self.sampleRate)) Hz")
        } catch {
            Log.engine.error("Start failed on \(device.name, privacy: .public): \(error.localizedDescription, privacy: .public)")
            errorMessage = error.localizedDescription + ". "
                + tr("Thiết bị “\(device.name)” có thể chưa sẵn sàng hoặc không được hỗ trợ.",
                     "“\(device.name)” may not be ready or supported.")
        }
    }

    private func observeSystem() {
        AudioObjectID.observeSystem(kAudioHardwarePropertyDefaultOutputDevice) { [weak self] in
            self?.devicesChanged()
        }
        AudioObjectID.observeSystem(kAudioHardwarePropertyDevices) { [weak self] in
            self?.devicesChanged()
        }
        AudioObjectID.observeSystem(kAudioHardwarePropertyProcessObjectList) { [weak self] in
            self?.scheduleProcessRefresh()
        }
        // An app's audio process can appear before the app is known as running; look again once it is.
        NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didLaunchApplicationNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.scheduleProcessRefresh() }
        }
        NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didWakeNotification, object: nil, queue: .main
        ) { [weak self] _ in
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
                MainActor.assumeIsolated {
                    self?.devicesChanged()
                    self?.restartEngine()
                }
            }
        }
    }

    /// Timers run only while they have work: the 0.5 s monitor while processing, the health check while on.
    private func updateTimers() {
        if isProcessing, monitorTimer == nil {
            let timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.monitorTick() }
            }
            timer.tolerance = 0.1
            monitorTimer = timer
        } else if !isProcessing {
            monitorTimer?.invalidate()
            monitorTimer = nil
        }

        if isActive, healthTimer == nil {
            let timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.healthCheck() }
            }
            timer.tolerance = 0.5
            healthTimer = timer
        } else if !isActive {
            healthTimer?.invalidate()
            healthTimer = nil
        }
    }

    private func monitorTick() {
        guard isProcessing else { return }
        // Something claims to be playing, yet the tap delivers silence: most likely the permission is missing.
        let input = router.processors.map(\.currentInputLevel).max() ?? 0
        silentTicks = isAudioPlaying && input < 0.0001 ? silentTicks + 1 : 0
        let silent = silentTicks >= 40
        if silent != noSignal {
            noSignal = silent
            if silent { Log.engine.notice("No audio from the tap while apps play (permission missing?)") }
        }
        feedDetector()
    }

    /// Restarts the engine when audio stops flowing or the device sample rate changes,
    /// and re-checks playback activity in case a notification was missed.
    private func healthCheck() {
        healthTicks += 1
        switch engineState {
        case .off:
            return
        case .standby:
            activity.resync()
            return
        case .running:
            if healthTicks % 5 == 0 { activity.resync() }
        }
        guard let device = activeDevice else { return }

        let count = router.main.processor.callbackCount
        let stalled = count == lastCallbackCount
        lastCallbackCount = count
        let rate = device.id.nominalSampleRate
        let rateChanged = rate > 0 && abs(rate - sampleRate) > 1

        guard stalled || rateChanged else {
            recoveryAttempts = 0
            return
        }
        recoveryAttempts += 1
        Log.engine.notice("Recovering (stalled: \(stalled), rate changed: \(rateChanged)), attempt \(self.recoveryAttempts)")
        if recoveryAttempts <= 3 {
            restartEngine(isRecovery: true)
        } else {
            router.stop()
            engineState = .off
            updateTimers()
            errorMessage = tr("Mất kết nối với “\(device.name)”. Hãy tắt rồi bật lại NavTune.",
                              "Lost connection to “\(device.name)”. Turn NavTune off and on again.")
        }
    }
}
