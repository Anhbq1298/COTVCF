using System.IO;

namespace Mcotis.Desktop.Infrastructure;

/// <summary>Writable, per-user application storage. Never place mutable recovery or log data beside the executable.</summary>
public static class AppDataPaths
{
    public static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mcotis");

    public static string RecoveryDirectory => Path.Combine(RootDirectory, "Recovery");
    public static string LogsDirectory => Path.Combine(RootDirectory, "Logs");
    public static string SessionsDirectory => Path.Combine(RootDirectory, "Sessions");

    public static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
