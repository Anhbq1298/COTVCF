using System.Collections.ObjectModel;

namespace Mcotis.Desktop.Domain;

/// <summary>One Cartesian loading choice: one qz value for every configured load pattern.</summary>
public sealed record LoadingCombination(long Index, IReadOnlyList<(string Name, double Qz)> Loads);

/// <summary>One complete loading candidate, including every point-load assignment written to SAP2000.</summary>
public sealed record LoadingStudyCandidate
{
    public required string Id { get; init; }
    public required LoadingCombination Combination { get; init; }
    public required IReadOnlyList<LoadingCandidate> PointLoads { get; init; }
    public required IReadOnlyList<PointLoad> Loads { get; init; }
    public int JointCount => Loads.Select(load => load.PointName).Distinct(StringComparer.Ordinal).Count();
    public string DisplayName => $"{Id} - {JointCount} joint{(JointCount == 1 ? "" : "s")} / {Loads.Count} point load{(Loads.Count == 1 ? "" : "s")}";
    public string AssignmentSummary => $"{Id} · {JointCount} joint{(JointCount == 1 ? "" : "s")} · {Loads.Count} load{(Loads.Count == 1 ? "" : "s")}";
    public string AssignmentDetails => string.Join("; ", Loads
        .GroupBy(load => load.LoadPattern, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .Select(group =>
        {
            var rows = group.OrderBy(load => load.PointName, StringComparer.Ordinal).ToArray();
            var minimum = rows.Min(load => load.Fz);
            var maximum = rows.Max(load => load.Fz);
            var force = Close(minimum, maximum) ? $"{minimum:0.###}" : $"{minimum:0.###}..{maximum:0.###}";
            return $"{group.Key}: {rows.Length} joint{(rows.Length == 1 ? "" : "s")} [{string.Join(", ", rows.Select(load => load.PointName))}], Fz {force} kN";
        }));

    private static bool Close(double left, double right) =>
        Math.Abs(left - right) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));
}

/// <summary>One complete Assign → Cable Loads → Target Force assignment.</summary>
public sealed record PretensionAssignment(string CableName, string LoadPattern, double TargetForce);

public enum StudyIterationStatus
{
    Planned,
    Queued,
    Skipped,
    PreparingFolder,
    ReusingExisting,
    OpeningBaseline,
    CreatingChild,
    ApplyingGeometry,
    ApplyingLoading,
    ApplyingPretension,
    Saving,
    RunningAnalysis,
    SavingAnalysis,
    Verifying,
    ExtractingResults,
    GeneratingDiagnostic,
    Ready,
    Reused,
    Failed,
    Cancelled
}

public enum StudyWorkerStage
{
    Waiting,
    PreparingFolder,
    ReusingExisting,
    OpeningBaseline,
    CreatingChild,
    ApplyingGeometry,
    ApplyingLoading,
    ApplyingPretension,
    Saving,
    RunningAnalysis,
    SavingAnalysis,
    Verifying,
    ExtractingResults,
    GeneratingDiagnostic,
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
    private string resumeDispositionText = "No prior run";
    private string resumeReason = "No compatible run has been inspected.";

    public required long RunIndex { get; init; }
    public required string IterationId { get; init; }
    public required string LoadingCandidateId { get; init; }
    public required string HeightCandidateId { get; init; }
    public required string PretensionCandidateId { get; init; }
    public required LoadingStudyCandidate LoadingCandidate { get; init; }
    public required TrussCandidate HeightCandidate { get; init; }
    public required PretensionCombination PretensionCandidate { get; init; }
    public required IReadOnlyList<PretensionAssignment> PretensionAssignments { get; init; }
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
    public string LoadingAssignmentText => LoadingCandidate.AssignmentSummary;
    public string LoadingAssignmentDetails => LoadingCandidate.AssignmentDetails;
    public string PretensionAssignmentText
    {
        get
        {
            if (PretensionAssignments.Count == 0) return $"{PretensionCandidateId} · no targets";
            var minimum = PretensionAssignments.Min(assignment => assignment.TargetForce);
            var maximum = PretensionAssignments.Max(assignment => assignment.TargetForce);
            var force = Math.Abs(minimum - maximum) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(minimum), Math.Abs(maximum)))
                ? $"{minimum:0.###}"
                : $"{minimum:0.###}..{maximum:0.###}";
            return $"{PretensionCandidateId} · {PretensionAssignments.Count} target{(PretensionAssignments.Count == 1 ? "" : "s")} · {force} kN";
        }
    }
    public string PretensionAssignmentDetails => string.Join("; ", PretensionAssignments.Select(assignment =>
        $"{assignment.CableName} @ {assignment.LoadPattern} = {assignment.TargetForce:0.###} kN"));
    public string HeightAssignmentText =>
        $"{HeightCandidateId} · ΔH {HeightCandidate.DeltaH:+0.###;-0.###;0} m · {HeightCandidate.Height:0.###} m";
    public string HeightAssignmentDetails =>
        $"{HeightCandidateId}: truss height {HeightCandidate.Height:0.###} m (ΔH {HeightCandidate.DeltaH:+0.###;-0.###;0} m from baseline)"
        + (string.IsNullOrWhiteSpace(HeightCandidate.Reference) ? "" : $"; ref {HeightCandidate.Reference}");

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
    public string ResumeDispositionText { get => resumeDispositionText; set => Set(ref resumeDispositionText, value); }
    public string ResumeReason { get => resumeReason; set => Set(ref resumeReason, value); }
    public string WorkerText => AssignedWorkerId is int id ? $"#{id}" : "";
    public string StatusText => Status switch
    {
        StudyIterationStatus.PreparingFolder => "Preparing Folder",
        StudyIterationStatus.ReusingExisting => "Reusing Existing",
        StudyIterationStatus.OpeningBaseline => "Opening Baseline",
        StudyIterationStatus.CreatingChild => "Creating Child",
        StudyIterationStatus.ApplyingGeometry => "Applying Geometry",
        StudyIterationStatus.ApplyingLoading => "Applying Loading",
        StudyIterationStatus.ApplyingPretension => "Applying Pretension",
        StudyIterationStatus.RunningAnalysis => "Running Analysis",
        StudyIterationStatus.SavingAnalysis => "Saving Analysis",
        StudyIterationStatus.Verifying => "Verifying",
        StudyIterationStatus.ExtractingResults => "Extracting Results",
        StudyIterationStatus.GeneratingDiagnostic => "Diagnostic Report",
        StudyIterationStatus.Reused => "Reused",
        _ => Status.ToString()
    };
    public bool IsRunning => Status is >= StudyIterationStatus.PreparingFolder and <= StudyIterationStatus.GeneratingDiagnostic;
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
        StudyWorkerStage.ReusingExisting => "Reusing Existing Child",
        StudyWorkerStage.OpeningBaseline => "Opening Baseline",
        StudyWorkerStage.CreatingChild => "Creating Child",
        StudyWorkerStage.ApplyingGeometry => "Applying Geometry",
        StudyWorkerStage.ApplyingLoading => "Applying Loading",
        StudyWorkerStage.ApplyingPretension => "Applying Pretension",
        StudyWorkerStage.Saving => "Saving Inputs",
        StudyWorkerStage.RunningAnalysis => "Running Analysis",
        StudyWorkerStage.SavingAnalysis => "Saving Analysis",
        StudyWorkerStage.Verifying => "Verifying Child",
        StudyWorkerStage.ExtractingResults => "Extracting Results",
        StudyWorkerStage.GeneratingDiagnostic => "Diagnostic Report",
        StudyWorkerStage.ReturningToBaseline => "Returning to Baseline",
        _ => CurrentStage.ToString()!
    };
    public int StageNumber => CurrentStage switch
    {
        StudyWorkerStage.PreparingFolder or StudyWorkerStage.ReusingExisting => 1,
        StudyWorkerStage.OpeningBaseline => 2,
        StudyWorkerStage.CreatingChild => 3,
        StudyWorkerStage.ApplyingGeometry => 4,
        StudyWorkerStage.ApplyingLoading => 5,
        StudyWorkerStage.ApplyingPretension => 6,
        StudyWorkerStage.Saving => 7,
        StudyWorkerStage.RunningAnalysis => 8,
        StudyWorkerStage.Verifying => 9,
        StudyWorkerStage.ExtractingResults or StudyWorkerStage.GeneratingDiagnostic or StudyWorkerStage.Completed => 10,
        _ => 0
    };
    public string StageProgressText => StageNumber == 0 ? "" : $"Stage {StageNumber} / 10";
}
