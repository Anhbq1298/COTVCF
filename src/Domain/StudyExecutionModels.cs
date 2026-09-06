using System.Collections.ObjectModel;

namespace COT_VCF.Domain;

/// <summary>One Cartesian loading choice: one qz value for every configured load pattern.</summary>
public sealed record LoadingCombination(long Index, IReadOnlyList<(string Name, double Qz)> Loads);

/// <summary>One complete loading candidate, including every point-load assignment written to SAP2000.</summary>
public sealed record LoadingStudyCandidate
{
    public required string Id { get; init; }
    public required LoadingCombination Combination { get; init; }
    public required IReadOnlyList<LoadingCandidate> PointLoads { get; init; }
    public required IReadOnlyList<PointLoad> Loads { get; init; }
    public string DisplayName => $"{Id} - {Combination.Loads.Count} load definition{(Combination.Loads.Count == 1 ? "" : "s")}";
}

public enum StudyIterationStatus
{
    Planned,
    Queued,
    Skipped,
    PreparingFolder,
    OpeningBaseline,
    CreatingChild,
    ApplyingGeometry,
    ApplyingLoading,
    ApplyingPretension,
    Saving,
    Ready,
    Failed,
    Cancelled
}

public enum StudyWorkerStage
{
    Waiting,
    PreparingFolder,
    OpeningBaseline,
    CreatingChild,
    ApplyingGeometry,
    ApplyingLoading,
    ApplyingPretension,
    Saving,
    ReturningToBaseline,
    Completed
}

public enum SapWorkerState
{
    Offline,
    Starting,
    Ready,
    Busy,
    ReturningToBaseline,
    Faulted,
    Stopping
}

public enum StudyBuildState
{
    ReadyToBuild,
    Running,
    Completed,
    CompletedWithErrors,
    Cancelled,
    Failed
}

/// <summary>A deterministic Tab 05 plan row which acquires execution state only after a worker accepts it.</summary>
public sealed class StudyIteration : Observable
{
    private bool runIterate;
    private StudyIterationStatus status = StudyIterationStatus.Planned;
    private int? assignedWorkerId;
    private string? currentStageText;
    private string? errorMessage;

    public required long RunIndex { get; init; }
    public required string IterationId { get; init; }
    public required string LoadingCandidateId { get; init; }
    public required string HeightCandidateId { get; init; }
    public required string PretensionCandidateId { get; init; }
    public required LoadingStudyCandidate LoadingCandidate { get; init; }
    public required TrussCandidate HeightCandidate { get; init; }
    public required PretensionCombination PretensionCandidate { get; init; }
    public required string IterationDirectory { get; init; }
    public required string ModelName { get; init; }
    public required string ModelFullPath { get; init; }
    public required string Description { get; init; }
    public bool IsValid { get; init; } = true;
    public string ValidationMessage { get; init; } = "";

    // Compatibility aliases retained for the existing composer/UI vocabulary.
    public string IterationKey => IterationId;
    public string SapModelName => ModelName;
    public string OutputDirectory => IterationDirectory;
    public string FullOutputPath => ModelFullPath;

    public bool RunIterate { get => runIterate; set => Set(ref runIterate, value); }
    public StudyIterationStatus Status
    {
        get => status;
        set
        {
            if (!Set(ref status, value)) return;
            Notify(nameof(StatusText));
            Notify(nameof(IsRunning));
        }
    }
    public int? AssignedWorkerId
    {
        get => assignedWorkerId;
        set { if (Set(ref assignedWorkerId, value)) Notify(nameof(WorkerText)); }
    }
    public string? CurrentStageText { get => currentStageText; set => Set(ref currentStageText, value); }
    public string? ErrorMessage { get => errorMessage; set { if (Set(ref errorMessage, value)) Notify(nameof(HasError)); } }
    public string WorkerText => AssignedWorkerId is int id ? $"#{id}" : "";
    public string StatusText => Status switch
    {
        StudyIterationStatus.PreparingFolder => "Preparing Folder",
        StudyIterationStatus.OpeningBaseline => "Opening Baseline",
        StudyIterationStatus.CreatingChild => "Creating Child",
        StudyIterationStatus.ApplyingGeometry => "Applying Geometry",
        StudyIterationStatus.ApplyingLoading => "Applying Loading",
        StudyIterationStatus.ApplyingPretension => "Applying Pretension",
        _ => Status.ToString()
    };
    public bool IsRunning => Status is >= StudyIterationStatus.PreparingFolder and <= StudyIterationStatus.Saving;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
}

/// <summary>UI-safe status copy for one isolated, sequential SAP2000 worker lane.</summary>
public sealed class SapWorkerSnapshot : Observable
{
    private SapWorkerState state = SapWorkerState.Offline;
    private string? currentIterationId;
    private StudyWorkerStage? currentStage;
    private string? currentModelPath;
    private int completedCount;
    private string? lastMessage;

    public required int WorkerId { get; init; }
    public string InstanceName => $"Instance {WorkerId}";
    public SapWorkerState State
    {
        get => state;
        set
        {
            if (!Set(ref state, value)) return;
            Notify(nameof(GateText));
            Notify(nameof(StateText));
        }
    }
    public string? CurrentIterationId { get => currentIterationId; set => Set(ref currentIterationId, value); }
    public StudyWorkerStage? CurrentStage
    {
        get => currentStage;
        set
        {
            if (!Set(ref currentStage, value)) return;
            Notify(nameof(StageText));
            Notify(nameof(StageNumber));
            Notify(nameof(StageProgressText));
        }
    }
    public string? CurrentModelPath { get => currentModelPath; set => Set(ref currentModelPath, value); }
    public int CompletedCount { get => completedCount; set => Set(ref completedCount, value); }
    public string? LastMessage { get => lastMessage; set => Set(ref lastMessage, value); }
    public string StateText => State.ToString().ToUpperInvariant();
    public string GateText => State switch
    {
        SapWorkerState.Busy or SapWorkerState.ReturningToBaseline => "Gate: OCCUPIED",
        SapWorkerState.Faulted => "Gate: FAULTED",
        SapWorkerState.Ready => "Gate: AVAILABLE",
        _ => "Gate: CLOSED"
    };
    public string StageText => CurrentStage switch
    {
        null or StudyWorkerStage.Waiting => "Waiting for next candidate...",
        StudyWorkerStage.PreparingFolder => "Preparing Folder",
        StudyWorkerStage.OpeningBaseline => "Opening Baseline",
        StudyWorkerStage.CreatingChild => "Creating Child",
        StudyWorkerStage.ApplyingGeometry => "Applying Geometry",
        StudyWorkerStage.ApplyingLoading => "Applying Loading",
        StudyWorkerStage.ApplyingPretension => "Applying Pretension",
        StudyWorkerStage.ReturningToBaseline => "Returning to Baseline",
        _ => CurrentStage.ToString()!
    };
    public int StageNumber => CurrentStage switch
    {
        StudyWorkerStage.PreparingFolder => 1,
        StudyWorkerStage.OpeningBaseline => 2,
        StudyWorkerStage.CreatingChild => 3,
        StudyWorkerStage.ApplyingGeometry => 4,
        StudyWorkerStage.ApplyingLoading => 5,
        StudyWorkerStage.ApplyingPretension => 6,
        StudyWorkerStage.Saving or StudyWorkerStage.Completed => 7,
        _ => 0
    };
    public string StageProgressText => StageNumber == 0 ? "" : $"Stage {StageNumber} / 7";
}
