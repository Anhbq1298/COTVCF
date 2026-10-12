using System.Globalization;
using System.IO;
using System.Text.Json;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;
using COT_VCF.Infrastructure;

namespace COT_VCF.Application.Results;

public sealed record OptimizationArtifacts(
    string Folder,
    string SummaryPath,
    string RecordPath,
    string? DeflectionReportPath,
    string? MemberForceReportPath,
    string? SupportReactionReportPath,
    string? CableForceReportPath,
    StudyQualification Qualification);

/// <summary>What an optimized report set was generated from — written beside it as
/// <c>Optimization_Criteria.json</c>, so the set stays traceable and Tab 06A can tell when it is out of date.</summary>
public sealed record OptimizationRecord(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    string RawIdentity,
    bool DeflectionEnabled,
    double? DeflectionMinMm,
    double? DeflectionMaxMm,
    bool CableEnabled,
    double MaxCableUtilization,
    string CapacitySignature,
    int TotalCandidates,
    IReadOnlyList<string> QualifiedIterationKeys);

/// <summary>Tab 06A: writes the optimized report set — the existing reports restricted to the qualified
/// candidates, plus a summary page — into <c>Study/Optimized/</c>. Reads the raw package only; the full reports
/// in the study folder are never touched. No SAP2000.</summary>
public sealed class StudyOptimizationProcessor
{
    public const string FolderName = "Optimized";
    public const string RecordFileName = "Optimization_Criteria.json";
    public const string SetLabel = "Optimized set";
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string FolderOf(string studyFolder) => Path.Combine(Path.GetFullPath(studyFolder), FolderName);

    /// <summary>One line saying what the set is: how many qualify, under which criteria.</summary>
    public static string Note(StudyQualification q) =>
        $"Qualified candidates: {q.Summary.Qualified.ToString("N0", CultureInfo.InvariantCulture)} of {q.Summary.Total.ToString("N0", CultureInfo.InvariantCulture)} · {q.Criteria.Label}";

    /// <param name="cableCapacities">Capacities resolved now from Tab 03; they win over the ones stored with the
    /// results, exactly as for the Cable Forces report.</param>
    public OptimizationArtifacts Generate(
        string studyFolder,
        QualificationCriteria criteria,
        IReadOnlyDictionary<string, double>? cableCapacities,
        IReadOnlyCollection<string>? configuredCables = null,
        ReportPrintSettings? printSettings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(studyFolder);
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Problem() is { } problem) throw new InvalidOperationException(problem);

        var root = Path.GetFullPath(studyFolder);
        var rawFile = new FileInfo(StudyMetricsStore.RawPath(root));
        var raw = new StudyRawDataStore().Read(rawFile.FullName);
        var results = raw.Results;
        var index = StudyMetricsStore.Build(raw, rawFile);
        var capacitySet = CableCapacityResolver.Resolve(cableCapacities, configuredCables, raw.CableDesignCapacities);
        var qualification = StudyQualifier.Evaluate(index.Candidates, criteria, capacitySet.Capacities);

        var keys = qualification.Qualified.Select(c => c.Metrics.IterationKey).ToHashSet(StringComparer.Ordinal);
        var subset = results.Where(result => keys.Contains(result.IterationKey)).ToArray();
        var scope = ScopeOf(qualification);

        var folder = FolderOf(root);
        Directory.CreateDirectory(folder);
        var hasMember = subset.Any(result => (result.MemberForceGroups?.Count ?? 0) > 0);
        var hasSupport = subset.Any(result => (result.SupportReactionGroups?.Count ?? 0) > 0);
        var hasCable = subset.Any(result => (result.CableForces?.Count ?? 0) > 0);
        var hasDeflection = subset.Length > 0;
        var siblings = new List<string> { "optimization" };
        if (hasDeflection) siblings.Add("deflection");
        if (hasMember) siblings.Add("member");
        if (hasSupport) siblings.Add("support");
        if (hasCable) siblings.Add("cable");

        // a report of an earlier set that this one does not rewrite would show other candidates under this set's name
        void Clear(bool keep, params string[] fileNames)
        {
            if (keep) return;
            foreach (var name in fileNames) { var path = Path.Combine(folder, name); if (File.Exists(path)) File.Delete(path); }
        }
        Clear(hasDeflection, DeflectionReportGenerator.ReportFileName);
        Clear(hasMember, MemberForceReportGenerator.ReportFileName, MemberForceReportGenerator.TopChordFileName,
            MemberForceReportGenerator.BottomChordFileName, MemberForceReportGenerator.MegaColumnFileName);
        Clear(hasSupport, SupportReactionReportGenerator.ReportFileName);
        Clear(hasCable, CableForceReportGenerator.ReportFileName);

        var deflection = hasDeflection
            ? new DeflectionReportGenerator().Generate(folder, subset, raw.BaselineModelName, raw.BaselineModelPath, raw.StudyMode,
                siblings, criteria.DeflectionBand, printSettings, scope)
            : null;
        var member = hasMember
            ? new MemberForceReportGenerator().Generate(folder, subset, raw.BaselineModelName, raw.StudyMode, siblings, printSettings, scope)
            : null;
        var support = hasSupport
            ? new SupportReactionReportGenerator().Generate(folder, subset, raw.BaselineModelName, raw.StudyMode, siblings, printSettings, scope)
            : null;
        var cable = hasCable
            ? new CableForceReportGenerator().Generate(folder, subset, raw.BaselineModelName, raw.StudyMode, raw.CableResultCase,
                capacitySet.Capacities, siblings, capacitySet.Sources, printSettings, scope)
            : null;
        var summary = new OptimizationSummaryReport().Generate(folder, qualification, raw.BaselineModelName, raw.StudyMode, siblings, printSettings);

        var recordPath = Path.Combine(folder, RecordFileName);
        AtomicFile.WriteAllText(recordPath, JsonSerializer.Serialize(RecordOf(index, qualification, capacitySet.Capacities), Json));
        return new(folder, summary, recordPath, deflection, member, support, cable, qualification);
    }

    /// <summary>The scope every report of the set carries: the full study's ids, and every iteration outside it.</summary>
    public static ReportScope ScopeOf(StudyQualification q)
    {
        string[] Ids(Func<CandidateMetrics, string> id) => q.Candidates.Select(c => id(c.Metrics)).Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, NaturalNameComparer.Instance).ToArray();
        return new(SetLabel, Note(q), Ids(m => m.LoadId), Ids(m => m.HeightId), Ids(m => m.PretensionId),
            q.Candidates.Where(c => c.Status != QualificationStatus.Qualified)
                .Select(c => new[] { c.Metrics.LoadId, c.Metrics.HeightId, c.Metrics.PretensionId }).ToArray());
    }

    public static OptimizationRecord RecordOf(StudyMetricsIndex index, StudyQualification q, IReadOnlyDictionary<string, double> capacities) => new(
        SchemaVersion, DateTimeOffset.Now, index.RawIdentity,
        q.Criteria.DeflectionEnabled, q.Criteria.DeflectionBand.MinMm, q.Criteria.DeflectionBand.MaxMm,
        q.Criteria.CableEnabled, q.Criteria.MaxCableUtilization, CapacitySignature(index, capacities), q.Summary.Total,
        q.Qualified.Select(c => c.Metrics.IterationKey).ToArray());

    /// <summary>The capacities the cables of this study were checked against — a change in any of them changes
    /// which candidates qualify.</summary>
    public static string CapacitySignature(StudyMetricsIndex index, IReadOnlyDictionary<string, double> capacities) =>
        string.Join(";", index.Candidates.SelectMany(c => c.Cables).Select(c => c.CableName).Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => $"{name}={(capacities.TryGetValue(name, out var capacity) && CableCapacityResolver.IsCapacity(capacity) ? capacity.ToString("R", CultureInfo.InvariantCulture) : "none")}"));

    public static OptimizationRecord? ReadRecord(string studyFolder)
    {
        try
        {
            var path = Path.Combine(FolderOf(studyFolder), RecordFileName);
            if (!File.Exists(path)) return null;
            var record = JsonSerializer.Deserialize<OptimizationRecord>(File.ReadAllText(path), Json);
            return record is { SchemaVersion: SchemaVersion, QualifiedIterationKeys: not null } ? record : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Whether an optimized set was generated from this raw data, these criteria and these capacities.</summary>
    public static bool IsCurrent(OptimizationRecord record, StudyMetricsIndex index, QualificationCriteria criteria,
        IReadOnlyDictionary<string, double> capacities) =>
        record.RawIdentity == index.RawIdentity &&
        record.DeflectionEnabled == criteria.DeflectionEnabled &&
        Nullable.Equals(record.DeflectionMinMm, criteria.DeflectionBand.MinMm) &&
        Nullable.Equals(record.DeflectionMaxMm, criteria.DeflectionBand.MaxMm) &&
        record.CableEnabled == criteria.CableEnabled &&
        record.MaxCableUtilization.Equals(criteria.MaxCableUtilization) &&
        record.CapacitySignature == CapacitySignature(index, capacities);
}
