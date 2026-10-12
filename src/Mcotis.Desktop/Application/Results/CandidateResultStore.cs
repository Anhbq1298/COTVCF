using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcotis.Desktop.Domain.Results;
using Mcotis.Desktop.Infrastructure;

namespace Mcotis.Desktop.Application.Results;

/// <summary>
/// Durable, per-candidate result payload used to rebuild a complete consolidated result set after an
/// interrupted run. The frozen snapshot hash prevents a result from another study definition being merged.
/// </summary>
public sealed class CandidateResultStore
{
    public const string FileName = "Candidate_Result.json";
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly StudyExecutionSnapshot snapshot;
    private readonly string snapshotSha256;
    private readonly IReadOnlyDictionary<string, StudyIterationExecutionDto> jobs;

    public CandidateResultStore(StudyExecutionSnapshot snapshot)
    {
        this.snapshot = snapshot;
        snapshotSha256 = Fingerprints.SnapshotSha256(snapshot);
        jobs = snapshot.Jobs.ToDictionary(job => job.IterationId, StringComparer.Ordinal);
    }

    public void Write(CandidateResultDto result)
    {
        if (result.ExtractionStatus is not (CandidateExtractionStatus.ExtractedOk or CandidateExtractionStatus.ExtractedWithWarning))
            return;
        if (!jobs.TryGetValue(result.IterationKey, out var job))
            throw new InvalidOperationException($"Candidate result {result.IterationKey} is not part of the frozen study.");
        ValidateIdentity(job, result);
        var path = Path.Combine(job.IterationDirectory, FileName);
        var envelope = new CandidateResultEnvelope(SchemaVersion, snapshotSha256, result);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(envelope, Json));
    }

    public IReadOnlyList<CandidateResultDto> Read(IReadOnlySet<string> includedIterationIds)
    {
        var results = new List<CandidateResultDto>();
        foreach (var id in includedIterationIds.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (!jobs.TryGetValue(id, out var job)) continue;
            try
            {
                var path = Path.Combine(job.IterationDirectory, FileName);
                if (!File.Exists(path)) continue;
                var envelope = JsonSerializer.Deserialize<CandidateResultEnvelope>(File.ReadAllText(path), Json);
                if (envelope is null || envelope.SchemaVersion != SchemaVersion ||
                    !envelope.SnapshotSha256.Equals(snapshotSha256, StringComparison.OrdinalIgnoreCase)) continue;
                ValidateIdentity(job, envelope.Result);
                if (envelope.Result.ExtractionStatus is CandidateExtractionStatus.ExtractedOk or CandidateExtractionStatus.ExtractedWithWarning)
                    results.Add(envelope.Result);
            }
            catch
            {
                // A corrupt or stale sidecar is never authoritative. The current candidate can still be rerun.
            }
        }
        return results.OrderBy(result => result.Parameters.RunIndex).ToArray();
    }

    private void ValidateIdentity(StudyIterationExecutionDto job, CandidateResultDto result)
    {
        if (!result.IterationKey.Equals(job.IterationId, StringComparison.Ordinal) ||
            !Path.GetFullPath(result.CandidateModelPath).Equals(Path.GetFullPath(job.ModelFullPath), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(result.BaselineModelPath).Equals(Path.GetFullPath(snapshot.BaselineModelPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Candidate result identity does not match the frozen study job {job.IterationId}.");
    }

    private sealed record CandidateResultEnvelope(int SchemaVersion, string SnapshotSha256, CandidateResultDto Result);
}
