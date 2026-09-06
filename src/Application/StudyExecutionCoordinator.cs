using COT_VCF.Application.Workers;
using System.IO;

namespace COT_VCF.Application;

public sealed class StudyExecutionCoordinator(ISapInstanceHostFactory hostFactory)
{
    private readonly StudyPathService paths = new();

    public async Task<StudyExecutionResult> BuildAsync(
        StudyExecutionSnapshot snapshot,
        IProgress<StudyExecutionProgress> progress,
        CancellationToken token)
    {
        ValidateSnapshot(snapshot);
        await using var pool = new SapWorkerPool(hostFactory);
        Exception? executionError = null;
        try
        {
            await pool.StartAsync(snapshot, progress, token);
            return await pool.ExecuteAsync(progress, token);
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
    }

    private void ValidateSnapshot(StudyExecutionSnapshot snapshot)
    {
        if (snapshot.RequestedWorkerCount is < 1 or > 3)
            throw new InvalidOperationException("Concurrent study workers must be between 1 and 3.");
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
            if (File.Exists(expectedModel))
                throw new InvalidOperationException($"Existing output will not be overwritten: {expectedModel}");
            if (Directory.Exists(expectedDirectory) && Directory.EnumerateFileSystemEntries(expectedDirectory).Any())
                throw new InvalidOperationException($"Existing iteration folder is not empty and will not be overwritten: {expectedDirectory}");
        }
    }
}
