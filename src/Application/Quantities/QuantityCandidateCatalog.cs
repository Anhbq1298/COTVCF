using System.IO;
using COT_VCF.Application.Results;
using COT_VCF.Domain;

namespace COT_VCF.Application.Quantities;

/// <summary>When a study run worked on a candidate, from the run journal.</summary>
public sealed record QuantityRunWindow(DateTimeOffset StartedAt, DateTimeOffset? CompletedAt);

/// <summary>One candidate model of a study, as the study's own records and folders identify it.</summary>
public sealed record QuantityCandidate(
    string Id,
    string ModelPath,
    string TextExportPath,
    string? RecordStatus,
    string? RecordWarning,
    IReadOnlyList<QuantityRunWindow> RunWindows)
{
    public override string ToString() => Id;

    public bool Equals(QuantityCandidate? other) =>
        other is not null && Id == other.Id && RecordStatus == other.RecordStatus && RecordWarning == other.RecordWarning &&
        string.Equals(ModelPath, other.ModelPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(TextExportPath, other.TextExportPath, StringComparison.OrdinalIgnoreCase) && RunWindows.SequenceEqual(other.RunWindows);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id);
}

/// <summary>
/// Lists the candidate models of the study that belongs to a baseline model. A candidate is resolved through the
/// study's records: the run journal (<c>StudyRun.json</c> and its history) names each <c>Lxx_Hxx_Pxx</c> and its model,
/// and <see cref="StudyPathService"/> gives the path a candidate of this baseline always has. Only candidates whose
/// model, or SAP2000's text copy of it, is on disk are listed. Nothing is written.
/// </summary>
public sealed class QuantityCandidateCatalog
{
    public const string TextExportExtension = ".$2k";
    private readonly StudyPathService paths = new();

    public IReadOnlyList<QuantityCandidate> Discover(string? baselineModelPath)
    {
        string root, baseline;
        try
        {
            baseline = Path.GetFullPath(baselineModelPath ?? "");
            root = Path.GetFullPath(paths.GetStudyRoot(baseline));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException) { return []; }
        try
        {
            if (!Directory.Exists(root)) return [];
            var records = StudyRunJournal.ReadAll(root)
                .SelectMany(manifest => manifest.Candidates.Select(candidate => (Manifest: manifest, Candidate: candidate)))
                .Where(record => StudyPathService.IsIterationId(record.Candidate.IterationId))
                .ToLookup(record => record.Candidate.IterationId, StringComparer.Ordinal);
            var folders = Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>().Where(StudyPathService.IsIterationId);
            return records.Select(group => group.Key).Concat(folders).Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, NaturalNameComparer.Instance)
                .Select(id => Resolve(baseline, root, id, records[id].ToArray())).OfType<QuantityCandidate>().ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private QuantityCandidate? Resolve(string baseline, string root, string id, (StudyRunManifest Manifest, StudyRunCandidateManifest Candidate)[] records)
    {
        var folder = paths.GetIterationDirectory(baseline, id);
        var model = paths.GetIterationModelPath(baseline, id);
        if (!File.Exists(model))
        {
            // A study keeps its children when the baseline is renamed afterwards: take the model the run journal
            // recorded, provided it is inside this study's own candidate folder, or the one model that folder holds.
            var recorded = records.Select(record => record.Candidate.ModelPath).FirstOrDefault(path => IsIn(folder, path) && File.Exists(path));
            var only = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.sdb") : [];
            if (recorded is not null) model = Path.GetFullPath(recorded);
            else if (only.Length == 1) model = only[0];
        }
        var export = Path.ChangeExtension(model, TextExportExtension);
        if (!File.Exists(model) && !File.Exists(export)) return null;

        // Records are newest first. The one that counts is the latest run that worked on the candidate, not a later
        // run that left it out.
        static bool Worked(StudyRunCandidateManifest record) =>
            record.Status is not (StudyIterationStatus.Planned or StudyIterationStatus.Queued or StudyIterationStatus.Skipped);
        var latest = records.Select(record => record.Candidate).FirstOrDefault(Worked) ?? records.FirstOrDefault().Candidate;
        var windows = records.Where(record => record.Candidate.StartedAt is not null)
            .Select(record => new QuantityRunWindow(record.Candidate.StartedAt!.Value, record.Candidate.CompletedAt ?? record.Manifest.CompletedAt)).ToArray();
        var warning = latest is null || !Worked(latest) || latest.Status is StudyIterationStatus.Ready or StudyIterationStatus.Reused
            ? null
            : $"The study run records {id} as {latest.Status}" + (latest.Stage is StudyWorkerStage.Completed or StudyWorkerStage.Waiting ? "" : $" at {latest.Stage}") +
              "; its model may not carry the whole candidate.";
        return new(id, model, export, latest?.Status.ToString(), warning, windows);
    }

    private static bool IsIn(string folder, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
