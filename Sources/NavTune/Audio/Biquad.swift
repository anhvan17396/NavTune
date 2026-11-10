import Foundation

/// Normalized biquad coefficients (RBJ Audio EQ Cookbook).
struct Biquad: Equatable {
    var b0 = 1.0, b1 = 0.0, b2 = 0.0, a1 = 0.0, a2 = 0.0

    static let butterworthQ = 1 / 2.0.squareRoot()

    private init(b0: Double, b1: Double, b2: Double, a0: Double, a1: Double, a2: Double) {
        self.b0 = b0 / a0
        self.b1 = b1 / a0
        self.b2 = b2 / a0
        self.a1 = a1 / a0
        self.a2 = a2 / a0
    }

    init() {}

    static func peaking(frequency: Double, gainDB: Double, q: Double, sampleRate: Double) -> Biquad {
        guard gainDB != 0, frequency < sampleRate * 0.45 else { return Biquad() }
        let a = pow(10, gainDB / 40)
        let w0 = 2 * Double.pi * frequency / sampleRate
        let alpha = sin(w0) / (2 * q)
        let cosw = cos(w0)
        return Biquad(b0: 1 + alpha * a, b1: -2 * cosw, b2: 1 - alpha * a,
                      a0: 1 + alpha / a, a1: -2 * cosw, a2: 1 - alpha / a)
    }

    static func lowShelf(frequency: Double, gainDB: Double, q: Double = butterworthQ, sampleRate: Double) -> Biquad {
        guard gainDB != 0 else { return Biquad() }
        let a = pow(10, gainDB / 40)
        let w0 = 2 * Double.pi * frequency / sampleRate
        let cosw = cos(w0)
        let k = 2 * sqrt(a) * sin(w0) / (2 * q)
        return Biquad(b0: a * ((a + 1) - (a - 1) * cosw + k),
                      b1: 2 * a * ((a - 1) - (a + 1) * cosw),
                      b2: a * ((a + 1) - (a - 1) * cosw - k),
                      a0: (a + 1) + (a - 1) * cosw + k,
                      a1: -2 * ((a - 1) + (a + 1) * cosw),
                      a2: (a + 1) + (a - 1) * cosw - k)
    }

    static func highShelf(frequency: Double, gainDB: Double, q: Double = butterworthQ, sampleRate: Double) -> Biquad {
        guard gainDB != 0, frequency < sampleRate * 0.45 else { return Biquad() }
        let a = pow(10, gainDB / 40)
        let w0 = 2 * Double.pi * frequency / sampleRate
        let cosw = cos(w0)
        let k = 2 * sqrt(a) * sin(w0) / (2 * q)
        return Biquad(b0: a * ((a + 1) + (a - 1) * cosw + k),
                      b1: -2 * a * ((a - 1) + (a + 1) * cosw),
                      b2: a * ((a + 1) + (a - 1) * cosw - k),
                      a0: (a + 1) - (a - 1) * cosw + k,
                      a1: 2 * ((a - 1) - (a + 1) * cosw),
                      a2: (a + 1) - (a - 1) * cosw - k)
    }

    static func highPass(frequency: Double, q: Double = butterworthQ, sampleRate: Double) -> Biquad {
        guard frequency < sampleRate * 0.45 else { return Biquad() }
        let w0 = 2 * Double.pi * frequency / sampleRate
        let alpha = sin(w0) / (2 * q)
        let cosw = cos(w0)
        return Biquad(b0: (1 + cosw) / 2, b1: -(1 + cosw), b2: (1 + cosw) / 2,
                      a0: 1 + alpha, a1: -2 * cosw, a2: 1 - alpha)
    }

    static func lowPass(frequency: Double, q: Double = butterworthQ, sampleRate: Double) -> Biquad {
        guard frequency < sampleRate * 0.45 else { return Biquad() }
        let w0 = 2 * Double.pi * frequency / sampleRate
        let alpha = sin(w0) / (2 * q)
        let cosw = cos(w0)
        return Biquad(b0: (1 - cosw) / 2, b1: 1 - cosw, b2: (1 - cosw) / 2,
                      a0: 1 + alpha, a1: -2 * cosw, a2: 1 - alpha)
    }

    var isIdentity: Bool { b0 == 1 && b1 == 0 && b2 == 0 && a1 == 0 && a2 == 0 }

    /// Power response |H|² given cos/sin of ω and 2ω.
    @inline(__always)
    func power(cos1: Double, sin1: Double, cos2: Double, sin2: Double) -> Double {
        let nr = b0 + b1 * cos1 + b2 * cos2, ni = -(b1 * sin1 + b2 * sin2)
        let dr = 1 + a1 * cos1 + a2 * cos2, di = -(a1 * sin1 + a2 * sin2)
        return (nr * nr + ni * ni) / max(dr * dr + di * di, 1e-30)
    }

    /// Magnitude response in dB at `frequency`.
    func magnitudeDB(at frequency: Double, sampleRate: Double) -> Double {
        let w = 2 * Double.pi * frequency / sampleRate
        return 10 * log10(max(power(cos1: cos(w), sin1: sin(w), cos2: cos(2 * w), sin2: sin(2 * w)), 1e-30))
    }
}

/// Precomputed trigonometry for evaluating an EQ response at log-spaced frequencies,
/// so drawing the curve costs one log10 per point instead of several per filter.
struct ResponseTable {
    let frequencies: [Double]
    private let cos1: [Double], sin1: [Double], cos2: [Double], sin2: [Double]

    init(sampleRate: Double, count: Int, from low: Double = 20, to high: Double = 20000) {
        frequencies = (0..<count).map { low * pow(high / low, Double($0) / Double(count - 1)) }
        let w = frequencies.map { 2 * Double.pi * $0 / sampleRate }
        cos1 = w.map(cos)
        sin1 = w.map(sin)
        cos2 = w.map { cos(2 * $0) }
        sin2 = w.map { sin(2 * $0) }
    }

    /// Total response in dB of the cascade plus a flat `gain`.
    func decibels(for filters: [Biquad], gain: Double) -> [Double] {
        let active = filters.filter { !$0.isIdentity }
        return frequencies.indices.map { i in
            var power = 1.0
            for k in active {
                power *= k.power(cos1: cos1[i], sin1: sin1[i], cos2: cos2[i], sin2: sin2[i])
            }
            return 10 * log10(max(power, 1e-30)) + gain
        }
    }
}
