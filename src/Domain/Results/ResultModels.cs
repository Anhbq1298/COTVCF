namespace COT_VCF.Domain.Results;

// ============================ enums ============================

/// <summary>Which model coordinate a monitored-point deflection profile is plotted against. This phase: always X.</summary>
public enum ProfileCoordinateAxis { X, Y, Z }

/// <summary>Outcome of reading one monitored point's displacement from SAP2000. 0.0 is a valid engineering
/// result and must never be produced for anything other than <see cref="Valid"/>.</summary>
public enum ResultValueStatus
{
    Valid,           // SAP returned a number
    NoResult,        // case selected, point exists, API returned zero rows
    PointNotFound,   // point name not in the model
    CaseNotFound,    // output case / combo not in the model
    AnalysisNotRun,  // model not locked / no results available
    ApiError         // nonzero return code or exception
}

/// <summary>Per-candidate result-extraction outcome, independent of the analysis run itself.</summary>
public enum CandidateExtractionStatus
{
    NotStarted,
    Extracting,
    ExtractedOk,
    ExtractedWithWarning, // analysis ok, some points missing/NoResult but at least one Valid
    ExtractionFailed,     // case missing, every point failed, or an API error
    AnalysisFailed        // upstream analysis never succeeded, so no extraction was attempted
}

public enum ResultWarningSeverity { Info, Warning, Error }

// ============================ raw SAP read ============================

/// <summary>One row returned from <c>Results.JointDispl</c> for a monitored point, already unit-normalised
/// to metres (coords) and millimetres (Uz). <see cref="Status"/> says whether <see cref="UzMm"/> is meaningful.</summary>
public sealed record JointDisplacement(
    string PointName,
    double X, double Y, double Z,
    double? UzMm,
    ResultValueStatus Status,
    string? Warning = null);

// ============================ extraction-side DTOs ============================

public sealed record MonitoredPointResultDto(
    string PointName,
    double X, double Y, double Z,
    int Sequence,               // 1-based, after sorting by the group's ProfileAxis ascending
    double? UzMm,               // null unless Status == Valid
    ResultValueStatus Status,
    string? Warning);

public sealed record MonitoredPointGroupResultDto(
    string GroupName,
    string OutputCase,          // the case actually selected in SAP for this read
    ProfileCoordinateAxis ProfileAxis,
    IReadOnlyList<MonitoredPointResultDto> Points);   // already sorted by ProfileAxis

/// <summary>Study parameters resolved for one candidate. Fields that a given study mode does not vary are still
/// populated from the frozen candidate; single-value convenience fields are null when the candidate is mixed.</summary>
public sealed record CandidateParametersDto(
    long RunIndex,
    string IterationKey,        // "L01_H01_P01"
    string LoadingCandidateId,  // "L01"
    string HeightCandidateId,   // "H01"
    string PretensionCandidateId, // "P01"
    double? LoadLevelKPa,       // single qz if the loading candidate has exactly one; else null
    string LoadingLabel,        // "2.5 kPa" | "DL 1.0 + LL 2.5"
    double TrussHeightM,
    double DeltaHM,             // signed, from baseline
    double? PretensionKN,       // single target force if every assignment is equal; else null
    string PretensionLabel,     // "P01 · 5000 kN" | "mixed" | "none"
    IReadOnlyList<PretensionCableDto>? PretensionCables = null); // per-cable target forces for this candidate

/// <summary>One cable's resolved Cable Loads / Target Force for a study candidate.</summary>
public sealed record PretensionCableDto(string CableName, double TargetForceKN);

public sealed record ResultWarningDto(
    string IterationKey,        // "" for study-level warnings
    string Category,            // "MonitoredPoint" | "OutputCase" | "Extraction" | "Excel" | "Report"
    ResultWarningSeverity Severity,
    string Message,
    DateTimeOffset Timestamp);

public sealed record CandidateResultDto(
    string IterationKey,
    string CandidateModelPath,
    string BaselineModelPath,
    string BaselineModelName,
    IterationStudyMode StudyMode,
    CandidateParametersDto Parameters,
    DateTimeOffset AnalysisStartedAt,
    DateTimeOffset AnalysisCompletedAt,
    DateTimeOffset ExtractionCompletedAt,
    CandidateExtractionStatus ExtractionStatus,
    IReadOnlyList<MonitoredPointGroupResultDto> Groups,
    IReadOnlyList<ResultWarningDto> Warnings)
{
    public string DiagnosticReportFileName => $"Deflection_Diagnostic_{IterationKey}.html";
    public string DiagnosticReportRelativePath => $"{IterationKey}/{DiagnosticReportFileName}";
}
