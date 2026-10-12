using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Domain.Results;

namespace Mcotis.Desktop.Application.Results;

/// <summary>Turns one analyzed child model into a <see cref="CandidateResultDto"/>: reads the configured
/// monitored-point Uz under the configured output case and records an explicit status for every point.
/// This phase produces exactly one deflection group. Missing results are never coerced to zero.</summary>
public sealed class SapResultExtractor
{
    public const string GroupName = "Monitored Deflection";

    public async Task<CandidateResultDto> ExtractAsync(
        ISapGateway gateway,
        StudyExecutionSnapshot snapshot,
        StudyIterationExecutionDto job,
        ModelInfo analyzedChild,
        DateTimeOffset analysisStarted,
        DateTimeOffset analysisCompleted,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var warnings = new List<ResultWarningDto>();
        var parameters = BuildParameters(job);
        var groups = new List<MonitoredPointGroupResultDto>();
        var status = CandidateExtractionStatus.ExtractedOk;

        var points = snapshot.DeflectionPoints ?? [];
        var outputCase = snapshot.DeflectionOutputCase ?? "";

        if (points.Count == 0)
        {
            warnings.Add(Warn(job, "MonitoredPoint", ResultWarningSeverity.Warning,
                "No monitored deflection points are configured in Output Settings."));
            status = CandidateExtractionStatus.ExtractedWithWarning;
        }
        else if (string.IsNullOrWhiteSpace(outputCase))
        {
            warnings.Add(Warn(job, "OutputCase", ResultWarningSeverity.Error,
                "No deflection output case is configured in Output Settings."));
            status = CandidateExtractionStatus.ExtractionFailed;
        }
        else
        {
            var reads = await gateway.GetJointDisplacementsAsync(points, outputCase, isCombo: false, analyzedChild);
            if (reads.Count > 0 && reads.All(r => r.Status == ResultValueStatus.CaseNotFound))
                reads = await gateway.GetJointDisplacementsAsync(points, outputCase, isCombo: true, analyzedChild);

            var ordered = reads
                .Select((read, i) => (read, i))
                .OrderBy(t => t.read.X)
                .ThenBy(t => t.i)
                .Select((t, index) => new MonitoredPointResultDto(
                    t.read.PointName, t.read.X, t.read.Y, t.read.Z, index + 1,
                    t.read.Status == ResultValueStatus.Valid ? t.read.UzMm : null,
                    t.read.Status, t.read.Warning))
                .ToArray();

            foreach (var p in ordered.Where(p => p.Status != ResultValueStatus.Valid))
                warnings.Add(Warn(job, "MonitoredPoint", ResultWarningSeverity.Warning,
                    $"{p.PointName}: {p.Status}{(p.Warning is null ? "" : $" — {p.Warning}")}"));

            groups.Add(new MonitoredPointGroupResultDto(GroupName, outputCase, ProfileCoordinateAxis.X, ordered));

            var valid = ordered.Count(p => p.Status == ResultValueStatus.Valid);
            status = valid == 0 ? CandidateExtractionStatus.ExtractionFailed
                : valid < ordered.Length ? CandidateExtractionStatus.ExtractedWithWarning
                : CandidateExtractionStatus.ExtractedOk;
        }

        // Cable forces (Tab 06.05) — additive: never changes the deflection-driven ExtractionStatus, only warns.
        var cableForces = await ExtractCableForcesAsync(gateway, snapshot, job, analyzedChild, warnings);
        // Member forces are also additive. Every SAP analysis-element/station row is retained so an
        // auto-meshed frame object can be plotted as the same detailed profile shown by SAP2000.
        var memberForces = await ExtractMemberForcesAsync(gateway, snapshot, job, analyzedChild, warnings);
        // Foundation reactions are additive and follow the exact Left/Right point selections frozen from Output Settings.
        var supportReactions = await ExtractSupportReactionsAsync(gateway, snapshot, job, analyzedChild, warnings);

        return new CandidateResultDto(
            job.IterationId,
            job.ModelFullPath,
            snapshot.BaselineModelPath,
            snapshot.BaselineModel.Name,
            snapshot.StudyMode,
            parameters,
            analysisStarted,
            analysisCompleted,
            DateTimeOffset.Now,
            status,
            groups,
            warnings,
            cableForces,
            memberForces,
            supportReactions);
    }

    private async Task<IReadOnlyList<SupportReactionGroupResultDto>> ExtractSupportReactionsAsync(
        ISapGateway gateway, StudyExecutionSnapshot snapshot, StudyIterationExecutionDto job,
        ModelInfo analyzedChild, List<ResultWarningDto> warnings)
    {
        var left = snapshot.FoundationLeftPoints ?? [];
        var right = snapshot.FoundationRightPoints ?? [];
        var resultCase = snapshot.FoundationReactionResultCase ?? "";
        if (left.Count == 0 && right.Count == 0) return [];
        if (string.IsNullOrWhiteSpace(resultCase))
        {
            warnings.Add(Warn(job, "SupportReaction", ResultWarningSeverity.Warning,
                "Foundation reaction result load case is not configured in Output Settings — reactions were not extracted."));
            return [];
        }

        var requested = left.Concat(right).Distinct(StringComparer.Ordinal).ToArray();
        var reads = await gateway.GetJointReactionsAsync(requested, resultCase, isCombo: false, analyzedChild);
        if (reads.Count > 0 && reads.All(read => read.Status == ResultValueStatus.CaseNotFound))
            reads = await gateway.GetJointReactionsAsync(requested, resultCase, isCombo: true, analyzedChild);
        var byName = reads.ToDictionary(read => read.PointName, StringComparer.Ordinal);

        SupportReactionGroupResultDto BuildGroup(string side, string sapGroup, IReadOnlyList<string> names)
        {
            var ordered = names.Distinct(StringComparer.Ordinal)
                .Select(name => byName.TryGetValue(name, out var read)
                    ? read
                    : new JointReactionRead(name, 0, 0, 0, [], ResultValueStatus.NoResult,
                        $"No support-reaction result returned for '{name}'."))
                .OrderBy(read => read.X).ThenBy(read => read.Y).ThenBy(read => read.Z)
                .ThenBy(read => read.PointName, StringComparer.Ordinal)
                .ToArray();
            var points = new List<SupportReactionPointResultDto>();
            foreach (var (read, index) in ordered.Select((read, index) => (read, index)))
            {
                if (read.Status != ResultValueStatus.Valid)
                {
                    warnings.Add(Warn(job, "SupportReaction", ResultWarningSeverity.Warning,
                        $"{side} / {read.PointName}: {read.Status}{(read.Warning is null ? "" : $" — {read.Warning}")}"));
                    points.Add(new(read.PointName, index + 1, read.X, read.Y, read.Z, "", 0,
                        null, null, null, null, null, null, read.Status, read.Warning));
                    continue;
                }
                foreach (var row in read.Rows)
                    points.Add(new(read.PointName, index + 1, read.X, read.Y, read.Z, row.StepType, row.StepNumber,
                        row.Fx, row.Fy, row.Fz, row.Mx, row.My, row.Mz, ResultValueStatus.Valid));
            }
            return new(side, sapGroup, resultCase, points);
        }

        return
        [
            BuildGroup("Left", OutputGroupNaming.FoundationLeft, left),
            BuildGroup("Right", OutputGroupNaming.FoundationRight, right)
        ];
    }

    private async Task<IReadOnlyList<MemberForceGroupResultDto>> ExtractMemberForcesAsync(
        ISapGateway gateway, StudyExecutionSnapshot snapshot, StudyIterationExecutionDto job,
        ModelInfo analyzedChild, List<ResultWarningDto> warnings)
    {
        var configured = snapshot.OutputGroups.Where(group => group.ObjectKind == OutputGroupObjectKind.Frame).ToArray();
        var resultCase = snapshot.MemberForceResultCase ?? "";
        if (configured.Length == 0) return [];
        if (string.IsNullOrWhiteSpace(resultCase))
        {
            warnings.Add(Warn(job, "MemberForce", ResultWarningSeverity.Warning,
                "Member-force result load case is not configured in Output Settings — member forces were not extracted."));
            return [];
        }

        var members = snapshot.BaselineGeometry.Members.ToDictionary(member => member.Name, StringComparer.Ordinal);
        var points = snapshot.BaselineGeometry.Points;
        var output = new List<MemberForceGroupResultDto>(configured.Length);
        foreach (var group in configured)
        {
            var orderedNames = group.ObjectNames
                .Distinct(StringComparer.Ordinal)
                .Select(name =>
                {
                    if (!members.TryGetValue(name, out var member) ||
                        !points.TryGetValue(member.Start, out var start) || !points.TryGetValue(member.End, out var end))
                        return (Name: name, StartX: 0.0, EndX: 0.0, Length: 0.0, Known: false);
                    var dx = end.Position.X - start.Position.X;
                    var dy = end.Position.Y - start.Position.Y;
                    var dz = end.Position.Z - start.Position.Z;
                    return (Name: name, StartX: start.Position.X, EndX: end.Position.X,
                        Length: Math.Sqrt(dx * dx + dy * dy + dz * dz), Known: true);
                })
                .OrderBy(item => item.Known ? Math.Min(item.StartX, item.EndX) : double.MaxValue)
                .ThenBy(item => item.Known ? Math.Max(item.StartX, item.EndX) : double.MaxValue)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ToArray();

            var reads = await gateway.GetFrameForcesAsync(orderedNames.Select(item => item.Name).ToArray(), resultCase, isCombo: false, analyzedChild);
            if (reads.Count > 0 && reads.All(read => read.Status == ResultValueStatus.CaseNotFound))
                reads = await gateway.GetFrameForcesAsync(orderedNames.Select(item => item.Name).ToArray(), resultCase, isCombo: true, analyzedChild);
            var byId = reads.ToDictionary(read => read.FrameName, StringComparer.Ordinal);
            var stations = new List<MemberForceStationResultDto>();
            var profileOffset = 0.0;
            foreach (var (item, index) in orderedNames.Select((item, index) => (item, index)))
            {
                var read = byId.TryGetValue(item.Name, out var found)
                    ? found
                    : new FrameForceRead(item.Name, [], ResultValueStatus.NoResult,
                        $"No frame-force result returned for '{item.Name}'.");
                if (read.Status != ResultValueStatus.Valid)
                    warnings.Add(Warn(job, "MemberForce", ResultWarningSeverity.Warning,
                        $"{group.Name} / {read.FrameName}: {read.Status}{(read.Warning is null ? "" : $" — {read.Warning}")}"));
                foreach (var row in read.Stations)
                {
                    var local = item.StartX <= item.EndX ? row.ObjectStationM : item.Length - row.ObjectStationM;
                    if (item.Length > 0) local = Math.Clamp(local, 0, item.Length);
                    stations.Add(new MemberForceStationResultDto(read.FrameName, index + 1, row.ElementName,
                        row.ObjectStationM, row.ElementStationM, profileOffset + local, row.StepType, row.StepNumber,
                        row.P, row.V2, row.V3, row.T, row.M2, row.M3, ResultValueStatus.Valid));
                }
                profileOffset += item.Length;
            }

            var displayName = group.Name.StartsWith(OutputGroupNaming.Prefix, StringComparison.OrdinalIgnoreCase)
                ? group.Name[OutputGroupNaming.Prefix.Length..]
                : group.Name;
            output.Add(new MemberForceGroupResultDto(displayName, group.Name, resultCase,
                stations.OrderBy(row => row.ProfileCoordinateM).ThenBy(row => row.FrameSequence)
                    .ThenBy(row => row.ElementName, StringComparer.Ordinal).ThenBy(row => row.ElementStationM)
                    .ThenBy(row => row.StepType, StringComparer.Ordinal).ThenBy(row => row.StepNumber).ToArray()));
        }
        return output;
    }

    private async Task<IReadOnlyList<CableForceResultDto>> ExtractCableForcesAsync(
        ISapGateway gateway, StudyExecutionSnapshot snapshot, StudyIterationExecutionDto job,
        ModelInfo analyzedChild, List<ResultWarningDto> warnings)
    {
        var cables = snapshot.CableResultCables ?? [];
        var resultCase = snapshot.CableResultLoadCase ?? "";
        var targets = job.Pretension.Assignments
            .GroupBy(a => a.CableName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (double?)g.First().TargetForce, StringComparer.Ordinal);

        if (cables.Count == 0 || string.IsNullOrWhiteSpace(resultCase))
        {
            if (cables.Count > 0)
                warnings.Add(Warn(job, "CableForce", ResultWarningSeverity.Warning,
                    "Cable result load case is not configured in Output Settings — cable forces were not extracted."));
            return [];
        }

        var reads = await gateway.GetCableForcesAsync(cables, resultCase, isCombo: false, analyzedChild);
        if (reads.Count > 0 && reads.All(r => r.Status == ResultValueStatus.CaseNotFound))
            reads = await gateway.GetCableForcesAsync(cables, resultCase, isCombo: true, analyzedChild);

        var rows = reads.Select(r => new CableForceResultDto(
            r.CableName,
            targets.TryGetValue(r.CableName, out var t) ? t : null,
            r.Status == ResultValueStatus.Valid ? r.ForceKN : null,
            r.Status, r.Warning)).ToArray();

        foreach (var r in rows.Where(r => r.Status != ResultValueStatus.Valid))
            warnings.Add(Warn(job, "CableForce", ResultWarningSeverity.Warning,
                $"{r.CableName}: {r.Status}{(r.Warning is null ? "" : $" — {r.Warning}")}"));
        return rows;
    }

    /// <summary>Builds a failure DTO when analysis or verification never succeeded, so a diagnostic
    /// report can still be written for the candidate.</summary>
    public CandidateResultDto FailureResult(
        StudyExecutionSnapshot snapshot, StudyIterationExecutionDto job, string reason,
        DateTimeOffset analysisStarted) => new(
            job.IterationId, job.ModelFullPath, snapshot.BaselineModelPath, snapshot.BaselineModel.Name,
            snapshot.StudyMode, BuildParameters(job), analysisStarted, DateTimeOffset.Now, DateTimeOffset.Now,
            CandidateExtractionStatus.AnalysisFailed, [],
            [Warn(job, "Extraction", ResultWarningSeverity.Error, reason)]);

    private static CandidateParametersDto BuildParameters(StudyIterationExecutionDto job)
    {
        var combo = job.Loading.Combination.Loads;
        double? loadLevel = combo.Count == 1 ? combo[0].Qz : null;
        var loadingLabel = combo.Count == 0 ? job.LoadingCandidateId
            : string.Join(" + ", combo.Select(l => $"{l.Name} {l.Qz:0.###} kPa"));
        var loadComponents = combo.Select(l => new LoadComponentDto(l.Name, l.Qz)).ToArray();

        var forces = job.Pretension.Assignments.Select(a => a.TargetForce).Distinct().ToArray();
        double? pretension = forces.Length == 1 ? forces[0] : null;
        var pretensionLabel = job.Pretension.Assignments.Count == 0 ? "none"
            : pretension is { } f ? $"{job.PretensionCandidateId} · {f:0.###} kN"
            : $"{job.PretensionCandidateId} · mixed";
        var cables = job.Pretension.Assignments
            .GroupBy(a => a.CableName, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PretensionCableDto(g.Key, g.First().TargetForce))
            .ToArray();

        return new CandidateParametersDto(
            job.RunIndex, job.IterationId, job.LoadingCandidateId, job.HeightCandidateId, job.PretensionCandidateId,
            loadLevel, loadingLabel, job.Height.Height, job.Height.DeltaH, pretension, pretensionLabel, cables, loadComponents);
    }

    private static ResultWarningDto Warn(StudyIterationExecutionDto job, string category, ResultWarningSeverity severity, string message) =>
        new(job.IterationId, category, severity, message, DateTimeOffset.Now);
}
