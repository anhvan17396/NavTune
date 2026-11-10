import CoreAudio
import Foundation
import Testing
@testable import NavTune

private let sampleRate = 48000.0

/// Runs a stereo sine through the processor in 512-frame blocks and returns the output peak of the last second.
private func render(_ processor: AudioProcessor, frequency: Double, amplitude: Float, seconds: Double = 2) -> Float {
    let frames = 512
    let input = AudioBufferList.allocate(maximumBuffers: 1)
    let output = AudioBufferList.allocate(maximumBuffers: 1)
    let inData = UnsafeMutablePointer<Float>.allocate(capacity: frames * 2)
    let outData = UnsafeMutablePointer<Float>.allocate(capacity: frames * 2)
    defer {
        inData.deallocate(); outData.deallocate()
        free(input.unsafeMutablePointer); free(output.unsafeMutablePointer)
    }
    let bytes = UInt32(frames * 2 * MemoryLayout<Float>.size)
    input[0] = AudioBuffer(mNumberChannels: 2, mDataByteSize: bytes, mData: inData)
    output[0] = AudioBuffer(mNumberChannels: 2, mDataByteSize: bytes, mData: outData)

    let blocks = Int(seconds * sampleRate) / frames
    var peak: Float = 0
    var n = 0
    for block in 0..<blocks {
        for f in 0..<frames {
            let v = amplitude * Float(sin(2 * .pi * frequency * Double(n) / sampleRate))
            inData[f * 2] = v
            inData[f * 2 + 1] = v
            n += 1
        }
        processor.render(input: input.unsafePointer, output: output.unsafeMutablePointer)
        if block >= blocks / 2 {
            for i in 0..<frames * 2 {
                #expect(outData[i].isFinite)
                peak = max(peak, abs(outData[i]))
            }
        }
    }
    return peak
}

private func makeProcessor(_ settings: SoundSettings, bypass: Bool = false) -> AudioProcessor {
    let p = AudioProcessor()
    p.prepare(sampleRate: sampleRate)
    p.update(settings: settings, bypass: bypass)
    return p
}

private func db(_ ratio: Float) -> Double { 20 * log10(Double(ratio)) }

@Test func flatSettingsPassSignalUnchanged() {
    let peak = render(makeProcessor(SoundSettings()), frequency: 1000, amplitude: 0.5)
    #expect(abs(db(peak / 0.5)) < 0.1)
}

@Test func bandBoostMatchesGain() {
    var s = SoundSettings()
    s.gains[5] = 6 // 1 kHz
    let peak = render(makeProcessor(s), frequency: 1000, amplitude: 0.1)
    #expect(abs(db(peak / 0.1) - 6) < 0.3)
}

@Test func bandCutDoesNotAffectFarFrequencies() {
    var s = SoundSettings()
    s.gains[0] = -12 // 32 Hz
    let peak = render(makeProcessor(s), frequency: 4000, amplitude: 0.1)
    #expect(abs(db(peak / 0.1)) < 0.3)
}

@Test func bassShelfBoostsLows() {
    var s = SoundSettings()
    s.bass = 6
    let low = render(makeProcessor(s), frequency: 30, amplitude: 0.1)
    let high = render(makeProcessor(s), frequency: 5000, amplitude: 0.1)
    #expect(abs(db(low / 0.1) - 6) < 0.6)
    #expect(abs(db(high / 0.1)) < 0.3)
}

@Test func limiterPreventsClipping() {
    var s = SoundSettings()
    s.gains = [Float](repeating: 12, count: SoundSettings.bandCount)
    s.boost = 12
    let peak = render(makeProcessor(s), frequency: 1000, amplitude: 0.9)
    #expect(peak <= 1.0)
}

@Test func loudnessLevelerRaisesQuietAudio() {
    var s = SoundSettings()
    s.loudness = 1
    let peak = render(makeProcessor(s), frequency: 1000, amplitude: 0.01, seconds: 4)
    #expect(peak > 0.01 * 1.5)
}

@Test func monoWidthCollapsesStereo() {
    var s = SoundSettings()
    s.width = 0
    let p = makeProcessor(s)
    // Identical L/R stays identical in mono, so level is unchanged.
    let peak = render(p, frequency: 1000, amplitude: 0.5)
    #expect(abs(db(peak / 0.5)) < 0.1)
}

@Test func bypassIgnoresSettings() {
    var s = SoundSettings()
    s.gains[5] = 12
    let peak = render(makeProcessor(s, bypass: true), frequency: 1000, amplitude: 0.5)
    #expect(abs(db(peak / 0.5)) < 0.01)
}

@Test func presetsAreWellFormed() {
    #expect(Set(Preset.builtIns.map(\.id)).count == Preset.builtIns.count)
    for preset in Preset.builtIns {
        #expect(preset.gains.count == SoundSettings.bandCount)
        #expect(preset.gains.allSatisfy { abs($0) <= 12 })
    }
}

@Test func settingsDecodeWithMissingKeys() throws {
    let json = #"{"presetID":"rock","gains":[1,2,3,4,5,6,7,8,9,10]}"#
    let s = try JSONDecoder().decode(SoundSettings.self, from: Data(json.utf8))
    #expect(s.presetID == "rock")
    #expect(s.width == 1)
    #expect(s.boost == 0)
}

@Test func parametricBellBoostsItsFrequency() {
    var s = SoundSettings()
    s.mode = .parametric
    s.bands = [EQBand(kind: .peak, frequency: 2000, gain: 6, q: 1)]
    let at = render(makeProcessor(s), frequency: 2000, amplitude: 0.1)
    let away = render(makeProcessor(s), frequency: 100, amplitude: 0.1)
    #expect(abs(db(at / 0.1) - 6) < 0.3)
    #expect(abs(db(away / 0.1)) < 0.3)
}

@Test func lowCutRemovesRumble() {
    var s = SoundSettings()
    s.mode = .parametric
    s.bands = [EQBand(kind: .lowCut, frequency: 200, q: Biquad.butterworthQ)]
    let low = render(makeProcessor(s), frequency: 30, amplitude: 0.5)
    let high = render(makeProcessor(s), frequency: 3000, amplitude: 0.5)
    #expect(db(low / 0.5) < -24)
    #expect(abs(db(high / 0.5)) < 0.3)
}

@Test func disabledBandIsTransparent() {
    var s = SoundSettings()
    s.mode = .parametric
    s.bands = [EQBand(kind: .peak, frequency: 1000, gain: 12, q: 1, enabled: false)]
    let peak = render(makeProcessor(s), frequency: 1000, amplitude: 0.5)
    #expect(abs(db(peak / 0.5)) < 0.1)
}

@Test func responseCurveMatchesRenderedGain() {
    var s = SoundSettings()
    s.gains[3] = -6 // 250 Hz
    let curve = AudioProcessor.filters(for: s, sampleRate: sampleRate)
        .reduce(0) { $0 + $1.magnitudeDB(at: 250, sampleRate: sampleRate) }
    #expect(abs(curve + 6) < 0.1)
}

@Test func classifierMapsInstrumentsToPresets() {
    #expect(ContentDetector.classify(["speech": 0.9, "music": 0.1]) == .speech)
    #expect(ContentDetector.classify(["music": 0.9, "electric_guitar": 0.6]) == .rock)
    #expect(ContentDetector.classify(["music": 0.9, "synthesizer": 0.5]) == .electronic)
    #expect(ContentDetector.classify(["music": 0.8, "singing": 0.7]) == .pop)
    #expect(ContentDetector.classify(["silence": 0.9]) == nil)
}

@Test func oldSettingsDecodeAsGraphicMode() throws {
    let json = #"{"presetID":"pop","gains":[0,0,0,0,0,0,0,0,0,0],"bass":2}"#
    let s = try JSONDecoder().decode(SoundSettings.self, from: Data(json.utf8))
    #expect(s.mode == .graphic)
    #expect(s.bands == SoundSettings.defaultBands)
    #expect(s.bass == 2)
}

@Test func responseTableMatchesDirectEvaluation() {
    var s = SoundSettings()
    s.gains = [3, -2, 4, 0, -6, 2, 0, 5, -3, 1]
    s.bass = 4
    let filters = AudioProcessor.filters(for: s, sampleRate: sampleRate)
    let table = ResponseTable(sampleRate: sampleRate, count: 40)
    let fast = table.decibels(for: filters, gain: 0)
    for (i, f) in table.frequencies.enumerated() {
        let direct = filters.reduce(0) { $0 + $1.magnitudeDB(at: f, sampleRate: sampleRate) }
        #expect(abs(fast[i] - direct) < 1e-6)
    }
}

@Test func spectrumAnalyzerFindsTone() {
    let analyzer = SpectrumAnalyzer(bandCount: 64)
    let samples = (0..<SpectrumAnalyzer.size).map { Float(0.5 * sin(2 * .pi * 1000 * Double($0) / sampleRate)) }
    let bands = analyzer.analyze(samples, sampleRate: sampleRate)
    let loudest = bands.indices.max { bands[$0] < bands[$1] }!
    // Band edges are 20 Hz * 1000^(i/64); 1 kHz falls in band 36.
    #expect(abs(loudest - 36) <= 1)
    #expect(bands[loudest] > 0.8)
    #expect(bands[5] < 0.3)
}

@Test func reenablingBypassedProcessingIsClean() {
    var s = SoundSettings()
    s.gains[0] = 12
    let p = makeProcessor(s, bypass: true)
    _ = render(p, frequency: 1000, amplitude: 0.5, seconds: 0.5)
    p.update(settings: s, bypass: false)
    let peak = render(p, frequency: 1000, amplitude: 0.5, seconds: 1)
    #expect(peak <= 1)
}

@Test func levelsAreReportedWithoutBeingConsumed() {
    let p = makeProcessor(SoundSettings())
    _ = render(p, frequency: 1000, amplitude: 0.5, seconds: 0.5)
    let first = p.currentOutputLevel
    let second = p.currentOutputLevel
    #expect(first > 0.4)
    #expect(first == second)
    #expect(p.currentInputLevel > 0.4)
}

/// Real-time factor of the full chain with every filter active, the leveler on and a boost,
/// rendering 512-frame stereo blocks the way the HAL does. Run with `swift test -c release` for real numbers.
@Test func processingIsFarFasterThanRealTime() {
    var s = SoundSettings()
    s.gains = [3, -2, 4, 1, -6, 2, -1, 5, -3, 1]
    s.bass = 3
    s.clarity = 2
    s.width = 1.3
    s.loudness = 0.5
    s.boost = 3
    let p = makeProcessor(s)

    let frames = 512
    let input = AudioBufferList.allocate(maximumBuffers: 1)
    let output = AudioBufferList.allocate(maximumBuffers: 1)
    let inData = UnsafeMutablePointer<Float>.allocate(capacity: frames * 2)
    let outData = UnsafeMutablePointer<Float>.allocate(capacity: frames * 2)
    defer {
        inData.deallocate(); outData.deallocate()
        free(input.unsafeMutablePointer); free(output.unsafeMutablePointer)
    }
    for f in 0..<frames {
        let v = Float(0.3 * sin(2 * .pi * 440 * Double(f) / sampleRate))
        inData[f * 2] = v
        inData[f * 2 + 1] = -v
    }
    let bytes = UInt32(frames * 2 * MemoryLayout<Float>.size)
    input[0] = AudioBuffer(mNumberChannels: 2, mDataByteSize: bytes, mData: inData)
    output[0] = AudioBuffer(mNumberChannels: 2, mDataByteSize: bytes, mData: outData)

    let seconds = 30.0
    let blocks = Int(seconds * sampleRate) / frames
    let clock = ContinuousClock()
    let elapsed = clock.measure {
        for _ in 0..<blocks {
            p.render(input: input.unsafePointer, output: output.unsafeMutablePointer)
        }
    }
    let used = Double(elapsed.components.seconds) + Double(elapsed.components.attoseconds) / 1e18
    print("DSP load: \(String(format: "%.3f", used / seconds * 100))% of one core (\(String(format: "%.1f", used / Double(blocks) * 1e6)) µs per 512-frame block)")
    #expect(outData[0].isFinite)
    #expect(used / seconds < 0.5)
}
