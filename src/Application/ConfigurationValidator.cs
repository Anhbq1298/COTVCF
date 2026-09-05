using COT_VCF.Domain;
using COT_VCF.Geometry;
namespace COT_VCF.Application;

public sealed class ConfigurationValidator
{
    public IReadOnlyList<ValidationIssue> Validate(ApplicationState state)
    {
        var issues = new List<ValidationIssue>();
        if (!state.IsSapConnected) issues.Add(new("SAP model", "Connect to SAP2000."));
        if (state.Geometry.Points.Count == 0) issues.Add(new("SAP model", "Retrieve the model geometry."));
        issues.AddRange(ValidateLoads(state));
        issues.AddRange(new TrussGeometryService().Validate(state.Baseline, state.Truss));
        if (!state.BaselineCaptured) issues.Add(new("Truss height", "Capture the baseline geometry."));
        if (state.Cables.Count == 0) issues.Add(new("Cable force", "Select at least one cable or tendon force source."));
        foreach (var c in state.Cables)
        {
            if (!state.Geometry.Members.Any(m => m.Name == c.ObjectName && m.Kind == c.Kind)) issues.Add(new("Cable force", $"{c.ObjectName} no longer exists."));
            if (c.CurrentForce is not double f || !double.IsFinite(f) || !string.IsNullOrEmpty(c.ReadProblem)) issues.Add(new("Cable force", $"{c.ObjectName}: force source could not be read. {c.ReadProblem}"));
            else if (!TrussGeometryService.ValidRange(c.MinimumForce, c.MaximumForce, c.Increment) || f < c.MinimumForce || f > c.MaximumForce || !TrussGeometryService.OnGrid(c.TargetForce, c.MinimumForce, c.MaximumForce, c.Increment)) issues.Add(new("Cable force", $"{c.ObjectName}: check range, current force and target increment."));
        }
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
        if (state.PointLoads.GroupBy(r => (r.PointName, r.LoadPattern)).Any(g => g.Count() > 1)) issues.Add(new("Point loading", "Duplicate point / load-pattern rows are not allowed."));
        return issues;
    }
}
