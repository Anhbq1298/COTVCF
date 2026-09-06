using System.Numerics;
using System.IO;
using COT_VCF.Domain;

namespace COT_VCF.Application;

public sealed record StudyLoadingCandidate(string Id, LoadingCombination Combination, IReadOnlyList<LoadingCandidate> PointLoads)
{
    public string DisplayName => $"{Id} - {Combination.Loads.Count} load definition{(Combination.Loads.Count == 1 ? "" : "s")}";
}

public sealed record StudyHeightCandidate(string Id, TrussCandidate Candidate, string Unit)
{
    public string DisplayName => $"{Id} - {Candidate.Height:0.000} {Unit}";
}

public sealed record StudyPretensionCandidate(string Id, PretensionCombination Candidate)
{
    public string DisplayName => $"{Id} - {Candidate.CableForces.Count} cable{(Candidate.CableForces.Count == 1 ? "" : "s")}";
}

public sealed class StudyIterationPreview : Observable
{
    private bool runIterate;

    public required int RunIndex { get; init; }
    public required string IterationKey { get; init; }
    public required string LoadingCandidateId { get; init; }
    public required string HeightCandidateId { get; init; }
    public required string PretensionCandidateId { get; init; }
    public required StudyLoadingCandidate LoadingCandidate { get; init; }
    public required TrussCandidate HeightCandidate { get; init; }
    public required PretensionCombination PretensionCandidate { get; init; }
    public required string SapModelName { get; init; }
    public required string OutputDirectory { get; init; }
    public required string FullOutputPath { get; init; }
    public required string Description { get; init; }
    public bool RunIterate { get => runIterate; set => Set(ref runIterate, value); }
    public bool IsValid { get; init; } = true;
    public string ValidationMessage { get; init; } = "";
}

public sealed record StudyCompositionResult(
    IReadOnlyList<StudyLoadingCandidate> LoadingCandidates,
    IReadOnlyList<StudyHeightCandidate> HeightCandidates,
    IReadOnlyList<StudyPretensionCandidate> PretensionCandidates,
    IReadOnlyList<StudyIterationPreview> Iterations,
    BigInteger ExpectedRunCount,
    bool PreviewLimitExceeded,
    string EffectiveFixedHeightId,
    string EffectiveFixedPretensionId);

public sealed class StudyCompositionService
{
    public const int MaxPreviewRows = 50_000;

    public StudyCompositionResult Compose(ApplicationState state)
    {
        var loadCount = LoadingCandidates.TotalCount(state.LoadDefinitions);
        var heightCount = state.BaselineCaptured ? state.Truss.CandidateCount : 0;
        var pretensionCount = state.Cables.Count == 0 || state.Cables.Any(c => c.CandidateCount <= 0)
            ? BigInteger.Zero
            : PretensionCandidates.TotalCount(state.Cables);
        var expected = StudyPlan.TotalRuns(state.Study.Mode, ToIntCount(loadCount), heightCount, pretensionCount);

        var candidateLimitExceeded = loadCount > MaxPreviewRows || heightCount > MaxPreviewRows || pretensionCount > MaxPreviewRows;
        if (loadCount <= 0 || heightCount <= 0 || pretensionCount <= 0 || candidateLimitExceeded)
            return Empty(expected, candidateLimitExceeded || expected > MaxPreviewRows, state.Study);

        var loads = LoadingCandidates.Enumerate(state.LoadDefinitions.ToArray())
            .Select(c => new StudyLoadingCandidate(CandidateId('L', c.Index), c, BuildPointLoads(state, c)))
            .ToArray();
        var heights = state.Truss.CandidateHeights.Select((height, index) =>
        {
            var delta = height - state.Truss.BaselineHeight;
            var (top, mid, bot) = COT_VCF.Geometry.TrussGeometryService.ChordDeltas(state.Truss.Reference, delta);
            return new StudyHeightCandidate(CandidateId('H', index),
                new TrussCandidate(index + 1, height, delta, state.Truss.Reference.ToString(), top, mid, bot),
                state.Model?.LengthUnit ?? "m");
        }).ToArray();
        var pretensions = PretensionCandidates.Enumerate(state.Cables.ToArray())
            .Select(c => new StudyPretensionCandidate(CandidateId('P', c.CombinationIndex), c))
            .ToArray();

        var fixedHeight = ResolveId(state.Study.FixedHeightCandidateId, heights.Select(c => c.Id));
        var fixedPretension = ResolveId(state.Study.FixedPretensionCandidateId, pretensions.Select(c => c.Id));
        if (expected > MaxPreviewRows)
            return new(loads, heights, pretensions, [], expected, true, fixedHeight, fixedPretension);

        int fixedHeightIndex = Array.FindIndex(heights, c => c.Id == fixedHeight);
        int fixedPretensionIndex = Array.FindIndex(pretensions, c => c.Id == fixedPretension);
        if ((state.Study.Mode == IterationStudyMode.FixHeight && fixedHeightIndex < 0) ||
            (state.Study.Mode == IterationStudyMode.FixPretension && fixedPretensionIndex < 0))
            return new(loads, heights, pretensions, [], expected, false, fixedHeight, fixedPretension);
        var rows = new List<StudyIterationPreview>((int)expected);
        foreach (var run in StudyPlan.Enumerate(state.Study.Mode, loads.Length, heights.Length, pretensions.LongLength,
                     fixedHeightIndex, fixedPretensionIndex))
        {
            var load = loads[run.LoadIndex];
            var height = heights[run.HeightIndex];
            var pretension = pretensions[run.PretensionIndex];
            var key = $"{load.Id}_{height.Id}_{pretension.Id}";
            var fileName = BuildModelName(state.SessionBaselineModel ?? state.Model, key);
            var fullPath = CombinePath(state.Study.OutputDirectory, fileName);
            rows.Add(new StudyIterationPreview
            {
                RunIndex = checked((int)run.Index + 1),
                IterationKey = key,
                LoadingCandidateId = load.Id,
                HeightCandidateId = height.Id,
                PretensionCandidateId = pretension.Id,
                LoadingCandidate = load,
                HeightCandidate = height.Candidate,
                PretensionCandidate = pretension.Candidate,
                SapModelName = fileName,
                OutputDirectory = state.Study.OutputDirectory,
                FullOutputPath = fullPath,
                Description = Describe(state, load, height, pretension),
                RunIterate = state.Study.IsRunSelected(key),
                IsValid = fullPath.Length > 0,
                ValidationMessage = fullPath.Length > 0 ? "" : "Output folder is not valid."
            });
        }

        return new(loads, heights, pretensions, rows, expected, false, fixedHeight, fixedPretension);
    }

    public static string CandidateId(char prefix, long zeroBasedIndex) => $"{prefix}{zeroBasedIndex + 1:D2}";

    private static int ToIntCount(BigInteger count) => count > int.MaxValue ? int.MaxValue : count < 0 ? 0 : (int)count;

    private static StudyCompositionResult Empty(BigInteger expected, bool exceeded, IterationStudyConfig study) =>
        new([], [], [], [], expected, exceeded, study.FixedHeightCandidateId, study.FixedPretensionCandidateId);

    private static string ResolveId(string selected, IEnumerable<string> available)
    {
        var ids = available.ToArray();
        return ids.Contains(selected, StringComparer.Ordinal) ? selected : "";
    }

    private static string BuildModelName(ModelInfo? baseline, string key)
    {
        var stem = baseline is null || string.IsNullOrWhiteSpace(baseline.Path)
            ? "COT_VCF"
            : Path.GetFileNameWithoutExtension(baseline.Path);
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var clean = new string(stem.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return $"{(clean.Length == 0 ? "COT_VCF" : clean)}_{key}.sdb";
    }

    private static string CombinePath(string directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(directory)) return "";
        try { return Path.GetFullPath(Path.Combine(directory, fileName)); }
        catch { return ""; }
    }

    private static string Describe(ApplicationState state, StudyLoadingCandidate load,
        StudyHeightCandidate height, StudyPretensionCandidate pretension)
    {
        var parts = new[]
        {
            DescribeLoading(state, load),
            $"Height {state.Truss.BaselineHeight:0.###} \u2192 {height.Candidate.Height:0.###} {height.Unit} (\u0394H {height.Candidate.DeltaH:+0.###;-0.###;0} {height.Unit}, {height.Id})",
            DescribePretension(state, pretension)
        };
        return string.Join(" | ", parts);
    }

    private static string DescribeLoading(ApplicationState state, StudyLoadingCandidate load)
    {
        var values = load.Combination.Loads;
        var qzText = values.Count <= 3
            ? string.Join(", ", values.Select(v => $"{v.Name}={v.Qz:0.###}"))
            : $"{values.Count} definitions, qz {values.Min(v => v.Qz):0.###}-{values.Max(v => v.Qz):0.###}";
        var baseline = state.SessionBaselinePointLoads
            .GroupBy(row => (row.PointName, row.LoadPattern))
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Fz));
        var comparisons = load.PointLoads.Select(row =>
        {
            var baselineFz = baseline.GetValueOrDefault((row.AppliedPt, row.Case));
            return (Baseline: baselineFz, Candidate: row.FzKn);
        }).ToArray();
        var changed = comparisons.Count(pair => !Close(pair.Baseline, pair.Candidate));
        var forceText = comparisons.Length == 0 ? "no point-load assignments"
            : comparisons.Length == 1 ? $"Fz {comparisons[0].Baseline:0.###} \u2192 {comparisons[0].Candidate:0.###} kN"
            : $"{changed} of {comparisons.Length} assignments updated, Fz {Range(comparisons.Select(pair => pair.Baseline))} \u2192 {Range(comparisons.Select(pair => pair.Candidate))} kN";
        return $"Loading {load.Id}: {forceText}; qz {qzText} kPa";
    }

    private static IReadOnlyList<LoadingCandidate> BuildPointLoads(ApplicationState state, LoadingCombination combination)
    {
        var points = state.AppliedPoints.OrderBy(point => point.X).ThenBy(point => point.PointName, StringComparer.Ordinal).ToArray();
        var rows = new List<LoadingCandidate>(points.Length * combination.Loads.Count);
        var stories = double.IsFinite(state.NumberOfStories) && state.NumberOfStories >= 1 && state.NumberOfStories <= int.MaxValue
            ? (int)Math.Truncate(state.NumberOfStories)
            : 0;
        for (int i = 0; i < points.Length; i++)
        {
            var tributaryLength = points.Length switch
            {
                1 => 0,
                _ when i == 0 => Math.Max(0, points[1].X - points[0].X) / 2,
                _ when i == points.Length - 1 => Math.Max(0, points[^1].X - points[^2].X) / 2,
                _ => Math.Max(0, points[i + 1].X - points[i - 1].X) / 2
            };
            var area = tributaryLength * state.TributaryWidthB;
            foreach (var (name, qz) in combination.Loads)
                rows.Add(new LoadingCandidate(points[i].PointName, name, qz, area, stories, -qz * area * stories));
        }
        return rows;
    }

    private static string DescribePretension(ApplicationState state, StudyPretensionCandidate pretension)
    {
        var forces = pretension.Candidate.CableForces;
        var changed = forces.Count(force => !state.SessionBaselinePretensionForces.TryGetValue(force.Key, out var baseline) || !Close(baseline, force.Value));
        var valueText = forces.Count <= 3
            ? string.Join(", ", forces.Select(force => state.SessionBaselinePretensionForces.TryGetValue(force.Key, out var baseline)
                ? $"{force.Key} {baseline:0.###} \u2192 {force.Value:0.###}"
                : $"{force.Key} unassigned \u2192 {force.Value:0.###}"))
            : $"{changed} of {forces.Count} cables updated, target range {forces.Values.Min():0.###}-{forces.Values.Max():0.###}";
        return $"Pretension {pretension.Id}: {valueText} kN";
    }

    private static string Range(IEnumerable<double> values)
    {
        var materialized = values.ToArray();
        if (materialized.Length == 0) return "0";
        var min = materialized.Min();
        var max = materialized.Max();
        return Close(min, max) ? min.ToString("0.###") : $"{min:0.###}..{max:0.###}";
    }

    private static bool Close(double left, double right) =>
        Math.Abs(left - right) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));
}
