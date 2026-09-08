using COT_VCF.Domain;
using COT_VCF.Geometry;
using System.IO;

namespace COT_VCF.Application;

public enum StudyValidationState { NotValidated, Valid, ValidWithWarnings, Invalid }

public sealed class StudyValidationService
{
    public IReadOnlyList<ValidationIssue> Validate(ApplicationState state, StudyCompositionResult composition)
    {
        var issues = new List<ValidationIssue>();
        void Error(string section, string message) => issues.Add(new(section, message));
        void Warn(string section, string message) => issues.Add(new(section, message, ValidationSeverity.Warning));

        if (!state.IsSapConnected || state.Model is null) Error("SAP Model", "Connect to a SAP2000 model.");
        if (!state.SessionBaselineCaptured) Error("Baseline Model", "The first-connected baseline snapshot is not available.");
        if (!state.BaselineCaptured) Error("Truss Height", "Capture the baseline height from the original model.");

        ValidateBaselineAndOutput(state, composition, Error, Warn);

        foreach (var issue in new ConfigurationValidator().ValidateLoadingCriteria(state))
            issues.Add(issue with { Section = "Loading Setup" });
        foreach (var load in state.LoadDefinitions.Where(l => !string.IsNullOrWhiteSpace(l.LoadName)))
            if (!state.LoadPatterns.Contains(load.LoadName, StringComparer.Ordinal))
                Error("Loading Setup", $"Load pattern {load.LoadName} does not exist in the connected model.");

        foreach (var issue in new TrussGeometryService().Validate(state.Baseline, state.Truss)) issues.Add(issue);

        foreach (var issue in new OutputSettingsValidator().Validate(state))
            issues.Add(issue);

        if (state.Cables.Count == 0) Error("Pretension Setup", "No cable pretension candidates are available from Tab 3.");
        foreach (var cable in state.Cables)
        {
            if (cable.Missing || !state.Geometry.Members.Any(m => m.Name == cable.CableName && m.Kind == MemberKind.Cable))
                Error("Pretension Setup", $"Cable {cable.CableName} no longer exists in the model.");
            if (cable.CandidateCount <= 0)
                Error("Pretension Setup", $"{cable.CableName} has an invalid candidate definition.");
            if (string.IsNullOrWhiteSpace(cable.LoadPattern) || !state.LoadPatterns.Contains(cable.LoadPattern, StringComparer.OrdinalIgnoreCase))
                Error("Pretension Setup", $"Load pattern {cable.LoadPattern} does not exist in the connected model. Cable target force assignments require a load pattern, not an analysis load case.");
            if (cable.SyncMode != CableSyncMode.Independent)
            {
                var word = cable.SyncMode.Label();
                if (string.IsNullOrEmpty(cable.ReferenceCableName))
                    Error("Pretension Setup", $"{cable.CableName}: '{word}' needs a reference cable.");
                else if (CableSyncGraph.DirectReference(cable, state.Cables) is null)
                    Error("Pretension Setup", $"{cable.CableName}: '{word}' reference cable '{cable.ReferenceCableName}' does not exist.");
                else if (CableSyncGraph.RootOf(cable, state.Cables) is null)
                    Error("Pretension Setup", $"{cable.CableName}: '{word}' reference chain never reaches an Independent cable (broken or circular).");
                else if (cable.SyncMode == CableSyncMode.SameStep
                         && (!cable.ApplyRange || cable.MinForce is not double lo || cable.MaxForce is not double hi
                             || !double.IsFinite(lo) || !double.IsFinite(hi) || hi < lo))
                    Error("Pretension Setup", $"{cable.CableName}: 'Same Step' needs its own Min and Max force (spacing is derived from the reference).");
                else if (cable.CanEditFactor
                         && (cable.SyncFactor is not double f || !double.IsFinite(f)
                             || (cable.SyncMode == CableSyncMode.SameRatio && f <= 0)))
                    Error("Pretension Setup", cable.SyncMode == CableSyncMode.SameRatio
                        ? $"{cable.CableName}: Same Ratio needs a ratio greater than zero."
                        : $"{cable.CableName}: Same Offset needs an offset value.");
            }
            else if (PretensionCandidates.FollowersOf(cable, state.Cables) is { Count: > 0 } deps
                     && cable.CandidateCount <= 0)
                Error("Pretension Setup", $"{cable.CableName} is a Sync reference for {string.Join(", ", deps.Select(d => d.CableName))} but has no valid candidates.");
        }
        if (PretensionCandidates.FindCycle(state.Cables) is { Count: > 0 } cycle)
            Error("Pretension Setup", $"Circular cable Sync reference: {string.Join(" → ", cycle)} → {cycle[0]}. Set one cable back to Independent.");

        if (state.Study.Mode == IterationStudyMode.FixHeight &&
            !composition.HeightCandidates.Any(c => c.Id == state.Study.FixedHeightCandidateId))
            Error("Fixed Parameter", "Select one valid height candidate from Tab 2.");
        if (state.Study.Mode == IterationStudyMode.FixPretension &&
            !composition.PretensionCandidates.Any(c => c.Id == state.Study.FixedPretensionCandidateId))
            Error("Fixed Parameter", "Select one valid pretension candidate from Tab 3.");

        if (composition.PreviewLimitExceeded)
            Error("Study Preview", $"The study exceeds the {StudyCompositionService.MaxPreviewRows:N0}-row preview limit. Reduce the candidate space.");
        else if (composition.Iterations.Count == 0)
            Error("Study Preview", "No study iterations were generated.");

        if (composition.Iterations.Count(i => i.RunIterate) == 0)
            Error("Study Preview", "Select at least one iteration to run.");
        if (composition.Iterations.Select(i => i.SapModelName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != composition.Iterations.Count)
            Error("Study Preview", "Generated SAP model names are not unique.");
        if (composition.Iterations.Select(i => i.FullOutputPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != composition.Iterations.Count)
            Error("Study Preview", "Generated output paths are not unique.");
        if (composition.Iterations.Any(i => !i.IsValid))
            Error("Study Preview", "One or more generated output paths are invalid.");

        return issues.Distinct().ToArray();
    }

    private static void ValidateBaselineAndOutput(
        ApplicationState state,
        StudyCompositionResult composition,
        Action<string, string> error,
        Action<string, string> warn)
    {
        if (state.SessionBaselineModel is not { Path.Length: > 0 } baseline)
        {
            error("Baseline Model", "Save and reconnect the baseline SAP2000 model before building a study.");
            return;
        }
        try
        {
            var baselinePath = Path.GetFullPath(baseline.Path);
            if (!Path.IsPathFullyQualified(baselinePath) || !File.Exists(baselinePath))
                error("Baseline Model", "The immutable baseline .sdb file does not exist at its connected path.");
            if (!string.Equals(baseline.ForceUnit, "kN", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(baseline.LengthUnit, "m", StringComparison.OrdinalIgnoreCase))
                error("Baseline Model", $"Study candidates use kN and m. Set SAP2000 present units to kN / m before reconnecting (current: {baseline.Units}).");
            _ = new StudyPathService().GetStudyRoot(baselinePath);

            var reused = 0;
            foreach (var row in composition.Iterations.Where(row => row.RunIterate))
            {
                if (File.Exists(row.ModelFullPath))
                    reused++;
                else if (Directory.Exists(row.IterationDirectory) && Directory.EnumerateFileSystemEntries(row.IterationDirectory).Any())
                    warn("Existing Output", $"{row.IterationId} has a non-empty iteration folder but no child model — it will be rebuilt into that folder.");
            }
            if (reused > 0)
                warn("Existing Output",
                    $"{reused} selected candidate{(reused == 1 ? "" : "s")} already ha{(reused == 1 ? "s" : "ve")} a child model — " +
                    "the existing analysis is reused (opened, verified, re-extracted), never overwritten. " +
                    "Delete an iteration folder to force a rebuild.");
        }
        catch (Exception)
        {
            error("Output Folder", "The study folder derived from the baseline model path is not valid or accessible.");
        }
    }
}
