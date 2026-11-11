using System.Runtime.InteropServices;
using NavTune.Interop;

namespace NavTune.Audio;

/// <summary>
/// Keeps the Windows volume working while NavTune is on.
///
/// The volume keys and the taskbar flyout control the default device, which is now the virtual device. On devices
/// that apply volume "in hardware" (FxSound's does), loopback capture is taken before that volume, so turning it
/// down would change nothing. The mirror copies the virtual device's volume and mute to the real output device
/// (and back, e.g. when Bluetooth headphones change their own volume), so everything behaves as without NavTune.
/// Devices that apply volume in software already deliver attenuated audio, so they are left alone.
/// </summary>
public sealed class VolumeMirror : IDisposable
{
    private static readonly Guid Context = new("6e0b7c43-2f7b-4f0e-9a51-6e61766e7475");

    private readonly Action<Action> post;
    private IAudioEndpointVolume? virtualVolume, outputVolume;
    private Callback? virtualCallback, outputCallback;

    /// <summary>True when the mirror is active (the virtual device applies volume after the loopback point).</summary>
    public bool IsMirroring { get; }

    /// <param name="post">Runs an action on the thread that owns NavTune's state (the UI thread).</param>
    public VolumeMirror(string virtualDeviceId, string outputDeviceId, Action<Action> post)
    {
        this.post = post;
        virtualVolume = Open(virtualDeviceId);
        outputVolume = Open(outputDeviceId);
        if (virtualVolume is null || outputVolume is null) return;
        virtualVolume.QueryHardwareSupport(out uint support);
        IsMirroring = (support & 1) != 0;
        if (!IsMirroring) return;

        // Start from the volume the user had on the real device, so turning NavTune on doesn't change loudness.
        Copy(outputVolume, virtualVolume);
        virtualCallback = new Callback(() => this.post(() => { if (virtualVolume is { } v && outputVolume is { } o) Copy(v, o); }));
        outputCallback = new Callback(() => this.post(() => { if (virtualVolume is { } v && outputVolume is { } o) Copy(o, v); }));
        virtualVolume.RegisterControlChangeNotify(virtualCallback);
        outputVolume.RegisterControlChangeNotify(outputCallback);
    }

    private static IAudioEndpointVolume? Open(string deviceId)
    {
        var enumerator = AudioDevices.CreateEnumerator();
        try
        {
            if (enumerator.GetDevice(deviceId, out var device) < 0) return null;
            try
            {
                var iid = Iids.AudioEndpointVolume;
                return device.Activate(ref iid, NativeMethods.CLSCTX_ALL, IntPtr.Zero, out var volume) >= 0
                    ? (IAudioEndpointVolume)volume
                    : null;
            }
            finally { AudioDevices.Release(device); }
        }
        finally { AudioDevices.Release(enumerator); }
    }

    private static void Copy(IAudioEndpointVolume from, IAudioEndpointVolume to)
    {
        var context = Context;
        if (from.GetMasterVolumeLevelScalar(out float level) >= 0 &&
            to.GetMasterVolumeLevelScalar(out float current) >= 0 && Math.Abs(current - level) > 0.001f)
            to.SetMasterVolumeLevelScalar(level, ref context);
        if (from.GetMute(out bool muted) >= 0 && to.GetMute(out bool currentMute) >= 0 && muted != currentMute)
            to.SetMute(muted, ref context);
    }

    public void Dispose()
    {
        if (virtualCallback is not null) virtualVolume?.UnregisterControlChangeNotify(virtualCallback);
        if (outputCallback is not null) outputVolume?.UnregisterControlChangeNotify(outputCallback);
        virtualCallback = outputCallback = null;
        AudioDevices.Release(virtualVolume);
        AudioDevices.Release(outputVolume);
        virtualVolume = outputVolume = null;
    }

    [ComVisible(true)]
    private sealed class Callback(Action changed) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr notifyData)
        {
            // Ignore the changes the mirror makes itself.
            if (notifyData != IntPtr.Zero && Marshal.PtrToStructure<AudioVolumeNotificationData>(notifyData).EventContext == Context)
                return 0;
            changed();
            return 0;
        }
    }
}
