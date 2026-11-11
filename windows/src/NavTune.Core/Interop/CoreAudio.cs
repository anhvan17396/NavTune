using System.Runtime.InteropServices;

namespace NavTune.Interop;

// Minimal COM interop for the Windows Core Audio APIs (MMDevice, WASAPI, endpoint volume, sessions).
// Every method keeps its HRESULT ([PreserveSig]) so the real-time path never pays for exceptions;
// callers use Hr.Check where a failure should throw.

public enum EDataFlow { Render = 0, Capture = 1, All = 2 }

public enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

[Flags]
public enum DeviceState : uint
{
    Active = 0x1,
    Disabled = 0x2,
    NotPresent = 0x4,
    Unplugged = 0x8,
    All = 0xF,
}

public enum AudioSessionState { Inactive = 0, Active = 1, Expired = 2 }

[Flags]
public enum AudioClientStreamFlags : uint
{
    None = 0,
    CrossProcess = 0x00010000,
    Loopback = 0x00020000,
    EventCallback = 0x00040000,
    NoPersist = 0x00080000,
    RateAdjust = 0x00100000,
    SrcDefaultQuality = 0x08000000,
    AutoConvertPcm = 0x80000000,
}

[Flags]
public enum AudioClientBufferFlags : uint
{
    None = 0,
    DataDiscontinuity = 0x1,
    Silent = 0x2,
    TimestampError = 0x4,
}

public enum AudioClientShareMode { Shared = 0, Exclusive = 1 }

[StructLayout(LayoutKind.Sequential)]
public struct PropertyKey
{
    public Guid FormatId;
    public int PropertyId;

    public PropertyKey(Guid formatId, int propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }

    public static readonly PropertyKey DeviceFriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    public static readonly PropertyKey DeviceDescription = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    public static readonly PropertyKey DeviceEnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
    public static readonly PropertyKey DeviceInterfaceFriendlyName = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);
    public static readonly PropertyKey AudioEndpointFormFactor = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
    public static readonly PropertyKey AudioEngineDeviceFormat = new(new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0);
}

/// <summary>PROPVARIANT, laid out for x64/x86 (24/16 bytes). Only the members NavTune reads are mapped.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
public struct PropVariant
{
    [FieldOffset(0)] public ushort ValueType;
    [FieldOffset(8)] public uint UIntValue;
    [FieldOffset(8)] public IntPtr PointerValue;

    public const ushort VT_EMPTY = 0, VT_UI4 = 19, VT_LPWSTR = 31, VT_BLOB = 65;

    public string? AsString() => ValueType == VT_LPWSTR ? Marshal.PtrToStringUni(PointerValue) : null;

    public uint? AsUInt() => ValueType == VT_UI4 ? UIntValue : null;

    /// <summary>BLOB: cbSize at offset 8, pBlobData at the next pointer-aligned slot.</summary>
    public unsafe byte[]? AsBlob()
    {
        if (ValueType != VT_BLOB) return null;
        fixed (PropVariant* p = &this)
        {
            byte* b = (byte*)p;
            uint size = *(uint*)(b + 8);
            IntPtr data = *(IntPtr*)(b + 8 + IntPtr.Size);
            if (data == IntPtr.Zero || size == 0) return [];
            var result = new byte[size];
            Marshal.Copy(data, result, 0, (int)size);
            return result;
        }
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct WaveFormatEx
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort ExtraSize;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct WaveFormatExtensible
{
    public WaveFormatEx Format;
    public ushort ValidBitsPerSample;
    public uint ChannelMask;
    public Guid SubFormat;

    public const ushort WAVE_FORMAT_PCM = 1, WAVE_FORMAT_IEEE_FLOAT = 3, WAVE_FORMAT_EXTENSIBLE = 0xFFFE;
    public static readonly Guid SubTypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid SubTypeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public static WaveFormatExtensible Float(int sampleRate, int channels, uint channelMask) => new()
    {
        Format = new WaveFormatEx
        {
            FormatTag = WAVE_FORMAT_EXTENSIBLE,
            Channels = (ushort)channels,
            SamplesPerSec = (uint)sampleRate,
            BitsPerSample = 32,
            BlockAlign = (ushort)(4 * channels),
            AvgBytesPerSec = (uint)(4 * channels * sampleRate),
            ExtraSize = 22,
        },
        ValidBitsPerSample = 32,
        ChannelMask = channelMask,
        SubFormat = SubTypeFloat,
    };

    public static uint DefaultChannelMask(int channels) => channels switch
    {
        1 => 0x4,        // front center
        2 => 0x3,        // front left | front right
        4 => 0x33,       // quad
        6 => 0x3F,       // 5.1
        8 => 0x63F,      // 7.1
        _ => 0,
    };
}

/// <summary>Decoded WAVEFORMATEX(TENSIBLE): what NavTune needs to read and write samples.</summary>
public readonly record struct AudioFormat(int SampleRate, int Channels, int BitsPerSample, int BlockAlign, bool IsFloat, uint ChannelMask)
{
    public static unsafe AudioFormat FromPointer(IntPtr pointer)
    {
        var ex = *(WaveFormatEx*)pointer;
        bool isFloat = ex.FormatTag == WaveFormatExtensible.WAVE_FORMAT_IEEE_FLOAT;
        uint mask = WaveFormatExtensible.DefaultChannelMask(ex.Channels);
        if (ex.FormatTag == WaveFormatExtensible.WAVE_FORMAT_EXTENSIBLE && ex.ExtraSize >= 22)
        {
            var ext = *(WaveFormatExtensible*)pointer;
            isFloat = ext.SubFormat == WaveFormatExtensible.SubTypeFloat;
            mask = ext.ChannelMask;
        }
        return new AudioFormat((int)ex.SamplesPerSec, ex.Channels, ex.BitsPerSample, ex.BlockAlign, isFloat, mask);
    }

    public static AudioFormat? FromBlob(byte[]? blob)
    {
        if (blob is null || blob.Length < Marshal.SizeOf<WaveFormatEx>()) return null;
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try { return FromPointer(handle.AddrOfPinnedObject()); }
        finally { handle.Free(); }
    }

    public override string ToString() =>
        $"{SampleRate} Hz, {Channels} ch, {BitsPerSample}-bit {(IsFloat ? "float" : "PCM")}";
}

[StructLayout(LayoutKind.Sequential)]
public struct AudioVolumeNotificationData
{
    public Guid EventContext;
    [MarshalAs(UnmanagedType.Bool)] public bool Muted;
    public float MasterVolume;
    public uint Channels;
    // float ChannelVolumes[Channels] follows.
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int Item(int index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out DeviceState state);
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, DeviceState newState);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPropertyStore
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetAt(int index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioClient
{
    [PreserveSig] int Initialize(AudioClientShareMode shareMode, AudioClientStreamFlags streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint bufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
    [PreserveSig] int IsFormatSupported(AudioClientShareMode shareMode, IntPtr format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint framesRequested, out IntPtr data);
    [PreserveSig] int ReleaseBuffer(uint framesWritten, AudioClientBufferFlags flags);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint framesToRead, out AudioClientBufferFlags flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint framesRead);
    [PreserveSig] int GetNextPacketSize(out uint framesInNextPacket);
}

[ComImport, Guid("f6e4c0a0-46d9-4fb8-be21-57a3ef2b626c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioClockAdjustment
{
    [PreserveSig] int SetSampleRate(float sampleRate);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
    [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
    [PreserveSig] int GetChannelCount(out uint channels);
    [PreserveSig] int SetMasterVolumeLevel(float levelDB, ref Guid eventContext);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
    [PreserveSig] int GetMasterVolumeLevel(out float levelDB);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDB, ref Guid eventContext);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDB);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
    [PreserveSig] int VolumeStepUp(ref Guid eventContext);
    [PreserveSig] int VolumeStepDown(ref Guid eventContext);
    [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
    [PreserveSig] int GetVolumeRange(out float minDB, out float maxDB, out float incrementDB);
}

[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(IntPtr notifyData);
}

[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioMeterInformation
{
    [PreserveSig] int GetPeakValue(out float peak);
    [PreserveSig] int GetMeteringChannelCount(out uint channels);
    [PreserveSig] int GetChannelsPeakValues(uint channelCount, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] float[] peaks);
    [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionManager2
{
    // IAudioSessionManager
    [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint streamFlags, out IAudioSessionControl2 sessionControl);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint streamFlags, out ISimpleAudioVolume audioVolume);
    // IAudioSessionManager2
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    [PreserveSig] int RegisterSessionNotification(IAudioSessionNotification sessionNotification);
    [PreserveSig] int UnregisterSessionNotification(IAudioSessionNotification sessionNotification);
    [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);
    [PreserveSig] int UnregisterDuckNotification(IntPtr duckNotification);
}

[ComImport, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionNotification
{
    [PreserveSig] int OnSessionCreated(IntPtr newSession);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
}

[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionControl2
{
    // IAudioSessionControl
    [PreserveSig] int GetState(out AudioSessionState state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingParam);
    [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
    // IAudioSessionControl2
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetProcessId(out uint processId);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
    [PreserveSig] int GetMasterVolume(out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

/// <summary>
/// Undocumented but long-stable interface (Windows 7 – 11) used by the Sound control panel to change
/// the default endpoint. FxSound, EarTrumpet and SoundSwitch rely on the same call.
/// </summary>
[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool getDefault, out IntPtr format);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool getDefault, out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool fxStore, IntPtr key, IntPtr value);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool fxStore, IntPtr key, IntPtr value);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool visible);
}

[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class PolicyConfigClientComObject { }

public static class Iids
{
    public static readonly Guid AudioClient = typeof(IAudioClient).GUID;
    public static readonly Guid AudioRenderClient = typeof(IAudioRenderClient).GUID;
    public static readonly Guid AudioCaptureClient = typeof(IAudioCaptureClient).GUID;
    public static readonly Guid AudioClockAdjustment = typeof(IAudioClockAdjustment).GUID;
    public static readonly Guid AudioEndpointVolume = typeof(IAudioEndpointVolume).GUID;
    public static readonly Guid AudioMeterInformation = typeof(IAudioMeterInformation).GUID;
    public static readonly Guid AudioSessionManager2 = typeof(IAudioSessionManager2).GUID;
    public static readonly Guid SimpleAudioVolume = typeof(ISimpleAudioVolume).GUID;
}

public static class Hr
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_NOTFOUND = unchecked((int)0x80070490);
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const int AUDCLNT_E_UNSUPPORTED_FORMAT = unchecked((int)0x88890008);
    public const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);
    public const int AUDCLNT_E_SERVICE_NOT_RUNNING = unchecked((int)0x88890010);
    public const int AUDCLNT_E_RESOURCES_INVALIDATED = unchecked((int)0x88890026);
    public const int AUDCLNT_S_BUFFER_EMPTY = 0x08890001;

    public static void Check(int hr, string action)
    {
        if (hr < 0) throw new AudioException(action, hr);
    }

    public static bool IsDeviceLost(int hr) =>
        hr == AUDCLNT_E_DEVICE_INVALIDATED || hr == AUDCLNT_E_RESOURCES_INVALIDATED ||
        hr == AUDCLNT_E_SERVICE_NOT_RUNNING || hr == E_NOTFOUND;
}

public sealed class AudioException(string action, int hresult)
    : Exception($"{action} (0x{hresult:X8})")
{
    public string Action { get; } = action;
    public int HResult32 { get; } = hresult;
    public bool IsDeviceLost => Hr.IsDeviceLost(HResult32);
}

internal static class NativeMethods
{
    public const uint CLSCTX_ALL = 0x17;
    public const uint STGM_READ = 0;

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PropVariant value);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr pointer);

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
