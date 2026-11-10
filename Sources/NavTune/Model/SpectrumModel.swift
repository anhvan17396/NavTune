import Foundation

/// Spectrum for the graph, computed on demand when a visible graph asks for a frame (nothing runs otherwise).
@MainActor
final class SpectrumModel {
    static let bandCount = 64

    private let analyzer = SpectrumAnalyzer(bandCount: SpectrumModel.bandCount)
    private var samples = [Float](repeating: 0, count: SpectrumAnalyzer.size)
    private var bands = [Float](repeating: 0, count: SpectrumModel.bandCount)
    private var lastUpdate = Date.distantPast

    /// Several graphs may draw the same frame (window + menu bar); the FFT runs once per frame.
    func bands(at date: Date, from processor: AudioProcessor?, sampleRate: Double) -> [Float] {
        let elapsed = date.timeIntervalSince(lastUpdate)
        guard elapsed > 0.012 || elapsed < 0 else { return bands }
        lastUpdate = date

        guard let processor, sampleRate > 0 else {
            bands = [Float](repeating: 0, count: Self.bandCount)
            return bands
        }
        processor.copyRecentSamples(into: &samples)
        let fresh = analyzer.analyze(samples, sampleRate: sampleRate)
        let fall = Float(min(max(elapsed, 0), 0.1)) * 0.75
        for i in bands.indices {
            bands[i] = max(fresh[i], bands[i] - fall)
        }
        return bands
    }
}
