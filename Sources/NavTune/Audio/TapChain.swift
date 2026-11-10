import AudioToolbox
import CoreAudio
import Foundation

/// One processing chain: a Core Audio process tap (muting the original audio), a processor,
/// and a private aggregate device that plays the result on the output device.
/// Several chains can play to the same device; the system mixes them.
final class TapChain {
    enum Source: Equatable {
        /// Every process except these (and NavTune itself).
        case allExcept([AudioObjectID])
        /// Only these processes.
        case only([AudioObjectID])
    }

    static let aggregatePrefix = "vn.navtools.navtune.aggregate"

    let processor = AudioProcessor()

    private var tapID = AudioObjectID.unknown
    private var aggregateID = AudioObjectID.unknown
    private var procID: AudioDeviceIOProcID?
    private var tapUUID = UUID()

    private(set) var source: Source?
    /// True while audio is flowing; false when stopped or paused for standby.
    private(set) var isPlaying = false

    /// True once the tap and device exist, whether playing or paused.
    var isRunning: Bool { procID != nil }

    func start(source: Source, outputDevice: AudioDeviceID, playing: Bool = true) throws {
        stop()
        do {
            try startUnchecked(source: source, outputDevice: outputDevice, playing: playing)
        } catch {
            stop()
            throw error
        }
    }

    /// Stops the IO without tearing anything down. While paused, the tap isn't read,
    /// so the original audio plays untouched and the output device can go idle.
    func pause() {
        guard let procID, isPlaying else { return }
        AudioDeviceStop(aggregateID, procID)
        isPlaying = false
    }

    func resume() throws {
        guard let procID, !isPlaying else { return }
        try check(AudioDeviceStart(aggregateID, procID), tr("Bắt đầu phát âm thanh", "Starting playback"))
        isPlaying = true
    }

    /// Changes which processes are tapped without interrupting playback. Returns false if unsupported.
    func updateSource(_ newSource: Source) -> Bool {
        guard isRunning else { return false }
        guard newSource != source else { return true }
        let status = tapID.setTapDescription(makeDescription(newSource))
        if status == noErr { source = newSource }
        return status == noErr
    }

    private func makeDescription(_ source: Source) -> CATapDescription {
        let description: CATapDescription
        switch source {
        case .allExcept(let excluded):
            // Exclude ourselves, otherwise our own output would be fed back into the tap.
            let me = AudioObjectID.processObject(pid: getpid())
            description = CATapDescription(stereoGlobalTapButExcludeProcesses: me == .unknown ? excluded : excluded + [me])
        case .only(let processes):
            description = CATapDescription(stereoMixdownOfProcesses: processes)
        }
        description.uuid = tapUUID
        description.name = "NavTune Tap"
        description.isPrivate = true
        // Original audio is muted only while NavTune reads the tap, so it comes back if we stop or crash.
        description.muteBehavior = .mutedWhenTapped
        return description
    }

    private func startUnchecked(source: Source, outputDevice: AudioDeviceID, playing: Bool) throws {
        let outputUID = try outputDevice.readString(kAudioDevicePropertyDeviceUID)

        tapUUID = UUID()
        var tap = AudioObjectID.unknown
        try check(AudioHardwareCreateProcessTap(makeDescription(source), &tap),
                  tr("Tạo bộ bắt âm thanh hệ thống", "Creating the system audio tap"))
        tapID = tap
        self.source = source

        let aggregate: [String: Any] = [
            kAudioAggregateDeviceNameKey: "NavTune",
            kAudioAggregateDeviceUIDKey: "\(Self.aggregatePrefix).\(UUID().uuidString)",
            kAudioAggregateDeviceMainSubDeviceKey: outputUID,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: outputUID]],
            kAudioAggregateDeviceTapListKey: [[
                kAudioSubTapUIDKey: tapUUID.uuidString,
                kAudioSubTapDriftCompensationKey: true,
            ]],
        ]
        var device = AudioObjectID.unknown
        try check(AudioHardwareCreateAggregateDevice(aggregate as CFDictionary, &device),
                  tr("Tạo thiết bị âm thanh trung gian", "Creating the aggregate device"))
        aggregateID = device

        let rate = device.nominalSampleRate
        processor.prepare(sampleRate: rate > 0 ? rate : 48000)

        // No dispatch queue: the HAL invokes the block directly on its real-time IO thread.
        let processor = self.processor
        var proc: AudioDeviceIOProcID?
        try check(AudioDeviceCreateIOProcIDWithBlock(&proc, device, nil) { _, input, _, output, _ in
            processor.render(input: input, output: output)
        }, tr("Khởi tạo xử lý âm thanh", "Setting up audio processing"))
        procID = proc
        if playing { try resume() }
    }

    func stop() {
        if let procID {
            if isPlaying { AudioDeviceStop(aggregateID, procID) }
            AudioDeviceDestroyIOProcID(aggregateID, procID)
        }
        procID = nil
        isPlaying = false
        if aggregateID != .unknown { AudioHardwareDestroyAggregateDevice(aggregateID) }
        aggregateID = .unknown
        if tapID != .unknown { AudioHardwareDestroyProcessTap(tapID) }
        tapID = .unknown
        source = nil
    }

    deinit { stop() }
}
