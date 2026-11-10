import Foundation

/// An EQ curve. Gains in dB for bands 32, 64, 125, 250, 500, 1k, 2k, 4k, 8k, 16k Hz;
/// user presets saved from the parametric editor also carry `bands`.
struct Preset: Identifiable, Hashable, Codable {
    var id: String
    var name: String
    var icon: String
    var gains: [Float]
    var preamp: Float
    var nameEN: String?
    var bands: [EQBand]?

    static let userPrefix = "user."
    static let customID = "custom"

    var isBuiltIn: Bool { !id.hasPrefix(Self.userPrefix) }

    var displayName: String { tr(name, nameEN ?? name) }

    static func == (lhs: Preset, rhs: Preset) -> Bool { lhs.id == rhs.id && lhs.name == rhs.name }
    func hash(into hasher: inout Hasher) { hasher.combine(id) }

    static func user(name: String, gains: [Float], preamp: Float, bands: [EQBand]? = nil) -> Preset {
        Preset(id: userPrefix + UUID().uuidString, name: name, icon: bands == nil ? "star" : "dial.medium",
               gains: gains, preamp: preamp, bands: bands)
    }

    /// Built-in presets get automatic headroom so they don't constantly hit the limiter.
    private static func builtIn(_ id: String, _ name: String, _ nameEN: String, _ icon: String, _ gains: [Float]) -> Preset {
        let boost = max(0, gains.max() ?? 0)
        return Preset(id: id, name: name, icon: icon, gains: gains, preamp: -(boost * 0.6).rounded(), nameEN: nameEN)
    }

    static let builtIns: [Preset] = [
        builtIn("flat", "Gốc", "Flat", "minus", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        builtIn("vpop", "V-Pop", "V-Pop", "music.mic", [1, 2, 2, 1, 0, 1, 2, 2, 2, 2]),
        builtIn("pop", "Pop", "Pop", "star", [-1, 1, 3, 4, 2, 0, -1, 0, 1, 2]),
        builtIn("ballad", "Ballad", "Ballad", "music.note", [2, 2, 1, 0, 1, 2, 2, 1, 1, 1]),
        builtIn("bolero", "Bolero / Trữ tình", "Bolero", "heart", [2, 2, 1, 0, 1, 2, 3, 2, 1, 0]),
        builtIn("rock", "Rock", "Rock", "guitars", [5, 4, 3, 1, -1, -1, 1, 3, 4, 5]),
        builtIn("metal", "Metal", "Metal", "flame", [6, 5, 2, 0, -2, -1, 2, 4, 5, 5]),
        builtIn("edm", "EDM", "EDM", "bolt", [6, 5, 3, 0, -1, 0, 1, 2, 4, 5]),
        builtIn("dance", "Dance / Remix", "Dance / Remix", "figure.dance", [5, 4, 2, 0, 0, 1, 1, 2, 3, 4]),
        builtIn("rap", "Rap / Hip-hop", "Rap / Hip-hop", "mic", [6, 5, 3, 1, -1, -1, 1, 1, 2, 3]),
        builtIn("rnb", "R&B", "R&B", "music.quarternote.3", [4, 5, 3, 1, -1, 0, 1, 1, 2, 3]),
        builtIn("kpop", "K-Pop", "K-Pop", "sparkles", [3, 3, 1, 0, 0, 1, 2, 3, 3, 3]),
        builtIn("lofi", "Lo-fi / Chill", "Lo-fi / Chill", "cloud.moon", [3, 3, 2, 1, 0, -1, -2, -2, -3, -4]),
        builtIn("jazz", "Jazz", "Jazz", "music.note.list", [3, 2, 1, 2, -1, -1, 0, 1, 2, 3]),
        builtIn("classical", "Cổ điển", "Classical", "building.columns", [4, 3, 2, 1, -1, -1, 0, 2, 3, 4]),
        builtIn("acoustic", "Acoustic", "Acoustic", "leaf", [3, 3, 2, 1, 1, 1, 2, 2, 2, 1]),
        builtIn("piano", "Piano", "Piano", "pianokeys", [2, 1, 0, 1, 2, 1, 2, 3, 2, 1]),
        builtIn("vocal", "Giọng hát", "Vocal", "person.wave.2", [-2, -1, 0, 2, 4, 4, 3, 1, 0, -1]),
        builtIn("bass", "Tăng Bass", "Bass Boost", "speaker.wave.3", [7, 6, 4, 2, 0, 0, 0, 0, 0, 0]),
        builtIn("treble", "Tăng Treble", "Treble Boost", "waveform.path", [0, 0, 0, 0, 0, 1, 2, 4, 5, 6]),
        builtIn("movie", "Xem phim", "Movies", "film", [4, 3, 1, 0, 1, 2, 2, 2, 2, 2]),
        builtIn("game", "Chơi game", "Gaming", "gamecontroller", [3, 2, 0, 0, 1, 2, 3, 3, 2, 1]),
        builtIn("podcast", "Podcast / Lời nói", "Podcast / Speech", "waveform", [-4, -2, 0, 2, 3, 3, 2, 1, 0, -2]),
        builtIn("night", "Nghe đêm", "Late Night", "moon.stars", [-3, -2, -1, 0, 1, 1, 1, 0, -1, -2]),
    ]
}
