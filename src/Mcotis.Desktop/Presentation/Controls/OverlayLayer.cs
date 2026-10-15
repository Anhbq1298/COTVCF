using System.Windows;
using System.Windows.Media;

namespace Mcotis.Desktop.Presentation.Controls;

/// <summary>A layer drawn over the model view by whoever owns it — the grid of the 3D view, the preview of a drawing tool —
/// without redrawing the model under it: <see cref="Show"/> keeps the drawing and repaints this layer alone, so a mouse
/// move costs one small redraw. It takes no mouse input.</summary>
public sealed class OverlayLayer : FrameworkElement
{
    private Action<DrawingContext>? draw;

    public OverlayLayer() => IsHitTestVisible = false;

    /// <summary>Whether the layer draws anything (checks).</summary>
    internal bool HasContent => draw is not null;

    public void Show(Action<DrawingContext>? drawing)
    {
        if (draw is null && drawing is null) return;
        draw = drawing;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (draw is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        draw(dc);
        dc.Pop();
    }
}
