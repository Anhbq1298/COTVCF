using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using COT_VCF.Application;
using COT_VCF.Domain;
using COT_VCF.Geometry;

namespace COT_VCF.Presentation.Controls;

public partial class Structural2DViewer : UserControl
{
    public Structural2DViewer()
    {
        InitializeComponent();
        Canvas.SelectionChanged += (_, _) => Update3DSelection();
    }
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(GeometryModel), typeof(Structural2DViewer), new PropertyMetadata(GeometryModel.Empty));
    public static readonly DependencyProperty ToolbarContentProperty = DependencyProperty.Register(nameof(ToolbarContent), typeof(object), typeof(Structural2DViewer), new PropertyMetadata(null));
    public static readonly DependencyProperty ShowPointLabelsProperty = DependencyProperty.Register(nameof(ShowPointLabels), typeof(bool), typeof(Structural2DViewer), new PropertyMetadata(false));
    public static readonly DependencyProperty ShowFrameLabelsProperty = DependencyProperty.Register(nameof(ShowFrameLabels), typeof(bool), typeof(Structural2DViewer), new PropertyMetadata(false));
    public static readonly DependencyProperty ShowCableLabelsProperty = DependencyProperty.Register(nameof(ShowCableLabels), typeof(bool), typeof(Structural2DViewer), new PropertyMetadata(false));
    public static readonly DependencyProperty ShowDimensionsProperty = DependencyProperty.Register(nameof(ShowDimensions), typeof(bool), typeof(Structural2DViewer), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty SpanPointsProperty = DependencyProperty.Register(nameof(SpanPoints), typeof(IEnumerable<string>), typeof(Structural2DViewer), new PropertyMetadata(null));
    /// <summary>Pan mode: the left button moves the model; off (default), a click selects joints and members as in SAP2000.</summary>
    public static readonly DependencyProperty PanModeProperty = DependencyProperty.Register(nameof(PanMode), typeof(bool), typeof(Structural2DViewer),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public bool PanMode { get => (bool)GetValue(PanModeProperty); set => SetValue(PanModeProperty, value); }
    public void ClearSelection() => Canvas.ClearSelection();
    public IReadOnlyList<string> SelectedMembers => Canvas.SelectedMembers;
    public IReadOnlyList<string> SelectedJoints => Canvas.SelectedJoints;
    /// <summary>Raised when the selected joints or members change.</summary>
    public event EventHandler? SelectionChanged { add => Canvas.SelectionChanged += value; remove => Canvas.SelectionChanged -= value; }
    /// <summary>The truss chord points: the overall span dimension runs between their outermost lines (the mega columns).</summary>
    public IEnumerable<string>? SpanPoints { get => (IEnumerable<string>?)GetValue(SpanPointsProperty); set => SetValue(SpanPointsProperty, value); }
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
    /// <summary>Dimension the model: span of each bay and overall span below it, height of each level and node elevations beside it.</summary>
    public bool ShowDimensions { get => (bool)GetValue(ShowDimensionsProperty); set => SetValue(ShowDimensionsProperty, value); }
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
    public static readonly DependencyProperty MemberNotesProperty = DependencyProperty.Register(nameof(MemberNotes), typeof(IReadOnlyDictionary<string, string>), typeof(Structural2DViewer), new PropertyMetadata(null));
    public static readonly DependencyProperty HighlightedMembersProperty = DependencyProperty.Register(nameof(HighlightedMembers), typeof(IEnumerable<string>), typeof(Structural2DViewer), new PropertyMetadata(null));
    public static readonly DependencyProperty JointNotesProperty = DependencyProperty.Register(nameof(JointNotes), typeof(IReadOnlyDictionary<string, string>), typeof(Structural2DViewer), new PropertyMetadata(null));
    /// <summary>Member name → a note drawn along the member (e.g. a cable's pretension range).</summary>
    public IReadOnlyDictionary<string, string>? MemberNotes { get => (IReadOnlyDictionary<string, string>?)GetValue(MemberNotesProperty); set => SetValue(MemberNotesProperty, value); }
    /// <summary>Point name → a note drawn below the joint, reading upward (e.g. the pretension of the fan cable anchored there:
    /// the anchors are a bay apart, where notes along the cables would run into each other at the corner).</summary>
    public IReadOnlyDictionary<string, string>? JointNotes { get => (IReadOnlyDictionary<string, string>?)GetValue(JointNotesProperty); set => SetValue(JointNotesProperty, value); }
    /// <summary>Members drawn emphasised (e.g. the cables a variant adds to the model).</summary>
    public IEnumerable<string>? HighlightedMembers { get => (IEnumerable<string>?)GetValue(HighlightedMembersProperty); set => SetValue(HighlightedMembersProperty, value); }
    public static readonly DependencyProperty ShowAxialValuesProperty = DependencyProperty.Register(nameof(ShowAxialValues), typeof(bool), typeof(Structural2DViewer), new PropertyMetadata(true));
    /// <summary>Label frame N/V/M values and cable tensions in the active result view.</summary>
    public bool ShowAxialValues { get => (bool)GetValue(ShowAxialValuesProperty); set => SetValue(ShowAxialValuesProperty, value); }

    public static readonly DependencyProperty MemberSectionsProperty = DependencyProperty.Register(nameof(MemberSections),
        typeof(IReadOnlyDictionary<string, FemSectionProperties>), typeof(Structural2DViewer),
        new PropertyMetadata(null, (d, _) => ((Structural2DViewer)d).Rebuild3D(keepCamera: true)));
    /// <summary>SAP section data per member (shape, local-axis angle, area) for the to-scale 3D view.</summary>
    public IReadOnlyDictionary<string, FemSectionProperties>? MemberSections
    {
        get => (IReadOnlyDictionary<string, FemSectionProperties>?)GetValue(MemberSectionsProperty);
        set => SetValue(MemberSectionsProperty, value);
    }

    public static readonly DependencyProperty ShowUndeformedProperty = DependencyProperty.Register(nameof(ShowUndeformed), typeof(bool), typeof(Structural2DViewer), new PropertyMetadata(true));
    public bool ShowUndeformed { get => (bool)GetValue(ShowUndeformedProperty); set => SetValue(ShowUndeformedProperty, value); }
    public static readonly DependencyProperty ShowDeflectionContourProperty = DependencyProperty.Register(nameof(ShowDeflectionContour), typeof(bool), typeof(Structural2DViewer), new PropertyMetadata(true));
    public bool ShowDeflectionContour { get => (bool)GetValue(ShowDeflectionContourProperty); set => SetValue(ShowDeflectionContourProperty, value); }
    public static readonly DependencyProperty DeflectionRangeMinMmProperty = DependencyProperty.Register(nameof(DeflectionRangeMinMm), typeof(double), typeof(Structural2DViewer), new PropertyMetadata(0.0));
    public double DeflectionRangeMinMm { get => (double)GetValue(DeflectionRangeMinMmProperty); set => SetValue(DeflectionRangeMinMmProperty, value); }
    public static readonly DependencyProperty DeflectionRangeMaxMmProperty = DependencyProperty.Register(nameof(DeflectionRangeMaxMm), typeof(double), typeof(Structural2DViewer), new PropertyMetadata(0.0));
    public double DeflectionRangeMaxMm { get => (double)GetValue(DeflectionRangeMaxMmProperty); set => SetValue(DeflectionRangeMaxMmProperty, value); }
    private bool Is3D => View3DHost?.Visibility == Visibility.Visible;

    private void Fit(object sender, RoutedEventArgs e)
    {
        if (Is3D) Fit3D();
        else Canvas.Fit();
    }
    /// <summary>Top-bar tools: zoom extents, zoom in/out about the view centre, and the projection (XY / XZ / YZ / 3D).</summary>
    public void FitView() => Fit(this, new RoutedEventArgs());
    public void ZoomBy(double factor)
    {
        if (Is3D) { viewWidth = Math.Max(1e-3, viewWidth / factor); UpdateCamera(); }
        else Canvas.ZoomBy(factor);
    }
    public void ShowPlane(string plane)
    {
        var item = Plane.Items.OfType<ComboBoxItem>().FirstOrDefault(entry => string.Equals(entry.Content as string, plane, StringComparison.OrdinalIgnoreCase));
        if (item is not null) Plane.SelectedItem = item;
    }
    public string CurrentPlane => (Plane.SelectedItem as ComboBoxItem)?.Content as string ?? "XZ";

    private void ResetView(object sender, RoutedEventArgs e)
    {
        Plane.SelectedIndex = 1; // XZ, the workflow default
        Canvas.Plane = "XZ";
        Canvas.Fit();
    }
    private void PlaneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Canvas is null) return;
        var plane = ((ComboBoxItem)Plane.SelectedItem).Content.ToString()!;
        var show3D = plane == "3D";
        View3DHost.Visibility = show3D ? Visibility.Visible : Visibility.Collapsed;
        Canvas.Visibility = show3D ? Visibility.Collapsed : Visibility.Visible;
        if (show3D)
        {
            azimuthDeg = -45; elevationDeg = 35.264; // true isometric
            Rebuild3D(keepCamera: false);
            return;
        }
        Canvas.Plane = plane;
        Canvas.Fit();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ModelProperty) Rebuild3D(keepCamera: false);
    }

    // ---- 3D view: orthographic camera orbiting the model centre ----
    private double azimuthDeg = -45, elevationDeg = 35.264, viewWidth = 10;
    private Point3D target;
    private Point? dragStart;
    private Point? dragOrigin;
    private bool panning;
    private StructureModel3D.Scene? scene3D;
    private static readonly Material Selected3DMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x00)));

    private void Rebuild3D(bool keepCamera)
    {
        if (!Is3D || Structure3D is null) return;
        var scene = StructureModel3D.Build(Model, MemberSections);
        scene3D = scene;
        Structure3D.Content = scene.Model;
        Update3DSelection();
        View3DInfo.Text = MemberSections is null
            ? "Isometric 3D · member sizes not read — use Read from SAP (FEM → Inputs) for to-scale sections · drag rotate · right-drag pan · wheel zoom · double-click fit"
            : $"Isometric 3D · to scale · {scene.FramesWithSapShape} frames with SAP section dimensions" +
              (scene.FramesApproximated > 0 ? $", {scene.FramesApproximated} as equivalent solid (same A, I)" : "") +
              " · click to select · drag rotate · right-drag pan · wheel zoom · double-click fit";
        if (!keepCamera || scene.Bounds.IsEmpty) Fit3D();
        else UpdateCamera();
    }

    private void Fit3D()
    {
        if (Structure3D?.Content is not Model3D model || model.Bounds.IsEmpty) { UpdateCamera(); return; }
        var bounds = model.Bounds;
        target = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        var diagonal = Math.Sqrt(bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ);
        // The orthographic camera width is horizontal; the visible height is width / aspect. Fit the bounding-sphere
        // diameter in both directions.
        var aspect = View3DHost.ActualHeight > 0 ? View3DHost.ActualWidth / View3DHost.ActualHeight : 1.6;
        viewWidth = Math.Max(1e-3, diagonal * 1.08 * Math.Max(1, aspect));
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        if (Camera3D is null) return;
        var azimuth = azimuthDeg * Math.PI / 180;
        var elevation = elevationDeg * Math.PI / 180;
        // Looking from (cos az·cos el, sin az·cos el, sin el) towards the target; Z stays up.
        var from = new Vector3D(Math.Cos(azimuth) * Math.Cos(elevation), Math.Sin(azimuth) * Math.Cos(elevation), Math.Sin(elevation));
        Camera3D.Position = target + from * viewWidth * 10;
        Camera3D.LookDirection = -from;
        Camera3D.Width = viewWidth;
    }

    private void View3DMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Fit3D(); return; }
        dragStart = e.GetPosition(View3DHost);
        dragOrigin = dragStart;
        panning = e.ChangedButton == MouseButton.Right || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || PanMode;
        View3DHost.CaptureMouse();
    }

    private void View3DMouseMove(object sender, MouseEventArgs e)
    {
        if (dragStart is not { } start || !View3DHost.IsMouseCaptured) return;
        var now = e.GetPosition(View3DHost);
        if (dragOrigin is { } origin && (now - origin).Length <= 4) return;
        var delta = now - start;
        dragStart = now;
        if (panning)
        {
            // Move the target in the camera's screen plane.
            var look = Camera3D.LookDirection; look.Normalize();
            var right = Vector3D.CrossProduct(look, new Vector3D(0, 0, 1));
            if (right.Length < 1e-9) right = new Vector3D(1, 0, 0);
            right.Normalize();
            var up = Vector3D.CrossProduct(right, look);
            var metresPerPixel = viewWidth / Math.Max(1, View3DHost.ActualWidth);
            target += (-right * delta.X + up * delta.Y) * metresPerPixel;
        }
        else
        {
            azimuthDeg -= delta.X * 0.4;
            elevationDeg = Math.Clamp(elevationDeg + delta.Y * 0.4, -89, 89);
        }
        UpdateCamera();
    }

    private void View3DMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && !panning && dragOrigin is { } origin &&
            (e.GetPosition(View3DHost) - origin).Length <= 4 && scene3D is { } scene)
        {
            string? hit = null;
            VisualTreeHelper.HitTest(View3D, null, result =>
            {
                if (result is RayMeshGeometry3DHitTestResult { ModelHit: GeometryModel3D model } &&
                    scene.MemberVisuals.FirstOrDefault(item => ReferenceEquals(item.Model, model)) is { } member)
                {
                    hit = member.Name;
                    return HitTestResultBehavior.Stop;
                }
                return HitTestResultBehavior.Continue;
            }, new PointHitTestParameters(e.GetPosition(View3D)));
            if (hit is not null) Canvas.ToggleMemberSelection(hit);
        }
        dragStart = null;
        dragOrigin = null;
        if (View3DHost.IsMouseCaptured) View3DHost.ReleaseMouseCapture();
    }

    private void Update3DSelection()
    {
        if (scene3D is null) return;
        var selected = Canvas.SelectedMembers.ToHashSet(StringComparer.Ordinal);
        foreach (var member in scene3D.MemberVisuals)
            member.Model.Material = member.Model.BackMaterial = selected.Contains(member.Name) ? Selected3DMaterial : member.BaseMaterial;
    }

    private void View3DMouseWheel(object sender, MouseWheelEventArgs e)
    {
        viewWidth = Math.Max(1e-3, viewWidth * (e.Delta > 0 ? 1 / 1.15 : 1.15));
        UpdateCamera();
        e.Handled = true;
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
    public static readonly DependencyProperty MemberNotesProperty = DependencyProperty.Register(nameof(MemberNotes), typeof(IReadOnlyDictionary<string, string>), typeof(ModelCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HighlightedMembersProperty = DependencyProperty.Register(nameof(HighlightedMembers), typeof(IEnumerable<string>), typeof(ModelCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty JointNotesProperty = DependencyProperty.Register(nameof(JointNotes), typeof(IReadOnlyDictionary<string, string>), typeof(ModelCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyDictionary<string, string>? JointNotes { get => (IReadOnlyDictionary<string, string>?)GetValue(JointNotesProperty); set => SetValue(JointNotesProperty, value); }
    public IReadOnlyDictionary<string, string>? MemberNotes { get => (IReadOnlyDictionary<string, string>?)GetValue(MemberNotesProperty); set => SetValue(MemberNotesProperty, value); }
    public IEnumerable<string>? HighlightedMembers { get => (IEnumerable<string>?)GetValue(HighlightedMembersProperty); set => SetValue(HighlightedMembersProperty, value); }
    public static readonly DependencyProperty ShowAxialValuesProperty = DependencyProperty.Register(nameof(ShowAxialValues), typeof(bool), typeof(ModelCanvas), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool ShowAxialValues { get => (bool)GetValue(ShowAxialValuesProperty); set => SetValue(ShowAxialValuesProperty, value); }
    public static readonly DependencyProperty ShowUndeformedProperty = DependencyProperty.Register(nameof(ShowUndeformed), typeof(bool), typeof(ModelCanvas), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool ShowUndeformed { get => (bool)GetValue(ShowUndeformedProperty); set => SetValue(ShowUndeformedProperty, value); }
    public static readonly DependencyProperty ShowDeflectionContourProperty = DependencyProperty.Register(nameof(ShowDeflectionContour), typeof(bool), typeof(ModelCanvas), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool ShowDeflectionContour { get => (bool)GetValue(ShowDeflectionContourProperty); set => SetValue(ShowDeflectionContourProperty, value); }
    public static readonly DependencyProperty DeflectionRangeMinMmProperty = DependencyProperty.Register(nameof(DeflectionRangeMinMm), typeof(double), typeof(ModelCanvas), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double DeflectionRangeMinMm { get => (double)GetValue(DeflectionRangeMinMmProperty); set => SetValue(DeflectionRangeMinMmProperty, value); }
    public static readonly DependencyProperty DeflectionRangeMaxMmProperty = DependencyProperty.Register(nameof(DeflectionRangeMaxMm), typeof(double), typeof(ModelCanvas), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double DeflectionRangeMaxMm { get => (double)GetValue(DeflectionRangeMaxMmProperty); set => SetValue(DeflectionRangeMaxMmProperty, value); }
    public static readonly DependencyProperty ShowDimensionsProperty = DependencyProperty.Register(nameof(ShowDimensions), typeof(bool), typeof(ModelCanvas), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SpanPointsProperty = DependencyProperty.Register(nameof(SpanPoints), typeof(IEnumerable<string>), typeof(ModelCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IEnumerable<string>? SpanPoints { get => (IEnumerable<string>?)GetValue(SpanPointsProperty); set => SetValue(SpanPointsProperty, value); }
    public bool ShowDimensions { get => (bool)GetValue(ShowDimensionsProperty); set => SetValue(ShowDimensionsProperty, value); }
    public string Plane { get; set; } = "XZ";

    private readonly Dictionary<AppliedPoint, Point> appliedPointLocations = [];
    private readonly Dictionary<CablePretensionConfig, (Point Start, Point End)> cableLocations = [];
    private double zoom = 1;
    private Vector pan;
    private Point? last;

    // ---- selection, as SAP2000: a click selects (or deselects) the joint / member under the cursor; a drag on empty space
    // draws a window — left to right selects what lies wholly inside, right to left also what it crosses. Pan mode (or the
    // middle button) moves the model instead. Esc clears the selection and leaves pan mode.
    public static readonly DependencyProperty PanModeProperty = DependencyProperty.Register(nameof(PanMode), typeof(bool), typeof(ModelCanvas),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ModelCanvas)d).UpdateCursor()));
    public bool PanMode { get => (bool)GetValue(PanModeProperty); set => SetValue(PanModeProperty, value); }

    private readonly HashSet<string> selectedMembers = new(StringComparer.Ordinal);
    private readonly HashSet<string> selectedJoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Point Start, Point End, MemberKind Kind)> memberLocations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Point> jointLocations = new(StringComparer.Ordinal);
    private Point? windowStart, windowEnd;
    private bool panning;

    /// <summary>The selected members and joints (names), in the order of the model.</summary>
    public IReadOnlyList<string> SelectedMembers => Model.Members.Select(member => member.Name).Where(selectedMembers.Contains).ToArray();
    public IReadOnlyList<string> SelectedJoints => Model.Points.Keys.Where(selectedJoints.Contains).ToArray();

    /// <summary>Where each joint was last drawn (checks).</summary>
    internal IReadOnlyDictionary<string, Point> JointScreenPoints => jointLocations;
    internal IReadOnlyDictionary<string, (Point Start, Point End, MemberKind Kind)> MemberScreenLines => memberLocations;

    /// <summary>Raised when the selected joints or members change: a click, a window, a clear.</summary>
    public event EventHandler? SelectionChanged;

    public void ClearSelection()
    {
        var any = selectedMembers.Count > 0 || selectedJoints.Count > 0;
        selectedMembers.Clear();
        selectedJoints.Clear();
        InvalidateVisual();
        if (any) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Toggles a member selected in the 3D view using the same selection as the 2D canvas.</summary>
    public void ToggleMemberSelection(string name)
    {
        if (!Model.Members.Any(member => member.Name == name)) return;
        Toggle(selectedMembers, name);
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects (or deselects) what lies at <paramref name="position"/>, as a click does (checks).</summary>
    public bool SelectAt(Point position)
    {
        var joint = jointLocations.Where(item => (item.Value - position).Length <= 6).OrderBy(item => (item.Value - position).Length).Select(item => item.Key).FirstOrDefault();
        if (joint is not null) { Toggle(selectedJoints, joint); InvalidateVisual(); SelectionChanged?.Invoke(this, EventArgs.Empty); return true; }
        var member = memberLocations.Where(item => DistanceToSegment(position, item.Value.Start, item.Value.End) <= 5)
            .OrderBy(item => Math.Round(DistanceToSegment(position, item.Value.Start, item.Value.End), 1)).ThenBy(item => item.Value.Kind != MemberKind.Frame)
            .Select(item => item.Key).FirstOrDefault();
        if (member is not null) { Toggle(selectedMembers, member); InvalidateVisual(); SelectionChanged?.Invoke(this, EventArgs.Empty); return true; }
        return false;
    }

    /// <summary>Window (left to right: wholly inside) or crossing (right to left: touching) selection between two screen points.</summary>
    public void SelectWindow(Point from, Point to)
    {
        var box = new Rect(from, to);
        var crossing = to.X < from.X;
        foreach (var (name, point) in jointLocations) if (box.Contains(point)) selectedJoints.Add(name);
        foreach (var (name, (start, end, _)) in memberLocations)
            if (box.Contains(start) && box.Contains(end) || crossing && SegmentTouches(box, start, end)) selectedMembers.Add(name);
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void Toggle(HashSet<string> set, string name) { if (!set.Remove(name)) set.Add(name); }

    private static bool SegmentTouches(Rect box, Point a, Point b)
    {
        if (box.Contains(a) || box.Contains(b)) return true;
        Point[] corners = [box.TopLeft, box.TopRight, box.BottomRight, box.BottomLeft];
        for (var i = 0; i < 4; i++) if (Intersects(a, b, corners[i], corners[(i + 1) % 4])) return true;
        return false;
        static double Cross(Point o, Point p, Point q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
        static bool Intersects(Point p1, Point p2, Point q1, Point q2) =>
            Cross(p1, p2, q1) * Cross(p1, p2, q2) <= 0 && Cross(q1, q2, p1) * Cross(q1, q2, p2) <= 0;
    }

    private void UpdateCursor() { Cursor = PanMode ? Cursors.SizeAll : Cursors.Arrow; InvalidateVisual(); }

    private void DrawSelection(DrawingContext dc)
    {
        var highlight = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x00)), 5) { DashStyle = DashStyles.Dash, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        foreach (var name in selectedMembers)
            if (memberLocations.TryGetValue(name, out var location)) dc.DrawLine(highlight, location.Start, location.End);
        var cross = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x00)), 2.5);
        foreach (var name in selectedJoints)
            if (jointLocations.TryGetValue(name, out var point))
            {
                dc.DrawLine(cross, point + new Vector(-6, -6), point + new Vector(6, 6));
                dc.DrawLine(cross, point + new Vector(-6, 6), point + new Vector(6, -6));
            }
        if (windowStart is { } from && windowEnd is { } to)
        {
            var crossing = to.X < from.X;
            dc.DrawRectangle(new SolidColorBrush(crossing ? Color.FromArgb(28, 0x2E, 0xA0, 0x40) : Color.FromArgb(28, 0x2E, 0x90, 0xFA)),
                new Pen(crossing ? Brushes.SeaGreen : Brushes.RoyalBlue, 1) { DashStyle = crossing ? DashStyles.Dash : DashStyles.Solid }, new Rect(from, to));
        }
        if (selectedMembers.Count + selectedJoints.Count == 0) return;
        // status read-out, as SAP2000's "n Points, n Frames selected" (with the section of a single member)
        var frames = selectedMembers.Count(name => memberLocations.TryGetValue(name, out var item) && item.Kind == MemberKind.Frame);
        var cables = selectedMembers.Count - frames;
        var parts = new List<string>();
        if (selectedJoints.Count > 0) parts.Add($"{selectedJoints.Count} Point{(selectedJoints.Count == 1 ? "" : "s")}");
        if (frames > 0) parts.Add($"{frames} Frame{(frames == 1 ? "" : "s")}");
        if (cables > 0) parts.Add($"{cables} Cable{(cables == 1 ? "" : "s")}");
        var text = string.Join(", ", parts) + " selected";
        if (selectedMembers.Count + selectedJoints.Count == 1)
            text += selectedMembers.Count == 1
                ? $" — {selectedMembers.First()}" + (MemberSizes?.TryGetValue(selectedMembers.First(), out var size) == true && !string.IsNullOrWhiteSpace(size) ? $" · {size}" : "")
                : $" — joint {selectedJoints.First()}" + (Model.Points.TryGetValue(selectedJoints.First(), out var joint)
                    ? $" (X {joint.Position.X:0.###}, Z {joint.Position.Z:0.###} m)" : "");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var label = new FormattedText(text + "   ·   Esc clears", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.Black, dpi);
        var origin = new Point(ActualWidth - label.Width - 14, ActualHeight - label.Height - 12);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(235, 255, 248, 225)), new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x00)), 1),
            new Rect(origin.X - 6, origin.Y - 3, label.Width + 12, label.Height + 6), 3, 3);
        dc.DrawText(label, origin);
    }

    public ModelCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        UpdateCursor();
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

    /// <summary>Zooms about the view centre, like the + / − keys.</summary>
    public void ZoomBy(double factor)
    {
        var old = zoom;
        zoom = Math.Clamp(zoom * factor, .05, 100);
        pan *= zoom / old;
        InvalidateVisual();
    }

    private Point Project(Position position) => Plane == "XY" ? new(position.X, position.Y) : Plane == "YZ" ? new(position.Y, position.Z) : new(position.X, position.Z);
    private Brush Brush(string name) => (Brush)FindResource(name);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        deflectedNodes.Clear();
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
            // Dimensions sit below and to the right of the model; leave room for them when fitting.
            double marginX = ShowDimensions ? 96 + DimensionBandX : 96, marginY = ShowDimensions ? 96 + DimensionBandY : 96;
            double scale = Math.Min(Math.Max(1, ActualWidth - marginX) / Math.Max(spanX, 1e-9), Math.Max(1, ActualHeight - marginY) / Math.Max(spanY, 1e-9)) * zoom;
            var shift = ShowDimensions ? new Vector(-DimensionBandX / 2, -DimensionBandY / 2) : default;
            Point Screen(Point point) => new((point.X - (minX + spanX / 2)) * scale + ActualWidth / 2 + pan.X + shift.X, -(point.Y - (minY + spanY / 2)) * scale + ActualHeight / 2 + pan.Y + shift.Y);
            var screenPoints = projected.ToDictionary(item => item.Key, item => Screen(item.Value));
            jointLocations.Clear();
            foreach (var (name, point) in screenPoints) jointLocations[name] = point;
            memberLocations.Clear();
            foreach (var member in Model.Members)
                if (screenPoints.TryGetValue(member.Start, out var a) && screenPoints.TryGetValue(member.End, out var b)) memberLocations[member.Name] = (a, b, member.Kind);
            if (ShowDimensions) DrawDimensions(dc, projected.Values, Screen);

            var deflectionView = FemResult is not null && FemResultMode == FemResultMode.DeflectedShape;
            if (!deflectionView || ShowUndeformed)
            {
             if (deflectionView) dc.PushOpacity(0.22);
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
                if (deflectionView) pen.DashStyle = DashStyles.Dash;
                else if (member.Kind != MemberKind.Frame) pen.DashStyle = member.Kind == MemberKind.Cable ? DashStyles.Dash : DashStyles.Dot;
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

            var highlighted = (HighlightedMembers ?? []).ToHashSet(StringComparer.Ordinal);
            foreach (var member in Model.Members.Where(member => highlighted.Contains(member.Name)))
                if (screenPoints.TryGetValue(member.Start, out var from) && screenPoints.TryGetValue(member.End, out var to))
                    dc.DrawLine(new Pen(Brush("Accent"), 2.5) { DashStyle = member.Kind == MemberKind.Frame ? DashStyles.Solid : DashStyles.Dash }, from, to);
            if (MemberNotes is { } notes)
            {
                var noted = Model.Members.Where(member => notes.TryGetValue(member.Name, out var note) && !string.IsNullOrWhiteSpace(note) &&
                    screenPoints.ContainsKey(member.Start) && screenPoints.ContainsKey(member.End))
                    .Select(member => (member.Name, Start: screenPoints[member.Start], End: screenPoints[member.End])).ToArray();
                static Point Middle(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                foreach (var (name, start, end) in noted)
                {
                    // two noted members that cross at their middle (an X of cables): each note moves towards its upper end
                    var crossed = noted.Any(other => other.Name != name && (Middle(other.Start, other.End) - Middle(start, end)).Length < 14);
                    var (upper, lower) = start.Y <= end.Y ? (start, end) : (end, start);
                    MemberLabel(dc, notes[name], upper, lower, MemberSizes is null ? -9 : -20, highlighted.Contains(name) ? "Accent" : "AccentBright", crossed ? 0.3 : 0.5);
                }
            }

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
                    CableLabel(dc, ResultDisplayText.Force(fz), point + new Vector(6, -62), false);
            }

            foreach (var name in PinSupportPoints ?? [])
                if (screenPoints.TryGetValue(name, out var support)) DrawPinSupport(dc, support);

            foreach (var (name, note) in JointNotes ?? new Dictionary<string, string>())
                if (!string.IsNullOrWhiteSpace(note) && screenPoints.TryGetValue(name, out var joint)) JointNote(dc, note, joint);

             if (deflectionView) dc.Pop();
            }
            if (FemResult is { } femResult)
            {
                DrawFemResultOverlay(dc, screenPoints, femResult, FemResultMode);
                if (!deflectionView) { CollectHoverTargets(screenPoints, femResult); DrawHover(dc); }
                else hoverTargets.Clear();
            }
            else hoverTargets.Clear();

            if (ShowPointLabels && (!deflectionView || ShowUndeformed))
                foreach (var item in screenPoints.OrderBy(item => item.Key))
                    Label(dc, item.Key, item.Value + new Vector(6, -18));
        }
        else Text(dc, "No geometry loaded", new(Math.Max(16, ActualWidth / 2 - 70), ActualHeight / 2), "ViewportEdge");

        DrawSelection(dc);
        DrawAxes(dc);
        Text(dc, Plane + " projection" + (PanMode ? "  ·  PAN — drag to move the model (Esc to select)" : ""), new(16, 12), "ViewportEdge");
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
            static double Magnitude(double ux, double uz) => Math.Sqrt(ux * ux + uz * uz) * 1000;
            var maximum = result.Joints.Select(joint => Magnitude(joint.UXM, joint.UZM))
                .Concat(result.Frames.SelectMany(frame => frame.Stations ?? []).Select(station => Magnitude(station.UXM, station.UZM))).DefaultIfEmpty().Max();
            if (!double.IsFinite(maximum)) return;
            var scale = maximum > 1e-9 ? 42000 / maximum : 0;
            var min = double.IsFinite(DeflectionRangeMinMm) ? Math.Max(0, DeflectionRangeMinMm) : 0;
            var max = double.IsFinite(DeflectionRangeMaxMm) && DeflectionRangeMaxMm > min ? DeflectionRangeMaxMm : Math.Max(maximum, min + 1e-9);
            Vector Offset(double ux, double uz) => Plane == "XY" ? new(ux * scale, 0) : Plane == "YZ" ? new(0, -uz * scale) : new(ux * scale, -uz * scale);
            Brush Contour(double value) => ShowDeflectionContour ? DeflectionBrush((value - min) / (max - min)) : Brush("AccentBright");
            void Segment(Point from, Point to, double v1, double v2)
            {
                var gradient = new LinearGradientBrush(((SolidColorBrush)Contour(v1)).Color, ((SolidColorBrush)Contour(v2)).Color, 0)
                { MappingMode = BrushMappingMode.Absolute, StartPoint = from, EndPoint = to };
                dc.DrawLine(new Pen(gradient, 3), from, to);
            }
            var meshedFrames = result.Frames.Where(frame => frame.Stations is { Count: > 2 })
                .ToDictionary(frame => frame.MemberName, frame => frame.Stations!, StringComparer.Ordinal);
            foreach (var member in Model.Members)
            {
                if (!screenPoints.TryGetValue(member.Start, out var start) || !screenPoints.TryGetValue(member.End, out var end) ||
                    !displacements.TryGetValue(member.Start, out var u1) || !displacements.TryGetValue(member.End, out var u2)) continue;
                if (meshedFrames.TryGetValue(member.Name, out var stations))
                {
                    for (var i = 1; i < stations.Count; i++)
                    {
                        var a = stations[i - 1]; var b = stations[i];
                        Segment(start + (end - start) * a.Fraction + Offset(a.UXM, a.UZM),
                            start + (end - start) * b.Fraction + Offset(b.UXM, b.UZM), Magnitude(a.UXM, a.UZM), Magnitude(b.UXM, b.UZM));
                    }
                }
                else Segment(start + Offset(u1.UXM, u1.UZM), end + Offset(u2.UXM, u2.UZM), Magnitude(u1.UXM, u1.UZM), Magnitude(u2.UXM, u2.UZM));
            }
            foreach (var joint in result.Joints)
                if (screenPoints.TryGetValue(joint.PointName, out var original))
                {
                    var point = original + Offset(joint.UXM, joint.UZM);
                    deflectedNodes[joint.PointName] = point;
                    dc.DrawEllipse(Contour(Magnitude(joint.UXM, joint.UZM)), new Pen(Brush("ViewportBg"), 1), point, 3.5, 3.5);
                }
            if (ShowDeflectionContour)
            {
                var left = Math.Max(18, ActualWidth - 245); var top = ActualHeight - 70;
                for (var i = 0; i < 100; i++) dc.DrawRectangle(DeflectionBrush(i / 99.0), null, new Rect(left + i * 1.6, top, 1.7, 10));
                Text(dc, $"|U| {min:G5} to {max:G5} mm", new Point(left, top - 20), "Text");
                Text(dc, "Shape magnified - click a node", new Point(left, top + 14), "Text");
            }
            if (selectedDeflectionNode is { } name && displacements.TryGetValue(name, out var selected) && deflectedNodes.TryGetValue(name, out var selectedPoint))
            {
                dc.DrawEllipse(null, new Pen(Brush("Text"), 2), selectedPoint, 7, 7);
                CableLabel(dc, $"Ux {selected.UXM * 1000:G8} mm  Uy 0 mm  Uz {selected.UZM * 1000:G8} mm", new Point(18, 90), false);
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
                    ValueLabel(dc, $"Rz {ResultDisplayText.Force(reaction.ReactionZKN)}", point + new Vector(0, 20 + length + 9), "Text");
                }
                if (Math.Abs(reaction.ReactionXKN) > 1e-9)
                {
                    // Beside the pin: tail on the side the reaction comes from, head at the joint.
                    var length = 18 + 28 * Math.Abs(reaction.ReactionXKN) / maximum;
                    var direction = reaction.ReactionXKN > 0 ? 1 : -1;
                    var head = point + new Vector(-direction * 12, 8);
                    var tail = head + new Vector(-direction * length, 0);
                    DrawResultArrow(dc, tail, head, brush);
                    ValueLabel(dc, $"Rx {ResultDisplayText.Force(reaction.ReactionXKN)}", tail + new Vector(-direction * 34, 0), "Text");
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
        var maximumCable = mode == FemResultMode.AxialForce
            ? result.Cables.Where(cable => cable.Active).Select(cable => Math.Max(0, cable.AxialForceKN)).DefaultIfEmpty().Max()
            : 0;
        var maximumDiagram = Math.Max(maximumFrame, maximumCable);
        var diagramWidth = Math.Clamp(Math.Min(ActualWidth, ActualHeight) * 0.055, 18, 52);
        if (maximumDiagram > 1e-12)
        {
            var frameByName = result.Frames.ToDictionary(frame => frame.MemberName, StringComparer.Ordinal);
            var scale = diagramWidth / maximumDiagram;
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
            if (maximumDiagram > 1e-12)
            {
                var accent = (SolidColorBrush)Brush("AccentBright");
                var cableFill = new SolidColorBrush(Color.FromArgb(100, accent.Color.R, accent.Color.G, accent.Color.B));
                var cableOutline = new Pen(accent, 1.5);
                foreach (var member in Model.Members.Where(member => member.Kind == MemberKind.Cable))
                    if (cableByName.TryGetValue(member.Name, out var cable) && cable.Active && cable.AxialForceKN > 1e-12 &&
                        screenPoints.TryGetValue(member.Start, out var start) && screenPoints.TryGetValue(member.End, out var end))
                        DrawCableForceDiagram(dc, start, end, cable.AxialForceKN * diagramWidth / maximumDiagram, cableFill, cableOutline);
            }
            // Draw every cable axis after all fills so crossing cables remain legible.
            foreach (var member in Model.Members.Where(member => member.Kind == MemberKind.Cable))
            {
                if (!cableByName.TryGetValue(member.Name, out var cable) || !screenPoints.TryGetValue(member.Start, out var start) ||
                    !screenPoints.TryGetValue(member.End, out var end)) continue;
                var pen = new Pen(Brush(cable.Active ? "AccentBright" : "Muted"), 2.5)
                { DashStyle = cable.Active ? DashStyles.Solid : DashStyles.Dash };
                dc.DrawLine(pen, start, end);
            }
        }

        if (!ShowAxialValues || mode is not (FemResultMode.AxialForce or FemResultMode.BendingMoment or FemResultMode.Shear)) return;
        if (mode != FemResultMode.AxialForce && maximumFrame <= 1e-12) return;

        // Each label is located at a frame-piece midpoint and follows that frame's projected axis.
        // The force at that location is interpolated from the actual FEM stations.
        var labelledFrames = result.Frames.ToDictionary(frame => frame.MemberName, StringComparer.Ordinal);
        foreach (var member in Model.Members.Where(member => member.Kind == MemberKind.Frame))
        {
            if (!labelledFrames.TryGetValue(member.Name, out var frame) ||
                !screenPoints.TryGetValue(member.Start, out var start) || !screenPoints.TryGetValue(member.End, out var end)) continue;
            foreach (var (fraction, axial) in AxialPieces(frame))
            {
                var section = mode == FemResultMode.AxialForce ? null : SectionAt(frame, fraction);
                var value = mode switch
                {
                    FemResultMode.AxialForce => axial,
                    FemResultMode.BendingMoment => section!.MomentKNm,
                    _ => section!.ShearKN
                };
                var text = mode switch
                {
                    FemResultMode.AxialForce => $"N {ResultDisplayText.Force(value)}",
                    FemResultMode.BendingMoment => $"M {ResultDisplayText.Moment(value)}",
                    _ => $"V {ResultDisplayText.Force(value)}"
                };
                var offset = mode == FemResultMode.AxialForce ? 0 :
                    (value < 0 ? -1 : 1) * (Math.Min(Math.Abs(value) * diagramWidth / maximumFrame, diagramWidth) + 9);
                MemberValueLabel(dc, text, start, end, fraction, offset, value >= 0 ? "Accent" : "Error");
            }
        }

        if (mode != FemResultMode.AxialForce) return;
        // Cable tensions last so they sit on top; 65% along avoids their shared anchors.
        var labelledCables = result.Cables.ToDictionary(cable => cable.MemberName, StringComparer.Ordinal);
        foreach (var member in Model.Members.Where(member => member.Kind == MemberKind.Cable))
            if (labelledCables.TryGetValue(member.Name, out var cable) && screenPoints.TryGetValue(member.Start, out var start) &&
                screenPoints.TryGetValue(member.End, out var end))
                MemberValueLabel(dc, $"T {ResultDisplayText.Force(cable.AxialForceKN)}", start, end, 0.65, 12,
                    cable.Active ? "AccentBright" : "Muted");
    }

    /// <summary>Mid-fraction and mean axial force of each piece of a frame; pieces start where a station repeats.</summary>
    private readonly Dictionary<string, Point> deflectedNodes = [];
    private string? selectedDeflectionNode;
    private static SolidColorBrush DeflectionBrush(double ratio)
    {
        var t = Math.Clamp(ratio, 0, 1);
        var colors = new[] { Colors.RoyalBlue, Colors.Cyan, Colors.LimeGreen, Colors.Gold, Colors.Red };
        var index = Math.Min(3, (int)(t * 4)); var f = t * 4 - index;
        var a = colors[index]; var b = colors[index + 1];
        return new SolidColorBrush(Color.FromRgb((byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f)));
    }

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

    private void MemberValueLabel(DrawingContext dc, string text, Point start, Point end, double fraction, double offset, string brush)
    {
        var direction = end - start;
        var length = direction.Length;
        if (length < 1) return;
        var normal = new Vector(-direction.Y / length, direction.X / length);
        var center = start + direction * fraction + normal * offset;
        dc.PushTransform(new RotateTransform(ResultDisplayText.UprightAngle(direction.X, direction.Y), center.X, center.Y));
        ValueLabel(dc, text, center, brush);
        dc.Pop();
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

    private static void DrawCableForceDiagram(DrawingContext dc, Point start, Point end, double width, Brush fill, Pen outline)
    {
        var direction = end - start;
        if (direction.Length < 1 || !double.IsFinite(width)) return;
        var normal = new Vector(-direction.Y, direction.X);
        normal.Normalize();
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, true, true);
            context.LineTo(end, true, false);
            context.LineTo(end + normal * width, true, false);
            context.LineTo(start + normal * width, true, false);
        }
        dc.DrawGeometry(fill, outline, geometry);
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
    // ---- Dimensions: a chain of the distinct node positions along each axis of the projection ----
    private const double DimensionBandX = 150, DimensionBandY = 96;

    private static IReadOnlyList<double> Levels(IEnumerable<double> values)
    {
        var levels = new List<double>();
        foreach (var value in values.OrderBy(value => value))
            if (levels.Count == 0 || value - levels[^1] > 1e-3) levels.Add(value);
        return levels;
    }

    private static string Metres(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    private void DrawDimensions(DrawingContext dc, IEnumerable<Point> modelPoints, Func<Point, Point> screen)
    {
        var points = modelPoints.ToArray();
        var xs = Levels(points.Select(point => point.X));
        var ys = Levels(points.Select(point => point.Y));
        var brush = Brush("Muted");
        var pen = new Pen(brush, 1);
        var extension = new Pen(brush, 0.6) { DashStyle = DashStyles.Dot };
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Label(string text, double size = 10, bool bold = false) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, brush, dpi);
        void Tick(Point at) => dc.DrawLine(pen, at + new Vector(-4, 4), at + new Vector(4, -4));
        double minY = ys[0], maxY = ys[^1], minX = xs[0], maxX = xs[^1];
        var bottom = screen(new Point(minX, minY)).Y;
        var right = screen(new Point(maxX, maxY)).X;
        var (horizontal, horizontalLabels) = (Plane == "YZ" ? "Y" : "X", Plane == "XY" ? "Y" : "Z");

        // Spans along the bottom: each bay, then the overall span.
        if (xs.Count > 1)
        {
            var chainY = bottom + 46;     // clear of the pin-support symbols below the base joints
            var totalY = bottom + 74;
            foreach (var x in xs)
            {
                var at = screen(new Point(x, minY));
                dc.DrawLine(extension, new Point(at.X, bottom + 6), new Point(at.X, chainY + 4));
                Tick(new Point(at.X, chainY));
            }
            dc.DrawLine(pen, new Point(screen(new Point(minX, minY)).X, chainY), new Point(screen(new Point(maxX, minY)).X, chainY));
            for (var index = 1; index < xs.Count; index++)
            {
                var a = screen(new Point(xs[index - 1], minY)).X;
                var b = screen(new Point(xs[index], minY)).X;
                var text = Label(Metres(xs[index] - xs[index - 1]));
                if (b - a >= text.Width + 4) dc.DrawText(text, new Point((a + b) / 2 - text.Width / 2, chainY - text.Height - 1));
            }
            // Overall span: between the mega columns (the outermost truss chord lines) when the chord points are known,
            // so the end frames outside them are not counted; otherwise the full extent.
            var spanXs = (SpanPoints ?? []).Where(Model.Points.ContainsKey).Select(name => Project(Model.Points[name].Position).X).ToArray();
            var betweenColumns = spanXs.Length > 0 && spanXs.Max() - spanXs.Min() > 1e-3;
            var (spanFrom, spanTo) = betweenColumns ? (spanXs.Min(), spanXs.Max()) : (minX, maxX);
            var left = screen(new Point(spanFrom, minY)).X;
            var end = screen(new Point(spanTo, minY)).X;
            dc.DrawLine(extension, new Point(left, chainY + 4), new Point(left, totalY + 4));
            dc.DrawLine(extension, new Point(end, chainY + 4), new Point(end, totalY + 4));
            dc.DrawLine(pen, new Point(left, totalY), new Point(end, totalY));
            Tick(new Point(left, totalY)); Tick(new Point(end, totalY));
            var total = Label(betweenColumns ? $"Span {Metres(spanTo - spanFrom)} m (between mega columns)" : $"{horizontal} span {Metres(spanTo - spanFrom)} m", 11, bold: true);
            dc.DrawText(total, new Point((left + end) / 2 - total.Width / 2, totalY - total.Height - 1));
        }

        // Heights at the right: each level-to-level height, the overall height, and every node level's elevation.
        if (ys.Count > 1)
        {
            var chainX = right + 34;
            var totalX = right + 62;
            foreach (var y in ys)
            {
                var at = screen(new Point(maxX, y));
                dc.DrawLine(extension, new Point(right + 6, at.Y), new Point(chainX + 4, at.Y));
                Tick(new Point(chainX, at.Y));
                var level = Label($"{horizontalLabels} {(y - minY >= 0 ? "+" : "")}{Metres(y - minY)}");
                dc.DrawText(level, new Point(totalX + 8, at.Y - level.Height / 2));
            }
            dc.DrawLine(pen, new Point(chainX, screen(new Point(maxX, minY)).Y), new Point(chainX, screen(new Point(maxX, maxY)).Y));
            for (var index = 1; index < ys.Count; index++)
            {
                var a = screen(new Point(maxX, ys[index - 1])).Y;
                var b = screen(new Point(maxX, ys[index])).Y;
                var text = Label(Metres(ys[index] - ys[index - 1]));
                if (a - b < text.Width + 4) continue;
                dc.PushTransform(new RotateTransform(-90, chainX - 3, (a + b) / 2));
                dc.DrawText(text, new Point(chainX - 3 - text.Width / 2, (a + b) / 2 - text.Height));
                dc.Pop();
            }
            var top = screen(new Point(maxX, maxY)).Y;
            var bottomY = screen(new Point(maxX, minY)).Y;
            dc.DrawLine(pen, new Point(totalX, bottomY), new Point(totalX, top));
            Tick(new Point(totalX, bottomY)); Tick(new Point(totalX, top));
            var total = Label($"H {Metres(maxY - minY)} m", 11, bold: true);
            dc.PushTransform(new RotateTransform(-90, totalX - 3, (top + bottomY) / 2));
            dc.DrawText(total, new Point(totalX - 3 - total.Width / 2, (top + bottomY) / 2 - total.Height));
            dc.Pop();
        }
    }

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

    /// <summary>A note below a joint, reading upward: joints a bay apart keep their notes clear of each other.</summary>
    private void JointNote(DrawingContext dc, string text, Point joint)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Consolas"), 9.5, Brush("Accent"), dpi);
        // laid out to the left of the joint, then turned a quarter about it: the text ends just under the joint
        var origin = new Point(joint.X - 7 - label.Width, joint.Y - label.Height / 2);
        dc.PushTransform(new RotateTransform(-90, joint.X, joint.Y));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), null,
            new Rect(origin.X - 2, origin.Y - 1, label.Width + 4, label.Height + 2));
        dc.DrawText(label, origin);
        dc.Pop();
    }

    /// <summary>A label along a→b at <paramref name="at"/> of its length (0.5 = the middle), <paramref name="offset"/> px off the axis.</summary>
    private void MemberLabel(DrawingContext dc, string text, Point a, Point b, double offset, string brush, double at = 0.5)
    {
        var dir = b - a;
        if (dir.Length < 8) return;
        var angle = ResultDisplayText.UprightAngle(dir.X, dir.Y);
        var mid = a + dir * at;
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
        Focus();
        var position = e.GetPosition(this);
        // pan: the middle button always, the left button in pan mode
        if (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Left && PanMode)
        {
            if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Middle) { Fit(); return; }
            panning = true; last = position; CaptureMouse(); e.Handled = true; return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) return;   // a double click selects twice; Home / Zoom extents fit the view

        if (FemResultMode == FemResultMode.DeflectedShape && FemResult is not null)
        {
            selectedDeflectionNode = deflectedNodes.Where(item => (item.Value - position).Length <= 10)
                .OrderBy(item => (item.Value - position).Length).Select(item => item.Key).FirstOrDefault();
            InvalidateVisual();
            if (selectedDeflectionNode is not null) { e.Handled = true; return; }
        }

        // a load point or a configured cable also drives the workflow tabs' selection
        var hit = appliedPointLocations.Where(item => (item.Value - position).Length <= 12).OrderBy(item => (item.Value - position).Length).Select(item => item.Key).FirstOrDefault();
        if (hit is not null) SelectedAppliedPoint = hit;
        var cableHit = cableLocations
            .Where(item => DistanceToSegment(position, item.Value.Start, item.Value.End) <= 8)
            .OrderBy(item => DistanceToSegment(position, item.Value.Start, item.Value.End))
            .Select(item => item.Key)
            .FirstOrDefault();
        if (cableHit is not null) SelectedCable = cableHit;

        if (SelectAt(position) || hit is not null || cableHit is not null) { InvalidateVisual(); e.Handled = true; return; }
        // empty space: start a selection window
        windowStart = position;
        windowEnd = null;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var next = e.GetPosition(this);
        if (windowStart is not null && IsMouseCaptured)
        {
            windowEnd = next;
            InvalidateVisual();
            return;
        }
        if (!panning || last is not { } point || !IsMouseCaptured)
        {
            // Hover read-out of FEM results at the section under the cursor.
            if (FemResult is null && hoverCursor is null) return;
            hoverCursor = next;
            InvalidateVisual();
            return;
        }
        pan += next - point;
        last = next;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (hoverCursor is null) return;
        hoverCursor = null;
        InvalidateVisual();
    }

    private abstract record HoverTarget;
    private sealed record FrameHover(StructuralMember Member, FemFrameResponse Result, Point Start, Point End, double LengthM) : HoverTarget;
    private sealed record CableHover(StructuralMember Member, FemCableResponse Result, Point Start, Point End) : HoverTarget;
    private sealed record SupportHover(FemSupportReaction Result, Point Point) : HoverTarget;

    private readonly List<HoverTarget> hoverTargets = [];
    private Point? hoverCursor;
    private const double HoverRadius = 10;

    private void CollectHoverTargets(IReadOnlyDictionary<string, Point> screenPoints, FemAnalysisResult result)
    {
        hoverTargets.Clear();
        var frames = result.Frames.ToDictionary(frame => frame.MemberName, StringComparer.Ordinal);
        var cables = result.Cables.ToDictionary(cable => cable.MemberName, StringComparer.Ordinal);
        foreach (var member in Model.Members)
        {
            if (!screenPoints.TryGetValue(member.Start, out var start) || !screenPoints.TryGetValue(member.End, out var end)) continue;
            if (member.Kind == MemberKind.Frame && frames.TryGetValue(member.Name, out var frame))
            {
                var a = Model.Points[member.Start].Position;
                var b = Model.Points[member.End].Position;
                hoverTargets.Add(new FrameHover(member, frame, start, end, Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Z - a.Z, 2))));
            }
            else if (member.Kind == MemberKind.Cable && cables.TryGetValue(member.Name, out var cable))
                hoverTargets.Add(new CableHover(member, cable, start, end));
        }
        foreach (var reaction in result.SupportReactions)
            if (screenPoints.TryGetValue(reaction.PointName, out var point)) hoverTargets.Add(new SupportHover(reaction, point));
    }

    private void DrawHover(DrawingContext dc)
    {
        if (hoverCursor is not { } cursor) return;
        // Supports win over members when both are within reach (a pin sits at the end of a column).
        var support = hoverTargets.OfType<SupportHover>().Where(item => (item.Point - cursor).Length <= HoverRadius + 6)
            .MinBy(item => (item.Point - cursor).Length);
        if (support is not null)
        {
            HoverMarker(dc, support.Point);
            HoverBox(dc, cursor, $"Support {support.Result.PointName}",
                [$"Rx  {ResultDisplayText.Force(support.Result.ReactionXKN, "0.0")}",
                 $"Rz  {ResultDisplayText.Force(support.Result.ReactionZKN, "0.0")}"]);
            return;
        }
        var nearest = hoverTargets
            .Select(target => target switch
            {
                FrameHover frame => (Target: target, Hit: Nearest(cursor, frame.Start, frame.End)),
                CableHover cable => (Target: target, Hit: Nearest(cursor, cable.Start, cable.End)),
                _ => (Target: target, Hit: (Fraction: 0.0, Distance: double.PositiveInfinity, Point: cursor))
            })
            .Where(item => item.Hit.Distance <= HoverRadius)
            .OrderBy(item => item.Hit.Distance)
            .FirstOrDefault();
        switch (nearest.Target)
        {
            case FrameHover frame:
            {
                var section = SectionAt(frame.Result, nearest.Hit.Fraction);
                DrawSectionCut(dc, frame.Start, frame.End, nearest.Hit.Point);
                HoverBox(dc, cursor, $"{frame.Member.Name}  x = {nearest.Hit.Fraction * frame.LengthM:0.000} m  ({nearest.Hit.Fraction * 100:0}%)",
                [
                    $"N   {ResultDisplayText.Force(section.AxialKN, "0.0")}",
                    $"V   {ResultDisplayText.Force(section.ShearKN, "0.0")}",
                    $"M   {ResultDisplayText.Moment(section.MomentKNm, "0.0")}",
                    $"ux  {section.UXM * 1000,10:0.00} mm",
                    $"uz  {section.UZM * 1000,10:0.00} mm"
                ]);
                break;
            }
            case CableHover cable:
                HoverMarker(dc, nearest.Hit.Point);
                HoverBox(dc, cursor, $"{cable.Member.Name} (cable)",
                [
                    $"T   {ResultDisplayText.Force(cable.Result.AxialForceKN, "0.0")}" + (cable.Result.Active ? "" : "  slack"),
                    .. cable.Result.TargetForceKN > 0 ? new[] { $"Target {ResultDisplayText.Force(cable.Result.TargetForceKN, "0.0")}" } : [],
                    .. cable.Result is { SagM: double sag, TangentStiffnessRatio: double ratio }
                        ? new[] { $"Sag {sag * 1000,10:0.0} mm", $"Stiffness {ratio * 100,5:0.0} % of EA/L" } : []
                ]);
                break;
        }
    }

    /// <summary>Linear interpolation between the mesh stations bracketing the fraction (stations repeat at the joints
    /// where a frame is split; the piece containing the fraction is used).</summary>
    private static FemFrameStation SectionAt(FemFrameResponse frame, double fraction)
    {
        var stations = frame.Stations is { Count: > 1 } list ? list :
        [
            new(0, frame.AxialStartKN, frame.ShearStartKN, frame.MomentStartKNm, 0, 0),
            new(1, frame.AxialEndKN, frame.ShearEndKN, frame.MomentEndKNm, 0, 0)
        ];
        for (var index = 1; index < stations.Count; index++)
        {
            var a = stations[index - 1];
            var b = stations[index];
            if (b.Fraction <= a.Fraction || fraction > b.Fraction && index < stations.Count - 1) continue;
            var t = Math.Clamp((fraction - a.Fraction) / (b.Fraction - a.Fraction), 0, 1);
            double Lerp(double from, double to) => from + (to - from) * t;
            return new(fraction, Lerp(a.AxialKN, b.AxialKN), Lerp(a.ShearKN, b.ShearKN), Lerp(a.MomentKNm, b.MomentKNm),
                Lerp(a.UXM, b.UXM), Lerp(a.UZM, b.UZM));
        }
        return stations[^1];
    }

    private static (double Fraction, double Distance, Point Point) Nearest(Point point, Point start, Point end)
    {
        var segment = end - start;
        var lengthSquared = segment.X * segment.X + segment.Y * segment.Y;
        if (lengthSquared <= double.Epsilon) return (0, (point - start).Length, start);
        var t = Math.Clamp(((point - start).X * segment.X + (point - start).Y * segment.Y) / lengthSquared, 0, 1);
        var onSegment = start + segment * t;
        return (t, (point - onSegment).Length, onSegment);
    }

    private void DrawSectionCut(DrawingContext dc, Point start, Point end, Point at)
    {
        var direction = end - start;
        if (direction.Length < 1) return;
        direction.Normalize();
        var normal = new Vector(-direction.Y, direction.X);
        dc.DrawLine(new Pen(Brush("Error"), 1.5) { DashStyle = DashStyles.Dash }, at - normal * 14, at + normal * 14);
        HoverMarker(dc, at);
    }

    private void HoverMarker(DrawingContext dc, Point at) =>
        dc.DrawEllipse(Brush("Error"), new Pen(Brush("ViewportBg"), 1.5), at, 4, 4);

    private void HoverBox(DrawingContext dc, Point cursor, string title, IReadOnlyList<string> lines)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var heading = new FormattedText(title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 11, Brush("Text"), dpi);
        var body = new FormattedText(string.Join("\n", lines), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Consolas"), 11, Brush("Text"), dpi);
        var width = Math.Max(heading.Width, body.Width) + 16;
        var height = heading.Height + body.Height + 14;
        // Keep the box inside the viewport, preferring below-right of the cursor.
        var x = cursor.X + 16 + width > ActualWidth ? cursor.X - 16 - width : cursor.X + 16;
        var y = cursor.Y + 16 + height > ActualHeight ? cursor.Y - 16 - height : cursor.Y + 16;
        var box = new Rect(Math.Max(4, x), Math.Max(4, y), width, height);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)), new Pen(Brush("AccentBright"), 1), box, 3, 3);
        dc.DrawText(heading, new Point(box.X + 8, box.Y + 5));
        dc.DrawText(body, new Point(box.X + 8, box.Y + 9 + heading.Height));
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (windowStart is { } from)
        {
            if (windowEnd is { } to && (to - from).Length > 4) SelectWindow(from, to);
            windowStart = windowEnd = null;
            InvalidateVisual();
        }
        last = null;
        panning = false;
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
        if (e.Key == Key.Escape) { PanMode = false; ClearSelection(); e.Handled = true; }
        else if (e.Key == Key.Home) Fit();
        else if (e.Key == Key.Add) zoom = Math.Min(100, zoom * 1.15);
        else if (e.Key == Key.Subtract) zoom = Math.Max(.05, zoom / 1.15);
        InvalidateVisual();
    }
}
