using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
namespace Mcotis.Desktop.Application;

public sealed class ConfigurationValidator
{
    public IReadOnlyList<ValidationIssue> Validate(ApplicationState state)
    {
        var issues = new List<ValidationIssue>();
        if (!state.IsSapConnected) issues.Add(new("SAP model", "Connect to SAP2000."));
        if (state.Geometry.Points.Count == 0) issues.Add(new("SAP model", "Retrieve the model geometry."));
        issues.AddRange(ValidateLoads(state));
        issues.AddRange(ValidateLoadingCriteria(state));
        issues.AddRange(new TrussGeometryService().Validate(state.Baseline, state.Truss));
        if (!state.BaselineCaptured) issues.Add(new("Truss height", "Capture the baseline geometry."));
        if (state.Cables.Count == 0) issues.Add(new("Cable pretension", "Add at least one cable."));
        var cableList = state.Cables.ToArray();
        foreach (var c in state.Cables)
        {
            if (!state.Geometry.Members.Any(m => m.Name == c.CableName && m.Kind == MemberKind.Cable))
                issues.Add(new("Cable pretension", $"{c.CableName} is no longer a cable object in the model."));
            else if (c.CandidateCount <= 0)
                issues.Add(new("Cable pretension", $"{c.CableName}: {(c.ApplyRange ? "review Min / Max / Step force." : "enter a target force.")}"));

            if (c.SyncMode != CableSyncMode.Independent)
            {
                var word = c.SyncMode.Label();
                if (string.IsNullOrEmpty(c.ReferenceCableName))
                    issues.Add(new("Cable pretension", $"{c.CableName}: '{word}' has no reference cable.", ValidationSeverity.Warning));
                else if (CableSyncGraph.DirectReference(c, cableList) is null)
                    issues.Add(new("Cable pretension", $"{c.CableName}: '{word}' reference {c.ReferenceCableName} does not exist.", ValidationSeverity.Warning));
                else if (CableSyncGraph.RootOf(c, cableList) is null)
                    issues.Add(new("Cable pretension", $"{c.CableName}: '{word}' reference chain is broken or circular. Treated as independent.", ValidationSeverity.Warning));
                else if (c.SyncMode == CableSyncMode.SameStep && c.CandidateCount <= 0)
                    issues.Add(new("Cable pretension", $"{c.CableName}: 'Same Step' needs its own Min and Max force.", ValidationSeverity.Warning));
                else if (c.SyncMode is CableSyncMode.SameRatio or CableSyncMode.SameOffset
                         && (c.SyncFactor is not double f || !double.IsFinite(f) || (c.SyncMode == CableSyncMode.SameRatio && f <= 0)))
                    issues.Add(new("Cable pretension", $"{c.CableName}: '{word}' needs a {(c.SyncMode == CableSyncMode.SameRatio ? "ratio greater than zero" : "numeric offset")}.", ValidationSeverity.Warning));
            }
        }
        if (PretensionCandidates.FindCycle(cableList) is { Count: > 0 } cycle)
            issues.Add(new("Cable pretension", $"Circular Sync reference: {string.Join(" → ", cycle)} → {cycle[0]}. Set one cable to Independent.", ValidationSeverity.Warning));
        return issues;
    }
    public IReadOnlyList<ValidationIssue> ValidateLoads(ApplicationState state)
    {
        var issues = new List<ValidationIssue>();
        foreach (var row in state.PointLoads)
        {
            if (!state.Geometry.Points.ContainsKey(row.PointName)) issues.Add(new("Point loading", $"Point {row.PointName} is missing."));
            if (!state.LoadPatterns.Contains(row.LoadPattern)) issues.Add(new("Point loading", $"Select a valid load pattern for {row.PointName}."));
            if (!double.IsFinite(row.Fx) || !double.IsFinite(row.Fy) || !double.IsFinite(row.Fz)) issues.Add(new("Point loading", $"{row.PointName}: forces must be finite numbers."));
        }
        if (state.PointLoads.GroupBy(row => (row.PointName, row.LoadPattern)).Any(group => group.Count() > 1)) issues.Add(new("Point loading", "Duplicate point / load-pattern rows are not allowed."));
        return issues;
    }
    public IReadOnlyList<ValidationIssue> ValidateLoadingCriteria(ApplicationState state)
    {
        var issues = new List<ValidationIssue>();
        foreach (var point in state.AppliedPoints)
            if (!state.Geometry.Points.ContainsKey(point.PointName)) issues.Add(new("Loading criteria", $"Applied point {point.PointName} is missing."));
        if (state.AppliedPoints.Count == 0) issues.Add(new("Loading criteria", "Select at least one applied point."));
        if (state.AppliedPoints.GroupBy(point => point.PointName).Any(group => group.Count() > 1)) issues.Add(new("Loading criteria", "Duplicate applied points are not allowed."));
        if (!double.IsFinite(state.TributaryWidthB) || state.TributaryWidthB <= 0) issues.Add(new("Loading criteria", "Out-of-plane tributary width B must be greater than zero."));
        if (!double.IsFinite(state.NumberOfStories) || state.NumberOfStories < 1 || state.NumberOfStories > int.MaxValue || state.NumberOfStories != Math.Truncate(state.NumberOfStories)) issues.Add(new("Loading criteria", "NStories must be a whole number greater than or equal to one."));
        if (state.LoadDefinitions.Count == 0) issues.Add(new("Loading criteria", "Add at least one load definition."));
        foreach (var row in state.LoadDefinitions)
        {
            if (string.IsNullOrWhiteSpace(row.LoadName) || !state.LoadPatterns.Contains(row.LoadName, StringComparer.Ordinal))
                issues.Add(new("Loading criteria", $"Select an existing SAP load pattern for {Name(row.LoadName)}."));
            if (!row.ApplyIncrementStep && (row.Qz is not double qz || !double.IsFinite(qz) || qz < 0))
                issues.Add(new("Loading criteria", $"{row.LoadName}: qz must be zero or greater."));
            if (row.ApplyIncrementStep && (row.MinQz is not double min || !double.IsFinite(min) || min < 0 || row.MaxQz is not double max || !double.IsFinite(max) || max < min || row.IncrementStep is not double step || !double.IsFinite(step) || step <= 0))
                issues.Add(new("Loading criteria", $"{row.LoadName}: review minimum, maximum and increment step."));
            else if (row.CandidateCount > LoadDefinition.MaterializeCeiling)
                issues.Add(new("Loading criteria", $"{row.LoadName}: {row.CandidateCount:N0} values exceed the {LoadDefinition.MaterializeCeiling:N0} limit. Widen the increment step."));
        }
        if (state.LoadDefinitions.Where(row => !string.IsNullOrWhiteSpace(row.LoadName))
            .GroupBy(row => row.LoadName, StringComparer.Ordinal).Any(group => group.Count() > 1))
            issues.Add(new("Loading criteria", "Each SAP load pattern can appear only once."));
        return issues;
    }

    private static string Name(string? value) => string.IsNullOrWhiteSpace(value) ? "this row" : value;
}
