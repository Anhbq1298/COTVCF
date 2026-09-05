using COT_VCF.Domain;

namespace COT_VCF.Configuration;

/// <summary>
/// Turns the current <see cref="ApplicationState"/> (Tabs 02–04) into a <see cref="CotVcfConfigDto"/>.
/// Read-only: it never touches SAP2000. The model snapshot carries only the points / cables / load
/// patterns actually referenced by the configuration, taken from the immutable baseline geometry.
/// </summary>
public sealed class ConfigurationExtractor
{
    public CotVcfConfigDto Extract(ApplicationState state, IReadOnlyCollection<string> modelLoadCases)
    {
        var tab02 = ExtractLoading(state);
        var tab03 = ExtractTruss(state);
        var tab04 = ExtractCables(state);
        return new CotVcfConfigDto
        {
            ConfigVersion = "1.0",
            Units = new UnitsDto(),
            Model = Snapshot(state, tab02, tab03, tab04, modelLoadCases),
            Tab02Loading = tab02,
            Tab03TrussHeight = tab03,
            Tab04CableForce = tab04,
        };
    }

    private static LoadingConfigDto ExtractLoading(ApplicationState s) => new()
    {
        TributaryWidth = s.TributaryWidthB,
        NumberOfStories = s.NumberOfStories,
        AppliedPoints = s.AppliedPoints.OrderBy(p => p.Order).Select(p => p.PointName).ToList(),
        LoadDefinitions = s.LoadDefinitions.Select(r => new LoadingRowDto
        {
            LoadName = r.LoadName,
            IncrementEnabled = r.ApplyIncrementStep,
            FixedQz = r.ApplyIncrementStep ? null : r.Qz,
            MinQz = r.ApplyIncrementStep ? r.MinQz : null,
            MaxQz = r.ApplyIncrementStep ? r.MaxQz : null,
            StepQz = r.ApplyIncrementStep ? r.IncrementStep : null,
        }).ToList(),
    };

    private static TrussHeightConfigDto ExtractTruss(ApplicationState s)
    {
        var t = s.Truss;
        var h0 = s.BaselineCaptured ? t.BaselineHeight : 0;
        return new TrussHeightConfigDto
        {
            TopPoints = t.TopPoints.ToList(),
            MiddlePoints = t.MiddlePoints.ToList(),
            BottomPoints = t.BottomPoints.ToList(),
            ReferenceElevation = t.Reference,
            MinDeltaH = t.MinimumHeight - h0,
            MaxDeltaH = t.MaximumHeight - h0,
            StepDeltaH = t.Increment,
        };
    }

    private static CableForceConfigDto ExtractCables(ApplicationState s) => new()
    {
        Cables = s.Cables.Select(c => new CableForceRowDto
        {
            CableName = c.CableName,
            PretensionLoadCase = c.LoadCase,
            Mode = c.ApplyRange ? CableForceMode.Range : CableForceMode.Fixed,
            FixedForce = c.ApplyRange ? null : c.TargetForce,
            MinForce = c.ApplyRange ? c.MinForce : null,
            MaxForce = c.ApplyRange ? c.MaxForce : null,
            StepForce = c.ApplyRange ? c.StepForce : null,
        }).ToList(),
    };

    private static ModelSnapshotDto Snapshot(ApplicationState s, LoadingConfigDto t02, TrussHeightConfigDto t03, CableForceConfigDto t04, IReadOnlyCollection<string> modelLoadCases)
    {
        // Baseline geometry = original coordinates captured at connect time (Tab 03 points must use
        // baseline, and it is the safe original source for every dependency).
        var geom = s.Baseline.Points.Count > 0 ? s.Baseline : s.Geometry;

        var pointNames = new HashSet<string>(StringComparer.Ordinal);
        pointNames.UnionWith(t02.AppliedPoints);
        pointNames.UnionWith(t03.TopPoints);
        pointNames.UnionWith(t03.MiddlePoints);
        pointNames.UnionWith(t03.BottomPoints);

        var cableNames = new HashSet<string>(t04.Cables.Select(c => c.CableName), StringComparer.Ordinal);
        var members = geom.Members.Where(m => m.Kind == MemberKind.Cable && cableNames.Contains(m.Name)).ToArray();
        foreach (var m in members) { pointNames.Add(m.Start); pointNames.Add(m.End); }

        CoordinateDto Coord(string name) => geom.Points.TryGetValue(name, out var p)
            ? new CoordinateDto { X = p.Position.X, Y = p.Position.Y, Z = p.Position.Z }
            : new CoordinateDto();

        var patterns = new HashSet<string>(t02.LoadDefinitions.Select(r => r.LoadName), StringComparer.Ordinal);
        patterns.IntersectWith(s.LoadPatterns);
        var cases = new HashSet<string>(t04.Cables.Select(c => c.PretensionLoadCase).Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.Ordinal);

        return new ModelSnapshotDto
        {
            FileName = s.Model is { } info ? System.IO.Path.GetFileName(info.Path) : "",
            Points = pointNames.Where(geom.Points.ContainsKey).OrderBy(n => n, StringComparer.Ordinal)
                .Select(n => { var c = Coord(n); return new PointSnapshotDto { Name = n, X = c.X, Y = c.Y, Z = c.Z }; }).ToList(),
            Cables = members.OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => new CableSnapshotDto
            {
                Name = m.Name, PointI = m.Start, PointJ = m.End,
                PointICoordinate = Coord(m.Start), PointJCoordinate = Coord(m.End),
            }).ToList(),
            LoadPatterns = patterns.OrderBy(n => n, StringComparer.Ordinal).ToList(),
            LoadCases = cases.OrderBy(n => n, StringComparer.Ordinal).ToList(),
        };
    }
}
