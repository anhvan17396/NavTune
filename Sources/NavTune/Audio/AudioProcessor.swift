import Accelerate
import CoreAudio
import Foundation
import Synchronization

/// Real-time DSP chain:
/// input gain -> EQ (10 graphic or up to 10 parametric bands) -> bass shelf -> clarity shelf
/// -> stereo width -> loudness leveler -> boost -> limiter.
///
/// The audio thread never allocates, never blocks and never touches Swift arrays: new settings arrive through
/// a preallocated coefficient buffer guarded by a Mutex that the audio thread only try-locks.
/// Audio is processed block-wise in planar buffers, and filters left at 0 dB are skipped entirely.
final class AudioProcessor: @unchecked Sendable {
    static let frequencies: [Double] = [32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000]
    static let bandQ = 1.4
    static let bassFrequency = 90.0
    static let clarityFrequency = 9000.0
    /// Graphic or parametric EQ slots, then bass and clarity shelves.
    static let eqSlots = max(frequencies.count, SoundSettings.maxParametricBands)
    static let filterCount = eqSlots + 2
    /// ~1.3 s at 48 kHz: enough for the spectrum and for content detection polled twice a second.
    static let spectrumCapacity = 65536

    private static let maxChannels = 8
    private static let maxFrames = 16384
    /// The leveler's gain computer runs once per this many frames and is interpolated in between.
    private static let controlInterval = 16

    static func filters(for settings: SoundSettings, sampleRate: Double) -> [Biquad] {
        var filters: [Biquad] = switch settings.mode {
        case .graphic:
            zip(frequencies, settings.gains).map {
                Biquad.peaking(frequency: $0, gainDB: Double($1), q: bandQ, sampleRate: sampleRate)
            }
        case .parametric:
            settings.bands.prefix(eqSlots).map { $0.biquad(sampleRate: sampleRate) }
        }
        filters += [Biquad](repeating: Biquad(), count: eqSlots - filters.count)
        filters.append(.lowShelf(frequency: bassFrequency, gainDB: Double(settings.bass), sampleRate: sampleRate))
        filters.append(.highShelf(frequency: clarityFrequency, gainDB: Double(settings.clarity), sampleRate: sampleRate))
        return filters
    }

    private struct Targets {
        var inputGain: Float = 1
        var outputGain: Float = 1
        var width: Float = 1
        var loudness: Float = 0
        var bypass = false
        var version = 0
    }

    private let shared = Mutex(Targets())
    /// Coefficients handed over by `update`; only touched while holding `shared`.
    private let pending: UnsafeMutablePointer<Biquad>

    private let callbacks = Atomic<UInt64>(0)
    private let ringWrite = Atomic<Int>(0)
    private let inputLevelBits = Atomic<UInt32>(0)
    private let outputLevelBits = Atomic<UInt32>(0)

    // Audio-thread state, preallocated so rendering never allocates.
    private var appliedVersion = -1
    private var target = Targets()
    private let coeffs: UnsafeMutablePointer<Biquad>
    private let activeFilters: UnsafeMutablePointer<Int>
    private var activeCount = 0
    /// Filter state laid out as [channel][filter][z1, z2].
    private let state: UnsafeMutablePointer<Double>
    /// Planar samples: channel c starts at c * maxFrames.
    private let scratch: UnsafeMutablePointer<Float>
    private let ring: UnsafeMutablePointer<Float>
    private var wasBypassed = true
    private var inputGain: Float = 1
    private var outputGain: Float = 1
    private var width: Float = 1
    private var envelope: Float = 0
    private var levelerGain: Float = 1
    private var levelerStep: Float = 0
    private var limiterGain: Float = 1
    private var inputLevel: Float = 0
    private var outputLevel: Float = 0

    // Constants derived from the sample rate and block size.
    private var attack: Float = 0
    private var release: Float = 0
    private var limiterRelease: Float = 0
    private var smoothingFrames: Float = 960
    private var levelFrames: Float = 14400
    private var blockFrames = 0
    private var blockSmoothing: Float = 0
    private var blockLevelDecay: Float = 0
    private let ceiling: Float = 0.95

    private(set) var sampleRate: Double = 48000

    init() {
        let stateCount = Self.maxChannels * Self.filterCount * 2
        pending = .allocate(capacity: Self.filterCount)
        pending.initialize(repeating: Biquad(), count: Self.filterCount)
        coeffs = .allocate(capacity: Self.filterCount)
        coeffs.initialize(repeating: Biquad(), count: Self.filterCount)
        activeFilters = .allocate(capacity: Self.filterCount)
        activeFilters.initialize(repeating: 0, count: Self.filterCount)
        state = .allocate(capacity: stateCount)
        state.initialize(repeating: 0, count: stateCount)
        scratch = .allocate(capacity: Self.maxChannels * Self.maxFrames)
        scratch.initialize(repeating: 0, count: Self.maxChannels * Self.maxFrames)
        ring = .allocate(capacity: Self.spectrumCapacity)
        ring.initialize(repeating: 0, count: Self.spectrumCapacity)
    }

    deinit {
        pending.deallocate()
        coeffs.deallocate()
        activeFilters.deallocate()
        state.deallocate()
        scratch.deallocate()
        ring.deallocate()
    }

    /// Call only while the engine is stopped.
    func prepare(sampleRate: Double) {
        self.sampleRate = sampleRate
        func coefficient(seconds: Double) -> Float { Float(1 - exp(-1 / (seconds * sampleRate))) }
        attack = coefficient(seconds: 0.005)
        release = coefficient(seconds: 0.3)
        limiterRelease = coefficient(seconds: 0.15)
        smoothingFrames = Float(0.02 * sampleRate)
        levelFrames = Float(0.3 * sampleRate)
        blockFrames = 0
        wasBypassed = true
        inputLevel = 0
        outputLevel = 0
        inputLevelBits.store(0, ordering: .relaxed)
        outputLevelBits.store(0, ordering: .relaxed)
        activeCount = 0
        coeffs.update(repeating: Biquad(), count: Self.filterCount)
        appliedVersion = -1
    }

    func update(settings: SoundSettings, bypass: Bool) {
        let filters = Self.filters(for: settings, sampleRate: sampleRate)
        let inputGain = pow(10, settings.preamp / 20)
        let outputGain = pow(10, settings.boost / 20)
        shared.withLock { targets in
            for i in 0..<Self.filterCount { pending[i] = filters[i] }
            targets.inputGain = inputGain
            targets.outputGain = outputGain
            targets.width = settings.width
            targets.loudness = settings.loudness
            targets.bypass = bypass
            targets.version += 1
        }
    }

    var callbackCount: UInt64 { callbacks.load(ordering: .relaxed) }

    /// Smoothed peak level (0...1) of the incoming audio. Non-destructive, safe from any thread.
    var currentInputLevel: Float { Float(bitPattern: inputLevelBits.load(ordering: .relaxed)) }

    /// Smoothed peak level (0...1) of the processed audio.
    var currentOutputLevel: Float { Float(bitPattern: outputLevelBits.load(ordering: .relaxed)) }

    var sampleWritePosition: Int { ringWrite.load(ordering: .acquiring) }

    /// Copies the most recent processed mono samples, e.g. for the spectrum analyzer.
    func copyRecentSamples(into buffer: inout [Float]) {
        let count = min(buffer.count, Self.spectrumCapacity)
        let end = ringWrite.load(ordering: .acquiring)
        let mask = Self.spectrumCapacity - 1
        for i in 0..<count {
            buffer[i] = ring[(end - count + i) & mask]
        }
    }

    /// Copies processed mono samples written since `position` and advances it. Returns the count copied.
    func readSamples(from position: inout Int, into buffer: inout [Float]) -> Int {
        let end = ringWrite.load(ordering: .acquiring)
        let start = max(position, end - Self.spectrumCapacity)
        let count = max(min(end - start, buffer.count), 0)
        let mask = Self.spectrumCapacity - 1
        for i in 0..<count {
            buffer[i] = ring[(start + i) & mask]
        }
        position = start + count
        return count
    }

    // MARK: - Audio thread

    @inline(__always)
    private func channel(_ c: Int) -> UnsafeMutablePointer<Float> {
        scratch + c * Self.maxFrames
    }

    private func pullParams() {
        shared.withLockIfAvailable { targets in
            guard targets.version != appliedVersion else { return }
            activeCount = 0
            for i in 0..<Self.filterCount {
                let k = pending[i]
                if k.isIdentity {
                    // A bypassed filter must start from silence when it comes back.
                    if !coeffs[i].isIdentity { clearState(filter: i) }
                } else {
                    activeFilters[activeCount] = i
                    activeCount += 1
                }
                coeffs[i] = k
            }
            target = targets
            appliedVersion = targets.version
        }
    }

    func render(input: UnsafePointer<AudioBufferList>, output: UnsafeMutablePointer<AudioBufferList>) {
        callbacks.wrappingAdd(1, ordering: .relaxed)
        pullParams()
        let outList = UnsafeMutableAudioBufferListPointer(output)
        let inList = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: input))

        // Tap streams come after any hardware input streams of the aggregate device.
        guard let inBuf = inList.last, let inData = inBuf.mData, inBuf.mNumberChannels > 0 else {
            silence(outList)
            return
        }
        let stride = Int(inBuf.mNumberChannels)
        let channels = min(stride, Self.maxChannels)
        let frames = min(Int(inBuf.mDataByteSize) / (MemoryLayout<Float>.size * stride), Self.maxFrames)
        guard frames > 0 else {
            silence(outList)
            return
        }
        if frames != blockFrames { updateBlockConstants(frames: frames) }

        let src = inData.assumingMemoryBound(to: Float.self)
        for c in 0..<channels {
            let dst = channel(c)
            for f in 0..<frames { dst[f] = src[f * stride + c] }
        }
        let inPeak = peak(frames: frames, channels: channels)

        if target.bypass {
            wasBypassed = true
        } else {
            if wasBypassed {
                clearAllState()
                envelope = 0
                levelerGain = 1
                levelerStep = 0
                limiterGain = 1
                wasBypassed = false
            }
            applyInputGain(frames: frames, channels: channels)
            for i in 0..<activeCount {
                runFilter(activeFilters[i], frames: frames, channels: channels)
            }
            if channels == 2 { applyWidth(frames: frames) }
            applyDynamics(frames: frames, channels: channels)
        }

        inputLevel = max(inPeak, inputLevel * blockLevelDecay)
        outputLevel = max(peak(frames: frames, channels: channels), outputLevel * blockLevelDecay)
        inputLevelBits.store(inputLevel.bitPattern, ordering: .relaxed)
        outputLevelBits.store(outputLevel.bitPattern, ordering: .relaxed)

        writeSpectrum(frames: frames, channels: channels)
        writeOutput(outList, frames: frames, channels: channels)
    }

    private func updateBlockConstants(frames: Int) {
        blockFrames = frames
        blockSmoothing = 1 - exp(-Float(frames) / smoothingFrames)
        blockLevelDecay = exp(-Float(frames) / levelFrames)
    }

    /// One-pole smoothing evaluated per block; returns the ramp to apply across this block.
    @inline(__always)
    private func ramp(_ value: inout Float, toward goal: Float, frames: Int) -> (start: Float, step: Float) {
        let start = value
        value = abs(goal - start) < 1e-6 ? goal : start + (goal - start) * blockSmoothing
        return (start, (value - start) / Float(frames))
    }

    private func peak(frames: Int, channels: Int) -> Float {
        var result: Float = 0
        for c in 0..<channels {
            var p: Float = 0
            vDSP_maxmgv(channel(c), 1, &p, vDSP_Length(frames))
            result = max(result, p)
        }
        return result
    }

    private func applyInputGain(frames: Int, channels: Int) {
        var (start, step) = ramp(&inputGain, toward: target.inputGain, frames: frames)
        if start == 1 && step == 0 { return }
        for c in 0..<channels {
            let x = channel(c)
            if step == 0 {
                vDSP_vsmul(x, 1, &start, x, 1, vDSP_Length(frames))
            } else {
                var s = start
                vDSP_vrampmul(x, 1, &s, &step, x, 1, vDSP_Length(frames))
            }
        }
    }

    /// Runs one biquad over every channel with its state held in registers.
    private func runFilter(_ index: Int, frames: Int, channels: Int) {
        let k = coeffs[index]
        for c in 0..<channels {
            let x = channel(c)
            let z = state + (c * Self.filterCount + index) * 2
            var z1 = z[0], z2 = z[1]
            for f in 0..<frames {
                let input = Double(x[f])
                let y = k.b0 * input + z1
                z1 = k.b1 * input - k.a1 * y + z2
                z2 = k.b2 * input - k.a2 * y
                x[f] = Float(y)
            }
            // Flush denormals so silence never slows the filter down.
            z[0] = abs(z1) < 1e-25 ? 0 : z1
            z[1] = abs(z2) < 1e-25 ? 0 : z2
        }
    }

    private func applyWidth(frames: Int) {
        let (start, step) = ramp(&width, toward: target.width, frames: frames)
        if start == 1 && step == 0 { return }
        let left = channel(0), right = channel(1)
        var w = start
        for f in 0..<frames {
            let mid = (left[f] + right[f]) * 0.5
            let side = (left[f] - right[f]) * 0.5 * w
            left[f] = mid + side
            right[f] = mid - side
            w += step
        }
    }

    /// Loudness leveler, output boost and peak limiter, linked across channels to keep the stereo image.
    private func applyDynamics(frames: Int, channels: Int) {
        var (gain, step) = ramp(&outputGain, toward: target.outputGain, frames: frames)
        let loudness = target.loudness
        let leveling = loudness > 0.01
        let threshold = -10 - 20 * loudness
        let slope = 1 - 1 / (1 + 3 * loudness)
        let makeup = -threshold * slope * 0.5

        for f in 0..<frames {
            var peak: Float = 0
            for c in 0..<channels { peak = max(peak, abs(channel(c)[f])) }

            if leveling {
                envelope += (peak - envelope) * (peak > envelope ? attack : release)
                if f % Self.controlInterval == 0 {
                    let level = 20 * log10(envelope + 1e-9)
                    let goal = pow(10, (makeup - max(0, level - threshold) * slope) / 20)
                    levelerStep = (goal - levelerGain) / Float(Self.controlInterval)
                }
                levelerGain += levelerStep
            } else if levelerGain != 1 {
                levelerGain = abs(levelerGain - 1) < 1e-5 ? 1 : levelerGain + (1 - levelerGain) * limiterRelease
            }

            let total = gain * levelerGain
            let out = peak * total
            let limit = out > ceiling ? ceiling / out : 1
            if limit < limiterGain {
                limiterGain = limit
            } else {
                limiterGain += (limit - limiterGain) * limiterRelease
            }
            let applied = total * limiterGain
            for c in 0..<channels {
                let x = channel(c)
                x[f] = min(max(x[f] * applied, -1), 1)
            }
            gain += step
        }
    }

    private func writeSpectrum(frames: Int, channels: Int) {
        let mask = Self.spectrumCapacity - 1
        var write = ringWrite.load(ordering: .relaxed)
        let scale = 1 / Float(channels)
        for f in 0..<frames {
            var sum: Float = 0
            for c in 0..<channels { sum += channel(c)[f] }
            ring[write & mask] = sum * scale
            write &+= 1
        }
        ringWrite.store(write, ordering: .releasing)
    }

    private func writeOutput(_ list: UnsafeMutableAudioBufferListPointer, frames: Int, channels: Int) {
        let totalChannels = list.reduce(0) { $0 + Int($1.mNumberChannels) }
        var outChannel = 0
        for buffer in list {
            guard let data = buffer.mData, buffer.mNumberChannels > 0 else { continue }
            let stride = Int(buffer.mNumberChannels)
            let dst = data.assumingMemoryBound(to: Float.self)
            let capacity = Int(buffer.mDataByteSize) / (MemoryLayout<Float>.size * stride)
            let count = min(frames, capacity)

            if totalChannels == 1 && channels > 1 {
                // Mono device: mix down instead of dropping the right channel.
                let scale = 1 / Float(channels)
                for f in 0..<count {
                    var sum: Float = 0
                    for c in 0..<channels { sum += channel(c)[f] }
                    dst[f] = sum * scale
                }
            } else {
                for c in 0..<stride {
                    let source = outChannel + c
                    if source < channels {
                        let x = channel(source)
                        for f in 0..<count { dst[f * stride + c] = x[f] }
                    } else {
                        for f in 0..<count { dst[f * stride + c] = 0 }
                    }
                }
            }
            if capacity > count {
                memset(dst + count * stride, 0, (capacity - count) * stride * MemoryLayout<Float>.size)
            }
            outChannel += stride
        }
    }

    private func clearState(filter: Int) {
        for c in 0..<Self.maxChannels {
            let z = state + (c * Self.filterCount + filter) * 2
            z[0] = 0
            z[1] = 0
        }
    }

    private func clearAllState() {
        state.update(repeating: 0, count: Self.maxChannels * Self.filterCount * 2)
    }

    private func silence(_ list: UnsafeMutableAudioBufferListPointer) {
        for buf in list {
            if let data = buf.mData { memset(data, 0, Int(buf.mDataByteSize)) }
        }
    }
}
