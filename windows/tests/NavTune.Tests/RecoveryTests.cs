using System.Text.Json;
using NavTune.Audio;
using NavTune.Model;

namespace NavTune.Tests;

public class RecoveryTests
{
    [Fact]
    public void SystemChangesRoundTripWithOriginalAppVolumes()
    {
        var changes = new SystemChanges
        {
            OwnsDefault = true,
            VirtualDeviceId = "{virtual}",
            RestoreDeviceId = "{speakers}",
            TouchedApps = ["chrome.exe", "zalo.exe"],
            OriginalApps = new Dictionary<string, AppVolume> { ["chrome.exe"] = new AppVolume(false, 0.6f) },
        };
        string json = JsonSerializer.Serialize(changes, SettingsStore.Options);
        Assert.DoesNotContain("isEmpty", json);
        var back = JsonSerializer.Deserialize<SystemChanges>(json, SettingsStore.Options)!;
        Assert.True(back.OwnsDefault);
        Assert.Equal(["chrome.exe", "zalo.exe"], back.TouchedApps);
        Assert.Equal(new AppVolume(false, 0.6f), back.OriginalApps["chrome.exe"]);
    }

    [Fact]
    public void AppsGoBackToHowTheyWereFound()
    {
        var changes = new SystemChanges { OriginalApps = new Dictionary<string, AppVolume> { ["obs64.exe"] = new AppVolume(true, 0.4f) } };
        // Restores exactly what NavTune found (the user had muted OBS themselves)...
        Assert.Equal((true, 0.4f), changes.OriginalOf("obs64.exe"));
        // ...and apps it never saw before changing them come back unmuted at full volume.
        Assert.Equal((false, 1f), changes.OriginalOf("spotify.exe"));
    }

    [Fact]
    public void NothingRecordedMeansNothingToUndo()
    {
        Assert.True(new SystemChanges().IsEmpty);
        Assert.False(new SystemChanges { OwnsDefault = true }.IsEmpty);
        Assert.False(new SystemChanges { TouchedApps = ["chrome.exe"] }.IsEmpty);
    }

    [Fact]
    public void ReleasingWithoutARecordedDeviceClearsTheRecord()
    {
        var changes = new SystemChanges { OwnsDefault = true, VirtualDeviceId = null };
        Assert.True(Recovery.ReleaseDefault(changes));
        Assert.False(changes.OwnsDefault);
    }
}
