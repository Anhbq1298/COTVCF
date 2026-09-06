using COT_VCF.Domain;
using COT_VCF.Application;
using System.IO;
using System.Numerics;

namespace COT_VCF.Configuration;

public sealed record PointCoordinateMismatch(string PointName, CoordinateDto Saved, CoordinateDto Current)
{
    public override string ToString() => $"{PointName}\n  Saved:   {Saved}\n  Current: {Current}";
}

/// <summary>Schema + model-compatibility result for one tab section. A tab loads only when it is present and clean.</summary>
public sealed class TabCheck
{
    public bool Present { get; set; }
    public List<string> Problems { get; } = new();
    public bool Ok => Present && Problems.Count == 0;
}

/// <summary>
/// Per-tab validation of a loaded config against the connected SAP model. Whole-file problems
/// (bad version, not a config) go in <see cref="FileErrors"/> and abort the load; otherwise each present
/// tab is checked independently so the compatible tabs can still load.
/// </summary>
public sealed class ConfigValidationResult
{
    public List<string> FileErrors { get; } = new();
    public bool FileNameMatches { get; set; }
    public TabCheck Loading { get; } = new();
    public TabCheck Truss { get; } = new();
    public TabCheck Cable { get; } = new();
    public TabCheck Study { get; } = new();

    public bool FileValid => FileErrors.Count == 0;
    public bool AnyPresent => Loading.Present || Truss.Present || Cable.Present || Study.Present;
    public bool AnyOk => Loading.Ok || Truss.Ok || Cable.Ok || Study.Ok;
    public bool AllPresentOk => AnyPresent
        && (!Loading.Present || Loading.Ok) && (!Truss.Present || Truss.Ok) && (!Cable.Present || Cable.Ok) && (!Study.Present || Study.Ok);
    public bool ModelAppearsRenamed => FileValid && AnyOk && !FileNameMatches;
}

public sealed class ConfigCompatibilityValidator
{
    public const double CoordinateToleranceM = 0.001; // 1 mm
    private static readonly string[] SupportedVersions = { "1.0" };

    public ConfigValidationResult Validate(CotVcfConfigDto cfg, ApplicationState state, IReadOnlyCollection<string> modelLoadCases)
    {
        var r = new ConfigValidationResult();
        if (cfg is null) { r.FileErrors.Add("The file is not a COT VCF configuration."); return r; }
        if (string.IsNullOrWhiteSpace(cfg.ConfigVersion)) r.FileErrors.Add("Missing configVersion.");
        else if (Array.IndexOf(SupportedVersions, cfg.ConfigVersion) < 0) r.FileErrors.Add($"Unsupported configVersion '{cfg.ConfigVersion}'.");
        if (cfg.PresentTabs == ConfigTabs.None) r.FileErrors.Add("The file contains no tab configuration to load.");
        if (!r.FileValid) return r;

        var currentFile = state.Model is { } m ? System.IO.Path.GetFileName(m.Path) : "";
        r.FileNameMatches = string.Equals(currentFile, cfg.Model?.FileName ?? "", StringComparison.OrdinalIgnoreCase);

        var savedPoints = (cfg.Model?.Points ?? []).ToDictionary(p => p.Name, StringComparer.Ordinal);
        var savedCables = (cfg.Model?.Cables ?? []).ToDictionary(c => c.Name, StringComparer.Ordinal);
        var current = state.Geometry.Points;
        var currentCables = state.Geometry.Members.Where(mm => mm.Kind == MemberKind.Cable).ToDictionary(mm => mm.Name, StringComparer.Ordinal);
        var patterns = new HashSet<string>(state.LoadPatterns, StringComparer.OrdinalIgnoreCase);

        void CheckPoint(TabCheck tc, string name)
        {
            if (!current.TryGetValue(name, out var cur)) { tc.Problems.Add($"missing point {name}"); return; }
            if (savedPoints.TryGetValue(name, out var sp) && !CloseXyz(cur.Position, sp.X, sp.Y, sp.Z))
                tc.Problems.Add(new PointCoordinateMismatch(name,
                    new() { X = sp.X, Y = sp.Y, Z = sp.Z },
                    new() { X = cur.Position.X, Y = cur.Position.Y, Z = cur.Position.Z }).ToString());
        }

        // ---- Tab 02 Loading ----
        if (cfg.Tab02Loading is { } t2)
        {
            var tc = r.Loading; tc.Present = true;
            foreach (var row in t2.LoadDefinitions ?? [])
            {
                var tag = Name(row.LoadName);
                if (string.IsNullOrWhiteSpace(row.LoadName) || !patterns.Contains(row.LoadName))
                    tc.Problems.Add($"{tag}: load pattern does not exist in the connected model.");
                if (row.IncrementEnabled)
                {
                    if (row.MinQz is null || row.MaxQz is null || row.StepQz is null) tc.Problems.Add($"{tag}: range needs Min, Max and Step qz.");
                    else if (row.StepQz <= 0) tc.Problems.Add($"{tag}: step qz must be greater than zero.");
                    else if (row.MaxQz < row.MinQz) tc.Problems.Add($"{tag}: max qz must be at least min qz.");
                }
                else if (row.FixedQz is null || !double.IsFinite(row.FixedQz.Value)) tc.Problems.Add($"{tag}: a fixed qz value is required.");
            }
            if ((t2.LoadDefinitions ?? []).Where(row => !string.IsNullOrWhiteSpace(row.LoadName))
                .GroupBy(row => row.LoadName, StringComparer.Ordinal).Any(group => group.Count() > 1))
                tc.Problems.Add("each load pattern can appear only once.");
            foreach (var name in (t2.AppliedPoints ?? []).Distinct(StringComparer.Ordinal)) CheckPoint(tc, name);
            foreach (var pat in savedPoints.Count == 0 ? [] : (cfg.Model?.LoadPatterns ?? []))
                if (!patterns.Contains(pat)) tc.Problems.Add($"missing load pattern {pat}");
        }

        // ---- Tab 03 Truss Height ----
        if (cfg.Tab03TrussHeight is { } t3)
        {
            var tc = r.Truss; tc.Present = true;
            if (!Enum.IsDefined(t3.ReferenceElevation)) tc.Problems.Add("reference elevation is invalid.");
            if ((t3.TopPoints?.Count ?? 0) == 0 || (t3.MiddlePoints?.Count ?? 0) == 0 || (t3.BottomPoints?.Count ?? 0) == 0)
                tc.Problems.Add("top, middle and bottom point groups are all required.");
            if (!(t3.StepDeltaH > 0)) tc.Problems.Add("step ΔH must be greater than zero.");
            if (t3.MaxDeltaH < t3.MinDeltaH) tc.Problems.Add("max ΔH must be at least min ΔH.");
            foreach (var name in Concat(t3.TopPoints, t3.MiddlePoints, t3.BottomPoints)) CheckPoint(tc, name);
        }

        // ---- Tab 04 Cable Force ----
        if (cfg.Tab04CableForce is { } t4)
        {
            var tc = r.Cable; tc.Present = true;
            foreach (var row in t4.Cables ?? [])
            {
                var tag = Name(row.CableName);
                if (string.IsNullOrWhiteSpace(row.PretensionLoadCase)) tc.Problems.Add($"{tag}: target-force load pattern is required.");
                else if (!patterns.Contains(row.PretensionLoadCase)) tc.Problems.Add($"{tag}: load pattern {row.PretensionLoadCase} does not exist in the model. Cable target force assignments require a load pattern, not an analysis load case.");
                if (row.Mode == CableForceMode.Range)
                {
                    if (row.MinForce is null || row.MaxForce is null || row.StepForce is null) tc.Problems.Add($"{tag}: range needs Min, Max and Step force.");
                    else if (row.StepForce <= 0) tc.Problems.Add($"{tag}: step force must be greater than zero.");
                    else if (row.MaxForce < row.MinForce) tc.Problems.Add($"{tag}: max force must be at least min force.");
                }
                else if (row.FixedForce is null || !double.IsFinite(row.FixedForce.Value)) tc.Problems.Add($"{tag}: a fixed target force is required.");

                if (!currentCables.TryGetValue(row.CableName, out var cm)) { tc.Problems.Add($"missing cable {row.CableName}"); continue; }
                if (savedCables.TryGetValue(row.CableName, out var sc))
                {
                    if (cm.Start != sc.PointI || cm.End != sc.PointJ)
                        tc.Problems.Add($"{row.CableName}: endpoints changed (saved {sc.PointI}–{sc.PointJ}, now {cm.Start}–{cm.End})");
                    else
                    {
                        if (current.TryGetValue(cm.Start, out var pi) && !CloseCoord(pi.Position, sc.PointICoordinate)) tc.Problems.Add($"{row.CableName}: I-end {cm.Start} moved");
                        if (current.TryGetValue(cm.End, out var pj) && !CloseCoord(pj.Position, sc.PointJCoordinate)) tc.Problems.Add($"{row.CableName}: J-end {cm.End} moved");
                    }
                }
            }
        }

        // ---- Tab 05 Iteration Study ----
        if (cfg.Tab05IterationStudy is { } t5)
        {
            var tc = r.Study; tc.Present = true;
            if (cfg.Tab02Loading is null || cfg.Tab03TrussHeight is null || cfg.Tab04CableForce is null)
                tc.Problems.Add("the study requires the Loading, Truss Height and Cable Force sections in the same project.");
            if (!Enum.IsDefined(t5.StudyMode)) tc.Problems.Add("study mode is invalid.");
            if (t5.StudyMode == IterationStudyMode.FixHeight && !ValidCandidateId(t5.FixedHeightCandidateId, 'H'))
                tc.Problems.Add("fixed height candidate ID is invalid.");
            else if (t5.StudyMode == IterationStudyMode.FixHeight && CandidateOrdinal(t5.FixedHeightCandidateId) > HeightCandidateCount(cfg, state))
                tc.Problems.Add($"fixed height candidate {t5.FixedHeightCandidateId} does not exist.");
            if (t5.StudyMode == IterationStudyMode.FixPretension && !ValidCandidateId(t5.FixedPretensionCandidateId, 'P'))
                tc.Problems.Add("fixed pretension candidate ID is invalid.");
            else if (t5.StudyMode == IterationStudyMode.FixPretension && CandidateOrdinal(t5.FixedPretensionCandidateId) > PretensionCandidateCount(cfg, state))
                tc.Problems.Add($"fixed pretension candidate {t5.FixedPretensionCandidateId} does not exist.");
            // OutputDirectory is retained for JSON compatibility only. Runtime study paths are always
            // derived from the connected immutable baseline model.
        }

        return r;
    }

    private static IEnumerable<string> Concat(params List<string>?[] groups) =>
        groups.SelectMany(g => g ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal);
    private static string Name(string s) => string.IsNullOrWhiteSpace(s) ? "(unnamed)" : s;
    private static bool ValidCandidateId(string? id, char prefix) =>
        id is { Length: >= 2 } && id[0] == prefix && long.TryParse(id[1..], out var n) && n > 0;
    private static long CandidateOrdinal(string id) => long.TryParse(id[1..], out var n) ? n : 0;
    private static long HeightCandidateCount(CotVcfConfigDto cfg, ApplicationState state)
    {
        if (cfg.Tab03TrussHeight is not { } h) return state.Truss.CandidateCount;
        if (!double.IsFinite(h.MinDeltaH) || !double.IsFinite(h.MaxDeltaH) || !double.IsFinite(h.StepDeltaH) || h.StepDeltaH <= 0 || h.MaxDeltaH < h.MinDeltaH) return 0;
        var count = Math.Floor((h.MaxDeltaH - h.MinDeltaH) / h.StepDeltaH + 1e-9) + 1;
        return count > long.MaxValue ? long.MaxValue : (long)count;
    }
    private static BigInteger PretensionCandidateCount(CotVcfConfigDto cfg, ApplicationState state)
    {
        if (cfg.Tab04CableForce is not { } cableConfig) return PretensionCandidates.TotalCount(state.Cables);
        BigInteger total = BigInteger.One;
        if (cableConfig.Cables.Count == 0) return BigInteger.Zero;
        foreach (var cable in cableConfig.Cables)
        {
            if (cable.Mode == CableForceMode.Fixed)
            {
                if (cable.FixedForce is not double value || !double.IsFinite(value)) return BigInteger.Zero;
                continue;
            }
            if (cable.MinForce is not double min || cable.MaxForce is not double max || cable.StepForce is not double step ||
                !double.IsFinite(min) || !double.IsFinite(max) || !double.IsFinite(step) || step <= 0 || max < min) return BigInteger.Zero;
            total *= (long)Math.Floor((max - min) / step + 1e-6) + 1;
        }
        return total;
    }
    private static bool Close(double a, double b) => Math.Abs(a - b) <= CoordinateToleranceM;
    private static bool CloseXyz(Position p, double x, double y, double z) => Close(p.X, x) && Close(p.Y, y) && Close(p.Z, z);
    private static bool CloseCoord(Position p, CoordinateDto c) => Close(p.X, c.X) && Close(p.Y, c.Y) && Close(p.Z, c.Z);
}
