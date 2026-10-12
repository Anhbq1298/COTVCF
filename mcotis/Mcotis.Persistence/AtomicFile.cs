using System.Text;

namespace Mcotis.Persistence;

/// <summary>Writes a temporary file beside the destination, then replaces the destination in one step. When the
/// writer fails the destination is exactly what it was.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string destination, string contents) =>
        Write(destination, temporary => File.WriteAllText(temporary, contents, new UTF8Encoding(false)));

    public static void Write(string destination, Action<string> writeTemporaryFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(writeTemporaryFile);

        var fullPath = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("The destination has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            writeTemporaryFile(temporary);
            if (!File.Exists(temporary)) throw new IOException("The writer did not create its temporary file.");
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null, ignoreMetadataErrors: true);
            else File.Move(temporary, fullPath);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { /* a leftover temporary file is harmless; the destination is already safe */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
