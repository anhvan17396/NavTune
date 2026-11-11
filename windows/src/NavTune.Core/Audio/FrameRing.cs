namespace NavTune.Audio;

/// <summary>
/// Interleaved float FIFO between the capture side and the render side of the engine.
/// Used by the engine thread only, so it needs no synchronization. When the reader catches up with the writer
/// it goes back to prebuffering, so playback restarts cleanly after a gap instead of stuttering.
/// </summary>
public sealed unsafe class FrameRing
{
    private readonly float[] buffer;
    private readonly int capacity;
    private readonly int mask;
    private long writeIndex, readIndex;
    private bool prebuffering = true;

    public int Channels { get; }
    public int Capacity => capacity;
    public int Count => (int)(writeIndex - readIndex);
    public bool IsPrebuffering => prebuffering;
    public long Underruns { get; private set; }
    public long DroppedFrames { get; private set; }

    public FrameRing(int minimumFrames, int channels)
    {
        capacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(minimumFrames, 64));
        mask = capacity - 1;
        Channels = channels;
        buffer = GC.AllocateArray<float>(capacity * channels, pinned: true);
    }

    public void Clear()
    {
        readIndex = writeIndex;
        prebuffering = true;
    }

    /// <summary>Appends frames; if the ring is full the oldest frames are dropped.</summary>
    public void Write(float* source, int frames)
    {
        if (frames <= 0) return;
        if (frames > capacity)
        {
            source += (frames - capacity) * Channels;
            DroppedFrames += frames - capacity;
            frames = capacity;
        }
        int overflow = Count + frames - capacity;
        if (overflow > 0)
        {
            readIndex += overflow;
            DroppedFrames += overflow;
        }
        fixed (float* b = buffer)
        {
            int start = (int)(writeIndex & mask);
            int first = Math.Min(frames, capacity - start);
            Buffer.MemoryCopy(source, b + start * Channels, (long)first * Channels * 4, (long)first * Channels * 4);
            if (frames > first)
                Buffer.MemoryCopy(source + first * Channels, b, (long)(frames - first) * Channels * 4, (long)(frames - first) * Channels * 4);
        }
        writeIndex += frames;
    }

    /// <summary>
    /// Copies up to <paramref name="frames"/> frames into <paramref name="destination"/> and zero-fills the rest.
    /// While prebuffering nothing is read until at least <paramref name="prebufferFrames"/> are queued.
    /// Returns the number of real frames copied.
    /// </summary>
    public int Read(float* destination, int frames, int prebufferFrames)
    {
        if (prebuffering)
        {
            if (Count < Math.Max(prebufferFrames, 1))
            {
                new Span<float>(destination, frames * Channels).Clear();
                return 0;
            }
            prebuffering = false;
        }
        int n = Math.Min(frames, Count);
        fixed (float* b = buffer)
        {
            int start = (int)(readIndex & mask);
            int first = Math.Min(n, capacity - start);
            Buffer.MemoryCopy(b + start * Channels, destination, (long)first * Channels * 4, (long)first * Channels * 4);
            if (n > first)
                Buffer.MemoryCopy(b, destination + first * Channels, (long)(n - first) * Channels * 4, (long)(n - first) * Channels * 4);
        }
        readIndex += n;
        if (n < frames)
        {
            new Span<float>(destination + n * Channels, (frames - n) * Channels).Clear();
            prebuffering = true;
            Underruns++;
        }
        return n;
    }

    /// <summary>Discards the oldest frames, e.g. to cut latency that built up after a hiccup.</summary>
    public void Skip(int frames)
    {
        int n = Math.Clamp(frames, 0, Count);
        readIndex += n;
        DroppedFrames += n;
    }
}

/// <summary>
/// Keeps the ring at its target fill although the virtual device and the output device run on different clocks:
/// a PI controller on the smoothed fill level, returning a relative rate correction for the render stream.
/// Critically damped with a time constant of about 20 s, so corrections stay far below audible pitch changes.
/// </summary>
public sealed class DriftController
{
    private const double Kp = 0.1;
    private const double Ki = 0.0025;
    private const double MaxCorrection = 0.005;

    private readonly double sampleRate;
    private double average;
    private double integral;
    private bool initialized;

    public double TargetFrames { get; }
    public double Correction { get; private set; }
    public double AverageFill => average;

    public DriftController(double sampleRate, double targetFrames)
    {
        this.sampleRate = sampleRate;
        TargetFrames = targetFrames;
    }

    /// <summary>
    /// Feeds one measurement and returns the new correction. <paramref name="sinceLastPacket"/> is how long ago the
    /// last capture packet arrived: the virtual device has produced that much more audio that simply hasn't been
    /// delivered yet. Counting it makes the measurement independent of the phase between capture packets and
    /// render events, which otherwise slides slowly with the clock difference and would make the rate wobble.
    /// </summary>
    public double Update(double queuedFrames, double seconds, double sinceLastPacket)
    {
        double fill = queuedFrames + Math.Clamp(sinceLastPacket, 0, 0.02) * sampleRate;
        if (!initialized)
        {
            average = fill;
            initialized = true;
        }
        average += (fill - average) * Math.Min(1, seconds / 1.5);
        double error = (average - TargetFrames) / sampleRate;
        integral = Math.Clamp(integral + error * seconds, -2, 2);
        Correction = Math.Clamp(Kp * error + Ki * integral, -MaxCorrection, MaxCorrection);
        return Correction;
    }

    /// <summary>After a gap the old average says nothing about the new stream.</summary>
    public void Restart() => initialized = false;
}
