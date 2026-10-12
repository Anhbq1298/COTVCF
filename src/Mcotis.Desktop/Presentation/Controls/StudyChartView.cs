using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Mcotis.Desktop.Application;

namespace Mcotis.Desktop.Presentation.Controls;

/// <summary>
/// One figure of the optimization study (<see cref="StudyChart"/>): a line chart (mean curve with its min … max band,
/// markers), grouped bars, or a scatter, with round-step gridlines, axis titles, an optional limit line and a legend.
/// </summary>
public sealed class StudyChartView : FrameworkElement
{
    public static readonly DependencyProperty ChartProperty = DependencyProperty.Register(nameof(Chart), typeof(StudyChart), typeof(StudyChartView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public StudyChart? Chart { get => (StudyChart?)GetValue(ChartProperty); set => SetValue(ChartProperty, value); }

    // categorical palette: blue, orange, green, purple, teal, red
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x1F, 0x5F, 0xBF), Color.FromRgb(0xE0, 0x7A, 0x10), Color.FromRgb(0x2E, 0x7D, 0x32),
        Color.FromRgb(0x7B, 0x3F, 0xA0), Color.FromRgb(0x00, 0x89, 0x7B), Color.FromRgb(0xC6, 0x28, 0x28),
    ];
    private static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE3, 0xE6, 0xEA)), 1));
    private static readonly Pen AxisPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA8)), 1));
    private static readonly Pen LimitPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)), 1.2) { DashStyle = DashStyles.Dash });
    private static T Frozen<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }
    private static Brush Solid(Color colour, byte alpha = 255) => Frozen(new SolidColorBrush(Color.FromArgb(alpha, colour.R, colour.G, colour.B)));

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 520 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 260 : availableSize.Height);

    /// <summary>Round tick step for a range: 1, 2, 2.5 or 5 × 10ⁿ, about five ticks.</summary>
    private static double Step(double range)
    {
        if (!(range > 0)) return 1;
        var raw = range / 5;
        var power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        return new[] { 1, 2, 2.5, 5, 10 }.Select(factor => factor * power).First(step => step >= raw * 0.999);
    }

    private static string Tick(double value, double step)
    {
        var magnitude = Math.Abs(value);
        if (magnitude >= 1e6) return (value / 1e6).ToString(step >= 1e6 ? "0.#" : "0.##", CultureInfo.CurrentCulture) + "M";
        if (magnitude >= 1e4) return (value / 1e3).ToString(step >= 1e3 ? "0" : "0.#", CultureInfo.CurrentCulture) + "k";
        var decimals = step >= 1 ? 0 : Math.Min(4, (int)Math.Ceiling(-Math.Log10(step)));
        return value.ToString("F" + decimals, CultureInfo.CurrentCulture);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        dc.DrawRectangle(Brushes.White, null, new Rect(size));
        if (Chart is not { } chart) return;
        var points = chart.Series.SelectMany(series => series.Points).ToArray();
        if (points.Length == 0) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Text(string text, Brush brush, double sizePt = 10, FontWeight? weight = null) =>
            new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal), sizePt, brush, dpi);
        var muted = Solid(Color.FromRgb(0x5F, 0x66, 0x6E));

        // extents (bands, limits and bars from zero included)
        var ys = points.Select(point => point.Y)
            .Concat(chart.Series.SelectMany(series => (series.Low ?? []).Concat(series.High ?? [])))
            .Concat(chart.LimitY is { } limitY ? [limitY] : []).Where(double.IsFinite).ToArray();
        double yMin = ys.Min(), yMax = ys.Max();
        if (chart.Kind == StudyChartKind.Bars) { yMin = Math.Min(0, yMin); yMax = Math.Max(0, yMax); }
        if (yMax - yMin < 1e-12) { var pad = Math.Max(1, Math.Abs(yMax) * 0.05); yMin -= pad; yMax += pad; }
        var yStep = Step(yMax - yMin);
        // round outwards to whole steps; a value already on a step (0 for bars) stays the edge
        yMin = Math.Floor(yMin / yStep + 1e-9) * yStep;
        yMax = Math.Ceiling(yMax / yStep - 1e-9) * yStep;

        var categorical = chart.Categories is { Count: > 0 };
        var xs = points.Select(point => point.X).Concat(chart.LimitX is { } limitX ? [limitX] : []).ToArray();
        double xMin, xMax, xStep;
        if (categorical) { xMin = -0.5; xMax = chart.Categories!.Count - 0.5; xStep = 1; }
        else
        {
            (xMin, xMax) = (xs.Min(), xs.Max());
            if (xMax - xMin < 1e-12) { xMin -= 1; xMax += 1; }
            xStep = Step(xMax - xMin);
            var pad = (xMax - xMin) * 0.04;
            (xMin, xMax) = (xMin - pad, xMax + pad);
        }

        // legend above the plot when there is more than one series
        var legend = chart.Series.Count > 1 ? chart.Series.Select(series => Text(series.Name, Brushes.Black, 9)).ToArray() : [];
        var legendHeight = legend.Length > 0 ? 16 : 0;
        var yLabelWidth = new[] { yMin, yMax }.Max(value => Text(Tick(value, yStep), muted, 9).Width);
        var plot = new Rect(yLabelWidth + 26, 8 + legendHeight, Math.Max(10, size.Width - yLabelWidth - 38), Math.Max(10, size.Height - 48 - legendHeight));
        Point P(double x, double y) => new(plot.Left + (x - xMin) / (xMax - xMin) * plot.Width, plot.Bottom - (y - yMin) / (yMax - yMin) * plot.Height);

        // grid and ticks
        for (var y = yMin; y <= yMax + yStep * 1e-6; y += yStep)
        {
            dc.DrawLine(Math.Abs(y) < yStep * 1e-6 ? AxisPen : GridPen, new Point(plot.Left, P(0, y).Y), new Point(plot.Right, P(0, y).Y));
            var label = Text(Tick(y, yStep), muted, 9);
            dc.DrawText(label, new Point(plot.Left - label.Width - 4, P(0, y).Y - label.Height / 2));
        }
        if (categorical)
            for (var i = 0; i < chart.Categories!.Count; i++)
            {
                var label = Text(chart.Categories[i], muted, 9);
                label.MaxTextWidth = Math.Max(8, plot.Width / chart.Categories.Count - 4);
                label.MaxLineCount = 1; label.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(label, new Point(P(i, yMin).X - label.Width / 2, plot.Bottom + 3));
            }
        else
            for (var x = Math.Ceiling(xMin / xStep) * xStep; x <= xMax + xStep * 1e-6; x += xStep)
            {
                dc.DrawLine(GridPen, new Point(P(x, 0).X, plot.Top), new Point(P(x, 0).X, plot.Bottom));
                var label = Text(Tick(x, xStep), muted, 9);
                dc.DrawText(label, new Point(P(x, 0).X - label.Width / 2, plot.Bottom + 3));
            }
        dc.DrawRectangle(null, AxisPen, plot);

        // axis titles
        var xTitle = Text(chart.XTitle, Brushes.Black, 10);
        xTitle.MaxTextWidth = plot.Width; xTitle.MaxLineCount = 1; xTitle.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(xTitle, new Point(plot.Left + (plot.Width - xTitle.Width) / 2, plot.Bottom + 18));
        var yTitle = Text(chart.YTitle, Brushes.Black, 10);
        yTitle.MaxTextWidth = plot.Height; yTitle.MaxLineCount = 1; yTitle.Trimming = TextTrimming.CharacterEllipsis;
        dc.PushTransform(new RotateTransform(-90, 0, 0));
        dc.DrawText(yTitle, new Point(-(plot.Top + (plot.Height + yTitle.Width) / 2), 2));
        dc.Pop();

        dc.PushClip(new RectangleGeometry(new Rect(plot.Left - 4, plot.Top - 4, plot.Width + 8, plot.Height + 8)));
        if (chart.Kind == StudyChartKind.Bars)
        {
            var groups = chart.Series.Count;
            var groupWidth = plot.Width / (xMax - xMin) * 0.7;
            for (var s = 0; s < groups; s++)
            {
                var brush = Solid(Palette[chart.Series[s].Colour % Palette.Length]);
                foreach (var (x, y) in chart.Series[s].Points)
                {
                    var left = P(x, 0).X - groupWidth / 2 + s * groupWidth / groups;
                    var (top, bottom) = (Math.Min(P(x, y).Y, P(x, 0).Y), Math.Max(P(x, y).Y, P(x, 0).Y));
                    dc.DrawRectangle(brush, null, new Rect(left + 1, top, Math.Max(1, groupWidth / groups - 2), Math.Max(0.5, bottom - top)));
                }
            }
        }
        else
            foreach (var series in chart.Series)
            {
                var colour = Palette[series.Colour % Palette.Length];
                var ordered = series.Points.Select((point, index) => (point.X, point.Y, Index: index)).ToArray();
                if (series.Low is { } low && series.High is { } high && ordered.Length >= 2)
                {
                    var band = new StreamGeometry();
                    using (var context = band.Open())
                    {
                        context.BeginFigure(P(ordered[0].X, high[0]), true, true);
                        foreach (var point in ordered.Skip(1)) context.LineTo(P(point.X, high[point.Index]), false, false);
                        foreach (var point in Enumerable.Reverse(ordered)) context.LineTo(P(point.X, low[point.Index]), false, false);
                    }
                    band.Freeze();
                    dc.DrawGeometry(Solid(colour, 40), null, band);
                }
                var pen = new Pen(Solid(colour), 2) { LineJoin = PenLineJoin.Round };
                if (series.Line)
                    for (var i = 1; i < ordered.Length; i++) dc.DrawLine(pen, P(ordered[i - 1].X, ordered[i - 1].Y), P(ordered[i].X, ordered[i].Y));
                if (series.Markers)
                    foreach (var point in ordered)
                        dc.DrawEllipse(series.Line ? Brushes.White : Solid(colour, 200), new Pen(Solid(colour), 1.5), P(point.X, point.Y), 3.2, 3.2);
            }
        if (chart.LimitY is { } limit)
        {
            dc.DrawLine(LimitPen, P(xMin, limit), P(xMax, limit));
            var label = Text(chart.LimitLabel ?? "limit", Solid(Palette[5]), 9);
            dc.DrawText(label, new Point(plot.Right - label.Width - 4, P(0, limit).Y - label.Height - 1));
        }
        if (chart.LimitX is { } limitAtX)
        {
            dc.DrawLine(LimitPen, P(limitAtX, yMin), P(limitAtX, yMax));
            var label = Text(chart.LimitLabel ?? "limit", Solid(Palette[5]), 9);
            var at = P(limitAtX, 0).X;
            dc.DrawText(label, new Point(at + 3 + label.Width > plot.Right ? at - label.Width - 3 : at + 3, plot.Top + 2));
        }
        dc.Pop();

        // legend
        var lx = plot.Left;
        for (var s = 0; s < legend.Length; s++)
        {
            var colour = Solid(Palette[chart.Series[s].Colour % Palette.Length]);
            dc.DrawRectangle(colour, null, new Rect(lx, 6, 10, 10));
            dc.DrawText(legend[s], new Point(lx + 14, 4));
            lx += legend[s].Width + 28;
            if (lx > plot.Right - 40) break;
        }
    }
}
