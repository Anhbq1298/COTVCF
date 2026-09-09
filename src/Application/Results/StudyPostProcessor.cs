using System.IO;
using COT_VCF.Domain.Results;

namespace COT_VCF.Application.Results;

/// <summary>
/// Tab 06 boundary: derives user-facing Excel/HTML outputs from raw study files only. This service has no SAP
/// gateway and can therefore be rerun after post-processing inputs or the raw JSON package are changed.
/// </summary>
public sealed class StudyPostProcessor
{
    public StudyArtifactOutcome Generate(
        string studyFolder,
        IReadOnlyDictionary<string, double>? cableCapacities,
        StudyDecisionCriteria decisionCriteria)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(studyFolder);
        ArgumentNullException.ThrowIfNull(decisionCriteria);

        var root = Path.GetFullPath(studyFolder);
        var raw = new StudyRawDataStore().ReadFromStudyFolder(root);
        var results = raw.Results.OrderBy(result => result.Parameters.RunIndex)
            .ThenBy(result => result.IterationKey, StringComparer.Ordinal).ToArray();
        var validCapacities = (cableCapacities ?? raw.CableDesignCapacities)
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && double.IsFinite(pair.Value) && pair.Value > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        var diagnosticGenerator = new CandidateDiagnosticReportGenerator();
        foreach (var result in results)
            diagnosticGenerator.Write(result, CandidateDirectory(root, result.IterationKey));

        var consolidator = new ConsolidatedResultWriter();
        foreach (var result in results) consolidator.Add(result);
        var workbook = consolidator.Write(root, raw.DeflectionPoints, raw.DeflectionOutputCase,
            raw.BaselineModelName, raw.BaselineModelPath, raw.StudyMode, validCapacities, raw.CableResultCase);

        var hasCableReport = results.Any(result => (result.CableForces?.Count ?? 0) > 0);
        IReadOnlyList<string> currentReports = hasCableReport
            ? ["deflection", "cable", "shortlist"]
            : ["deflection", "shortlist"];
        var deflection = new DeflectionReportGenerator().Generate(root, results,
            raw.BaselineModelName, raw.BaselineModelPath, raw.StudyMode, currentReports);

        string? cable = null;
        if (hasCableReport)
            cable = new CableForceReportGenerator().Generate(root, results,
                raw.BaselineModelName, raw.StudyMode, raw.CableResultCase, validCapacities, currentReports);

        var decisions = new StudyDecisionService().Evaluate(results, decisionCriteria);
        var shortlist = new StudyShortlistExporter().Export(root, decisions, decisionCriteria,
            siblingReports: currentReports);
        var rawFolder = Path.Combine(root, StudyRawDataStore.DirectoryName);
        return new(StudyArtifactState.Ready, workbook, deflection, cable, null,
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
