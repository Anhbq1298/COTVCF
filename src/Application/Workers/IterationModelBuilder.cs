using COT_VCF.Domain;
using COT_VCF.Geometry;
using System.IO;

namespace COT_VCF.Application.Workers;

public sealed class IterationModelBuilder(
    TrussGeometryService? geometryService = null,
    IterationCandidateVerifier? verifier = null)
{
    private readonly TrussGeometryService geometryService = geometryService ?? new();
    private readonly IterationCandidateVerifier verifier = verifier ?? new();

    public async Task<ModelInfo> BuildAsync(
        ISapGateway gateway,
        StudyExecutionSnapshot snapshot,
        StudyIterationExecutionDto job,
        int workerId,
        Action<StudyWorkerStage, StudyIterationStatus, string, string?> report,
        CancellationToken token)
    {
        var stage = StudyWorkerStage.PreparingFolder;
        try
        {
            token.ThrowIfCancellationRequested();
            report(stage, StudyIterationStatus.PreparingFolder, "Preparing candidate folder.", null);
            if (File.Exists(job.ModelFullPath))
                throw new InvalidOperationException($"Existing output will not be overwritten: {job.ModelFullPath}");
            Directory.CreateDirectory(job.IterationDirectory);
            AppendDiagnostic(job, workerId,
                $"START baseline={snapshot.BaselineModelPath} child={job.ModelFullPath} " +
                $"candidates=L:{job.LoadingCandidateId},H:{job.HeightCandidateId},P:{job.PretensionCandidateId}");

            token.ThrowIfCancellationRequested();
            stage = StudyWorkerStage.OpeningBaseline;
            Report(stage, StudyIterationStatus.OpeningBaseline, "Opening immutable baseline.", snapshot.BaselineModelPath);
            var baselineInfo = await gateway.OpenModelAsync(snapshot.BaselineModelPath);
            VerifyBaseline(snapshot.BaselineModel, baselineInfo);

            token.ThrowIfCancellationRequested();
            stage = StudyWorkerStage.CreatingChild;
            Report(stage, StudyIterationStatus.CreatingChild, "Creating isolated child model.", job.ModelFullPath);
            var childInfo = await gateway.SaveAsAsync(job.ModelFullPath, baselineInfo);

            token.ThrowIfCancellationRequested();
            stage = StudyWorkerStage.ApplyingGeometry;
            Report(stage, StudyIterationStatus.ApplyingGeometry, "Applying truss-height candidate.", childInfo.Path);
            var proposed = geometryService.BuildCandidateGeometry(
                snapshot.BaselineGeometry,
                snapshot.TopPoints,
                snapshot.MiddlePoints,
                snapshot.BottomPoints,
                job.Height);
            var geometryChangeCount = proposed.Points.Values.Count(point =>
                snapshot.BaselineGeometry.Points.TryGetValue(point.Name, out var baseline) && baseline.Position != point.Position);
            AppendDiagnostic(job, workerId,
                $"RESOLVED height={job.HeightCandidateId},H0={job.Height.Height - job.Height.DeltaH:0.########}," +
                $"H={job.Height.Height:0.########},deltaH={job.Height.DeltaH:+0.########;-0.########;0},modifiedPoints={geometryChangeCount}; " +
                $"loading={job.LoadingCandidateId},pointLoads={job.Loading.Loads.Count}; " +
                $"pretension={job.PretensionCandidateId},cableTargets={job.Pretension.Assignments.Count}");
            await gateway.MovePointsAsync(snapshot.BaselineGeometry, proposed, childInfo);

            token.ThrowIfCancellationRequested();
            stage = StudyWorkerStage.ApplyingLoading;
            Report(stage, StudyIterationStatus.ApplyingLoading,
                $"Applying {job.Loading.Id}: {job.Loading.Loads.Count} point loads to {job.Loading.JointCount} selected joints.", childInfo.Path);
            await gateway.ApplyLoadsAsync(job.Loading.Loads, childInfo);

            token.ThrowIfCancellationRequested();
            stage = StudyWorkerStage.ApplyingPretension;
            Report(stage, StudyIterationStatus.ApplyingPretension,
                $"Applying {job.Pretension.Id}: {job.Pretension.Assignments.Count} cable target-force assignments.", childInfo.Path);
            foreach (var assignment in job.Pretension.Assignments)
            {
                token.ThrowIfCancellationRequested();
                await gateway.SetCableTargetForceAsync(
                    assignment.CableName,
                    assignment.LoadPattern,
                    assignment.TargetForce,
                    childInfo);
            }

            token.ThrowIfCancellationRequested();
            stage = StudyWorkerStage.Saving;
            Report(stage, StudyIterationStatus.Saving, "Saving child model before analysis.", childInfo.Path);
            await gateway.SaveAsync(childInfo);

            // Analyze.RunAnalysis persists the analyzed model and its result files (.Y* / .OUT / .LOG …) next to
            // the child .sdb by itself. Do NOT File.Save() again or re-OpenFile() the child afterwards: SAP2000
            // then treats that instance as modified-after-analysis and deletes its result files the next time the
            // worker switches models (opens the baseline for the next candidate).
            token.ThrowIfCancellationRequested();
            stage = StudyWorkerStage.RunningAnalysis;
            Report(stage, StudyIterationStatus.RunningAnalysis,
                "Running the child model's active SAP2000 analysis cases.", childInfo.Path);
            await gateway.RunAnalysisAsync(childInfo);
            AppendDiagnostic(job, workerId, $"ANALYZED child={childInfo.Path}");

            // Verify the just-analyzed model in place. It must be locked (results computed) and still hold the
            // resolved height / loading / pretension assignments.
            token.ThrowIfCancellationRequested();
            stage = StudyWorkerStage.Verifying;
            Report(stage, StudyIterationStatus.Verifying, "Verifying the analyzed child model.", childInfo.Path);
            var analyzedChild = await gateway.GetCurrentModelInfoAsync();
            if (!analyzedChild.IsLocked)
                throw new InvalidOperationException(
                    $"The analyzed child model is unlocked, so its analysis results were not produced: {job.ModelFullPath}. " +
                    "Confirm the baseline has at least one load case set to run.");
            var verified = await verifier.VerifyAsync(gateway, snapshot, job, proposed, analyzedChild, token);
            AppendDiagnostic(job, workerId,
                $"VERIFIED geometryPoints={verified.GeometryPointsVerified},pointLoads={verified.PointLoadsVerified}," +
                $"cableTargets={verified.CableTargetForcesVerified},active={analyzedChild.Path}");
            return analyzedChild;

            void Report(StudyWorkerStage nextStage, StudyIterationStatus status, string message, string? path)
            {
                report(nextStage, status, message, path);
                AppendDiagnostic(job, workerId, $"STAGE {nextStage}: {message} path={path ?? "(none)"}");
            }
        }
        catch (OperationCanceledException)
        {
            if (Directory.Exists(job.IterationDirectory))
                AppendDiagnostic(job, workerId, $"CANCELLED stage={stage}");
            throw;
        }
        catch (Exception ex)
        {
            var detail = $"Iteration {job.IterationId} failed on Worker {workerId} during {stage}. " +
                $"Candidates L={job.LoadingCandidateId}, H={job.HeightCandidateId}, P={job.PretensionCandidateId}. {ex.Message}";
            if (Directory.Exists(job.IterationDirectory))
                AppendDiagnostic(job, workerId, $"FAILED stage={stage}: {ex}");
            throw new InvalidOperationException(detail, ex);
        }
    }

    private static void AppendDiagnostic(StudyIterationExecutionDto job, int workerId, string message)
    {
        var logPath = Path.Combine(job.IterationDirectory, "build.log");
        File.AppendAllText(logPath,
            $"{DateTimeOffset.Now:O} [Worker {workerId}] [{job.IterationId}] {message}{Environment.NewLine}");
    }

    private static void VerifyBaseline(ModelInfo expected, ModelInfo actual)
    {
        if (!SamePath(expected.Path, actual.Path))
            throw new InvalidOperationException($"Worker opened the wrong baseline model. Expected {expected.Path}; active {actual.Path}.");
        if (expected.UnitsCode != actual.UnitsCode)
            throw new InvalidOperationException($"Worker baseline units differ from the controller ({expected.Units} vs {actual.Units}).");
    }

    private static bool SamePath(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
    }
}
