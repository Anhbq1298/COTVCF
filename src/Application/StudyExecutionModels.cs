using COT_VCF.Domain;

namespace COT_VCF.Application;

public sealed record ResolvedPretensionCandidate(
    string Id,
    IReadOnlyList<PretensionAssignment> Assignments);

public sealed record StudyIterationExecutionDto(
    long RunIndex,
    string IterationId,
    string LoadingCandidateId,
    string HeightCandidateId,
    string PretensionCandidateId,
    LoadingStudyCandidate Loading,
    TrussCandidate Height,
    ResolvedPretensionCandidate Pretension,
    string IterationDirectory,
    string ModelName,
    string ModelFullPath,
    string Description,
    bool RunIterate);

public sealed record StudyExecutionSnapshot(
    string BaselineModelPath,
    ModelInfo BaselineModel,
    GeometryModel BaselineGeometry,
    IReadOnlyList<string> TopPoints,
    IReadOnlyList<string> MiddlePoints,
    IReadOnlyList<string> BottomPoints,
    IterationStudyMode StudyMode,
    IReadOnlyList<StudyIterationExecutionDto> Jobs,
    int RequestedWorkerCount)
{
    // Frozen after Tab 05 groups have been prepared on and saved into the parent.
    // Reserved for the future result-extraction phase; current workers do not read results.
    public IReadOnlyList<OutputSapGroupDefinition> OutputGroups { get; init; } = [];
    public string CableResultLoadCase { get; init; } = "";
}

public sealed record StudyExecutionProgress
{
    public int Total { get; init; }
    public int Completed { get; init; }
    public int Failed { get; init; }
    public int Cancelled { get; init; }
    public int Running { get; init; }
    public int Queued { get; init; }
    public int Skipped { get; init; }
    public int WorkerId { get; init; }
    public string? IterationId { get; init; }
    public StudyWorkerStage Stage { get; init; } = StudyWorkerStage.Waiting;
    public StudyIterationStatus? IterationStatus { get; init; }
    public SapWorkerState WorkerState { get; init; } = SapWorkerState.Offline;
    public string? CurrentModelPath { get; init; }
    public int WorkerCompletedCount { get; init; }
    public string Message { get; init; } = "";
}

public sealed record StudyExecutionResult(
    int Selected,
    int Completed,
    int Failed,
    int Cancelled,
    int Skipped,
    int WorkerCount,
    int PeakConcurrent,
    bool AllWorkersFaulted = false);
