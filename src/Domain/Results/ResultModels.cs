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
    FrameNotFound,   // frame name not in the model
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

/// <summary>Governing axial force [kN] read for one cable under one selected output case (max |P| over the
/// cable's analysis elements). <see cref="Status"/> says whether <see cref="ForceKN"/> is meaningful — a
/// missing result is never reported as 0.</summary>
public sealed record CableForceRead(
    string CableName,
    double? ForceKN,
    ResultValueStatus Status,
    string? Warning = null);

/// <summary>One unmodified row returned by SAP for a frame object. Object and element stations are
/// retained because one frame object may be meshed into several analysis elements.</summary>
public sealed record FrameForceStationRead(
    string FrameObjectName,
    string ElementName,
    double ObjectStationM,
    double ElementStationM,
    string OutputCase,
    string StepType,
    double StepNumber,
    double P,
    double V2,
    double V3,
    double T,
    double M2,
    double M3);

/// <summary>All SAP frame-force rows for one selected frame object and output case/combo.</summary>
public sealed record FrameForceRead(
    string FrameName,
    IReadOnlyList<FrameForceStationRead> Stations,
    ResultValueStatus Status,
    string? Warning = null);

/// <summary>One unmodified global-axis support-reaction row returned by SAP for a selected point.</summary>
public sealed record JointReactionRowRead(
    string OutputCase,
    string StepType,
    double StepNumber,
    double Fx,
    double Fy,
    double Fz,
    double Mx,
    double My,
    double Mz);

/// <summary>All SAP support-reaction rows for one selected point and output case/combo.</summary>
public sealed record JointReactionRead(
    string PointName,
    double X,
    double Y,
    double Z,
    IReadOnlyList<JointReactionRowRead> Rows,
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

/// <summary>One cable's post-processing row: its resolved pretension target and the analysed axial force
/// read under the configured cable result case. <see cref="ForceKN"/> is null unless <see cref="Status"/>
/// is <see cref="ResultValueStatus.Valid"/> — never coerced to 0.</summary>
public sealed record CableForceResultDto(
    string CableName,
    double? TargetForceKN,
    double? ForceKN,
    ResultValueStatus Status,
    string? Warning = null);

/// <summary>One SAP result row located along an ordered output group profile.</summary>
public sealed record MemberForceStationResultDto(
    string FrameName,
    int FrameSequence,
    string ElementName,
    double ObjectStationM,
    double ElementStationM,
    double ProfileCoordinateM,
    string StepType,
    double StepNumber,
    double P,
    double V2,
    double V3,
    double T,
    double M2,
    double M3,
    ResultValueStatus Status,
    string? Warning = null);

public sealed record MemberForceGroupResultDto(
    string GroupName,
    string SapGroupName,
    string OutputCase,
    IReadOnlyList<MemberForceStationResultDto> Stations);

/// <summary>One selected foundation point at one SAP result step. Components are global-axis reactions.</summary>
public sealed record SupportReactionPointResultDto(
    string PointName,
    int PointSequence,
    double X,
    double Y,
    double Z,
    string StepType,
    double StepNumber,
    double? Fx,
    double? Fy,
    double? Fz,
    double? Mx,
    double? My,
    double? Mz,
    ResultValueStatus Status,
    string? Warning = null);

public sealed record SupportReactionGroupResultDto(
    string FoundationSide,
    string SapGroupName,
    string OutputCase,
    IReadOnlyList<SupportReactionPointResultDto> Points);

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
    IReadOnlyList<PretensionCableDto>? PretensionCables = null, // per-cable target forces for this candidate
    IReadOnlyList<LoadComponentDto>? LoadingComponents = null); // per-pattern qz for this loading candidate

/// <summary>One cable's resolved Cable Loads / Target Force for a study candidate.</summary>
public sealed record PretensionCableDto(string CableName, double TargetForceKN);

/// <summary>One load pattern's resolved qz [kPa] for a study loading candidate.</summary>
public sealed record LoadComponentDto(string PatternName, double LevelKPa);

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
    IReadOnlyList<ResultWarningDto> Warnings,
    IReadOnlyList<CableForceResultDto>? CableForces = null,
    IReadOnlyList<MemberForceGroupResultDto>? MemberForceGroups = null,
    IReadOnlyList<SupportReactionGroupResultDto>? SupportReactionGroups = null)
{
    public string DiagnosticReportFileName => $"Deflection_Diagnostic_{IterationKey}.html";
    public string DiagnosticReportRelativePath => $"{IterationKey}/{DiagnosticReportFileName}";
}
