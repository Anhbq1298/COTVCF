using System.IO;
using System.Text.Json;
namespace COT_VCF.Infrastructure;

public static class RecoveryJournal
{
    public static string Save<T>(string operation, T data)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Recovery");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{operation}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}
