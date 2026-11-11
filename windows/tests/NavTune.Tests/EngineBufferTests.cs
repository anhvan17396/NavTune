using NavTune.Audio;
using Xunit.Abstractions;

namespace NavTune.Tests;

public unsafe class EngineBufferTests(ITestOutputHelper output)
{
    [Fact]
    public void RingKeepsOrderAcrossWrapAround()
    {
        var ring = new FrameRing(64, 2);
        var data = new float[200];
        var read = new float[200];
        int next = 0, expected = 0;
        for (int round = 0; round < 50; round++)
        {
            for (int i = 0; i < 40; i++) data[i] = next++;
            fixed (float* d = data) ring.Write(d, 20);
            fixed (float* r = read)
            {
                int got = ring.Read(r, 20, 0);
                Assert.Equal(20, got);
                for (int i = 0; i < 40; i++) Assert.Equal(expected++, read[i]);
            }
        }
    }

    [Fact]
    public void RingPrebuffersAndZeroFillsOnUnderrun()
    {
        var ring = new FrameRing(256, 1);
        var data = Enumerable.Range(1, 10).Select(i => (float)i).ToArray();
        var read = new float[16];
        fixed (float* d = data)
        fixed (float* r = read)
        {
            ring.Write(d, 10);
            Assert.Equal(0, ring.Read(r, 16, prebufferFrames: 20)); // still prebuffering
            Assert.All(read, v => Assert.Equal(0f, v));
            ring.Write(d, 10);
            Assert.Equal(16, ring.Read(r, 16, prebufferFrames: 20));
            Assert.Equal(4, ring.Read(r, 16, prebufferFrames: 20)); // underrun: 4 real frames then zeros
            Assert.Equal(0f, read[4]);
            Assert.True(ring.IsPrebuffering);
            Assert.Equal(1, ring.Underruns);
        }
    }

    [Fact]
    public void RingDropsOldestWhenFull()
    {
        var ring = new FrameRing(64, 1);
        var data = Enumerable.Range(0, 100).Select(i => (float)i).ToArray();
        var read = new float[64];
        fixed (float* d = data)
        fixed (float* r = read)
        {
            ring.Write(d, 100);
            Assert.Equal(64, ring.Count);
            ring.Read(r, 64, 0);
            Assert.Equal(36f, read[0]);
            Assert.Equal(99f, read[63]);
        }
    }

    /// <summary>
    /// Simulates the engine: a virtual device delivering 10 ms packets on its own clock and an output device
    /// consuming on another, with the controller nudging the consumption rate. The queue must settle at its
    /// target and never run dry, for clocks that disagree by up to ±200 ppm.
    /// </summary>
    [Theory]
    [InlineData(200e-6)]
    [InlineData(-200e-6)]
    [InlineData(35e-6)]
    [InlineData(0)]
    public void DriftControllerHoldsTheQueueAtItsTarget(double drift)
    {
        const double rate = 48000;
        const double target = 0.025 * rate;
        var controller = new DriftController(rate, target);
        double queue = target, renderDebt = 0, correction = 0;
        double minQueue = double.MaxValue, maxQueue = 0;
        double correctionSum = 0, minCorrection = double.MaxValue, maxCorrection = double.MinValue;
        int samples = 0;
        var random = new Random(1);
        // Capture packets: 10 ms of audio each, on the virtual device's clock, out of phase with the render events.
        long packet = 0, renderEvent = 1;
        double NextCapture() => 0.0037 + packet * 0.010 / (1 + drift);
        double NextRender() => renderEvent * 0.010 + (random.NextDouble() - 0.5) * 0.002;
        double captureAt = NextCapture(), renderAt = NextRender(), lastPacketAt = 0;
        while (Math.Min(captureAt, renderAt) < 180)
        {
            if (captureAt <= renderAt)
            {
                queue += 480;
                packet++;
                lastPacketAt = captureAt;
                captureAt = NextCapture();
                continue;
            }
            // Render event: the output device consumes 10 ms of its time, scaled by the rate correction.
            renderDebt += 480 * (1 + correction);
            int pulled = (int)renderDebt;
            renderDebt -= pulled;
            queue -= pulled;
            correction = controller.Update(queue, 0.010, renderAt - lastPacketAt);
            if (renderAt > 60)
            {
                minQueue = Math.Min(minQueue, queue);
                maxQueue = Math.Max(maxQueue, queue);
                correctionSum += correction;
                minCorrection = Math.Min(minCorrection, correction);
                maxCorrection = Math.Max(maxCorrection, correction);
                samples++;
            }
            renderEvent++;
            renderAt = NextRender();
        }
        double meanCorrection = correctionSum / samples;
        output.WriteLine($"drift {drift * 1e6:0} ppm: correction mean {meanCorrection * 1e6:0.0} ppm " +
                         $"({minCorrection * 1e6:0}..{maxCorrection * 1e6:0}), queue {minQueue / rate * 1000:0.0}..{maxQueue / rate * 1000:0.0} ms (target {target / rate * 1000:0} ms)");
        Assert.True(minQueue > 0, "queue ran dry");
        Assert.True(maxQueue < target + 0.015 * rate, "latency built up");
        Assert.True(Math.Abs(controller.AverageFill - target) < 0.004 * rate, "average drifted away from the target");
        // The controller must follow the clock difference, with a residual wobble far below audible pitch changes
        // (100 ppm is 0.17 cent).
        Assert.True(Math.Abs(meanCorrection - drift) < 20e-6, "correction did not converge on the clock difference");
        Assert.True(maxCorrection - minCorrection < 300e-6, "correction swings too much");
    }
}
