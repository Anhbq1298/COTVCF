using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using COT_VCF.Domain;
using COT_VCF.Infrastructure;

namespace COT_VCF.Application.Results;

/// <summary>The candidate metrics of one raw-data package, with enough of the package's identity to tell when
/// they no longer belong to it.</summary>
public sealed record StudyMetricsIndex(
    long RawLength,
    DateTime RawLastWriteUtc,
    DateTimeOffset RawGeneratedAt,
    string BaselineModelName,
    IterationStudyMode StudyMode,
    IReadOnlyDictionary<string, double> StoredCableCapacities,
    IReadOnlyList<CandidateMetrics> Candidates)
{
    /// <summary>Identifies the raw data a result was derived from.</summary>
    public string RawIdentity => $"{RawGeneratedAt.UtcDateTime:O}|{RawLength}";
}

/// <summary>Derives the Tab 06A candidate metrics from <c>Study_Raw_Results.json</c> once and keeps them in a
/// small sidecar beside it. The raw package grows with every stored station of every candidate; the metrics do
/// not, so criteria are re-applied to them instead of to the package. The sidecar is a rebuildable cache: it is
/// used only while it still matches the raw file, and nothing else reads it.</summary>
public sealed class StudyMetricsStore
{
    public const string FileName = "Study_Candidate_Metrics.json";
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record Sidecar(int SchemaVersion, StudyMetricsIndex Index);

    public static string RawPath(string studyFolder) =>
        Path.Combine(Path.GetFullPath(studyFolder), StudyRawDataStore.DirectoryName, StudyRawDataStore.JsonFileName);

    public static string SidecarPath(string studyFolder) =>
        Path.Combine(Path.GetFullPath(studyFolder), StudyRawDataStore.DirectoryName, FileName);

    /// <summary>The metrics of the study's current raw data: from the sidecar when it still matches the raw
    /// file, otherwise derived from the raw package (and the sidecar rewritten).</summary>
    public StudyMetricsIndex Load(string studyFolder)
    {
        var raw = new FileInfo(RawPath(studyFolder));
        if (!raw.Exists) throw new FileNotFoundException($"No {StudyRawDataStore.JsonFileName} raw-data package was found.", raw.FullName);
        if (TryReadSidecar(studyFolder) is { } cached && cached.RawLength == raw.Length && cached.RawLastWriteUtc == raw.LastWriteTimeUtc)
            return cached;

        var index = Build(new StudyRawDataStore().Read(raw.FullName), raw);
        try { AtomicFile.WriteAllText(SidecarPath(studyFolder), JsonSerializer.Serialize(new Sidecar(SchemaVersion, index), Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }     // a cache: the study still works without it
        return index;
    }

    public static StudyMetricsIndex Build(StudyRawDataPackage package, FileInfo raw) => new(
        raw.Length, raw.LastWriteTimeUtc, package.GeneratedAt, package.BaselineModelName, package.StudyMode,
        new Dictionary<string, double>(package.CableDesignCapacities, StringComparer.Ordinal),
        package.Results.Select(CandidateMetrics.From).ToArray());

    private static StudyMetricsIndex? TryReadSidecar(string studyFolder)
    {
        try
        {
            var path = SidecarPath(studyFolder);
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            var sidecar = JsonSerializer.Deserialize<Sidecar>(stream, Json);
            return sidecar is { SchemaVersion: SchemaVersion, Index.Candidates: not null, Index.StoredCableCapacities: not null } ? sidecar.Index : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }
}
