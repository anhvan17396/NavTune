using System.Runtime.InteropServices;
using NavTune.Interop;

namespace NavTune.Audio;

public enum DeviceTransport
{
    Other,
    BuiltIn,
    Bluetooth,
    Usb,
    Hdmi,
    Spdif,
    Virtual,
}

/// <summary>A playback endpoint as NavTune shows it.</summary>
public sealed record AudioDevice(
    string Id,
    string Name,
    string AdapterName,
    DeviceState State,
    DeviceTransport Transport,
    int FormFactor,
    AudioFormat? Format)
{
    public bool IsActive => State == DeviceState.Active;
    public bool IsVirtual => Transport == DeviceTransport.Virtual;
    public bool IsBluetooth => Transport == DeviceTransport.Bluetooth;

    /// <summary>Headphones or headset form factor.</summary>
    public bool IsHeadphones => FormFactor is 3 or 5;
}

/// <summary>Enumerates endpoints and reads their properties. Safe to call from any thread.</summary>
public static class AudioDevices
{
    /// <summary>Endpoint names of virtual devices NavTune can capture system audio from, best first.</summary>
    private static readonly string[] KnownVirtualDevices =
    [
        "CABLE Input",          // VB-Audio Virtual Cable
        "CABLE In 16",          // VB-Audio Virtual Cable (16 ch edition)
        "FxSound",              // FxSound Audio Enhancer
        "Virtual Audio",        // Generic virtual drivers
        "VB-Audio",
    ];

    public static IMMDeviceEnumerator CreateEnumerator() =>
        (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    public static List<AudioDevice> RenderDevices(bool includeInactive = false)
    {
        var enumerator = CreateEnumerator();
        try
        {
            var mask = includeInactive ? DeviceState.Active | DeviceState.Unplugged | DeviceState.Disabled : DeviceState.Active;
            Hr.Check(enumerator.EnumAudioEndpoints(EDataFlow.Render, mask, out var collection), "EnumAudioEndpoints");
            try
            {
                Hr.Check(collection.GetCount(out int count), "IMMDeviceCollection.GetCount");
                var result = new List<AudioDevice>(count);
                for (int i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) < 0) continue;
                    try
                    {
                        if (Describe(device) is { } info) result.Add(info);
                    }
                    finally { Release(device); }
                }
                result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
                return result;
            }
            finally { Release(collection); }
        }
        finally { Release(enumerator); }
    }

    public static string? DefaultRenderDeviceId(ERole role = ERole.Multimedia)
    {
        var enumerator = CreateEnumerator();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, role, out var device) < 0) return null;
            try { return device.GetId(out var id) >= 0 ? id : null; }
            finally { Release(device); }
        }
        finally { Release(enumerator); }
    }

    public static AudioDevice? Find(string id)
    {
        var enumerator = CreateEnumerator();
        try
        {
            if (enumerator.GetDevice(id, out var device) < 0) return null;
            try { return Describe(device); }
            finally { Release(device); }
        }
        finally { Release(enumerator); }
    }

    /// <summary>The virtual device NavTune should capture from: the preferred one if present, else the best known one.</summary>
    public static AudioDevice? PickVirtualDevice(IReadOnlyList<AudioDevice> devices, string? preferredId)
    {
        if (preferredId is not null && devices.FirstOrDefault(d => d.Id == preferredId && d.IsActive) is { } preferred)
            return preferred;
        foreach (var name in KnownVirtualDevices)
        {
            var match = devices.FirstOrDefault(d => d.IsActive && d.IsVirtual &&
                (d.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                 d.AdapterName.Contains(name, StringComparison.OrdinalIgnoreCase)));
            if (match is not null) return match;
        }
        return devices.FirstOrDefault(d => d.IsActive && d.IsVirtual);
    }

    public static AudioDevice? Describe(IMMDevice device)
    {
        if (device.GetId(out var id) < 0 || string.IsNullOrEmpty(id)) return null;
        device.GetState(out var state);
        string name = id, adapter = "", enumeratorName = "";
        int formFactor = 10;
        AudioFormat? format = null;
        if (device.OpenPropertyStore(NativeMethods.STGM_READ, out var store) >= 0)
        {
            try
            {
                name = ReadString(store, PropertyKey.DeviceFriendlyName) ?? id;
                adapter = ReadString(store, PropertyKey.DeviceInterfaceFriendlyName) ?? "";
                enumeratorName = ReadString(store, PropertyKey.DeviceEnumeratorName) ?? "";
                formFactor = (int)(ReadUInt(store, PropertyKey.AudioEndpointFormFactor) ?? 10);
                format = AudioFormat.FromBlob(ReadBlob(store, PropertyKey.AudioEngineDeviceFormat));
            }
            finally { Release(store); }
        }
        return new AudioDevice(id, name, adapter, state, Classify(enumeratorName, formFactor, name, adapter), formFactor, format);
    }

    /// <summary>Maps the bus that enumerated the device and its form factor to a transport, the way users think of it.</summary>
    internal static DeviceTransport Classify(string enumeratorName, int formFactor, string name, string adapter)
    {
        string e = enumeratorName.ToUpperInvariant();
        if (e.StartsWith("BTH") || e.Contains("BLUETOOTH")) return DeviceTransport.Bluetooth;
        if (e.StartsWith("USB")) return DeviceTransport.Usb;
        if (e == "ROOT" || e == "SWD" || e.StartsWith("VIRTUAL") ||
            KnownVirtualDevices.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                                         adapter.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return DeviceTransport.Virtual;
        if (formFactor == 9) return DeviceTransport.Hdmi;
        if (formFactor == 8) return DeviceTransport.Spdif;
        if (e.StartsWith("HDAUDIO") || e.StartsWith("INTELAUDIO") || e.StartsWith("ACPI")) return DeviceTransport.BuiltIn;
        return DeviceTransport.Other;
    }

    private static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) < 0) return null;
        try { return value.AsString(); }
        finally { NativeMethods.PropVariantClear(ref value); }
    }

    private static uint? ReadUInt(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) < 0) return null;
        try { return value.AsUInt(); }
        finally { NativeMethods.PropVariantClear(ref value); }
    }

    private static byte[]? ReadBlob(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) < 0) return null;
        try { return value.AsBlob(); }
        finally { NativeMethods.PropVariantClear(ref value); }
    }

    public static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
    }
}

/// <summary>Changes the Windows default playback device (all the roles a user means by "default").</summary>
public static class DefaultDevice
{
    public static void Set(string deviceId, bool includeCommunications = false)
    {
        var policy = (IPolicyConfig)new PolicyConfigClientComObject();
        try
        {
            Hr.Check(policy.SetDefaultEndpoint(deviceId, ERole.Console), "SetDefaultEndpoint(Console)");
            Hr.Check(policy.SetDefaultEndpoint(deviceId, ERole.Multimedia), "SetDefaultEndpoint(Multimedia)");
            if (includeCommunications)
                Hr.Check(policy.SetDefaultEndpoint(deviceId, ERole.Communications), "SetDefaultEndpoint(Communications)");
        }
        finally { AudioDevices.Release(policy); }
    }

    /// <summary>Sets one role only; used to verify the interface without changing what the user hears.</summary>
    public static int SetRole(string deviceId, ERole role)
    {
        var policy = (IPolicyConfig)new PolicyConfigClientComObject();
        try { return policy.SetDefaultEndpoint(deviceId, role); }
        finally { AudioDevices.Release(policy); }
    }
}
