using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using COT_VCF.Infrastructure;
using COT_VCF.Domain;

namespace COT_VCF.Application;

public enum StudyRunState { Running, Completed, CompletedWithErrors, Cancelled, Failed }
public enum StudyRunIntent { AllSelected, ResumeInterrupted, RetryFailed, RebuildSelected }

public sealed class StudyRunManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public StudyRunState State { get; set; } = StudyRunState.Running;
    public StudyRunIntent Intent { get; set; }
    public string? ParentRunId { get; set; }
    public string? Message { get; set; }
    public string BaselineModelPath { get; set; } = "";
    public string BaselineSha256 { get; set; } = "";
    public string SnapshotSha256 { get; set; } = "";
    public IterationStudyMode StudyMode { get; set; }
    public int RequestedWorkerCount { get; set; }
    public int SelectedCount { get; set; }
    public List<StudyRunCandidateManifest> Candidates { get; set; } = [];
    public List<StudyRunArtifactManifest> Artifacts { get; set; } = [];
}

public sealed class StudyRunCandidateManifest
{
    public long RunIndex { get; set; }
    public string IterationId { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public bool Selected { get; set; }
    public StudyIterationStatus Status { get; set; }
    public StudyWorkerStage Stage { get; set; }
    public int? WorkerId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Message { get; set; }
}

public sealed record StudyRunArtifactManifest(
    string Kind,
    string Path,
    long Size,
    string Sha256);

public sealed record StudyRunReference(
    string RunId,
    string ManifestPath,
    string BaselineSha256,
    string SnapshotSha256,
    StudyRunState State);

/// <summary>Atomic, thread-safe journal for the latest run in one Study folder.</summary>
public sealed class StudyRunJournal
{
    public const string FileName = "StudyRun.json";
    public const string HistoryDirectoryName = "RunHistory";
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object gate = new();
    private readonly string manifestPath;
    private readonly string historyPath;
    private readonly StudyRunManifest manifest;

    private StudyRunJournal(string manifestPath, string historyPath, StudyRunManifest manifest)
    {
        this.manifestPath = manifestPath;
        this.historyPath = historyPath;
        this.manifest = manifest;
    }

    public string ManifestPath => manifestPath;
    public string RunId => manifest.RunId;

    public static StudyRunJournal Start(
        StudyExecutionSnapshot snapshot,
        StudyRunIntent intent = StudyRunIntent.AllSelected,
        string? parentRunId = null)
    {
        var studyRoot = Path.GetFullPath(new StudyPathService().GetStudyRoot(snapshot.BaselineModelPath));
        Directory.CreateDirectory(studyRoot);
        var manifest = new StudyRunManifest
        {
            StartedAt = DateTimeOffset.UtcNow,
            Intent = intent,
            ParentRunId = parentRunId,
            BaselineModelPath = Path.GetFullPath(snapshot.BaselineModelPath),
            BaselineSha256 = Fingerprints.FileSha256(snapshot.BaselineModelPath),
            SnapshotSha256 = Fingerprints.SnapshotSha256(snapshot),
            StudyMode = snapshot.StudyMode,
            RequestedWorkerCount = snapshot.RequestedWorkerCount,
            SelectedCount = snapshot.Jobs.Count(job => job.RunIterate),
            Candidates = snapshot.Jobs.Select(job => new StudyRunCandidateManifest
            {
                RunIndex = job.RunIndex,
                IterationId = job.IterationId,
                ModelPath = Path.GetFullPath(job.ModelFullPath),
                Selected = job.RunIterate,
                Status = job.RunIterate ? StudyIterationStatus.Queued : StudyIterationStatus.Skipped,
                Stage = StudyWorkerStage.Waiting,
                CompletedAt = job.RunIterate ? null : DateTimeOffset.UtcNow,
                Message = job.RunIterate ? "Queued for dispatch." : "Excluded from this run."
            }).ToList()
        };
        var historyRoot = Path.Combine(studyRoot, HistoryDirectoryName);
        var historyPath = Path.Combine(historyRoot, $"{manifest.StartedAt:yyyyMMddTHHmmssfffZ}_{manifest.RunId}.json");
        var journal = new StudyRunJournal(Path.Combine(studyRoot, FileName), historyPath, manifest);
        journal.Save();
        return journal;
    }

    public void Report(StudyExecutionProgress progress)
    {
        if (string.IsNullOrWhiteSpace(progress.IterationId)) return;
        lock (gate)
        {
            var candidate = manifest.Candidates.FirstOrDefault(item => item.IterationId == progress.IterationId);
            if (candidate is null) return;
            var now = DateTimeOffset.UtcNow;
            if (candidate.StartedAt is null && progress.IterationStatus is not null and not StudyIterationStatus.Queued and not StudyIterationStatus.Skipped)
                candidate.StartedAt = now;
            if (progress.IterationStatus is { } status) candidate.Status = status;
            candidate.Stage = progress.Stage;
            candidate.WorkerId = progress.WorkerId > 0 ? progress.WorkerId : candidate.WorkerId;
            candidate.Message = progress.Message;
            if (candidate.Status is StudyIterationStatus.Ready or StudyIterationStatus.Reused or StudyIterationStatus.Failed or StudyIterationStatus.Cancelled or StudyIterationStatus.Skipped)
                candidate.CompletedAt ??= now;
            SaveLocked();
        }
    }

    public StudyRunReference Complete(StudyExecutionResult result)
    {
        lock (gate)
        {
            manifest.CompletedAt = DateTimeOffset.UtcNow;
            manifest.State = result.AllWorkersFaulted ? StudyRunState.Failed
                : result.Cancelled > 0 ? StudyRunState.Cancelled
                : result.Failed > 0 || result.Artifacts.State == StudyArtifactState.Failed ? StudyRunState.CompletedWithErrors
                : StudyRunState.Completed;
            manifest.Message = result.Artifacts.ErrorMessage;
            manifest.Artifacts = ArtifactEntries(result.Artifacts).ToList();
            SaveLocked();
            return ReferenceLocked();
        }
    }

    public StudyRunReference Fail(Exception error)
    {
        lock (gate)
        {
            manifest.CompletedAt = DateTimeOffset.UtcNow;
            manifest.State = error is OperationCanceledException ? StudyRunState.Cancelled : StudyRunState.Failed;
            manifest.Message = error.Message;
            SaveLocked();
            return ReferenceLocked();
        }
    }

    public StudyRunReference Reference()
    {
        lock (gate) return ReferenceLocked();
    }

    public static StudyRunManifest? TryRead(string studyRoot)
    {
        try
        {
            var path = Path.Combine(Path.GetFullPath(studyRoot), FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<StudyRunManifest>(File.ReadAllText(path), Json) : null;
        }
        catch { return null; }
    }

    public static StudyRunManifest? TryReadLatestCompatible(StudyExecutionSnapshot snapshot)
    {
        try
        {
            var root = Path.GetFullPath(new StudyPathService().GetStudyRoot(snapshot.BaselineModelPath));
            var baselineHash = Fingerprints.FileSha256(snapshot.BaselineModelPath);
            var snapshotHash = Fingerprints.SnapshotSha256(snapshot);
            return ManifestPaths(root).Where(File.Exists)
                .Select(TryReadPath)
                .Where(item => item is not null &&
                    item.BaselineSha256.Equals(baselineHash, StringComparison.OrdinalIgnoreCase) &&
                    item.SnapshotSha256.Equals(snapshotHash, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(item => item!.RunId)
                .OrderByDescending(item => item!.StartedAt)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    public static StudyRunManifest? TryReadCompatibleRun(StudyExecutionSnapshot snapshot, string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) return null;
        try
        {
            var root = Path.GetFullPath(new StudyPathService().GetStudyRoot(snapshot.BaselineModelPath));
            var baselineHash = Fingerprints.FileSha256(snapshot.BaselineModelPath);
            var snapshotHash = Fingerprints.SnapshotSha256(snapshot);
            return ManifestPaths(root).Where(File.Exists).Select(TryReadPath).FirstOrDefault(item =>
                item is not null && item.RunId.Equals(runId, StringComparison.Ordinal) &&
                item.BaselineSha256.Equals(baselineHash, StringComparison.OrdinalIgnoreCase) &&
                item.SnapshotSha256.Equals(snapshotHash, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    public static bool ArtifactsAreIntact(StudyRunManifest manifest) =>
        manifest.Artifacts.Count > 0 && manifest.Artifacts.All(ArtifactIsIntact);

    public static string? ValidArtifactPath(StudyRunManifest manifest, string kind)
    {
        var artifact = manifest.Artifacts.FirstOrDefault(item => item.Kind.Equals(kind, StringComparison.Ordinal));
        return artifact is not null && ArtifactIsIntact(artifact) ? Path.GetFullPath(artifact.Path) : null;
    }

    public static bool RefreshArtifact(string studyRoot, string kind, string path)
    {
        try
        {
            var root = Path.GetFullPath(studyRoot);
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) return false;
            var manifest = TryRead(root);
            if (manifest is null) return false;
            manifest.Artifacts.RemoveAll(item => item.Kind.Equals(kind, StringComparison.Ordinal));
            manifest.Artifacts.Add(new StudyRunArtifactManifest(
                kind, full, new FileInfo(full).Length, Fingerprints.FileSha256(full)));
            AtomicFile.WriteAllText(Path.Combine(root, FileName), JsonSerializer.Serialize(manifest, Json));
            return true;
        }
        catch { return false; }
    }

    private static bool ArtifactIsIntact(StudyRunArtifactManifest entry)
    {
        try
        {
            var full = Path.GetFullPath(entry.Path);
            return File.Exists(full) && new FileInfo(full).Length == entry.Size &&
                   string.Equals(Fingerprints.FileSha256(full), entry.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static IEnumerable<StudyRunArtifactManifest> ArtifactEntries(StudyArtifactOutcome outcome)
    {
        foreach (var (kind, path) in new[]
        {
            ("Workbook", outcome.WorkbookPath),
            ("DeflectionReport", outcome.DeflectionReportPath),
            ("MemberForceReport", outcome.MemberForceReportPath),
            ("SupportReactionReport", outcome.SupportReactionReportPath),
            ("CableForceReport", outcome.CableForceReportPath),
            ("ShortlistWorkbook", outcome.ShortlistWorkbookPath),
            ("ShortlistReport", outcome.ShortlistReportPath)
        })
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            var full = Path.GetFullPath(path);
            yield return new(kind, full, new FileInfo(full).Length, Fingerprints.FileSha256(full));
        }
    }

    private StudyRunReference ReferenceLocked() => new(
        manifest.RunId, manifestPath, manifest.BaselineSha256, manifest.SnapshotSha256, manifest.State);

    private void Save()
    {
        lock (gate) SaveLocked();
    }

    private void SaveLocked()
    {
        var json = JsonSerializer.Serialize(manifest, Json);
        AtomicFile.WriteAllText(manifestPath, json);
        AtomicFile.WriteAllText(historyPath, json);
    }

    private static StudyRunManifest? TryReadPath(string path)
    {
        try { return JsonSerializer.Deserialize<StudyRunManifest>(File.ReadAllText(path), Json); }
        catch { return null; }
    }

    private static IEnumerable<string> ManifestPaths(string studyRoot)
    {
        yield return Path.Combine(studyRoot, FileName);
        var historyRoot = Path.Combine(studyRoot, HistoryDirectoryName);
        if (!Directory.Exists(historyRoot)) yield break;
        foreach (var path in Directory.EnumerateFiles(historyRoot, "*.json")) yield return path;
    }
}

public static class Fingerprints
{
    public static string FileSha256(string path)
    {
        using var stream = File.OpenRead(Path.GetFullPath(path));
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string SnapshotSha256(StudyExecutionSnapshot snapshot)
    {
        var canonical = new
        {
            Baseline = Path.GetFullPath(snapshot.BaselineModelPath),
            snapshot.BaselineModel.Version,
            snapshot.BaselineModel.UnitsCode,
            snapshot.StudyMode,
            Points = snapshot.BaselineGeometry.Points.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { pair.Key, pair.Value.Position.X, pair.Value.Position.Y, pair.Value.Position.Z }),
            Members = snapshot.BaselineGeometry.Members.OrderBy(item => item.Name, StringComparer.Ordinal)
                .Select(item => new { item.Name, item.Start, item.End, item.Kind }),
            snapshot.TopPoints,
            snapshot.MiddlePoints,
            snapshot.BottomPoints,
            OutputGroups = snapshot.OutputGroups.OrderBy(group => group.Name, StringComparer.Ordinal),
            snapshot.DeflectionPoints,
            snapshot.DeflectionOutputCase,
            snapshot.MemberForceResultCase,
            snapshot.FoundationLeftPoints,
            snapshot.FoundationRightPoints,
            snapshot.FoundationReactionResultCase,
            snapshot.CableResultCables,
            snapshot.CableResultLoadCase,
            snapshot.DecisionCriteria,
            CableDesignCapacities = snapshot.CableDesignCapacities.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new { Cable = item.Key, CapacityKN = item.Value }),
            FilteredCandidateKeys = snapshot.Jobs.OrderBy(job => job.RunIndex).Select(job => job.IterationId),
            Jobs = snapshot.Jobs.OrderBy(job => job.RunIndex).Select(job => new
            {
                job.RunIndex, job.IterationId,
                Loads = job.Loading.Loads.OrderBy(load => load.PointName, StringComparer.Ordinal)
                    .ThenBy(load => load.LoadPattern, StringComparer.Ordinal),
                job.Height.Height, job.Height.DeltaH,
                Pretension = job.Pretension.Assignments.OrderBy(item => item.CableName, StringComparer.Ordinal)
            })
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
