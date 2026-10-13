namespace Mcotis.Desktop.Application.Results;

// Report-side DTOs for Cable_Forces_Report.html. Serialized (camelCase) into the page as #report-data.
// Kept separate from the SAP extraction objects so the HTML never depends on domain / SAP types.
// Everything below is derived at report-generation time from values that were already extracted; none of it
// is written back to Study_Raw_Results.json, the workbook's result sheets, SAP2000 or the project file.

public sealed record CableForceReportDto(
    CableForceStudyDto Study,
    IReadOnlyList<string> Cables,                 // natural name order (Cable1, Cable2, Cable10)
    IReadOnlyList<CableForceAxisDto> Loads,       // load selector; never mixed inside one chart
    IReadOnlyList<CableForceAxisDto> Heights,     // actual truss heights of this study, ascending
    IReadOnlyList<CableForceAxisDto> Pretensions, // one detailed chart per side for each of these
    IReadOnlyList<CableForceCandidateDto> Rows,
    ReportCandidateDefinitionsDto CandidateDefinitions,
    IReadOnlyList<CableForceWarningDto> Warnings,
    // cable -> design capacity [kN], by exact cable object name. A cable absent from the map has no limit
    // line and no utilisation (N/A) — never a borrowed or invented value.
    IReadOnlyDictionary<string, double> Capacities,
    // cable -> CableCapacityResolver.Current | Stored | Invalid | None
    IReadOnlyDictionary<string, string> CapacitySources,
    // cable -> "A" | "B" | "" (Unassigned): the Auto Detect suggestion. Presentation only.
    IReadOnlyDictionary<string, string> AutoGroups,
    IReadOnlyList<CableForceHeightOverviewDto> Overview,
    IReadOnlyList<CableForceGoverningDto> Governing,
    // module keys whose sibling report exists in the same study folder (drives the Result Library links)
    IReadOnlyList<string> SiblingReports);

public sealed record CableForceStudyDto(
    string BaselineModelName,
    string StudyMode,
    int CandidateCount,
    string ResultCase,          // the SAP load case the final analysed cable force was read under
    string GeneratedAtIso,
    ReportScopeDto? Scope = null);      // set when the report covers a subset of the study (Tab 06A)

/// <summary>One axis value. <see cref="Value"/> is null for a categorical / mixed pretension candidate —
/// the chart then places it by ordinal position and never invents a kN number for it.</summary>
public sealed record CableForceAxisDto(string Id, string Label, double? Value, bool Mixed = false);

public sealed record CableForceCandidateDto(
    long Run,
    string CandidateId,         // "C001"
    string IterationKey,        // "L01_H01_P01"
    string ModelFileName,
    string LoadId, double? LoadKPa,
    string HeightId, double HeightM,
    string PretId, string PretLabel, bool PretMixed,
    IReadOnlyDictionary<string, double> Targets,          // cable -> prestress target [kN]
    IReadOnlyDictionary<string, double?> Forces,          // cable -> final analysed force [kN]; null = unavailable
    IReadOnlyDictionary<string, double?> Deltas,          // cable -> final − target [kN]; null = unavailable
    IReadOnlyDictionary<string, double?> DeltaPercents,   // cable -> (final − target) / target × 100; null = N/A
    IReadOnlyDictionary<string, string> Statuses);        // cable -> extraction status of the final force

/// <summary>One Section-01 chart: every cable's range across the pretension candidates that have a result
/// at this loading candidate × truss height. A missing candidate is excluded from the range, never read as 0.</summary>
public sealed record CableForceHeightOverviewDto(
    string LoadId,
    string HeightId,
    double HeightM,
    int ExpectedCount,                       // pretension candidates in the study
    int AvailableCount,                      // of those, how many have a result at this load × height
    IReadOnlyList<string> MissingPretIds,
    string Note,                             // "Range based on 8 available pretension candidates; P08-H30 unavailable."
    IReadOnlyList<CableForceRangeDto> Ranges);

public sealed record CableForceRangeDto(
    string Cable,
    double? TargetMin, double? TargetMax, int TargetCount,
    double? FinalMin, double? FinalMax, int FinalCount,
    string? FinalMinPretId, string? FinalMaxPretId);

/// <summary>The candidate that governs one cable (largest final analysed force) inside <see cref="Scope"/>,
/// checked against the capacity supplied for that exact cable.</summary>
public sealed record CableForceGoverningDto(
    string Scope,               // a loading candidate id, or "*" for the whole study
    string Cable,
    double? FinalForceKN,
    double? TargetForceKN,
    string? CandidateId,
    string? IterationKey,
    string? LoadId,
    string? HeightId,
    double? HeightM,
    string? PretId,
    double? CapacityKN,
    double? UtilisationPct,
    string Status);             // "pass" | "exceed" | "no-capacity" | "no-result"

public sealed record CableForceWarningDto(string IterationKey, string Message, bool IsError);
