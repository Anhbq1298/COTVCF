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
    string LoadId,            // "L01" — study loading candidate id
    string HeightId,          // "H01" — study truss-height candidate id
    string PretensionId,      // "P01" — study pretension candidate id
    double? LoadLevelKPa,
    string LoadingLabel,
    double TrussHeightM,
    double DeltaHM,
    double? PretensionKN,
    string PretensionLabel,
    string ExtractionStatus,
    string DiagnosticReportRelativePath,
    IReadOnlyList<string> ColorKeys,   // one per DeflectionReportDto.Views — key into that view's ColorLegend
    IReadOnlyList<string> DashKeys,    // one per view — key into that view's DashLegend
    IReadOnlyList<string> FilterKeys,  // one per view — key into that view's FilterLegend ("" when the view has none)
    IReadOnlyList<PretensionCableEntryDto> PretensionCables,  // per-cable target force for this candidate
    IReadOnlyList<LoadComponentEntryDto> LoadingComponents,   // per-pattern qz for this loading candidate
    IReadOnlyList<DeflectionPointDto> Points);

public sealed record PretensionCableEntryDto(string Cable, double ForceKN);
public sealed record LoadComponentEntryDto(string Pattern, double LevelKPa);

public sealed record DeflectionGroupReportDto(
    string GroupName,
    string OutputCase,
    string ProfileAxis,   // "X"
    IReadOnlyList<DeflectionCandidateSeriesDto> Series);

public sealed record LegendEntryDto(string Key, string Label, string Value);

/// <summary>One chart panel: the deflection profile coloured by <see cref="ColorDimension"/> with the line
/// style keyed to <see cref="DashDimension"/>. The report carries one per parameter the study actually
/// sweeps — 1 to 3 (load candidate always; truss height unless FixHeight; pretension unless FixPretension).</summary>
public sealed record ChartViewDto(
    string ColorDimension,                       // "Load candidate" | "Truss height" | "Initial pretension"
    IReadOnlyList<LegendEntryDto> ColorLegend,   // Value = hex colour
    string DashDimension,                        // first other swept dimension, or same as colour when none
    IReadOnlyList<LegendEntryDto> DashLegend,    // Value = "solid|dash|dot|dashdot|longdash|longdashdot"
    bool DashMarkers,                            // true when the line dimension is pretension — draw a per-candidate point marker
    string FilterDimension,                      // the remaining swept dimension (no visual encoding), "" when none
    IReadOnlyList<LegendEntryDto> FilterLegend); // Value unused — legend rows are filter-only

public sealed record DeflectionReportDto(
    StudyMetadataDto Study,
    IReadOnlyList<DeflectionGroupReportDto> Groups,
    IReadOnlyList<ChartViewDto> Views,
    IReadOnlyList<ResultReportWarningDto> Warnings);

public sealed record ResultReportWarningDto(string IterationKey, string Category, string Severity, string Message);
