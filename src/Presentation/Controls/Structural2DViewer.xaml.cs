using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using COT_VCF.Application;
using COT_VCF.Domain;
using COT_VCF.Geometry;

namespace COT_VCF.Presentation.Controls;

public partial class Structural2DViewer : UserControl
{
    public Structural2DViewer() => InitializeComponent();
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(GeometryModel), typeof(Structural2DViewer), new PropertyMetadata(GeometryModel.Empty));
    public static readonly DependencyProperty ToolbarContentProperty = DependencyProperty.Register(nameof(ToolbarContent), typeof(object), typeof(Structural2DViewer), new PropertyMetadata(null));
    public static readonly DependencyProperty ShowPointLabelsProperty = DependencyProperty.Register(nameof(ShowPointLabels), typeof(bool), typeof(Structural2DViewer), new PropertyMetadata(false));
    public static readonly DependencyProperty ShowFrameLabelsProperty = DependencyProperty.Register(nameof(ShowFrameLabels), typeof(bool), typeof(Structural2DViewer), new PropertyMetadata(false));
    public static readonly DependencyProperty ShowCableLabelsProperty = DependencyProperty.Register(nameof(ShowCableLabels), typeof(bool), typeof(Structural2DViewer), new PropertyMetadata(false));
    public static readonly DependencyProperty AppliedPointsProperty = DependencyProperty.Register(nameof(AppliedPoints), typeof(IEnumerable<AppliedPoint>), typeof(Structural2DViewer), new PropertyMetadata(null));
    public static readonly DependencyProperty SelectedAppliedPointProperty = DependencyProperty.Register(nameof(SelectedAppliedPoint), typeof(AppliedPoint), typeof(Structural2DViewer), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty CablesProperty = DependencyProperty.Register(nameof(Cables), typeof(IEnumerable<CablePretensionConfig>), typeof(Structural2DViewer), new PropertyMetadata(null));
    public static readonly DependencyProperty SelectedCableProperty = DependencyProperty.Register(nameof(SelectedCable), typeof(CablePretensionConfig), typeof(Structural2DViewer), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty TopPointsProperty = DependencyProperty.Register(nameof(TopPoints), typeof(IEnumerable<string>), typeof(Structural2DViewer), new PropertyMetadata(null));
    public static readonly DependencyProperty MiddlePointsProperty = DependencyProperty.Register(nameof(MiddlePoints), typeof(IEnumerable<string>), typeof(Structural2DViewer), new PropertyMetadata(null));
    public static readonly DependencyProperty BottomPointsProperty = DependencyProperty.Register(nameof(BottomPoints), typeof(IEnumerable<string>), typeof(Structural2DViewer), new PropertyMetadata(null));
    public static readonly DependencyProperty PinSupportPointsProperty = DependencyProperty.Register(nameof(PinSupportPoints), typeof(IEnumerable<string>), typeof(Structural2DViewer), new PropertyMetadata(null));
    public static readonly DependencyProperty FemResultProperty = DependencyProperty.Register(nameof(FemResult), typeof(FemAnalysisResult), typeof(Structural2DViewer), new PropertyMetadata(null));
    public static readonly DependencyProperty FemResultModeProperty = DependencyProperty.Register(nameof(FemResultMode), typeof(FemResultMode), typeof(Structural2DViewer), new PropertyMetadata(FemResultMode.AxialForce));

    public GeometryModel Model { get => (GeometryModel)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public object? ToolbarContent { get => GetValue(ToolbarContentProperty); set => SetValue(ToolbarContentProperty, value); }
    public bool ShowPointLabels { get => (bool)GetValue(ShowPointLabelsProperty); set => SetValue(ShowPointLabelsProperty, value); }
    public bool ShowFrameLabels { get => (bool)GetValue(ShowFrameLabelsProperty); set => SetValue(ShowFrameLabelsProperty, value); }
    public bool ShowCableLabels { get => (bool)GetValue(ShowCableLabelsProperty); set => SetValue(ShowCableLabelsProperty, value); }
    public IEnumerable<AppliedPoint>? AppliedPoints { get => (IEnumerable<AppliedPoint>?)GetValue(AppliedPointsProperty); set => SetValue(AppliedPointsProperty, value); }
    public AppliedPoint? SelectedAppliedPoint { get => (AppliedPoint?)GetValue(SelectedAppliedPointProperty); set => SetValue(SelectedAppliedPointProperty, value); }
    public IEnumerable<CablePretensionConfig>? Cables { get => (IEnumerable<CablePretensionConfig>?)GetValue(CablesProperty); set => SetValue(CablesProperty, value); }
    public CablePretensionConfig? SelectedCable { get => (CablePretensionConfig?)GetValue(SelectedCableProperty); set => SetValue(SelectedCableProperty, value); }
    public IEnumerable<string>? TopPoints { get => (IEnumerable<string>?)GetValue(TopPointsProperty); set => SetValue(TopPointsProperty, value); }
    public IEnumerable<string>? MiddlePoints { get => (IEnumerable<string>?)GetValue(MiddlePointsProperty); set => SetValue(MiddlePointsProperty, value); }
    public IEnumerable<string>? BottomPoints { get => (IEnumerable<string>?)GetValue(BottomPointsProperty); set => SetValue(BottomPointsProperty, value); }
    public IEnumerable<string>? PinSupportPoints { get => (IEnumerable<string>?)GetValue(PinSupportPointsProperty); set => SetValue(PinSupportPointsProperty, value); }
    public FemAnalysisResult? FemResult { get => (FemAnalysisResult?)GetValue(FemResultProperty); set => SetValue(FemResultProperty, value); }
    public FemResultMode FemResultMode { get => (FemResultMode)GetValue(FemResultModeProperty); set => SetValue(FemResultModeProperty, value); }
    public static readonly DependencyProperty FrameMeshSegmentsProperty = DependencyProperty.Register(nameof(FrameMeshSegments), typeof(int), typeof(Structural2DViewer), new PropertyMetadata(0));
    public static readonly DependencyProperty MemberSizesProperty = DependencyProperty.Register(nameof(MemberSizes), typeof(IReadOnlyDictionary<string, string>), typeof(Structural2DViewer), new PropertyMetadata(null));
    public static readonly DependencyProperty ShowLoadsProperty = DependencyProperty.Register(nameof(ShowLoads), typeof(bool), typeof(Structural2DViewer), new PropertyMetadata(true));
    public static readonly DependencyProperty LoadValuesProperty = DependencyProperty.Register(nameof(LoadValues), typeof(IReadOnlyDictionary<string, double>), typeof(Structural2DViewer), new PropertyMetadata(null));
    /// <summary>When above 0, frames are drawn broken into this many mesh segments (cables are never meshed).</summary>
    public int FrameMeshSegments { get => (int)GetValue(FrameMeshSegmentsProperty); set => SetValue(FrameMeshSegmentsProperty, value); }
    /// <summary>Member name → section name; when set, each frame and cable is labelled with its size.</summary>
    public IReadOnlyDictionary<string, string>? MemberSizes { get => (IReadOnlyDictionary<string, string>?)GetValue(MemberSizesProperty); set => SetValue(MemberSizesProperty, value); }
    public bool ShowLoads { get => (bool)GetValue(ShowLoadsProperty); set => SetValue(ShowLoadsProperty, value); }
    /// <summary>Point name → vertical load Fz [kN] labelled at the load arrows.</summary>
    public IReadOnlyDictionary<string, double>? LoadValues { get => (IReadOnlyDictionary<string, double>?)GetValue(LoadValuesProperty); set => SetValue(LoadValuesProperty, value); }
    public static readonly DependencyProperty ShowAxialValuesProperty = DependencyProperty.Register(nameof(ShowAxialValues), typeof(bool), typeof(Structural2DViewer), new PropertyMetadata(true));
    /// <summary>In the axial-force result view, label frame axial forces and cable tensions in whole kN.</summary>
    public bool ShowAxialValues { get => (bool)GetValue(ShowAxialValuesProperty); set => SetValue(ShowAxialValuesProperty, value); }

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
    public static readonly DependencyProperty ShowFrameLabelsProperty = DependencyProperty.Register(nameof(ShowFrameLabels), typeof(bool), typeof(ModelCanvas), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowCableLabelsProperty = DependencyProperty.Register(nameof(ShowCableLabels), typeof(bool), typeof(ModelCanvas), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AppliedPointsProperty = DependencyProperty.Register(nameof(AppliedPoints), typeof(IEnumerable<AppliedPoint>), typeof(ModelCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, AppliedPointsChanged));
    public static readonly DependencyProperty SelectedAppliedPointProperty = DependencyProperty.Register(nameof(SelectedAppliedPoint), typeof(AppliedPoint), typeof(ModelCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty CablesProperty = DependencyProperty.Register(nameof(Cables), typeof(IEnumerable<CablePretensionConfig>), typeof(ModelCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, CablesChanged));
    public static readonly DependencyProperty SelectedCableProperty = DependencyProperty.Register(nameof(SelectedCable), typeof(CablePretensionConfig), typeof(ModelCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty TopPointsProperty = DependencyProperty.Register(nameof(TopPoints), typeof(IEnumerable<string>), typeof(ModelCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MiddlePointsProperty = DependencyProperty.Register(nameof(MiddlePoints), typeof(IEnumerable<string>), typeof(ModelCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BottomPointsProperty = DependencyProperty.Register(nameof(BottomPoints), typeof(IEnumerable<string>), typeof(ModelCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PinSupportPointsProperty = DependencyProperty.Register(nameof(PinSupportPoints), typeof(IEnumerable<string>), typeof(ModelCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FemResultProperty = DependencyProperty.Register(nameof(FemResult), typeof(FemAnalysisResult), typeof(ModelCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FemResultModeProperty = DependencyProperty.Register(nameof(FemResultMode), typeof(FemResultMode), typeof(ModelCanvas), new FrameworkPropertyMetadata(FemResultMode.AxialForce, FrameworkPropertyMetadataOptions.AffectsRender));

    public GeometryModel Model { get => (GeometryModel)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public bool ShowPointLabels { get => (bool)GetValue(ShowPointLabelsProperty); set => SetValue(ShowPointLabelsProperty, value); }
    public bool ShowFrameLabels { get => (bool)GetValue(ShowFrameLabelsProperty); set => SetValue(ShowFrameLabelsProperty, value); }
    public bool ShowCableLabels { get => (bool)GetValue(ShowCableLabelsProperty); set => SetValue(ShowCableLabelsProperty, value); }
    public IEnumerable<AppliedPoint>? AppliedPoints { get => (IEnumerable<AppliedPoint>?)GetValue(AppliedPointsProperty); set => SetValue(AppliedPointsProperty, value); }
    public AppliedPoint? SelectedAppliedPoint { get => (AppliedPoint?)GetValue(SelectedAppliedPointProperty); set => SetValue(SelectedAppliedPointProperty, value); }
    public IEnumerable<CablePretensionConfig>? Cables { get => (IEnumerable<CablePretensionConfig>?)GetValue(CablesProperty); set => SetValue(CablesProperty, value); }
    public CablePretensionConfig? SelectedCable { get => (CablePretensionConfig?)GetValue(SelectedCableProperty); set => SetValue(SelectedCableProperty, value); }
    public IEnumerable<string>? TopPoints { get => (IEnumerable<string>?)GetValue(TopPointsProperty); set => SetValue(TopPointsProperty, value); }
    public IEnumerable<string>? MiddlePoints { get => (IEnumerable<string>?)GetValue(MiddlePointsProperty); set => SetValue(MiddlePointsProperty, value); }
    public IEnumerable<string>? BottomPoints { get => (IEnumerable<string>?)GetValue(BottomPointsProperty); set => SetValue(BottomPointsProperty, value); }
    public IEnumerable<string>? PinSupportPoints { get => (IEnumerable<string>?)GetValue(PinSupportPointsProperty); set => SetValue(PinSupportPointsProperty, value); }
    public FemAnalysisResult? FemResult { get => (FemAnalysisResult?)GetValue(FemResultProperty); set => SetValue(FemResultProperty, value); }
    public FemResultMode FemResultMode { get => (FemResultMode)GetValue(FemResultModeProperty); set => SetValue(FemResultModeProperty, value); }
    public static readonly DependencyProperty FrameMeshSegmentsProperty = DependencyProperty.Register(nameof(FrameMeshSegments), typeof(int), typeof(ModelCanvas), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MemberSizesProperty = DependencyProperty.Register(nameof(MemberSizes), typeof(IReadOnlyDictionary<string, string>), typeof(ModelCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowLoadsProperty = DependencyProperty.Register(nameof(ShowLoads), typeof(bool), typeof(ModelCanvas), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LoadValuesProperty = DependencyProperty.Register(nameof(LoadValues), typeof(IReadOnlyDictionary<string, double>), typeof(ModelCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public int FrameMeshSegments { get => (int)GetValue(FrameMeshSegmentsProperty); set => SetValue(FrameMeshSegmentsProperty, value); }
    public IReadOnlyDictionary<string, string>? MemberSizes { get => (IReadOnlyDictionary<string, string>?)GetValue(MemberSizesProperty); set => SetValue(MemberSizesProperty, value); }
    public bool ShowLoads { get => (bool)GetValue(ShowLoadsProperty); set => SetValue(ShowLoadsProperty, value); }
    public IReadOnlyDictionary<string, double>? LoadValues { get => (IReadOnlyDictionary<string, double>?)GetValue(LoadValuesProperty); set => SetValue(LoadValuesProperty, value); }
    public static readonly DependencyProperty ShowAxialValuesProperty = DependencyProperty.Register(nameof(ShowAxialValues), typeof(bool), typeof(ModelCanvas), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool ShowAxialValues { get => (bool)GetValue(ShowAxialValuesProperty); set => SetValue(ShowAxialValuesProperty, value); }
    public string Plane { get; set; } = "XZ";

    private readonly Dictionary<AppliedPoint, Point> appliedPointLocations = [];
    private readonly Dictionary<CablePretensionConfig, (Point Start, Point End)> cableLocations = [];
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

    private static void CablesChanged(DependencyObject item, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (ModelCanvas)item;
        if (e.OldValue is INotifyCollectionChanged oldCollection) oldCollection.CollectionChanged -= canvas.CablesCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newCollection) newCollection.CollectionChanged += canvas.CablesCollectionChanged;
        canvas.InvalidateVisual();
    }

    private void CablesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

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
        cableLocations.Clear();
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

            // Joints the FEM connects frames at: every member end. A frame is split at those lying along it, then each
            // piece is meshed, exactly as the solver does; cables are never split or meshed.
            var memberJoints = FrameMeshSegments > 1
                ? Model.Members.SelectMany(member => new[] { member.Start, member.End }).Distinct(StringComparer.Ordinal)
                    .Where(Model.Points.ContainsKey).ToDictionary(name => name, name => Model.Points[name].Position, StringComparer.Ordinal)
                : null;
            foreach (var member in Model.Members)
            {
                if (!screenPoints.TryGetValue(member.Start, out var start) || !screenPoints.TryGetValue(member.End, out var end)) continue;
                var pen = new Pen(Brush(member.Kind == MemberKind.Frame ? "ViewportGeom" : "AccentBright"), 1.5);
                if (member.Kind != MemberKind.Frame) pen.DashStyle = member.Kind == MemberKind.Cable ? DashStyles.Dash : DashStyles.Dot;
                if (member.Kind == MemberKind.Frame && memberJoints is not null)
                    foreach (var piece in FrameJoints.Pieces(member, memberJoints))
                        DrawMeshedFrame(dc, pen, start + (end - start) * piece.FractionStart, start + (end - start) * piece.FractionEnd, FrameMeshSegments);
                else dc.DrawLine(pen, start, end);
            }

            if (MemberSizes is { } sizes)
                foreach (var member in Model.Members)
                    if (sizes.TryGetValue(member.Name, out var size) && !string.IsNullOrWhiteSpace(size) &&
                        screenPoints.TryGetValue(member.Start, out var sizeStart) && screenPoints.TryGetValue(member.End, out var sizeEnd))
                        MemberLabel(dc, size, sizeStart, sizeEnd, -9, member.Kind == MemberKind.Frame ? "Accent" : "AccentBright");

            var cableMembers = Model.Members
                .Where(member => member.Kind == MemberKind.Cable)
                .ToDictionary(member => member.Name, StringComparer.Ordinal);
            foreach (var cable in Cables ?? [])
            {
                if (!cableMembers.TryGetValue(cable.CableName, out var member) ||
                    !screenPoints.TryGetValue(member.Start, out var start) ||
                    !screenPoints.TryGetValue(member.End, out var end)) continue;
                var selected = ReferenceEquals(cable, SelectedCable);
                cableLocations[cable] = (start, end);
                DrawCable(dc, cable.CableName, start, end, selected);
            }

            if (ShowFrameLabels || ShowCableLabels)
            {
                var configuredCableNames = (Cables ?? []).Select(c => c.CableName).ToHashSet(StringComparer.Ordinal);
                foreach (var member in Model.Members)
                {
                    var isFrame = member.Kind == MemberKind.Frame;
                    if (isFrame ? !ShowFrameLabels : !ShowCableLabels) continue;
                    if (!isFrame && configuredCableNames.Contains(member.Name)) continue; // already labelled by the cable overlay
                    if (!screenPoints.TryGetValue(member.Start, out var ms) || !screenPoints.TryGetValue(member.End, out var me)) continue;
                    MemberLabel(dc, member.Name, ms, me);
                }
            }

            foreach (var point in screenPoints.Values)
                dc.DrawEllipse(Brush("ViewportBg"), new Pen(Brush("ViewportEdge"), 1), point, 2.5, 2.5);

            DrawGroup(dc, screenPoints, BottomPoints, Color.FromRgb(0x2E, 0x7D, 0x32));
            DrawGroup(dc, screenPoints, MiddlePoints, Color.FromRgb(0x25, 0x63, 0xEB));
            DrawGroup(dc, screenPoints, TopPoints, Color.FromRgb(0xC6, 0x28, 0x28));

            foreach (var applied in ShowLoads ? AppliedPoints ?? [] : [])
            {
                if (!screenPoints.TryGetValue(applied.PointName, out var point)) continue;
                appliedPointLocations[applied] = point;
                DrawLoadArrow(dc, point);
                DrawAppliedPoint(dc, point, ReferenceEquals(applied, SelectedAppliedPoint));
                if (LoadValues is { } loads && loads.TryGetValue(applied.PointName, out var fz))
                    CableLabel(dc, $"{fz:0.##} kN", point + new Vector(6, -62), false);
            }

            foreach (var name in PinSupportPoints ?? [])
                if (screenPoints.TryGetValue(name, out var support)) DrawPinSupport(dc, support);

            if (FemResult is { } femResult) DrawFemResultOverlay(dc, screenPoints, femResult, FemResultMode);

            if (ShowPointLabels)
                foreach (var item in screenPoints.OrderBy(item => item.Key))
                    Label(dc, item.Key, item.Value + new Vector(6, -18));
        }
        else Text(dc, "No geometry loaded", new(Math.Max(16, ActualWidth / 2 - 70), ActualHeight / 2), "ViewportEdge");

        DrawAxes(dc);
        Text(dc, Plane + " projection", new(16, 12), "ViewportEdge");
    }

    private void DrawGroup(DrawingContext dc, IReadOnlyDictionary<string, Point> screen, IEnumerable<string>? names, Color color)
    {
        if (names is null) return;
        var fill = new SolidColorBrush(color);
        var ring = new Pen(Brush("ViewportBg"), 1.5);
        foreach (var name in names)
            if (screen.TryGetValue(name, out var p))
                dc.DrawEllipse(fill, ring, p, 4.5, 4.5); // larger than the 2.5 px plain joint marker
    }

    private void DrawAppliedPoint(DrawingContext dc, Point point, bool selected)
    {
        var accent = Brush("AccentBright");
        var selectedBrush = Brush("Error");
        var halo = new SolidColorBrush(selected ? Color.FromArgb(72, 198, 40, 40) : Color.FromArgb(55, 46, 144, 250));
        dc.DrawEllipse(halo, null, point, selected ? 13 : 10, selected ? 13 : 10);
        dc.DrawEllipse(selected ? selectedBrush : accent, new Pen(Brush("ViewportBg"), 2), point, selected ? 6.5 : 5.5, selected ? 6.5 : 5.5);
    }

    private void DrawPinSupport(DrawingContext dc, Point joint)
    {
        var accent = Brush("Accent");
        var outline = new Pen(accent, 1.5);
        var triangle = new StreamGeometry();
        using (var context = triangle.Open())
        {
            context.BeginFigure(joint + new Vector(0, 2), true, true);
            context.LineTo(joint + new Vector(-9, 14), true, false);
            context.LineTo(joint + new Vector(9, 14), true, false);
        }
        dc.DrawGeometry(Brush("ViewportBg"), outline, triangle);
        var groundY = joint.Y + 15;
        dc.DrawLine(outline, new Point(joint.X - 12, groundY), new Point(joint.X + 12, groundY));
        for (var offset = -9; offset <= 9; offset += 6)
            dc.DrawLine(outline, new Point(joint.X + offset, groundY), new Point(joint.X + offset - 3, groundY + 4));
    }

    private void DrawFemResultOverlay(DrawingContext dc, IReadOnlyDictionary<string, Point> screenPoints,
        FemAnalysisResult result, FemResultMode mode)
    {
        if (mode == FemResultMode.DeflectedShape)
        {
            var displacements = result.Joints.ToDictionary(joint => joint.PointName, StringComparer.Ordinal);
            var maximum = result.Joints.Select(joint => Math.Sqrt(joint.UXM * joint.UXM + joint.UZM * joint.UZM)).DefaultIfEmpty().Max();
            if (!double.IsFinite(maximum) || maximum <= 1e-12) return;
            var scale = 42 / maximum;
            var pen = new Pen(Brush("AccentBright"), 2) { DashStyle = DashStyles.Dash };
            var meshedFrames = result.Frames.Where(frame => frame.Stations is { Count: > 2 })
                .ToDictionary(frame => frame.MemberName, frame => frame.Stations!, StringComparer.Ordinal);
            foreach (var member in Model.Members)
            {
                if (!screenPoints.TryGetValue(member.Start, out var start) || !screenPoints.TryGetValue(member.End, out var end) ||
                    !displacements.TryGetValue(member.Start, out var startDisplacement) || !displacements.TryGetValue(member.End, out var endDisplacement)) continue;
                if (meshedFrames.TryGetValue(member.Name, out var stations))
                {
                    // Curved deflected shape through the recovered mesh-node displacements.
                    var previous = (Point?)null;
                    foreach (var station in stations)
                    {
                        var deformed = start + (end - start) * station.Fraction + new Vector(station.UXM, -station.UZM) * scale;
                        if (previous is { } from) dc.DrawLine(pen, from, deformed);
                        previous = deformed;
                    }
                    continue;
                }
                var deformedStart = start + new Vector(startDisplacement.UXM, -startDisplacement.UZM) * scale;
                var deformedEnd = end + new Vector(endDisplacement.UXM, -endDisplacement.UZM) * scale;
                dc.DrawLine(pen, deformedStart, deformedEnd);
            }
            var maxJoint = result.Joints.MaxBy(joint => Math.Sqrt(joint.UXM * joint.UXM + joint.UZM * joint.UZM));
            if (maxJoint is not null && screenPoints.TryGetValue(maxJoint.PointName, out var maxPoint))
            {
                var deformed = maxPoint + new Vector(maxJoint.UXM, -maxJoint.UZM) * scale;
                CableLabel(dc, $"max {Math.Sqrt(maxJoint.UXM * maxJoint.UXM + maxJoint.UZM * maxJoint.UZM) * 1000:0.##} mm", deformed, true);
            }
            return;
        }

        if (mode == FemResultMode.Reactions)
        {
            // Horizontal (Rx) and vertical (Rz) components drawn separately, each arrow pointing in the direction the
            // support pushes on the structure, with its head at the support. Arrow length scales with the largest
            // component of all supports.
            var maximum = result.SupportReactions.SelectMany(reaction => new[] { Math.Abs(reaction.ReactionXKN), Math.Abs(reaction.ReactionZKN) })
                .DefaultIfEmpty().Max();
            if (!double.IsFinite(maximum) || maximum <= 1e-12) return;
            var brush = Brush("ChordBottom");
            foreach (var reaction in result.SupportReactions)
            {
                if (!screenPoints.TryGetValue(reaction.PointName, out var point)) continue;
                if (Math.Abs(reaction.ReactionZKN) > 1e-9)
                {
                    // Below the pin symbol: an upward reaction points up into the support.
                    var length = 18 + 28 * Math.Abs(reaction.ReactionZKN) / maximum;
                    var up = reaction.ReactionZKN > 0;
                    var head = point + new Vector(0, up ? 20 : 20 + length);
                    var tail = point + new Vector(0, up ? 20 + length : 20);
                    DrawResultArrow(dc, tail, head, brush);
                    ValueLabel(dc, $"Rz {reaction.ReactionZKN:0} kN", point + new Vector(0, 20 + length + 9), "Text");
                }
                if (Math.Abs(reaction.ReactionXKN) > 1e-9)
                {
                    // Beside the pin: tail on the side the reaction comes from, head at the joint.
                    var length = 18 + 28 * Math.Abs(reaction.ReactionXKN) / maximum;
                    var direction = reaction.ReactionXKN > 0 ? 1 : -1;
                    var head = point + new Vector(-direction * 12, 8);
                    var tail = head + new Vector(-direction * length, 0);
                    DrawResultArrow(dc, tail, head, brush);
                    ValueLabel(dc, $"Rx {reaction.ReactionXKN:0} kN", tail + new Vector(-direction * 34, 0), "Text");
                }
            }
            return;
        }

        static double StationValue(FemFrameStation station, FemResultMode mode) => mode switch
        {
            FemResultMode.AxialForce => station.AxialKN,
            FemResultMode.BendingMoment => station.MomentKNm,
            _ => station.ShearKN
        };
        var frameValues = result.Frames.SelectMany(frame => (mode switch
        {
            FemResultMode.AxialForce => new[] { frame.AxialStartKN, frame.AxialEndKN },
            FemResultMode.BendingMoment => new[] { frame.MomentStartKNm, frame.MomentEndKNm },
            FemResultMode.Shear => new[] { frame.ShearStartKN, frame.ShearEndKN },
            _ => Array.Empty<double>()
        }).Concat(mode is FemResultMode.AxialForce or FemResultMode.BendingMoment or FemResultMode.Shear
            ? frame.Stations?.Select(station => StationValue(station, mode)) ?? []
            : [])).ToArray();
        var maximumFrame = frameValues.Select(Math.Abs).DefaultIfEmpty().Max();
        if (maximumFrame > 1e-12)
        {
            var frameByName = result.Frames.ToDictionary(frame => frame.MemberName, StringComparer.Ordinal);
            var diagramWidth = Math.Clamp(Math.Min(ActualWidth, ActualHeight) * 0.055, 18, 52);
            var scale = diagramWidth / maximumFrame;
            foreach (var member in Model.Members.Where(member => member.Kind == MemberKind.Frame))
            {
                if (!frameByName.TryGetValue(member.Name, out var frame) ||
                    !screenPoints.TryGetValue(member.Start, out var start) || !screenPoints.TryGetValue(member.End, out var end)) continue;
                if (frame.Stations is { Count: > 2 } stations)
                {
                    // Piecewise diagram over the frame mesh stations.
                    for (var index = 1; index < stations.Count; index++)
                        DrawResultDiagram(dc, start + (end - start) * stations[index - 1].Fraction, start + (end - start) * stations[index].Fraction,
                            StationValue(stations[index - 1], mode), StationValue(stations[index], mode), scale);
                    continue;
                }
                var (valueI, valueJ) = mode switch
                {
                    FemResultMode.AxialForce => (frame.AxialStartKN, frame.AxialEndKN),
                    FemResultMode.BendingMoment => (frame.MomentStartKNm, frame.MomentEndKNm),
                    _ => (frame.ShearStartKN, frame.ShearEndKN)
                };
                DrawResultDiagram(dc, start, end, valueI, valueJ, scale);
            }
        }

        if (mode == FemResultMode.AxialForce)
        {
            var cableByName = result.Cables.ToDictionary(cable => cable.MemberName, StringComparer.Ordinal);
            foreach (var member in Model.Members.Where(member => member.Kind == MemberKind.Cable))
            {
                if (!cableByName.TryGetValue(member.Name, out var cable) || !screenPoints.TryGetValue(member.Start, out var start) ||
                    !screenPoints.TryGetValue(member.End, out var end)) continue;
                var pen = new Pen(Brush(cable.Active ? "AccentBright" : "Muted"), 2.5)
                { DashStyle = cable.Active ? DashStyles.Solid : DashStyles.Dash };
                dc.DrawLine(pen, start, end);
            }

            if (ShowAxialValues)
            {
                // Frame axial force per piece (a frame split at intermediate joints can change force at each joint).
                var frameByName = result.Frames.ToDictionary(frame => frame.MemberName, StringComparer.Ordinal);
                foreach (var member in Model.Members.Where(member => member.Kind == MemberKind.Frame))
                {
                    if (!frameByName.TryGetValue(member.Name, out var frame) ||
                        !screenPoints.TryGetValue(member.Start, out var start) || !screenPoints.TryGetValue(member.End, out var end)) continue;
                    foreach (var (fraction, axial) in AxialPieces(frame))
                        ValueLabel(dc, $"{axial:0} kN", start + (end - start) * fraction, axial >= 0 ? "Accent" : "Error");
                }
                // Cable tensions last so they sit on top; placed 65% along from the I-end, away from shared anchors.
                foreach (var member in Model.Members.Where(member => member.Kind == MemberKind.Cable))
                    if (cableByName.TryGetValue(member.Name, out var cable) && screenPoints.TryGetValue(member.Start, out var start) &&
                        screenPoints.TryGetValue(member.End, out var end))
                        ValueLabel(dc, $"T {cable.AxialForceKN:0} kN", start + (end - start) * 0.65, cable.Active ? "AccentBright" : "Muted");
            }
        }
    }

    /// <summary>Mid-fraction and mean axial force of each piece of a frame; pieces start where a station repeats.</summary>
    private static IEnumerable<(double Fraction, double AxialKN)> AxialPieces(FemFrameResponse frame)
    {
        if (frame.Stations is not { Count: > 1 } stations)
        {
            yield return (0.5, (frame.AxialStartKN + frame.AxialEndKN) / 2);
            yield break;
        }
        var first = 0;
        for (var index = 1; index <= stations.Count; index++)
        {
            if (index < stations.Count && stations[index].Fraction > stations[index - 1].Fraction) continue;
            var piece = stations.Skip(first).Take(index - first).ToArray();
            yield return ((piece[0].Fraction + piece[^1].Fraction) / 2, piece.Average(station => station.AxialKN));
            first = index;
        }
    }

    private void ValueLabel(DrawingContext dc, string text, Point center, string brush)
    {
        var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Consolas"), 10, Brush(brush), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var origin = new Point(center.X - label.Width / 2, center.Y - label.Height / 2);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)), new Pen(Brush("Border"), 1),
            new Rect(origin.X - 3, origin.Y - 1, label.Width + 6, label.Height + 2), 2, 2);
        dc.DrawText(label, origin);
    }

    private static void DrawResultDiagram(DrawingContext dc, Point start, Point end, double valueI, double valueJ, double scale)
    {
        var direction = end - start;
        if (direction.Length < 1) return;
        var normal = new Vector(-direction.Y, direction.X);
        normal.Normalize();
        if (valueI * valueJ < 0)
        {
            var zeroFraction = Math.Abs(valueI) / (Math.Abs(valueI) + Math.Abs(valueJ));
            var zero = start + direction * zeroFraction;
            DrawResultDiagramSegment(dc, start, zero, normal, valueI, 0, scale, valueI >= 0);
            DrawResultDiagramSegment(dc, zero, end, normal, 0, valueJ, scale, valueJ >= 0);
            return;
        }
        DrawResultDiagramSegment(dc, start, end, normal, valueI, valueJ, scale, (valueI + valueJ) >= 0);
    }

    private static void DrawResultDiagramSegment(DrawingContext dc, Point start, Point end, Vector normal,
        double valueI, double valueJ, double scale, bool positive)
    {
        var outerI = start + normal * valueI * scale;
        var outerJ = end + normal * valueJ * scale;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, true, true);
            context.LineTo(end, true, false);
            context.LineTo(outerJ, true, false);
            context.LineTo(outerI, true, false);
        }
        var brush = positive ? new SolidColorBrush(Color.FromRgb(0x1F, 0x47, 0x88)) : new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
        var fill = new SolidColorBrush(Color.FromArgb(218, positive ? (byte)0x1F : (byte)0xC6, positive ? (byte)0x47 : (byte)0x28, positive ? (byte)0x88 : (byte)0x28));
        dc.DrawGeometry(fill, new Pen(brush, 2), geometry);
    }

    private static void DrawResultArrow(DrawingContext dc, Point start, Point end, Brush brush)
    {
        var pen = new Pen(brush, 2.2);
        dc.DrawLine(pen, start, end);
        var direction = end - start;
        if (direction.Length <= 1e-12) return;
        direction.Normalize();
        var normal = new Vector(-direction.Y, direction.X);
        dc.DrawLine(pen, end, end - direction * 8 + normal * 4);
        dc.DrawLine(pen, end, end - direction * 8 - normal * 4);
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

    private void DrawCable(DrawingContext dc, string name, Point start, Point end, bool selected)
    {
        if (selected)
            dc.DrawLine(new Pen(Brush("SelectedBorder"), 7), start, end);

        var pen = new Pen(Brush(selected ? "Accent" : "AccentBright"), selected ? 3.5 : 2.5)
        {
            DashStyle = DashStyles.Dash
        };
        dc.DrawLine(pen, start, end);

        var direction = end - start;
        var length = direction.Length;
        var normal = length > 0.001 ? new Vector(-direction.Y / length, direction.X / length) : new Vector(0, -1);
        CableLabel(dc, name, start + direction * 0.5 + normal * 10, selected);
    }

    private void CableLabel(DrawingContext dc, string text, Point point, bool selected)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var brush = Brush(selected ? "Accent" : "Text");
        var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Consolas"), 10, brush, dpi);
        var bounds = new Rect(point.X - 3, point.Y - 2, label.Width + 6, label.Height + 4);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)),
            new Pen(Brush(selected ? "AccentBright" : "Border"), 1), bounds, 2, 2);
        dc.DrawText(label, point);
    }

    private void DrawAxes(DrawingContext dc)
    {
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
    }

    private static Brush AxisBrush(char axis) => axis switch
    {
        'X' => new SolidColorBrush(Color.FromArgb(220, 198, 40, 40)),
        'Y' => new SolidColorBrush(Color.FromArgb(220, 37, 99, 235)),
        'Z' => new SolidColorBrush(Color.FromArgb(220, 46, 125, 50)),
        _ => Brushes.DimGray
    };

    private void Text(DrawingContext dc, string text, Point point, string brush) => Text(dc, text, point, Brush(brush));
    private void Text(DrawingContext dc, string text, Point point, Brush brush) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), point);

    /// <summary>A frame drawn as its mesh: segments separated by small gaps with a tick at each mesh node.</summary>
    private void DrawMeshedFrame(DrawingContext dc, Pen pen, Point start, Point end, int segments)
    {
        var direction = end - start;
        var length = direction.Length;
        if (length < 1) return;
        var unit = direction / length;
        var normal = new Vector(-unit.Y, unit.X);
        var gap = Math.Min(1.5, length / segments * 0.2);
        var tick = new Pen(Brush("ViewportEdge"), 1);
        for (var segment = 0; segment < segments; segment++)
        {
            var a = start + direction * ((double)segment / segments);
            var b = start + direction * ((double)(segment + 1) / segments);
            dc.DrawLine(pen, a + unit * gap, b - unit * gap);
            if (segment > 0) dc.DrawLine(tick, a - normal * 3, a + normal * 3);
        }
    }

    // Frame / cable name drawn along the member, rotated to the element's screen-space direction and kept upright.
    private void MemberLabel(DrawingContext dc, string text, Point a, Point b) => MemberLabel(dc, text, a, b, 9, "Text");

    private void MemberLabel(DrawingContext dc, string text, Point a, Point b, double offset, string brush)
    {
        var dir = b - a;
        if (dir.Length < 8) return;
        var angle = Math.Atan2(dir.Y, dir.X) * 180 / Math.PI;
        if (angle > 90) angle -= 180;
        else if (angle < -90) angle += 180;
        var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        var normal = new Vector(-dir.Y, dir.X);
        if (normal.Length > 0) normal.Normalize();
        var anchor = mid + normal * offset;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Consolas"), 9.5, Brush(brush), dpi);
        var origin = new Point(anchor.X - label.Width / 2, anchor.Y - label.Height / 2);
        dc.PushTransform(new RotateTransform(angle, anchor.X, anchor.Y));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), null,
            new Rect(origin.X - 2, origin.Y - 1, label.Width + 4, label.Height + 2));
        dc.DrawText(label, origin);
        dc.Pop();
    }

    private void Label(DrawingContext dc, string text, Point point)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brush("Accent"), dpi);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(185, 255, 255, 255)), null, new Rect(point.X - 2, point.Y - 1, label.Width + 4, label.Height + 2));
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
        var cableHit = cableLocations
            .Where(item => DistanceToSegment(position, item.Value.Start, item.Value.End) <= 8)
            .OrderBy(item => DistanceToSegment(position, item.Value.Start, item.Value.End))
            .Select(item => item.Key)
            .FirstOrDefault();
        if (cableHit is not null)
        {
            SelectedCable = cableHit;
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

    private static double DistanceToSegment(Point point, Point start, Point end)
    {
        var segment = end - start;
        var lengthSquared = segment.X * segment.X + segment.Y * segment.Y;
        if (lengthSquared <= double.Epsilon) return (point - start).Length;
        var fromStart = point - start;
        var t = Math.Clamp((fromStart.X * segment.X + fromStart.Y * segment.Y) / lengthSquared, 0, 1);
        return (point - (start + segment * t)).Length;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Home) Fit();
        else if (e.Key == Key.Add) zoom = Math.Min(100, zoom * 1.15);
        else if (e.Key == Key.Subtract) zoom = Math.Max(.05, zoom / 1.15);
        InvalidateVisual();
    }
}
