import Foundation

enum EQMode: String, Codable {
    case graphic, parametric
}

enum FilterKind: String, Codable, CaseIterable, Identifiable {
    case peak, lowShelf, highShelf, lowCut, highCut

    var id: Self { self }

    var title: String {
        switch self {
        case .peak: tr("Chuông (Peak)", "Bell (Peak)")
        case .lowShelf: tr("Kệ trầm (Low shelf)", "Low shelf")
        case .highShelf: tr("Kệ cao (High shelf)", "High shelf")
        case .lowCut: tr("Cắt trầm (Low cut)", "Low cut")
        case .highCut: tr("Cắt cao (High cut)", "High cut")
        }
    }

    var hasGain: Bool { self != .lowCut && self != .highCut }
}

/// One band of the parametric equalizer.
struct EQBand: Codable, Equatable, Identifiable {
    var id = UUID()
    var kind = FilterKind.peak
    var frequency: Double = 1000
    var gain: Float = 0
    var q: Double = 1
    var enabled = true

    func biquad(sampleRate: Double) -> Biquad {
        guard enabled else { return Biquad() }
        let gainDB = Double(gain)
        return switch kind {
        case .peak: .peaking(frequency: frequency, gainDB: gainDB, q: q, sampleRate: sampleRate)
        case .lowShelf: .lowShelf(frequency: frequency, gainDB: gainDB, q: q, sampleRate: sampleRate)
        case .highShelf: .highShelf(frequency: frequency, gainDB: gainDB, q: q, sampleRate: sampleRate)
        case .lowCut: .highPass(frequency: frequency, q: q, sampleRate: sampleRate)
        case .highCut: .lowPass(frequency: frequency, q: q, sampleRate: sampleRate)
        }
    }
}

/// Everything that shapes the sound; stored per output device.
struct SoundSettings: Codable, Equatable {
    static let bandCount = AudioProcessor.frequencies.count
    static let maxParametricBands = 10

    static let defaultBands: [EQBand] = [
        EQBand(kind: .lowShelf, frequency: 80, q: Biquad.butterworthQ),
        EQBand(kind: .peak, frequency: 250, q: 1),
        EQBand(kind: .peak, frequency: 1000, q: 1),
        EQBand(kind: .peak, frequency: 3000, q: 1),
        EQBand(kind: .peak, frequency: 6000, q: 1.2),
        EQBand(kind: .highShelf, frequency: 10000, q: Biquad.butterworthQ),
    ]

    var presetID = "flat"
    var mode = EQMode.graphic
    var gains = [Float](repeating: 0, count: SoundSettings.bandCount)
    var bands = SoundSettings.defaultBands
    var preamp: Float = 0
    /// Low shelf at 90 Hz, dB.
    var bass: Float = 0
    /// High shelf at 9 kHz, dB.
    var clarity: Float = 0
    /// Stereo width: 0 = mono, 1 = original, 2 = extra wide.
    var width: Float = 1
    /// Loudness leveler amount, 0...1.
    var loudness: Float = 0
    /// Output boost, dB (protected by the limiter).
    var boost: Float = 0

    init() {}

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = SoundSettings()
        presetID = try c.decodeIfPresent(String.self, forKey: .presetID) ?? d.presetID
        mode = try c.decodeIfPresent(EQMode.self, forKey: .mode) ?? d.mode
        let gains = try c.decodeIfPresent([Float].self, forKey: .gains) ?? d.gains
        self.gains = gains.count == Self.bandCount ? gains : d.gains
        let bands = try c.decodeIfPresent([EQBand].self, forKey: .bands) ?? d.bands
        self.bands = Array(bands.prefix(Self.maxParametricBands))
        preamp = try c.decodeIfPresent(Float.self, forKey: .preamp) ?? d.preamp
        bass = try c.decodeIfPresent(Float.self, forKey: .bass) ?? d.bass
        clarity = try c.decodeIfPresent(Float.self, forKey: .clarity) ?? d.clarity
        width = try c.decodeIfPresent(Float.self, forKey: .width) ?? d.width
        loudness = try c.decodeIfPresent(Float.self, forKey: .loudness) ?? d.loudness
        boost = try c.decodeIfPresent(Float.self, forKey: .boost) ?? d.boost
    }

    mutating func resetEffects() {
        let d = SoundSettings()
        bass = d.bass
        clarity = d.clarity
        width = d.width
        loudness = d.loudness
        boost = d.boost
    }
}
