using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Mcotis.Desktop.Application.Design;

namespace Mcotis.Desktop.Presentation.Controls;

/// <summary>
/// An N–M interaction diagram: M [kNm] across, N [kN] up (compression +), one curve per <see cref="InteractionSeries"/>
/// (the in-plane axis solid blue, a dashed comparison curve grey, the minor axis orange), with a labelled grid and legend.
/// </summary>
public sealed class InteractionChart : FrameworkElement
{
    public static readonly DependencyProperty DiagramProperty = DependencyProperty.Register(nameof(Diagram), typeof(InteractionDiagram), typeof(InteractionChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public InteractionDiagram? Diagram { get => (InteractionDiagram?)GetValue(DiagramProperty); set => SetValue(DiagramProperty, value); }

    private static readonly Brush Major = new SolidColorBrush(Color.FromRgb(0x1F, 0x5F, 0xBF));
    private static readonly Brush Compare = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
    private static readonly Brush MinorBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x10));
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromRgb(0xE3, 0xE6, 0xEA)), 1);
    private static readonly Pen AxisPen = new(Brushes.DimGray, 1);

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        dc.DrawRectangle(Brushes.White, new Pen(Brushes.Gray, 1), new Rect(size));
        if (Diagram is not { Series.Count: > 0 } diagram) return;
        var points = diagram.Series.SelectMany(series => series.Points).ToArray();
        if (points.Length == 0) return;
        double nMin = Math.Min(0, points.Min(point => point.NKN)), nMax = Math.Max(1, points.Max(point => point.NKN)), mMax = Math.Max(1, points.Max(point => point.MKNm));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Text(string text, Brush brush, double sizePt = 10) =>
            new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), sizePt, brush, dpi);

        var plot = new Rect(52, 24, Math.Max(10, size.Width - 64), Math.Max(10, size.Height - 60));
        Point P(double m, double n) => new(plot.Left + m / mMax * plot.Width, plot.Bottom - (n - nMin) / (nMax - nMin) * plot.Height);

        // grid with round steps
        static double Step(double range) { var raw = range / 5; var power = Math.Pow(10, Math.Floor(Math.Log10(raw))); return new[] { 1, 2, 2.5, 5, 10 }.Select(f => f * power).First(step => step >= raw); }
        var nStep = Step(nMax - nMin);
        for (var n = Math.Ceiling(nMin / nStep) * nStep; n <= nMax + 1e-9; n += nStep)
        {
            dc.DrawLine(Math.Abs(n) < 1e-9 ? AxisPen : GridPen, P(0, n), P(mMax, n));
            var label = Text(Format(n), Brushes.DimGray, 9);
            dc.DrawText(label, new Point(plot.Left - label.Width - 4, P(0, n).Y - label.Height / 2));
        }
        var mStep = Step(mMax);
        for (var m = 0.0; m <= mMax + 1e-9; m += mStep)
        {
            dc.DrawLine(m == 0 ? AxisPen : GridPen, P(m, nMin), P(m, nMax));
            var label = Text(Format(m), Brushes.DimGray, 9);
            dc.DrawText(label, new Point(P(m, 0).X - label.Width / 2, plot.Bottom + 2));
        }
        var xTitle = Text("M  [kNm]", Brushes.Black);
        dc.DrawText(xTitle, new Point(plot.Right - xTitle.Width, plot.Bottom + 15));
        dc.DrawText(Text("N  [kN]  (compression +)", Brushes.Black), new Point(4, 6));

        // curves, with the legend boxed in the plot's top-right corner (the curves are narrow near Npl,Rd)
        var labels = diagram.Series.Select(series => Text(series.Name, Brushes.Black, 9)).ToArray();
        var legendWidth = labels.Max(label => label.Width) + 28;
        var legendX = plot.Right - legendWidth - 4;
        var legendY = plot.Top + 4;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)), new Pen(Brushes.LightGray, 1),
            new Rect(legendX - 4, legendY - 2, legendWidth + 4, labels.Length * 13 + 4));
        var entry = 0;
        foreach (var series in diagram.Series)
        {
            var brush = series.Minor ? MinorBrush : series.Dashed ? Compare : Major;
            var pen = new Pen(brush, series.Dashed ? 1.2 : 2) { DashStyle = series.Dashed ? DashStyles.Dash : DashStyles.Solid, LineJoin = PenLineJoin.Round };
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                var ordered = series.Points.OrderBy(point => point.NKN).ToArray();
                context.BeginFigure(P(ordered[0].MKNm, ordered[0].NKN), false, false);
                foreach (var point in ordered.Skip(1)) context.LineTo(P(point.MKNm, point.NKN), true, true);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
            var y = legendY + entry * 13;
            dc.DrawLine(pen, new Point(legendX, y + 7), new Point(legendX + 18, y + 7));
            dc.DrawText(labels[entry++], new Point(legendX + 22, y));
        }
    }

    private static string Format(double value) =>
        Math.Abs(value) >= 10_000 ? (value / 1000).ToString("0.#", CultureInfo.CurrentCulture) + "k" : value.ToString("0", CultureInfo.CurrentCulture);
}
