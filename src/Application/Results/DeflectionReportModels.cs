namespace COT_VCF.Application.Results;

// Report-side DTOs. Serialized (camelCase) into the final HTML as #report-data. Kept separate from the
// SAP extraction objects so the HTML never depends on domain/SAP types.

public sealed record StudyMetadataDto(
    string BaselineModelName,
    string BaselineModelPath,
    string StudyFolder,
    string StudyMode,
    int CandidateCount,
    string GeneratedAtIso,
    string OutputComponent,   // "Uz"
    string CoordinateUnit,    // "m"
    string DeflectionUnit);   // "mm"

public sealed record DeflectionPointDto(
    string PointName, double Coord, double? UzMm, int Sequence, string Status);

public sealed record DeflectionCandidateSeriesDto(
    string IterationKey,
    long RunIndex,
    double? LoadLevelKPa,
    string LoadingLabel,
    double TrussHeightM,
    double DeltaHM,
    double? PretensionKN,
    string PretensionLabel,
    string ExtractionStatus,
    string DiagnosticReportRelativePath,
    string ColorKey,   // key into ColorLegend — the parameter dimension that varies (load level, else height, else pretension)
    string DashKey,    // key into DashLegend — the second varying dimension, or "" when there is none
    IReadOnlyList<DeflectionPointDto> Points);

public sealed record DeflectionGroupReportDto(
    string GroupName,
    string OutputCase,
    string ProfileAxis,   // "X"
    IReadOnlyList<DeflectionCandidateSeriesDto> Series);

public sealed record LegendEntryDto(string Key, string Label, string Value);

public sealed record DeflectionReportDto(
    StudyMetadataDto Study,
    IReadOnlyList<DeflectionGroupReportDto> Groups,
    string ColorDimension,                       // e.g. "Load level" / "Truss height"
    string DashDimension,                        // e.g. "Truss height" / "" when only one dimension varies
    IReadOnlyList<LegendEntryDto> ColorLegend,   // Value = hex colour
    IReadOnlyList<LegendEntryDto> DashLegend,    // Value = "solid|dash|dot|dashdot|longdash|longdashdot"
    IReadOnlyList<ResultReportWarningDto> Warnings);

public sealed record ResultReportWarningDto(string IterationKey, string Category, string Severity, string Message);
