using System.Numerics;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Application;

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
        if (fixedHeight < 0 || fixedHeight >= heights.Count ||
            fixedPretension < 0 || fixedPretension >= pretensions.Count) return BigInteger.Zero;
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
        if (fixedHeight < 0 || fixedHeight >= heights.Count ||
            fixedPretension < 0 || fixedPretension >= pretensions.Count) yield break;

        var allowedLoads = loads.Select((value, candidateIndex) => new IndexedMetric(candidateIndex, value))
            .Where(item => loadAllowed[item.Index]).ToArray();
        var allowedPretensions = pretensions.Select((value, candidateIndex) => new IndexedMetric(candidateIndex, value))
            .Where(item => pretensionAllowed[item.Index])
            .OrderBy(item => item.Value).ThenBy(item => item.Index).ToArray();
        var allowedPretensionValues = allowedPretensions.Select(item => item.Value).ToArray();

        if (mode == IterationStudyMode.FixHeight)
        {
            if (!heightAllowed[fixedHeight]) yield break;
            var height = heights[fixedHeight];
            var eligiblePretensions = EligibleAtMost(allowedPretensions, height, pretensionHeightLimit);
            foreach (var load in allowedLoads)
            {
                if (!PassLoadHeight(load.Value, height, loadHeightLimit)) continue;
                foreach (var pretension in eligiblePretensions)
                    yield return new(index++, load.Index, fixedHeight, pretension);
            }
            yield break;
        }

        if (mode == IterationStudyMode.FixPretension && !pretensionAllowed[fixedPretension]) yield break;
        var heightCandidates = heights.Select((value, candidateIndex) => new IndexedMetric(candidateIndex, value))
            .Where(item => heightAllowed[item.Index] &&
                (mode != IterationStudyMode.FixPretension || PassPretensionHeight(pretensions[fixedPretension], item.Value, pretensionHeightLimit)) &&
                (mode != IterationStudyMode.Combined || EligibleCount(allowedPretensionValues, item.Value, pretensionHeightLimit) > 0))
            .OrderBy(item => item.Value).ThenBy(item => item.Index).ToArray();

        foreach (var load in allowedLoads)
        {
            foreach (var heightIndex in EligibleAtLeast(heightCandidates, load.Value, loadHeightLimit))
            {
                if (mode == IterationStudyMode.FixPretension)
                {
                    yield return new(index++, load.Index, heightIndex, fixedPretension);
                }
                else
                {
                    foreach (var pretensionIndex in EligibleAtMost(allowedPretensions, heights[heightIndex], pretensionHeightLimit))
                        yield return new(index++, load.Index, heightIndex, pretensionIndex);
                }
            }
        }
    }

    private static int[] EligibleAtLeast(IReadOnlyList<IndexedMetric> sortedValues, double load, double? limit)
    {
        if (limit is null) return sortedValues.OrderBy(item => item.Index).Select(item => item.Index).ToArray();
        var denominator = limit.Value + Tolerance(limit.Value);
        if (denominator <= 0) return [];
        var minimum = load / denominator;
        var lo = 0;
        var hi = sortedValues.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (sortedValues[mid].Value < minimum) lo = mid + 1;
            else hi = mid;
        }
        return sortedValues.Skip(lo)
            .Where(item => PassLoadHeight(load, item.Value, limit))
            .OrderBy(item => item.Index).Select(item => item.Index).ToArray();
    }

    private static int[] EligibleAtMost(IReadOnlyList<IndexedMetric> sortedValues, double height, double? limit)
    {
        if (limit is null) return sortedValues.OrderBy(item => item.Index).Select(item => item.Index).ToArray();
        if (height <= 0) return [];
        var maximum = height * (limit.Value + Tolerance(limit.Value));
        var lo = 0;
        var hi = sortedValues.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (sortedValues[mid].Value <= maximum) lo = mid + 1;
            else hi = mid;
        }
        return sortedValues.Take(lo)
            .Where(item => PassPretensionHeight(item.Value, height, limit))
            .OrderBy(item => item.Index).Select(item => item.Index).ToArray();
    }

    private static int EligibleCount(IReadOnlyList<double> sortedValues, double height, double? limit)
    {
        if (limit is null) return sortedValues.Count;
        if (height <= 0) return 0;
        var maximum = height * (limit.Value + Tolerance(limit.Value));
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
        limit is not double max || height > 0 && load <= height * (max + Tolerance(max));
    private static bool PassPretensionHeight(double force, double height, double? limit) =>
        limit is not double max || height > 0 && force <= height * (max + Tolerance(max));
    private static bool InBounds(double value, double? minimum, double? maximum) =>
        (minimum is not double lo || value >= lo - Tolerance(lo)) &&
        (maximum is not double hi || value <= hi + Tolerance(hi));
    private static double LoadingMetric(LoadingStudyCandidate candidate) =>
        candidate.Combination.Loads.Count == 0 ? 0 : candidate.Combination.Loads.Max(item => Math.Abs(item.Qz));
    private static double PretensionMetric(StudyPretensionCandidate candidate) =>
        candidate.Assignments.Count == 0 ? 0 : candidate.Assignments.Max(item => Math.Abs(item.TargetForce));
    private static double Tolerance(double value) => 1e-9 * Math.Max(1, Math.Abs(value));

    private sealed record IndexedMetric(int Index, double Value);
}
