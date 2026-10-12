using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Mcotis.Desktop.Presentation.Controls;

/// <summary>
/// Cable target-force convergence: P_result / P_preset after each pretension-fitting run, starting from 0 at run 0
/// (no pretension), with the target line at 1 and the ±tolerance band shaded.
/// </summary>
public sealed class ConvergenceChart : FrameworkElement
{
    public static readonly DependencyProperty RatiosProperty = DependencyProperty.Register(nameof(Ratios), typeof(IReadOnlyList<double>), typeof(ConvergenceChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TolerancePercentProperty = DependencyProperty.Register(nameof(TolerancePercent), typeof(double), typeof(ConvergenceChart),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(nameof(EmptyText), typeof(string), typeof(ConvergenceChart),
        new FrameworkPropertyMetadata("No convergence data yet.", FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Ratios { get => (IReadOnlyList<double>?)GetValue(RatiosProperty); set => SetValue(RatiosProperty, value); }
    public double TolerancePercent { get => (double)GetValue(TolerancePercentProperty); set => SetValue(TolerancePercentProperty, value); }
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    private Brush Resource(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 120 || height < 90) return;
        var background = Resource("Surface", Brushes.White);
        var text = Resource("Text", Brushes.Black);
        var muted = Resource("Muted", Brushes.Gray);
        dc.DrawRectangle(background, null, new Rect(RenderSize));

        const double left = 58, right = 18, top = 30, bottom = 40;
        var plot = new Rect(left, top, Math.Max(10, width - left - right), Math.Max(10, height - top - bottom));
        var ratios = Ratios ?? [];
        var points = new List<double> { 0 };
        points.AddRange(ratios);
        var runs = Math.Max(points.Count - 1, 1);
        var maxRatio = Math.Max(1.2, points.DefaultIfEmpty().Max() * 1.08);
        var minRatio = Math.Min(0, points.DefaultIfEmpty().Min());
        Point Map(double run, double ratio) => new(plot.Left + plot.Width * run / runs,
            plot.Bottom - plot.Height * (ratio - minRatio) / (maxRatio - minRatio));

        var axis = new Pen(text, 1.5);
        var grid = new Pen(Resource("Border", Brushes.LightGray), 1) { DashStyle = DashStyles.Dot };

        // Tolerance band around the target.
        if (TolerancePercent > 0)
        {
            var band = TolerancePercent / 100;
            var upper = Map(0, Math.Min(maxRatio, 1 + band));
            var lower = Map(runs, Math.Max(minRatio, 1 - band));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 46, 125, 50)), null, new Rect(upper, lower));
        }

        // Y grid and labels.
        for (var tick = 0.0; tick <= maxRatio + 1e-9; tick += maxRatio > 2.5 ? 0.5 : 0.2)
        {
            if (tick < minRatio) continue;
            var y = Map(0, tick).Y;
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            if (Math.Abs(tick - 1) > 1e-9) // the target line carries its own "1" label
                Label(dc, tick.ToString("0.0", CultureInfo.InvariantCulture), new Point(plot.Left - 8, y), muted, 10, alignRight: true);
        }
        // X ticks (integer runs).
        var step = Math.Max(1, (int)Math.Ceiling(runs / 12.0));
        for (var run = 0; run <= runs; run += step)
        {
            var x = Map(run, minRatio).X;
            dc.DrawLine(axis, new Point(x, plot.Bottom), new Point(x, plot.Bottom + 4));
            Label(dc, run.ToString(CultureInfo.InvariantCulture), new Point(x, plot.Bottom + 12), muted, 10, center: true);
        }

        // Target line P_result / P_preset = 1.
        var oneY = Map(0, 1).Y;
        dc.DrawLine(new Pen(text, 1.5), new Point(plot.Left - 6, oneY), new Point(plot.Right, oneY));
        Label(dc, "1", new Point(plot.Left - 8, oneY - 12), text, 12, alignRight: true);

        // Axes with arrowheads.
        dc.DrawLine(axis, new Point(plot.Left, plot.Bottom + 4), new Point(plot.Left, plot.Top - 12));
        dc.DrawLine(axis, new Point(plot.Left - 4, plot.Bottom), new Point(plot.Right + 8, plot.Bottom));
        Arrow(dc, text, new Point(plot.Left, plot.Top - 16), new Vector(0, -1));
        Arrow(dc, text, new Point(plot.Right + 12, plot.Bottom), new Vector(1, 0));
        Label(dc, "Presult / Ppreset", new Point(plot.Left + 8, plot.Top - 22), text, 12);
        Label(dc, "Number of run", new Point(plot.Right, plot.Bottom + 26), text, 12, alignRight: true);

        if (ratios.Count == 0)
        {
            Label(dc, EmptyText, new Point(plot.Left + plot.Width / 2, plot.Top + plot.Height / 2), muted, 12, center: true);
            return;
        }

        // Convergence curve.
        var red = new SolidColorBrush(Color.FromRgb(0xE5, 0x1A, 0x1A));
        var line = new Pen(red, 2.2) { LineJoin = PenLineJoin.Round };
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(Map(0, points[0]), false, false);
            for (var run = 1; run < points.Count; run++) context.LineTo(Map(run, points[run]), true, true);
        }
        dc.DrawGeometry(null, line, geometry);
        for (var run = 1; run < points.Count; run++)
            dc.DrawEllipse(red, new Pen(background, 1.5), Map(run, points[run]), 3.5, 3.5);
        var last = points[^1];
        Label(dc, $"{last:0.000}", Map(points.Count - 1, last) + new Vector(-6, last >= 1 ? -18 : 6), red, 11, alignRight: true);
    }

    private void Label(DrawingContext dc, string value, Point anchor, Brush brush, double size, bool alignRight = false, bool center = false)
    {
        var formatted = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var x = alignRight ? anchor.X - formatted.Width : center ? anchor.X - formatted.Width / 2 : anchor.X;
        dc.DrawText(formatted, new Point(x, anchor.Y - formatted.Height / 2));
    }

    private static void Arrow(DrawingContext dc, Brush brush, Point tip, Vector direction)
    {
        var normal = new Vector(-direction.Y, direction.X);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(tip, true, true);
            context.LineTo(tip - direction * 12 + normal * 6, true, false);
            context.LineTo(tip - direction * 12 - normal * 6, true, false);
        }
        dc.DrawGeometry(brush, null, geometry);
    }
}
