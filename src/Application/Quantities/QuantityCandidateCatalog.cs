using System.IO;
using COT_VCF.Application.Results;
using COT_VCF.Domain;

namespace COT_VCF.Application.Quantities;

/// <summary>When a study run worked on a candidate, from the run journal.</summary>
public sealed record QuantityRunWindow(DateTimeOffset StartedAt, DateTimeOffset? CompletedAt);

/// <summary>One model the take-off can be made for: a candidate model of a study, as the study's own records and
/// folders identify it — or the model that is open in the connected SAP2000 right now.</summary>
public sealed record QuantityCandidate(
    string Id,
    string ModelPath,
    string TextExportPath,
    string? RecordStatus,
    string? RecordWarning,
    IReadOnlyList<QuantityRunWindow> RunWindows)
{
    public const string ConnectedModelId = "Connected model";

    /// <summary>The model open in the connected SAP2000. It is read live through the API every time — never from a
    /// file on disk — so it shows the model as it is now, saved or not.</summary>
    public bool IsConnectedModel { get; init; }

    public static QuantityCandidate Connected(ModelInfo model) => new(ConnectedModelId, model.Path, "", null, null, []) { IsConnectedModel = true };

    public override string ToString() => Id;

    public bool Equals(QuantityCandidate? other) =>
        other is not null && Id == other.Id && IsConnectedModel == other.IsConnectedModel && RecordStatus == other.RecordStatus && RecordWarning == other.RecordWarning &&
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
        string primaryRoot, baseline;
        try
        {
            baseline = Path.GetFullPath(baselineModelPath ?? "");
            primaryRoot = Path.GetFullPath(paths.GetStudyRoot(baseline));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException) { return []; }
        try
        {
            // Current studies are isolated under Study/<baseline stem>. Older studies used Study directly;
            // keep that layout readable so quantity take-off still works after upgrading or renaming a baseline.
            var parent = Path.GetDirectoryName(baseline)!;
            var legacyRoot = Path.Combine(parent, "Study");
            return new[] { primaryRoot, Path.GetFullPath(legacyRoot) }
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists)
                .SelectMany(root => DiscoverRoot(baseline, primaryRoot, root))
                .GroupBy(candidate => candidate.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(candidate => candidate.Id, NaturalNameComparer.Instance)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private IReadOnlyList<QuantityCandidate> DiscoverRoot(string baseline, string primaryRoot, string root)
    {
        var records = StudyRunJournal.ReadAll(root)
                .SelectMany(manifest => manifest.Candidates.Select(candidate => (Manifest: manifest, Candidate: candidate)))
                .Where(record => StudyPathService.IsIterationId(record.Candidate.IterationId))
                .ToLookup(record => record.Candidate.IterationId, StringComparer.Ordinal);
        var folders = Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>().Where(StudyPathService.IsIterationId);
        return records.Select(group => group.Key).Concat(folders).Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, NaturalNameComparer.Instance)
            .Select(id => Resolve(baseline, primaryRoot, root, id, records[id].ToArray())).OfType<QuantityCandidate>().ToArray();
    }

    private QuantityCandidate? Resolve(string baseline, string primaryRoot, string root, string id, (StudyRunManifest Manifest, StudyRunCandidateManifest Candidate)[] records)
    {
        var folder = Path.Combine(root, id);
        var model = string.Equals(root, primaryRoot, StringComparison.OrdinalIgnoreCase)
            ? paths.GetIterationModelPath(baseline, id)
            : Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(baseline)}_{id}.sdb");
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
        if (!File.Exists(model) && !File.Exists(export))
        {
            // No model at all: the folder may still hold SAP2000's text copy of it.
            var exports = Directory.Exists(folder) ? Directory.GetFiles(folder, "*" + TextExportExtension) : [];
            if (exports.Length != 1) return null;
            export = exports[0];
            model = Path.ChangeExtension(export, ".sdb");
        }

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
