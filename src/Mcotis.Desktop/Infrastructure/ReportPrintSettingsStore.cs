using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Infrastructure;

/// <summary>Keeps the report Print Setup (company, logo, header / footer texts, default sheet) for the current
/// Windows user — what the Print Setup card shows when the application starts and what a project without a
/// Print Setup of its own is printed with. A Mcotis project file can carry its own (<c>printSetup</c>): opening
/// that project puts it on the card, and so here. Never touches a study folder.</summary>
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
