using System.Collections.Immutable;
using System.Globalization;
using Mcotis.Core;

namespace Mcotis.Application;

/// <summary>One end of a member about to be drawn: a point the model has, or a new point at a position.</summary>
public readonly record struct PointUse(ObjectId? ExistingId, Xyz Position)
{
    public bool IsNew => ExistingId is null;
}

/// <summary>A member that can be drawn: both ends resolved and the checks passed. <see cref="NewPoints"/> is how many
/// points the draw makes (0, 1 or 2).</summary>
public sealed record FramePlan(PointUse Start, PointUse End, double Length)
{
    public int NewPoints => (Start.IsNew ? 1 : 0) + (End.IsNew ? 1 : 0);
}

/// <summary>A move that can be made: every point it moves once, by one offset.</summary>
public sealed record MovePlan(ImmutableArray<ObjectId> PointIds, Xyz Offset, ImmutableArray<ObjectId> MembersChanged);

/// <summary>What Delete takes out of the model, and what goes with it.</summary>
public sealed record DeletePlan(ImmutableArray<ObjectId> Points, ImmutableArray<ObjectId> Frames, ImmutableArray<ObjectId> Cables,
    ImmutableArray<ObjectId> Shells, ImmutableArray<ObjectId> ConnectedMembers, ImmutableArray<ObjectId> Loads, int Restraints, int JointMasses)
{
    public bool IsEmpty => Points.IsEmpty && Frames.IsEmpty && Cables.IsEmpty && Shells.IsEmpty;
    public IEnumerable<ObjectId> Objects => Shells.Concat(Frames).Concat(Cables).Concat(Points);
}

/// <summary>
/// Geometry editing on a native document (geometry editing plan, 2026-10-15): drawing points and members, moving and
/// deleting, and the grid and level generators — every rule decided here, with no window, viewer or solver behind it.
/// A Plan… method checks a request against the document and refuses it with the reason (<see cref="DocumentException"/>,
/// <see cref="DocumentError.Invalid"/>) before anything changes; the command made from a plan is one undoable step.
/// <para>Rules: a position within <paramref name="tolerance"/> of a point is that point, never a second point beside it
/// (no silent disconnection); a member has a length; two members never join the same two points, nor overlap along a
/// line; a member is moved by moving its end points, each once, so connected members follow; a deleted point takes the
/// members on it (as SAP2000), a deleted member leaves its end points. Nothing is split or merged on the quiet.</para>
/// </summary>
public static class GeometryEditService
{
    /// <summary>The default distance [m] within which a position is an existing point: 1 mm, the model's coordinate
    /// tolerance for joints. A screen's snap radius is a separate, pixel-based matter of the view.</summary>
    public const double DefaultTolerance = 1e-3;

    // ---- Points ----

    /// <summary>The point nearest <paramref name="position"/> within <paramref name="tolerance"/>, or null.</summary>
    public static ObjectId? PointAt(ModelDocument document, Xyz position, double tolerance = DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(document);
        RequireTolerance(tolerance);
        return document.Points.Values.Where(point => point.Position.DistanceTo(position) <= tolerance)
            .OrderBy(point => point.Position.DistanceTo(position)).ThenBy(point => point.Id).Select(point => (ObjectId?)point.Id).FirstOrDefault();
    }

    /// <summary>An existing point at <paramref name="position"/> (within the tolerance), else a new point there.</summary>
    public static PointUse Resolve(ModelDocument document, Xyz position, double tolerance = DefaultTolerance)
    {
        if (!position.IsFinite) throw Invalid("Enter finite X, Y and Z coordinates.");
        return PointAt(document, position, tolerance) is { } id ? new(id, document.Points[id].Position) : new(null, position);
    }

    /// <summary>Draw → Point: a point at <paramref name="position"/>. A point the model already has there is not doubled:
    /// the request is refused, naming it.</summary>
    public static IDocumentCommand AddPoint(ModelDocument document, Xyz position, ObjectId id, string? name = null, double tolerance = DefaultTolerance)
    {
        var use = Resolve(document, position, tolerance);
        if (use.ExistingId is { } existing)
            throw Invalid($"Point '{document.Points[existing].Name}' is already at ({Coordinates(position)}): a second point there would be a joint that is not connected to it.");
        return DocumentCommands.Edit("Add Point", editor => editor.AddPoint(position, name, id, new(OriginKind.User)));
    }

    /// <summary>Points at <paramref name="positions"/> as one step (Generate Points, a pasted table): positions within the
    /// tolerance of a point the model has, or of one made before them in the list, are left out. Returns the command and
    /// the positions it makes points at, in order.</summary>
    public static (IDocumentCommand Command, ImmutableArray<Xyz> Made) AddPoints(ModelDocument document, IEnumerable<Xyz> positions,
        string commandName = "Generate Points", double tolerance = DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(positions);
        RequireTolerance(tolerance);
        var made = new List<Xyz>();
        foreach (var position in positions)
        {
            if (!position.IsFinite) throw Invalid("Every point needs finite X, Y and Z coordinates.");
            if (PointAt(document, position, tolerance) is not null || made.Any(other => other.DistanceTo(position) <= tolerance)) continue;
            made.Add(position);
        }
        var list = made.ToImmutableArray();
        return (DocumentCommands.Edit(commandName, editor =>
        {
            foreach (var position in list) editor.AddPoint(position, origin: new(OriginKind.User));
        }), list);
    }

    // ---- Members ----

    /// <summary>Checks a member from <paramref name="start"/> to <paramref name="end"/> against the document: it has a
    /// length, does not join two points another member joins, and does not lie along a member it would overlap.</summary>
    public static FramePlan PlanFrame(ModelDocument document, PointUse start, PointUse end, double tolerance = DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(document);
        RequireTolerance(tolerance);
        foreach (var use in new[] { start, end })
            if (use.ExistingId is { } id && !document.Points.ContainsKey(id)) throw Invalid("A member cannot end on a point the model does not have.");
        if (!start.Position.IsFinite || !end.Position.IsFinite) throw Invalid("Enter finite X, Y and Z coordinates for both ends.");
        var length = start.Position.DistanceTo(end.Position);
        if (start.ExistingId is { } same && same == end.ExistingId || length <= tolerance)
            throw Invalid($"A member needs a length: both ends are at ({Coordinates(start.Position)}).");

        foreach (var (_, name, kind, a, b) in Members(document))
        {
            var (pa, pb) = (document.Points[a].Position, document.Points[b].Position);
            if (start.ExistingId is { } s && end.ExistingId is { } e && (s == a && e == b || s == b && e == a))
                throw Invalid($"{kind} '{name}' already joins these two points: a second member there would double it.");
            if (Overlap(start.Position, end.Position, pa, pb, tolerance) is { } overlap && overlap > tolerance)
                throw Invalid($"The member would lie along {kind.ToLowerInvariant()} '{name}' for {overlap.ToString("0.###", CultureInfo.InvariantCulture)} m: draw it to the end of '{name}', or divide it first.");
        }
        return new(start, end, length);
    }

    /// <summary>Draw → Frame (or Cable): the member of <paramref name="plan"/> and the new points it ends on, as one step
    /// named "Draw Frame". Ids are the caller's, so it knows the new objects without asking.</summary>
    public static IDocumentCommand DrawMember(FramePlan plan, ObjectId memberId, ObjectId? newStartId = null, ObjectId? newEndId = null,
        bool cable = false, string? name = null, ObjectId? sectionId = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return DocumentCommands.Edit(cable ? "Draw Cable" : "Draw Frame", editor =>
        {
            var start = plan.Start.ExistingId ?? editor.AddPoint(plan.Start.Position, id: newStartId ?? ObjectId.New(), origin: new(OriginKind.User)).Id;
            var end = plan.End.ExistingId ?? editor.AddPoint(plan.End.Position, id: newEndId ?? ObjectId.New(), origin: new(OriginKind.User)).Id;
            if (cable)
            {
                editor.AddCable(start, end, name, memberId, new(OriginKind.User));
                if (sectionId is { } section) editor.SetCableSection([memberId], section);
            }
            else
            {
                editor.AddFrame(start, end, name, memberId, new(OriginKind.User));
                if (sectionId is { } section) editor.SetFrameSection([memberId], section);
            }
        });
    }

    // ---- Move ----

    /// <summary>Edit → Move: the selected points and the end points of the selected members, each once, by
    /// <paramref name="offset"/>. Members on the moved points follow; a member keeps its length unless one end stays. A
    /// move that would collapse a member or put a point on another point (two joints in one place, not connected) is
    /// refused; joining them is Merge Joints, later.</summary>
    public static MovePlan PlanMove(ModelDocument document, IEnumerable<ObjectId> objectIds, Xyz offset, double tolerance = DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(document);
        RequireTolerance(tolerance);
        if (!offset.IsFinite) throw Invalid("Enter finite DX, DY and DZ.");
        var moved = new HashSet<ObjectId>();
        foreach (var id in objectIds ?? throw new ArgumentNullException(nameof(objectIds)))
        {
            if (document.Points.ContainsKey(id)) moved.Add(id);
            else if (document.Frames.TryGetValue(id, out var frame)) { moved.Add(frame.StartPointId); moved.Add(frame.EndPointId); }
            else if (document.Cables.TryGetValue(id, out var cable)) { moved.Add(cable.StartPointId); moved.Add(cable.EndPointId); }
            else if (document.Shells.TryGetValue(id, out var shell)) moved.UnionWith(shell.PointIds);
            else throw Invalid("Select points, frames or cables of the model to move.");
        }
        if (moved.Count == 0) throw Invalid("Select points, frames or cables of the model to move.");
        if (offset.Length <= tolerance) throw Invalid("The move is shorter than the model tolerance: nothing would move.");

        Xyz After(ObjectId id) => moved.Contains(id) ? document.Points[id].Position + offset : document.Points[id].Position;
        foreach (var id in moved)
        {
            var position = After(id);
            if (document.Points.Values.FirstOrDefault(other => !moved.Contains(other.Id) && other.Position.DistanceTo(position) <= tolerance) is { } hit)
                throw Invalid($"Point '{document.Points[id].Name}' would land on point '{hit.Name}': two joints in one place that are not connected. Move it elsewhere, or merge the joints (later).");
        }
        var changed = new List<ObjectId>();
        foreach (var (memberId, name, kind, a, b) in Members(document))
        {
            if (!moved.Contains(a) && !moved.Contains(b)) continue;
            if (After(a).DistanceTo(After(b)) <= tolerance) throw Invalid($"{kind} '{name}' would have no length after the move.");
            changed.Add(memberId);
        }
        return new([.. moved.OrderBy(id => id)], offset, [.. changed]);
    }

    public static IDocumentCommand Move(MovePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return DocumentCommands.Edit("Move", editor =>
        {
            foreach (var id in plan.PointIds) editor.MovePoint(id, editor.Points[id].Position + plan.Offset);
        });
    }

    // ---- Delete ----

    /// <summary>Edit → Delete: the selection and what goes with it — the members and shells on a deleted point (as
    /// SAP2000), the loads on every deleted object, the restraints and masses of deleted points. A deleted member leaves
    /// its end points, also when no other member uses them.</summary>
    public static DeletePlan PlanDelete(ModelDocument document, IEnumerable<ObjectId> selected)
    {
        ArgumentNullException.ThrowIfNull(document);
        var chosen = (selected ?? throw new ArgumentNullException(nameof(selected))).ToHashSet();
        var points = chosen.Where(document.Points.ContainsKey).ToHashSet();
        var frames = chosen.Where(document.Frames.ContainsKey).ToHashSet();
        var cables = chosen.Where(document.Cables.ContainsKey).ToHashSet();
        var shells = chosen.Where(document.Shells.ContainsKey).ToHashSet();
        var connected = new List<ObjectId>();
        foreach (var frame in document.Frames.Values)
            if ((points.Contains(frame.StartPointId) || points.Contains(frame.EndPointId)) && frames.Add(frame.Id)) connected.Add(frame.Id);
        foreach (var cable in document.Cables.Values)
            if ((points.Contains(cable.StartPointId) || points.Contains(cable.EndPointId)) && cables.Add(cable.Id)) connected.Add(cable.Id);
        foreach (var shell in document.Shells.Values)
            if (shell.PointIds.Any(points.Contains) && shells.Add(shell.Id)) connected.Add(shell.Id);
        var removed = points.Concat(frames).Concat(cables).Concat(shells).ToHashSet();
        var loads = document.Loads.Values.Where(load => removed.Contains(load.TargetId)).Select(load => load.Id).OrderBy(id => id).ToImmutableArray();
        return new([.. points.OrderBy(id => id)], [.. frames.OrderBy(id => id)], [.. cables.OrderBy(id => id)], [.. shells.OrderBy(id => id)],
            [.. connected], loads, points.Count(document.Restraints.ContainsKey), points.Count(document.JointMassAssignments.ContainsKey));
    }

    public static IDocumentCommand Delete(DeletePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.IsEmpty) throw Invalid("Select the points, frames or cables to delete first.");
        return DocumentCommands.Edit("Delete", editor => editor.Remove([.. plan.Objects]));
    }

    // ---- Grids and levels ----

    /// <summary>Grid line labels as SAP2000 and ETABS give them by default: X lines 1, 2, 3 …; Y lines A … Z, AA, AB ….</summary>
    public static string GridLabel(bool xAxis, int index, string prefix = "")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (xAxis) return prefix + (index + 1).ToString(CultureInfo.InvariantCulture);
        var letters = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) letters = (char)('A' + (n - 1) % 26) + letters;
        return prefix + letters;
    }

    /// <summary>Quick Grid: lines at <paramref name="start"/> and then at every spacing in turn (equal or unequal bays),
    /// labelled in order.</summary>
    public static ImmutableArray<GridLine> LinesFromSpacings(bool xAxis, double start, IEnumerable<double> spacings, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(spacings);
        if (!double.IsFinite(start)) throw Invalid("The first grid line needs a finite coordinate.");
        var lines = new List<GridLine> { new(GridLabel(xAxis, 0, prefix), start) };
        var at = start;
        foreach (var spacing in spacings)
        {
            if (!double.IsFinite(spacing) || spacing <= ReferenceGeometry.PositionTolerance) throw Invalid("Every grid spacing must be a finite length above zero.");
            at += spacing;
            lines.Add(new(GridLabel(xAxis, lines.Count, prefix), at));
        }
        return [.. lines];
    }

    /// <summary>Quick Grid with equal bays: <paramref name="bays"/> spacings of <paramref name="spacing"/>.</summary>
    public static ImmutableArray<GridLine> UniformLines(bool xAxis, double start, int bays, double spacing, string prefix = "")
    {
        if (bays is < 0 or > 500) throw Invalid("The number of bays must be from 0 to 500.");
        return LinesFromSpacings(xAxis, start, Enumerable.Repeat(spacing, bays), prefix);
    }

    /// <summary>Generate Levels: a base at <paramref name="baseElevation"/> and one level on each storey height, named
    /// Base, L1, L2 … and Roof for the top one (more than one storey).</summary>
    public static ImmutableArray<Level> LevelsFromHeights(double baseElevation, IReadOnlyList<double> storeyHeights)
    {
        ArgumentNullException.ThrowIfNull(storeyHeights);
        if (!double.IsFinite(baseElevation)) throw Invalid("The base needs a finite elevation.");
        if (storeyHeights.Count > 500) throw Invalid("A model can have at most 500 storeys here.");
        var levels = new List<Level> { new("Base", baseElevation) };
        var z = baseElevation;
        for (var index = 0; index < storeyHeights.Count; index++)
        {
            var height = storeyHeights[index];
            if (!double.IsFinite(height) || height <= ReferenceGeometry.PositionTolerance) throw Invalid("Every storey height must be a finite length above zero.");
            z += height;
            levels.Add(new(index == storeyHeights.Count - 1 && storeyHeights.Count > 1 ? "Roof" : $"L{index + 1}", z));
        }
        return [.. levels];
    }

    /// <summary>The global positions where the X and Y lines of <paramref name="system"/> cross, at each of
    /// <paramref name="elevations"/> (the system's own elevation when none is given). Hidden lines are left out unless asked.</summary>
    public static ImmutableArray<Xyz> GridIntersections(GridSystem system, IEnumerable<double>? elevations = null, bool includeHidden = false)
    {
        ArgumentNullException.ThrowIfNull(system);
        var zs = (elevations ?? [system.Origin.Z]).ToArray();
        var xs = system.XLines.Where(line => includeHidden || line.Visible).ToArray();
        var ys = system.YLines.Where(line => includeHidden || line.Visible).ToArray();
        return [.. from z in zs from x in xs from y in ys select system.ToGlobal(x.Coordinate, y.Coordinate) with { Z = z }];
    }

    /// <summary>Define → Grid Systems / Levels → OK: the reference geometry as one undoable step. No point moves.</summary>
    public static IDocumentCommand SetReferenceGeometry(ReferenceGeometry reference, string name = "Edit Grids and Levels")
    {
        ArgumentNullException.ThrowIfNull(reference);
        return DocumentCommands.Edit(name, editor => editor.SetReferenceGeometry(reference));
    }

    /// <summary>The grid systems and levels a model's own points suggest — one grid line at every distinct X and Y of its
    /// points and one level at every distinct Z — offered to start from in a model that has none. Nothing is saved until
    /// the user applies it.</summary>
    public static ReferenceGeometry SuggestFromPoints(ModelDocument document, int maximumLines = 60)
    {
        ArgumentNullException.ThrowIfNull(document);
        static double[] Distinct(IEnumerable<double> values)
        {
            var list = new List<double>();
            foreach (var value in values.OrderBy(value => value))
                if (list.Count == 0 || value - list[^1] > DefaultTolerance) list.Add(value);
            return [.. list];
        }
        var points = document.Points.Values.Select(point => point.Position).ToArray();
        if (points.Length == 0) return ReferenceGeometry.Empty;
        var xs = Distinct(points.Select(p => p.X));
        var ys = Distinct(points.Select(p => p.Y));
        var zs = Distinct(points.Select(p => p.Z));
        if (xs.Length > maximumLines || ys.Length > maximumLines || zs.Length > maximumLines) return ReferenceGeometry.Empty;
        var grid = new GridSystem("GLOBAL", new(0, 0, zs[0]), 0,
            [.. xs.Select((x, index) => new GridLine(GridLabel(true, index), x))], [.. ys.Select((y, index) => new GridLine(GridLabel(false, index), y))]);
        var levels = zs.Select((z, index) => new Level(index == 0 ? "Base" : index == zs.Length - 1 && zs.Length > 2 ? "Roof" : $"L{index}", z));
        return new([grid], [.. levels]);
    }

    // ---- internals ----

    private static IEnumerable<(ObjectId Id, string Name, string Kind, ObjectId Start, ObjectId End)> Members(ModelDocument document) =>
        document.Frames.Values.Select(frame => (frame.Id, frame.Name, "Frame", frame.StartPointId, frame.EndPointId))
            .Concat(document.Cables.Values.Select(cable => (cable.Id, cable.Name, "Cable", cable.StartPointId, cable.EndPointId)))
            .OrderBy(member => member.Name, StringComparer.Ordinal);

    /// <summary>The length along which segment a–b lies on segment c–d (both on one line within the tolerance), or null
    /// when they are not on one line or only touch.</summary>
    internal static double? Overlap(Xyz a, Xyz b, Xyz c, Xyz d, double tolerance)
    {
        var direction = d - c;
        var length = direction.Length;
        if (length <= tolerance) return null;
        var unit = direction * (1 / length);
        double Off(Xyz p) => ((p - c) - unit * (p - c).Dot(unit)).Length;
        if (Off(a) > tolerance || Off(b) > tolerance) return null;
        double ta = (a - c).Dot(unit), tb = (b - c).Dot(unit);
        var overlap = Math.Min(Math.Max(ta, tb), length) - Math.Max(Math.Min(ta, tb), 0);
        return overlap > 0 ? overlap : null;
    }

    private static string Coordinates(Xyz p) => string.Create(CultureInfo.InvariantCulture, $"{p.X:0.###}, {p.Y:0.###}, {p.Z:0.###}");

    private static void RequireTolerance(double tolerance)
    {
        if (!double.IsFinite(tolerance) || tolerance <= 0) throw new ArgumentOutOfRangeException(nameof(tolerance), "The tolerance must be a finite length above zero.");
    }

    private static DocumentException Invalid(string message) => new(DocumentError.Invalid, message);
}
