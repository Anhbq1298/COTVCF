using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Presentation.GeometryEditing;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Presentation.Controls;

/// <summary>
/// The editing side of the model view (geometry editing plan): one view plane shared by the projection box and the
/// window (<see cref="ViewPlane"/>), the plane's coordinate, the grids and levels, what is shown, and the drawing tools —
/// in the 2D view on the plane shown, in the 3D view on the work plane (<see cref="WorkPlane"/>, a ray from the screen
/// to it; nothing is placed where that plane is seen edge-on, and without a work plane only points of the model are
/// picked: the depth is never guessed).
/// </summary>
public partial class Structural2DViewer
{
    private static DependencyProperty Register<T>(string name, T fallback, PropertyChangedCallback? changed = null, bool twoWay = false) =>
        DependencyProperty.Register(name, typeof(T), typeof(Structural2DViewer),
            new FrameworkPropertyMetadata(fallback, twoWay ? FrameworkPropertyMetadataOptions.BindsTwoWayByDefault : FrameworkPropertyMetadataOptions.None, changed));

    public static readonly DependencyProperty ViewPlaneProperty = Register(nameof(ViewPlane), "XZ", (d, e) => ((Structural2DViewer)d).ViewPlaneChanged((string)e.NewValue), twoWay: true);
    /// <summary>The projection: XY, XZ, YZ or 3D — the same value as the box at the top right, both ways.</summary>
    public string ViewPlane { get => (string)GetValue(ViewPlaneProperty); set => SetValue(ViewPlaneProperty, value); }

    public static readonly DependencyProperty KeepViewOnEditProperty = Register(nameof(KeepViewOnEdit), false);
    public bool KeepViewOnEdit { get => (bool)GetValue(KeepViewOnEditProperty); set => SetValue(KeepViewOnEditProperty, value); }
    public static readonly DependencyProperty ExtentsModelProperty = Register<GeometryModel?>(nameof(ExtentsModel), null, (d, _) => ((Structural2DViewer)d).Refresh3DOverlays());
    public GeometryModel? ExtentsModel { get => (GeometryModel?)GetValue(ExtentsModelProperty); set => SetValue(ExtentsModelProperty, value); }
    public static readonly DependencyProperty ReferenceGeometryProperty = Register<Native.ReferenceGeometry?>(nameof(ReferenceGeometry), null, (d, _) => ((Structural2DViewer)d).Refresh3DOverlays());
    public Native.ReferenceGeometry? ReferenceGeometry { get => (Native.ReferenceGeometry?)GetValue(ReferenceGeometryProperty); set => SetValue(ReferenceGeometryProperty, value); }
    public static readonly DependencyProperty PlaneCoordinateProperty = Register<double?>(nameof(PlaneCoordinate), null);
    public double? PlaneCoordinate { get => (double?)GetValue(PlaneCoordinateProperty); set => SetValue(PlaneCoordinateProperty, value); }
    public static readonly DependencyProperty ShowViewChromeProperty = Register(nameof(ShowViewChrome), true, (d, _) => ((Structural2DViewer)d).ApplyViewChrome());
    /// <summary>The view's own controls and captions over the model: the projection box with Fit and the label toggles, the
    /// deformed-shape options, the 3D hint line and the plane title. A window that has them in its toolbar and its Display
    /// Options turns them off (the main window, 2026-10-15: "too many things on the plate"); a preview keeps them.</summary>
    public bool ShowViewChrome { get => (bool)GetValue(ShowViewChromeProperty); set => SetValue(ShowViewChromeProperty, value); }

    private void ApplyViewChrome()
    {
        // a local value wins over the style's triggers, so these stay hidden whatever the result mode
        foreach (var element in new FrameworkElement[] { ViewerControls, ResultOptionsBox, View3DInfo })
            if (ShowViewChrome) element.ClearValue(VisibilityProperty); else element.Visibility = Visibility.Collapsed;
        Canvas.ShowTitle = ShowViewChrome;
    }

    public static readonly DependencyProperty LeftInsetProperty = Register(nameof(LeftInset), 0.0);
    /// <summary>Pixels at the left of the 2D view kept clear of the model, for a tool strip over it.</summary>
    public double LeftInset { get => (double)GetValue(LeftInsetProperty); set => SetValue(LeftInsetProperty, value); }
    public static readonly DependencyProperty ViewTitleProperty = Register<string?>(nameof(ViewTitle), null);
    public string? ViewTitle { get => (string?)GetValue(ViewTitleProperty); set => SetValue(ViewTitleProperty, value); }

    /// <summary>The plane a tool draws on in the 3D view (the last plane shown in 2D), and its coordinate.</summary>
    public static readonly DependencyProperty WorkPlaneProperty = Register<string?>(nameof(WorkPlane), null, (d, _) => ((Structural2DViewer)d).Refresh3DOverlays());
    public string? WorkPlane { get => (string?)GetValue(WorkPlaneProperty); set => SetValue(WorkPlaneProperty, value); }
    public static readonly DependencyProperty WorkPlaneCoordinateProperty = Register<double?>(nameof(WorkPlaneCoordinate), null, (d, _) => ((Structural2DViewer)d).Refresh3DOverlays());
    public double? WorkPlaneCoordinate { get => (double?)GetValue(WorkPlaneCoordinateProperty); set => SetValue(WorkPlaneCoordinateProperty, value); }

    public static readonly DependencyProperty ShowJointsProperty = Register(nameof(ShowJoints), true, (d, _) => ((Structural2DViewer)d).VisibilityChanged3D());
    public static readonly DependencyProperty ShowFramesProperty = Register(nameof(ShowFrames), true, (d, _) => ((Structural2DViewer)d).VisibilityChanged3D());
    public static readonly DependencyProperty ShowCablesProperty = Register(nameof(ShowCables), true, (d, _) => ((Structural2DViewer)d).VisibilityChanged3D());
    public static readonly DependencyProperty ShowGridLinesProperty = Register(nameof(ShowGridLines), true, (d, _) => ((Structural2DViewer)d).Refresh3DOverlays());
    public static readonly DependencyProperty ShowLevelsProperty = Register(nameof(ShowLevels), true, (d, _) => ((Structural2DViewer)d).Refresh3DOverlays());
    public static readonly DependencyProperty ShowReferenceLabelsProperty = Register(nameof(ShowReferenceLabels), true, (d, _) => ((Structural2DViewer)d).Refresh3DOverlays());
    public bool ShowJoints { get => (bool)GetValue(ShowJointsProperty); set => SetValue(ShowJointsProperty, value); }
    public bool ShowFrames { get => (bool)GetValue(ShowFramesProperty); set => SetValue(ShowFramesProperty, value); }
    public bool ShowCables { get => (bool)GetValue(ShowCablesProperty); set => SetValue(ShowCablesProperty, value); }
    public bool ShowGridLines { get => (bool)GetValue(ShowGridLinesProperty); set => SetValue(ShowGridLinesProperty, value); }
    public bool ShowLevels { get => (bool)GetValue(ShowLevelsProperty); set => SetValue(ShowLevelsProperty, value); }
    public bool ShowReferenceLabels { get => (bool)GetValue(ShowReferenceLabelsProperty); set => SetValue(ShowReferenceLabelsProperty, value); }

    public static readonly DependencyProperty DrawingToolProperty = Register(nameof(DrawingTool), DrawingTool.None, (d, _) => ((Structural2DViewer)d).ToolChanged3D(), twoWay: true);
    public DrawingTool DrawingTool { get => (DrawingTool)GetValue(DrawingToolProperty); set => SetValue(DrawingToolProperty, value); }
    public static readonly DependencyProperty SnapModesProperty = Register(nameof(SnapModes), SnapResolver.Defaults);
    public SnapModes SnapModes { get => (SnapModes)GetValue(SnapModesProperty); set => SetValue(SnapModesProperty, value); }
    public static readonly DependencyProperty ChainDrawingProperty = Register(nameof(ChainDrawing), true);
    public bool ChainDrawing { get => (bool)GetValue(ChainDrawingProperty); set => SetValue(ChainDrawingProperty, value); }

    public event EventHandler<PointPlacedEventArgs>? PointPlaced;
    public event EventHandler<MemberDrawnEventArgs>? MemberDrawn;
    public event EventHandler<MoveRequestedEventArgs>? MoveRequested;
    public event EventHandler? CursorMoved;

    /// <summary>Where the cursor is in the model (snapped while a tool is on), in the 2D or the 3D view; null when not known.</summary>
    public Position? CursorWorld => Is3D ? cursor3D?.World : Canvas.CursorWorld;
    public SnapCandidate? CursorSnap => Is3D ? cursor3D : Canvas.CursorSnap;
    public bool HasToolInteraction => Is3D ? anchor3D is not null : Canvas.HasToolInteraction;

    /// <summary>Esc: drops what a tool has half made, in either view. False when there is nothing to drop.</summary>
    public bool CancelToolInteraction()
    {
        if (!Is3D) return Canvas.CancelToolInteraction();
        if (anchor3D is null) return false;
        (anchor3D, anchorJoint3D) = (null, null);
        Refresh3DOverlays();
        return true;
    }

    private void InitializeEditing()
    {
        Canvas.Overlay = ToolLayer;
        Canvas.PointPlaced += (_, e) => PointPlaced?.Invoke(this, e);
        Canvas.MemberDrawn += (_, e) => MemberDrawn?.Invoke(this, e);
        Canvas.MoveRequested += (_, e) => MoveRequested?.Invoke(this, e);
        Canvas.CursorMoved += (_, _) => CursorMoved?.Invoke(this, EventArgs.Empty);
        Plane.SelectionChanged += (_, _) =>
        {
            if (Plane.SelectedItem is not ComboBoxItem { Content: string plane }) return;
            (anchor3D, anchorJoint3D, cursor3D) = (null, null, null);
            Canvas.CancelToolInteraction();
            SetCurrentValue(ViewPlaneProperty, plane);
            Refresh3DOverlays();
        };
        View3DHost.PreviewMouseLeftButtonDown += Tool3DMouseDown;
        View3DHost.PreviewMouseMove += Tool3DMouseMove;
        View3DHost.MouseLeave += (_, _) => { cursor3D = null; CursorMoved?.Invoke(this, EventArgs.Empty); Refresh3DOverlays(); };
        View3DHost.SizeChanged += (_, _) => Refresh3DOverlays();
        Camera3D.Changed += (_, _) => Refresh3DOverlays();
    }

    private void ViewPlaneChanged(string plane)
    {
        if (Plane is null || string.Equals(CurrentPlane, plane, StringComparison.OrdinalIgnoreCase)) return;
        ShowPlane(plane);
    }

    private void ToolChanged3D()
    {
        (anchor3D, anchorJoint3D) = (null, null);
        View3DHost.Cursor = DrawingTool == DrawingTool.None ? null : Cursors.Cross;
        Refresh3DOverlays();
    }

    private void VisibilityChanged3D()
    {
        Rebuild3D(keepCamera: true);
        Update3DJoints();
    }

    /// <summary>The model as the 3D view builds it: the members of a hidden kind left out (Display Options), so they are
    /// neither drawn nor picked.</summary>
    private GeometryModel Model3D => ShowFrames && ShowCables ? Model
        : Model with { Members = Model.Members.Where(member => member.Kind == MemberKind.Frame ? ShowFrames : ShowCables).ToArray() };

    // ---- 3D: the work plane, its grid, and the tools on it ----
    private Position? anchor3D;
    private string? anchorJoint3D;
    private SnapCandidate? cursor3D;

    private bool Tool3DActive => Is3D && DrawingTool != DrawingTool.None && !PanMode;

    /// <summary>The model position where the line of sight through a screen point meets the work plane; null without a
    /// work plane, or where the plane is seen edge-on (the position would be a guess).</summary>
    internal Position? RayToWorkPlane(Point screen)
    {
        if (WorkPlane is not { } plane || WorkPlaneCoordinate is not { } c || !ViewPlanes.IsPlane(plane) || Camera3D is null ||
            View3DHost.ActualWidth <= 0 || viewWidth <= 0) return null;
        var look = Camera3D.LookDirection;
        if (look.Length < 1e-12) return null;
        look.Normalize();
        var right = Vector3D.CrossProduct(look, new Vector3D(0, 0, 1));
        if (right.Length < 1e-9) right = new Vector3D(1, 0, 0);
        right.Normalize();
        var up = Vector3D.CrossProduct(right, look);
        var pixelsPerMetre = View3DHost.ActualWidth / viewWidth;
        var origin = target + right * ((screen.X - View3DHost.ActualWidth / 2) / pixelsPerMetre) + up * ((View3DHost.ActualHeight / 2 - screen.Y) / pixelsPerMetre);
        var normal = plane == "XY" ? new Vector3D(0, 0, 1) : plane == "XZ" ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0);
        var facing = Vector3D.DotProduct(look, normal);
        if (Math.Abs(facing) < 0.05) return null;
        var t = (c - Vector3D.DotProduct((Vector3D)origin, normal)) / facing;
        var hit = origin + look * t;
        return new(hit.X, hit.Y, hit.Z);
    }

    /// <summary>Where a model position is drawn in the 3D view (checks).</summary>
    internal Point? ProjectToScreen3D(Position position) => Project3D(position);

    private ReferenceLines.Layout WorkPlaneLayout() =>
        ReferenceGeometry is { IsEmpty: false } reference && WorkPlane is { } plane && WorkPlaneCoordinate is { } c && ViewPlanes.IsPlane(plane)
            ? ReferenceLines.For(reference, plane, c, ExtentsModel ?? Model) : new([], []);

    /// <summary>The snap in the 3D view: a point of the model within reach, a grid crossing of the work plane, else the
    /// work plane under the cursor (Alt: no snap).</summary>
    internal SnapCandidate? Resolve3D(Point position)
    {
        var free = RayToWorkPlane(position);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return free is { } f ? new(SnapKind.None, position, f, "") : null;
        var candidates = new List<SnapCandidate>();
        if (ShowJoints)
            foreach (var point in Model.Points.Values)
                if (Project3D(point.Position) is { } at && (at - position).Length <= SnapResolver.RadiusPx)
                    candidates.Add(new(SnapKind.Point, at, point.Position, point.Name, Joint: point.Name));
        foreach (var crossing in WorkPlaneLayout().Crossings)
            if (Project3D(crossing.At) is { } at && (at - position).Length <= SnapResolver.RadiusPx)
                candidates.Add(new(SnapKind.GridIntersection, at, crossing.At, $"Grid {crossing.Label}"));
        return SnapResolver.Pick(position, candidates, SnapModes) ?? (free is { } p ? new(SnapKind.None, position, p, "") : null);
    }

    private void Tool3DMouseDown(object sender, MouseButtonEventArgs e)
    {
        // with a tool on, a left click picks (the view turns with a right-drag pan, the wheel and Fit meanwhile)
        if (!Tool3DActive || e.ClickCount > 1) return;
        e.Handled = true;
        var position = e.GetPosition(View3D);
        var picked = Resolve3D(position);
        if (picked is null)
        {
            prompt3D = WorkPlane is null || WorkPlaneCoordinate is null
                ? "Choose a plane in 2D first (XY at a level, XZ or YZ at a grid): it becomes the work plane of the 3D view; or click a point of the model"
                : "The work plane is seen edge-on here: turn the view, or click a point of the model";
            Refresh3DOverlays();
            return;
        }
        prompt3D = null;
        switch (DrawingTool)
        {
            case DrawingTool.Point:
                PointPlaced?.Invoke(this, new(picked.World, picked.Joint, picked.Kind));
                break;
            case DrawingTool.Frame or DrawingTool.Cable when anchor3D is null:
                (anchor3D, anchorJoint3D) = (picked.World, picked.Joint);
                break;
            case DrawingTool.Frame or DrawingTool.Cable:
                var drawn = new MemberDrawnEventArgs(anchor3D!.Value, anchorJoint3D, picked.World, picked.Joint, DrawingTool == DrawingTool.Cable);
                MemberDrawn?.Invoke(this, drawn);
                if (drawn.Accepted && ChainDrawing) (anchor3D, anchorJoint3D) = (picked.World, drawn.EndJoint);
                else if (drawn.Accepted) (anchor3D, anchorJoint3D) = (null, null);
                break;
            case DrawingTool.Move when Canvas.SelectedJoints.Count + Canvas.SelectedMembers.Count == 0:
                break;
            case DrawingTool.Move when anchor3D is null:
                (anchor3D, anchorJoint3D) = (picked.World, picked.Joint);
                break;
            case DrawingTool.Move:
                var move = new MoveRequestedEventArgs(anchor3D!.Value, picked.World);
                MoveRequested?.Invoke(this, move);
                if (move.Accepted) (anchor3D, anchorJoint3D) = (null, null);
                break;
        }
        cursor3D = Resolve3D(position);
        Refresh3DOverlays();
    }

    private void Tool3DMouseMove(object sender, MouseEventArgs e)
    {
        if (!Is3D) return;
        var position = e.GetPosition(View3D);
        cursor3D = Tool3DActive ? Resolve3D(position) : RayToWorkPlane(position) is { } free ? new(SnapKind.None, position, free, "") : null;
        CursorMoved?.Invoke(this, EventArgs.Empty);
        if (Tool3DActive) Refresh3DOverlays();
    }

    /// <summary>A right-click in the 3D view with a tool on: ends the chain under way, else leaves the tool.</summary>
    private bool ToolRightClick3D()
    {
        if (!Tool3DActive) return false;
        if (anchor3D is not null) (anchor3D, anchorJoint3D) = (null, null);
        else DrawingTool = DrawingTool.None;
        Refresh3DOverlays();
        return true;
    }

    private string? prompt3D;
    private static readonly Brush WorkPlaneFill = Freeze(new SolidColorBrush(Color.FromArgb(28, 0x2E, 0x90, 0xFA)));
    private static readonly Brush ReferenceBrush3D = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x9B, 0xAE)));
    private static T Freeze<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }

    /// <summary>The 3D view's references and tool preview: the grid lines at the base level and on the work plane, the work
    /// plane shaded, and — with a tool on — the snap, the member being drawn and the instruction.</summary>
    private void Refresh3DOverlays()
    {
        if (Reference3D is null || ToolLayer is null) return;
        if (!Is3D)
        {
            Reference3D.Show(null);
            if (!Canvas.IsVisible) ToolLayer.Show(null);
            return;
        }
        var lines = new List<ReferenceLine>();
        var plane = WorkPlaneLayout();
        if (ReferenceGeometry is { IsEmpty: false } reference && ShowGridLines)
        {
            var model = ExtentsModel ?? Model;
            var baseLevel = reference.Levels.Where(level => level.Visible).Select(level => (double?)level.Elevation).Min()
                ?? reference.GridSystems.Select(system => (double?)system.Origin.Z).FirstOrDefault() ?? 0;
            lines.AddRange(ReferenceLines.PlanAt(reference, baseLevel, model));
            lines.AddRange(plane.Lines.Where(line => line.Kind != ReferenceLineKind.Level || ShowLevels));
        }
        var segments = lines.Select(line => (Project3D(line.A), Project3D(line.B), line)).Where(item => item.Item1 is not null && item.Item2 is not null)
            .Select(item => (A: item.Item1!.Value, B: item.Item2!.Value, item.line)).ToArray();
        // the work plane: the extent of its lines (or of the model) shaded, so it is clear where a click lands
        Point[] outline = [];
        if (WorkPlane is { } workPlane && WorkPlaneCoordinate is { } c && ViewPlanes.IsPlane(workPlane))
        {
            var corners = plane.Lines.SelectMany(line => new[] { line.A, line.B })
                .Concat((ExtentsModel ?? Model).Points.Values.Select(point => point.Position)).Select(p => ViewPlanes.Project(p, workPlane)).ToArray();
            if (corners.Length > 0)
            {
                double minU = corners.Min(p => p.X), maxU = corners.Max(p => p.X), minV = corners.Min(p => p.Y), maxV = corners.Max(p => p.Y);
                outline = new[] { new Point(minU, minV), new Point(maxU, minV), new Point(maxU, maxV), new Point(minU, maxV) }
                    .Select(uv => Project3D(ViewPlanes.ToWorld(uv, workPlane, c))).Where(p => p is not null).Select(p => p!.Value).ToArray();
            }
        }
        var labels = ShowReferenceLabels;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Reference3D.Show(segments.Length == 0 && outline.Length < 3 ? null : dc =>
        {
            if (outline.Length >= 3)
            {
                var figure = new PathFigure(outline[0], outline.Skip(1).Select(p => (PathSegment)new LineSegment(p, true)), true);
                dc.DrawGeometry(WorkPlaneFill, new Pen(new SolidColorBrush(Color.FromArgb(120, 0x2E, 0x90, 0xFA)), 1), new PathGeometry([figure]));
            }
            var pen = new Pen(ReferenceBrush3D, 0.8) { DashStyle = new DashStyle([6, 4], 0) };
            foreach (var (a, b, line) in segments)
            {
                dc.DrawLine(pen, a, b);
                if (!labels || line.Kind == ReferenceLineKind.Level) continue;
                var text = new FormattedText(line.Label, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 10, ReferenceBrush3D, pixelsPerDip);
                dc.DrawText(text, new Point(a.X - text.Width / 2, a.Y - text.Height - 2));
            }
        });

        if (!Tool3DActive)
        {
            ToolLayer.Show(null);
            return;
        }
        var from = anchor3D is { } start ? Project3D(start) : null;
        var target = cursor3D;
        var ghost = DrawingTool == DrawingTool.Move && from is { } f && target is { } t ? t.Screen - f : (Vector?)null;
        var selectedPoints = ghost is null ? [] : Canvas.SelectedJoints.Where(Model.Points.ContainsKey)
            .Select(name => Project3D(Model.Points[name].Position)).Where(p => p is not null).Select(p => p!.Value).ToArray();
        var selected = Canvas.SelectedMembers.ToHashSet(StringComparer.Ordinal);
        var selectedLines = ghost is null ? [] : Model.Members.Where(member => selected.Contains(member.Name) && Model.Points.ContainsKey(member.Start) && Model.Points.ContainsKey(member.End))
            .Select(member => (Project3D(Model.Points[member.Start].Position), Project3D(Model.Points[member.End].Position)))
            .Where(item => item.Item1 is not null && item.Item2 is not null).Select(item => (item.Item1!.Value, item.Item2!.Value)).ToArray();
        var text = prompt3D ?? Canvas.ToolPromptFor(DrawingTool, anchor3D is not null);
        ToolLayer.Show(dc => ModelCanvas.DrawToolPreview(dc, text, from, anchor3D, target, ghost, selectedLines, selectedPoints, pixelsPerDip));
    }
}
