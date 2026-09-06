using System.Collections.Concurrent;
using System.Threading.Channels;
using COT_VCF.Domain;

namespace COT_VCF.Application.Workers;

/// <summary>A central next-available-worker queue feeding 1..3 sequential SAP lanes.</summary>
public sealed class SapWorkerPool(
    ISapInstanceHostFactory hostFactory,
    IterationModelBuilder? builder = null) : ISapWorkerPool
{
    private readonly IterationModelBuilder builder = builder ?? new();
    private readonly List<SapWorkerInstance> workers = [];
    private StudyExecutionSnapshot? snapshot;
    private int total, completed, failed, running, queued, skipped, cancelled, peakConcurrent;
    private bool stopped;

    public IReadOnlyList<SapWorkerSnapshot> Workers => workers.Select(worker => worker.Snapshot).ToArray();

    public async Task StartAsync(
        StudyExecutionSnapshot snapshot,
        IProgress<StudyExecutionProgress> progress,
        CancellationToken token)
    {
        if (this.snapshot is not null) throw new InvalidOperationException("The SAP study worker pool has already started.");
        if (snapshot.RequestedWorkerCount is < 1 or > 3)
            throw new InvalidOperationException("Total SAP2000 instances must be between 1 and 3.");
        this.snapshot = snapshot;

        for (int id = 1; id <= snapshot.RequestedWorkerCount; id++)
        {
            var worker = new SapWorkerInstance(id, hostFactory.Create(id), builder, snapshot);
            workers.Add(worker);
            try
            {
                await worker.StartAsync(
                    (state, stage, message, iterationId, path) =>
                        Report(progress, id, iterationId, stage, null, state, path, worker.Snapshot.CompletedCount, message),
                    token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await StopAsync(progress);
                throw;
            }
            catch (Exception ex)
            {
                await StopAsync(progress);
                throw new InvalidOperationException(
                    $"Requested workers: {snapshot.RequestedWorkerCount}. Started successfully: {id - 1}. " +
                    $"Instance {id} failed: {ex.Message} Study execution has not started. Reduce Total SAP2000 Instances and retry.", ex);
            }
        }
    }

    public async Task<StudyExecutionResult> ExecuteAsync(
        IProgress<StudyExecutionProgress> progress,
        CancellationToken token)
    {
        var frozen = snapshot ?? throw new InvalidOperationException("Start the SAP study worker pool before dispatching jobs.");
        var selected = frozen.Jobs.Where(job => job.RunIterate).ToArray();
        total = selected.Length;
        queued = selected.Length;
        skipped = frozen.Jobs.Count - selected.Length;
        if (selected.Length == 0) throw new InvalidOperationException("Select at least one study iteration to build.");

        var channel = Channel.CreateUnbounded<StudyIterationExecutionDto>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        foreach (var job in selected) await channel.Writer.WriteAsync(job, token);
        channel.Writer.TryComplete();

        var claimed = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var tasks = workers.Select(worker => ConsumeAsync(worker, channel.Reader, claimed, progress, token)).ToArray();
        await Task.WhenAll(tasks);

        var unclaimed = selected.Where(job => !claimed.ContainsKey(job.IterationId)).ToArray();
        foreach (var job in unclaimed)
        {
            Interlocked.Decrement(ref queued);
            if (token.IsCancellationRequested)
            {
                Interlocked.Increment(ref cancelled);
                Report(progress, 0, job.IterationId, StudyWorkerStage.Waiting, StudyIterationStatus.Cancelled,
                    SapWorkerState.Offline, job.ModelFullPath, 0, "Cancelled before worker assignment.");
            }
            else
            {
                Interlocked.Increment(ref failed);
                Report(progress, 0, job.IterationId, StudyWorkerStage.Waiting, StudyIterationStatus.Failed,
                    SapWorkerState.Faulted, job.ModelFullPath, 0, "No healthy SAP2000 worker remained for this candidate.");
            }
        }

        return new(total, completed, failed, cancelled, skipped, workers.Count, peakConcurrent,
            workers.Count > 0 && workers.All(worker => worker.Snapshot.State == SapWorkerState.Faulted));
    }

    private async Task ConsumeAsync(
        SapWorkerInstance worker,
        ChannelReader<StudyIterationExecutionDto> reader,
        ConcurrentDictionary<string, byte> claimed,
        IProgress<StudyExecutionProgress> progress,
        CancellationToken token)
    {
        try
        {
            await foreach (var job in reader.ReadAllAsync(token))
            {
                if (!claimed.TryAdd(job.IterationId, 0)) continue;
                Interlocked.Decrement(ref queued);
                var active = Interlocked.Increment(ref running);
                UpdatePeak(active);
                Report(progress, worker.WorkerId, job.IterationId, StudyWorkerStage.PreparingFolder,
                    StudyIterationStatus.PreparingFolder, SapWorkerState.Busy, job.ModelFullPath,
                    worker.Snapshot.CompletedCount, "Candidate assigned.");

                SapWorkerJobResult result;
                try
                {
                    result = await worker.ExecuteAsync(job,
                        (state, stage, iterationStatus, message, path) =>
                            Report(progress, worker.WorkerId, job.IterationId, stage, iterationStatus, state,
                                path, worker.Snapshot.CompletedCount, message), token);
                }
                catch (OperationCanceledException)
                {
                    result = new(StudyIterationStatus.Cancelled, false,
                        "Cancelled before the worker gate was acquired.", null);
                }

                Interlocked.Decrement(ref running);
                switch (result.Status)
                {
                    case StudyIterationStatus.Ready: Interlocked.Increment(ref completed); break;
                    case StudyIterationStatus.Cancelled: Interlocked.Increment(ref cancelled); break;
                    default: Interlocked.Increment(ref failed); break;
                }
                Report(progress, worker.WorkerId, job.IterationId,
                    result.Status == StudyIterationStatus.Ready ? StudyWorkerStage.Completed : StudyWorkerStage.Waiting,
                    result.Status, result.WorkerFaulted ? SapWorkerState.Faulted : worker.Snapshot.State,
                    result.ModelPath ?? job.ModelFullPath, worker.Snapshot.CompletedCount, result.Message);
                if (result.WorkerFaulted) break;
                if (token.IsCancellationRequested) break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public async Task StopAsync(IProgress<StudyExecutionProgress>? progress = null)
    {
        if (stopped) return;
        stopped = true;
        List<Exception> failures = [];
        foreach (var worker in workers)
        {
            var wasFaulted = worker.Snapshot.State == SapWorkerState.Faulted;
            progress?.Report(CreateProgress(worker.WorkerId, worker.Snapshot.CurrentIterationId,
                StudyWorkerStage.Waiting, null, SapWorkerState.Stopping, worker.Snapshot.CurrentModelPath,
                worker.Snapshot.CompletedCount, "Stopping SAP2000 worker."));
            Exception? stopFailure = null;
            try { await worker.DisposeAsync(); }
            catch (Exception ex) { stopFailure = ex; failures.Add(ex); }
            progress?.Report(CreateProgress(worker.WorkerId, null, StudyWorkerStage.Waiting, null,
                stopFailure is null && !wasFaulted ? SapWorkerState.Offline : SapWorkerState.Faulted, null,
                worker.Snapshot.CompletedCount,
                stopFailure is not null ? stopFailure.Message : wasFaulted ? worker.Snapshot.LastMessage ?? "Worker faulted." : "Stopped."));
        }
        if (failures.Count > 0) throw new AggregateException("One or more SAP2000 workers could not be stopped cleanly.", failures);
    }

    private void Report(
        IProgress<StudyExecutionProgress> progress,
        int workerId,
        string? iterationId,
        StudyWorkerStage stage,
        StudyIterationStatus? iterationStatus,
        SapWorkerState workerState,
        string? path,
        int workerCompleted,
        string message) => progress.Report(CreateProgress(workerId, iterationId, stage, iterationStatus,
            workerState, path, workerCompleted, message));

    private StudyExecutionProgress CreateProgress(
        int workerId,
        string? iterationId,
        StudyWorkerStage stage,
        StudyIterationStatus? iterationStatus,
        SapWorkerState workerState,
        string? path,
        int workerCompleted,
        string message) => new()
    {
        Total = Volatile.Read(ref total),
        Completed = Volatile.Read(ref completed),
        Failed = Volatile.Read(ref failed),
        Cancelled = Volatile.Read(ref cancelled),
        Running = Volatile.Read(ref running),
        Queued = Math.Max(0, Volatile.Read(ref queued)),
        Skipped = Volatile.Read(ref skipped),
        WorkerId = workerId,
        IterationId = iterationId,
        Stage = stage,
        IterationStatus = iterationStatus,
        WorkerState = workerState,
        CurrentModelPath = path,
        WorkerCompletedCount = workerCompleted,
        Message = message
    };

    private void UpdatePeak(int active)
    {
        while (true)
        {
            var current = Volatile.Read(ref peakConcurrent);
            if (active <= current || Interlocked.CompareExchange(ref peakConcurrent, active, current) == current) return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!stopped) await StopAsync();
    }
}
