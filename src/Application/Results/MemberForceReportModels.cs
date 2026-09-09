namespace COT_VCF.Application.Results;

public sealed record MemberForceStudyDto(
    string BaselineModelName,
    string StudyMode,
    int CandidateCount,
    string GeneratedAtIso,
    int IndividualProfileLimit);

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
    IReadOnlyList<MemberForceCandidateDto> Series);

public sealed record MemberForceReportDto(
    MemberForceStudyDto Study,
    IReadOnlyList<MemberForceGroupReportDto> Groups,
    IReadOnlyList<ResultReportWarningDto> Warnings,
    IReadOnlyList<string> SiblingReports);
