using System.Collections.Specialized;
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
    public static readonly DependencyProperty AppliedPointsProperty = DependencyProperty.Register(nameof(AppliedPoints), typeof(IEnumerable<AppliedPoint>), typeof(Structural2DViewer), new PropertyMetadata(null));
    public static readonly DependencyProperty SelectedAppliedPointProperty = DependencyProperty.Register(nameof(SelectedAppliedPoint), typeof(AppliedPoint), typeof(Structural2DViewer), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public GeometryModel Model { get => (GeometryModel)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public object? ToolbarContent { get => GetValue(ToolbarContentProperty); set => SetValue(ToolbarContentProperty, value); }
    public bool ShowPointLabels { get => (bool)GetValue(ShowPointLabelsProperty); set => SetValue(ShowPointLabelsProperty, value); }
    public IEnumerable<AppliedPoint>? AppliedPoints { get => (IEnumerable<AppliedPoint>?)GetValue(AppliedPointsProperty); set => SetValue(AppliedPointsProperty, value); }
    public AppliedPoint? SelectedAppliedPoint { get => (AppliedPoint?)GetValue(SelectedAppliedPointProperty); set => SetValue(SelectedAppliedPointProperty, value); }

    private void Fit(object sender, RoutedEventArgs e) => Canvas.Fit();
    private void ResetView(object sender, RoutedEventArgs e)
    {
        Plane.SelectedIndex = 1; // XZ, the workflow default
        Canvas.Plane = "XZ";
        Canvas.Fit();
    }
    private void PlaneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Canvas is null) return;
        Canvas.Plane = ((ComboBoxItem)Plane.SelectedItem).Content.ToString()!;
        Canvas.Fit();
    }
}

public sealed class ModelCanvas : FrameworkElement
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(GeometryModel), typeof(ModelCanvas), new FrameworkPropertyMetadata(GeometryModel.Empty, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ModelCanvas)d).Fit()));
    public static readonly DependencyProperty ShowPointLabelsProperty = DependencyProperty.Register(nameof(ShowPointLabels), typeof(bool), typeof(ModelCanvas), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AppliedPointsProperty = DependencyProperty.Register(nameof(AppliedPoints), typeof(IEnumerable<AppliedPoint>), typeof(ModelCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, AppliedPointsChanged));
    public static readonly DependencyProperty SelectedAppliedPointProperty = DependencyProperty.Register(nameof(SelectedAppliedPoint), typeof(AppliedPoint), typeof(ModelCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public GeometryModel Model { get => (GeometryModel)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public bool ShowPointLabels { get => (bool)GetValue(ShowPointLabelsProperty); set => SetValue(ShowPointLabelsProperty, value); }
    public IEnumerable<AppliedPoint>? AppliedPoints { get => (IEnumerable<AppliedPoint>?)GetValue(AppliedPointsProperty); set => SetValue(AppliedPointsProperty, value); }
    public AppliedPoint? SelectedAppliedPoint { get => (AppliedPoint?)GetValue(SelectedAppliedPointProperty); set => SetValue(SelectedAppliedPointProperty, value); }
    public string Plane { get; set; } = "XZ";

    private readonly Dictionary<AppliedPoint, Point> appliedPointLocations = [];
    private double zoom = 1;
    private Vector pan;
    private Point? last;

    public ModelCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Hand;
        SizeChanged += (_, _) => InvalidateVisual();
    }

    private static void AppliedPointsChanged(DependencyObject item, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (ModelCanvas)item;
        if (e.OldValue is INotifyCollectionChanged oldCollection) oldCollection.CollectionChanged -= canvas.AppliedPointsCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newCollection) newCollection.CollectionChanged += canvas.AppliedPointsCollectionChanged;
        canvas.InvalidateVisual();
    }

    private void AppliedPointsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    public void Fit()
    {
        zoom = 1;
        pan = default;
        InvalidateVisual();
    }

    private Point Project(Position position) => Plane == "XY" ? new(position.X, position.Y) : Plane == "YZ" ? new(position.Y, position.Z) : new(position.X, position.Z);
    private Brush Brush(string name) => (Brush)FindResource(name);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        appliedPointLocations.Clear();
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        dc.DrawRectangle(Brush("ViewportBg"), null, new Rect(RenderSize));
        var grid = new Pen(Brush("ViewportGrid"), 1);
        for (double x = 0; x < ActualWidth; x += 32) dc.DrawLine(grid, new(x, 0), new(x, ActualHeight));
        for (double y = 0; y < ActualHeight; y += 32) dc.DrawLine(grid, new(0, y), new(ActualWidth, y));

        if (Model.Points.Count > 0)
        {
            var projected = Model.Points.ToDictionary(item => item.Key, item => Project(item.Value.Position));
            var minX = projected.Values.Min(point => point.X);
            var maxX = projected.Values.Max(point => point.X);
            var minY = projected.Values.Min(point => point.Y);
            var maxY = projected.Values.Max(point => point.Y);
            double spanX = maxX - minX;
            double spanY = maxY - minY;
            double scale = Math.Min(Math.Max(1, ActualWidth - 96) / Math.Max(spanX, 1e-9), Math.Max(1, ActualHeight - 96) / Math.Max(spanY, 1e-9)) * zoom;
            Point Screen(Point point) => new((point.X - (minX + spanX / 2)) * scale + ActualWidth / 2 + pan.X, -(point.Y - (minY + spanY / 2)) * scale + ActualHeight / 2 + pan.Y);
            var screenPoints = projected.ToDictionary(item => item.Key, item => Screen(item.Value));

            foreach (var member in Model.Members)
            {
                if (!screenPoints.TryGetValue(member.Start, out var start) || !screenPoints.TryGetValue(member.End, out var end)) continue;
                var pen = new Pen(Brush(member.Kind == MemberKind.Frame ? "ViewportGeom" : "AccentBright"), 1.5);
                if (member.Kind != MemberKind.Frame) pen.DashStyle = member.Kind == MemberKind.Cable ? DashStyles.Dash : DashStyles.Dot;
                dc.DrawLine(pen, start, end);
            }

            foreach (var point in screenPoints.Values)
                dc.DrawEllipse(Brush("ViewportBg"), new Pen(Brush("ViewportEdge"), 1), point, 2.5, 2.5);

            foreach (var applied in AppliedPoints ?? [])
            {
                if (!screenPoints.TryGetValue(applied.PointName, out var point)) continue;
                appliedPointLocations[applied] = point;
                DrawLoadArrow(dc, point);
                DrawAppliedPoint(dc, point, ReferenceEquals(applied, SelectedAppliedPoint));
            }

            if (ShowPointLabels)
                foreach (var item in screenPoints.OrderBy(item => item.Key))
                    Label(dc, item.Key, item.Value + new Vector(6, -18));
        }
        else Text(dc, "No geometry loaded", new(Math.Max(16, ActualWidth / 2 - 70), ActualHeight / 2), "ViewportEdge");

        DrawAxes(dc);
        Text(dc, Plane + " projection", new(16, 12), "ViewportEdge");
    }

    private void DrawAppliedPoint(DrawingContext dc, Point point, bool selected)
    {
        var accent = Brush("AccentBright");
        var selectedBrush = Brush("Error");
        var halo = new SolidColorBrush(selected ? Color.FromArgb(90, 240, 68, 56) : Color.FromArgb(70, 46, 144, 250));
        dc.DrawEllipse(halo, null, point, selected ? 13 : 10, selected ? 13 : 10);
        dc.DrawEllipse(selected ? selectedBrush : accent, new Pen(Brush("ViewportGeom"), 2), point, selected ? 6.5 : 5.5, selected ? 6.5 : 5.5);
    }

    private void DrawLoadArrow(DrawingContext dc, Point point)
    {
        var brush = Brush("Error");
        var pen = new Pen(brush, 2);
        var start = point + new Vector(0, -50);
        var end = point + new Vector(0, -10);
        dc.DrawLine(pen, start, end);
        dc.DrawLine(pen, end, end + new Vector(-5, -8));
        dc.DrawLine(pen, end, end + new Vector(5, -8));
    }

    private void DrawAxes(DrawingContext dc)
    {
        var origin = new Point(40, ActualHeight - 40);
        var horizontalBrush = AxisBrush(Plane[0]);
        var verticalBrush = AxisBrush(Plane[1]);
        var horizontalAxis = new Pen(horizontalBrush, 1.5);
        var verticalAxis = new Pen(verticalBrush, 1.5);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 18, 22, 27)), null, new Rect(16, ActualHeight - 112, 112, 96));
        dc.DrawLine(horizontalAxis, origin, origin + new Vector(56, 0));
        dc.DrawLine(verticalAxis, origin, origin + new Vector(0, -48));
        dc.DrawLine(horizontalAxis, origin + new Vector(56, 0), origin + new Vector(50, -4));
        dc.DrawLine(verticalAxis, origin + new Vector(0, -48), origin + new Vector(4, -42));
        Text(dc, Plane[0].ToString(), origin + new Vector(60, -8), horizontalBrush);
        Text(dc, Plane[1].ToString(), origin + new Vector(-4, -68), verticalBrush);
    }

    private static Brush AxisBrush(char axis) => axis switch
    {
        'X' => new SolidColorBrush(Color.FromArgb(235, 240, 96, 72)),
        'Y' => new SolidColorBrush(Color.FromArgb(235, 84, 150, 255)),
        'Z' => new SolidColorBrush(Color.FromArgb(235, 84, 200, 110)),
        _ => Brushes.DimGray
    };

    private void Text(DrawingContext dc, string text, Point point, string brush) => Text(dc, text, point, Brush(brush));
    private void Text(DrawingContext dc, string text, Point point, Brush brush) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), point);

    private void Label(DrawingContext dc, string text, Point point)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brush("ViewportGeom"), dpi);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(190, 18, 22, 27)), null, new Rect(point.X - 2, point.Y - 1, label.Width + 4, label.Height + 2));
        dc.DrawText(label, point);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var old = zoom;
        zoom = Math.Clamp(zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), .05, 100);
        var point = e.GetPosition(this) - new Point(ActualWidth / 2, ActualHeight / 2);
        pan = point - (point - pan) * (zoom / old);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Fit(); return; }
        Focus();
        var position = e.GetPosition(this);
        var hit = appliedPointLocations.Where(item => (item.Value - position).Length <= 12).OrderBy(item => (item.Value - position).Length).Select(item => item.Key).FirstOrDefault();
        if (hit is not null)
        {
            SelectedAppliedPoint = hit;
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        last = position;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (last is not { } point || !IsMouseCaptured) return;
        var next = e.GetPosition(this);
        pan += next - point;
        last = next;
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        last = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Home) Fit();
        else if (e.Key == Key.Add) zoom = Math.Min(100, zoom * 1.15);
        else if (e.Key == Key.Subtract) zoom = Math.Max(.05, zoom / 1.15);
        InvalidateVisual();
    }
}
