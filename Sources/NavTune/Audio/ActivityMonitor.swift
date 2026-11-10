import CoreAudio
import Foundation

/// Knows which processes are producing audio right now, driven by Core Audio notifications instead of polling.
@MainActor
final class ActivityMonitor {
    /// Called when the set of playing processes changes.
    var onChange: (@MainActor () -> Void)?

    private(set) var playing: Set<AudioObjectID> = []
    private var listeners: [AudioObjectID: AudioObjectPropertyListenerBlock] = [:]
    private var address = AudioObjectPropertyAddress(mSelector: kAudioProcessPropertyIsRunningOutput,
                                                     mScope: kAudioObjectPropertyScopeGlobal,
                                                     mElement: kAudioObjectPropertyElementMain)

    func track(_ ids: [AudioObjectID]) {
        let wanted = Set(ids)
        for (id, block) in listeners where !wanted.contains(id) {
            AudioObjectRemovePropertyListenerBlock(id, &address, DispatchQueue.main, block)
            listeners[id] = nil
        }
        for id in wanted where listeners[id] == nil {
            let block: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
                MainActor.assumeIsolated { self?.refresh(id) }
            }
            if AudioObjectAddPropertyListenerBlock(id, &address, DispatchQueue.main, block) == noErr {
                listeners[id] = block
            }
        }
        resync()
    }

    /// Re-reads every tracked process, in case a notification was missed.
    func resync() {
        update(Set(listeners.keys.filter(Self.isRunningOutput)))
    }

    private func refresh(_ id: AudioObjectID) {
        var next = playing
        if Self.isRunningOutput(id) {
            next.insert(id)
        } else {
            next.remove(id)
        }
        update(next)
    }

    private func update(_ next: Set<AudioObjectID>) {
        guard next != playing else { return }
        playing = next
        onChange?()
    }

    private static func isRunningOutput(_ id: AudioObjectID) -> Bool {
        ((try? id.read(kAudioProcessPropertyIsRunningOutput, initial: UInt32(0))) ?? 0) != 0
    }
}
