using COT_VCF.Application;
using COT_VCF.Domain;

namespace COT_VCF.Configuration;

/// <summary>
/// Writes a validated <see cref="CotVcfConfigDto"/> back into <see cref="ApplicationState"/> (Tabs 02–04),
/// then lets the existing reactive machinery regenerate candidates and refresh the preview. Callers must
/// only invoke this after schema + model validation have both passed. Nothing here writes to SAP2000.
/// </summary>
public sealed class ConfigurationApplicator(ApplicationState state, WorkflowService workflow)
{
    public void Apply(CotVcfConfigDto cfg)
    {
        ApplyLoading(cfg.Tab02Loading);
        ApplyTruss(cfg.Tab03TrussHeight);
        ApplyCables(cfg.Tab04CableForce);
    }

    private void ApplyLoading(LoadingConfigDto d)
    {
        state.TributaryWidthB = d.TributaryWidth;
        state.NumberOfStories = d.NumberOfStories;

        state.AppliedPoints.Clear();
        foreach (var name in d.AppliedPoints)
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
        foreach (var row in d.LoadDefinitions)
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
        t.TopPoints = d.TopPoints.ToArray();
        t.MiddlePoints = d.MiddlePoints.ToArray();
        t.BottomPoints = d.BottomPoints.ToArray();
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
            // Capture Baseline manually. Tabs 02 and 04 are unaffected.
        }
    }

    private void ApplyCables(CableForceConfigDto d)
    {
        state.Cables.Clear();
        foreach (var row in d.Cables)
            state.Cables.Add(new CablePretensionConfig
            {
                CableName = row.CableName,
                ApplyRange = row.Mode == CableForceMode.Range,
                TargetForce = row.Mode == CableForceMode.Fixed ? row.FixedForce : 0,
                MinForce = row.MinForce,
                MaxForce = row.MaxForce,
                StepForce = row.StepForce,
            });
    }
}
