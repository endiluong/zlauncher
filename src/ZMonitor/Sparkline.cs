using System.Windows;
using System.Windows.Media;

namespace ZMonitor;

/// <summary>Minimal live line graph with a soft gradient fill. Push one sample per tick.</summary>
sealed class Sparkline : FrameworkElement
{
    readonly double[] _values;
    int _count, _head;

    public Sparkline() : this(60) { }
    /// <summary>XAML-settable capacity; use for series filled with <see cref="SetValues"/>.</summary>
    public int Capacity { init => _values = new double[value]; }
    public Sparkline(int capacity) { _values = new double[capacity]; IsHitTestVisible = false; }

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Color), typeof(Sparkline),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public Color Stroke { get => (Color)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    /// <summary>Lower bound of the y-axis; the upper bound follows the largest value seen (at least <see cref="MinimumMax"/>).</summary>
    public double Min { get; set; }
    public double MinimumMax { get; set; } = 1;

    public void Push(double v)
    {
        _values[_head] = v;
        _head = (_head + 1) % _values.Length;
        _count = Math.Min(_count + 1, _values.Length);
        InvalidateVisual();
    }

    /// <summary>Replace the whole series (newest last); keeps at most the capacity's worth of points.</summary>
    public void SetValues(IReadOnlyList<double> values)
    {
        int n = Math.Min(values.Count, _values.Length);
        for (int i = 0; i < n; i++) _values[i] = values[values.Count - n + i];
        _count = n;
        _head = n % _values.Length;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), 1);
        for (int i = 1; i < 3; i++) dc.DrawLine(gridPen, new Point(0, Math.Round(h * i / 3) + .5), new Point(w, Math.Round(h * i / 3) + .5));
        if (_count < 2) return;

        double seen = 0;
        for (int i = 0; i < _count; i++) seen = Math.Max(seen, _values[i]);
        double max = Math.Max(MinimumMax, seen * 1.08), range = Math.Max(1e-6, max - Min);
        double step = w / (_values.Length - 1);
        int start = (_head - _count + _values.Length) % _values.Length;
        double x0 = w - (_count - 1) * step;

        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var lc = line.Open())
        using (var fc = fill.Open())
        {
            for (int i = 0; i < _count; i++)
            {
                double v = _values[(start + i) % _values.Length];
                var p = new Point(x0 + i * step, h - Math.Clamp((v - Min) / range, 0, 1) * (h - 2) - 1);
                if (i == 0) { lc.BeginFigure(p, false, false); fc.BeginFigure(new Point(p.X, h), true, true); fc.LineTo(p, false, false); }
                else { lc.LineTo(p, true, true); fc.LineTo(p, false, false); }
            }
            fc.LineTo(new Point(w, h), false, false);
        }
        line.Freeze(); fill.Freeze();

        var c = Stroke;
        var grad = new LinearGradientBrush(Color.FromArgb(90, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
        dc.DrawGeometry(grad, null, fill);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(c), 1.6) { LineJoin = PenLineJoin.Round }, line);
    }
}
