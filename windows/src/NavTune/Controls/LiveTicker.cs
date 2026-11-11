using System.Windows;
using System.Windows.Media;

namespace NavTune.Controls;

public interface ILiveElement
{
    void Tick();
}

/// <summary>
/// One frame clock for the live meters and spectra: hooked to the render loop only while a live element is on screen
/// and audio is being processed, and throttled to about 30 frames per second. Nothing runs otherwise.
/// </summary>
public static class LiveTicker
{
    private static readonly HashSet<ILiveElement> elements = [];
    private static TimeSpan last;
    private static bool hooked;

    public static void Set(ILiveElement element, bool live)
    {
        if (live) elements.Add(element);
        else elements.Remove(element);
        if (elements.Count > 0 && !hooked)
        {
            CompositionTarget.Rendering += OnRendering;
            hooked = true;
        }
        else if (elements.Count == 0 && hooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            hooked = false;
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        var time = e is RenderingEventArgs args ? args.RenderingTime : TimeSpan.Zero;
        if (time - last < TimeSpan.FromMilliseconds(30)) return;
        last = time;
        foreach (var element in elements.ToArray()) element.Tick();
    }
}

/// <summary>Base for elements that draw live audio data while visible and processing.</summary>
public abstract class LiveElement : FrameworkElement, ILiveElement
{
    public static readonly DependencyProperty IsLiveProperty = DependencyProperty.Register(
        nameof(IsLive), typeof(bool), typeof(LiveElement), new PropertyMetadata(false, (d, _) => ((LiveElement)d).UpdateLive()));

    /// <summary>Bound to whether audio is being processed.</summary>
    public bool IsLive
    {
        get => (bool)GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }

    protected LiveElement()
    {
        IsVisibleChanged += (_, _) => UpdateLive();
        Unloaded += (_, _) => LiveTicker.Set(this, false);
        Loaded += (_, _) => UpdateLive();
    }

    private void UpdateLive()
    {
        bool live = IsLive && IsVisible && IsLoaded;
        LiveTicker.Set(this, live);
        if (!live) OnStopped();
        InvalidateVisual();
    }

    protected virtual void OnStopped() { }

    public virtual void Tick() => InvalidateVisual();
}
