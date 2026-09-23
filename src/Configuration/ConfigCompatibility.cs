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
    public TabCheck Output { get; } = new();
    public TabCheck Study { get; } = new();

    public bool FileValid => FileErrors.Count == 0;
    public bool AnyPresent => Loading.Present || Truss.Present || Cable.Present || Output.Present || Study.Present;
    public bool AnyOk => Loading.Ok || Truss.Ok || Cable.Ok || Output.Ok || Study.Ok;
    public bool AllPresentOk => AnyPresent
        && (!Loading.Present || Loading.Ok) && (!Truss.Present || Truss.Ok) && (!Cable.Present || Cable.Ok)
        && (!Output.Present || Output.Ok) && (!Study.Present || Study.Ok);
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
        if (cfg.Units is null || cfg.Units.Length != "m" || cfg.Units.Force != "kN" || cfg.Units.Pressure != "kPa")
            r.FileErrors.Add("Configuration units must be m, kN and kPa.");
        if ((cfg.Model?.Points ?? []).GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() > 1) ||
            (cfg.Model?.Cables ?? []).GroupBy(c => c.Name, StringComparer.Ordinal).Any(g => g.Count() > 1) ||
            (cfg.Model?.Frames ?? []).GroupBy(f => f.Name, StringComparer.Ordinal).Any(g => g.Count() > 1))
            r.FileErrors.Add("The model snapshot contains duplicate object names.");
        if (!r.FileValid) return r;

        var currentFile = state.Model is { } m ? System.IO.Path.GetFileName(m.Path) : "";
        r.FileNameMatches = string.Equals(currentFile, cfg.Model?.FileName ?? "", StringComparison.OrdinalIgnoreCase);

        var savedPoints = (cfg.Model?.Points ?? []).ToDictionary(p => p.Name, StringComparer.Ordinal);
        var savedCables = (cfg.Model?.Cables ?? []).ToDictionary(c => c.Name, StringComparer.Ordinal);
        var savedFrames = (cfg.Model?.Frames ?? []).ToDictionary(c => c.Name, StringComparer.Ordinal);
        var current = state.Geometry.Points;
        var currentCables = state.Geometry.Members.Where(mm => mm.Kind == MemberKind.Cable).ToDictionary(mm => mm.Name, StringComparer.Ordinal);
        var currentFrames = state.Geometry.Members.Where(mm => mm.Kind == MemberKind.Frame).ToDictionary(mm => mm.Name, StringComparer.Ordinal);
        var patterns = new HashSet<string>(state.LoadPatterns, StringComparer.OrdinalIgnoreCase);

        void CheckPoint(TabCheck tc, string name)
        {
            if (!current.TryGetValue(name, out var cur)) { tc.Problems.Add($"missing point {name}"); return; }
            if (savedPoints.TryGetValue(name, out var sp) && !CloseXyz(cur.Position, sp.X, sp.Y, sp.Z))
                tc.Problems.Add(new PointCoordinateMismatch(name,
                    new() { X = sp.X, Y = sp.Y, Z = sp.Z },
                    new() { X = cur.Position.X, Y = cur.Position.Y, Z = cur.Position.Z }).ToString());
        }

        // ---- Tab 01 Loading ----
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
                    else if (!double.IsFinite(row.MinQz.Value) || !double.IsFinite(row.MaxQz.Value) || !double.IsFinite(row.StepQz.Value)) tc.Problems.Add($"{tag}: range values must be finite.");
                    else if (row.MinQz < 0) tc.Problems.Add($"{tag}: minimum qz must be zero or greater.");
                    else if (row.StepQz <= 0) tc.Problems.Add($"{tag}: step qz must be greater than zero.");
                    else if (row.MaxQz < row.MinQz) tc.Problems.Add($"{tag}: max qz must be at least min qz.");
                    else if (LoadingRowCount(row) > LoadDefinition.MaterializeCeiling) tc.Problems.Add($"{tag}: range exceeds {LoadDefinition.MaterializeCeiling:N0} values.");
                }
                else if (row.FixedQz is null || !double.IsFinite(row.FixedQz.Value) || row.FixedQz.Value < 0) tc.Problems.Add($"{tag}: a finite, nonnegative fixed qz value is required.");
            }
            if ((t2.LoadDefinitions ?? []).Where(row => !string.IsNullOrWhiteSpace(row.LoadName))
                .GroupBy(row => row.LoadName, StringComparer.Ordinal).Any(group => group.Count() > 1))
                tc.Problems.Add("each load pattern can appear only once.");
            foreach (var name in (t2.AppliedPoints ?? []).Distinct(StringComparer.Ordinal)) CheckPoint(tc, name);
            foreach (var pat in savedPoints.Count == 0 ? [] : (cfg.Model?.LoadPatterns ?? []))
                if (!patterns.Contains(pat)) tc.Problems.Add($"missing load pattern {pat}");
        }

        // ---- Tab 02 Truss Height ----
        if (cfg.Tab03TrussHeight is { } t3)
        {
            var tc = r.Truss; tc.Present = true;
            if (!Enum.IsDefined(t3.ReferenceElevation)) tc.Problems.Add("reference elevation is invalid.");
            if ((t3.TopPoints?.Count ?? 0) == 0 || (t3.MiddlePoints?.Count ?? 0) == 0 || (t3.BottomPoints?.Count ?? 0) == 0)
                tc.Problems.Add("top, middle and bottom point groups are all required.");
            if (!double.IsFinite(t3.MinDeltaH) || !double.IsFinite(t3.MaxDeltaH) || !double.IsFinite(t3.StepDeltaH)) tc.Problems.Add("height-range values must be finite.");
            else if (!(t3.StepDeltaH > 0)) tc.Problems.Add("step ΔH must be greater than zero.");
            if (t3.MaxDeltaH < t3.MinDeltaH) tc.Problems.Add("max ΔH must be at least min ΔH.");
            foreach (var name in Concat(t3.TopPoints, t3.MiddlePoints, t3.BottomPoints)) CheckPoint(tc, name);
        }

        // ---- Tab 03 Cable Force ----
        if (cfg.Tab04CableForce is { } t4)
        {
            var tc = r.Cable; tc.Present = true;
            var rows = t4.Cables ?? [];
            if (rows.Where(row => !string.IsNullOrWhiteSpace(row.CableName))
                .GroupBy(row => row.CableName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                tc.Problems.Add("each cable can appear only once.");

            if (!double.IsFinite(t4.CableStrengthMpa) || t4.CableStrengthMpa <= 0)
                tc.Problems.Add("cable strength must be a positive number of MPa.");
            if (!double.IsFinite(t4.StrengthReductionFactor) || t4.StrengthReductionFactor <= 0 || t4.StrengthReductionFactor > 1)
                tc.Problems.Add("strength-reduction coefficient φ must be greater than zero and at most one.");

            var runtime = rows.Select(ToRuntimeCable).ToArray();
            foreach (var cable in runtime) cable.Peers = () => runtime;

            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                var cable = runtime[index];
                var tag = Name(row.CableName);
                if (string.IsNullOrWhiteSpace(row.PretensionLoadCase)) tc.Problems.Add($"{tag}: target-force load pattern is required.");
                else if (!patterns.Contains(row.PretensionLoadCase)) tc.Problems.Add($"{tag}: load pattern {row.PretensionLoadCase} does not exist in the model. Cable target force assignments require a load pattern, not an analysis load case.");
                if (!Enum.IsDefined(row.Mode)) tc.Problems.Add($"{tag}: cable-force mode is invalid.");
                if (!Enum.IsDefined(row.SyncMode)) tc.Problems.Add($"{tag}: Sync mode is invalid.");

                if (cable.SyncMode == CableSyncMode.Independent && row.Mode == CableForceMode.Range)
                {
                    if (row.MinForce is null || row.MaxForce is null || row.StepForce is null) tc.Problems.Add($"{tag}: range needs Min, Max and Step force.");
                    else if (!double.IsFinite(row.MinForce.Value) || !double.IsFinite(row.MaxForce.Value) || !double.IsFinite(row.StepForce.Value)) tc.Problems.Add($"{tag}: range values must be finite.");
                    else if (row.StepForce <= 0) tc.Problems.Add($"{tag}: step force must be greater than zero.");
                    else if (row.MaxForce < row.MinForce) tc.Problems.Add($"{tag}: max force must be at least min force.");
                    else if (cable.CandidateCount > CablePretensionConfig.MaterializeCeiling) tc.Problems.Add($"{tag}: range exceeds {CablePretensionConfig.MaterializeCeiling:N0} values.");
                }
                else if (cable.SyncMode == CableSyncMode.Independent &&
                         (row.FixedForce is null || !double.IsFinite(row.FixedForce.Value)))
                    tc.Problems.Add($"{tag}: a fixed target force is required.");

                if (cable.SyncMode != CableSyncMode.Independent)
                {
                    if (string.IsNullOrWhiteSpace(cable.ReferenceCableName)) tc.Problems.Add($"{tag}: {cable.SyncMode.Label()} needs a reference cable.");
                    else if (CableSyncGraph.DirectReference(cable, runtime) is null) tc.Problems.Add($"{tag}: reference cable '{cable.ReferenceCableName}' does not exist.");
                    else if (CableSyncGraph.RootOf(cable, runtime) is null) tc.Problems.Add($"{tag}: reference chain is broken or circular.");
                    if (cable.SyncMode == CableSyncMode.SameStep &&
                        (row.Mode != CableForceMode.Range || row.MinForce is not double lo || row.MaxForce is not double hi || !double.IsFinite(lo) || !double.IsFinite(hi) || hi < lo))
                        tc.Problems.Add($"{tag}: Same Step needs finite Min and Max force values.");
                    if (cable.SyncMode is CableSyncMode.SameRatio or CableSyncMode.SameOffset &&
                        (row.SyncFactor is not double factor || !double.IsFinite(factor) || (cable.SyncMode == CableSyncMode.SameRatio && factor <= 0)))
                        tc.Problems.Add($"{tag}: {cable.SyncMode.Label()} needs a {(cable.SyncMode == CableSyncMode.SameRatio ? "ratio greater than zero" : "finite offset")}.");
                    if (ResolvedEndpointForces(cable, runtime).Any(value => !double.IsFinite(value)))
                        tc.Problems.Add($"{tag}: resolved Sync force is not finite.");
                }

                if (row.SectionAreaM2 is double area && (!double.IsFinite(area) || area <= 0))
                    tc.Problems.Add($"{tag}: section area must be greater than zero.");
                if (row.DesignCapacityKN is double capacity && (!double.IsFinite(capacity) || capacity <= 0))
                    tc.Problems.Add($"{tag}: design capacity must be greater than zero.");

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
            if (PretensionCandidates.FindCycle(runtime) is { Count: > 0 } cycle)
                tc.Problems.Add($"circular Sync reference: {string.Join(" → ", cycle)} → {cycle[0]}.");
        }

        // ---- Tab 04 Output Settings ----
        if (cfg.Tab05OutputSettings is { } t5)
        {
            var tc = r.Output; tc.Present = true;
            if (!string.Equals(t5.OutputGroupPrefix, OutputGroupNaming.Prefix, StringComparison.Ordinal))
                tc.Problems.Add($"output group prefix must be {OutputGroupNaming.Prefix}.");

            foreach (var name in (t5.DeflectionPointNames ?? []).Distinct(StringComparer.Ordinal)) CheckPoint(tc, name);
            foreach (var name in (t5.FoundationLeftPointNames ?? []).Distinct(StringComparer.Ordinal)) CheckPoint(tc, name);
            foreach (var name in (t5.FoundationRightPointNames ?? []).Distinct(StringComparer.Ordinal)) CheckPoint(tc, name);

            if ((t5.FoundationLeftPointNames?.Count ?? 0) != 2) tc.Problems.Add("Foundation Left requires exactly 2 points.");
            if ((t5.FoundationRightPointNames?.Count ?? 0) != 2) tc.Problems.Add("Foundation Right requires exactly 2 points.");
            if ((t5.FoundationLeftPointNames ?? []).Intersect(t5.FoundationRightPointNames ?? [], StringComparer.Ordinal).Any())
                tc.Problems.Add("foundation Left and Right groups cannot share a point.");
            if (string.IsNullOrWhiteSpace(t5.FoundationReactionResultLoadCase))
                tc.Problems.Add("foundation reaction result load case is required.");
            else if (!modelLoadCases.Contains(t5.FoundationReactionResultLoadCase, StringComparer.OrdinalIgnoreCase))
                tc.Problems.Add($"foundation reaction result load case {t5.FoundationReactionResultLoadCase} does not exist in the connected model.");

            var sapGroupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assignedFrames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var group in t5.MemberForceGroups ?? [])
            {
                var expectedSapName = OutputGroupNaming.MemberGroupName(group.Name);
                if (string.IsNullOrWhiteSpace(group.Name)) tc.Problems.Add("member-force group name is required.");
                if (!string.IsNullOrWhiteSpace(group.SapGroupName) &&
                    !string.Equals(group.SapGroupName, expectedSapName, StringComparison.Ordinal))
                    tc.Problems.Add($"{Name(group.Name)}: saved SAP group name does not match {expectedSapName}.");
                if (OutputGroupNaming.ReservedNames.Contains(expectedSapName))
                    tc.Problems.Add($"{expectedSapName} is reserved for a fixed output group.");
                if (!sapGroupNames.Add(expectedSapName)) tc.Problems.Add($"duplicate member-force group {expectedSapName}.");
                if ((group.FrameNames?.Count ?? 0) == 0) tc.Problems.Add($"{expectedSapName} contains no frames.");

                foreach (var frameName in (group.FrameNames ?? []).Distinct(StringComparer.Ordinal))
                {
                    if (!currentFrames.TryGetValue(frameName, out var currentFrame))
                    {
                        tc.Problems.Add($"missing frame {frameName}");
                        continue;
                    }
                    if (assignedFrames.TryGetValue(frameName, out var prior) && !string.Equals(prior, expectedSapName, StringComparison.OrdinalIgnoreCase))
                        tc.Problems.Add($"frame {frameName} is assigned to both {prior} and {expectedSapName}.");
                    else assignedFrames[frameName] = expectedSapName;

                    if (savedFrames.TryGetValue(frameName, out var savedFrame))
                    {
                        if (currentFrame.Start != savedFrame.PointI || currentFrame.End != savedFrame.PointJ)
                            tc.Problems.Add($"{frameName}: endpoints changed (saved {savedFrame.PointI}–{savedFrame.PointJ}, now {currentFrame.Start}–{currentFrame.End})");
                        else
                        {
                            if (current.TryGetValue(currentFrame.Start, out var pi) && !CloseCoord(pi.Position, savedFrame.PointICoordinate)) tc.Problems.Add($"{frameName}: I-end {currentFrame.Start} moved");
                            if (current.TryGetValue(currentFrame.End, out var pj) && !CloseCoord(pj.Position, savedFrame.PointJCoordinate)) tc.Problems.Add($"{frameName}: J-end {currentFrame.End} moved");
                        }
                    }
                }
            }

            if ((t5.MemberForceGroups?.Count ?? 0) > 0 && string.IsNullOrWhiteSpace(t5.MemberForceResultLoadCase))
                tc.Problems.Add("member-force result load case is required.");
            else if (!string.IsNullOrWhiteSpace(t5.MemberForceResultLoadCase) &&
                     !modelLoadCases.Contains(t5.MemberForceResultLoadCase, StringComparer.OrdinalIgnoreCase))
                tc.Problems.Add($"member-force result load case {t5.MemberForceResultLoadCase} does not exist in the connected model.");

            foreach (var cableName in (t5.CableNames ?? []).Distinct(StringComparer.Ordinal))
            {
                if (!currentCables.TryGetValue(cableName, out var currentCable))
                {
                    tc.Problems.Add($"missing cable {cableName}");
                    continue;
                }
                if (savedCables.TryGetValue(cableName, out var savedCable) &&
                    (currentCable.Start != savedCable.PointI || currentCable.End != savedCable.PointJ))
                    tc.Problems.Add($"{cableName}: endpoints changed (saved {savedCable.PointI}–{savedCable.PointJ}, now {currentCable.Start}–{currentCable.End})");
            }
            if (string.IsNullOrWhiteSpace(t5.CableResultLoadCase))
                tc.Problems.Add("cable result load case is required.");
            else if (!modelLoadCases.Contains(t5.CableResultLoadCase, StringComparer.OrdinalIgnoreCase))
                tc.Problems.Add($"cable result load case {t5.CableResultLoadCase} does not exist in the connected model.");

            if (string.IsNullOrWhiteSpace(t5.DeflectionOutputCaseName))
                tc.Problems.Add("deflection output case is required.");
            else if (!modelLoadCases.Contains(t5.DeflectionOutputCaseName, StringComparer.OrdinalIgnoreCase))
                tc.Problems.Add($"deflection output case {t5.DeflectionOutputCaseName} does not exist in the connected model.");
        }

        // ---- Legacy Tab 05 JSON key / UI Tab 05 Iteration Study ----
        if (cfg.Tab05IterationStudy is { } t6)
        {
            var tc = r.Study; tc.Present = true;
            if (cfg.Tab02Loading is null || cfg.Tab03TrussHeight is null || cfg.Tab04CableForce is null)
                tc.Problems.Add("the study requires the Loading, Truss Height and Cable Force sections in the same project.");
            if (!Enum.IsDefined(t6.StudyMode)) tc.Problems.Add("study mode is invalid.");
            if (t6.StudyMode == IterationStudyMode.FixHeight && !ValidCandidateId(t6.FixedHeightCandidateId, 'H'))
                tc.Problems.Add("fixed height candidate ID is invalid.");
            else if (t6.StudyMode == IterationStudyMode.FixHeight && CandidateOrdinal(t6.FixedHeightCandidateId) > HeightCandidateCount(cfg, state))
                tc.Problems.Add($"fixed height candidate {t6.FixedHeightCandidateId} does not exist.");
            if (t6.StudyMode == IterationStudyMode.FixPretension && !ValidCandidateId(t6.FixedPretensionCandidateId, 'P'))
                tc.Problems.Add("fixed pretension candidate ID is invalid.");
            else if (t6.StudyMode == IterationStudyMode.FixPretension && CandidateOrdinal(t6.FixedPretensionCandidateId) > PretensionCandidateCount(cfg, state))
                tc.Problems.Add($"fixed pretension candidate {t6.FixedPretensionCandidateId} does not exist.");
            if (t6.UseCandidateFilters)
            {
                ValidateBounds(t6.MinLoadingKpa, t6.MaxLoadingKpa, "loading", tc.Problems);
                ValidateBounds(t6.MinHeightM, t6.MaxHeightM, "height", tc.Problems);
                ValidateBounds(t6.MinPretensionKn, t6.MaxPretensionKn, "pretension", tc.Problems);
                ValidatePositive(t6.MaxLoadingToHeightRatio, "maximum loading/height ratio", tc.Problems);
                ValidatePositive(t6.MaxPretensionToHeightRatio, "maximum pretension/height ratio", tc.Problems);
            }
            ValidateBounds(t6.DeflectionPassLimitMm, t6.DeflectionFailLimitMm, "absolute deflection", tc.Problems);
            ValidateBounds(t6.MinimumCableForceKn, t6.MaximumCableForceKn, "cable force", tc.Problems);
            if (t6.DeflectionPassLimitMm is double pass && pass < 0) tc.Problems.Add("deflection Pass limit cannot be negative.");
            if (t6.DeflectionFailLimitMm is double fail && fail < 0) tc.Problems.Add("deflection Fail limit cannot be negative.");
            if (!double.IsFinite(t6.DecisionWarningUtilization) || t6.DecisionWarningUtilization <= 0 || t6.DecisionWarningUtilization >= 1)
                tc.Problems.Add("decision warning utilization must be between zero and one.");
            var weights = new[] { t6.DeflectionRankWeight, t6.PretensionRankWeight, t6.HeightRankWeight };
            if (weights.Any(value => !double.IsFinite(value) || value < 0) || weights.Sum() <= 0)
                tc.Problems.Add("decision ranking weights must be finite, nonnegative, and have a positive total.");
            // OutputDirectory is retained for JSON compatibility only. Runtime study paths are always
            // derived from the connected immutable baseline model.
        }

        return r;
    }

    private static void ValidateBounds(double? minimum, double? maximum, string name, List<string> problems)
    {
        if (minimum is double lo && !double.IsFinite(lo)) problems.Add($"minimum {name} filter must be finite.");
        if (maximum is double hi && !double.IsFinite(hi)) problems.Add($"maximum {name} filter must be finite.");
        if (minimum is double min && maximum is double max && double.IsFinite(min) && double.IsFinite(max) && max < min)
            problems.Add($"maximum {name} filter must be greater than or equal to its minimum.");
    }

    private static void ValidatePositive(double? value, string name, List<string> problems)
    {
        if (value is double number && (!double.IsFinite(number) || number <= 0))
            problems.Add($"{name} must be finite and greater than zero.");
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
    private static BigInteger LoadingRowCount(LoadingRowDto row)
    {
        if (!row.IncrementEnabled) return row.FixedQz is double fixedQz && double.IsFinite(fixedQz) && fixedQz >= 0 ? BigInteger.One : BigInteger.Zero;
        if (row.MinQz is not double min || row.MaxQz is not double max || row.StepQz is not double step ||
            !double.IsFinite(min) || !double.IsFinite(max) || !double.IsFinite(step) || min < 0 || max < min || step <= 0) return BigInteger.Zero;
        var intervals = Math.Floor((max - min) / step + 1e-10);
        return !double.IsFinite(intervals) ? new BigInteger(LoadDefinition.MaterializeCeiling) + 1 : new BigInteger(intervals) + 1;
    }

    private static CablePretensionConfig ToRuntimeCable(CableForceRowDto row)
    {
        var (mode, reference) = ResolveSync(row);
        return new CablePretensionConfig
        {
            CableName = row.CableName,
            LoadPattern = row.PretensionLoadCase,
            ApplyRange = row.Mode == CableForceMode.Range,
            TargetForce = row.FixedForce,
            MinForce = row.MinForce,
            MaxForce = row.MaxForce,
            StepForce = row.StepForce,
            SyncMode = mode,
            ReferenceCableName = reference,
            SyncFactor = row.SyncFactor
        };
    }

    private static (CableSyncMode Mode, string? Reference) ResolveSync(CableForceRowDto row)
    {
        if (Enum.IsDefined(row.SyncMode) && row.SyncMode != CableSyncMode.Independent && !string.IsNullOrWhiteSpace(row.ReferenceCableName))
            return (row.SyncMode, row.ReferenceCableName);
        if (!string.IsNullOrWhiteSpace(row.SameStepCableName)) return (CableSyncMode.SameStep, row.SameStepCableName);
        if (!string.IsNullOrWhiteSpace(row.SameAsCableName)) return (CableSyncMode.SameValues, row.SameAsCableName);
        return (Enum.IsDefined(row.SyncMode) ? row.SyncMode : CableSyncMode.Independent, row.ReferenceCableName);
    }

    private static IReadOnlyList<double> ResolvedEndpointForces(CablePretensionConfig cable, IReadOnlyList<CablePretensionConfig> all)
    {
        var chain = new List<CablePretensionConfig>();
        var seen = new HashSet<CablePretensionConfig>();
        var current = cable;
        while (seen.Add(current))
        {
            chain.Add(current);
            if (current.SyncMode == CableSyncMode.Independent) break;
            if (CableSyncGraph.DirectReference(current, all) is not { } next) return [];
            current = next;
        }
        if (chain[^1].SyncMode != CableSyncMode.Independent) return [];
        chain.Reverse();
        var root = chain[0];
        var values = root.ApplyRange
            ? new[] { root.MinForce ?? double.NaN, root.MaxForce ?? double.NaN }
            : new[] { root.TargetForce ?? double.NaN };
        foreach (var follower in chain.Skip(1))
        {
            values = follower.SyncMode switch
            {
                CableSyncMode.SameStep => new[] { follower.MinForce ?? double.NaN, follower.MaxForce ?? double.NaN },
                CableSyncMode.SameRatio => values.Select(v => v * (follower.SyncFactor ?? double.NaN)).ToArray(),
                CableSyncMode.SameOffset => values.Select(v => v + (follower.SyncFactor ?? double.NaN)).ToArray(),
                _ => values
            };
        }
        return values;
    }
    private static BigInteger PretensionCandidateCount(CotVcfConfigDto cfg, ApplicationState state)
    {
        if (cfg.Tab04CableForce is not { } cableConfig) return PretensionCandidates.TotalCount(state.Cables);
        BigInteger total = BigInteger.One;
        if (cableConfig.Cables.Count == 0) return BigInteger.Zero;
        foreach (var cable in cableConfig.Cables)
        {
            // a follower (any non-Independent Sync mode with a reference) adds no independent axis.
            // Tolerate v1.x SameAs / SameStep field names during migration.
            var isFollower =
                (cable.SyncMode != CableSyncMode.Independent && !string.IsNullOrEmpty(cable.ReferenceCableName))
                || !string.IsNullOrEmpty(cable.SameStepCableName)
                || !string.IsNullOrEmpty(cable.SameAsCableName);
            if (isFollower) continue;

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
