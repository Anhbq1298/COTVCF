using System.IO;
using System.Text;

namespace Mcotis.Desktop.Infrastructure;

/// <summary>Same-volume temporary write followed by atomic replacement. The destination is untouched when the writer fails.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string destination, string contents, Encoding? encoding = null) =>
        Write(destination, temp => File.WriteAllText(temp, contents, encoding ?? new UTF8Encoding(false)));

    public static void Write(string destination, Action<string> writeTemporaryFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(writeTemporaryFile);

        var fullPath = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("The destination directory is unavailable.");
        Directory.CreateDirectory(directory);
        var extension = Path.GetExtension(fullPath);
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        var temporary = Path.Combine(directory, $".{stem}.{Guid.NewGuid():N}.tmp{extension}");

        try
        {
            writeTemporaryFile(temporary);
            if (!File.Exists(temporary)) throw new IOException("The atomic writer did not create its temporary file.");
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null, true);
            else File.Move(temporary, fullPath);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { /* best effort cleanup; the destination has already been preserved */ }
        }
    }
}
