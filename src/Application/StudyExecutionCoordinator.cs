using COT_VCF.Application.Results;
using COT_VCF.Application.Workers;
using COT_VCF.Domain.Results;
using System.IO;

namespace COT_VCF.Application;

public sealed class StudyExecutionCoordinator(ISapInstanceHostFactory hostFactory)
{
    private readonly StudyPathService paths = new();

    /// <summary>Absolute path of the last <c>Deflection_Report.html</c> written by a run, or null.</summary>
    public string? LastDeflectionReportPath { get; private set; }
    /// <summary>Absolute path of the last <c>Cable_Forces_Report.html</c> written by a run, or null.</summary>
    public string? LastCableForceReportPath { get; private set; }

    public async Task<StudyExecutionResult> BuildAsync(
        StudyExecutionSnapshot snapshot,
        IProgress<StudyExecutionProgress> progress,
        CancellationToken token,
        Action<CandidateResultDto>? onCandidateResult = null)
    {
        ValidateSnapshot(snapshot);
        var consolidator = new ConsolidatedResultWriter();
        await using var pool = new SapWorkerPool(hostFactory, onCandidateResult: dto =>
        {
            consolidator.Add(dto);
            onCandidateResult?.Invoke(dto);
        });
        Exception? executionError = null;
        StudyExecutionResult result;
        try
        {
            await pool.StartAsync(snapshot, progress, token);
            result = await pool.ExecuteAsync(progress, token);
        }
        catch (Exception ex)
        {
            executionError = ex;
            throw;
        }
        finally
        {
            try { await pool.StopAsync(progress); }
            catch when (executionError is not null) { }
        }

        // Consolidate the deflection results — best-effort; a completed study is never failed by this.
        if (consolidator.Count > 0)
        {
            try
            {
                var studyFolder = Path.GetFullPath(paths.GetStudyRoot(snapshot.BaselineModelPath));
                consolidator.Write(studyFolder, snapshot.DeflectionPoints, snapshot.DeflectionOutputCase,
                    snapshot.BaselineModel.Name, snapshot.BaselineModelPath, snapshot.StudyMode);
                LastDeflectionReportPath = new DeflectionReportGenerator().Generate(
                    studyFolder, consolidator.Snapshot(),
                    snapshot.BaselineModel.Name, snapshot.BaselineModelPath, snapshot.StudyMode);
                if (consolidator.Snapshot().Any(r => (r.CableForces?.Count ?? 0) > 0))
                    LastCableForceReportPath = new CableForceReportGenerator().Generate(
                        studyFolder, consolidator.Snapshot(),
                        snapshot.BaselineModel.Name, snapshot.StudyMode, snapshot.CableResultLoadCase);
            }
            catch (Exception ex)
            {
                progress.Report(new StudyExecutionProgress { WorkerId = 0, Message = $"Result consolidation failed: {ex.Message}" });
            }
        }
        return result;
    }

    private void ValidateSnapshot(StudyExecutionSnapshot snapshot)
    {
        if (snapshot.RequestedWorkerCount is < 1 or > 3)
            throw new InvalidOperationException("Total SAP2000 instances must be between 1 and 3.");
        if (!File.Exists(snapshot.BaselineModelPath))
            throw new FileNotFoundException("The immutable baseline SAP2000 model no longer exists.", snapshot.BaselineModelPath);
        if (snapshot.Jobs.Count(job => job.RunIterate) == 0)
            throw new InvalidOperationException("Select at least one study iteration to build.");

        var expectedRoot = Path.GetFullPath(paths.GetStudyRoot(snapshot.BaselineModelPath));
        var selected = snapshot.Jobs.Where(job => job.RunIterate).ToArray();
        if (selected.Select(job => job.ModelFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length)
            throw new InvalidOperationException("The frozen study contains duplicate child model paths.");
        foreach (var job in selected)
        {
            var expectedDirectory = Path.GetFullPath(paths.GetIterationDirectory(snapshot.BaselineModelPath, job.IterationId));
            var expectedModel = Path.GetFullPath(paths.GetIterationModelPath(snapshot.BaselineModelPath, job.IterationId));
            if (!string.Equals(expectedDirectory, Path.GetFullPath(job.IterationDirectory), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(expectedModel, Path.GetFullPath(job.ModelFullPath), StringComparison.OrdinalIgnoreCase) ||
                !expectedDirectory.StartsWith(expectedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Iteration {job.IterationId} has an invalid or unsafe output path.");
            // An existing child model is NOT an error: the worker reuses it (opens, verifies, re-extracts
            // results) instead of rebuilding. Study modes overlap on their fixed dimension by design, so
            // re-running a neighbouring study must not require deleting the shared candidates first. The
            // existing .sdb and its result files are never overwritten.
        }
    }
}
