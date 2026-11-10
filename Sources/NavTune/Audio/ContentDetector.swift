import AVFoundation
import SoundAnalysis

/// What is playing, as far as Apple's built-in sound classifier can tell.
/// The classifier knows instruments and voice types rather than genres, so each kind maps to the closest preset.
enum ContentKind: String {
    case speech, rap, rock, electronic, classical, jazz, piano, acoustic, pop

    var presetID: String {
        switch self {
        case .speech: "podcast"
        case .rap: "rap"
        case .rock: "rock"
        case .electronic: "edm"
        case .classical: "classical"
        case .jazz: "jazz"
        case .piano: "piano"
        case .acoustic: "acoustic"
        case .pop: "pop"
        }
    }

    var title: String {
        switch self {
        case .speech: tr("Lời nói / Podcast", "Speech / Podcast")
        case .rap: tr("Rap", "Rap")
        case .rock: tr("Guitar điện / Rock", "Electric guitar / Rock")
        case .electronic: tr("Nhạc điện tử", "Electronic")
        case .classical: tr("Dàn nhạc / Cổ điển", "Orchestral / Classical")
        case .jazz: tr("Kèn / Jazz", "Horns / Jazz")
        case .piano: tr("Piano", "Piano")
        case .acoustic: tr("Guitar mộc / Acoustic", "Acoustic guitar")
        case .pop: tr("Nhạc có lời / Pop", "Vocal music / Pop")
        }
    }
}

final class ContentDetector: NSObject, SNResultsObserving, @unchecked Sendable {
    /// Called on the main thread after each analysis window (about every 1.5 s).
    var onResult: (@MainActor (ContentKind?) -> Void)?

    private let queue = DispatchQueue(label: "vn.navtools.navtune.detector", qos: .utility)
    private var analyzer: SNAudioStreamAnalyzer?
    private var format: AVAudioFormat?
    private var position: AVAudioFramePosition = 0
    private var scores: [String: Double] = [:]

    func reset(sampleRate: Double) {
        queue.async { [self] in
            analyzer?.removeAllRequests()
            analyzer = nil
            scores = [:]
            position = 0
            guard sampleRate > 0, let format = AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: 1) else { return }
            let analyzer = SNAudioStreamAnalyzer(format: format)
            do {
                let request = try SNClassifySoundRequest(classifierIdentifier: .version1)
                request.overlapFactor = 0.5
                try analyzer.add(request, withObserver: self)
            } catch {
                return
            }
            self.analyzer = analyzer
            self.format = format
        }
    }

    func stop() {
        queue.async { [self] in
            analyzer?.removeAllRequests()
            analyzer = nil
        }
    }

    func feed(_ samples: [Float]) {
        queue.async { [self] in
            guard let analyzer, let format, !samples.isEmpty,
                  let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(samples.count))
            else { return }
            buffer.frameLength = AVAudioFrameCount(samples.count)
            samples.withUnsafeBufferPointer {
                buffer.floatChannelData![0].update(from: $0.baseAddress!, count: samples.count)
            }
            analyzer.analyze(buffer, atAudioFramePosition: position)
            position += AVAudioFramePosition(samples.count)
        }
    }

    func request(_ request: SNRequest, didProduce result: SNResult) {
        guard let result = result as? SNClassificationResult else { return }
        for classification in result.classifications {
            let previous = scores[classification.identifier] ?? 0
            scores[classification.identifier] = previous * 0.6 + classification.confidence * 0.4
        }
        let kind = Self.classify(scores)
        let handler = onResult
        DispatchQueue.main.async {
            MainActor.assumeIsolated { handler?(kind) }
        }
    }

    static func classify(_ scores: [String: Double]) -> ContentKind? {
        func score(_ labels: String...) -> Double { labels.map { scores[$0] ?? 0 }.max() ?? 0 }

        let music = score("music")
        let speech = score("speech")
        guard max(music, speech) > 0.2 else { return nil }
        if speech > music * 1.5 && score("singing", "rapping") < 0.3 { return .speech }

        let candidates: [(ContentKind, Double)] = [
            (.rap, score("rapping")),
            (.rock, score("electric_guitar")),
            (.electronic, score("synthesizer", "disc_scratching")),
            (.classical, score("orchestra", "violin_fiddle", "cello", "bowed_string_instrument", "harp")),
            (.jazz, score("saxophone", "trumpet", "brass_instrument", "double_bass", "vibraphone")),
            (.piano, score("piano", "electric_piano")),
            (.acoustic, score("acoustic_guitar", "ukulele", "guitar_strum")),
        ]
        if let best = candidates.max(by: { $0.1 < $1.1 }), best.1 > 0.25 { return best.0 }
        return .pop
    }
}
