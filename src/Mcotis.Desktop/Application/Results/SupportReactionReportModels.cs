namespace Mcotis.Desktop.Application.Results;

public sealed record SupportReactionStudyDto(
    string BaselineModelName,
    string StudyMode,
    int CandidateCount,
    string GeneratedAtIso,
    int IndividualProfileLimit,
    ReportScopeDto? Scope = null);      // set when the report covers a subset of the study (Tab 06A)

public sealed record SupportReactionRowDto(
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
    string Status);

public sealed record SupportReactionCandidateDto(
    string IterationKey,
    long RunIndex,
    string LoadId,
    string HeightId,
    string PretensionId,
    string LoadingLabel,
    double TrussHeightM,
    string PretensionLabel,
    IReadOnlyList<SupportReactionRowDto> Rows);

public sealed record SupportReactionGroupReportDto(
    string FoundationSide,
    string SapGroupName,
    string OutputCase,
    IReadOnlyList<SupportReactionCandidateDto> Series,
    // Set when the side's two supports can be told apart as inner / outer: drives the Summary and By Candidate
    // views. Null = generic views only.
    SupportReactionSummaryDto? Summary = null);

/// <summary>A foundation side read as an inner and an outer support, derived at report time (see
/// <c>SupportReactionSummary</c>).</summary>
public sealed record SupportReactionSummaryDto(
    string InnerPoint,                                  // SAP point name of the support towards the other foundation
    string OuterPoint,
    IReadOnlyList<SupportReactionSummaryCandidateDto> Candidates);

public sealed record SupportReactionSummaryCandidateDto(
    string IterationKey,
    IReadOnlyList<SupportReactionSummaryStepDto> Steps);

/// <summary>One result step of one iteration. A component is null unless both supports report it.</summary>
public sealed record SupportReactionSummaryStepDto(
    string StepType,
    double StepNumber,
    SupportReactionPairDto? Fx,
    SupportReactionPairDto? Fz);

/// <summary>Signed source values [kN] as stored; <c>Net</c> = <c>Inner</c> + <c>Outer</c>, the algebraic sum.</summary>
public sealed record SupportReactionPairDto(double Inner, double Outer, double Net);

public sealed record SupportReactionReportDto(
    SupportReactionStudyDto Study,
    IReadOnlyList<SupportReactionGroupReportDto> Groups,
    ReportCandidateDefinitionsDto CandidateDefinitions,
    IReadOnlyList<ResultReportWarningDto> Warnings,
    IReadOnlyList<string> SiblingReports);
