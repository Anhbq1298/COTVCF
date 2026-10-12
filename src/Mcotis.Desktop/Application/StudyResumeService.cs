using System.Text.Json;
using System.IO;
using COT_VCF.Domain;
using COT_VCF.Infrastructure;

namespace COT_VCF.Application;

public enum StudyCandidateRunDisposition
{
    NoPreviousRun,
    Resumable,
    Retryable,
    Reusable,
    Excluded,
    Blocked
}

public sealed record StudyCandidateRunAssessment(
    string IterationId,
    StudyCandidateRunDisposition Disposition,
    string Reason);

public sealed record StudyResumeAssessment(
    bool Compatible,
    StudyRunManifest? Manifest,
    IReadOnlyList<StudyCandidateRunAssessment> Candidates,
    string Message)
{
    public IReadOnlySet<string> ResumeIds => Candidates
        .Where(item => item.Disposition == StudyCandidateRunDisposition.Resumable)
        .Select(item => item.IterationId).ToHashSet(StringComparer.Ordinal);
    public IReadOnlySet<string> RetryIds => Candidates
        .Where(item => item.Disposition == StudyCandidateRunDisposition.Retryable)
        .Select(item => item.IterationId).ToHashSet(StringComparer.Ordinal);
}

public sealed class StudyResumeService
{
    public StudyResumeAssessment Assess(StudyExecutionSnapshot snapshot)
    {
        var manifest = StudyRunJournal.TryReadLatestCompatible(snapshot);
        if (manifest is null)
        {
            var latest = StudyRunJournal.TryRead(new StudyPathService().GetStudyRoot(snapshot.BaselineModelPath));
            var reason = latest is null
                ? "No prior study manifest exists for this baseline."
                : "The latest run belongs to a different baseline or study definition.";
            return new(false, latest, snapshot.Jobs.Select(job => new StudyCandidateRunAssessment(
                job.IterationId, StudyCandidateRunDisposition.Blocked, reason)).ToArray(), reason);
        }

        var prior = manifest.Candidates.ToDictionary(item => item.IterationId, StringComparer.Ordinal);
        var rows = snapshot.Jobs.Select(job => AssessCandidate(job, prior)).ToArray();
        var resumable = rows.Count(item => item.Disposition == StudyCandidateRunDisposition.Resumable);
        var retryable = rows.Count(item => item.Disposition == StudyCandidateRunDisposition.Retryable);
        var reusable = rows.Count(item => item.Disposition == StudyCandidateRunDisposition.Reusable);
        return new(true, manifest, rows,
            $"Compatible run {Short(manifest.RunId)}: {resumable:N0} resumable, {retryable:N0} retryable, {reusable:N0} already verified.");
    }

    public StudyExecutionSnapshot Select(
        StudyExecutionSnapshot snapshot,
        StudyResumeAssessment assessment,
        StudyRunIntent intent)
    {
        if (intent is StudyRunIntent.ResumeInterrupted or StudyRunIntent.RetryFailed && !assessment.Compatible)
            throw new InvalidOperationException(assessment.Message);
        IReadOnlySet<string> eligible = intent switch
        {
            StudyRunIntent.ResumeInterrupted => assessment.ResumeIds,
            StudyRunIntent.RetryFailed => assessment.RetryIds,
            _ => snapshot.Jobs.Where(job => job.RunIterate).Select(job => job.IterationId).ToHashSet(StringComparer.Ordinal)
        };
        if (eligible.Count == 0)
            throw new InvalidOperationException(intent == StudyRunIntent.ResumeInterrupted
                ? "The compatible run has no unfinished candidates to resume."
                : intent == StudyRunIntent.RetryFailed
                    ? "The compatible run has no failed or cancelled candidates to retry."
                    : "Select at least one study iteration.");
        return snapshot with
        {
            Jobs = snapshot.Jobs.Select(job => job with { RunIterate = eligible.Contains(job.IterationId) }).ToArray()
        };
    }

    private static StudyCandidateRunAssessment AssessCandidate(
        StudyIterationExecutionDto job,
        IReadOnlyDictionary<string, StudyRunCandidateManifest> prior)
    {
        if (!prior.TryGetValue(job.IterationId, out var candidate))
            return new(job.IterationId, StudyCandidateRunDisposition.NoPreviousRun,
                "This candidate was not present in the compatible run; use Build & Analyze.");
        if (!candidate.Selected || candidate.Status == StudyIterationStatus.Skipped)
            return new(job.IterationId, StudyCandidateRunDisposition.Excluded,
                "This candidate was excluded from the compatible run.");
        if (candidate.Status is StudyIterationStatus.Ready or StudyIterationStatus.Reused)
            return new(job.IterationId, StudyCandidateRunDisposition.Reusable,
                "The prior run verified this analyzed child; a normal run will re-open and verify it without overwriting.");
        if (candidate.Status is StudyIterationStatus.Failed or StudyIterationStatus.Cancelled)
            return new(job.IterationId, StudyCandidateRunDisposition.Retryable,
                $"The prior run ended {candidate.Status}; Retry dispatches this candidate only.");
        return new(job.IterationId, StudyCandidateRunDisposition.Resumable,
            $"The prior run stopped at {candidate.Stage}; Resume safely continues through existing-child verification or a clean build.");
    }

    private static string Short(string id) => id[..Math.Min(8, id.Length)];
}

public sealed record StudyRebuildArchiveResult(string ArchiveRoot, IReadOnlyList<string> ArchivedIterationIds);

/// <summary>Moves selected candidate folders to a recoverable sibling archive before an explicit rebuild.</summary>
public sealed class StudyRebuildArchive
{
    public StudyRebuildArchiveResult Archive(StudyExecutionSnapshot snapshot)
    {
        var paths = new StudyPathService();
        var studyRoot = Path.GetFullPath(paths.GetStudyRoot(snapshot.BaselineModelPath));
        var archiveRoot = Path.Combine(studyRoot, "RebuildArchive",
            $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}_{Guid.NewGuid():N}");
        var moved = new List<string>();
        foreach (var job in snapshot.Jobs.Where(job => job.RunIterate && Directory.Exists(job.IterationDirectory)))
        {
            var source = Path.GetFullPath(job.IterationDirectory);
            var expected = Path.GetFullPath(paths.GetIterationDirectory(snapshot.BaselineModelPath, job.IterationId));
            if (!source.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                !source.StartsWith(studyRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Refusing to archive unsafe candidate path: {source}");
            Directory.CreateDirectory(archiveRoot);
            var destination = Path.Combine(archiveRoot, job.IterationId);
            if (Directory.Exists(destination))
                throw new IOException($"Rebuild archive destination already exists: {destination}");
            Directory.Move(source, destination);
            moved.Add(job.IterationId);
            WriteIndex(archiveRoot, snapshot, moved);
        }
        return new(archiveRoot, moved);
    }

    private static void WriteIndex(string root, StudyExecutionSnapshot snapshot, IReadOnlyList<string> moved)
    {
        var payload = new
        {
            archivedAt = DateTimeOffset.UtcNow,
            baselineModelPath = Path.GetFullPath(snapshot.BaselineModelPath),
            snapshotSha256 = Fingerprints.SnapshotSha256(snapshot),
            iterationIds = moved
        };
        AtomicFile.WriteAllText(Path.Combine(root, "RebuildArchive.json"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }
}
