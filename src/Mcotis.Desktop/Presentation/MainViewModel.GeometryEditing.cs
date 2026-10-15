using Mcotis.Application;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Presentation.Controls;
using Mcotis.Desktop.Presentation.GeometryEditing;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Presentation;

// ---- Geometry editing in the main window (docs/geometry/GEOMETRY_EDITING_PLAN.md, GRID_LEVEL_CONTROLLER.md) ----
// One view context — the plane (3D, XY, XZ, YZ) and its coordinate — shared by the toolbar, the plane navigator and the
// viewer's own projection box; the work plane a tool draws on in 3D; grids and levels as data of the document; and the
// edits — Add Point, Draw Frame / Cable, Move, Delete, Generate Points — each one step of the model's history. The rules
// are GeometryEditService's (Mcotis.Application), checked on the native document; the main window's model is then
// changed as every legacy-first edit is, and the document takes the step under its name (SyncNativeDocument). Grids and
// levels are the document's own: they move nothing and leave the results as they are.
public sealed partial class MainViewModel
{
    // ---- the view context ----
    // the viewer opens on XZ; the window turns it to 3D at startup (M9), and the binding brings that here
    private string viewPlane = "XZ";
    private double? viewCoordinate;
    private readonly Dictionary<string, double?> lastViewCoordinate = new(StringComparer.Ordinal);
    private (string Plane, double Coordinate)? workPlane;

    /// <summary>The projection of the model view: 3D, XY, XZ or YZ — bound both ways to the viewer, whose projection box,
    /// toolbar buttons and Display Options all set it. A view only: it changes no degree of freedom of an analysis.</summary>
    public string ViewPlane
    {
        get => viewPlane;
        set
        {
            if (string.IsNullOrEmpty(value) || value == viewPlane) return;
            if (ViewPlanes.IsPlane(viewPlane)) lastViewCoordinate[viewPlane] = viewCoordinate;
            viewPlane = value;
            // back to a plane: the coordinate it was left at; a first visit: the coordinate every point shares, else all
            viewCoordinate = !ViewPlanes.IsPlane(value) ? null
                : lastViewCoordinate.TryGetValue(value, out var last) ? last : ViewPlanes.DefaultCoordinate(SharedViewerModel, value);
            RememberWorkPlane();
            NotifyViewPlane();
        }
    }

    /// <summary>The fixed coordinate of the plane shown (null: the whole model projected).</summary>
    public double? ViewCoordinate
    {
        get => viewCoordinate;
        set
        {
            if (!ViewPlanes.IsPlane(viewPlane)) return;
            if (value is { } c && !double.IsFinite(c)) return;
            if (Nullable.Equals(value, viewCoordinate)) return;
            viewCoordinate = value;
            RememberWorkPlane();
            NotifyViewPlane();
        }
    }

    private void RememberWorkPlane()
    {
        if (ViewPlanes.IsPlane(viewPlane) && viewCoordinate is { } c) workPlane = (viewPlane, c);
    }

    public bool ViewPlaneIsPlane => ViewPlanes.IsPlane(viewPlane);
    public string ViewPlaneAxisLabel => ViewPlanes.IsPlane(viewPlane) ? $"{ViewPlanes.FixedAxis(viewPlane)} =" : "3D";

    /// <summary>The plane navigator: the projection, the named levels or grid lines of the plane, the coordinates of the
    /// model's points.</summary>
    public IReadOnlyList<PlaneChoice> ViewPlaneChoices => ViewPlanes.Choices(viewPlane, SharedViewerReference, SharedViewerModel);

    public PlaneChoice? SelectedViewPlaneChoice
    {
        get => ViewPlanes.IsPlane(viewPlane) ? ViewPlanes.ChoiceFor(ViewPlaneChoices, viewPlane, viewCoordinate) : null;
        set { if (value is not null) ViewCoordinate = value.Coordinate; }
    }

    /// <summary>"XY plan · L2 · Z = +8.000 m": the view's own name, on the canvas and in the status bar.</summary>
    public string ViewPlaneTitle => ViewPlanes.IsPlane(viewPlane) ? ViewPlanes.Describe(viewPlane, SelectedViewPlaneChoice)
        : workPlane is { } plane ? $"3D · work plane {ViewPlanes.Describe(plane.Plane, ViewPlanes.ChoiceFor(ViewPlanes.Choices(plane.Plane, SharedViewerReference, SharedViewerModel), plane.Plane, plane.Coordinate))}"
        : "3D · no work plane (choose a plane in 2D to draw in empty space)";

    /// <summary>What the view draws: the members lying in the plane, or the whole model.</summary>
    public GeometryModel SharedViewerVisibleModel => ViewPlanes.Slice(SharedViewerModel, viewPlane, viewCoordinate);
    public Native.ReferenceGeometry SharedViewerReference => NativeDocumentSession.Document.ReferenceGeometry;
    public double? SharedViewerPlaneCoordinate => ViewPlanes.IsPlane(viewPlane) ? viewCoordinate : null;
    public string? SharedViewerWorkPlane => workPlane?.Plane;
    public double? SharedViewerWorkCoordinate => workPlane?.Coordinate;

    /// <summary>Previous / next plane of the navigator (the projection is not a step). False at either end.</summary>
    internal bool StepViewPlane(int direction)
    {
        if (!ViewPlanes.IsPlane(viewPlane)) return false;
        var coordinates = ViewPlaneChoices.Where(choice => choice.Coordinate is not null).Select(choice => choice.Coordinate!.Value).ToArray();
        if (coordinates.Length == 0) return false;
        double next;
        if (viewCoordinate is not { } current) next = direction > 0 ? coordinates[0] : coordinates[^1];
        else
        {
            var candidates = direction > 0 ? coordinates.Where(c => c > current + ViewPlanes.Tolerance).ToArray() : coordinates.Where(c => c < current - ViewPlanes.Tolerance).ToArray();
            if (candidates.Length == 0) return false;
            next = direction > 0 ? candidates.Min() : candidates.Max();
        }
        ViewCoordinate = next;
        return true;
    }

    private Native.ObjectId? viewDocumentId;

    /// <summary>Another model in the window (New, Open, Load, Import): its planes start afresh — a coordinate of the model
    /// before must not hide the new one — and no tool is left on.</summary>
    private void SyncViewContextToDocument()
    {
        var id = NativeDocumentSession.Document.DocumentId;
        if (viewDocumentId == id) return;
        viewDocumentId = id;
        lastViewCoordinate.Clear();
        workPlane = null;
        if (ViewPlanes.IsPlane(viewPlane)) viewCoordinate = ViewPlanes.DefaultCoordinate(SharedViewerModel, viewPlane);
        RememberWorkPlane();
        if (drawingTool != DrawingTool.None) { drawingTool = DrawingTool.None; Notify(nameof(DrawingTool)); Notify(nameof(DrawingToolText)); }
    }

    private void NotifyViewPlane()
    {
        SyncViewContextToDocument();
        foreach (var name in new[]
                 {
                     nameof(ViewPlane), nameof(ViewCoordinate), nameof(ViewPlaneIsPlane), nameof(ViewPlaneAxisLabel), nameof(ViewPlaneChoices),
                     nameof(SelectedViewPlaneChoice), nameof(ViewPlaneTitle), nameof(SharedViewerVisibleModel), nameof(SharedViewerReference),
                     nameof(SharedViewerPlaneCoordinate), nameof(SharedViewerWorkPlane), nameof(SharedViewerWorkCoordinate), nameof(CanDrawGeometry),
                     nameof(DrawFrameSection), nameof(DrawCableSection), nameof(SharedViewerFemResult),
                     nameof(ShowingAxial), nameof(ShowingShear), nameof(ShowingMoment),
                 })
            Notify(name);
    }

    // ---- display (Display Options, Ctrl+W): presentation only, never model or analysis data ----
    private bool showJoints = true, showFrames = true, showCables = true, showGridLines = true, showLevels = true, showReferenceLabels = true;
    public bool ShowJoints { get => showJoints; set => Set(ref showJoints, value); }
    public bool ShowFrames { get => showFrames; set => Set(ref showFrames, value); }
    public bool ShowCables { get => showCables; set => Set(ref showCables, value); }
    public bool ShowGridLines { get => showGridLines; set => Set(ref showGridLines, value); }
    public bool ShowLevels { get => showLevels; set => Set(ref showLevels, value); }
    public bool ShowReferenceLabels { get => showReferenceLabels; set => Set(ref showReferenceLabels, value); }

    // ---- the drawing tools ----
    private DrawingTool drawingTool;
    private SnapModes snapModes = SnapResolver.Defaults;
    private bool chainDrawing = true;
    private string? drawFrameSection, drawCableSection;

    /// <summary>The tool of the model view (bound both ways to the viewer: a right-click there leaves the tool).</summary>
    public DrawingTool DrawingTool
    {
        get => drawingTool;
        set
        {
            if (value != DrawingTool.None && !CanDrawGeometry) value = DrawingTool.None;
            if (!Set(ref drawingTool, value)) return;
            Notify(nameof(DrawingToolText));
            Notify(nameof(IsDrawingFrame));
            Notify(nameof(IsDrawingCable));
        }
    }
    public SnapModes SnapModes
    {
        get => snapModes;
        set
        {
            if (!Set(ref snapModes, value)) return;
            Notify(nameof(DrawingToolText));
            foreach (var name in new[] { nameof(SnapToPoints), nameof(SnapToMidpoints), nameof(SnapToIntersections), nameof(SnapToGrid), nameof(SnapToGridLines),
                         nameof(SnapPerpendicular), nameof(SnapNearest), nameof(SnapOrtho) })
                Notify(name);
        }
    }
    private bool Snap(SnapModes flag) => snapModes.HasFlag(flag);
    private void Snap(SnapModes flag, bool on) => SnapModes = on ? snapModes | flag : snapModes & ~flag;
    // Draw → Snap To: one switch per kind of snap (user settings of the view, never model data)
    public bool SnapToPoints { get => Snap(SnapModes.Point); set => Snap(SnapModes.Point, value); }
    public bool SnapToMidpoints { get => Snap(SnapModes.Midpoint); set => Snap(SnapModes.Midpoint, value); }
    public bool SnapToIntersections { get => Snap(SnapModes.Intersection); set => Snap(SnapModes.Intersection, value); }
    public bool SnapToGrid { get => Snap(SnapModes.GridIntersection); set => Snap(SnapModes.GridIntersection, value); }
    public bool SnapToGridLines { get => Snap(SnapModes.GridLine); set => Snap(SnapModes.GridLine, value); }
    public bool SnapPerpendicular { get => Snap(SnapModes.Perpendicular); set => Snap(SnapModes.Perpendicular, value); }
    public bool SnapNearest { get => Snap(SnapModes.Nearest); set => Snap(SnapModes.Nearest, value); }
    public bool SnapOrtho { get => Snap(SnapModes.Ortho); set => Snap(SnapModes.Ortho, value); }

    /// <summary>A refusal or a result of a canvas tool, in the status bar (no dialog for an ordinary operation).</summary>
    internal void ReportEditing(string text) => Message = text;

    /// <summary>The section boxes of the toolbar show while the tool that uses them is on.</summary>
    public bool IsDrawingFrame => drawingTool == DrawingTool.Frame;
    public bool IsDrawingCable => drawingTool == DrawingTool.Cable;

    // ---- N / V / M on the toolbar ----

    /// <summary>The force diagram on the view: axial, shear or moment of the case chosen, while a result is drawn.</summary>
    public bool ShowingAxial => ShowingForces(FemResultMode.AxialForce);
    public bool ShowingShear => ShowingForces(FemResultMode.Shear);
    public bool ShowingMoment => ShowingForces(FemResultMode.BendingMoment);
    private bool ShowingForces(FemResultMode mode) => SharedViewerFemResult is not null && SharedViewerFemResultMode == mode;

    /// <summary>The toolbar's N, V or M: that diagram of the chosen case or combination on the view, in any plane (the shear
    /// and moment of the plane shown, <see cref="PlaneFrameForces"/>). The left pane stays as it is.</summary>
    internal void ShowForces(FemResultMode mode)
    {
        if (!HasFemResults) return;
        ShowFemResult(mode);
        NotifyResultButtons();
        NotifySharedViewer();
    }

    private void NotifyResultButtons()
    {
        Notify(nameof(ShowingAxial));
        Notify(nameof(ShowingShear));
        Notify(nameof(ShowingMoment));
    }

    /// <summary>The result the view draws: in an XY plan or a YZ elevation, the frames' shear and moment are those of that
    /// plane; the XZ elevation and 3D draw the result as it is.</summary>
    private FemAnalysisResult? ViewedFemResult
    {
        get
        {
            var result = SelectedFemResult;
            if (result is null || viewPlane is not ("XY" or "YZ")) return result;
            if (planeResultCache is { } cached && ReferenceEquals(cached.Source, result) && cached.Plane == viewPlane) return cached.Projected;
            var projected = PlaneFrameForces.For(result, SelectedFemRun?.Native, SelectedFemResultName, viewPlane,
                FemCombinationRows.Select(row => (row.Name, row.Definition)).ToArray());
            planeResultCache = (result, viewPlane, projected);
            return projected;
        }
    }
    private (FemAnalysisResult Source, string Plane, FemAnalysisResult Projected)? planeResultCache;
    public bool ChainDrawing { get => chainDrawing; set => Set(ref chainDrawing, value); }
    public bool CanDrawGeometry => CanEditFemModel;

    public IReadOnlyList<string> DrawFrameSectionChoices => FrameSections.Select(section => section.Name).ToArray();
    public IReadOnlyList<string> DrawCableSectionChoices => CableSections.Select(section => section.Name).ToArray();
    /// <summary>The section a drawn frame (cable) gets: the one chosen, else the first of the library.</summary>
    public string? DrawFrameSection
    {
        get => drawFrameSection is { } name && FrameSections.Any(section => section.Name == name) ? name : FrameSections.FirstOrDefault()?.Name;
        // a list that is filled again clears its selection for a moment: that is no choice of the user's
        set { if (value is not null) Set(ref drawFrameSection, value); }
    }
    public string? DrawCableSection
    {
        get => drawCableSection is { } name && CableSections.Any(section => section.Name == name) ? name : CableSections.FirstOrDefault()?.Name;
        set { if (value is not null) Set(ref drawCableSection, value); }
    }

    /// <summary>The status bar's mode and snaps: "Draw Frame · snaps Point, Midpoint, Grid".</summary>
    public string DrawingToolText
    {
        get
        {
            var mode = drawingTool switch
            {
                DrawingTool.Point => "Draw Point",
                DrawingTool.Frame => "Draw Frame",
                DrawingTool.Cable => "Draw Cable",
                DrawingTool.Move => "Move",
                _ => "Select",
            };
            if (drawingTool == DrawingTool.None) return mode;
            var snaps = Enum.GetValues<SnapModes>().Where(flag => flag != SnapModes.None && snapModes.HasFlag(flag))
                .Select(flag => flag == SnapModes.GridIntersection ? "Grid" : flag == SnapModes.GridLine ? "Grid line" : flag.ToString()).ToArray();
            return $"{mode} · snaps {(snaps.Length == 0 ? "off" : string.Join(", ", snaps))}";
        }
    }

    // ---- edits ----

    /// <summary>Draw → Point: a point at <paramref name="position"/>, named P1, P2 … unless named. A point already within
    /// the model's tolerance there is not doubled: the request is refused, naming it. One step of the history.</summary>
    internal string AddPoint(Position position, string? name = null)
    {
        RequireEditable();
        var document = NativeDocumentSession.Document;
        var at = new Native.Xyz(position.X, position.Y, position.Z);
        if (GeometryEditService.Resolve(document, at).ExistingId is { } existing)
            throw new InvalidOperationException($"Point '{LegacyNames.Of(document)[existing]}' is already at ({Coordinates(position)}): a second point there would not be connected to it.");
        var taken = TakenObjectNames();
        var pointName = name is null ? NextObjectName(Native.ObjectKind.Point, taken) : RequireFreeName(name, taken);
        var points = new Dictionary<string, StructuralPoint>(State.Geometry.Points, StringComparer.Ordinal) { [pointName] = new(pointName, position) };
        CommitGeometryEdit("Add Point", State.Geometry with { Points = points });
        Message = $"Added point {pointName} at ({Coordinates(position)}) — Edit → Undo takes it back.";
        return pointName;
    }

    /// <summary>Generate Points (Draw menu): a point at every given position where the model has none (within its
    /// tolerance), as one step. Returns how many were made.</summary>
    internal int GeneratePoints(IEnumerable<Position> positions, string stepName = "Generate Points")
    {
        RequireEditable();
        var (_, made) = GeometryEditService.AddPoints(NativeDocumentSession.Document, positions.Select(p => new Native.Xyz(p.X, p.Y, p.Z)), stepName);
        if (made.Length == 0)
        {
            Message = "Every position already has a point: nothing was added.";
            return 0;
        }
        var taken = TakenObjectNames();
        var points = new Dictionary<string, StructuralPoint>(State.Geometry.Points, StringComparer.Ordinal);
        foreach (var position in made)
        {
            var pointName = NextObjectName(Native.ObjectKind.Point, taken);
            taken.Add(pointName);
            points[pointName] = new(pointName, new(position.X, position.Y, position.Z));
        }
        CommitGeometryEdit(stepName, State.Geometry with { Points = points });
        Message = $"{stepName}: {made.Length} point{(made.Length == 1 ? "" : "s")} added — Edit → Undo takes them back.";
        return made.Length;
    }

    /// <summary>Draw → Frame / Cable: a member from <paramref name="start"/> to <paramref name="end"/>. An end on a point of
    /// the model (named, or within its tolerance) uses that point; otherwise a new point is made there. A member without
    /// length, one that would double a member or lie along one is refused with the reason. The member takes the drawing
    /// section (<see cref="DrawFrameSection"/> / <see cref="DrawCableSection"/>). Returns the member and the point it ends
    /// on, so a chain goes on from it. One step of the history; the results go, as with any change of the model.</summary>
    internal (string Member, string EndJoint) DrawMember(Position start, string? startJoint, Position end, string? endJoint, bool cable, string? sectionName = null)
    {
        RequireEditable();
        EnsureMaterials();
        EnsureSections();
        var document = NativeDocumentSession.Document;
        var names = LegacyNames.Of(document);
        PointUse Use(Position position, string? joint) => joint is not null && names.Point(joint) is { } id
            ? new(id, document.Points[id].Position)
            : GeometryEditService.Resolve(document, new(position.X, position.Y, position.Z));
        FramePlan plan;
        try { plan = GeometryEditService.PlanFrame(document, Use(start, startJoint), Use(end, endJoint)); }
        catch (Native.DocumentException ex) { throw new InvalidOperationException(ex.Message, ex); }

        // the section: the library's, as Assign → Sections gives it (material, area, inertias, outline)
        var section = sectionName ?? (cable ? DrawCableSection : DrawFrameSection);
        var frameSection = cable ? null : FrameSections.FirstOrDefault(item => item.Name == section);
        var strand = cable ? CableSections.FirstOrDefault(item => item.Name == section) : null;
        if (frameSection is null && strand is null)
            throw new InvalidOperationException(cable
                ? "Define a cable section first (Define → Section Properties → Cable Sections), then draw the cable."
                : "Define a frame section first (Define → Section Properties → Frame Sections), then draw the frame.");

        var taken = TakenObjectNames();
        var points = new Dictionary<string, StructuralPoint>(State.Geometry.Points, StringComparer.Ordinal);
        string End(PointUse use)
        {
            if (use.ExistingId is { } id) return names[id];
            var made = NextObjectName(Native.ObjectKind.Point, taken);
            taken.Add(made);
            points[made] = new(made, new(use.Position.X, use.Position.Y, use.Position.Z));
            return made;
        }
        var (from, to) = (End(plan.Start), End(plan.End));
        var memberName = NextObjectName(cable ? Native.ObjectKind.Cable : Native.ObjectKind.Frame, taken);
        var members = State.Geometry.Members.Append(new StructuralMember(memberName, from, to, cable ? MemberKind.Cable : MemberKind.Frame)).ToArray();
        // the section's own values, through the rule Assign → Sections uses; the template's 1s must not survive into it
        var template = new FemSectionProperties(memberName, 0, 0, cable ? null : 0);
        var property = frameSection is not null ? PropertiesFor(template, frameSection) : PropertiesFor(template, strand!);
        if (!(property.AreaM2 > 0) || !(property.ElasticModulusKNPerM2 > 0) || !cable && !(property.InPlaneInertiaM4 > 0))
            throw new InvalidOperationException($"Section '{section}' does not give the member an area, a modulus and an inertia above zero: " +
                "check its dimensions and that its material is defined (Define → Materials).");
        CommitGeometryEdit(cable ? "Draw Cable" : "Draw Frame", new GeometryModel(points, members), () => FemSectionProperties.Add(property));
        Message = $"Drew {(cable ? "cable" : "frame")} {memberName} from {from} to {to} ({plan.Length:0.###} m, {section})" +
                  (plan.NewPoints > 0 ? $", {plan.NewPoints} new point{(plan.NewPoints == 1 ? "" : "s")}" : "") + " — Edit → Undo takes it back.";
        return (memberName, to);
    }

    /// <summary>Edit → Move: the selected points and the end points of the selected members, each once, by
    /// <paramref name="offset"/>; the members on them follow. A move that would put a point on another or collapse a member
    /// is refused. One step of the history. Returns how many points moved.</summary>
    internal int MoveObjects(IReadOnlyCollection<string> joints, IReadOnlyCollection<string> members, Position offset)
    {
        RequireEditable();
        var document = NativeDocumentSession.Document;
        var names = LegacyNames.Of(document);
        var ids = joints.Select(names.Point).Concat(members.Select(name => names.Frame(name) ?? names.Cable(name)))
            .Where(id => id is not null).Select(id => id!.Value).ToArray();
        MovePlan plan;
        try { plan = GeometryEditService.PlanMove(document, ids, new(offset.X, offset.Y, offset.Z)); }
        catch (Native.DocumentException ex) { throw new InvalidOperationException(ex.Message, ex); }
        var moved = plan.PointIds.Select(id => names[id]).ToHashSet(StringComparer.Ordinal);
        var points = State.Geometry.Points.ToDictionary(item => item.Key, item => moved.Contains(item.Key)
            ? item.Value with { Position = new(item.Value.Position.X + offset.X, item.Value.Position.Y + offset.Y, item.Value.Position.Z + offset.Z) }
            : item.Value, StringComparer.Ordinal);
        CommitGeometryEdit("Move", State.Geometry with { Points = points }, () =>
        {
            // the loaded points of the storey loads keep their coordinates with their joints
            foreach (var applied in State.AppliedPoints.Where(applied => moved.Contains(applied.PointName)))
                (applied.X, applied.Y, applied.Z) = (applied.X + offset.X, applied.Y + offset.Y, applied.Z + offset.Z);
        }, recaptureTruss: true);
        Message = $"Moved {moved.Count} point{(moved.Count == 1 ? "" : "s")} by ({Coordinates(offset)}) m; {plan.MembersChanged.Length} member{(plan.MembersChanged.Length == 1 ? "" : "s")} follow — Edit → Undo takes it back.";
        return moved.Count;
    }

    /// <summary>What Edit → Delete takes with the selection, in words for its confirmation: the members on deleted points,
    /// the loads, restraints and masses that go with them. Planned on the document (GeometryEditService.PlanDelete).</summary>
    internal string DeleteConsequences(IReadOnlyCollection<string> joints, IReadOnlyCollection<string> members)
    {
        var document = NativeDocumentSession.Document;
        var names = LegacyNames.Of(document);
        var ids = joints.Select(names.Point).Concat(members.Select(name => names.Frame(name) ?? names.Cable(name)))
            .Where(id => id is not null).Select(id => id!.Value).ToArray();
        var plan = GeometryEditService.PlanDelete(document, ids);
        var parts = new List<string>();
        if (plan.ConnectedMembers.Length > 0)
            parts.Add($"{plan.ConnectedMembers.Length} member{(plan.ConnectedMembers.Length == 1 ? "" : "s")} on the deleted points ({string.Join(", ", plan.ConnectedMembers.Take(8).Select(id => names.TryGetName(id, out var name) ? name : "?"))}{(plan.ConnectedMembers.Length > 8 ? ", …" : "")})");
        if (plan.Loads.Length > 0) parts.Add($"{plan.Loads.Length} load{(plan.Loads.Length == 1 ? "" : "s")} on the deleted objects");
        if (plan.Restraints > 0) parts.Add($"{plan.Restraints} restraint{(plan.Restraints == 1 ? "" : "s")}");
        if (plan.JointMasses > 0) parts.Add($"{plan.JointMasses} joint mass{(plan.JointMasses == 1 ? "" : "es")}");
        return parts.Count == 0 ? "Nothing else goes with them; the end points of a deleted member stay." : "With them go: " + string.Join("; ", parts) + ". The end points of a deleted member stay.";
    }

    /// <summary>Define → Grid Systems / Levels → OK or Apply: the grids and levels of the model as one step of the
    /// document's history. They are reference data: no point moves, the results stay current — so they can be changed on
    /// a model its results lock, too (unlocking would delete results a grid does not touch).</summary>
    internal bool ApplyReferenceGeometry(Native.ReferenceGeometry reference, string stepName = "Edit Grids and Levels")
    {
        if (IsBusy) throw new InvalidOperationException("Another operation is running; try again when it has finished.");
        if (NativeDocumentProblem is { } problem) throw new InvalidOperationException($"The model has no native document to hold grids: {problem}");
        Native.DocumentChangeSet changes;
        try { changes = NativeDocumentSession.Execute(GeometryEditService.SetReferenceGeometry(reference, stepName)); }
        catch (Native.DocumentException ex) { throw new InvalidOperationException(ex.Message, ex); }
        if (changes.IsEmpty)
        {
            Message = "The grids and levels are as they were.";
            return false;
        }
        var applied = NativeDocumentSession.Document.ReferenceGeometry;
        Message = $"{stepName}: {applied.GridSystems.Length} grid system{(applied.GridSystems.Length == 1 ? "" : "s")} " +
                  $"({applied.GridSystems.Sum(system => system.XLines.Length + system.YLines.Length)} lines), {applied.Levels.Length} level{(applied.Levels.Length == 1 ? "" : "s")} — no point moved; the results stay current.";
        NotifyViewPlane();
        NotifyModelHistory();
        return true;
    }

    // ---- internals ----

    private void RequireEditable()
    {
        if (!CanEditFemModel) throw new InvalidOperationException(IsFemModelLocked
            ? "The model is locked by its results: unlock it (Run → Unlock FEM Model) to edit it."
            : "The model cannot be edited now (busy, or without geometry).");
        if (NativeDocumentProblem is { } problem) throw new InvalidOperationException($"The model has no native document to edit: {problem}");
    }

    /// <summary>Every name the objects of the model use (points, frames and cables share one name space), any case.</summary>
    private HashSet<string> TakenObjectNames()
    {
        var taken = new HashSet<string>(State.Geometry.Points.Keys.Concat(State.Geometry.Members.Select(member => member.Name)), Native.NamingService.Comparer);
        foreach (var point in NativeDocumentSession.Document.Points.Values) taken.Add(point.Name);
        foreach (var frame in NativeDocumentSession.Document.Frames.Values) taken.Add(frame.Name);
        foreach (var cable in NativeDocumentSession.Document.Cables.Values) taken.Add(cable.Name);
        return taken;
    }

    /// <summary>The next automatic name of a kind — P, F or C and the number after the highest the model's sequence has
    /// reached — that no object uses.</summary>
    private string NextObjectName(Native.ObjectKind kind, HashSet<string> taken)
    {
        var start = NativeDocumentSession.Document.Names.LastNumber(kind) + 1;
        return Native.NamingService.NextFree(Native.NamingService.Prefix(kind), taken.Contains, ref start);
    }

    private static string RequireFreeName(string name, HashSet<string> taken)
    {
        if (!Native.NamingService.TryNormalize(name, out var normalized)) throw new InvalidOperationException("A name cannot be empty or blank, and cannot hold control characters.");
        if (taken.Contains(normalized)) throw new InvalidOperationException($"The name '{normalized}' is already used by an object of the model.");
        return normalized;
    }

    /// <summary>Makes <paramref name="edited"/> the geometry of the main window's model and the edit one step of the model's
    /// history, named <paramref name="stepName"/> (Edit → Undo names it). The results go, as with any change of the model.</summary>
    private void CommitGeometryEdit(string stepName, GeometryModel edited, Action? alsoChange = null, bool recaptureTruss = false)
    {
        // a model read from SAP2000 becomes an offline model: SAP2000 is never written
        if (State.IsSapConnected)
        {
            offlineLoadCaseNames = femSapLoading.Cases.Select(item => item.Name).ToArray();
            isDefaultModel = false;
            State.Connection = "Offline model";
        }
        var truss = State.Truss;
        var (deltaMin, deltaMax, step, deltaCurrent) = (truss.MinimumHeight - truss.BaselineHeight, truss.MaximumHeight - truss.BaselineHeight,
            truss.Increment, truss.CurrentHeight - truss.BaselineHeight);
        var captured = State.BaselineCaptured;
        State.Geometry = edited;
        State.Baseline = edited;
        State.SessionBaseline = edited;
        State.Preview = edited;
        applyingProject = true;
        try
        {
            alsoChange?.Invoke();
            // a VCF system's chord heights follow a joint that moved (as Delete keeps them)
            if (recaptureTruss && captured)
            {
                try
                {
                    workflow.CaptureBaseline();
                    truss.MinimumHeight = truss.BaselineHeight + deltaMin;
                    truss.MaximumHeight = truss.BaselineHeight + deltaMax;
                    truss.Increment = step;
                    truss.CurrentHeight = truss.BaselineHeight + deltaCurrent;
                    State.CommittedHeight = truss.BaselineHeight;
                    workflow.UpdatePreview();
                }
                catch (InvalidOperationException) { /* the chord groups no longer read as a truss: the VCF form says so */ }
            }
        }
        finally { applyingProject = false; }
        ClearFemResults();
        femWorkingDirty = true;
        nextStepName = stepName;
        PersistFemWorkingSnapshot(advanceRevision: true);
        Notify(nameof(SharedViewerSpanPoints));
        Notify(nameof(SharedViewerMemberSizes));
        Notify(nameof(SharedViewerSections));
        Notify(nameof(SharedViewerPinSupportPoints));
        Update();
    }

    private static string Coordinates(Position p) => string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{p.X:0.###}, {p.Y:0.###}, {p.Z:0.###}");

    /// <summary>A step of the history that changed only the grids and levels: Undo / Redo of it leaves the main window's
    /// model — and its results — as they are.</summary>
    private static bool OnlyReferenceDiffers(Native.ModelDocument before, Native.ModelDocument after) =>
        before.DocumentId == after.DocumentId && before.AnalysisInputHash == after.AnalysisInputHash &&
        before.ApplicationData.SequenceEqual(after.ApplicationData) && Equals(before.Generator, after.Generator) &&
        before.Points.Count == after.Points.Count && before.Points.All(item => after.Points.TryGetValue(item.Key, out var point) && point.Name == item.Value.Name) &&
        !before.ReferenceGeometry.Equals(after.ReferenceGeometry);

    /// <summary>The document with <paramref name="reference"/> as its grids and levels (the configuration a document is
    /// made from has no place for them: they are carried from the document it replaces).</summary>
    private static Native.ModelDocument WithReferenceGeometry(Native.ModelDocument document, Native.ReferenceGeometry reference)
    {
        if (document.ReferenceGeometry.Equals(reference)) return document;
        var editor = document.Edit();
        editor.SetReferenceGeometry(reference);
        return editor.Commit().Document;
    }

    /// <summary>The grids and levels the next document made from a configuration takes (a native document opened into the
    /// window: TemplateBuilding, a .mcotis without the window's configuration).</summary>
    private Native.ReferenceGeometry? carriedReferenceGeometry;
}
