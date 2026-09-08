using COT_VCF.Application.Results;
using COT_VCF.Application.Workers;
using COT_VCF.Domain.Results;
using System.IO;
using System.Collections.Concurrent;

namespace COT_VCF.Application;

public sealed class StudyExecutionCoordinator
{
    private readonly ISapInstanceHostFactory hostFactory;
    private readonly IStudyArtifactPublisher artifactPublisher;
    private readonly StudyPathService paths = new();

    public StudyExecutionCoordinator(ISapInstanceHostFactory hostFactory, IStudyArtifactPublisher? artifactPublisher = null)
    {
        this.hostFactory = hostFactory;
        this.artifactPublisher = artifactPublisher ?? new StudyArtifactPublisher();
    }

    /// <summary>Absolute path of the last <c>Deflection_Report.html</c> written by a run, or null.</summary>
    public string? LastDeflectionReportPath { get; private set; }
    /// <summary>Absolute path of the last <c>Cable_Forces_Report.html</c> written by a run, or null.</summary>
    public string? LastCableForceReportPath { get; private set; }
    public StudyArtifactOutcome LastArtifactOutcome { get; private set; } = StudyArtifactOutcome.NotProduced;
    public StudyRunReference? LastRunReference { get; private set; }

    public async Task<StudyExecutionResult> BuildAsync(
        StudyExecutionSnapshot snapshot,
        IProgress<StudyExecutionProgress> progress,
        CancellationToken token,
        Action<CandidateResultDto>? onCandidateResult = null,
        StudyRunIntent intent = StudyRunIntent.AllSelected,
        string? parentRunId = null)
    {
        ValidateSnapshot(snapshot);
        LastDeflectionReportPath = null;
        LastCableForceReportPath = null;
        LastArtifactOutcome = StudyArtifactOutcome.NotProduced;
        LastRunReference = null;
        var journal = StudyRunJournal.Start(snapshot, intent, parentRunId);
        LastRunReference = journal.Reference();
        var trackedProgress = new ForwardingProgress<StudyExecutionProgress>(item =>
        {
            var tracked = item with { RunId = journal.RunId, ManifestPath = journal.ManifestPath };
            journal.Report(tracked);
            progress.Report(tracked);
        });
        var candidateResults = new ConcurrentBag<CandidateResultDto>();
        var resultStore = new CandidateResultStore(snapshot);
        await using var pool = new SapWorkerPool(hostFactory, onCandidateResult: dto =>
        {
            candidateResults.Add(dto);
            try { resultStore.Write(dto); }
            catch { /* The in-memory result can still be consolidated for this run. */ }
            onCandidateResult?.Invoke(dto);
        });
        try
        {
            if (intent == StudyRunIntent.RebuildSelected)
                _ = new StudyRebuildArchive().Archive(snapshot);
            Exception? executionError = null;
            StudyExecutionResult result;
            try
            {
                await pool.StartAsync(snapshot, trackedProgress, token);
                result = await pool.ExecuteAsync(trackedProgress, token);
            }
            catch (Exception ex)
            {
                executionError = ex;
                throw;
            }
            finally
            {
                try { await pool.StopAsync(trackedProgress); }
                catch when (executionError is not null) { }
            }

            var artifactResults = ResolveArtifactResults(snapshot, candidateResults, resultStore, intent, parentRunId);
            if (artifactResults.Count > 0)
            {
                try
                {
                    LastArtifactOutcome = artifactPublisher.Publish(snapshot, artifactResults);
                    LastDeflectionReportPath = LastArtifactOutcome.DeflectionReportPath;
                    LastCableForceReportPath = LastArtifactOutcome.CableForceReportPath;
                }
                catch (Exception ex)
                {
                    LastArtifactOutcome = StudyArtifactOutcome.Failed(ex.Message);
                    trackedProgress.Report(new StudyExecutionProgress { WorkerId = 0, Message = $"Result consolidation failed: {ex.Message}" });
                }
            }
            else if (result.Completed > 0)
            {
                LastArtifactOutcome = StudyArtifactOutcome.Failed("Completed candidates returned no extractable result payload.");
            }
            var completed = result with { Artifacts = LastArtifactOutcome };
            var run = journal.Complete(completed);
            LastRunReference = run;
            return completed with { Run = run };
        }
        catch (Exception ex)
        {
            LastRunReference = journal.Fail(ex);
            throw;
        }
    }

    private sealed class ForwardingProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static IReadOnlyList<CandidateResultDto> ResolveArtifactResults(
        StudyExecutionSnapshot snapshot,
        IEnumerable<CandidateResultDto> currentResults,
        CandidateResultStore resultStore,
        StudyRunIntent intent,
        string? parentRunId)
    {
        var current = currentResults.ToArray();
        if (intent is not (StudyRunIntent.ResumeInterrupted or StudyRunIntent.RetryFailed))
            return current.OrderBy(item => item.Parameters.RunIndex).ToArray();

        var parent = StudyRunJournal.TryReadCompatibleRun(snapshot, parentRunId);
        if (parent is null) return current.OrderBy(item => item.Parameters.RunIndex).ToArray();
        var included = parent.Candidates.Where(candidate => candidate.Selected)
            .Select(candidate => candidate.IterationId).ToHashSet(StringComparer.Ordinal);
        var jobs = snapshot.Jobs.ToDictionary(job => job.IterationId, StringComparer.Ordinal);
        var merged = new Dictionary<string, CandidateResultDto>(StringComparer.Ordinal);

        var workbook = StudyRunJournal.ValidArtifactPath(parent, "Workbook");
        if (!string.IsNullOrWhiteSpace(workbook))
        {
            try
            {
                foreach (var result in ConsolidatedResultWriter.Read(workbook))
                    if (included.Contains(result.IterationKey) && MatchesSnapshot(result, jobs, snapshot.BaselineModelPath))
                        merged[result.IterationKey] = result;
            }
            catch { /* Per-candidate sidecars and current results remain available. */ }
        }
        foreach (var result in resultStore.Read(included)) merged[result.IterationKey] = result;
        foreach (var result in current.Where(result => included.Contains(result.IterationKey))) merged[result.IterationKey] = result;
        return merged.Values.OrderBy(item => item.Parameters.RunIndex).ToArray();
    }

    private static bool MatchesSnapshot(
        CandidateResultDto result,
        IReadOnlyDictionary<string, StudyIterationExecutionDto> jobs,
        string baselineModelPath) =>
        jobs.TryGetValue(result.IterationKey, out var job) &&
        Path.GetFullPath(result.CandidateModelPath).Equals(Path.GetFullPath(job.ModelFullPath), StringComparison.OrdinalIgnoreCase) &&
        Path.GetFullPath(result.BaselineModelPath).Equals(Path.GetFullPath(baselineModelPath), StringComparison.OrdinalIgnoreCase);

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
