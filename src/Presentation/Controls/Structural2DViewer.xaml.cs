using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using COT_VCF.Domain;

namespace COT_VCF.Presentation.Controls;
public partial class Structural2DViewer : UserControl
{
    public Structural2DViewer() => InitializeComponent();
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(GeometryModel), typeof(Structural2DViewer), new PropertyMetadata(GeometryModel.Empty));
    public static readonly DependencyProperty ToolbarContentProperty = DependencyProperty.Register(nameof(ToolbarContent), typeof(object), typeof(Structural2DViewer), new PropertyMetadata(null));
    public static readonly DependencyProperty ShowPointLabelsProperty = DependencyProperty.Register(nameof(ShowPointLabels), typeof(bool), typeof(Structural2DViewer), new PropertyMetadata(false));
    public GeometryModel Model { get => (GeometryModel)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public object? ToolbarContent { get => GetValue(ToolbarContentProperty); set => SetValue(ToolbarContentProperty, value); }
    public bool ShowPointLabels { get => (bool)GetValue(ShowPointLabelsProperty); set => SetValue(ShowPointLabelsProperty, value); }
    private void Fit(object sender, RoutedEventArgs e) => Canvas.Fit();
    private void PlaneChanged(object sender, SelectionChangedEventArgs e) { if (Canvas is null) return; Canvas.Plane = ((ComboBoxItem)Plane.SelectedItem).Content.ToString()!; Canvas.Fit(); }
}
public sealed class ModelCanvas : FrameworkElement
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(GeometryModel), typeof(ModelCanvas), new FrameworkPropertyMetadata(GeometryModel.Empty, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ModelCanvas)d).Fit()));
    public static readonly DependencyProperty ShowPointLabelsProperty = DependencyProperty.Register(nameof(ShowPointLabels), typeof(bool), typeof(ModelCanvas), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public GeometryModel Model { get => (GeometryModel)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public bool ShowPointLabels { get => (bool)GetValue(ShowPointLabelsProperty); set => SetValue(ShowPointLabelsProperty, value); }
    public string Plane { get; set; } = "XZ";
    private double zoom = 1;
    private Vector pan;
    private Point? last;
    public ModelCanvas() { ClipToBounds = true; Focusable = true; Cursor = Cursors.Hand; SizeChanged += (_, _) => InvalidateVisual(); }
    public void Fit() { zoom = 1; pan = default; InvalidateVisual(); }
    private Point Project(Position p) => Plane == "XY" ? new(p.X, p.Y) : Plane == "YZ" ? new(p.Y, p.Z) : new(p.X, p.Z);
    private Brush Brush(string name) => (Brush)FindResource(name);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        dc.DrawRectangle(Brush("Surface"), null, new Rect(RenderSize));
        var grid = new Pen(Brush("Raised"), 1);
        for (double x = 0; x < ActualWidth; x += 32) dc.DrawLine(grid, new(x, 0), new(x, ActualHeight));
        for (double y = 0; y < ActualHeight; y += 32) dc.DrawLine(grid, new(0, y), new(ActualWidth, y));
        if (Model.Points.Count > 0)
        {
            var projected = Model.Points.ToDictionary(k => k.Key, k => Project(k.Value.Position));
            var minX = projected.Values.Min(p => p.X); var maxX = projected.Values.Max(p => p.X);
            var minY = projected.Values.Min(p => p.Y); var maxY = projected.Values.Max(p => p.Y);
            double spanX = maxX - minX, spanY = maxY - minY;
            double scale = Math.Min(Math.Max(1, ActualWidth - 96) / Math.Max(spanX, 1e-9), Math.Max(1, ActualHeight - 96) / Math.Max(spanY, 1e-9)) * zoom;
            Point Screen(Point p) => new((p.X - (minX + spanX / 2)) * scale + ActualWidth / 2 + pan.X, -(p.Y - (minY + spanY / 2)) * scale + ActualHeight / 2 + pan.Y);
            foreach (var member in Model.Members)
            {
                if (!projected.TryGetValue(member.Start, out var a) || !projected.TryGetValue(member.End, out var b)) continue;
                var pen = new Pen(Brush(member.Kind == MemberKind.Frame ? "Frame" : "Accent"), 1.5);
                if (member.Kind != MemberKind.Frame) pen.DashStyle = member.Kind == MemberKind.Cable ? DashStyles.Dash : DashStyles.Dot;
                dc.DrawLine(pen, Screen(a), Screen(b));
            }
            foreach (var item in projected.OrderBy(p => p.Key))
            {
                var point = Screen(item.Value);
                dc.DrawEllipse(Brush("Surface"), new Pen(Brush("Frame"), 1), point, 2.5, 2.5);
                if (ShowPointLabels) Label(dc, item.Key, point + new Vector(6, -18));
            }
        }
        else Text(dc, "No geometry loaded", new(Math.Max(16, ActualWidth / 2 - 70), ActualHeight / 2), "Muted");
        // Axes are screen-space overlays, independent of zoom/pan.
        var origin = new Point(40, ActualHeight - 40);
        var horizontalBrush = AxisBrush(Plane[0]);
        var verticalBrush = AxisBrush(Plane[1]);
        var horizontalAxis = new Pen(horizontalBrush, 1.5);
        var verticalAxis = new Pen(verticalBrush, 1.5);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), null, new Rect(16, ActualHeight - 112, 112, 96));
        dc.DrawLine(horizontalAxis, origin, origin + new Vector(56, 0));
        dc.DrawLine(verticalAxis, origin, origin + new Vector(0, -48));
        dc.DrawLine(horizontalAxis, origin + new Vector(56, 0), origin + new Vector(50, -4));
        dc.DrawLine(verticalAxis, origin + new Vector(0, -48), origin + new Vector(4, -42));
        Text(dc, Plane[0].ToString(), origin + new Vector(60, -8), horizontalBrush);
        Text(dc, Plane[1].ToString(), origin + new Vector(-4, -68), verticalBrush);
        Text(dc, Plane + " projection", new(16, 12), "Muted");
    }
    private static Brush AxisBrush(char axis) => axis switch
    {
        'X' => new SolidColorBrush(Color.FromArgb(215, 198, 40, 40)),
        'Y' => new SolidColorBrush(Color.FromArgb(215, 37, 99, 235)),
        'Z' => new SolidColorBrush(Color.FromArgb(215, 46, 125, 50)),
        _ => Brushes.DimGray
    };
    private void Text(DrawingContext dc, string text, Point point, string brush) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brush(brush), VisualTreeHelper.GetDpi(this).PixelsPerDip), point);
    private void Text(DrawingContext dc, string text, Point point, Brush brush) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), point);
    private void Label(DrawingContext dc, string text, Point point)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brush("Accent"), dpi);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(185, 255, 255, 255)), null, new Rect(point.X - 2, point.Y - 1, label.Width + 4, label.Height + 2));
        dc.DrawText(label, point);
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e) { var old = zoom; zoom = Math.Clamp(zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), .05, 100); var p = e.GetPosition(this) - new Point(ActualWidth / 2, ActualHeight / 2); pan = p - (p - pan) * (zoom / old); InvalidateVisual(); e.Handled = true; }
    protected override void OnMouseDown(MouseButtonEventArgs e) { if (e.ClickCount == 2) { Fit(); return; } Focus(); last = e.GetPosition(this); CaptureMouse(); }
    protected override void OnMouseMove(MouseEventArgs e) { if (last is { } p && IsMouseCaptured) { var next = e.GetPosition(this); pan += next - p; last = next; InvalidateVisual(); } }
    protected override void OnMouseUp(MouseButtonEventArgs e) { last = null; ReleaseMouseCapture(); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.Key == Key.Home) Fit(); else if (e.Key == Key.Add) zoom = Math.Min(100, zoom * 1.15); else if (e.Key == Key.Subtract) zoom = Math.Max(.05, zoom / 1.15); InvalidateVisual(); }
}
