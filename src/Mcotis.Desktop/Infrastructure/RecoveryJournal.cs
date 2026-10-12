using System.IO;
using System.Text.Json;
namespace COT_VCF.Infrastructure;

public static class RecoveryJournal
{
    public static string Save<T>(string operation, T data)
    {
        var directory = AppDataPaths.EnsureDirectory(AppDataPaths.RecoveryDirectory);
        var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{operation}-{Guid.NewGuid():N}.json");
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}
