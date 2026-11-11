using System.Diagnostics;
using System.Runtime.InteropServices;
using NavTune.Interop;

namespace NavTune.Audio;

public enum LatencyMode
{
    Low = 0,
    Balanced = 1,
    Safe = 2,
}

public enum EngineState
{
    Off,
    /// <summary>Processing audio.</summary>
    Running,
    /// <summary>Set up but paused because nothing is playing: no CPU use, and the output device can sleep.</summary>
    Standby,
}

/// <summary>A state change of one engine run (<see cref="RunId"/>), so listeners can drop events from older runs.</summary>
public readonly record struct EngineEvent(EngineState State, Exception? Error = null, int RunId = 0);

/// <summary>Formats the engine settled on, for the Devices page and the log.</summary>
public sealed record EngineInfo(AudioFormat Capture, int SampleRate, int Channels, bool ClockAdjusted, double LatencyMs);

/// <summary>
/// The Windows counterpart of the macOS tap chain: captures everything played to a virtual device (loopback),
/// runs it through the <see cref="AudioProcessor"/>, and plays the result on the real output device.
///
/// One real-time thread (MMCSS "Pro Audio") owns both WASAPI streams. Capture packets are processed as they arrive
/// and queued in a <see cref="FrameRing"/>; the render stream pulls from it on its own events. The two devices run
/// on different clocks, so the render stream's rate is nudged through IAudioClockAdjustment (the Windows audio
/// engine's own resampler) to keep the queue at its target length. When nothing has played for a while the render
/// stream is stopped and the thread sleeps until the next capture packet arrives.
///
/// Every start creates its own <see cref="Run"/> (thread, event handles, state). If a driver hangs a thread past the
/// start or stop timeouts, that run is abandoned: it can never touch a later run, and it closes its own handles
/// whenever the driver lets it go.
/// </summary>
public sealed unsafe class AudioEngine : IDisposable
{
    public static readonly TimeSpan StandbyDelay = TimeSpan.FromSeconds(10);
    /// <summary>About -80 dBFS: quieter input counts as silence.</summary>
    private const float SignalThreshold = 1e-4f;
    /// <summary>The output device must ask for audio at least this often while playing.</summary>
    private static readonly long StallTicks = Stopwatch.Frequency * 2;

    public AudioProcessor Processor { get; } = new();

    /// <summary>Raised on the engine thread.</summary>
    public event Action<EngineEvent>? StateChanged;

    private sealed class Run(int id)
    {
        public readonly int Id = id;
        public IntPtr StopEvent, CaptureEvent, RenderEvent;
        public readonly ManualResetEventSlim Started = new(false);
        public Exception? StartError;
        public Thread? Thread;
        public volatile bool Abandoned;
        public int State;
        public long RenderPasses;
        public volatile EngineInfo? Info;
        public double QueuedMs, DriftCorrection;
        public long Underruns;
        private int closed;

        public void CloseHandles()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            if (StopEvent != IntPtr.Zero) CloseHandle(StopEvent);
            if (CaptureEvent != IntPtr.Zero) CloseHandle(CaptureEvent);
            if (RenderEvent != IntPtr.Zero) CloseHandle(RenderEvent);
            Started.Dispose();
        }
    }

    private volatile Run? current;
    private int runCounter;

    /// <summary>Identifies the current run in <see cref="EngineEvent.RunId"/>; 0 when stopped.</summary>
    public int RunId => current?.Id ?? 0;
    public EngineState State => current is { } run ? (EngineState)Volatile.Read(ref run.State) : EngineState.Off;
    public EngineInfo? Info => current?.Info;
    public bool IsActive => State != EngineState.Off;
    public bool IsProcessing => State == EngineState.Running;
    /// <summary>Increments on every render event of the current run.</summary>
    public long RenderPasses => current is { } run ? Interlocked.Read(ref run.RenderPasses) : 0;
    public long Underruns => current?.Underruns ?? 0;
    public double DriftCorrection => current?.DriftCorrection ?? 0;
    public double QueuedMs => current?.QueuedMs ?? 0;

    /// <summary>Starts capturing <paramref name="captureDeviceId"/> (a render endpoint, via loopback) and playing to
    /// <paramref name="renderDeviceId"/>. Blocks until both streams are open; throws if either fails.</summary>
    public void Start(string captureDeviceId, string renderDeviceId, LatencyMode latency)
    {
        Stop();
        var run = new Run(Interlocked.Increment(ref runCounter))
        {
            StopEvent = CreateEvent(IntPtr.Zero, false, false, null),
            CaptureEvent = CreateEvent(IntPtr.Zero, false, false, null),
            RenderEvent = CreateEvent(IntPtr.Zero, false, false, null),
        };
        run.Thread = new Thread(() => RunThread(run, captureDeviceId, renderDeviceId, latency))
        {
            Name = "NavTune audio",
            IsBackground = true,
            Priority = ThreadPriority.Highest,
        };
        run.Thread.SetApartmentState(ApartmentState.MTA);
        current = run;
        run.Thread.Start();
        if (!run.Started.Wait(TimeSpan.FromSeconds(8)))
        {
            current = null;
            Abandon(run);
            throw new AudioException("Audio engine did not start in time", unchecked((int)0x800705B4));
        }
        if (run.StartError is { } error)
        {
            Stop();
            throw error;
        }
    }

    public void Stop()
    {
        var run = current;
        if (run is null) return;
        current = null;
        SetEvent(run.StopEvent);
        if (run.Thread!.Join(TimeSpan.FromSeconds(3))) run.CloseHandles();
        else
        {
            Trace.TraceWarning("NavTune audio thread did not stop in time; abandoning it");
            Abandon(run);
        }
        Processor.ResetLevels();
    }

    public void Dispose() => Stop();

    /// <summary>Lets a stuck run go: it closes its own handles when (if) its thread ever returns.</summary>
    private static void Abandon(Run run)
    {
        run.Abandoned = true;
        SetEvent(run.StopEvent);
        if (!run.Thread!.IsAlive) run.CloseHandles();
    }

    private void Raise(Run run, EngineState next, Exception? error = null)
    {
        Volatile.Write(ref run.State, (int)next);
        if (run.Abandoned) return;
        try { StateChanged?.Invoke(new EngineEvent(next, error, run.Id)); }
        catch (Exception e) { Trace.TraceError($"NavTune engine listener failed: {e}"); }
    }

    // MARK: - Engine thread

    private sealed class Streams : IDisposable
    {
        public IAudioClient? CaptureClient, RenderClient;
        public IAudioCaptureClient? Capture;
        public IAudioRenderClient? Render;
        public IAudioClockAdjustment? Clock;

        public void Dispose()
        {
            try { CaptureClient?.Stop(); } catch { }
            try { RenderClient?.Stop(); } catch { }
            AudioDevices.Release(Clock);
            AudioDevices.Release(Render);
            AudioDevices.Release(Capture);
            AudioDevices.Release(RenderClient);
            AudioDevices.Release(CaptureClient);
        }
    }

    private void RunThread(Run run, string captureDeviceId, string renderDeviceId, LatencyMode latency)
    {
        uint taskIndex = 0;
        IntPtr mmcss = NativeMethods.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
        bool opened = false;
        try
        {
            using var streams = new Streams();
            try
            {
                var setup = Open(run, streams, captureDeviceId, renderDeviceId, latency);
                opened = true;
                Volatile.Write(ref run.State, (int)EngineState.Standby);
                run.Started.Set();
                Raise(run, EngineState.Standby);
                Loop(run, streams, setup);
            }
            catch (Exception e)
            {
                if (!opened)
                {
                    run.StartError = e;
                    run.Started.Set();
                }
                else
                {
                    Trace.TraceError($"NavTune engine failed: {e.Message}");
                    Raise(run, EngineState.Off, e);
                }
            }
        }
        finally
        {
            Volatile.Write(ref run.State, (int)EngineState.Off);
            if (mmcss != IntPtr.Zero) NativeMethods.AvRevertMmThreadCharacteristics(mmcss);
            if (run.Abandoned) run.CloseHandles();
        }
    }

    private sealed class Setup
    {
        public required AudioFormat Capture;
        public required int Channels;
        public required int SampleRate;
        public required int RenderBufferFrames;
        public required int RenderTargetFrames;
        public required int RingTargetFrames;
        public required FrameRing Ring;
        public required float[] InScratch;
        public required float[] OutScratch;
        public required int ScratchFrames;
    }

    private Setup Open(Run run, Streams streams, string captureDeviceId, string renderDeviceId, LatencyMode latency)
    {
        var enumerator = AudioDevices.CreateEnumerator();
        IMMDevice? captureDevice = null, renderDevice = null;
        try
        {
            Hr.Check(enumerator.GetDevice(captureDeviceId, out captureDevice), "Opening the virtual device");
            Hr.Check(enumerator.GetDevice(renderDeviceId, out renderDevice), "Opening the output device");

            // Capture: loopback of the virtual device in its own mix format.
            streams.CaptureClient = ActivateClient(captureDevice, "virtual device");
            Hr.Check(streams.CaptureClient.GetMixFormat(out var mixPointer), "Reading the virtual device format");
            AudioFormat capture;
            try
            {
                capture = AudioFormat.FromPointer(mixPointer);
                Hr.Check(streams.CaptureClient.Initialize(AudioClientShareMode.Shared,
                    AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
                    2_000_000, 0, mixPointer, IntPtr.Zero), "Starting system audio capture");
            }
            finally { NativeMethods.CoTaskMemFree(mixPointer); }
            Hr.Check(streams.CaptureClient.SetEventHandle(run.CaptureEvent), "Setting up capture events");
            streams.Capture = GetService<IAudioCaptureClient>(streams.CaptureClient, Iids.AudioCaptureClient, "capture");

            int channels = Math.Clamp((int)capture.Channels, 1, AudioProcessor.MaxChannels);
            int rate = capture.SampleRate;
            if (run.Abandoned) throw new OperationCanceledException();
            Processor.Prepare(rate);

            // Render: float at the capture rate; the Windows engine converts to the device format and lets us
            // fine-tune the rate to follow the virtual device's clock.
            var (ringMs, padMs) = latency switch
            {
                LatencyMode.Low => (15, 20),
                LatencyMode.Safe => (50, 60),
                _ => (25, 30),
            };
            uint mask = channels == capture.Channels && capture.ChannelMask != 0
                ? capture.ChannelMask
                : WaveFormatExtensible.DefaultChannelMask(channels);
            var format = WaveFormatExtensible.Float(rate, channels, mask);
            long bufferDuration = Math.Max(padMs * 2, 40) * 10_000L;
            var flags = AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm |
                        AudioClientStreamFlags.SrcDefaultQuality | AudioClientStreamFlags.RateAdjust;
            streams.RenderClient = ActivateClient(renderDevice, "output device");
            int hr = streams.RenderClient.Initialize(AudioClientShareMode.Shared, flags, bufferDuration, 0, (IntPtr)(&format), IntPtr.Zero);
            if (hr < 0)
            {
                // Some drivers refuse rate adjustment; fall back to dropping frames when the queue grows.
                AudioDevices.Release(streams.RenderClient);
                streams.RenderClient = ActivateClient(renderDevice, "output device");
                flags &= ~AudioClientStreamFlags.RateAdjust;
                Hr.Check(streams.RenderClient.Initialize(AudioClientShareMode.Shared, flags, bufferDuration, 0, (IntPtr)(&format), IntPtr.Zero),
                    "Starting playback on the output device");
            }
            Hr.Check(streams.RenderClient.SetEventHandle(run.RenderEvent), "Setting up playback events");
            Hr.Check(streams.RenderClient.GetBufferSize(out uint bufferFrames), "Reading the playback buffer size");
            streams.RenderClient.GetDevicePeriod(out long devicePeriod, out _);
            streams.Render = GetService<IAudioRenderClient>(streams.RenderClient, Iids.AudioRenderClient, "render");
            if ((flags & AudioClientStreamFlags.RateAdjust) != 0)
            {
                var clockIid = Iids.AudioClockAdjustment;
                if (streams.RenderClient.GetService(ref clockIid, out var clock) >= 0) streams.Clock = (IAudioClockAdjustment)clock;
            }

            int periodFrames = (int)Math.Max(rate * Math.Max(devicePeriod, 30_000) / 10_000_000, 64);
            int renderTarget = Math.Min(Math.Max(rate * padMs / 1000, periodFrames * 2), (int)bufferFrames);
            int ringTarget = rate * ringMs / 1000;
            int scratchFrames = rate; // one second; larger packets are processed in pieces
            var setup = new Setup
            {
                Capture = capture,
                Channels = channels,
                SampleRate = rate,
                RenderBufferFrames = (int)bufferFrames,
                RenderTargetFrames = renderTarget,
                RingTargetFrames = ringTarget,
                Ring = new FrameRing(rate * 2, channels),
                InScratch = GC.AllocateArray<float>(scratchFrames * channels, pinned: true),
                OutScratch = GC.AllocateArray<float>(scratchFrames * channels, pinned: true),
                ScratchFrames = scratchFrames,
            };
            Hr.Check(streams.CaptureClient.Start(), "Starting system audio capture");
            run.Info = new EngineInfo(capture, rate, channels, streams.Clock is not null,
                1000.0 * (ringTarget + renderTarget) / rate + 10);
            return setup;
        }
        finally
        {
            AudioDevices.Release(captureDevice);
            AudioDevices.Release(renderDevice);
            AudioDevices.Release(enumerator);
        }
    }

    private static IAudioClient ActivateClient(IMMDevice device, string what)
    {
        var iid = Iids.AudioClient;
        Hr.Check(device.Activate(ref iid, NativeMethods.CLSCTX_ALL, IntPtr.Zero, out var instance), $"Activating the {what}");
        return (IAudioClient)instance;
    }

    private static T GetService<T>(IAudioClient client, Guid iid, string what) where T : class
    {
        Hr.Check(client.GetService(ref iid, out var service), $"Getting the {what} service");
        return (T)service;
    }

    private void Loop(Run run, Streams streams, Setup setup)
    {
        var handles = stackalloc IntPtr[3] { run.StopEvent, run.CaptureEvent, run.RenderEvent };
        var clock = Stopwatch.StartNew();
        long lastSignal = long.MinValue;
        long lastPacket = 0;
        long lastRenderEvent = 0;
        long standbyTicks = (long)(StandbyDelay.TotalSeconds * Stopwatch.Frequency);
        bool rendering = false;
        var drift = new DriftController(setup.SampleRate, setup.RingTargetFrames);
        double appliedCorrection = double.NaN;
        long lastClockUpdate = 0;
        var render = streams.Render!;
        var renderClient = streams.RenderClient!;
        var capture = streams.Capture!;

        while (true)
        {
            uint wait = WaitForMultipleObjects(rendering ? 3u : 2u, handles, false, rendering ? 200u : 1000u);
            if (wait == WAIT_OBJECT_0 || run.Abandoned) return;
            if (wait == WAIT_FAILED) throw new AudioException("Waiting for audio events", Marshal.GetHRForLastWin32Error());

            // 1. Process every capture packet that is ready.
            while (true)
            {
                Hr.Check(capture.GetNextPacketSize(out uint packet), "Reading system audio");
                if (packet == 0) break;
                int hr = capture.GetBuffer(out var data, out uint frames, out var flags, out _, out _);
                if (hr == Hr.AUDCLNT_S_BUFFER_EMPTY) break;
                Hr.Check(hr, "Reading system audio");
                try
                {
                    bool silent = (flags & AudioClientBufferFlags.Silent) != 0;
                    int offset = 0;
                    while (offset < frames)
                    {
                        int n = Math.Min((int)frames - offset, setup.ScratchFrames);
                        fixed (float* input = setup.InScratch)
                        fixed (float* output = setup.OutScratch)
                        {
                            bool hasSignal = !silent && Convert((byte*)data + offset * setup.Capture.BlockAlign, input, n, setup);
                            if (!rendering)
                            {
                                if (!hasSignal) { offset += n; continue; }
                                // Something is playing: wake up.
                                StartRendering(renderClient, render, setup);
                                drift.Restart();
                                rendering = true;
                                lastRenderEvent = clock.ElapsedTicks;
                                Raise(run, EngineState.Running);
                            }
                            float peak = Processor.Process(silent ? null : input, output, n, setup.Channels);
                            lastPacket = clock.ElapsedTicks;
                            if (peak > SignalThreshold) lastSignal = lastPacket;
                            setup.Ring.Write(output, n);
                        }
                        offset += n;
                    }
                }
                finally
                {
                    capture.ReleaseBuffer(frames);
                }
            }

            if (!rendering) continue;

            // 2. Top up the output device. A device that stops asking for audio without reporting an error (it was
            //    unplugged badly, or a driver hung) must not leave NavTune "running" in silence.
            if (wait == WAIT_OBJECT_0 + 2) lastRenderEvent = clock.ElapsedTicks;
            else if (clock.ElapsedTicks - lastRenderEvent > StallTicks)
                throw new AudioException("The output device stopped requesting audio", Hr.AUDCLNT_E_DEVICE_INVALIDATED);

            Hr.Check(renderClient.GetCurrentPadding(out uint padding), "Playing audio");
            int want = Math.Min(setup.RenderTargetFrames - (int)padding, setup.RenderBufferFrames - (int)padding);
            if (want > 0)
            {
                Hr.Check(render.GetBuffer((uint)want, out var buffer), "Playing audio");
                int got = setup.Ring.Read((float*)buffer, want, setup.RingTargetFrames);
                render.ReleaseBuffer((uint)want, got == 0 ? AudioClientBufferFlags.Silent : AudioClientBufferFlags.None);
                Interlocked.Increment(ref run.RenderPasses);

                // 3. Follow the virtual device's clock.
                if (!setup.Ring.IsPrebuffering && got > 0)
                {
                    int queued = setup.Ring.Count;
                    if (queued > setup.RingTargetFrames + setup.SampleRate / 5)
                    {
                        // Way behind after a hiccup: jump back to the target instead of playing late.
                        setup.Ring.Skip(queued - setup.RingTargetFrames);
                        drift.Restart();
                    }
                    else if (streams.Clock is { } adjust)
                    {
                        long now = clock.ElapsedTicks;
                        double correction = drift.Update(queued, (double)want / setup.SampleRate,
                            (double)(now - lastPacket) / Stopwatch.Frequency);
                        if (double.IsNaN(appliedCorrection) ||
                            (Math.Abs(correction - appliedCorrection) > 2e-6 && now - lastClockUpdate > Stopwatch.Frequency / 10))
                        {
                            adjust.SetSampleRate((float)(setup.SampleRate * (1 + correction)));
                            appliedCorrection = correction;
                            lastClockUpdate = now;
                        }
                    }
                    else if (queued > setup.RingTargetFrames * 2 + 64)
                    {
                        setup.Ring.Skip(Math.Min(queued - setup.RingTargetFrames, 8));
                    }
                }
                run.QueuedMs = 1000.0 * setup.Ring.Count / setup.SampleRate;
                run.DriftCorrection = double.IsNaN(appliedCorrection) ? 0 : appliedCorrection;
                run.Underruns = setup.Ring.Underruns;
            }

            // 4. Nothing audible for a while: stop the output so the device can sleep. Apps that keep a stream open
            //    send silent packets all along, so this goes by the last time anything was heard, not by the queue.
            if (lastSignal == long.MinValue || clock.ElapsedTicks - lastSignal > standbyTicks)
            {
                renderClient.Stop();
                renderClient.Reset();
                setup.Ring.Clear();
                Processor.ResetLevels();
                rendering = false;
                if (streams.Clock is { } adjust) adjust.SetSampleRate(setup.SampleRate);
                appliedCorrection = double.NaN;
                Raise(run, EngineState.Standby);
            }
        }
    }

    private static void StartRendering(IAudioClient client, IAudioRenderClient render, Setup setup)
    {
        // One period of silence so the device has something to play while the queue fills.
        int prefill = Math.Min(setup.RenderTargetFrames / 2, setup.RenderBufferFrames);
        if (prefill > 0 && render.GetBuffer((uint)prefill, out _) >= 0)
            render.ReleaseBuffer((uint)prefill, AudioClientBufferFlags.Silent);
        setup.Ring.Clear();
        Hr.Check(client.Start(), "Starting playback");
    }

    /// <summary>Converts a capture packet to interleaved float in the processing channel count. Returns true if any
    /// sample is above the silence threshold.</summary>
    private static bool Convert(byte* source, float* destination, int frames, Setup setup)
    {
        var format = setup.Capture;
        int outChannels = setup.Channels;
        float peak = 0;
        if (format.IsFloat && format.BitsPerSample == 32 && format.Channels == outChannels)
        {
            // The usual case: the mix format is already interleaved float.
            int count = frames * outChannels;
            float* src = (float*)source;
            for (int i = 0; i < count; i++)
            {
                float v = src[i];
                destination[i] = v;
                float a = Math.Abs(v);
                if (a > peak) peak = a;
            }
            return peak > SignalThreshold;
        }
        int container = Math.Max(format.BlockAlign / Math.Max(format.Channels, 1), 1);
        for (int f = 0; f < frames; f++)
        {
            byte* frame = source + f * format.BlockAlign;
            for (int c = 0; c < outChannels; c++)
            {
                float v = Sample(frame + c * container, container, format.IsFloat);
                destination[f * outChannels + c] = v;
                float a = Math.Abs(v);
                if (a > peak) peak = a;
            }
        }
        return peak > SignalThreshold;
    }

    private static float Sample(byte* p, int bytes, bool isFloat)
    {
        if (isFloat) return bytes == 8 ? (float)*(double*)p : *(float*)p;
        return bytes switch
        {
            2 => *(short*)p / 32768f,
            3 => ((p[0] << 8) | (p[1] << 16) | (p[2] << 24)) / 2147483648f,
            4 => *(int*)p / 2147483648f,
            _ => (*p - 128) / 128f,
        };
    }

    // MARK: - Win32

    private const uint WAIT_OBJECT_0 = 0;
    private const uint WAIT_FAILED = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForMultipleObjects(uint count, IntPtr* handles, bool waitAll, uint milliseconds);
}
