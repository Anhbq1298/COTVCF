using COT_VCF.Domain;
namespace COT_VCF.Geometry;

public sealed class TrussGeometryService
{
    public IReadOnlyList<ValidationIssue> Validate(GeometryModel baseline, TrussHeightConfiguration t)
    {
        var issues = new List<ValidationIssue>();
        void Error(string text) => issues.Add(new("Truss height", text));
        if (t.TopPoints.Count == 0 || t.MiddlePoints.Count == 0 || t.BottomPoints.Count == 0) Error("Select top, middle and bottom point groups.");
        var names = t.TopPoints.Concat(t.MiddlePoints).Concat(t.BottomPoints).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) Error("A point cannot belong to more than one truss group.");
        foreach (var name in names.Where(n => !baseline.Points.ContainsKey(n))) Error($"Point {name} no longer exists. Refresh and review its group.");
        if (!ValidRange(t.MinimumHeight, t.MaximumHeight, t.Increment) || t.MinimumHeight <= 0) Error("Use a positive minimum, maximum greater than minimum, and a positive finite step no larger than the range.");
        if (!double.IsFinite(t.BaselineHeight) || t.BaselineHeight <= 0) Error("Capture a valid baseline height first.");
        if (!double.IsFinite(t.CurrentHeight)) Error("Preview height must be a finite number.");
        if (baseline.Points.Values.Any(p => !p.Position.IsFinite)) Error("Baseline contains non-finite coordinates.");
        return issues;
    }
    public static bool ValidRange(double min, double max, double step) => double.IsFinite(min) && double.IsFinite(max) && double.IsFinite(step) && max > min && step > 0 && step <= max - min && (max - min) / step <= 1_000_000;
    public static bool OnGrid(double value, double min, double max, double step) => ValidRange(min, max, step) && double.IsFinite(value) && value >= min && value <= max && Math.Abs((value - min) / step - Math.Round((value - min) / step)) <= 1e-7;
    public double MeasureHeight(GeometryModel geometry, TrussHeightConfiguration t)
    {
        if (t.TopPoints.Count == 0 || t.BottomPoints.Count == 0) throw new InvalidOperationException("Select top and bottom groups first.");
        double Level(IReadOnlyList<string> names)
        {
            var z = names.Select(n => geometry.Points.TryGetValue(n, out var p) ? p.Position.Z : throw new InvalidOperationException($"Point {n} is missing.")).ToArray();
            if (z.Max() - z.Min() > 1e-6 * Math.Max(1, Math.Abs(z.Average()))) throw new InvalidOperationException("The selected chord is not level. Select level chord groups to measure a single baseline height.");
            return z.Average();
        }
        var height = Level(t.TopPoints) - Level(t.BottomPoints);
        if (!double.IsFinite(height) || height <= 0) throw new InvalidOperationException("Top points must be above bottom points in global Z.");
        return height;
    }
    public GeometryModel Preview(GeometryModel baseline, TrussHeightConfiguration t)
    {
        var issues = Validate(baseline, t);
        if (issues.Count > 0) throw new InvalidOperationException(string.Join("\n", issues.Select(i => i.Message)));
        var top = t.TopPoints.ToHashSet(StringComparer.Ordinal);
        var middle = t.MiddlePoints.ToHashSet(StringComparer.Ordinal);
        var bottom = t.BottomPoints.ToHashSet(StringComparer.Ordinal);
        var (dTop, dMid, dBot) = ChordDeltas(t.Reference, t.CurrentHeight - t.BaselineHeight);
        var points = baseline.Points.ToDictionary(p => p.Key, p => p.Value with { Position = p.Value.Position with { Z = p.Value.Position.Z + (top.Contains(p.Key) ? dTop : middle.Contains(p.Key) ? dMid : bottom.Contains(p.Key) ? dBot : 0) } });
        if (points.Values.Any(p => !p.Position.IsFinite)) throw new InvalidOperationException("Preview coordinates exceed the supported numeric range.");
        return new(points, baseline.Members);
    }

    /// <summary>Signed Z shift for each chord given the reference chord and ΔH = candidate - baseline height.
    /// Directions are never hard-coded; a negative ΔH reverses the movement.</summary>
    public static (double Top, double Mid, double Bot) ChordDeltas(ReferenceChord reference, double deltaHeight) => reference switch
    {
        ReferenceChord.Middle => (0.5 * deltaHeight, 0.0, -0.5 * deltaHeight),
        ReferenceChord.Bottom => (deltaHeight, 0.5 * deltaHeight, 0.0),
        _ => (0.0, -0.5 * deltaHeight, -deltaHeight),
    };
}
