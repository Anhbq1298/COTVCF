using COT_VCF.Domain;
using COT_VCF.Geometry;

namespace COT_VCF.Application;

public sealed class WorkflowService(ISapGateway sap, ApplicationState state)
{
    private readonly TrussGeometryService truss = new();
    public async Task ConnectAsync()
    {
        var info = await sap.ConnectAsync();
        if (state.Model is { } old && (old.Path != info.Path || old.UnitsCode != info.UnitsCode) && (state.AppliedPoints.Count > 0 || state.PointLoads.Count > 0 || state.Cables.Count > 0 || state.BaselineCaptured))
            throw new InvalidOperationException("The model or units differ from this session. Close and reopen COT VCF tools to begin a new configuration; the current setup has been retained.");
        if (state.Model is { } previous && (previous.Path != info.Path || previous.UnitsCode != info.UnitsCode))
        {
            state.Geometry = GeometryModel.Empty; state.Baseline = GeometryModel.Empty; state.Preview = GeometryModel.Empty; state.LoadPatterns.Clear();
        }
        state.Model = info; state.Connection = "Connected";
    }
    public async Task RefreshAsync()
    {
        var snapshot = await sap.ReadAsync();
        if (state.Model is { } old && (old.Path != snapshot.Info.Path || old.UnitsCode != snapshot.Info.UnitsCode)) throw new InvalidOperationException("The model or its units changed. Restore the original model and units, or restart COT VCF tools for a new session.");
        state.Model = snapshot.Info;
        bool changed = state.BaselineCaptured && (snapshot.Geometry.Points.Count != state.Geometry.Points.Count || snapshot.Geometry.Points.Any(p => !state.Geometry.Points.TryGetValue(p.Key, out var prior) || prior.Position != p.Value.Position) || !snapshot.Geometry.Members.SequenceEqual(state.Geometry.Members));
        state.Geometry = snapshot.Geometry;
        state.LoadPatterns.Clear(); foreach (var pattern in snapshot.LoadPatterns) state.LoadPatterns.Add(pattern);
        if (!state.BaselineCaptured) { state.Baseline = state.Geometry; state.Preview = state.Geometry; }
        else if (changed) { state.BaselineCaptured = false; state.Preview = state.Geometry; }
        foreach (var row in state.PointLoads)
            row.Status = !state.Geometry.Points.ContainsKey(row.PointName) || !state.LoadPatterns.Contains(row.LoadPattern) ? "Missing in SAP" : "Modified";
        foreach (var point in state.AppliedPoints)
        {
            if (state.Geometry.Points.TryGetValue(point.PointName, out var current))
            {
                point.X = current.Position.X; point.Y = current.Position.Y; point.Z = current.Position.Z; point.Status = "Selected";
            }
            else point.Status = "Missing in SAP";
        }
        SortAppliedPoints();
        // Tab 4 keeps its candidate definitions; only flag cables that are no longer in the model.
        var cableNames = state.Geometry.Members.Where(m => m.Kind == MemberKind.Cable).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var cable in state.Cables) cable.Missing = !cableNames.Contains(cable.CableName);
        if (changed) throw new InvalidOperationException("SAP geometry changed. Selections and ranges were retained. Review them and capture a new baseline before applying geometry.");
    }
    public int AddAppliedPoints(IReadOnlyList<string> pointNames)
    {
        int added = 0;
        foreach (var name in pointNames.Distinct(StringComparer.Ordinal))
        {
            if (!state.Geometry.Points.TryGetValue(name, out var point)) continue;
            var existing = state.AppliedPoints.FirstOrDefault(item => item.PointName == name);
            if (existing is not null)
            {
                existing.X = point.Position.X;
                existing.Y = point.Position.Y;
                existing.Z = point.Position.Z;
                existing.Status = "Selected";
                continue;
            }
            state.AppliedPoints.Add(new()
            {
                PointName = name,
                X = point.Position.X,
                Y = point.Position.Y,
                Z = point.Position.Z
            });
            added++;
        }
        SortAppliedPoints();
        return added;
    }
    public void SortAppliedPoints()
    {
        var sorted = state.AppliedPoints.OrderBy(p => p.X).ThenBy(p => p.PointName, StringComparer.Ordinal).ToArray();
        for (int target = 0; target < sorted.Length; target++)
        {
            var current = state.AppliedPoints.IndexOf(sorted[target]);
            if (current != target) state.AppliedPoints.Move(current, target);
            sorted[target].Order = target + 1;
        }
    }
    public void CaptureBaseline()
    {
        state.BaselineCaptured = false;
        var t = state.Truss;
        var names = t.TopPoints.Concat(t.MiddlePoints).Concat(t.BottomPoints).ToArray();
        if (t.MiddlePoints.Count == 0 || names.Length != names.Distinct().Count()) throw new InvalidOperationException("Select three non-overlapping point groups first.");
        if (names.Any(n => !state.Geometry.Points.ContainsKey(n))) throw new InvalidOperationException("Selected points are missing; refresh and review groups.");
        var height = truss.MeasureHeight(state.Geometry, t);
        state.Baseline = state.Geometry;
        t.BaselineHeight = height; t.MinimumHeight = height; t.MaximumHeight = height * 1.5; t.Increment = height / 20; t.CurrentHeight = height;
        state.CommittedHeight = height; state.BaselineCaptured = true; state.Preview = state.Geometry;
    }
    public void UpdatePreview()
    {
        if (state.BaselineCaptured) state.Preview = truss.Preview(state.Baseline, state.Truss);
    }
    public async Task ApplyGeometryAsync()
    {
        if (!state.BaselineCaptured) throw new InvalidOperationException("Capture the baseline first.");
        UpdatePreview();
        await sap.MovePointsAsync(state.Geometry, state.Preview, state.Model!);
        state.Geometry = state.Preview; state.CommittedHeight = state.Truss.CurrentHeight;
    }
    public async Task ApplyLoadsAsync()
    {
        var issues = new ConfigurationValidator().ValidateLoads(state);
        if (issues.Count > 0) throw new InvalidOperationException(string.Join("\n", issues.Select(i => i.Message)));
        var rows = state.PointLoads.ToArray();
        try { await sap.ApplyLoadsAsync(rows.Select(r => r.ToLoad()).ToArray(), state.Model!); foreach (var row in rows) row.Status = "Applied"; }
        catch { foreach (var row in rows) row.Status = "Error — review SAP"; throw; }
    }
}
