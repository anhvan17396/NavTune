using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using NavTune.Infrastructure;
using NavTune.ViewModels;

namespace NavTune.Controls;

/// <summary>EQ band slider: gradient fill from the 0 dB line to the knob, 0.5 dB steps, double-click resets to 0.</summary>
public sealed class VerticalSlider : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(VerticalSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(VerticalSlider), new FrameworkPropertyMetadata(-12.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(VerticalSlider), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    private const double Step = 0.5;
    private const double Knob = 18;
    private bool dragging;

    public VerticalSlider()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        FocusVisualStyle = null;
        MinWidth = 24;
    }

    private double Usable => Math.Max(ActualHeight - Knob, 1);
    private double YFor(double value) => Knob / 2 + Usable * (1 - (value - Minimum) / (Maximum - Minimum));

    private void SetFromY(double y)
    {
        double fraction = 1 - Math.Clamp((y - Knob / 2) / Usable, 0, 1);
        double raw = Minimum + fraction * (Maximum - Minimum);
        double snapped = Math.Round(raw / Step, MidpointRounding.AwayFromZero) * Step;
        if (snapped != Value) Value = snapped;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (h < Knob) return;
        double cx = Math.Round(w / 2);
        double mid = YFor(Math.Clamp(0, Minimum, Maximum));
        double y = YFor(Math.Clamp(Value, Minimum, Maximum));
        var track = (Brush)(TryFindResource("ControlStrongFillColorDisabledBrush") ?? Brushes.Gray);
        var line = (Brush)(TryFindResource("TextFillColorTertiaryBrush") ?? Brushes.Gray);

        dc.DrawRoundedRectangle(track, null, new Rect(cx - 3, Knob / 2, 6, Usable), 3, 3);
        dc.DrawRectangle(line, null, new Rect(cx - 8, Math.Round(mid), 16, 1));
        double top = Math.Min(y, mid), height = Math.Abs(y - mid);
        if (height > 0.5) dc.DrawRoundedRectangle(Brand.Gradient, null, new Rect(cx - 3, top, 6, height), 3, 3);

        double radius = dragging ? Knob / 2 * 1.12 : Knob / 2;
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)), null, new Point(cx, y + 1.2), radius + 0.6, radius + 0.6);
        var ring = new Pen(new SolidColorBrush(Color.FromArgb(dragging || IsKeyboardFocused ? (byte)230 : (byte)70, 97, 92, 250)), 2);
        dc.DrawEllipse(Brushes.White, ring, new Point(cx, y), radius - 1, radius - 1);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (e.ClickCount == 2)
        {
            Value = 0;
            e.Handled = true;
            return;
        }
        dragging = true;
        CaptureMouse();
        SetFromY(e.GetPosition(this).Y);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging && IsMouseCaptured) SetFromY(e.GetPosition(this).Y);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!dragging) return;
        dragging = false;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        dragging = false;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Value = Math.Clamp(Value + Math.Sign(e.Delta) * Step, Minimum, Maximum);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double? next = e.Key switch
        {
            Key.Up or Key.Right => Value + Step,
            Key.Down or Key.Left => Value - Step,
            Key.PageUp => Value + 3,
            Key.PageDown => Value - 3,
            Key.Home => Maximum,
            Key.End => Minimum,
            Key.D0 or Key.NumPad0 or Key.Delete => 0,
            _ => null,
        };
        if (next is null) return;
        Value = Math.Clamp(next.Value, Minimum, Maximum);
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }
}

/// <summary>Output level bar, animated only while audio is processed and the bar is visible.</summary>
public sealed class LevelMeter : LiveElement
{
    private static readonly Brush Red = Brand.Freeze(new SolidColorBrush(Color.FromRgb(229, 72, 77)));

    public LevelMeter()
    {
        SetBinding(IsLiveProperty, new Binding(nameof(AppState.IsProcessing)));
        ToolTip = new ToolTipText();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 1 || h < 1) return;
        var track = (Brush)(TryFindResource("ControlStrongFillColorDisabledBrush") ?? Brushes.Gray);
        dc.DrawRoundedRectangle(track, null, new Rect(0, 0, w, h), h / 2, h / 2);
        float level = IsLive && DataContext is AppState state ? Math.Min(state.OutputLevel, 1) : 0;
        if (level <= 0.002f) return;
        dc.DrawRoundedRectangle(level > 0.95f ? Red : Brand.HorizontalGradient, null, new Rect(0, 0, Math.Max(w * level, h), h), h / 2, h / 2);
    }

    private sealed class ToolTipText : System.Windows.Controls.TextBlock
    {
        public ToolTipText() => SetBinding(TextProperty, new Binding(nameof(LocalizationSource.IsVietnamese))
        {
            Source = LocalizationSource.Instance,
            Converter = new Pick(),
        });

        private sealed class Pick : IValueConverter
        {
            public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? "Mức tín hiệu" : "Signal level";
            public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
        }
    }
}

/// <summary>Small line drawing of a preset's EQ curve.</summary>
public sealed class PresetCurve : FrameworkElement
{
    private static readonly Brush Stroke = Brand.Freeze(new LinearGradientBrush(
        [new GradientStop(Color.FromRgb(50, 200, 250), 0), new GradientStop(Brand.Accent, 0.5), new GradientStop(Brand.Purple, 1)],
        new Point(0, 0.5), new Point(1, 0.5)));

    public static readonly DependencyProperty GainsProperty = DependencyProperty.Register(
        nameof(Gains), typeof(IReadOnlyList<float>), typeof(PresetCurve), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<float>? Gains { get => (IReadOnlyList<float>?)GetValue(GainsProperty); set => SetValue(GainsProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var gains = Gains;
        double w = ActualWidth, h = ActualHeight;
        if (gains is not { Count: > 1 } || w < 2) return;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (int i = 0; i < gains.Count; i++)
            {
                var point = new Point(w * i / (gains.Count - 1), h / 2 - gains[i] / 12.0 * h / 2);
                if (i == 0) g.BeginFigure(point, false, false);
                else g.LineTo(point, true, true);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(Stroke, 1.8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
    }
}

/// <summary>Gradient rounded square holding a glyph, used as section and row icons.</summary>
public sealed class IconBadge : FrameworkElement
{
    private static readonly Typeface GlyphFace = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(IconBadge), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(IconBadge), new FrameworkPropertyMetadata(26.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public IconBadge() => VerticalAlignment = VerticalAlignment.Center;

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        double s = Size;
        dc.DrawRoundedRectangle(Brand.Gradient, null, new Rect(0, 0, s, s), s * 0.28, s * 0.28);
        if (string.IsNullOrEmpty(Glyph)) return;
        var text = new FormattedText(Glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, GlyphFace, s * 0.5,
            Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point((s - text.Width) / 2, (s - text.Height) / 2));
    }
}
