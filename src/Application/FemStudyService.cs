using System.Numerics;
using COT_VCF.Domain;
using COT_VCF.Geometry;

namespace COT_VCF.Application;

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
public sealed record FemResultRow(string ObjectName, double StartValue, double? EndValue, string Unit);

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
                .Select(reaction => new FemResultRow(reaction.PointName, reaction.ReactionXKN, reaction.ReactionZKN, "kN · X / Z"))
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

public sealed class FemStudyService
{
    public const int MaxCandidates = StudyCompositionService.MaxPreviewRows;
    private readonly PlanarFrameSolver solver = new();
    private readonly TrussGeometryService geometryBuilder = new();

    public BigInteger CandidateCount(ApplicationState state)
    {
        var loading = LoadingCandidates.TotalCount(state.LoadDefinitions);
        var heights = state.BaselineCaptured ? state.Truss.CandidateCount : 0;
        var pretensions = PretensionCandidates.TotalCount(state.Cables.ToArray());
        return loading * heights * pretensions;
    }

    /// <param name="offlineModel">True when the model comes from a saved FEM model (Load Model) instead of a live SAP2000 connection.</param>
    public IReadOnlyList<FemStudyCandidate> Compose(ApplicationState state, bool offlineModel = false)
    {
        if (state.Model is null || !(state.IsSapConnected || offlineModel))
            throw new InvalidOperationException("Connect to SAP2000 and load a project, or Load Model, before running FEM.");
        if (state.Geometry.Members.Any(member => member.Kind == MemberKind.Tendon))
            throw new InvalidOperationException("The internal FEM solver currently supports SAP frame and cable objects, not tendon objects.");
        if (state.Baseline.Points.Count == 0 || !state.BaselineCaptured)
            throw new InvalidOperationException("Load a project with Tab 02 truss-height groups and capture its baseline before running FEM.");
        var trussIssues = geometryBuilder.Validate(state.Baseline, state.Truss);
        if (trussIssues.Count > 0) throw new InvalidOperationException(string.Join("\n", trussIssues.Select(issue => issue.Message)));
        if (state.AppliedPoints.Count < 2) throw new InvalidOperationException("Tab 01 needs at least two applied load locations to calculate tributary widths.");
        if (state.LoadDefinitions.Count == 0 || state.LoadDefinitions.Any(definition =>
                definition.CandidateCount <= 0 || !state.LoadPatterns.Contains(definition.LoadName, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Complete every Tab 01 load definition and confirm its load pattern exists in the linked SAP2000 model.");
        if (state.Cables.Count == 0 || state.Cables.Any(cable => cable.Missing || cable.Status != "Configured" ||
                !state.LoadPatterns.Contains(cable.LoadPattern, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Complete Tab 03 cable target-force definitions and confirm each cable/load pattern exists in SAP2000.");
        if (state.AppliedPoints.Any(point => !state.Baseline.Points.ContainsKey(point.PointName)))
            throw new InvalidOperationException("A Tab 01 loading location is absent from the baseline geometry. Refresh and review the project.");

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
        int maximumTargetFitIterations = PlanarFrameSolver.DefaultTargetFitIterations,
        int frameMeshSegments = 1,
        FemLoadPlan? loadPlan = null,
        bool cableSag = false)
    {
        if (frameMeshSegments is < 1 or > PlanarFrameSolver.MaximumFrameMeshSegments)
            throw new ArgumentOutOfRangeException(nameof(frameMeshSegments), $"Frame mesh must be between 1 and {PlanarFrameSolver.MaximumFrameMeshSegments} segments per frame.");
        var frameProperties = sectionProperties.Where(pair => pair.Value.InPlaneInertiaM4 is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var cableProperties = sectionProperties.Where(pair => pair.Value.InPlaneInertiaM4 is null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var runs = new List<FemStudyRun>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[index];
            var geometry = geometryBuilder.BuildCandidateGeometry(baseline, topPoints, middlePoints, bottomPoints, candidate.Height);
            FemAnalysisResult result;
            IReadOnlyDictionary<string, FemAnalysisResult>? results = null;
            try
            {
                if (loadPlan is null)
                    result = solver.Solve(geometry, frameProperties, cableProperties, candidate.PretensionTargetsKN,
                        candidate.Loads, pinSupports, cancellationToken, targetFitPercent, maximumTargetFitIterations, frameMeshSegments, cableSag);
                else
                {
                    // Load cases from the SAP model (as edited), then linear-additive combinations of their results.
                    // One solve per pretension stage: a case carries the pretension fitted in the stage it continues from.
                    var caseResults = new Dictionary<string, FemAnalysisResult>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (_, fitLoads, cases) in loadPlan.StagesFor(candidate.Loads))
                    foreach (var (name, caseResult) in solver.SolveCases(geometry, frameProperties, cableProperties, candidate.PretensionTargetsKN,
                                 fitLoads, cases, pinSupports, cancellationToken, targetFitPercent, maximumTargetFitIterations, frameMeshSegments, cableSag))
                        caseResults[name] = caseResult;
                    results = loadPlan.Complete(caseResults);
                    result = results[loadPlan.ResultNames[0]];
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