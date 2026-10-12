using System.Windows;
using System.Windows.Media;
using Mcotis.Desktop.Application.Quantities;

namespace Mcotis.Desktop.Presentation.Controls;

/// <summary>
/// Draws the cross-section of an assigned section to scale from its stored outlines: back to front, a tube with its
/// opening, concrete and steel in the theme's section colours. It fits the figure into whatever size it is given
/// and keeps the aspect ratio. Drawing only — it holds no numbers.
/// </summary>
public sealed class SectionFigureView : FrameworkElement
{
    public static readonly DependencyProperty FigureProperty = DependencyProperty.Register(
        nameof(Figure), typeof(QuantitySectionFigure), typeof(SectionFigureView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public QuantitySectionFigure? Figure
    {
        get => (QuantitySectionFigure?)GetValue(FigureProperty);
        set => SetValue(FigureProperty, value);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        const double Padding = 1.5;
        if (Figure is not { Shapes.Count: > 0, Width: > 0, Height: > 0 } figure) return;
        double width = ActualWidth - 2 * Padding, height = ActualHeight - 2 * Padding;
        if (width <= 0 || height <= 0) return;
        var scale = Math.Min(width / figure.Width, height / figure.Height);
        double centreX = (figure.MinX + figure.MaxX) / 2, centreY = (figure.MinY + figure.MaxY) / 2;
        Point Place(QuantityFigurePoint point) =>
            new(ActualWidth / 2 + (point.X - centreX) * scale, ActualHeight / 2 - (point.Y - centreY) * scale);     // Y up

        foreach (var shape in figure.Shapes)
        {
            var fill = shape.Class switch
            {
                QuantityMaterialClass.Concrete => Brush("SectionConcrete", Brushes.LightGray),
                QuantityMaterialClass.Steel => Brush("SectionSteel", Brushes.SteelBlue),
                _ => Brush("Muted", Brushes.Gray),
            };
            // Steel is edged in its own colour, so a thin plate still reads as steel at thumbnail size.
            var outline = new Pen(shape.Class == QuantityMaterialClass.Steel ? fill : Brush("ViewportEdge", Brushes.Gray), 0.8) { LineJoin = PenLineJoin.Miter };
            outline.Freeze();
            var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var context = geometry.Open())
                foreach (var points in shape.Outlines.Where(points => points.Count >= 3))
                {
                    context.BeginFigure(Place(points[0]), isFilled: true, isClosed: true);
                    context.PolyLineTo(points.Skip(1).Select(Place).ToArray(), isStroked: true, isSmoothJoin: false);
                }
            geometry.Freeze();
            drawing.DrawGeometry(fill, outline, geometry);
        }
    }

    private Brush Brush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
}
