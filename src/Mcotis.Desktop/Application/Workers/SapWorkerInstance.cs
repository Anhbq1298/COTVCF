using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Domain.Results;
using System.IO;

namespace Mcotis.Desktop.Application.Workers;

public sealed record SapWorkerJobResult(
    StudyIterationStatus Status,
    bool WorkerFaulted,
    string Message,
    string? ModelPath,
    CandidateResultDto? Result = null);

/// <summary>One sequential execution lane. Its host owns a unique SAP2000 process and STA COM graph.</summary>
public sealed class SapWorkerInstance : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ISapInstanceHost host;
    private readonly IterationModelBuilder builder;
    private readonly StudyExecutionSnapshot snapshot;
    private bool started;

    public SapWorkerInstance(
        int workerId,
        ISapInstanceHost host,
        IterationModelBuilder builder,
        StudyExecutionSnapshot snapshot)
    {
        WorkerId = workerId;
        this.host = host;
        this.builder = builder;
        this.snapshot = snapshot;
        Snapshot = new SapWorkerSnapshot { WorkerId = workerId };
    }

    public int WorkerId { get; }
    public SapWorkerSnapshot Snapshot { get; }

    public async Task StartAsync(
        Action<SapWorkerState, StudyWorkerStage, string, string?, string?> report,
        CancellationToken token)
    {
        SetState(SapWorkerState.Starting, StudyWorkerStage.Waiting, "Starting SAP2000 worker.", null, snapshot.BaselineModelPath, report);
        try
        {
            var info = await host.StartAsync(snapshot.BaselineModelPath, token);
            if (info.UnitsCode != snapshot.BaselineModel.UnitsCode)
                throw new InvalidOperationException($"Worker {WorkerId} units differ from the controller baseline.");
            if (!string.Equals(info.Version, snapshot.BaselineModel.Version, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Worker {WorkerId} SAP2000 version {info.Version} differs from the controller baseline version {snapshot.BaselineModel.Version}.");
            started = true;
            SetState(SapWorkerState.Ready, StudyWorkerStage.Waiting, "Ready.", null, info.Path, report);
        }
        catch (Exception ex)
        {
            SetState(SapWorkerState.Faulted, StudyWorkerStage.Waiting, ex.Message, null, null, report);
            throw;
        }
    }

    public async Task<SapWorkerJobResult> ExecuteAsync(
        StudyIterationExecutionDto job,
        Action<SapWorkerState, StudyWorkerStage, StudyIterationStatus?, string, string?> report,
        CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!started || Snapshot.State == SapWorkerState.Faulted)
                return new(StudyIterationStatus.Failed, true, $"Worker {WorkerId} is not available.", null);

            var reuse = File.Exists(job.ModelFullPath);
            SetState(SapWorkerState.Busy,
                reuse ? StudyWorkerStage.ReusingExisting : StudyWorkerStage.PreparingFolder,
                reuse ? StudyIterationStatus.ReusingExisting : StudyIterationStatus.PreparingFolder,
                reuse ? "Existing child model found — reusing." : "Candidate accepted.",
                job.IterationId, job.ModelFullPath, report);
            try
            {
                Action<StudyWorkerStage, StudyIterationStatus, string, string?> stageReport =
                    (stage, status, message, path) => SetState(SapWorkerState.Busy, stage, status, message, job.IterationId, path, report);
                var built = reuse
                    ? await builder.ReuseAsync(host.Gateway, snapshot, job, WorkerId, stageReport, token)
                    : await builder.BuildAsync(host.Gateway, snapshot, job, WorkerId, stageReport, token);
                await ReturnToBaselineAsync(job, report);
                Snapshot.CompletedCount++;
                SetState(SapWorkerState.Ready, StudyWorkerStage.Waiting, null,
                    "Ready for next candidate.", null, snapshot.BaselineModelPath, report);
                return built.Reused
                    ? new(StudyIterationStatus.Reused, false, "Existing child model reused — results re-extracted.", built.Child.Path, built.Result)
                    : new(StudyIterationStatus.Ready, false, "Child model is ready.", built.Child.Path, built.Result);
            }
            catch (OperationCanceledException)
            {
                var recoveryError = await TryReturnToBaselineAsync(job, report);
                if (recoveryError is not null)
                {
                    SetState(SapWorkerState.Faulted, StudyWorkerStage.ReturningToBaseline, null,
                        recoveryError, job.IterationId, null, report);
                    return new(StudyIterationStatus.Cancelled, true, recoveryError, null);
                }
                SetState(SapWorkerState.Ready, StudyWorkerStage.Waiting, null,
                    "Cancellation boundary reached.", null, snapshot.BaselineModelPath, report);
                return new(StudyIterationStatus.Cancelled, false, "Cancelled at a safe SAP2000 stage boundary.", null);
            }
            catch (Exception ex)
            {
                var recoveryError = await TryReturnToBaselineAsync(job, report);
                if (recoveryError is not null)
                {
                    var message = $"{ex.Message} Worker recovery also failed: {recoveryError}";
                    SetState(SapWorkerState.Faulted, StudyWorkerStage.ReturningToBaseline, null,
                        message, job.IterationId, null, report);
                    return new(StudyIterationStatus.Failed, true, message, null);
                }
                SetState(SapWorkerState.Ready, StudyWorkerStage.Waiting, null,
                    "Candidate failed; baseline restored.", null, snapshot.BaselineModelPath, report);
                return new(StudyIterationStatus.Failed, false, ex.Message, null);
            }
        }
        finally { gate.Release(); }
    }

    private async Task ReturnToBaselineAsync(
        StudyIterationExecutionDto job,
        Action<SapWorkerState, StudyWorkerStage, StudyIterationStatus?, string, string?> report)
    {
        SetState(SapWorkerState.ReturningToBaseline, StudyWorkerStage.ReturningToBaseline,
            null, "Returning to immutable baseline.", job.IterationId, snapshot.BaselineModelPath, report);
        var baseline = await host.Gateway.OpenModelAsync(snapshot.BaselineModelPath);
        if (!SamePath(baseline.Path, snapshot.BaselineModelPath) || baseline.UnitsCode != snapshot.BaselineModel.UnitsCode)
            throw new InvalidOperationException("Worker did not return to the verified baseline model.");
    }

    private async Task<string?> TryReturnToBaselineAsync(
        StudyIterationExecutionDto job,
        Action<SapWorkerState, StudyWorkerStage, StudyIterationStatus?, string, string?> report)
    {
        try { await ReturnToBaselineAsync(job, report); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    private void SetState(
        SapWorkerState state,
        StudyWorkerStage stage,
        string message,
        string? iterationId,
        string? modelPath,
        Action<SapWorkerState, StudyWorkerStage, string, string?, string?> report)
    {
        Snapshot.State = state;
        Snapshot.CurrentStage = stage;
        Snapshot.LastMessage = message;
        Snapshot.CurrentIterationId = iterationId;
        Snapshot.CurrentModelPath = modelPath;
        report(state, stage, message, iterationId, modelPath);
    }

    private void SetState(
        SapWorkerState state,
        StudyWorkerStage stage,
        StudyIterationStatus? iterationStatus,
        string message,
        string? iterationId,
        string? modelPath,
        Action<SapWorkerState, StudyWorkerStage, StudyIterationStatus?, string, string?> report)
    {
        Snapshot.State = state;
        Snapshot.CurrentStage = stage;
        Snapshot.LastMessage = message;
        Snapshot.CurrentIterationId = iterationId;
        Snapshot.CurrentModelPath = modelPath;
        report(state, stage, iterationStatus, message, modelPath);
    }

    private static bool SamePath(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        gate.Dispose();
        await host.DisposeAsync();
    }
}
