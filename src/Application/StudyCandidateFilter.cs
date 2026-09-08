using System.Numerics;
using COT_VCF.Domain;

namespace COT_VCF.Application;

public sealed record StudyFilterAudit(
    BigInteger BeforeCount,
    BigInteger AfterCount,
    IReadOnlyDictionary<string, BigInteger> ExcludedByRule)
{
    public BigInteger ExcludedCount => BigInteger.Max(BigInteger.Zero, BeforeCount - AfterCount);
    public static StudyFilterAudit None(BigInteger count) => new(count, count,
        new Dictionary<string, BigInteger>(StringComparer.Ordinal));
}

public sealed record StudyCandidateFilterEvaluation(
    BigInteger Count,
    StudyFilterAudit Audit,
    Func<IEnumerable<StudyRun>> Enumerate);

/// <summary>Deterministic engineering filters over L/H/P candidates. Counting is exact without expanding the Cartesian product.</summary>
public sealed class StudyCandidateFilter
{
    public StudyCandidateFilterEvaluation Evaluate(
        IterationStudyConfig config,
        IterationStudyMode mode,
        IReadOnlyList<LoadingStudyCandidate> loads,
        IReadOnlyList<StudyHeightCandidate> heights,
        IReadOnlyList<StudyPretensionCandidate> pretensions,
        int fixedHeightIndex,
        int fixedPretensionIndex)
    {
        var before = StudyPlan.TotalRuns(mode, loads.Count, heights.Count, pretensions.Count);
        if (!config.UseCandidateFilters)
            return new(before, StudyFilterAudit.None(before), () => StudyPlan.Enumerate(
                mode, loads.Count, heights.Count, pretensions.Count, fixedHeightIndex, fixedPretensionIndex));

        var loadMetric = loads.Select(LoadingMetric).ToArray();
        var heightMetric = heights.Select(item => item.Candidate.Height).ToArray();
        var pretensionMetric = pretensions.Select(PretensionMetric).ToArray();
        var loadAllowed = loadMetric.Select(value => InBounds(value, config.MinLoadingKpa, config.MaxLoadingKpa)).ToArray();
        var heightAllowed = heightMetric.Select(value => InBounds(value, config.MinHeightM, config.MaxHeightM)).ToArray();
        var pretensionAllowed = pretensionMetric.Select(value => InBounds(value, config.MinPretensionKn, config.MaxPretensionKn)).ToArray();

        var afterBounds = Count(mode, loadAllowed, heightAllowed, pretensionAllowed,
            loadMetric, heightMetric, pretensionMetric, fixedHeightIndex, fixedPretensionIndex, null, null);
        var after = Count(mode, loadAllowed, heightAllowed, pretensionAllowed,
            loadMetric, heightMetric, pretensionMetric, fixedHeightIndex, fixedPretensionIndex,
            config.MaxLoadingToHeightRatio, config.MaxPretensionToHeightRatio);
        var reasons = new Dictionary<string, BigInteger>(StringComparer.Ordinal)
        {
            ["Loading bounds (axis candidates)"] = loadAllowed.Count(ok => !ok),
            ["Height bounds (axis candidates)"] = heightAllowed.Count(ok => !ok),
            ["Pretension bounds (axis candidates)"] = pretensionAllowed.Count(ok => !ok),
            ["Cross-dimension predicates (study rows)"] = BigInteger.Max(BigInteger.Zero, afterBounds - after)
        };
        return new(after, new StudyFilterAudit(before, after, reasons), () => Enumerate(
            mode, loadAllowed, heightAllowed, pretensionAllowed, loadMetric, heightMetric, pretensionMetric,
            fixedHeightIndex, fixedPretensionIndex, config.MaxLoadingToHeightRatio, config.MaxPretensionToHeightRatio));
    }

    private static BigInteger Count(
        IterationStudyMode mode,
        IReadOnlyList<bool> loadAllowed,
        IReadOnlyList<bool> heightAllowed,
        IReadOnlyList<bool> pretensionAllowed,
        IReadOnlyList<double> loads,
        IReadOnlyList<double> heights,
        IReadOnlyList<double> pretensions,
        int fixedHeight,
        int fixedPretension,
        double? loadHeightLimit,
        double? pretensionHeightLimit)
    {
        if (fixedHeight < 0 || fixedPretension < 0) return BigInteger.Zero;
        var allowedLoads = loads.Where((_, index) => loadAllowed[index]).OrderBy(value => value).ToArray();
        var allowedPretensions = pretensions.Where((_, index) => pretensionAllowed[index]).OrderBy(value => value).ToArray();
        BigInteger count = 0;
        if (mode == IterationStudyMode.FixHeight)
        {
            if (!heightAllowed[fixedHeight]) return 0;
            var l = EligibleCount(allowedLoads, heights[fixedHeight], loadHeightLimit);
            var p = EligibleCount(allowedPretensions, heights[fixedHeight], pretensionHeightLimit);
            return (BigInteger)l * p;
        }
        if (mode == IterationStudyMode.FixPretension && !pretensionAllowed[fixedPretension]) return 0;
        for (var h = 0; h < heights.Count; h++)
        {
            if (!heightAllowed[h]) continue;
            var l = EligibleCount(allowedLoads, heights[h], loadHeightLimit);
            if (mode == IterationStudyMode.FixPretension)
            {
                if (PassPretensionHeight(pretensions[fixedPretension], heights[h], pretensionHeightLimit)) count += l;
            }
            else
            {
                var p = EligibleCount(allowedPretensions, heights[h], pretensionHeightLimit);
                count += (BigInteger)l * p;
            }
        }
        return count;
    }

    private static IEnumerable<StudyRun> Enumerate(
        IterationStudyMode mode,
        IReadOnlyList<bool> loadAllowed,
        IReadOnlyList<bool> heightAllowed,
        IReadOnlyList<bool> pretensionAllowed,
        IReadOnlyList<double> loads,
        IReadOnlyList<double> heights,
        IReadOnlyList<double> pretensions,
        int fixedHeight,
        int fixedPretension,
        double? loadHeightLimit,
        double? pretensionHeightLimit)
    {
        long index = 0;
        if (fixedHeight < 0 || fixedPretension < 0) yield break;
        for (var l = 0; l < loads.Count; l++)
        {
            if (!loadAllowed[l]) continue;
            if (mode == IterationStudyMode.FixHeight)
            {
                if (!heightAllowed[fixedHeight] || !PassLoadHeight(loads[l], heights[fixedHeight], loadHeightLimit)) continue;
                for (var p = 0; p < pretensions.Count; p++)
                    if (pretensionAllowed[p] && PassPretensionHeight(pretensions[p], heights[fixedHeight], pretensionHeightLimit))
                        yield return new(index++, l, fixedHeight, p);
                continue;
            }
            for (var h = 0; h < heights.Count; h++)
            {
                if (!heightAllowed[h] || !PassLoadHeight(loads[l], heights[h], loadHeightLimit)) continue;
                if (mode == IterationStudyMode.FixPretension)
                {
                    if (pretensionAllowed[fixedPretension] && PassPretensionHeight(pretensions[fixedPretension], heights[h], pretensionHeightLimit))
                        yield return new(index++, l, h, fixedPretension);
                }
                else
                {
                    for (var p = 0; p < pretensions.Count; p++)
                        if (pretensionAllowed[p] && PassPretensionHeight(pretensions[p], heights[h], pretensionHeightLimit))
                            yield return new(index++, l, h, p);
                }
            }
        }
    }

    private static int EligibleCount(IReadOnlyList<double> sortedValues, double height, double? limit)
    {
        if (limit is null) return sortedValues.Count;
        if (height <= 0) return 0;
        var maximum = limit.Value * height + Tolerance(limit.Value * height);
        var lo = 0;
        var hi = sortedValues.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (sortedValues[mid] <= maximum) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static bool PassLoadHeight(double load, double height, double? limit) =>
        limit is not double max || height > 0 && load / height <= max + Tolerance(max);
    private static bool PassPretensionHeight(double force, double height, double? limit) =>
        limit is not double max || height > 0 && force / height <= max + Tolerance(max);
    private static bool InBounds(double value, double? minimum, double? maximum) =>
        (minimum is not double lo || value >= lo - Tolerance(lo)) &&
        (maximum is not double hi || value <= hi + Tolerance(hi));
    private static double LoadingMetric(LoadingStudyCandidate candidate) =>
        candidate.Combination.Loads.Count == 0 ? 0 : candidate.Combination.Loads.Max(item => Math.Abs(item.Qz));
    private static double PretensionMetric(StudyPretensionCandidate candidate) =>
        candidate.Assignments.Count == 0 ? 0 : candidate.Assignments.Max(item => Math.Abs(item.TargetForce));
    private static double Tolerance(double value) => 1e-9 * Math.Max(1, Math.Abs(value));
}
