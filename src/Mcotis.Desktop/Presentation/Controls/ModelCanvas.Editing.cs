using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Presentation.GeometryEditing;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Presentation.Controls;

/// <summary>The tool of the model view: selecting (none), or drawing points, frames or cables, or moving the selection by
/// two picked points.</summary>
public enum DrawingTool { None, Point, Frame, Cable, Move }

/// <summary>A click of the Point tool: where, and the point of the model it landed on, if any. The handler makes the
/// point (or refuses) and says so in <see cref="Accepted"/>.</summary>
public sealed class PointPlacedEventArgs(Position position, string? joint, SnapKind snap) : EventArgs
{
    public Position Position { get; } = position;
    public string? Joint { get; } = joint;
    public SnapKind Snap { get; } = snap;
    public bool Accepted { get; set; }
}

/// <summary>The second click of the Frame or Cable tool: both ends, each a point of the model or a position. The handler
/// draws the member (or refuses), and names the point it ends on in <see cref="EndJoint"/>, so a chain goes on from it.</summary>
public sealed class MemberDrawnEventArgs(Position start, string? startJoint, Position end, string? endJoint, bool cable) : EventArgs
{
    public Position Start { get; } = start;
    public string? StartJoint { get; } = startJoint;
    public Position End { get; } = end;
    public string? EndJoint { get; set; } = endJoint;
    public bool Cable { get; } = cable;
    public bool Accepted { get; set; }
}

/// <summary>The second click of the Move tool: the selection moves by <see cref="Offset"/>.</summary>
public sealed class MoveRequestedEventArgs(Position from, Position to) : EventArgs
{
    public Position From { get; } = from;
    public Position To { get; } = to;
    public Position Offset => new(To.X - From.X, To.Y - From.Y, To.Z - From.Z);
    public bool Accepted { get; set; }
}

/// <summary>
/// Editing on the 2D model view (geometry editing plan, slices 01 and 04–07): a held view frame, the plane's fixed
/// coordinate, the grid systems and levels drawn under the model, and the Point, Frame, Cable and Move tools with snapping.
/// The canvas never changes the model: a tool raises an event with the model positions it resolved, and the window's view
/// model makes the edit as one undoable step (or refuses it with the reason).
/// </summary>
public sealed partial class ModelCanvas
{
    // ---- properties ----
    public static readonly DependencyProperty KeepViewOnEditProperty = DependencyProperty.Register(nameof(KeepViewOnEdit), typeof(bool), typeof(ModelCanvas),
        new PropertyMetadata(false));
    /// <summary>Hold the view frame while the model is edited (the main window): the model does not jump on an edit, so a
    /// click lands on the same model position before and after it. A new model, a plane change or Fit frames again.</summary>
    public bool KeepViewOnEdit { get => (bool)GetValue(KeepViewOnEditProperty); set => SetValue(KeepViewOnEditProperty, value); }

    public static readonly DependencyProperty ExtentsModelProperty = DependencyProperty.Register(nameof(ExtentsModel), typeof(GeometryModel), typeof(ModelCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((ModelCanvas)d).Reframe(e.OldValue as GeometryModel, e.NewValue as GeometryModel)));
    /// <summary>The whole model when <see cref="ModelCanvas.Model"/> is a slice of it: the frame follows the whole model, so
    /// stepping from level to level keeps the view where it is.</summary>
    public GeometryModel? ExtentsModel { get => (GeometryModel?)GetValue(ExtentsModelProperty); set => SetValue(ExtentsModelProperty, value); }

    public static readonly DependencyProperty ReferenceGeometryProperty = DependencyProperty.Register(nameof(ReferenceGeometry), typeof(Native.ReferenceGeometry), typeof(ModelCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((ModelCanvas)d).ReferenceChanged(e)));
    public Native.ReferenceGeometry? ReferenceGeometry { get => (Native.ReferenceGeometry?)GetValue(ReferenceGeometryProperty); set => SetValue(ReferenceGeometryProperty, value); }

    public static readonly DependencyProperty PlaneCoordinateProperty = DependencyProperty.Register(nameof(PlaneCoordinate), typeof(double?), typeof(ModelCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>The fixed coordinate of the plane shown (Z of an XY plan, Y of an XZ elevation, X of a YZ elevation): where
    /// a click in empty space lands. Null: the whole model projected — a click lands only on what the model has.</summary>
    public double? PlaneCoordinate { get => (double?)GetValue(PlaneCoordinateProperty); set => SetValue(PlaneCoordinateProperty, value); }

    public static readonly DependencyProperty ShowTitleProperty = DependencyProperty.Register(nameof(ShowTitle), typeof(bool), typeof(ModelCanvas),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>The projection / plane named at the top left of the view (off where the toolbar says it).</summary>
    public bool ShowTitle { get => (bool)GetValue(ShowTitleProperty); set => SetValue(ShowTitleProperty, value); }

    public static readonly DependencyProperty LeftInsetProperty = DependencyProperty.Register(nameof(LeftInset), typeof(double), typeof(ModelCanvas),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Pixels at the left of the view kept clear of the model (a tool strip floats there).</summary>
    public double LeftInset { get => (double)GetValue(LeftInsetProperty); set => SetValue(LeftInsetProperty, value); }

    public static readonly DependencyProperty ViewTitleProperty = DependencyProperty.Register(nameof(ViewTitle), typeof(string), typeof(ModelCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public string? ViewTitle { get => (string?)GetValue(ViewTitleProperty); set => SetValue(ViewTitleProperty, value); }

    public static readonly DependencyProperty ShowJointsProperty = Flag(nameof(ShowJoints));
    public static readonly DependencyProperty ShowFramesProperty = Flag(nameof(ShowFrames));
    public static readonly DependencyProperty ShowCablesProperty = Flag(nameof(ShowCables));
    public static readonly DependencyProperty ShowGridLinesProperty = Flag(nameof(ShowGridLines));
    public static readonly DependencyProperty ShowLevelsProperty = Flag(nameof(ShowLevels));
    public static readonly DependencyProperty ShowReferenceLabelsProperty = Flag(nameof(ShowReferenceLabels));
    private static DependencyProperty Flag(string name) => DependencyProperty.Register(name, typeof(bool), typeof(ModelCanvas),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Display Options → Objects: a hidden kind is not drawn and cannot be picked. It stays in the model and in
    /// the analysis.</summary>
    public bool ShowJoints { get => (bool)GetValue(ShowJointsProperty); set => SetValue(ShowJointsProperty, value); }
    public bool ShowFrames { get => (bool)GetValue(ShowFramesProperty); set => SetValue(ShowFramesProperty, value); }
    public bool ShowCables { get => (bool)GetValue(ShowCablesProperty); set => SetValue(ShowCablesProperty, value); }
    public bool ShowGridLines { get => (bool)GetValue(ShowGridLinesProperty); set => SetValue(ShowGridLinesProperty, value); }
    public bool ShowLevels { get => (bool)GetValue(ShowLevelsProperty); set => SetValue(ShowLevelsProperty, value); }
    public bool ShowReferenceLabels { get => (bool)GetValue(ShowReferenceLabelsProperty); set => SetValue(ShowReferenceLabelsProperty, value); }

    public static readonly DependencyProperty DrawingToolProperty = DependencyProperty.Register(nameof(DrawingTool), typeof(DrawingTool), typeof(ModelCanvas),
        new FrameworkPropertyMetadata(DrawingTool.None, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ModelCanvas)d).ToolChanged()));
    public DrawingTool DrawingTool { get => (DrawingTool)GetValue(DrawingToolProperty); set => SetValue(DrawingToolProperty, value); }

    public static readonly DependencyProperty SnapModesProperty = DependencyProperty.Register(nameof(SnapModes), typeof(SnapModes), typeof(ModelCanvas),
        new PropertyMetadata(SnapResolver.Defaults));
    public SnapModes SnapModes { get => (SnapModes)GetValue(SnapModesProperty); set => SetValue(SnapModesProperty, value); }

    public static readonly DependencyProperty ChainDrawingProperty = DependencyProperty.Register(nameof(ChainDrawing), typeof(bool), typeof(ModelCanvas),
        new PropertyMetadata(true));
    /// <summary>Draw Continuous: the next member starts where the last one ended, until a right-click or Esc.</summary>
    public bool ChainDrawing { get => (bool)GetValue(ChainDrawingProperty); set => SetValue(ChainDrawingProperty, value); }

    /// <summary>Where a tool draws its preview (set by the viewer that holds the canvas).</summary>
    public OverlayLayer? Overlay { get; set; }

    public event EventHandler<PointPlacedEventArgs>? PointPlaced;
    public event EventHandler<MemberDrawnEventArgs>? MemberDrawn;
    public event EventHandler<MoveRequestedEventArgs>? MoveRequested;
    /// <summary>The cursor moved: <see cref="CursorWorld"/> and <see cref="CursorSnap"/> say where it is in the model.</summary>
    public event EventHandler? CursorMoved;

    /// <summary>Where the cursor is in the model — snapped while a tool is on — or null where that is not known (off the
    /// view, or in a projection without a plane coordinate and on nothing of the model).</summary>
    public Position? CursorWorld => cursorTarget?.World;
    public SnapCandidate? CursorSnap => cursorTarget;
    /// <summary>A member or move half made (its first point picked).</summary>
    public bool HasToolInteraction => anchor is not null;
    /// <summary>What the tool asks for next, as the view shows it.</summary>
    public string ToolPrompt => ToolPromptFor(DrawingTool, anchor is not null);

    internal string ToolPromptFor(DrawingTool tool, bool anchored) => tool switch
    {
        DrawingTool.Point => "Draw Point — click to place a point (on a grid crossing, a member or anywhere in the plane) · right-click ends",
        DrawingTool.Frame or DrawingTool.Cable when !anchored => $"Draw {(tool == DrawingTool.Cable ? "Cable" : "Frame")} — click the start point · right-click ends",
        DrawingTool.Frame or DrawingTool.Cable => $"Draw {(tool == DrawingTool.Cable ? "Cable" : "Frame")} — click the end point · right-click or Esc ends the chain",
        DrawingTool.Move when SelectedJoints.Count + SelectedMembers.Count == 0 => "Move — select the points, frames or cables to move first (V, then click or drag a window)",
        DrawingTool.Move when !anchored => "Move — click the base point",
        DrawingTool.Move => "Move — click the destination · Esc cancels",
        _ => "",
    };

    // ---- the transform of the last render: model plane ↔ screen ----
    private bool hasTransform;
    private Point transformCenter, transformOrigin;
    private double transformScale = 1;
    private Rect? frozenFrame;

    private void SetTransform(Point center, double scale, Point origin)
    {
        (transformCenter, transformScale, transformOrigin, hasTransform) = (center, scale, origin, true);
    }

    internal Point ProjectedToScreen(Point p) =>
        new((p.X - transformCenter.X) * transformScale + transformOrigin.X, -(p.Y - transformCenter.Y) * transformScale + transformOrigin.Y);

    internal Point ScreenToProjected(Point s) =>
        new((s.X - transformOrigin.X) / transformScale + transformCenter.X, -(s.Y - transformOrigin.Y) / transformScale + transformCenter.Y);

    /// <summary>Where a model position is drawn, or null before the first render.</summary>
    internal Point? ToScreen(Position position) => hasTransform ? ProjectedToScreen(Project(position)) : null;

    /// <summary>The model position under a screen point, on the plane shown; null without a plane coordinate.</summary>
    internal Position? ToModel(Point screen) =>
        hasTransform && PlaneCoordinate is { } c && ViewPlanes.IsPlane(Plane) ? ViewPlanes.ToWorld(ScreenToProjected(screen), Plane, c) : null;

    private bool IsShown(StructuralMember member) => member.Kind == MemberKind.Frame ? ShowFrames : ShowCables;

    private Rect FrameFor(IEnumerable<Point> projected, ReferenceLines.Layout reference)
    {
        if (KeepViewOnEdit && frozenFrame is { } held) return held;
        var all = projected.Concat(reference.Lines.SelectMany(line => new[] { Project(line.A), Project(line.B) })).ToArray();
        if (KeepViewOnEdit && ExtentsModel is { } extents)
            all = [.. all, .. extents.Points.Values.Select(point => Project(point.Position))];
        var frame = all.Length == 0 ? new Rect(0, 0, 20, 12)
            : new Rect(new Point(all.Min(p => p.X), all.Min(p => p.Y)), new Point(all.Max(p => p.X), all.Max(p => p.Y)));
        if (KeepViewOnEdit) frozenFrame = frame;
        return frame;
    }

    private void ModelChanged(DependencyPropertyChangedEventArgs e)
    {
        // a slice of the whole model changes with the plane; the frame follows the whole model
        if (KeepViewOnEdit && ExtentsModel is not null) { InvalidateVisual(); RefreshToolOverlay(); return; }
        Reframe(e.OldValue as GeometryModel, e.NewValue as GeometryModel);
    }

    /// <summary>An edit keeps the view (KeepViewOnEdit) as long as the model is the same model — it shares points with the
    /// one before — and nothing of it is off the screen; anything else frames the model again.</summary>
    private void Reframe(GeometryModel? before, GeometryModel? after)
    {
        if (!KeepViewOnEdit || frozenFrame is null || !hasTransform || before is null || after is null || before.Points.Count == 0 ||
            !before.Points.Keys.Any(after.Points.ContainsKey))
        {
            Fit();
            return;
        }
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (after.Points.Values.Any(point => !bounds.Contains(ProjectedToScreen(Project(point.Position))))) Fit();
        else InvalidateVisual();
        RefreshToolOverlay();
    }

    private void ReferenceChanged(DependencyPropertyChangedEventArgs e)
    {
        referenceLayout = null;
        // a new or changed grid is shown whole, except over a model the user is zoomed into
        if (KeepViewOnEdit && (Model.Points.Count == 0 || e.OldValue is null)) Fit();
    }

    // ---- grids and levels ----
    private ReferenceLines.Layout? referenceLayout;
    private static readonly ReferenceLines.Layout NoReference = new([], []);

    private ReferenceLines.Layout ReferenceLayoutForRender()
    {
        if (ReferenceGeometry is not { IsEmpty: false } reference || !ViewPlanes.IsPlane(Plane) || !ShowGridLines && !ShowLevels)
            return referenceLayout = NoReference;
        var layout = ReferenceLines.For(reference, Plane, PlaneCoordinate, ExtentsModel ?? Model);
        var lines = layout.Lines.Where(line => line.Kind == ReferenceLineKind.Level ? ShowLevels : ShowGridLines).ToArray();
        var crossings = Plane == "XY" ? (ShowGridLines ? layout.Crossings : []) : ShowGridLines && ShowLevels ? layout.Crossings : [];
        return referenceLayout = new(lines, crossings);
    }

    private static readonly Brush ReferenceBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x9B, 0xAE)));
    private static readonly Brush LevelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x5F, 0x8F, 0x6A)));
    private static T Frozen<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }

    /// <summary>Grid lines thin and dashed under the model, with their bubbles; levels with their name and elevation. They
    /// are references: never picked as objects.</summary>
    private void DrawReference(DrawingContext dc, ReferenceLines.Layout layout, Func<Point, Point> screen)
    {
        if (layout.Lines.Count == 0) return;
        var gridPen = new Pen(ReferenceBrush, 0.8) { DashStyle = new DashStyle([6, 4], 0) };
        var levelPen = new Pen(LevelBrush, 0.8) { DashStyle = new DashStyle([10, 3, 2, 3], 0) };
        gridPen.Freeze();
        levelPen.Freeze();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Caption(string text, Brush brush, double size = 10) =>
            new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, dpi);
        foreach (var line in layout.Lines)
        {
            var (a, b) = (screen(Project(line.A)), screen(Project(line.B)));
            dc.DrawLine(line.Kind == ReferenceLineKind.Level ? levelPen : gridPen, a, b);
            if (!ShowReferenceLabels) continue;
            if (line.Kind == ReferenceLineKind.Level)
            {
                // the level's name and elevation at the right end
                var right = a.X >= b.X ? a : b;
                var tag = Caption($"{line.Label}  {ViewPlanes.Metres(line.A.Z)}", LevelBrush);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), null, new Rect(right.X + 4, right.Y - tag.Height / 2 - 1, tag.Width + 4, tag.Height + 2));
                dc.DrawText(tag, new Point(right.X + 6, right.Y - tag.Height / 2));
                continue;
            }
            // a bubble at the top end (a vertical line) or the left end (a horizontal one)
            var end = Math.Abs(a.Y - b.Y) >= Math.Abs(a.X - b.X) ? (a.Y < b.Y ? a : b) : (a.X < b.X ? a : b);
            var away = end - (end == a ? b : a);
            if (away.Length > 0) away.Normalize();
            var centre = end + away * 11;
            var text = Caption(line.Label, ReferenceBrush, 10);
            var radius = Math.Max(9, text.Width / 2 + 3);
            dc.DrawEllipse(Brushes.White, new Pen(ReferenceBrush, 1), centre, radius, 9);
            dc.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
        }
    }

    // ---- tools ----
    private Position? anchor;
    private string? anchorJoint;
    private Point? cursorScreen;
    private SnapCandidate? cursorTarget;

    private void ToolChanged()
    {
        anchor = null;
        anchorJoint = null;
        cursorTarget = null;
        UpdateCursor();
        RefreshToolOverlay();
    }

    private bool ToolActive => DrawingTool != DrawingTool.None && !PanMode;

    private bool ToolMouseDown(MouseButtonEventArgs e, Point position) =>
        ToolActive && e.ChangedButton == MouseButton.Left && ClickTool(position);

    /// <summary>A left click of the tool at <paramref name="position"/> — what the mouse handler does (and the checks call).</summary>
    internal bool ClickTool(Point position)
    {
        if (!ToolActive) return false;
        cursorScreen = position;
        var target = Resolve(position);
        if (target is null)
        {
            prompt = PlaneCoordinate is null
                ? "Choose a plane to draw in empty space (the plane box above the view), or click a point of the model"
                : "Nothing to pick here";
            RefreshToolOverlay();
            return true;
        }
        switch (DrawingTool)
        {
            case DrawingTool.Point:
                var placed = new PointPlacedEventArgs(target.World, target.Joint, target.Kind);
                PointPlaced?.Invoke(this, placed);
                break;
            case DrawingTool.Frame or DrawingTool.Cable when anchor is null:
                (anchor, anchorJoint) = (target.World, target.Joint);
                break;
            case DrawingTool.Frame or DrawingTool.Cable:
                var drawn = new MemberDrawnEventArgs(anchor!.Value, anchorJoint, target.World, target.Joint, DrawingTool == DrawingTool.Cable);
                MemberDrawn?.Invoke(this, drawn);
                if (drawn.Accepted && ChainDrawing) (anchor, anchorJoint) = (target.World, drawn.EndJoint);
                else if (drawn.Accepted) (anchor, anchorJoint) = (null, null);
                break;
            case DrawingTool.Move when SelectedJoints.Count + SelectedMembers.Count == 0:
                break;
            case DrawingTool.Move when anchor is null:
                (anchor, anchorJoint) = (target.World, target.Joint);
                break;
            case DrawingTool.Move:
                var move = new MoveRequestedEventArgs(anchor!.Value, target.World);
                MoveRequested?.Invoke(this, move);
                if (move.Accepted) (anchor, anchorJoint) = (null, null);
                break;
        }
        prompt = null;
        cursorTarget = Resolve(position);
        RefreshToolOverlay();
        return true;
    }

    /// <summary>The cursor over <paramref name="position"/>, as a mouse move (checks).</summary>
    internal void HoverTool(Point position) => ToolMouseMove(position);

    private void ToolMouseMove(Point position)
    {
        cursorScreen = position;
        // without a tool the cursor's coordinates are still read out, unsnapped
        cursorTarget = ToolActive ? Resolve(position) : ToModel(position) is { } free ? new(SnapKind.None, position, free, "") : null;
        CursorMoved?.Invoke(this, EventArgs.Empty);
        if (ToolActive) RefreshToolOverlay();
    }

    private void ToolMouseLeave()
    {
        cursorScreen = null;
        cursorTarget = null;
        CursorMoved?.Invoke(this, EventArgs.Empty);
        RefreshToolOverlay();
    }

    /// <summary>A right-click with a tool on: ends the chain (or the move) under way, else leaves the tool.</summary>
    internal bool ToolRightClick()
    {
        if (DrawingTool == DrawingTool.None) return false;
        if (anchor is not null) (anchor, anchorJoint) = (null, null);
        else DrawingTool = DrawingTool.None;
        RefreshToolOverlay();
        return true;
    }

    /// <summary>Esc: drops the member or move half made. The tool stays on (Esc never changes the tool).</summary>
    public bool CancelToolInteraction()
    {
        if (anchor is null) return false;
        (anchor, anchorJoint) = (null, null);
        RefreshToolOverlay();
        return true;
    }

    private string? prompt;

    /// <summary>The snap for a cursor position: Alt held — none; else the best candidate within reach; else, while a member
    /// is drawn with Shift held or Ortho on, the cursor held horizontal or vertical from its start; else the free position
    /// on the plane (none in a projection: the depth is not guessed).</summary>
    internal SnapCandidate? Resolve(Point cursor)
    {
        var free = ToModel(cursor);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return free is { } f ? new(SnapKind.None, cursor, f, "") : null;
        var picked = SnapResolver.Pick(cursor, Candidates(cursor), SnapModes);
        if (picked is not null) return picked;
        if (anchor is { } start && ToScreen(start) is { } from && (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || SnapModes.HasFlag(SnapModes.Ortho)))
        {
            var held = SnapResolver.Ortho(from, cursor);
            // along the plane of the start point, so a member drawn in a projection keeps its start's depth
            var uv = ScreenToProjected(held);
            var world = ViewPlanes.IsPlane(Plane) ? ViewPlanes.ToWorld(uv, Plane, PlaneCoordinate ?? ViewPlanes.Fixed(start, Plane)) : start;
            return new(SnapKind.Ortho, held, world, "Ortho");
        }
        return free is { } position ? new(SnapKind.None, cursor, position, "") : null;
    }

    private IEnumerable<SnapCandidate> Candidates(Point cursor)
    {
        const double reach = SnapResolver.RadiusPx;
        var list = new List<SnapCandidate>();
        foreach (var (name, at) in jointLocations)
            if ((at - cursor).Length <= reach && Model.Points.TryGetValue(name, out var point))
                list.Add(new(SnapKind.Point, at, point.Position, name, Joint: name));
        var segments = new List<SnapResolver.Segment>();
        foreach (var member in Model.Members)
            if (memberLocations.TryGetValue(member.Name, out var line) && Model.Points.TryGetValue(member.Start, out var a) && Model.Points.TryGetValue(member.End, out var b))
                segments.Add(new(member.Name, line.Start, line.End, a.Position, b.Position));
        // grid lines and crossings are on the plane only; in a projection their depth is not known
        if (PlaneCoordinate is not null && referenceLayout is { } layout)
        {
            foreach (var reference in layout.Lines)
                segments.Add(new(reference.Kind == ReferenceLineKind.Level ? $"level {reference.Label}" : $"grid {reference.Label}",
                    ProjectedToScreen(Project(reference.A)), ProjectedToScreen(Project(reference.B)), reference.A, reference.B, IsMember: false));
            foreach (var crossing in layout.Crossings)
            {
                var at = ProjectedToScreen(Project(crossing.At));
                if ((at - cursor).Length <= reach) list.Add(new(SnapKind.GridIntersection, at, crossing.At, $"Grid {crossing.Label}"));
            }
        }
        var start = anchor is { } from ? ToScreen(from) : null;
        list.AddRange(SnapResolver.AlongSegments(cursor, segments, start, anchor, reach));
        return list;
    }

    // ---- the preview, on the overlay layer ----
    internal void RefreshToolOverlay()
    {
        if (Overlay is null) return;
        if (!ToolActive || !hasTransform || Visibility != Visibility.Visible)
        {
            Overlay.Show(null);
            return;
        }
        var target = cursorTarget;
        var from = anchor is { } a ? ToScreen(a) : null;
        var ghost = DrawingTool == DrawingTool.Move && from is { } f && target is { } t ? t.Screen - f : (Vector?)null;
        var selectedLines = ghost is null ? [] : memberLocations.Where(item => selectedMembers.Contains(item.Key)).Select(item => (item.Value.Start, item.Value.End)).ToArray();
        var selectedPoints = ghost is null ? [] : jointLocations.Where(item => selectedJoints.Contains(item.Key)).Select(item => item.Value).ToArray();
        var text = prompt ?? ToolPrompt;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Overlay.Show(dc => DrawToolPreview(dc, text, from, anchor, target, ghost, selectedLines, selectedPoints, pixelsPerDip));
    }

    private static readonly Brush SnapBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x00)));
    private static readonly Brush PreviewBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2E, 0x90, 0xFA)));

    internal static void DrawToolPreview(DrawingContext dc, string prompt, Point? from, Position? fromWorld, SnapCandidate? target, Vector? ghost,
        IReadOnlyList<(Point Start, Point End)> selectedLines, IReadOnlyList<Point> selectedPoints, double pixelsPerDip)
    {
        FormattedText Caption(string text, double size = 11, Brush? brush = null) =>
            new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush ?? Brushes.Black, pixelsPerDip);
        // the instruction, under the view's title
        if (!string.IsNullOrEmpty(prompt))
        {
            var instruction = Caption(prompt + "   ·   Shift ortho · Alt no snap", 11, PreviewBrush);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), new Pen(PreviewBrush, 1), new Rect(12, 32, instruction.Width + 10, instruction.Height + 4));
            dc.DrawText(instruction, new Point(17, 34));
        }
        if (ghost is { } offset)
        {
            var pen = new Pen(PreviewBrush, 1.5) { DashStyle = DashStyles.Dash };
            foreach (var (start, end) in selectedLines) dc.DrawLine(pen, start + offset, end + offset);
            foreach (var point in selectedPoints) dc.DrawEllipse(null, pen, point + offset, 4, 4);
        }
        if (from is { } anchorAt)
        {
            dc.DrawEllipse(PreviewBrush, null, anchorAt, 3.5, 3.5);
            if (target is { } t)
            {
                dc.DrawLine(new Pen(PreviewBrush, 1.5) { DashStyle = DashStyles.Dash }, anchorAt, t.Screen);
                if (fromWorld is { } w)
                {
                    var (dx, dy, dz) = (t.World.X - w.X, t.World.Y - w.Y, t.World.Z - w.Z);
                    var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    var mid = new Point((anchorAt.X + t.Screen.X) / 2, (anchorAt.Y + t.Screen.Y) / 2);
                    var tag = Caption(string.Create(CultureInfo.CurrentCulture, $"L = {length:0.000} m"), 10, PreviewBrush);
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), null, new Rect(mid.X + 6, mid.Y - tag.Height - 4, tag.Width + 4, tag.Height + 2));
                    dc.DrawText(tag, new Point(mid.X + 8, mid.Y - tag.Height - 3));
                }
            }
        }
        if (target is not { } snap) return;
        DrawSnapGlyph(dc, snap);
        // the snap and the model coordinates beside the cursor
        // "Point P3", "Midpoint of F2", "Grid A/3", "Free" …
        var head = snap.Kind switch
        {
            SnapKind.Point => $"Point {snap.Joint}",
            SnapKind.None or SnapKind.Ortho => SnapResolver.Describe(snap.Kind),
            _ => snap.Label,
        };
        var connects = snap.Members is { Count: > 0 } members && snap.Kind is not SnapKind.Point
            ? $"\na joint here connects to {string.Join(", ", members)}" : "";
        var body = Caption(string.Create(CultureInfo.CurrentCulture,
            $"{head}\nX {snap.World.X:0.000}  Y {snap.World.Y:0.000}  Z {snap.World.Z:0.000} m{connects}"), 10);
        var box = new Rect(snap.Screen.X + 14, snap.Screen.Y + 12, body.Width + 10, body.Height + 6);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), new Pen(SnapBrush, 1), box, 3, 3);
        dc.DrawText(body, new Point(box.X + 5, box.Y + 3));
    }

    /// <summary>The glyph of a snap: a square on a point, a triangle on a midpoint, a cross on a crossing, a circle on a grid
    /// crossing, the right-angle mark on a perpendicular, an hourglass on the nearest position, a small cross when free.</summary>
    private static void DrawSnapGlyph(DrawingContext dc, SnapCandidate snap)
    {
        var pen = new Pen(SnapBrush, 1.8);
        var p = snap.Screen;
        switch (snap.Kind)
        {
            case SnapKind.Point:
                dc.DrawRectangle(null, pen, new Rect(p.X - 6, p.Y - 6, 12, 12));
                break;
            case SnapKind.Midpoint:
                dc.DrawGeometry(null, pen, System.Windows.Media.Geometry.Parse(string.Create(CultureInfo.InvariantCulture, $"M{p.X},{p.Y - 7} L{p.X + 7},{p.Y + 5} L{p.X - 7},{p.Y + 5} Z")));
                break;
            case SnapKind.Intersection:
                dc.DrawLine(pen, p + new Vector(-6, -6), p + new Vector(6, 6));
                dc.DrawLine(pen, p + new Vector(-6, 6), p + new Vector(6, -6));
                break;
            case SnapKind.GridIntersection:
                dc.DrawEllipse(null, pen, p, 6.5, 6.5);
                dc.DrawLine(pen, p + new Vector(-9, 0), p + new Vector(9, 0));
                dc.DrawLine(pen, p + new Vector(0, -9), p + new Vector(0, 9));
                break;
            case SnapKind.Perpendicular:
                dc.DrawLine(pen, p + new Vector(-7, 6), p + new Vector(7, 6));
                dc.DrawLine(pen, p + new Vector(-7, 6), p + new Vector(-7, -8));
                dc.DrawRectangle(null, pen, new Rect(p.X - 7, p.Y, 6, 6));
                break;
            case SnapKind.Nearest or SnapKind.GridLine:
                dc.DrawGeometry(null, pen, System.Windows.Media.Geometry.Parse(string.Create(CultureInfo.InvariantCulture,
                    $"M{p.X - 6},{p.Y - 6} L{p.X + 6},{p.Y - 6} L{p.X - 6},{p.Y + 6} L{p.X + 6},{p.Y + 6} Z")));
                break;
            default:
                var thin = new Pen(SnapBrush, 1);
                dc.DrawLine(thin, p + new Vector(-5, 0), p + new Vector(5, 0));
                dc.DrawLine(thin, p + new Vector(0, -5), p + new Vector(0, 5));
                break;
        }
    }
}
