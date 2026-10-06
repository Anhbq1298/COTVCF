namespace COT_VCF.Application.Results;

public sealed record MemberForceStudyDto(
    string BaselineModelName,
    string StudyMode,
    int CandidateCount,
    string GeneratedAtIso,
    int IndividualProfileLimit,
    ReportScopeDto? Scope = null);      // set when the report covers a subset of the study (Tab 06A)

public sealed record MemberForceStationDto(
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
    string Status);

public sealed record MemberForceCandidateDto(
    string IterationKey,
    long RunIndex,
    string LoadId,
    string HeightId,
    string PretensionId,
    string LoadingLabel,
    double TrussHeightM,
    string PretensionLabel,
    IReadOnlyList<MemberForceStationDto> Stations);

public sealed record MemberForceGroupReportDto(
    string GroupName,
    string SapGroupName,
    string OutputCase,
    IReadOnlyList<MemberForceCandidateDto> Series,
    // Set for a Top Chord, Bottom Chord or Mega Column group: drives its Summary view (and, for a chord, By Station).
    // Null = generic views only.
    MemberGroupSummaryDto? Summary = null);

/// <summary>Governing-force reading of a member-force group, derived at report time (see <c>MemberGroupSummary</c>).</summary>
public sealed record MemberGroupSummaryDto(
    string Kind,                                        // "top" | "bottom" | "mega"
    string Title,                                       // "Top Chord" | "Bottom Chord" | "Mega Column"
    bool ByStation,                                     // chords: a By Station drill-down exists. Mega columns: no station anywhere
    IReadOnlyList<MemberGroupSummaryCandidateDto> Candidates); // one per series, same order

public sealed record MemberGroupSummaryCandidateDto(
    string IterationKey,
    double? GoverningKN,        // signed source P [kN] where the chord governs; null = no valid station (never 0)
    string? FrameName,
    string? ElementName,
    double? StationM,           // physical station (profile coordinate) of the governing value; null for mega columns
    string? Step,               // result step of the governing value
    IReadOnlyList<double[]> Profile);   // [station m, signed source P kN] — one governing point per physical station; empty for mega columns

public sealed record MemberForceReportDto(
    MemberForceStudyDto Study,
    IReadOnlyList<MemberForceGroupReportDto> Groups,
    IReadOnlyList<ResultReportWarningDto> Warnings,
    IReadOnlyList<string> SiblingReports);
