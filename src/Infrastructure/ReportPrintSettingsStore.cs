using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using COT_VCF.Domain;

namespace COT_VCF.Infrastructure;

/// <summary>Keeps the report Print Setup (company, logo, header / footer texts, default sheet) for the current
/// Windows user, so it follows the engineer from project to project. It is not part of the COT VCF project file
/// and never touches a study folder.</summary>
public sealed class ReportPrintSettingsStore
{
    public const string FileName = "ReportPrintSettings.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ReportPrintSettingsStore(string? filePath = null) =>
        FilePath = filePath ?? Path.Combine(AppDataPaths.RootDirectory, FileName);

    public string FilePath { get; }

    /// <summary>The saved settings, or defaults when nothing was saved yet or the file cannot be read.</summary>
    public ReportPrintSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ReportPrintSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // An unreadable preference file must never stop the application; start from defaults.
        }
        return new();
    }

    public bool TrySave(ReportPrintSettings settings)
    {
        try
        {
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Json));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
