namespace COT_VCF.Application.Results;

// Report-side DTOs for Cable_Forces_Report.html. Serialized (camelCase) into the page as #report-data.
// Kept separate from the SAP extraction objects so the HTML never depends on domain / SAP types.

public sealed record CableForceReportDto(
    CableForceStudyDto Study,
    IReadOnlyList<string> Cables,
    IReadOnlyList<CableForceAxisDto> Loads,     // Load-Level selector (value not plotted)
    IReadOnlyList<CableForceAxisDto> Heights,   // "Height on X"
    IReadOnlyList<CableForceAxisDto> Pretensions, // "Pretension on X" (categorical when any is mixed)
    IReadOnlyList<CableForceCandidateDto> Rows,
    double YMin, double YMax,
    IReadOnlyList<CableForceWarningDto> Warnings);

public sealed record CableForceStudyDto(
    string BaselineModelName,
    string StudyMode,
    int CandidateCount,
    string ResultCase,          // the SAP load case the cable force was read under
    string GeneratedAtIso);

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
    IReadOnlyDictionary<string, double> Targets,     // cable -> target pretension [kN]
    IReadOnlyDictionary<string, double?> Forces);    // cable -> analysed cable force [kN]; null = unavailable

public sealed record CableForceWarningDto(string IterationKey, string Message, bool IsError);
