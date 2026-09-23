namespace COT_VCF.Application.Results;

public sealed record SupportReactionStudyDto(
    string BaselineModelName,
    string StudyMode,
    int CandidateCount,
    string GeneratedAtIso,
    int IndividualProfileLimit);

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
    IReadOnlyList<SupportReactionCandidateDto> Series);

public sealed record SupportReactionReportDto(
    SupportReactionStudyDto Study,
    IReadOnlyList<SupportReactionGroupReportDto> Groups,
    IReadOnlyList<ResultReportWarningDto> Warnings,
    IReadOnlyList<string> SiblingReports);
