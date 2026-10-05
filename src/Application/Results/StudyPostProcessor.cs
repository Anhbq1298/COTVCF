using System.IO;
using COT_VCF.Domain.Results;

namespace COT_VCF.Application.Results;

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
    public StudyArtifactOutcome Generate(
        string studyFolder,
        IReadOnlyDictionary<string, double>? cableCapacities,
        StudyDecisionCriteria decisionCriteria,
        IReadOnlyCollection<string>? configuredCables = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(studyFolder);
        ArgumentNullException.ThrowIfNull(decisionCriteria);

        var root = Path.GetFullPath(studyFolder);
        var raw = new StudyRawDataStore().ReadFromStudyFolder(root);
        var results = raw.Results.OrderBy(result => result.Parameters.RunIndex)
            .ThenBy(result => result.IterationKey, StringComparer.Ordinal).ToArray();
        var capacitySet = CableCapacityResolver.Resolve(cableCapacities, configuredCables, raw.CableDesignCapacities);
        var validCapacities = capacitySet.Capacities;

        var diagnosticGenerator = new CandidateDiagnosticReportGenerator();
        foreach (var result in results)
            diagnosticGenerator.Write(result, CandidateDirectory(root, result.IterationKey));

        var consolidator = new ConsolidatedResultWriter();
        foreach (var result in results) consolidator.Add(result);
        var workbook = consolidator.Write(root, raw.DeflectionPoints, raw.DeflectionOutputCase,
            raw.BaselineModelName, raw.BaselineModelPath, raw.StudyMode, validCapacities, raw.CableResultCase,
            raw.MemberGroups, raw.MemberForceResultCase, raw.FoundationLeftPoints,
            raw.FoundationRightPoints, raw.FoundationReactionResultCase);

        var hasCableReport = results.Any(result => (result.CableForces?.Count ?? 0) > 0);
        var hasMemberReport = results.Any(result => (result.MemberForceGroups?.Count ?? 0) > 0);
        var hasSupportReport = results.Any(result => (result.SupportReactionGroups?.Count ?? 0) > 0);
        var currentReports = new List<string> { "deflection" };
        if (hasMemberReport) currentReports.Add("member");
        if (hasSupportReport) currentReports.Add("support");
        if (hasCableReport) currentReports.Add("cable");
        currentReports.Add("shortlist");
        var deflection = new DeflectionReportGenerator().Generate(root, results,
            raw.BaselineModelName, raw.BaselineModelPath, raw.StudyMode, currentReports, decisionCriteria.DeflectionBand);

        string? member = null;
        if (hasMemberReport)
            member = new MemberForceReportGenerator().Generate(root, results,
                raw.BaselineModelName, raw.StudyMode, currentReports);
        string? support = null;
        if (hasSupportReport)
            support = new SupportReactionReportGenerator().Generate(root, results,
                raw.BaselineModelName, raw.StudyMode, currentReports);
        string? cable = null;
        if (hasCableReport)
            cable = new CableForceReportGenerator().Generate(root, results,
                raw.BaselineModelName, raw.StudyMode, raw.CableResultCase, validCapacities, currentReports,
                capacitySet.Sources);

        var decisions = new StudyDecisionService().Evaluate(results, decisionCriteria);
        var shortlist = new StudyShortlistExporter().Export(root, decisions, decisionCriteria,
            siblingReports: currentReports);
        var rawFolder = Path.Combine(root, StudyRawDataStore.DirectoryName);
        return new(StudyArtifactState.Ready, workbook, deflection, member, support, cable, null,
            shortlist.WorkbookPath, shortlist.HtmlPath,
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
