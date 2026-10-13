namespace Mcotis.Desktop.Application.Results;

/// <summary>Report-side reading of a foundation's two support points: which one is the inner and which the outer
/// support, and — per iteration and result step — each one's Fx / Fz with their algebraic sum. Derived from the
/// stored reaction rows only: nothing is estimated, and a pair with a missing or invalid point has no value.</summary>
public static class SupportReactionSummary
{
    /// <summary>Two supports whose distances to the centre differ by less than this [m] cannot be told apart.</summary>
    public const double RoleToleranceM = 1e-6;

    /// <summary>Inner / outer support per foundation side. The inner support is the one nearer, in plan, to the
    /// centre of all foundation points — towards the other foundation; the outer one is the farther. A side is
    /// left out (it keeps the generic views only) unless it has exactly two located points at different
    /// distances, so a single foundation or coincident coordinates never get a guessed role.</summary>
    public static IReadOnlyDictionary<string, (string Inner, string Outer)> ResolveRoles(IReadOnlyList<SupportReactionGroupReportDto> groups)
    {
        // one location per support point: the first row that reports it
        var points = groups.SelectMany(group => group.Series.SelectMany(candidate => candidate.Rows)
                .Where(row => double.IsFinite(row.X) && double.IsFinite(row.Y))
                .Select(row => (Side: group.FoundationSide, row.PointName, row.X, row.Y)))
            .GroupBy(point => (point.Side, point.PointName)).Select(point => point.First()).ToArray();
        var roles = new Dictionary<string, (string Inner, string Outer)>(StringComparer.Ordinal);
        if (points.Length == 0) return roles;

        double centreX = points.Average(point => point.X), centreY = points.Average(point => point.Y);
        foreach (var side in points.GroupBy(point => point.Side, StringComparer.Ordinal))
        {
            var ranked = side.Select(point => (point.PointName, Distance: Math.Sqrt(Math.Pow(point.X - centreX, 2) + Math.Pow(point.Y - centreY, 2))))
                .OrderBy(point => point.Distance).ToArray();
            if (ranked.Length == 2 && ranked[1].Distance - ranked[0].Distance > RoleToleranceM)
                roles[side.Key] = (ranked[0].PointName, ranked[1].PointName);
        }
        return roles;
    }

    /// <summary>One iteration's inner / outer / net reactions, per result step. A component is reported for a step
    /// only when both supports have a valid, finite value there — the net is never a sum over one support, and a
    /// missing value is never 0.</summary>
    public static SupportReactionSummaryCandidateDto Summarise(string iterationKey, IReadOnlyList<SupportReactionRowDto> rows, string innerPoint, string outerPoint)
    {
        var steps = rows.Where(row => row.Status == "Valid")
            .GroupBy(row => (row.StepType, row.StepNumber))
            .Select(step =>
            {
                var inner = step.FirstOrDefault(row => row.PointName == innerPoint);
                var outer = step.FirstOrDefault(row => row.PointName == outerPoint);
                return new SupportReactionSummaryStepDto(step.Key.StepType, step.Key.StepNumber,
                    Pair(inner?.Fx, outer?.Fx), Pair(inner?.Fz, outer?.Fz));
            })
            .Where(step => step.Fx is not null || step.Fz is not null).ToArray();
        return new(iterationKey, steps);
    }

    /// <summary>Σ = inner + outer: the algebraic sum, so opposite reactions cancel.</summary>
    public static SupportReactionPairDto? Pair(double? inner, double? outer) =>
        inner is { } i && outer is { } o && double.IsFinite(i) && double.IsFinite(o) ? new(i, o, i + o) : null;
}
