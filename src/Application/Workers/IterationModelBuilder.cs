using COT_VCF.Domain;
using COT_VCF.Geometry;
using System.IO;

namespace COT_VCF.Application.Workers;

public sealed class IterationModelBuilder(TrussGeometryService? geometryService = null)
{
    private readonly TrussGeometryService geometryService = geometryService ?? new();

    public async Task<ModelInfo> BuildAsync(
        ISapGateway gateway,
        StudyExecutionSnapshot snapshot,
        StudyIterationExecutionDto job,
        Action<StudyWorkerStage, StudyIterationStatus, string, string?> report,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        report(StudyWorkerStage.PreparingFolder, StudyIterationStatus.PreparingFolder, "Preparing candidate folder.", null);
        if (File.Exists(job.ModelFullPath))
            throw new InvalidOperationException($"Existing output will not be overwritten: {job.ModelFullPath}");
        Directory.CreateDirectory(job.IterationDirectory);

        token.ThrowIfCancellationRequested();
        report(StudyWorkerStage.OpeningBaseline, StudyIterationStatus.OpeningBaseline, "Opening immutable baseline.", snapshot.BaselineModelPath);
        var baselineInfo = await gateway.OpenModelAsync(snapshot.BaselineModelPath);
        VerifyBaseline(snapshot.BaselineModel, baselineInfo);

        token.ThrowIfCancellationRequested();
        report(StudyWorkerStage.CreatingChild, StudyIterationStatus.CreatingChild, "Creating isolated child model.", job.ModelFullPath);
        var childInfo = await gateway.SaveAsAsync(job.ModelFullPath, baselineInfo);

        token.ThrowIfCancellationRequested();
        report(StudyWorkerStage.ApplyingGeometry, StudyIterationStatus.ApplyingGeometry, "Applying truss-height candidate.", childInfo.Path);
        var proposed = geometryService.BuildCandidateGeometry(
            snapshot.BaselineGeometry,
            snapshot.TopPoints,
            snapshot.MiddlePoints,
            snapshot.BottomPoints,
            job.Height);
        await gateway.MovePointsAsync(snapshot.BaselineGeometry, proposed, childInfo);

        token.ThrowIfCancellationRequested();
        report(StudyWorkerStage.ApplyingLoading, StudyIterationStatus.ApplyingLoading, "Applying loading candidate.", childInfo.Path);
        await gateway.ApplyLoadsAsync(job.Loading.Loads, childInfo);

        token.ThrowIfCancellationRequested();
        report(StudyWorkerStage.ApplyingPretension, StudyIterationStatus.ApplyingPretension, "Applying cable pretension candidate.", childInfo.Path);
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
        report(StudyWorkerStage.Saving, StudyIterationStatus.Saving, "Saving child model.", childInfo.Path);
        await gateway.SaveAsync(childInfo);
        var active = await gateway.GetCurrentModelInfoAsync();
        if (!SamePath(active.Path, job.ModelFullPath))
            throw new InvalidOperationException($"Child verification failed. Expected {job.ModelFullPath}; active {active.Path}.");
        if (!File.Exists(job.ModelFullPath))
            throw new InvalidOperationException($"SAP2000 reported a successful save, but the child model was not found: {job.ModelFullPath}");
        return active;
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
