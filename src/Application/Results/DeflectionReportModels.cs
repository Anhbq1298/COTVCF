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
    string ColorKey,   // key into ColorLegend  — truss height (view 1: deflection vs truss height)
    string DashKey,    // key into DashLegend   — load candidate (line style, both views)
    string ColorKey2,  // key into ColorLegend2 — initial pretension force (view 2: deflection vs pretension)
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
    string ColorDimension,                       // view 1 colour dimension name — "Truss height"
    string DashDimension,                        // shared line-style dimension name — "Load candidate"
    IReadOnlyList<LegendEntryDto> ColorLegend,   // Value = hex colour
    IReadOnlyList<LegendEntryDto> DashLegend,    // Value = "solid|dash|dot|dashdot|longdash|longdashdot"
    string ColorDimension2,                      // view 2 colour dimension name — "Initial pretension"
    IReadOnlyList<LegendEntryDto> ColorLegend2,  // Value = hex colour
    IReadOnlyList<ResultReportWarningDto> Warnings);

public sealed record ResultReportWarningDto(string IterationKey, string Category, string Severity, string Message);
