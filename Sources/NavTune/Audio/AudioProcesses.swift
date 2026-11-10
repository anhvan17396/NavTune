import AppKit
import CoreAudio
import Darwin

/// A process known to the audio system, attributed to the user-facing app that owns it when there is one
/// (e.g. "Google Chrome Helper" belongs to Google Chrome; system daemons have no owner).
struct AudioProcess: Equatable {
    let id: AudioObjectID
    let pid: pid_t
    let bundleID: String
    let ownerBundleID: String?
    let ownerName: String?
}

struct AudioApp: Identifiable {
    let bundleID: String
    let name: String
    let icon: NSImage
    var isPlaying: Bool
    var isRunning: Bool

    var id: String { bundleID }
}

@MainActor
enum AudioProcesses {
    /// Process attribution never changes for a given audio object, so it is resolved once.
    private static var cache: [AudioObjectID: AudioProcess] = [:]
    private static var iconCache: [String: NSImage] = [:]

    /// Every audio process except NavTune itself.
    static func current() -> [AudioProcess] {
        let ids = AudioObjectID.system.readArray(kAudioHardwarePropertyProcessObjectList)
        let me = getpid()
        var running: (byPID: [pid_t: NSRunningApplication], all: [NSRunningApplication])?

        let processes: [AudioProcess] = ids.compactMap { id in
            if let cached = cache[id], cached.ownerBundleID != nil { return cached }
            let pid = (try? id.read(kAudioProcessPropertyPID, initial: pid_t(0))) ?? 0
            let bundleID = (try? id.readString(kAudioProcessPropertyBundleID)) ?? ""
            if running == nil {
                let apps = NSWorkspace.shared.runningApplications.filter {
                    $0.activationPolicy == .regular && $0.bundleIdentifier != nil
                }
                running = (Dictionary(apps.map { ($0.processIdentifier, $0) }, uniquingKeysWith: { first, _ in first }), apps)
            }
            let owner = owner(pid: pid, bundleID: bundleID, byPID: running!.byPID, apps: running!.all)
            let process = AudioProcess(id: id, pid: pid, bundleID: bundleID,
                                       ownerBundleID: owner?.bundleIdentifier, ownerName: owner?.localizedName)
            cache[id] = process
            return process
        }

        let alive = Set(ids)
        cache = cache.filter { alive.contains($0.key) }
        return processes.filter { $0.pid != me }
    }

    /// One entry per app, playing apps first.
    static func apps(from processes: [AudioProcess], playing: Set<AudioObjectID>) -> [AudioApp] {
        var result: [String: AudioApp] = [:]
        for process in processes {
            guard let owner = process.ownerBundleID else { continue }
            let isPlaying = playing.contains(process.id)
            if var app = result[owner] {
                app.isPlaying = app.isPlaying || isPlaying
                result[owner] = app
            } else {
                result[owner] = AudioApp(bundleID: owner, name: process.ownerName ?? owner, icon: icon(for: owner),
                                         isPlaying: isPlaying, isRunning: true)
            }
        }
        return result.values.sorted { a, b in
            a.isPlaying != b.isPlaying ? a.isPlaying : a.name.localizedCompare(b.name) == .orderedAscending
        }
    }

    static func icon(for bundleID: String) -> NSImage {
        if let cached = iconCache[bundleID] { return cached }
        let icon = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID)
            .map { NSWorkspace.shared.icon(forFile: $0.path) }
            ?? NSImage(systemSymbolName: "app", accessibilityDescription: nil) ?? NSImage()
        iconCache[bundleID] = icon
        return icon
    }

    private static func owner(pid: pid_t, bundleID: String, byPID: [pid_t: NSRunningApplication],
                              apps: [NSRunningApplication]) -> NSRunningApplication? {
        var current: pid_t? = pid
        for _ in 0..<6 {
            guard let p = current else { break }
            if let app = byPID[p] { return app }
            current = parentPID(of: p)
        }
        // Safari plays audio through the shared WebKit GPU process.
        if bundleID == "com.apple.WebKit.GPU" {
            return apps.first { $0.bundleIdentifier == "com.apple.Safari" }
        }
        guard !bundleID.isEmpty else { return nil }
        return apps.first { bundleID.hasPrefix($0.bundleIdentifier! + ".") }
    }

    private static func parentPID(of pid: pid_t) -> pid_t? {
        var info = kinfo_proc()
        var size = MemoryLayout<kinfo_proc>.stride
        var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, pid]
        guard sysctl(&mib, 4, &info, &size, nil, 0) == 0, size > 0 else { return nil }
        let parent = info.kp_eproc.e_ppid
        return parent > 1 ? parent : nil
    }
}
