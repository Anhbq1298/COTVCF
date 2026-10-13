using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Presentation.Controls;

/// <summary>The section drawing of a frame section form (as SAP2000's "Section" box): the outline to scale on a grid, with
/// the local 2 (up) and 3 (left) axes. A General / Section Designer section is drawn as its equivalent rectangle, dashed.</summary>
public sealed class SectionSketch : FrameworkElement
{
    public static readonly DependencyProperty SectionProperty = DependencyProperty.Register(nameof(Section), typeof(FrameSectionProperty), typeof(SectionSketch),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public FrameSectionProperty? Section { get => (FrameSectionProperty?)GetValue(SectionProperty); set => SetValue(SectionProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        dc.DrawRectangle(Brushes.White, new Pen(Brushes.Gray, 1), new Rect(size));
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(0xE3, 0xE6, 0xEA)), 1);
        for (double x = 8; x < size.Width; x += 8) dc.DrawLine(grid, new Point(x, 0), new Point(x, size.Height));
        for (double y = 8; y < size.Height; y += 8) dc.DrawLine(grid, new Point(0, y), new Point(size.Width, y));
        var centre = new Point(size.Width / 2, size.Height / 2);
        if (Section is not { } section) return;

        var values = section.Values();
        double depth = section.Depth, width = section.Shape is FrameShape.Pipe or FrameShape.Circle ? section.Depth : Math.Max(section.Width, section.BottomWidth);
        var general = section.Shape == FrameShape.General;
        if (general && values.Area > 0) { depth = Math.Sqrt(12 * values.I33 / values.Area); width = Math.Sqrt(12 * values.I22 / values.Area); }
        if (!(depth > 0) || !(width > 0)) return;
        var scale = 0.72 * Math.Min(size.Width / width, size.Height / depth);
        Point P(double x, double y) => new(centre.X - x * scale, centre.Y - y * scale);   // 3-axis points left, 2-axis up
        var fill = new SolidColorBrush(Color.FromRgb(0xB8, 0xBC, 0xC2));
        var edge = new Pen(Brushes.Black, 1.2);

        switch (section.Shape)
        {
            case FrameShape.Pipe:
            case FrameShape.Circle:
            {
                var outer = new EllipseGeometry(centre, depth / 2 * scale, depth / 2 * scale);
                var inner = section.Shape == FrameShape.Pipe ? Math.Max(0, depth / 2 - section.WebThickness) : 0;
                System.Windows.Media.Geometry ring = inner > 0 ? new CombinedGeometry(GeometryCombineMode.Exclude, outer, new EllipseGeometry(centre, inner * scale, inner * scale)) : outer;
                dc.DrawGeometry(fill, edge, ring);
                break;
            }
            case FrameShape.FilledTube or FrameShape.Encased or FrameShape.MegaComposite when section.Composite is { } c:
            {
                var concrete = new SolidColorBrush(Color.FromRgb(0xD9, 0xD4, 0xC7));
                var steel = new SolidColorBrush(Color.FromRgb(0x5B, 0x6B, 0x7D));
                const double mm = 0.001;
                if (section.Shape == FrameShape.FilledTube)
                {
                    var t = c.TubeThickness * mm;
                    if (c.Tube == TubeShape.Circular)
                    {
                        dc.DrawEllipse(steel, edge, centre, depth / 2 * scale, depth / 2 * scale);
                        var inner = Math.Max(0, depth / 2 - t) * scale;
                        dc.DrawEllipse(concrete, null, centre, inner, inner);
                    }
                    else
                    {
                        dc.DrawRectangle(steel, edge, new Rect(P(width / 2, depth / 2), P(-width / 2, -depth / 2)));
                        dc.DrawRectangle(concrete, null, new Rect(P(Math.Max(0, width / 2 - t), Math.Max(0, depth / 2 - t)), P(-Math.Max(0, width / 2 - t), -Math.Max(0, depth / 2 - t))));
                    }
                }
                else
                {
                    dc.DrawRectangle(concrete, edge, new Rect(P(width / 2, depth / 2), P(-width / 2, -depth / 2)));
                    double ha = c.SteelDepth * mm, ba = c.SteelWidth * mm, tf = c.FlangeThickness * mm, tw = c.WebThickness * mm;
                    IReadOnlyList<(double Y, double Z)> centres = section.Shape == FrameShape.MegaComposite ? CompositeSection.ProfileCentres(c) : [(0, 0)];
                    foreach (var (cy, cz) in centres)
                        foreach (var (x, y, w, h) in new[] { (-ba / 2, ha / 2 - tf, ba, tf), (-ba / 2, -ha / 2, ba, tf), (-tw / 2, -ha / 2 + tf, tw, Math.Max(0, ha - 2 * tf)) })
                            dc.DrawRectangle(steel, null, new Rect(P(x + cy * mm, y + cz * mm), P(x + w + cy * mm, y + h + cz * mm)));
                    var cover = (c.Cover + c.LinkDiameter / 2) * mm;
                    dc.DrawRectangle(null, new Pen(Brushes.DarkRed, 1) { DashStyle = DashStyles.Dash },
                        new Rect(P(width / 2 - cover, depth / 2 - cover), P(-width / 2 + cover, -depth / 2 + cover)));
                    foreach (var (y, z, diameter) in CompositeSection.Parts(section.Shape, c).Bars)
                        dc.DrawEllipse(Brushes.Black, null, P(y, z), Math.Max(1.5, diameter * mm / 2 * scale), Math.Max(1.5, diameter * mm / 2 * scale));
                }
                break;
            }
            case FrameShape.General:
            {
                var dashed = new Pen(Brushes.Black, 1.2) { DashStyle = DashStyles.Dash };
                dc.DrawRectangle(fill, dashed, new Rect(P(width / 2, depth / 2), P(-width / 2, -depth / 2)));
                var text = new FormattedText($"{section.SapType} · equivalent", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.DimGray,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(text, new Point(4, size.Height - text.Height - 2));
                break;
            }
            default:
                foreach (var (x, y, w, h) in section.Plates())
                    dc.DrawRectangle(fill, null, new Rect(P(x, y), P(x + w, y + h)));
                foreach (var (x, y, w, h) in section.Plates())
                    dc.DrawRectangle(null, edge, new Rect(P(x, y), P(x + w, y + h)));
                break;
        }

        // local axes: centre lines in red, 2 up and 3 left in blue
        var red = new Pen(Brushes.Red, 1);
        dc.DrawLine(red, new Point(centre.X, 4), new Point(centre.X, size.Height - 4));
        dc.DrawLine(red, new Point(4, centre.Y), new Point(size.Width - 4, centre.Y));
        var blue = new Pen(Brushes.Blue, 1.6);
        dc.DrawLine(blue, centre, new Point(centre.X, centre.Y - 46));
        dc.DrawLine(blue, centre, new Point(centre.X - 46, centre.Y));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawText(new FormattedText("2", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.Blue, dpi), new Point(centre.X + 3, centre.Y - 58));
        dc.DrawText(new FormattedText("3", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.Blue, dpi), new Point(centre.X - 58, centre.Y - 16));
    }
}
