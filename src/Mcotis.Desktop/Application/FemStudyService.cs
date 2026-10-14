using System.Numerics;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Application;

public sealed record FemStudyCandidate(
    string Id,
    string LoadingCandidateId,
    string HeightCandidateId,
    string PretensionCandidateId,
    TrussCandidate Height,
    IReadOnlyList<FemAppliedLoad> Loads,
    IReadOnlyDictionary<string, double> PretensionTargetsKN);

public sealed record FemStudyRun(
    string Id,
    string LoadingCandidateId,
    string HeightCandidateId,
    string PretensionCandidateId,
    FemAnalysisResult Result,
    IReadOnlyList<FemAppliedLoad>? Loads = null,
    IReadOnlyDictionary<string, FemAnalysisResult>? Results = null)
{
    /// <summary>The run of a document (<see cref="FemDocumentRun"/>): the solver's own results — all six components, the
    /// modes of a modal case — beside the results in the application's terms. Null for a run of the legacy path. Held in
    /// persisted in output schema v2 beside, never inside, the legacy planar tables.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public FemDocumentResults? Native { get; init; }

    /// <summary>The named load case / combination result, or <see cref="Result"/> when there is none by that name.</summary>
    public FemAnalysisResult ResultFor(string? name) =>
        name is not null && Results is not null && Results.TryGetValue(name, out var result) ? result : Result;
}

/// <summary>Target vs fitted pretension-stage tension (the deviation the fitting tolerance controls) and the final
/// tension once the applied loads act on the prestressed structure.</summary>
public sealed record FemCableFitRow(string RunId, string CableName, double? TargetForceKN, double? PretensionStageForceKN,
    double? ResultForceKN, double? AppliedPretensionKN, int? FitIterations, string? FitFailure = null,
    double? SagM = null, double? TangentStiffnessRatio = null)
{
    /// <summary>Mid-span sag below the chord in the result shown [mm]; blank for a straight cable.</summary>
    public double? SagMm => SagM * 1000;
    /// <summary>Stiffness the sagging cable keeps, in % of EA / L (Ernst's equivalent modulus over E).</summary>
    public double? StiffnessPercent => TangentStiffnessRatio * 100;
    public double? DeviationPercent => TargetForceKN is > 0 and double target && PretensionStageForceKN is double stage
        ? (stage - target) / target * 100 : null;
    public bool Failed { get; init; }
    public string Status => Failed ? "Failed" : FitFailure is null ? "Fitted" : "Not fitted";

    /// <summary>Row for a case whose FEM solve did not reach equilibrium; it has no forces to report and names no
    /// cable — the candidate, the Failed status and the reason (Status tooltip, run log) say what happened.</summary>
    public static FemCableFitRow FailedCase(string runId, string reason) =>
        new(runId, "", null, null, null, null, null, reason) { Failed = true };

    /// <summary>Rows for one case; Result is the cable tension under <paramref name="resultName"/> (case or combination).</summary>
    public static IEnumerable<FemCableFitRow> FromRun(FemStudyRun run, string? resultName = null)
    {
        var result = run.ResultFor(resultName);
        return result.Cables
            .OrderBy(cable => cable.MemberName, StringComparer.Ordinal)
            .Select(cable => new FemCableFitRow(run.Id, cable.MemberName, cable.TargetForceKN, cable.PretensionStageForceKN,
                cable.AxialForceKN, cable.AppliedPretensionKN, result.TargetFitIterations, result.TargetFitFailure,
                cable.SagM, cable.TangentStiffnessRatio));
    }
}

public enum FemResultMode { AxialForce, BendingMoment, Shear, Reactions, DeflectedShape }
/// <summary>A row of the Results Table. <paramref name="MomentValue"/>: the moment of a support (reactions only).</summary>
public sealed record FemResultRow(string ObjectName, double StartValue, double? EndValue, string Unit, double? MomentValue = null);
/// <summary>A row of the Results Table. <paramref name="MomentValue"/>: the moment of a support (reactions only).</summary>

public static class FemResultTable
{
    public static IReadOnlyList<FemResultRow> Build(FemAnalysisResult? result, FemResultMode mode)
    {
        if (result is null) return [];
        return mode switch
        {
            FemResultMode.AxialForce => result.Frames.Select(frame =>
                    new FemResultRow(frame.MemberName, frame.AxialStartKN, frame.AxialEndKN, "kN"))
                .Concat(result.Cables.Select(cable => new FemResultRow(cable.MemberName, cable.AxialForceKN, cable.AxialForceKN, "kN")))
                .OrderBy(row => row.ObjectName, StringComparer.Ordinal).ToArray(),
            FemResultMode.BendingMoment => result.Frames
                .Select(frame => new FemResultRow(frame.MemberName, frame.MomentStartKNm, frame.MomentEndKNm, "kN·m"))
                .OrderBy(row => row.ObjectName, StringComparer.Ordinal).ToArray(),
            FemResultMode.Shear => result.Frames
                .Select(frame => new FemResultRow(frame.MemberName, frame.ShearStartKN, frame.ShearEndKN, "kN"))
                .OrderBy(row => row.ObjectName, StringComparer.Ordinal).ToArray(),
            FemResultMode.Reactions => result.SupportReactions
                .Select(reaction => new FemResultRow(reaction.PointName, reaction.ReactionXKN, reaction.ReactionZKN, "kN · X / Z · kN·m", reaction.MomentYKNm))
                .OrderBy(row => row.ObjectName, StringComparer.Ordinal).ToArray(),
            FemResultMode.DeflectedShape => result.Joints
                .Select(joint => new FemResultRow(joint.PointName, joint.UXM, joint.UZM, "m · X / Z"))
                .OrderBy(row => row.ObjectName, StringComparer.Ordinal).ToArray(),
            _ => []
        };
    }
}

public sealed record FemProgress(int Completed, int Total, string CandidateId, FemStudyRun? CompletedRun = null,
    string? Failure = null);

/// <summary>What every candidate of an optimization is analysed with besides its own geometry, members, storey loads and
/// cable targets: the load rows of the open model, its loading, its analysis options — the same as Run FEM Analysis.</summary>
public sealed record FemCandidateSetup(
    SapFemLoading Loading,
    IReadOnlyList<FemPatternRow> Patterns,
    IReadOnlyList<FemCaseRow> Cases,
    IReadOnlyList<FemCombinationRow> Combinations,
    IReadOnlyCollection<string> Tab01Patterns,
    IReadOnlyCollection<string> PretensionPatterns,
    IReadOnlyCollection<string> AppliedPoints,
    double TargetFitPercent,
    int TargetFitIterations,
    int FrameMeshSegments,
    bool CableSag,
    Mcotis.Core.DofMask ActiveDofs,
    Mcotis.Core.DofMask CableDofs)
{
    public IReadOnlyList<Configuration.FemMassSourceDto> MassSources { get; init; } = [];
}

public sealed class FemStudyService
{
    public const int MaxCandidates = StudyCompositionService.MaxPreviewRows;
    private readonly FemSolver solver = new();
    private readonly TrussGeometryService geometryBuilder = new();

    public BigInteger CandidateCount(ApplicationState state)
    {
        var loading = LoadingCandidates.TotalCount(state.LoadDefinitions);
        var heights = state.BaselineCaptured ? state.Truss.CandidateCount : 0;
        var pretensions = PretensionCandidates.TotalCount(state.Cables.ToArray());
        return loading * heights * pretensions;
    }

    /// <summary>Id of the run of <see cref="ComposeModel"/>: the model itself, not an L/H/P study candidate.</summary>
    public const string ModelRunId = "Model";

    /// <summary>The model has what one FEM run needs: its geometry, the Tab 01 loads and the Tab 03 cable targets (none
    /// for a model without cables, e.g. one imported from ETABS). The Tab 02 chord groups are not needed.</summary>
    public bool CanComposeModel(ApplicationState state) => state.Baseline.Points.Count > 0 &&
        LoadingCandidates.TotalCount(state.LoadDefinitions) > 0 &&
        (PretensionCandidates.TotalCount(state.Cables.ToArray()) > 0 || WithoutCables(state));

    /// <summary>A model with no cable members and no Tab 03 cables: it runs without a pretension stage.</summary>
    private static bool WithoutCables(ApplicationState state) =>
        state.Cables.Count == 0 && state.Geometry.Members.All(member => member.Kind != MemberKind.Cable);

    /// <summary>
    /// The model as it stands, as the one case Run → Run FEM Analysis solves: its own geometry (no Tab 02 height change),
    /// the Tab 01 loads and the Tab 03 cable targets. A Tab 01 / Tab 03 range is an input of the parametric study, not of
    /// this run; it enters with its first value. <see cref="Compose"/> is the study's candidate space.
    /// </summary>
    /// <param name="offlineModel">True when the model comes from a saved FEM model (Load Model) instead of a live SAP2000 connection.</param>
    public FemStudyCandidate ComposeModel(ApplicationState state, bool offlineModel = false)
    {
        RequireModel(state, offlineModel);
        var loading = LoadingCandidates.Enumerate(state.LoadDefinitions.ToArray()).First();
        var loads = StudyCompositionService.BuildPointLoads(state, loading)
            .Select(row => new FemAppliedLoad(row.AppliedPt, 0, row.FzKn, row.Case)).ToArray();
        var pretension = PretensionCandidates.Enumerate(state.Cables.ToArray()).FirstOrDefault();
        var height = state.Truss.BaselineHeight;
        return new FemStudyCandidate(ModelRunId, "", "", "",
            new TrussCandidate(1, height, 0, state.Truss.Reference.ToString(), 0, 0, 0), loads,
            pretension?.CableForces ?? new Dictionary<string, double>(StringComparer.Ordinal));
    }

    /// <summary>What every FEM run needs of the model and of Tabs 01 and 03, whatever the number of cases.</summary>
    private static void RequireModel(ApplicationState state, bool offlineModel)
    {
        if (state.Model is null || !(state.IsSapConnected || offlineModel))
            throw new InvalidOperationException("Open a model before running FEM: File → Load VCF… or Load Model, or Connect SAP and load a project.");
        if (state.Geometry.Members.Any(member => member.Kind == MemberKind.Tendon))
            throw new InvalidOperationException("The internal FEM solver currently supports frame and cable objects, not tendon objects.");
        // the run solves the model's own geometry; the Tab 02 chord groups are an input of the truss-height study only
        if (state.Baseline.Points.Count == 0)
            throw new InvalidOperationException("The model has no geometry to analyse. Open or import a model first.");
        if (state.AppliedPoints.Count < 2) throw new InvalidOperationException("Tab 01 needs at least two applied load locations to calculate tributary widths.");
        if (state.LoadDefinitions.Count == 0 || state.LoadDefinitions.Any(definition =>
                definition.CandidateCount <= 0 || !state.LoadPatterns.Contains(definition.LoadName, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Complete every Tab 01 load definition and confirm its load pattern exists in the model.");
        if (state.Cables.Count == 0 && !WithoutCables(state) || state.Cables.Any(cable => cable.Missing || cable.Status != "Configured" ||
                !state.LoadPatterns.Contains(cable.LoadPattern, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Complete the Tab 03 cable target-force definitions and confirm each cable and load pattern exists in the model.");
        if (state.AppliedPoints.Any(point => !state.Baseline.Points.ContainsKey(point.PointName)))
            throw new InvalidOperationException("A Tab 01 loading location is absent from the model geometry. Review Tab 01.");
    }

    /// <summary>The study's candidate space: every Tab 01 × Tab 02 × Tab 03 combination (the SAP2000 benchmark runs it).</summary>
    /// <param name="offlineModel">True when the model comes from a saved FEM model (Load Model) instead of a live SAP2000 connection.</param>
    public IReadOnlyList<FemStudyCandidate> Compose(ApplicationState state, bool offlineModel = false)
    {
        RequireModel(state, offlineModel);
        var trussIssues = geometryBuilder.Validate(state.Baseline, state.Truss);
        if (trussIssues.Count > 0) throw new InvalidOperationException(string.Join("\n", trussIssues.Select(issue => issue.Message)));

        var total = CandidateCount(state);
        if (total <= 0) throw new InvalidOperationException("Tabs 01–03 do not produce a complete FEM candidate space.");
        if (total > MaxCandidates)
            throw new InvalidOperationException($"The FEM candidate space contains {total:N0} runs; the limit is {MaxCandidates:N0}. Reduce one or more Tab 01–03 ranges.");

        var loads = LoadingCandidates.Enumerate(state.LoadDefinitions.ToArray()).Select(combination =>
        {
            var rows = StudyCompositionService.BuildPointLoads(state, combination);
            return (Id: StudyCompositionService.CandidateId('L', combination.Index),
                Loads: (IReadOnlyList<FemAppliedLoad>)rows.Select(row => new FemAppliedLoad(row.AppliedPt, 0, row.FzKn, row.Case)).ToArray());
        }).ToArray();
        var heights = state.Truss.CandidateHeights.Select((height, index) =>
        {
            var delta = height - state.Truss.BaselineHeight;
            var (top, middle, bottom) = TrussGeometryService.ChordDeltas(state.Truss.Reference, delta);
            return (Id: StudyCompositionService.CandidateId('H', index),
                Candidate: new TrussCandidate(index + 1, height, delta, state.Truss.Reference.ToString(), top, middle, bottom));
        }).ToArray();
        var pretensions = PretensionCandidates.Enumerate(state.Cables.ToArray()).Select(combination =>
            (Id: StudyCompositionService.CandidateId('P', combination.CombinationIndex),
                Targets: combination.CableForces)).ToArray();

        if ((BigInteger)loads.Length * heights.Length * pretensions.Length != total)
            throw new InvalidOperationException("The FEM candidate axes changed while they were being frozen. Review the inputs and retry.");

        var candidates = new List<FemStudyCandidate>((int)total);
        foreach (var load in loads)
        foreach (var height in heights)
        foreach (var pretension in pretensions)
        {
            candidates.Add(new FemStudyCandidate(
                $"{load.Id}_{height.Id}_{pretension.Id}", load.Id, height.Id, pretension.Id,
                height.Candidate, load.Loads, pretension.Targets));
        }
        return candidates;
    }

    public static IReadOnlyList<string> FindPinSupportPoints(GeometryModel geometry)
    {
        var frames = geometry.Members.Where(member => member.Kind == MemberKind.Frame).ToArray();
        var connected = frames.SelectMany(member => new[] { member.Start, member.End }).Distinct(StringComparer.Ordinal)
            .Select(name => geometry.Points.TryGetValue(name, out var point)
                ? point
                : throw new InvalidOperationException($"Frame endpoint '{name}' is missing from the linked geometry."))
            .ToArray();
        if (connected.Length == 0) throw new InvalidOperationException("The linked model contains no connected frame joints.");
        var minimumZ = connected.Min(point => point.Position.Z);
        var tolerance = Math.Max(1e-6, (connected.Max(point => point.Position.Z) - minimumZ) * 1e-8);
        var supports = frames.Select(member =>
            {
                var start = geometry.Points[member.Start].Position;
                var end = geometry.Points[member.End].Position;
                var lowerName = start.Z <= end.Z ? member.Start : member.End;
                var lower = start.Z <= end.Z ? start : end;
                var upper = start.Z <= end.Z ? end : start;
                var verticalLength = Math.Abs(upper.Z - lower.Z);
                var horizontalOffset = Math.Abs(upper.X - lower.X);
                return lower.Z <= minimumZ + tolerance && upper.Z > minimumZ + tolerance &&
                    horizontalOffset <= Math.Max(1e-6, verticalLength * 0.01) ? lowerName : null;
            })
            .Where(name => name is not null)
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => geometry.Points[name].Position.X)
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (supports.Length < 2)
            throw new InvalidOperationException("Could not find at least two column-base joints at the lowest frame elevation.");
        return supports;
    }

    /// <summary>
    /// Run FEM Analysis of a document: the cases of <paramref name="plan"/>, solved from <paramref name="document"/> by
    /// <see cref="FemDocumentRun"/> stage by stage — the fit of each stage's cable target forces, then its cases — and the
    /// combinations of the plan added up. <paramref name="order"/> is the application's model, whose order the results keep.
    /// </summary>
    public FemStudyRun AnalyzeDocument(Mcotis.Core.ModelDocument document, FemLoadPlan plan, string runId, GeometryModel? order,
        IReadOnlyList<FemAppliedLoad>? loads = null, IProgress<FemEquilibriumProgress>? equilibrium = null, CancellationToken cancellationToken = default,
        bool modal = true)
    {
        // the order the monitor plans the run in: per pretension stage its cases, the case that fits first
        var names = plan.StagesFor(loads ?? []).SelectMany(stage => stage.Cases.Select(item => item.Name))
            .Concat(plan.CaseNames).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => modal || document.LoadCases.Values.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))?.Kind != Mcotis.Core.CaseKind.ModalEigen)
            .ToArray();
        var solved = FemDocumentRun.Solve(document, names, order, solver.StabilityThresholds, solver.MaximumEquilibriumIterations, equilibrium, cancellationToken);
        var results = plan.Complete(solved.Cases);
        var primary = results.TryGetValue(plan.PrimaryResultName, out var first) ? first : results[plan.ResultNames.First(results.ContainsKey)];
        return new FemStudyRun(runId, "", "", "", primary, loads, results) { Native = solved };
    }

    /// <summary>
    /// One candidate of an optimization, analysed as Run FEM Analysis analyses the model (task of 2026-10-14, M1: "candidate
    /// document → solver"): the candidate — its geometry, its members, the open model's load rows with the candidate's
    /// storey loads and cable targets — is made a native document the way the model's own document is made
    /// (<see cref="LegacyNativeDocumentMigration.Convert"/>), and solved by <see cref="FemDocumentRun"/>. Its supports
    /// are the pins at its column bases, as they have always been for a candidate. Refusals are
    /// <see cref="InvalidOperationException"/>s with the reason.
    /// </summary>
    public FemStudyRun AnalyzeCandidate(string id, GeometryModel geometry, IReadOnlyDictionary<string, FemSectionProperties> properties,
        FemCandidateSetup setup, IReadOnlyList<FemAppliedLoad> loads, IReadOnlyDictionary<string, double> targets,
        IReadOnlyDictionary<string, string>? targetPatterns = null, CancellationToken cancellationToken = default)
    {
        var plan = FemLoadPlan.Build(setup.Patterns, setup.Cases, setup.Combinations, setup.Loading, properties, geometry,
            setup.Tab01Patterns, setup.PretensionPatterns, setup.AppliedPoints, setup.MassSources.Select(source => (Guid.Parse(source.Id), source.Name)).ToArray());
        var document = CandidateDocument(geometry, properties, setup, loads, targets, targetPatterns);
        // a study reads static results: the modes of each candidate are not asked for
        return AnalyzeDocument(document, plan, id, geometry, loads, null, cancellationToken, modal: false);
    }

    /// <summary>A fixed identity for candidate documents: the same candidate is always the same document, to the last digit.</summary>
    private static readonly Guid CandidateDatabaseId = new("6d636f74-6973-4f70-8000-63616e646964");

    internal static Mcotis.Core.ModelDocument CandidateDocument(GeometryModel geometry, IReadOnlyDictionary<string, FemSectionProperties> properties,
        FemCandidateSetup setup, IReadOnlyList<FemAppliedLoad> loads, IReadOnlyDictionary<string, double> targets, IReadOnlyDictionary<string, string>? targetPatterns)
    {
        var tab01 = setup.Tab01Patterns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var applied = setup.AppliedPoints.ToHashSet(StringComparer.Ordinal);
        // the candidate's storey loads replace what the model holds in their patterns at the loaded points (FemLoadPlan)
        var joints = setup.Loading.JointLoads.Where(item => !(tab01.Contains(item.Pattern) && applied.Contains(item.Load.PointName)))
            .Concat(loads.Where(load => load.Pattern is { } pattern && tab01.Contains(pattern))
                .Select(load => new SapPatternJointLoad(load.Pattern!, new FemJointLoad(load.PointName, load.FxKN, load.FzKN))))
            .ToArray();
        string? fallbackPattern = setup.PretensionPatterns.FirstOrDefault();
        var cableRows = targets.Where(pair => pair.Value > 0 && properties.ContainsKey(pair.Key))
            .Select(pair => new Configuration.CableForceRowDto
            {
                CableName = pair.Key,
                PretensionLoadCase = targetPatterns?.GetValueOrDefault(pair.Key) ?? fallbackPattern
                    ?? throw new InvalidOperationException($"Cable '{pair.Key}' has a target force but the model has no cable target-force load pattern."),
                Mode = Configuration.CableForceMode.Fixed, FixedForce = pair.Value,
            }).ToList();
        static List<string> Names(Mcotis.Core.DofMask mask) => Mcotis.Core.DegreesOfFreedom.Components
            .Where(component => Mcotis.Core.DegreesOfFreedom.Contains(mask, component)).Select(component => component.ToString()).ToList();
        var snapshot = new Configuration.FemInputSnapshot
        {
            DatabaseId = CandidateDatabaseId,
            Geometry = geometry,
            Baseline = geometry,
            SectionProperties = properties.Values.OrderBy(property => property.MemberName, StringComparer.Ordinal).ToList(),
            Loading = setup.Loading with { JointLoads = joints },
            PatternRows = setup.Patterns.Select(Configuration.FemRowDtos.Pattern).ToList(),
            CaseRows = setup.Cases.Select(row => Configuration.FemRowDtos.Case(row, setup.MassSources)).ToList(),
            CombinationRows = setup.Combinations.Select(Configuration.FemRowDtos.Combination).ToList(),
            CableSetup = cableRows.Count > 0 ? new Configuration.CableForceConfigDto { Cables = cableRows } : null,
            MassSources = setup.MassSources.ToList(),
            Settings = new Configuration.FemSettingsDto
            {
                TargetFitPercent = setup.TargetFitPercent, TargetFitIterations = setup.TargetFitIterations,
                FrameMeshSegments = setup.FrameMeshSegments, CableSag = setup.CableSag,
                ActiveDofs = Names(setup.ActiveDofs), CableJointDofs = Names(setup.CableDofs),
            },
        };
        try { return LegacyNativeDocumentMigration.Convert(snapshot, LegacyNativeDocumentMigration.Source.Generated("Candidate")); }
        catch (Exception ex) when (ex is System.IO.InvalidDataException or ArgumentException or FormatException or Mcotis.Core.DocumentException)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    public IReadOnlyList<FemStudyRun> Analyze(
        GeometryModel baseline,
        IReadOnlyList<string> topPoints,
        IReadOnlyList<string> middlePoints,
        IReadOnlyList<string> bottomPoints,
        IReadOnlyList<string> pinSupports,
        IReadOnlyList<FemStudyCandidate> candidates,
        IReadOnlyDictionary<string, FemSectionProperties> sectionProperties,
        IProgress<FemProgress>? progress = null,
        CancellationToken cancellationToken = default,
        double? targetFitPercent = null,
        int maximumTargetFitIterations = FemSolver.DefaultTargetFitIterations,
        int frameMeshSegments = 1,
        FemLoadPlan? loadPlan = null,
        bool cableSag = false,
        IProgress<FemEquilibriumProgress>? equilibrium = null,
        Mcotis.Core.DofMask activeDofs = Mcotis.Core.DofMask.XZ,
        Mcotis.Core.DofMask cableDofs = Mcotis.Core.DofMask.Translations,
        IReadOnlyDictionary<string, Mcotis.Core.Restraint>? jointRestraints = null)
    {
        if (frameMeshSegments is < 1 or > FemSolver.MaximumFrameMeshSegments)
            throw new ArgumentOutOfRangeException(nameof(frameMeshSegments), $"Frame mesh must be between 1 and {FemSolver.MaximumFrameMeshSegments} segments per frame.");
        var frameProperties = sectionProperties.Where(pair => pair.Value.InPlaneInertiaM4 is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var cableProperties = sectionProperties.Where(pair => pair.Value.InPlaneInertiaM4 is null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var runs = new List<FemStudyRun>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[index];
            // Without a height change the case is the model geometry itself; the Tab 02 chord groups are not needed.
            var geometry = candidate.Height is { TopDz: 0, MidDz: 0, BotDz: 0 } ? baseline
                : geometryBuilder.BuildCandidateGeometry(baseline, topPoints, middlePoints, bottomPoints, candidate.Height);
            FemAnalysisResult result;
            IReadOnlyDictionary<string, FemAnalysisResult>? results = null;
            // A solve's report placed in the run: the fit stage under the name of its case, and its share of the whole run.
            int candidateIndex = index;
            IProgress<FemEquilibriumProgress>? Follow(string? fitStage, int stage, int stages) => equilibrium is null ? null
                : new DirectProgress<FemEquilibriumProgress>(report => equilibrium.Report(report with
                {
                    CaseName = report.FitStage ? fitStage ?? "" : report.CaseName,
                    Fraction = (candidateIndex + (stage + report.Fraction) / stages) / candidates.Count,
                }));
            try
            {
                if (loadPlan is null)
                    result = solver.Solve(geometry, frameProperties, cableProperties, candidate.PretensionTargetsKN,
                        candidate.Loads, pinSupports, cancellationToken, targetFitPercent, maximumTargetFitIterations, frameMeshSegments, cableSag,
                        Follow(null, 0, 1), activeDofs, cableDofs, jointRestraints);
                else
                {
                    // Load cases from the SAP model (as edited), then linear-additive combinations of their results.
                    // One solve per pretension stage: a case carries the pretension fitted in the stage it continues from.
                    var caseResults = new Dictionary<string, FemAnalysisResult>(StringComparer.OrdinalIgnoreCase);
                    var stages = loadPlan.StagesFor(candidate.Loads);
                    for (int stage = 0; stage < stages.Count; stage++)
                    {
                        var (fitStage, fitLoads, cases) = stages[stage];
                        foreach (var (name, caseResult) in solver.SolveCases(geometry, frameProperties, cableProperties,
                                     fitStage is null ? new Dictionary<string, double>() : candidate.PretensionTargetsKN,
                                     fitLoads, cases, pinSupports, cancellationToken, targetFitPercent, maximumTargetFitIterations, frameMeshSegments, cableSag,
                                     Follow(fitStage, stage, stages.Count), activeDofs, cableDofs, jointRestraints))
                            caseResults[name] = caseResult;
                    }
                    results = loadPlan.Complete(caseResults);
                    result = results[loadPlan.PrimaryResultName];
                }
            }
            catch (InvalidOperationException ex)
            {
                // One case that does not reach equilibrium must not hide the others; report it and continue.
                progress?.Report(new FemProgress(index + 1, candidates.Count, candidate.Id, Failure: ex.Message));
                continue;
            }
            var run = new FemStudyRun(candidate.Id, candidate.LoadingCandidateId, candidate.HeightCandidateId,
                candidate.PretensionCandidateId, result, candidate.Loads, results);
            runs.Add(run);
            progress?.Report(new FemProgress(index + 1, candidates.Count, candidate.Id, run));
        }
        return runs;
    }
}
