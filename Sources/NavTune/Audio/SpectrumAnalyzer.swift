import Accelerate
import Foundation

/// FFT of the most recent output, grouped into log-spaced bands from 20 Hz to 20 kHz.
/// All buffers and band ranges are allocated once and reused every frame.
final class SpectrumAnalyzer {
    static let size = 2048
    private let log2n = vDSP_Length(11)
    private let setup: FFTSetup
    private let window: [Float]
    private var windowed = [Float](repeating: 0, count: SpectrumAnalyzer.size)
    private var real = [Float](repeating: 0, count: SpectrumAnalyzer.size / 2)
    private var imag = [Float](repeating: 0, count: SpectrumAnalyzer.size / 2)
    private var magnitudes = [Float](repeating: 0, count: SpectrumAnalyzer.size / 2)
    private var levels: [Float]
    private var bins: [(first: Int, count: Int)] = []
    private var binsSampleRate = 0.0
    /// Converts FFT power to dBFS for a full-scale sine.
    private let offsetDB = 20 * log10(Float(SpectrumAnalyzer.size) / 2)
    let bandCount: Int

    init(bandCount: Int) {
        self.bandCount = bandCount
        levels = [Float](repeating: 0, count: bandCount)
        setup = vDSP_create_fftsetup(log2n, FFTRadix(kFFTRadix2))!
        var window = [Float](repeating: 0, count: Self.size)
        vDSP_hann_window(&window, vDSP_Length(Self.size), Int32(vDSP_HANN_NORM))
        self.window = window
    }

    deinit { vDSP_destroy_fftsetup(setup) }

    /// Returns band levels normalized to 0...1 (-80...0 dBFS).
    func analyze(_ samples: [Float], sampleRate: Double) -> [Float] {
        let half = Self.size / 2
        if sampleRate != binsSampleRate { computeBins(sampleRate: sampleRate) }

        vDSP_vmul(samples, 1, window, 1, &windowed, 1, vDSP_Length(Self.size))
        real.withUnsafeMutableBufferPointer { rp in
            imag.withUnsafeMutableBufferPointer { ip in
                var split = DSPSplitComplex(realp: rp.baseAddress!, imagp: ip.baseAddress!)
                windowed.withUnsafeBytes { raw in
                    vDSP_ctoz(raw.bindMemory(to: DSPComplex.self).baseAddress!, 2, &split, 1, vDSP_Length(half))
                }
                vDSP_fft_zrip(setup, &split, 1, log2n, FFTDirection(FFT_FORWARD))
                split.imagp[0] = 0
                vDSP_zvmags(&split, 1, &magnitudes, 1, vDSP_Length(half))
            }
        }

        magnitudes.withUnsafeBufferPointer { power in
            for (band, bin) in bins.enumerated() {
                var peak: Float = 0
                vDSP_maxv(power.baseAddress! + bin.first, 1, &peak, vDSP_Length(bin.count))
                let db = 10 * log10(peak + 1e-18) - offsetDB
                levels[band] = min(max((db + 80) / 80, 0), 1)
            }
        }
        return levels
    }

    private func computeBins(sampleRate: Double) {
        binsSampleRate = sampleRate
        let half = Self.size / 2
        let binWidth = sampleRate / Double(Self.size)
        bins = (0..<bandCount).map { band in
            let lo = 20 * pow(1000, Double(band) / Double(bandCount))
            let hi = 20 * pow(1000, Double(band + 1) / Double(bandCount))
            let first = min(max(Int(lo / binWidth), 1), half - 1)
            let last = min(max(Int(hi / binWidth), first), half - 1)
            return (first, last - first + 1)
        }
    }
}
