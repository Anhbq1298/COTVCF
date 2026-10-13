using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Application.Workers;

public interface ISapWorkerPool : IAsyncDisposable
{
    IReadOnlyList<SapWorkerSnapshot> Workers { get; }
    Task StartAsync(StudyExecutionSnapshot snapshot, IProgress<StudyExecutionProgress> progress, CancellationToken token);
    Task<StudyExecutionResult> ExecuteAsync(IProgress<StudyExecutionProgress> progress, CancellationToken token);
    Task StopAsync(IProgress<StudyExecutionProgress>? progress = null);
}
