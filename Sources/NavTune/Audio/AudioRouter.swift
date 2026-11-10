import CoreAudio
import Foundation

/// Where each app's audio goes. Pure, so it can be tested without Core Audio.
struct RoutingPlan: Equatable {
    /// Every process that has a rule: they all stay out of the main chain.
    var excluded: [AudioObjectID] = []
    /// One chain per app with its own preset.
    var chains: [String: [AudioObjectID]] = [:]
    /// Silenced on the speakers.
    var muted: [AudioObjectID] = []

    init(rules: [AppRule], processes: [AudioProcess]) {
        for rule in rules {
            let ids = processes.filter { $0.ownerBundleID == rule.bundleID }.map(\.id).sorted()
            guard !ids.isEmpty else { continue }
            excluded += ids
            switch rule.mode {
            case .preset: chains[rule.bundleID] = ids
            case .mute: muted += ids
            case .bypass: break
            }
        }
        excluded.sort()
        muted.sort()
    }
}

/// Splits system audio into the main chain (main EQ) and one chain per app that has its own preset.
/// Apps set to "untouched" are excluded from every chain, so they play as they are;
/// muted apps are excluded too and silenced by a separate tap that needs no processing.
@MainActor
final class AudioRouter {
    let main = TapChain()
    private var appChains: [String: TapChain] = [:]
    private let muteTap = MuteTap()
    private var device = AudioObjectID.unknown

    /// All chains are set up but paused (standby).
    private(set) var isSuspended = false

    var isRunning: Bool { main.isRunning }
    var sampleRate: Double { main.processor.sampleRate }
    var processors: [AudioProcessor] { [main.processor] + appChains.values.map(\.processor) }
    var appChainCount: Int { appChains.count }
    var mutedCount: Int { muteTap.processes.count }

    func start(device: AudioDeviceID) throws {
        stop()
        self.device = device
        try main.start(source: .allExcept([]), outputDevice: device)
    }

    /// Stops processing. Muted apps stay muted, so restarting the engine doesn't let them through for a moment.
    func stop() {
        appChains.values.forEach { $0.stop() }
        appChains = [:]
        main.stop()
        device = .unknown
        isSuspended = false
    }

    func unmuteAll() {
        muteTap.unmuteAll()
    }

    func suspend() {
        main.pause()
        appChains.values.forEach { $0.pause() }
        isSuspended = true
    }

    func resume() throws {
        try main.resume()
        for chain in appChains.values { try chain.resume() }
        isSuspended = false
    }

    /// Muting works even when the engine isn't running; the chains only when it is.
    func route(rules: [AppRule], processes: [AudioProcess], bypass: Bool, settings: (AppRule) -> SoundSettings) {
        // Core Audio rejects a tap description that names a process which has exited since the list was read.
        let alive = Set(AudioObjectID.system.readArray(kAudioHardwarePropertyProcessObjectList))
        let plan = RoutingPlan(rules: rules, processes: processes.filter { alive.contains($0.id) })
        if plan.muted != muteTap.processes {
            muteTap.mute(plan.muted)
            Log.engine.notice("Muted \(self.muteTap.processes.count) process(es)")
        }
        guard main.isRunning else { return }

        for (bundleID, chain) in appChains where plan.chains[bundleID] == nil {
            chain.stop()
            appChains[bundleID] = nil
        }

        for (bundleID, ids) in plan.chains {
            let chain = appChains[bundleID] ?? TapChain()
            let source = TapChain.Source.only(ids)
            if chain.source != source && !chain.updateSource(source) {
                do {
                    try chain.start(source: source, outputDevice: device, playing: !isSuspended)
                } catch {
                    // The app stays excluded from the main chain and plays untouched.
                    Log.engine.error("App chain for \(bundleID, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
                    appChains[bundleID] = nil
                    continue
                }
            }
            appChains[bundleID] = chain
            if let rule = rules.first(where: { $0.bundleID == bundleID }) {
                chain.processor.update(settings: settings(rule), bypass: bypass)
            }
        }

        let mainSource = TapChain.Source.allExcept(plan.excluded)
        if main.source != mainSource && !main.updateSource(mainSource) {
            Log.engine.notice("Tap description can't be updated live; restarting the main chain")
            try? main.start(source: mainSource, outputDevice: device, playing: !isSuspended)
        }
    }
}
