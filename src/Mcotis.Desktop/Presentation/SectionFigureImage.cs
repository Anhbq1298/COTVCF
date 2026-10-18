using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mcotis.Desktop.Application.Quantities;
using Mcotis.Desktop.Presentation.Controls;

namespace Mcotis.Desktop.Presentation;

/// <summary>A section drawing as a PNG for a report table, drawn by the same view as Show Quantities.</summary>
internal static class SectionFigureImage
{
    internal static byte[]? Render(QuantitySectionFigure figure)
    {
        if (figure.Shapes.Count == 0 || !(figure.Width > 0) || !(figure.Height > 0)) return null;
        var aspect = Math.Clamp(figure.Width / figure.Height, 0.5, 3);
        double height = 120, width = Math.Round(height * aspect);
        var view = new SectionFigureView { Figure = figure, Width = width, Height = height };
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(width * 2), (int)(height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
