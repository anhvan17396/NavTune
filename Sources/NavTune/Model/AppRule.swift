import Foundation

/// How NavTune treats one app's audio instead of the main EQ.
struct AppRule: Codable, Identifiable, Equatable {
    enum Mode: String, Codable, Hashable {
        /// The app gets its own preset and volume.
        case preset
        /// The app's audio is left untouched.
        case bypass
        /// Nothing the app plays reaches the speakers or headphones. The app itself keeps running,
        /// so OBS still streams it and screen recorders still capture it.
        case mute
    }

    var bundleID: String
    var name: String
    var mode = Mode.preset
    var presetID = "flat"
    /// dB; negative lowers the input, positive boosts through the limiter.
    var volume: Float = 0
    /// What unmuting returns to; nil means the main EQ.
    var modeBeforeMute: Mode?

    var id: String { bundleID }
}

extension AppRule {
    /// `existing` muted, remembering how it was handled; nil means the app was on the main EQ.
    static func muting(_ existing: AppRule?, bundleID: String, name: String) -> AppRule {
        if let existing, existing.mode == .mute { return existing }
        var rule = existing ?? AppRule(bundleID: bundleID, name: name)
        rule.modeBeforeMute = existing?.mode
        rule.mode = .mute
        return rule
    }

    /// The rule after unmuting; nil means back to the main EQ.
    func unmuted() -> AppRule? {
        guard mode == .mute else { return self }
        guard let previous = modeBeforeMute else { return nil }
        var rule = self
        rule.mode = previous
        rule.modeBeforeMute = nil
        return rule
    }
}
