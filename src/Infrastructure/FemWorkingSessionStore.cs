using System.Text.Json;
using System.IO;
using COT_VCF.Configuration;

namespace COT_VCF.Infrastructure;

public sealed record FemWorkingSession(string WorkingDatabasePath, string? InputDatabasePath, DateTime UpdatedUtc);

/// <summary>Crash-recoverable FEM working database. The manifest never contains model data, only the active paths.</summary>
public sealed class FemWorkingSessionStore(string? rootDirectory = null)
{
    private readonly string root = rootDirectory ?? AppDataPaths.SessionsDirectory;
    private string ManifestPath => Path.Combine(root, "active-session.json");

    public string Save(FemInputSnapshot snapshot, IReadOnlyList<FemOutputRegistration> outputs, string? inputPath)
    {
        var directory = Path.Combine(root, snapshot.DatabaseId.ToString("N"));
        Directory.CreateDirectory(directory);
        var working = Path.Combine(directory, "working" + FemInputDatabase.Extension);
        FemInputDatabase.Write(working, snapshot, outputs);
        var manifest = new FemWorkingSession(working, inputPath, DateTime.UtcNow);
        AtomicFile.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest));
        return working;
    }

    public FemWorkingSession? TryReadManifest()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return null;
            var manifest = JsonSerializer.Deserialize<FemWorkingSession>(File.ReadAllText(ManifestPath));
            return manifest is not null && File.Exists(manifest.WorkingDatabasePath) ? manifest : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    public FemInputDatabaseRead? TryReadActive()
    {
        var manifest = TryReadManifest();
        if (manifest is null) return null;
        try { return FemInputDatabase.Read(manifest.WorkingDatabasePath); }
        catch (IOException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (Microsoft.Data.Sqlite.SqliteException) { return null; }
    }
}
