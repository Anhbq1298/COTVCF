using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Presentation.Controls;

namespace Mcotis.Desktop.Presentation;

/// <summary>Uses the screen renderer in an independent canvas; exporting never changes the live view.</summary>
internal static class ResultFigureCapture
{
    internal static byte[] Capture(MainViewModel vm, ModelCanvas? source, FemAnalysisResult? result, FemResultMode mode, string? plane = null)
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
        if (plane is not null) canvas.Plane = plane;
        canvas.FemResult = result; canvas.FemResultMode = mode;
        if (result is null)
        {
            // the model view: the dimensioned geometry with the applied loads (red arrows) and their values
            canvas.ShowDimensions = true;
            canvas.AppliedPoints = vm.State.AppliedPoints; canvas.FrameLoads = vm.SharedViewerFrameLoads;
            canvas.LoadValues = vm.ReportLoadValues();
        }
        // A report figure shows the diagram's pattern on the geometry and its dimensions: no loads, no value or name labels, no grid
        // and no view chrome, which crowd an A4 page until nothing reads.
        canvas.ShowAxialValues = false; canvas.ShowLoads = result is null; canvas.ShowPointLabels = false;
        canvas.ShowFrameLabels = false; canvas.ShowCableLabels = false; canvas.ShowReferenceLabels = false;
        canvas.ShowFrameLocalAxes = false; canvas.ShowGridLines = false; canvas.ShowTitle = false;
        try
        {
            var size = new Size(1800, 700); canvas.Measure(size); canvas.Arrange(new Rect(size));
            canvas.Fit(); canvas.ZoomBy(.85); // leave room for the diagrams beyond the model bounds
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

    /// <summary>The model's geometry in a true isometric view (azimuth −45°, elevation 35.264°, Z up), with its sections as the 3D view draws them.</summary>
    internal static byte[] CaptureIsometric(MainViewModel vm)
    {
        var model = vm.SharedViewerModel;
        var scene = StructureModel3D.Build(model, vm.SharedViewerSections, vm.SharedViewerSectionDrawings, vm.ShowExtruded3D);
        const double azimuth = -45 * Math.PI / 180, elevation = 35.264 * Math.PI / 180;
        var from = new Vector3D(Math.Cos(azimuth) * Math.Cos(elevation), Math.Sin(azimuth) * Math.Cos(elevation), Math.Sin(elevation));
        var right = Vector3D.CrossProduct(-from, new Vector3D(0, 0, 1)); right.Normalize();
        var up = Vector3D.CrossProduct(right, -from); up.Normalize();
        var bounds = scene.Bounds.IsEmpty ? new Rect3D(0, 0, 0, 1, 1, 1) : scene.Bounds;
        var centre = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        double width = 0, height = 0;
        foreach (var x in new[] { bounds.X, bounds.X + bounds.SizeX })
        foreach (var y in new[] { bounds.Y, bounds.Y + bounds.SizeY })
        foreach (var z in new[] { bounds.Z, bounds.Z + bounds.SizeZ })
        {
            var offset = new Point3D(x, y, z) - centre;
            width = Math.Max(width, 2 * Math.Abs(Vector3D.DotProduct(offset, right)));
            height = Math.Max(height, 2 * Math.Abs(Vector3D.DotProduct(offset, up)));
        }
        var size = new Size(1800, 900);
        var span = Math.Max(1e-3, Math.Max(width, height * size.Width / size.Height)) * 1.08;
        var viewport = new Viewport3D
        {
            Camera = new OrthographicCamera(centre + from * span * 10, -from, new Vector3D(0, 0, 1), span),
            Width = size.Width, Height = size.Height,
        };
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0x5A, 0x5A, 0x5A)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0xC8, 0xC8, 0xC8), new Vector3D(-0.6, 0.8, -1)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x6E, 0x6E, 0x6E), new Vector3D(0.7, -0.4, 0.5)));
        viewport.Children.Add(new ModelVisual3D { Content = lights });
        viewport.Children.Add(new ModelVisual3D { Content = scene.Model });
        var host = new Grid { Background = Brushes.White, Width = size.Width, Height = size.Height, Children = { viewport } };
        host.Measure(size); host.Arrange(new Rect(size)); host.UpdateLayout();
        var bitmap = new RenderTargetBitmap(3600, 1800, 192, 192, PixelFormats.Pbgra32); bitmap.Render(host);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
}
