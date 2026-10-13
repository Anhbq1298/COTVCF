using System.IO;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Domain.Results;

namespace Mcotis.Desktop.Application.Results;

/// <summary>Which Tab 06 outputs one generation writes. <c>Generate Outputs</c> writes them all; a module's own
/// <c>Regenerate</c> asks for its report alone — the Excel workbook is by far the slowest output, and a report
/// does not depend on it.</summary>
[Flags]
public enum StudyOutputs
{
    None = 0,
    CandidateDiagnostics = 1,
    Workbook = 2,
    DeflectionReport = 4,
    MemberForceReport = 8,
    SupportReactionReport = 16,
    CableForceReport = 32,
    All = CandidateDiagnostics | Workbook | DeflectionReport | MemberForceReport | SupportReactionReport | CableForceReport,
}

/// <summary>
/// Tab 06 boundary: derives user-facing Excel/HTML outputs from raw study files only. This service has no SAP
/// gateway and can therefore be rerun after post-processing inputs or the raw JSON package are changed.
/// </summary>
public sealed class StudyPostProcessor
{
    /// <param name="cableCapacities">Capacities resolved now from Tab 03 (area × fpu × 1000 × φ). They win over
    /// the capacities stored with the raw results; null keeps the stored ones.</param>
    /// <param name="configuredCables">Every cable currently on Tab 03. One that is listed here without a current
    /// capacity is reported as unavailable; a cable Tab 03 does not know keeps the capacity stored for that same
    /// cable name. No capacity is ever inferred, borrowed from another cable, or defaulted.</param>
    /// <param name="printSettings">Print Setup (header / footer, logo, sheet) embedded into every report and
    /// candidate diagnostic through the shared print engine. A presentation input only.</param>
    /// <param name="outputs">What to write. An output that is not asked for is left exactly as it is on disk and
    /// reported with its existing file, if there is one.</param>
    public StudyArtifactOutcome Generate(
        string studyFolder,
        IReadOnlyDictionary<string, double>? cableCapacities,
        DeflectionLimitBand deflectionBand,
        IReadOnlyCollection<string>? configuredCables = null,
        ReportPrintSettings? printSettings = null,
        StudyOutputs outputs = StudyOutputs.All)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(studyFolder);
        ArgumentNullException.ThrowIfNull(deflectionBand);
        if (deflectionBand.Problem() is { } bandProblem) throw new InvalidOperationException(bandProblem);

        var root = Path.GetFullPath(studyFolder);
        var raw = new StudyRawDataStore().ReadFromStudyFolder(root);
        var results = raw.Results.OrderBy(result => result.Parameters.RunIndex)
            .ThenBy(result => result.IterationKey, StringComparer.Ordinal).ToArray();
        var capacitySet = CableCapacityResolver.Resolve(cableCapacities, configuredCables, raw.CableDesignCapacities);
        var validCapacities = capacitySet.Capacities;

        string? Existing(string fileName) => Path.Combine(root, fileName) is var path && File.Exists(path) ? path : null;

        if (outputs.HasFlag(StudyOutputs.CandidateDiagnostics))
        {
            var diagnosticGenerator = new CandidateDiagnosticReportGenerator();
            foreach (var result in results)
                diagnosticGenerator.Write(result, CandidateDirectory(root, result.IterationKey), printSettings);
        }

        var workbook = Existing(ConsolidatedResultWriter.WorkbookFileName);
        if (outputs.HasFlag(StudyOutputs.Workbook))
        {
            var consolidator = new ConsolidatedResultWriter();
            foreach (var result in results) consolidator.Add(result);
            workbook = consolidator.Write(root, raw.DeflectionPoints, raw.DeflectionOutputCase,
                raw.BaselineModelName, raw.BaselineModelPath, raw.StudyMode, validCapacities, raw.CableResultCase,
                raw.MemberGroups, raw.MemberForceResultCase, raw.FoundationLeftPoints,
                raw.FoundationRightPoints, raw.FoundationReactionResultCase);
        }

        var hasCableReport = results.Any(result => (result.CableForces?.Count ?? 0) > 0);
        var hasMemberReport = results.Any(result => (result.MemberForceGroups?.Count ?? 0) > 0);
        var hasSupportReport = results.Any(result => (result.SupportReactionGroups?.Count ?? 0) > 0);
        var currentReports = new List<string> { "deflection" };
        if (hasMemberReport) currentReports.Add("member");
        if (hasSupportReport) currentReports.Add("support");
        if (hasCableReport) currentReports.Add("cable");
        var deflection = Existing(DeflectionReportGenerator.ReportFileName);
        if (outputs.HasFlag(StudyOutputs.DeflectionReport))
            deflection = new DeflectionReportGenerator().Generate(root, results,
                raw.BaselineModelName, raw.BaselineModelPath, raw.StudyMode, currentReports, deflectionBand,
                printSettings);

        var member = hasMemberReport ? Existing(MemberForceReportGenerator.ReportFileName) : null;
        if (hasMemberReport && outputs.HasFlag(StudyOutputs.MemberForceReport))
            member = new MemberForceReportGenerator().Generate(root, results,
                raw.BaselineModelName, raw.StudyMode, currentReports, printSettings);
        var support = hasSupportReport ? Existing(SupportReactionReportGenerator.ReportFileName) : null;
        if (hasSupportReport && outputs.HasFlag(StudyOutputs.SupportReactionReport))
            support = new SupportReactionReportGenerator().Generate(root, results,
                raw.BaselineModelName, raw.StudyMode, currentReports, printSettings);
        var cable = hasCableReport ? Existing(CableForceReportGenerator.ReportFileName) : null;
        if (hasCableReport && outputs.HasFlag(StudyOutputs.CableForceReport))
            cable = new CableForceReportGenerator().Generate(root, results,
                raw.BaselineModelName, raw.StudyMode, raw.CableResultCase, validCapacities, currentReports,
                capacitySet.Sources, printSettings);

        var rawFolder = Path.Combine(root, StudyRawDataStore.DirectoryName);
        return new(StudyArtifactState.Ready, workbook, deflection, member, support, cable, null,
            Path.Combine(rawFolder, StudyRawDataStore.JsonFileName),
            Path.Combine(rawFolder, StudyRawDataStore.CsvFileName));
    }

    private static string CandidateDirectory(string studyRoot, string iterationKey)
    {
        if (string.IsNullOrWhiteSpace(iterationKey) ||
            !string.Equals(Path.GetFileName(iterationKey), iterationKey, StringComparison.Ordinal) ||
            iterationKey is "." or "..")
            throw new InvalidDataException($"Raw result contains an invalid iteration key: '{iterationKey}'.");

        var directory = Path.GetFullPath(Path.Combine(studyRoot, iterationKey));
        var rootPrefix = Path.TrimEndingDirectorySeparator(studyRoot) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Raw result iteration key escapes the Study folder: '{iterationKey}'.");
        return directory;
    }
}
