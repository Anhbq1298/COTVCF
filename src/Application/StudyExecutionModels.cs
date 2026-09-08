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
    // Frozen after Tab 04 groups have been prepared on and saved into the parent.
    public IReadOnlyList<OutputSapGroupDefinition> OutputGroups { get; init; } = [];
    public string CableResultLoadCase { get; init; } = "";
    // Cable-force post-processing (Tab 06.05): the cables whose axial force is read under CableResultLoadCase.
    public IReadOnlyList<string> CableResultCables { get; init; } = [];
    // Deflection post-processing (Tab 06): monitored points + the SAP case their Uz is read under.
    public IReadOnlyList<string> DeflectionPoints { get; init; } = [];
    public string DeflectionOutputCase { get; init; } = "";
}

public sealed record StudyExecutionProgress
{
    public int Total { get; init; }
    public int Completed { get; init; }
    public int Reused { get; init; }
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
    int Reused = 0,
    bool AllWorkersFaulted = false)
{
    public StudyArtifactOutcome Artifacts { get; init; } = StudyArtifactOutcome.NotProduced;
    public StudyRunReference? Run { get; init; }
}

public enum StudyArtifactState { NotProduced, Ready, Failed }

public sealed record StudyArtifactOutcome(
    StudyArtifactState State,
    string? WorkbookPath,
    string? DeflectionReportPath,
    string? CableForceReportPath,
    string? ErrorMessage)
{
    public static StudyArtifactOutcome NotProduced { get; } = new(StudyArtifactState.NotProduced, null, null, null, null);
    public static StudyArtifactOutcome Failed(string message) => new(StudyArtifactState.Failed, null, null, null, message);
}
