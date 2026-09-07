using COT_VCF.Application;
using COT_VCF.Domain;

namespace COT_VCF.Configuration;

/// <summary>
/// Writes a validated <see cref="CotVcfConfigDto"/> back into <see cref="ApplicationState"/> (Tabs 01-05),
/// then lets the existing reactive machinery regenerate candidates and refresh the preview. Callers must
/// only invoke this after schema + model validation have both passed. Nothing here writes to SAP2000.
/// </summary>
public sealed class ConfigurationApplicator(ApplicationState state, WorkflowService workflow)
{
    /// <summary>Apply only the tabs whose checks passed; skip the rest so a partial file still loads.</summary>
    public void Apply(CotVcfConfigDto cfg, ConfigValidationResult check)
    {
        if (check.Loading.Ok && cfg.Tab02Loading is { } l) ApplyLoading(l);
        if (check.Truss.Ok && cfg.Tab03TrussHeight is { } t) ApplyTruss(t);
        if (check.Cable.Ok && cfg.Tab04CableForce is { } c) ApplyCables(c);
        if (check.Output.Ok && cfg.Tab05OutputSettings is { } o) ApplyOutput(o);
        if (check.Study.Ok && cfg.Tab05IterationStudy is { } s) ApplyStudy(s);
    }

    private void ApplyLoading(LoadingConfigDto d)
    {
        state.TributaryWidthB = d.TributaryWidth;
        state.NumberOfStories = d.NumberOfStories;

        state.AppliedPoints.Clear();
        foreach (var name in d.AppliedPoints ?? [])
        {
            if (!state.Geometry.Points.TryGetValue(name, out var p)) continue;
            state.AppliedPoints.Add(new AppliedPoint
            {
                PointName = name,
                X = p.Position.X, Y = p.Position.Y, Z = p.Position.Z,
            });
        }
        workflow.SortAppliedPoints();

        state.LoadDefinitions.Clear();
        foreach (var row in d.LoadDefinitions ?? [])
            state.LoadDefinitions.Add(new LoadDefinition
            {
                LoadName = row.LoadName,
                ApplyIncrementStep = row.IncrementEnabled,
                Qz = row.IncrementEnabled ? 0 : row.FixedQz,
                MinQz = row.MinQz,
                MaxQz = row.MaxQz,
                IncrementStep = row.StepQz,
            });
    }

    private void ApplyTruss(TrussHeightConfigDto d)
    {
        var t = state.Truss;
        t.TopPoints = (d.TopPoints ?? []).ToArray();
        t.MiddlePoints = (d.MiddlePoints ?? []).ToArray();
        t.BottomPoints = (d.BottomPoints ?? []).ToArray();
        t.Reference = d.ReferenceElevation;
        state.BaselineCaptured = false;

        // Baseline height is recomputed from the current (validated) geometry, then the saved ΔH range is
        // reapplied as absolute heights the rest of the app understands.
        try
        {
            workflow.CaptureBaseline();
            t.MinimumHeight = t.BaselineHeight + d.MinDeltaH;
            t.MaximumHeight = t.BaselineHeight + d.MaxDeltaH;
            t.Increment = d.StepDeltaH;
            t.CurrentHeight = t.BaselineHeight;
            state.CommittedHeight = t.BaselineHeight;
            workflow.UpdatePreview();
        }
        catch
        {
            // Groups restored but a level-chord baseline could not be measured; leave the user to
            // Capture Baseline manually. Tabs 01 and 03 are unaffected.
        }
    }

    private void ApplyCables(CableForceConfigDto d)
    {
        state.Cables.Clear();
        foreach (var row in d.Cables ?? [])
            state.Cables.Add(new CablePretensionConfig
            {
                CableName = row.CableName,
                LoadPattern = string.IsNullOrWhiteSpace(row.PretensionLoadCase) ? CablePretensionConfig.PretensionLoadCase : row.PretensionLoadCase,
                ApplyRange = row.Mode == CableForceMode.Range,
                TargetForce = row.Mode == CableForceMode.Fixed ? row.FixedForce : 0,
                MinForce = row.MinForce,
                MaxForce = row.MaxForce,
                StepForce = row.StepForce,
                SameAsCableName = row.SameAsCableName,
                SameStepCableName = row.SameStepCableName,
            });
    }

    private void ApplyOutput(OutputSettingsConfigDto d)
    {
        var output = state.OutputSettings;
        output.DeflectionPoints.Clear();
        foreach (var name in d.DeflectionPointNames ?? []) output.DeflectionPoints.Add(name);

        output.MemberForceGroups.Clear();
        foreach (var row in d.MemberForceGroups ?? [])
        {
            var group = new MemberForceOutputGroup
            {
                Id = string.IsNullOrWhiteSpace(row.Id) ? Guid.NewGuid().ToString("N") : row.Id,
                Name = string.IsNullOrWhiteSpace(row.Name)
                    ? (row.SapGroupName ?? "").Replace(OutputGroupNaming.Prefix, "", StringComparison.OrdinalIgnoreCase)
                    : row.Name
            };
            foreach (var frame in row.FrameNames ?? []) group.Members.Add(frame);
            output.MemberForceGroups.Add(group);
        }

        output.FoundationLeftPoints.Clear();
        foreach (var name in d.FoundationLeftPointNames ?? []) output.FoundationLeftPoints.Add(name);
        output.FoundationRightPoints.Clear();
        foreach (var name in d.FoundationRightPointNames ?? []) output.FoundationRightPoints.Add(name);
        output.CableNames.Clear();
        foreach (var name in d.CableNames ?? []) output.CableNames.Add(name);
        output.CableResultLoadCase = d.CableResultLoadCase;
        output.DeflectionOutputCaseName = d.DeflectionOutputCaseName ?? "";
    }

    private void ApplyStudy(IterationStudyConfigDto d)
    {
        state.Study.Mode = d.StudyMode;
        state.Study.FixedHeightCandidateId = d.FixedHeightCandidateId;
        state.Study.FixedPretensionCandidateId = d.FixedPretensionCandidateId;
        try { state.Study.OutputDirectory = new StudyPathService().GetStudyRoot(state.SessionBaselineModel?.Path ?? state.Model?.Path ?? ""); }
        catch { state.Study.OutputDirectory = ""; }
        state.Study.RestoreExcludedIterationKeys(d.ExcludedIterationKeys);
    }
}
