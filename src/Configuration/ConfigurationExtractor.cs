using COT_VCF.Domain;
using COT_VCF.Application;

namespace COT_VCF.Configuration;

/// <summary>
/// Turns the current <see cref="ApplicationState"/> into a <see cref="CotVcfConfigDto"/> for the requested
/// tabs only. Read-only: it never touches SAP2000. The model snapshot carries just the points / cables /
/// load patterns referenced by the included tabs, taken from the immutable baseline geometry.
/// </summary>
public sealed class ConfigurationExtractor
{
    public CotVcfConfigDto Extract(ApplicationState state, IReadOnlyCollection<string> modelLoadCases, ConfigTabs tabs)
    {
        var t02 = tabs.HasFlag(ConfigTabs.Loading) ? ExtractLoading(state) : null;
        var t03 = tabs.HasFlag(ConfigTabs.Truss) ? ExtractTruss(state) : null;
        var t04 = tabs.HasFlag(ConfigTabs.Cable) ? ExtractCables(state) : null;
        var t05 = tabs.HasFlag(ConfigTabs.Output) ? ExtractOutput(state) : null;
        var t06 = tabs.HasFlag(ConfigTabs.Study) ? ExtractStudy(state) : null;
        return new CotVcfConfigDto
        {
            ConfigVersion = "1.0",
            Units = new UnitsDto(),
            Model = Snapshot(state, t02, t03, t04, t05, modelLoadCases, null),
            Tab02Loading = t02,
            Tab03TrussHeight = t03,
            Tab04CableForce = t04,
            Tab05OutputSettings = t05,
            Tab05IterationStudy = t06,
        };
    }

    /// <summary>Extract only one tab's section from the current UI state.</summary>
    public LoadingConfigDto ExtractLoadingSection(ApplicationState state) => ExtractLoading(state);
    public TrussHeightConfigDto ExtractTrussSection(ApplicationState state) => ExtractTruss(state);
    public CableForceConfigDto ExtractCableSection(ApplicationState state) => ExtractCables(state);
    public OutputSettingsConfigDto ExtractOutputSection(ApplicationState state) => ExtractOutput(state);
    public IterationStudyConfigDto ExtractStudySection(ApplicationState state) => ExtractStudy(state);

    /// <summary>Recompute a project's model snapshot from whichever tab sections it currently holds,
    /// preserving <see cref="ModelSnapshotDto.OriginalFileName"/>.</summary>
    public ModelSnapshotDto RebuildSnapshot(CotVcfConfigDto project, ApplicationState state, IReadOnlyCollection<string> modelLoadCases) =>
        Snapshot(state, project.Tab02Loading, project.Tab03TrussHeight, project.Tab04CableForce, project.Tab05OutputSettings, modelLoadCases, project.Model);

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
            PretensionLoadCase = c.LoadPattern,
            Mode = c.ApplyRange ? CableForceMode.Range : CableForceMode.Fixed,
            FixedForce = c.ApplyRange ? null : c.TargetForce,
            MinForce = c.ApplyRange ? c.MinForce : null,
            MaxForce = c.ApplyRange ? c.MaxForce : null,
            StepForce = c.ApplyRange ? c.StepForce : null,
            SyncMode = c.SyncMode,
            ReferenceCableName = c.ReferenceCableName,
            SyncFactor = c.SyncFactor,
        }).ToList(),
    };

    private static OutputSettingsConfigDto ExtractOutput(ApplicationState s) => new()
    {
        OutputGroupPrefix = OutputGroupNaming.Prefix,
        DeflectionPointNames = s.OutputSettings.DeflectionPoints.ToList(),
        MemberForceGroups = s.OutputSettings.MemberForceGroups.Select(group => new MemberForceGroupConfigDto
        {
            Id = group.Id,
            Name = group.Name,
            SapGroupName = group.SapGroupName,
            FrameNames = group.Members.ToList(),
        }).ToList(),
        FoundationLeftPointNames = s.OutputSettings.FoundationLeftPoints.ToList(),
        FoundationRightPointNames = s.OutputSettings.FoundationRightPoints.ToList(),
        CableNames = s.OutputSettings.CableNames.ToList(),
        CableResultLoadCase = s.OutputSettings.CableResultLoadCase,
        DeflectionOutputCaseName = s.OutputSettings.DeflectionOutputCaseName,
    };

    private static IterationStudyConfigDto ExtractStudy(ApplicationState s) => new()
    {
        StudyMode = s.Study.Mode,
        FixedHeightCandidateId = s.Study.FixedHeightCandidateId,
        FixedPretensionCandidateId = s.Study.FixedPretensionCandidateId,
        OutputDirectory = DerivedStudyRoot(s),
        ExcludedIterationKeys = s.Study.ExcludedIterationKeys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
        UseCandidateFilters = s.Study.UseCandidateFilters,
        MinLoadingKpa = s.Study.MinLoadingKpa,
        MaxLoadingKpa = s.Study.MaxLoadingKpa,
        MinHeightM = s.Study.MinHeightM,
        MaxHeightM = s.Study.MaxHeightM,
        MinPretensionKn = s.Study.MinPretensionKn,
        MaxPretensionKn = s.Study.MaxPretensionKn,
        MaxLoadingToHeightRatio = s.Study.MaxLoadingToHeightRatio,
        MaxPretensionToHeightRatio = s.Study.MaxPretensionToHeightRatio,
        DeflectionPassLimitMm = s.Study.DeflectionPassLimitMm,
        DeflectionFailLimitMm = s.Study.DeflectionFailLimitMm,
        MinimumCableForceKn = s.Study.MinimumCableForceKn,
        MaximumCableForceKn = s.Study.MaximumCableForceKn,
        DecisionWarningUtilization = s.Study.DecisionWarningUtilization,
        DeflectionRankWeight = s.Study.DeflectionRankWeight,
        PretensionRankWeight = s.Study.PretensionRankWeight,
        HeightRankWeight = s.Study.HeightRankWeight,
    };

    private static string DerivedStudyRoot(ApplicationState state)
    {
        try { return new StudyPathService().GetStudyRoot(state.SessionBaselineModel?.Path ?? state.Model?.Path ?? ""); }
        catch { return ""; }
    }

    private static ModelSnapshotDto Snapshot(ApplicationState s, LoadingConfigDto? t02, TrussHeightConfigDto? t03, CableForceConfigDto? t04, OutputSettingsConfigDto? t05, IReadOnlyCollection<string> modelLoadCases, ModelSnapshotDto? previous)
    {
        // Baseline geometry = original coordinates captured at connect time (Tab 02 must use baseline;
        // it is also the safe original source for every other dependency).
        var geom = s.SessionBaseline.Points.Count > 0 ? s.SessionBaseline
            : s.Baseline.Points.Count > 0 ? s.Baseline : s.Geometry;

        var pointNames = new HashSet<string>(StringComparer.Ordinal);
        if (t02 is not null) pointNames.UnionWith(t02.AppliedPoints);
        if (t03 is not null) { pointNames.UnionWith(t03.TopPoints); pointNames.UnionWith(t03.MiddlePoints); pointNames.UnionWith(t03.BottomPoints); }

        var cableNames = new HashSet<string>(t04?.Cables.Select(c => c.CableName) ?? [], StringComparer.Ordinal);
        cableNames.UnionWith(t05?.CableNames ?? []);
        var cableMembers = geom.Members.Where(m => m.Kind == MemberKind.Cable && cableNames.Contains(m.Name)).ToArray();
        foreach (var m in cableMembers) { pointNames.Add(m.Start); pointNames.Add(m.End); }

        if (t05 is not null)
        {
            pointNames.UnionWith(t05.DeflectionPointNames ?? []);
            pointNames.UnionWith(t05.FoundationLeftPointNames ?? []);
            pointNames.UnionWith(t05.FoundationRightPointNames ?? []);
        }
        var frameNames = new HashSet<string>(t05?.MemberForceGroups.SelectMany(group => group.FrameNames) ?? [], StringComparer.Ordinal);
        var frameMembers = geom.Members.Where(m => m.Kind == MemberKind.Frame && frameNames.Contains(m.Name)).ToArray();
        foreach (var m in frameMembers) { pointNames.Add(m.Start); pointNames.Add(m.End); }

        CoordinateDto Coord(string name) => geom.Points.TryGetValue(name, out var p)
            ? new CoordinateDto { X = p.Position.X, Y = p.Position.Y, Z = p.Position.Z }
            : new CoordinateDto();

        var patterns = new HashSet<string>(t02?.LoadDefinitions.Select(r => r.LoadName) ?? [], StringComparer.Ordinal);
        patterns.UnionWith((t04?.Cables.Select(c => c.PretensionLoadCase) ?? []).Where(n => !string.IsNullOrWhiteSpace(n)));
        patterns.IntersectWith(s.LoadPatterns);
        var cases = new HashSet<string>(modelLoadCases, StringComparer.Ordinal);

        var currentFile = s.Model is { } info ? System.IO.Path.GetFileName(info.Path) : "";
        return new ModelSnapshotDto
        {
            OriginalFileName = string.IsNullOrWhiteSpace(previous?.OriginalFileName) ? currentFile : previous!.OriginalFileName,
            FileName = currentFile,
            Points = pointNames.Where(geom.Points.ContainsKey).OrderBy(n => n, StringComparer.Ordinal)
                .Select(n => { var c = Coord(n); return new PointSnapshotDto { Name = n, X = c.X, Y = c.Y, Z = c.Z }; }).ToList(),
            Cables = cableMembers.OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => new CableSnapshotDto
            {
                Name = m.Name, PointI = m.Start, PointJ = m.End,
                PointICoordinate = Coord(m.Start), PointJCoordinate = Coord(m.End),
            }).ToList(),
            Frames = frameMembers.OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => new FrameSnapshotDto
            {
                Name = m.Name, PointI = m.Start, PointJ = m.End,
                PointICoordinate = Coord(m.Start), PointJCoordinate = Coord(m.End),
            }).ToList(),
            LoadPatterns = patterns.OrderBy(n => n, StringComparer.Ordinal).ToList(),
            LoadCases = cases.OrderBy(n => n, StringComparer.Ordinal).ToList(),
        };
    }
}
