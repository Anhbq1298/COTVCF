using System.Collections.Immutable;

namespace Mcotis.Core;

/// <summary>Assign → Frame → Frame Load: what SAP2000's Frame Distributed Loads form does to the frames it is applied to.</summary>
public enum LoadAssignMode
{
    /// <summary>Add to Existing Loads: the new loads go beside the loads the frame already has in the pattern.</summary>
    Add,
    /// <summary>Replace Existing Loads: every distributed load of the pattern on the frame goes, then the new ones are added.</summary>
    Replace,
    /// <summary>Delete Existing Loads: every distributed load of the pattern on the frame goes.</summary>
    Delete,
}

/// <summary>One point of the trapezoid: a distance from End-I (relative or absolute, as the definition says) and the
/// load there [kN/m or kN·m/m].</summary>
public readonly record struct FrameLoadPoint(double Distance, double Value);

/// <summary>What the Frame Distributed Loads form sends, as SAP2000 defines it: a load pattern, force or moment, a direction
/// (local or global, projected or not), up to four trapezoid points and a uniform load over the whole frame.</summary>
public sealed record FrameDistributedLoadDefinition(ObjectId PatternId, FrameLoadType Type, LoadDirection Direction, bool Projected,
    LoadDistanceType DistanceType, ImmutableArray<FrameLoadPoint> Points, double Uniform)
{
    public ImmutableArray<FrameLoadPoint> Points { get; init; } = Points.IsDefault ? [] : Points;

    /// <summary>Projected loading exists only along a global direction (SAP2000 directions 7–9 and 11).</summary>
    public static bool CanProject(LoadDirection direction) =>
        direction is LoadDirection.GlobalX or LoadDirection.GlobalY or LoadDirection.GlobalZ or LoadDirection.Gravity;

    public static bool IsLocal(LoadDirection direction) =>
        direction is LoadDirection.Local1 or LoadDirection.Local2 or LoadDirection.Local3;
}

/// <summary>The distributed loads of frames: how a definition becomes the segments the model holds, how they are
/// added, replaced and deleted, and what they come to along a frame. Every rule here is SAP2000's.</summary>
public static class FrameLoads
{
    public const int MaxTrapezoidPoints = 4;

    /// <summary>The loads one definition gives a frame of <paramref name="length"/> [m], as SAP2000 stores them: one linear
    /// segment per pair of consecutive trapezoid points — a pair at the same distance (a step) or with zero at both ends
    /// gives none — and one segment 0 → 1 for a nonzero uniform load. Distances become relative to the length.
    /// Throws <see cref="ArgumentException"/> with the point at fault for a definition SAP2000 would refuse.</summary>
    public static ImmutableArray<FrameDistributedLoad> Segments(FrameDistributedLoadDefinition definition, ObjectId frameId, double length)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Check(definition);
        if (!(double.IsFinite(length) && length > 0)) throw new ArgumentException("The frame has no length.", nameof(length));
        bool absolute = definition.DistanceType == LoadDistanceType.Absolute;
        double tolerance = absolute ? 1e-9 * Math.Max(1, length) : 1e-12, limit = absolute ? length : 1;
        var relative = new List<(double At, double Value)>();
        for (int i = 0; i < definition.Points.Length; i++)
        {
            var (distance, value) = definition.Points[i];
            if (distance < -tolerance || distance > limit + tolerance)
                throw new ArgumentException(absolute
                    ? $"Trapezoid point {i + 1}: the distance {distance:G6} m is outside the frame (0 … {length:G6} m)."
                    : $"Trapezoid point {i + 1}: the relative distance {distance:G6} is outside 0 … 1.", nameof(definition));
            if (i > 0 && distance < definition.Points[i - 1].Distance - tolerance)
                throw new ArgumentException($"Trapezoid point {i + 1}: the distances must not decrease from End-I to End-J.", nameof(definition));
            relative.Add((Math.Clamp(absolute ? distance / length : distance, 0, 1), value));
        }

        var segments = ImmutableArray.CreateBuilder<FrameDistributedLoad>();
        FrameDistributedLoad Segment(double start, double end, double startValue, double endValue) =>
            new(ObjectId.New(), definition.PatternId, frameId, definition.Direction, start, end, startValue, endValue)
            {
                Type = definition.Type, Projected = definition.Projected, EnteredAs = definition.DistanceType,
            };
        for (int i = 1; i < relative.Count; i++)
        {
            var (a, va) = relative[i - 1];
            var (b, vb) = relative[i];
            if (b - a > 1e-12 && (va != 0 || vb != 0)) segments.Add(Segment(a, b, va, vb));
        }
        if (definition.Uniform != 0) segments.Add(Segment(0, 1, definition.Uniform, definition.Uniform));
        return segments.ToImmutable();
    }

    /// <summary>Assigns <paramref name="definition"/> to each of <paramref name="frameIds"/> in one edit, as SAP2000's form
    /// does with the chosen option. Point loads and other patterns are never touched. Returns how many frames end with
    /// loads that differ from what they had.</summary>
    public static int Assign(DocumentEditor editor, IEnumerable<ObjectId> frameIds, FrameDistributedLoadDefinition definition, LoadAssignMode mode)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(definition);
        if (!editor.LoadPatterns.ContainsKey(definition.PatternId))
            throw new ArgumentException("Choose a load pattern of the model.", nameof(definition));
        if (mode != LoadAssignMode.Delete) Check(definition);
        int changed = 0;
        foreach (var frameId in frameIds.Distinct())
        {
            if (!editor.Frames.TryGetValue(frameId, out var frame))
                throw new ArgumentException($"The object {frameId} is not a frame of the model.", nameof(frameIds));
            var existing = editor.Loads.Values.OfType<FrameDistributedLoad>()
                .Where(load => load.TargetId == frameId && load.PatternId == definition.PatternId).ToArray();
            var added = mode == LoadAssignMode.Delete ? ImmutableArray<FrameDistributedLoad>.Empty
                : Segments(definition, frameId, LengthOf(editor.Points, frame));
            FrameDistributedLoad[] removed = mode == LoadAssignMode.Add ? [] : existing;
            var before = existing.Select(Value).OrderBy(item => item).ToArray();
            var after = existing.Except(removed).Select(Value).Concat(added.Select(Value)).OrderBy(item => item).ToArray();
            if (before.SequenceEqual(after)) continue;
            if (removed.Length > 0) editor.Remove(removed.Select(load => load.Id));
            foreach (var load in added) editor.PutLoad(load);
            changed++;
        }
        return changed;
    }

    /// <summary>The distributed loads of a frame, in one pattern or in all, from End-I.</summary>
    public static IEnumerable<FrameDistributedLoad> Of(ModelDocument document, ObjectId frameId, ObjectId? patternId = null) =>
        document.Loads.Values.OfType<FrameDistributedLoad>()
            .Where(load => load.TargetId == frameId && (patternId is null || load.PatternId == patternId))
            .OrderBy(load => load.RelativeStart).ThenBy(load => load.RelativeEnd).ThenBy(load => load.Id);

    /// <summary>The intensity of one kind of load at a relative distance along a frame, as given (not projected): the sum
    /// of the segments that cover it. Zero outside every segment.</summary>
    public static double IntensityAt(ModelDocument document, ObjectId frameId, ObjectId patternId, FrameLoadType type,
        LoadDirection direction, bool projected, double relative) =>
        Of(document, frameId, patternId)
            .Where(load => load.Type == type && load.Direction == direction && load.Projected == projected)
            .Sum(load => IntensityOf(load, relative));

    /// <summary>The value of one segment at a relative distance; zero outside it.</summary>
    public static double IntensityOf(FrameDistributedLoad load, double relative)
    {
        if (relative < load.RelativeStart || relative > load.RelativeEnd) return 0;
        var span = load.RelativeEnd - load.RelativeStart;
        return span <= 0 ? load.ValueStart : load.ValueStart + (load.ValueEnd - load.ValueStart) * (relative - load.RelativeStart) / span;
    }

    /// <summary>The factor a projected load is multiplied by to give the load per unit length of the frame: the sine of the
    /// angle between the frame axis <paramref name="axis1"/> and the load direction, √(1 − (e₁·d)²).</summary>
    public static double ProjectionFactor(Xyz axis1, LoadDirection direction)
    {
        Xyz d = direction switch
        {
            LoadDirection.GlobalX => new(1, 0, 0),
            LoadDirection.GlobalY => new(0, 1, 0),
            LoadDirection.GlobalZ or LoadDirection.Gravity => new(0, 0, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Only a global direction can be projected."),
        };
        var length = axis1.Length;
        var cosine = length > 0 ? axis1.Dot(d) / length : 0;
        return Math.Sqrt(Math.Max(0, 1 - cosine * cosine));
    }

    private static void Check(FrameDistributedLoadDefinition definition)
    {
        if (definition.Points.Length > MaxTrapezoidPoints)
            throw new ArgumentException($"A trapezoid has at most {MaxTrapezoidPoints} points.", nameof(definition));
        if (definition.Projected && !FrameDistributedLoadDefinition.CanProject(definition.Direction))
            throw new ArgumentException("Only a global direction (X, Y, Z or Gravity) can be projected.", nameof(definition));
        if (!double.IsFinite(definition.Uniform) || definition.Points.Any(point => !double.IsFinite(point.Distance) || !double.IsFinite(point.Value)))
            throw new ArgumentException("Every distance and load must be a finite number.", nameof(definition));
    }

    private static double LengthOf(IReadOnlyDictionary<ObjectId, PointObject> points, FrameObject frame) =>
        (points[frame.EndPointId].Position - points[frame.StartPointId].Position).Length;

    /// <summary>A load as what it does, without its id: two assignments of the same loads compare equal.</summary>
    private static (double, double, double, double, int, int, bool) Value(FrameDistributedLoad load) =>
        (load.RelativeStart, load.RelativeEnd, load.ValueStart, load.ValueEnd, (int)load.Type, (int)load.Direction, load.Projected);
}
