import CoreAudio
import Foundation

/// Silences apps on the speakers and headphones without touching them otherwise.
///
/// A process tap with the "muted" behavior stops the system from sending the apps' audio to any output device,
/// while the apps keep running and anything capturing them (OBS, screen recording) still gets their sound.
/// The muting only takes effect once the tap belongs to a device, so it is placed in a private aggregate device
/// that is never started: nothing is read, so it costs no CPU and doesn't keep the output awake.
/// Both are private to NavTune, so the system removes them, and the apps are heard again, if NavTune quits or crashes.
final class MuteTap {
    private var tapID = AudioObjectID.unknown
    private var aggregateID = AudioObjectID.unknown
    private var uuid = UUID()

    /// The processes currently silenced, sorted.
    private(set) var processes: [AudioObjectID] = []

    func mute(_ ids: [AudioObjectID]) {
        let ids = Array(Set(ids)).sorted()
        guard ids != processes else { return }
        guard !ids.isEmpty else { return unmuteAll() }

        if aggregateID != .unknown, tapID.setTapDescription(makeDescription(ids)) == noErr {
            processes = ids
            return
        }
        unmuteAll()
        do {
            try create(ids)
            processes = ids
        } catch {
            unmuteAll()
            Log.engine.error("Mute failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    func unmuteAll() {
        if aggregateID != .unknown { AudioHardwareDestroyAggregateDevice(aggregateID) }
        aggregateID = .unknown
        if tapID != .unknown { AudioHardwareDestroyProcessTap(tapID) }
        tapID = .unknown
        processes = []
    }

    private func create(_ ids: [AudioObjectID]) throws {
        uuid = UUID()
        var tap = AudioObjectID.unknown
        try check(AudioHardwareCreateProcessTap(makeDescription(ids), &tap),
                  tr("Tạo bộ tắt tiếng", "Creating the mute tap"))
        tapID = tap

        let aggregate: [String: Any] = [
            kAudioAggregateDeviceNameKey: "NavTune Mute",
            kAudioAggregateDeviceUIDKey: "\(TapChain.aggregatePrefix).mute.\(UUID().uuidString)",
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceTapListKey: [[kAudioSubTapUIDKey: uuid.uuidString]],
        ]
        var device = AudioObjectID.unknown
        try check(AudioHardwareCreateAggregateDevice(aggregate as CFDictionary, &device),
                  tr("Kích hoạt bộ tắt tiếng", "Activating the mute tap"))
        aggregateID = device
    }

    private func makeDescription(_ ids: [AudioObjectID]) -> CATapDescription {
        let description = CATapDescription(stereoMixdownOfProcesses: ids)
        description.uuid = uuid
        description.name = "NavTune Mute"
        description.isPrivate = true
        description.muteBehavior = .muted
        return description
    }

    deinit { unmuteAll() }
}
