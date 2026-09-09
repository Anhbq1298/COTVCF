using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;
using COT_VCF.Infrastructure;

namespace COT_VCF.Application.Results;

/// <summary>The complete SAP-independent input consumed by Tab 06 post-processing.</summary>
public sealed class StudyRawDataPackage
{
    public int SchemaVersion { get; set; } = StudyRawDataStore.SchemaVersion;
    public DateTimeOffset GeneratedAt { get; set; }
    public string BaselineModelName { get; set; } = "";
    public string BaselineModelPath { get; set; } = "";
    public IterationStudyMode StudyMode { get; set; }
    public List<string> DeflectionPoints { get; set; } = [];
    public string DeflectionOutputCase { get; set; } = "";
    public string CableResultCase { get; set; } = "";
    public Dictionary<string, double> CableDesignCapacities { get; set; } = new(StringComparer.Ordinal);
    public StudyDecisionCriteria DecisionCriteria { get; set; } = new(50, 75, null, null, 0.9, 0.5, 0.3, 0.2);
    public List<CandidateResultDto> Results { get; set; } = [];
}

public sealed record StudyRawDataPaths(string JsonPath, string CsvPath);

/// <summary>
/// Writes the only artifacts produced by a model run: an authoritative lossless JSON package and a flat,
/// spreadsheet-friendly CSV view. Neither file is a presentation report; Tab 06 consumes the JSON package.
/// </summary>
public sealed class StudyRawDataStore
{
    public const int SchemaVersion = 1;
    public const string DirectoryName = "RawData";
    public const string JsonFileName = "Study_Raw_Results.json";
    public const string CsvFileName = "Study_Raw_Results.csv";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public StudyRawDataPaths Write(
        string studyFolder,
        StudyExecutionSnapshot snapshot,
        IReadOnlyList<CandidateResultDto> results)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(studyFolder);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(results);

        var rawFolder = Path.Combine(Path.GetFullPath(studyFolder), DirectoryName);
        Directory.CreateDirectory(rawFolder);
        var ordered = results.OrderBy(result => result.Parameters.RunIndex)
            .ThenBy(result => result.IterationKey, StringComparer.Ordinal).ToList();
        var package = new StudyRawDataPackage
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            BaselineModelName = snapshot.BaselineModel.Name,
            BaselineModelPath = Path.GetFullPath(snapshot.BaselineModelPath),
            StudyMode = snapshot.StudyMode,
            DeflectionPoints = snapshot.DeflectionPoints.ToList(),
            DeflectionOutputCase = snapshot.DeflectionOutputCase,
            CableResultCase = snapshot.CableResultLoadCase,
            CableDesignCapacities = snapshot.CableDesignCapacities
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && double.IsFinite(pair.Value) && pair.Value > 0)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            DecisionCriteria = snapshot.DecisionCriteria,
            Results = ordered,
        };

        var jsonPath = Path.Combine(rawFolder, JsonFileName);
        var csvPath = Path.Combine(rawFolder, CsvFileName);
        AtomicFile.WriteAllText(jsonPath, JsonSerializer.Serialize(package, Json));
        AtomicFile.WriteAllText(csvPath, BuildCsv(ordered));
        return new(jsonPath, csvPath);
    }

    public StudyRawDataPackage ReadFromStudyFolder(string studyFolder) =>
        Read(Path.Combine(Path.GetFullPath(studyFolder), DirectoryName, JsonFileName));

    public StudyRawDataPackage Read(string jsonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
        var full = Path.GetFullPath(jsonPath);
        if (!File.Exists(full)) throw new FileNotFoundException($"No {JsonFileName} raw-data package was found.", full);
        var package = JsonSerializer.Deserialize<StudyRawDataPackage>(File.ReadAllText(full), Json)
            ?? throw new InvalidDataException($"{JsonFileName} is empty or invalid.");
        if (package.SchemaVersion != SchemaVersion)
            throw new InvalidDataException($"Unsupported raw-data schema {package.SchemaVersion}; expected {SchemaVersion}.");
        if (package.Results is null || package.Results.Count == 0)
            throw new InvalidDataException($"{JsonFileName} contains no candidate results.");
        package.Results = package.Results.OrderBy(result => result.Parameters.RunIndex)
            .ThenBy(result => result.IterationKey, StringComparer.Ordinal).ToList();
        package.DeflectionPoints ??= [];
        package.CableDesignCapacities ??= new(StringComparer.Ordinal);
        return package;
    }

    private static string BuildCsv(IReadOnlyList<CandidateResultDto> results)
    {
        var b = new StringBuilder();
        Row(b, "RecordType", "IterationKey", "RunIndex", "StudyMode", "CandidateModelPath", "BaselineModelPath",
            "LoadingCandidateId", "LoadLevel_kPa", "LoadingLabel", "HeightCandidateId", "TrussHeight_m", "DeltaH_m",
            "PretensionCandidateId", "Pretension_kN", "PretensionLabel", "GroupName", "OutputCase", "ObjectName",
            "Sequence", "X_m", "Y_m", "Z_m", "Component", "TargetForce_kN", "Value", "Unit", "Status", "Warning", "Timestamp");

        foreach (var result in results)
        {
            var p = result.Parameters;
            void DataRow(string type, string? group = null, string? outputCase = null, string? objectName = null,
                int? sequence = null, double? x = null, double? y = null, double? z = null, string? component = null,
                double? target = null, double? value = null, string? unit = null, object? status = null,
                string? warning = null, DateTimeOffset? timestamp = null) =>
                Row(b, type, result.IterationKey, p.RunIndex, result.StudyMode, result.CandidateModelPath, result.BaselineModelPath,
                    p.LoadingCandidateId, p.LoadLevelKPa, p.LoadingLabel, p.HeightCandidateId, p.TrussHeightM, p.DeltaHM,
                    p.PretensionCandidateId, p.PretensionKN, p.PretensionLabel, group, outputCase, objectName, sequence,
                    x, y, z, component, target, value, unit, status, warning, timestamp);

            DataRow("Candidate", status: result.ExtractionStatus, timestamp: result.ExtractionCompletedAt);
            foreach (var group in result.Groups ?? [])
                foreach (var point in group.Points ?? [])
                    DataRow("Deflection", group.GroupName, group.OutputCase, point.PointName, point.Sequence,
                        point.X, point.Y, point.Z, "Uz", value: point.UzMm, unit: "mm", status: point.Status,
                        warning: point.Warning, timestamp: result.ExtractionCompletedAt);
            foreach (var cable in result.CableForces ?? [])
                DataRow("CableForce", outputCase: null, objectName: cable.CableName, component: "P",
                    target: cable.TargetForceKN, value: cable.ForceKN, unit: "kN", status: cable.Status,
                    warning: cable.Warning, timestamp: result.ExtractionCompletedAt);
            foreach (var warning in result.Warnings ?? [])
                DataRow("Warning", group: warning.Category, status: warning.Severity,
                    warning: warning.Message, timestamp: warning.Timestamp);
        }
        return b.ToString();
    }

    private static void Row(StringBuilder b, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0) b.Append(',');
            var text = values[i] switch
            {
                null => "",
                double value => value.ToString("R", CultureInfo.InvariantCulture),
                float value => value.ToString("R", CultureInfo.InvariantCulture),
                DateTimeOffset value => value.ToString("O", CultureInfo.InvariantCulture),
                IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
                _ => values[i]!.ToString() ?? ""
            };
            if (text.IndexOfAny([',', '"', '\r', '\n']) >= 0)
                b.Append('"').Append(text.Replace("\"", "\"\"")).Append('"');
            else b.Append(text);
        }
        b.AppendLine();
    }
}
