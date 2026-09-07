using COT_VCF.Domain;
using COT_VCF.Domain.Results;

namespace COT_VCF.Application.Results;

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
            warnings);
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
