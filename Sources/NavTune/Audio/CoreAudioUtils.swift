import CoreAudio
import Foundation

struct CoreAudioError: LocalizedError {
    let action: String
    let status: OSStatus

    var errorDescription: String? { tr("\(action) thất bại (mã \(status))", "\(action) failed (code \(status))") }
}

func check(_ status: OSStatus, _ action: String) throws {
    if status != noErr { throw CoreAudioError(action: action, status: status) }
}

private func address(_ selector: AudioObjectPropertySelector,
                     scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> AudioObjectPropertyAddress {
    AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
}

extension AudioObjectID {
    static let system = AudioObjectID(kAudioObjectSystemObject)
    static let unknown = AudioObjectID(kAudioObjectUnknown)

    func read<T: BitwiseCopyable>(_ selector: AudioObjectPropertySelector, initial: T,
                                  scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) throws -> T {
        var addr = address(selector, scope: scope)
        var size = UInt32(MemoryLayout<T>.size)
        var value = initial
        try check(AudioObjectGetPropertyData(self, &addr, 0, nil, &size, &value), "Đọc thuộc tính âm thanh")
        return value
    }

    func readString(_ selector: AudioObjectPropertySelector) throws -> String {
        var addr = address(selector)
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        var value: Unmanaged<CFString>?
        try check(AudioObjectGetPropertyData(self, &addr, 0, nil, &size, &value), "Đọc thuộc tính âm thanh")
        return value?.takeRetainedValue() as String? ?? ""
    }

    func readArray(_ selector: AudioObjectPropertySelector,
                   scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> [AudioObjectID] {
        var addr = address(selector, scope: scope)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(self, &addr, 0, nil, &size) == noErr, size > 0 else { return [] }
        var ids = [AudioObjectID](repeating: .unknown, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(self, &addr, 0, nil, &size, &ids) == noErr else { return [] }
        return ids
    }

    var hasOutputStreams: Bool {
        !readArray(kAudioDevicePropertyStreams, scope: kAudioObjectPropertyScopeOutput).isEmpty
    }

    var nominalSampleRate: Double {
        (try? read(kAudioDevicePropertyNominalSampleRate, initial: Float64(0))) ?? 0
    }

    static func defaultOutputDevice() throws -> AudioDeviceID {
        try system.read(kAudioHardwarePropertyDefaultOutputDevice, initial: unknown)
    }

    /// Audio object representing a process, used to exclude NavTune itself from the tap.
    static func processObject(pid: pid_t) -> AudioObjectID {
        var addr = address(kAudioHardwarePropertyTranslatePIDToProcessObject)
        var pid = pid
        var object = unknown
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        let status = AudioObjectGetPropertyData(system, &addr, UInt32(MemoryLayout<pid_t>.size), &pid, &size, &object)
        return status == noErr ? object : unknown
    }

    /// Changes what a tap captures (and how it mutes) without recreating it.
    func setTapDescription(_ description: CATapDescription) -> OSStatus {
        var addr = address(kAudioTapPropertyDescription)
        // The property value is the CATapDescription object reference itself.
        return withUnsafePointer(to: description) {
            AudioObjectSetPropertyData(self, &addr, 0, nil, UInt32(MemoryLayout<CATapDescription>.size), $0)
        }
    }

    /// Calls `handler` on the main thread whenever a system-level property changes.
    static func observeSystem(_ selector: AudioObjectPropertySelector, _ handler: @escaping @MainActor () -> Void) {
        var addr = address(selector)
        AudioObjectAddPropertyListenerBlock(system, &addr, DispatchQueue.main) { _, _ in
            MainActor.assumeIsolated { handler() }
        }
    }
}
