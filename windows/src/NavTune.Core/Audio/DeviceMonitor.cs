using System.Runtime.InteropServices;
using NavTune.Interop;

namespace NavTune.Audio;

/// <summary>
/// Reports playback device changes from Core Audio. Events are raised on a Core Audio thread: handlers must not
/// call back into the device enumerator synchronously, so they should just post to the UI thread.
/// </summary>
[ComVisible(true)]
public sealed class DeviceMonitor : IMMNotificationClient, IDisposable
{
    private IMMDeviceEnumerator? enumerator;

    /// <summary>A device was added, removed, enabled, disabled, plugged or unplugged.</summary>
    public event Action? DevicesChanged;

    /// <summary>The default playback device changed for a role; the id may be null when there is none.</summary>
    public event Action<ERole, string?>? DefaultChanged;

    public DeviceMonitor()
    {
        enumerator = AudioDevices.CreateEnumerator();
        Hr.Check(enumerator.RegisterEndpointNotificationCallback(this), "Watching audio devices");
    }

    public void Dispose()
    {
        if (enumerator is null) return;
        enumerator.UnregisterEndpointNotificationCallback(this);
        AudioDevices.Release(enumerator);
        enumerator = null;
    }

    int IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        DevicesChanged?.Invoke();
        return 0;
    }

    int IMMNotificationClient.OnDeviceAdded(string deviceId)
    {
        DevicesChanged?.Invoke();
        return 0;
    }

    int IMMNotificationClient.OnDeviceRemoved(string deviceId)
    {
        DevicesChanged?.Invoke();
        return 0;
    }

    int IMMNotificationClient.OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
    {
        if (flow == EDataFlow.Render) DefaultChanged?.Invoke(role, defaultDeviceId);
        return 0;
    }

    int IMMNotificationClient.OnPropertyValueChanged(string deviceId, PropertyKey key)
    {
        // A renamed device or a changed format matters to the device list.
        if (key.FormatId == PropertyKey.DeviceFriendlyName.FormatId && key.PropertyId == PropertyKey.DeviceFriendlyName.PropertyId ||
            key.FormatId == PropertyKey.AudioEngineDeviceFormat.FormatId)
            DevicesChanged?.Invoke();
        return 0;
    }
}
