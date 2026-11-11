using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NavTune.Audio;
using NavTune.Infrastructure;
using NavTune.Model;
using NavTune.ViewModels;

namespace NavTune.Controls;

/// <summary>
/// Live spectrum of the output with the EQ response curve on top; in parametric mode the numbered band points can be
/// dragged. The grid and the curve are cached and rebuilt only when the size or the settings change; only the
/// spectrum is redrawn per frame, and only while audio is being processed and the graph is visible.
/// </summary>
public sealed class FrequencyGraph : LiveElement
{
    private const double MinFrequency = 20, MaxFrequency = 20000, RangeDB = 15;
    private static readonly Brush SpectrumFill = Brand.Freeze(new LinearGradientBrush(
        Color.FromArgb(128, 50, 200, 250), Color.FromArgb(31, 175, 82, 222), 90));
    private static readonly Brush CurveFill = Brand.Freeze(new SolidColorBrush(Color.FromArgb(46, 97, 92, 250)));
    private static readonly Pen CurveGlow = Brand.Freeze(new Pen(new SolidColorBrush(Color.FromArgb(115, 97, 92, 250)), 6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen CurvePen = Brand.Freeze(new Pen(Brushes.White, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen GridPen = Brand.Freeze(new Pen(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), 1));
    private static readonly Pen ZeroPen = Brand.Freeze(new Pen(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), 1));
    private static readonly Brush LabelBrush = Brand.Freeze(new SolidColorBrush(Color.FromArgb(115, 255, 255, 255)));
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface NodeFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Dictionary<double, ResponseTable> tables = [];

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(AppState), typeof(FrequencyGraph), new PropertyMetadata(null, OnStateChanged));
    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid), typeof(bool), typeof(FrequencyGraph), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowNodesProperty = DependencyProperty.Register(
        nameof(ShowNodes), typeof(bool), typeof(FrequencyGraph), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(FrequencyGraph), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public AppState? State { get => (AppState?)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
    public bool ShowNodes { get => (bool)GetValue(ShowNodesProperty); set => SetValue(ShowNodesProperty, value); }
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    private Geometry? curve, curveArea;
    private Size curveSize;
    private Guid? dragging;
    private float[] lastBands = [];

    public FrequencyGraph()
    {
        ClipToBounds = true;
        SetBinding(IsLiveProperty, new System.Windows.Data.Binding(nameof(AppState.IsProcessing)));
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var graph = (FrequencyGraph)d;
        if (e.OldValue is AppState old) old.SoundChanged -= graph.OnSoundChanged;
        if (e.NewValue is AppState state)
        {
            state.SoundChanged += graph.OnSoundChanged;
            graph.SetBinding(IsLiveProperty, new System.Windows.Data.Binding(nameof(AppState.IsProcessing)) { Source = state });
        }
        graph.OnSoundChanged();
    }

    private void OnSoundChanged()
    {
        curve = null;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        curve = null;
    }

    protected override void OnStopped() => lastBands = [];

    // MARK: - Scale

    private double X(double frequency) => ActualWidth * Math.Log10(frequency / MinFrequency) / Math.Log10(MaxFrequency / MinFrequency);
    private double Frequency(double x) => MinFrequency * Math.Pow(10, x / Math.Max(ActualWidth, 1) * Math.Log10(MaxFrequency / MinFrequency));
    private double Y(double db) => ActualHeight / 2 - db / RangeDB * ActualHeight / 2 * 0.9;
    private double DB(double y) => (ActualHeight / 2 - y) / (ActualHeight / 2 * 0.9) * RangeDB;

    // MARK: - Drawing

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2) return;
        var bounds = new Rect(0, 0, w, h);
        double r = CornerRadius;
        dc.PushClip(new RectangleGeometry(bounds, r, r));
        dc.DrawRectangle(Brand.GraphBackground, null, bounds);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (ShowGrid) DrawGrid(dc, w, h, dip);

        var state = State;
        if (state is not null && IsLive) DrawSpectrum(dc, state.SpectrumBands(Stopwatch.GetTimestamp()), w, h);
        if (state is not null)
        {
            if (curve is null || curveSize != RenderSize) BuildCurve(state.Sound, state.SampleRate);
            dc.DrawGeometry(CurveFill, null, curveArea);
            dc.DrawGeometry(null, CurveGlow, curve);
            dc.DrawGeometry(null, CurvePen, curve);
            if (ShowNodes && state.IsParametric) DrawNodes(dc, state, dip);
        }
        dc.Pop();
    }

    private void DrawGrid(DrawingContext dc, double w, double h, double dip)
    {
        foreach (double f in new double[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 })
        {
            double x = Math.Round(X(f)) + 0.5;
            dc.DrawLine(GridPen, new Point(x, 0), new Point(x, h));
            var label = Text(Format.Frequency(f), 9.5, LabelBrush, LabelFace, dip);
            dc.DrawText(label, new Point(x - label.Width / 2, h - label.Height - 3));
        }
        foreach (double db in new double[] { -12, -6, 0, 6, 12 })
        {
            double y = Math.Round(Y(db)) + 0.5;
            dc.DrawLine(db == 0 ? ZeroPen : GridPen, new Point(0, y), new Point(w, y));
            var label = Text(db == 0 ? "0" : db.ToString("+0;-0", CultureInfo.InvariantCulture), 9.5, LabelBrush, LabelFace, dip);
            dc.DrawText(label, new Point(8, y - label.Height - 1));
        }
    }

    private void DrawSpectrum(DrawingContext dc, float[] bands, double w, double h)
    {
        if (bands.Length < 2) return;
        var points = new Point[bands.Length];
        for (int i = 0; i < bands.Length; i++)
            points[i] = new Point(w * (i + 0.5) / bands.Length, h - h * bands[i] * 0.92);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(0, h), isFilled: true, isClosed: true);
            g.LineTo(points[0], false, false);
            for (int i = 0; i + 1 < points.Length; i++)
            {
                var mid = new Point((points[i].X + points[i + 1].X) / 2, (points[i].Y + points[i + 1].Y) / 2);
                g.QuadraticBezierTo(points[i], mid, false, true);
            }
            g.LineTo(points[^1], false, false);
            g.LineTo(new Point(w, h), false, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(SpectrumFill, null, geometry);
    }

    private void BuildCurve(SoundSettings settings, double sampleRate)
    {
        if (sampleRate <= 0) sampleRate = 48000;
        if (!tables.TryGetValue(sampleRate, out var table))
            tables[sampleRate] = table = new ResponseTable(sampleRate, 160, MinFrequency, MaxFrequency);
        var response = table.Decibels(AudioProcessor.Filters(settings, sampleRate), settings.Preamp);
        var points = response.Select((db, i) => new Point(X(table.Frequencies[i]), Y(Math.Clamp(db, -RangeDB, RangeDB)))).ToArray();

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(points[0], false, false);
            g.PolyLineTo(points.Skip(1).ToArray(), true, true);
        }
        line.Freeze();
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            double zero = Y(0);
            g.BeginFigure(new Point(points[0].X, zero), true, true);
            g.PolyLineTo(points, false, false);
            g.LineTo(new Point(points[^1].X, zero), false, false);
        }
        area.Freeze();
        curve = line;
        curveArea = area;
        curveSize = RenderSize;
    }

    private Point NodePosition(EQBand band) => new(X(band.Frequency), Y(band.Kind.HasGain() ? band.Gain : 0));

    private void DrawNodes(DrawingContext dc, AppState state, double dip)
    {
        var bands = state.Sound.Bands;
        Guid? selected = state.SelectedBand?.Id;
        for (int i = 0; i < bands.Count; i++)
        {
            var band = bands[i];
            var center = NodePosition(band);
            bool isSelected = band.Id == selected;
            dc.PushOpacity(band.Enabled ? 1 : 0.45);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(isSelected ? (byte)120 : (byte)60, 97, 92, 250)), null, center, 17, 17);
            dc.DrawEllipse(isSelected ? Brand.Gradient : Brushes.White, isSelected ? new Pen(Brushes.White, 2) : null, center, 12, 12);
            var number = Text((i + 1).ToString(CultureInfo.InvariantCulture), 11, isSelected ? Brushes.White : Brand.AccentBrush, NodeFace, dip);
            dc.DrawText(number, new Point(center.X - number.Width / 2, center.Y - number.Height / 2));
            dc.Pop();
        }
    }

    private static FormattedText Text(string text, double size, Brush brush, Typeface face, double dip) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, dip);

    // MARK: - Dragging parametric bands

    private EQBand? HitNode(Point point)
    {
        var state = State;
        if (!ShowNodes || state is null || !state.IsParametric) return null;
        return state.Sound.Bands
            .Select(b => (Band: b, Distance: (NodePosition(b) - point).Length))
            .Where(x => x.Distance < 16)
            .OrderBy(x => x.Distance)
            .Select(x => x.Band)
            .FirstOrDefault();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (HitNode(e.GetPosition(this)) is not { } band || State is not { } state) return;
        state.SelectedBand = state.ParametricBands.FirstOrDefault(b => b.Id == band.Id);
        dragging = band.Id;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (dragging is { } id && State is { } state && IsMouseCaptured)
        {
            double frequency = Math.Round(Math.Clamp(Frequency(point.X), MinFrequency, MaxFrequency));
            float gain = Format.Snap(Math.Clamp(DB(point.Y), -12, 12), 0.5, -12, 12);
            state.UpdateBand(id, b =>
            {
                b.Frequency = frequency;
                if (b.Kind.HasGain()) b.Gain = gain;
            });
            return;
        }
        Cursor = HitNode(point) is not null ? Cursors.Hand : null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (dragging is null) return;
        dragging = null;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        dragging = null;
    }
}
