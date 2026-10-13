using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Presentation.Controls;

namespace Mcotis.Desktop.Presentation;

/// <summary>Uses the screen renderer in an independent canvas; exporting never changes the live view.</summary>
internal static class ResultFigureCapture
{
    internal static byte[] Capture(MainViewModel vm, ModelCanvas? source, FemAnalysisResult result, FemResultMode mode)
    {
        var canvas = new ModelCanvas();
        if (source is not null)
        {
            foreach (var field in typeof(ModelCanvas).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (field.GetValue(null) is DependencyProperty property) canvas.SetValue(property, source.GetValue(property));
            canvas.Plane = source.Plane;
        }
        else
        {
            canvas.Model = vm.SharedViewerModel; canvas.ShowPointLabels = true;
            canvas.ShowDimensions = vm.ShowDimensions; canvas.PinSupportPoints = vm.SharedViewerPinSupportPoints;
            canvas.MemberSections = vm.SharedViewerSections; canvas.AppliedPoints = vm.SharedViewerAppliedPoints;
            canvas.ShowLoads = vm.SharedViewerShowLoads; canvas.LoadValues = vm.SharedViewerLoadValues;
            canvas.SpanPoints = vm.SharedViewerSpanPoints;
        }
        canvas.SelectedCable = null; canvas.SelectedAppliedPoint = null;
        canvas.FemResult = result; canvas.FemResultMode = mode; canvas.ShowAxialValues = true;
        try
        {
            var size = new Size(1800, 700); canvas.Measure(size); canvas.Arrange(new Rect(size));
            canvas.Fit(); canvas.ZoomBy(.8); // leave room for load arrows and diagrams beyond the model bounds
            canvas.UpdateLayout();
            var bitmap = new RenderTargetBitmap(3600, 1400, 192, 192, PixelFormats.Pbgra32); bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
        }
        finally
        {
            // Offscreen canvases must not stay subscribed to the live model's collections.
            canvas.AppliedPoints = null; canvas.Cables = null;
        }
    }
}
