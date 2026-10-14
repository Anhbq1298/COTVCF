using System.IO;
using System.Text.Json;
using Mcotis.Persistence;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Configuration;

/// <summary>Where the run of a saved model stands: the revision, whether it is locked for its results, and the output
/// databases of that locked revision. It is the file's, not the model's — two documents of the same model are equal
/// whether they were run or not — so it travels beside the document in the file and never enters the session.</summary>
public sealed record McotisRunState(long Revision, bool IsLocked, long? LockedRevision, IReadOnlyList<FemOutputRegistration> Outputs);

/// <summary>
/// The main window's file since 2026-10-14 (M6 of the main-window migration): a native <c>.mcotis</c> document of
/// <see cref="Mcotis.Persistence"/>, written in one step (<see cref="NativeDocumentStore"/>). It holds the model the
/// window shows — the document of <c>MainViewModel.NativeDocumentSession</c>, with the configuration the controls not yet
/// moved onto the document still read (<c>NativeToLegacyProjection</c>) — and, under <see cref="RunStateKey"/>, the run
/// state of the file (<see cref="McotisRunState"/>). An older <c>.cotfem.db</c> / <c>.cotfem.json</c> is opened as an
/// import: its model becomes a document, and Save writes a <c>.mcotis</c>; the older file is never written.
/// </summary>
internal static class McotisModelFile
{
    public const string Extension = ".mcotis";
    public const string Filter = "Mcotis model (*.mcotis)|*.mcotis";
    public const string OpenFilter = Filter +
        "|Older MCOTiS configuration, opened as an import (*.cotfem.db;*.cotfem.json)|*.cotfem.db;*.cotfem.json";
    /// <summary>The application data key of the run state in a file; removed from the document when it is read.</summary>
    public const string RunStateKey = "mcotis.desktop.runState";

    public static bool IsModelFile(string? path) => path?.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The folder beside a model file that holds what its runs produced: <c>&lt;name&gt;.results</c>, where the
    /// output databases of Run FEM Analysis are too.</summary>
    public static string ResultsFolder(string modelPath)
    {
        var full = Path.GetFullPath(modelPath);
        return Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + ".results");
    }

    private static readonly JsonSerializerOptions BesideJson = new() { IncludeFields = true };

    /// <summary>Writes <paramref name="value"/> as <paramref name="name"/> in the model's results folder, in one step.</summary>
    public static void WriteBeside<T>(string modelPath, string name, T value)
    {
        var path = Path.Combine(ResultsFolder(modelPath), name);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(value, BesideJson));
    }

    /// <summary>What <see cref="WriteBeside{T}"/> wrote, or null when there is none or it cannot be read.</summary>
    public static T? ReadBeside<T>(string modelPath, string name) where T : class
    {
        var path = Path.Combine(ResultsFolder(modelPath), name);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), BesideJson) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    /// <summary>A configuration file of the application before the document was its file.</summary>
    public static bool IsOlderFile(string? path) =>
        path is not null && (path.EndsWith(FemInputDatabase.Extension, StringComparison.OrdinalIgnoreCase) ||
                             path.EndsWith(FemModelFile.Extension, StringComparison.OrdinalIgnoreCase));

    /// <summary><paramref name="path"/> as a <c>.mcotis</c> file: the extension added, or put in place of an older one.</summary>
    public static string WithExtension(string path)
    {
        var trimmed = path.TrimEnd('.', ' ');
        if (IsModelFile(trimmed)) return trimmed;
        foreach (var older in new[] { FemInputDatabase.Extension, FemModelFile.Extension })
            if (trimmed.EndsWith(older, StringComparison.OrdinalIgnoreCase)) return trimmed[..^older.Length] + Extension;
        return trimmed + Extension;
    }

    /// <summary>Writes <paramref name="document"/> to <paramref name="path"/> in one step, with the run state beside it.
    /// The file is either the new one or, when the write fails, exactly what it was.</summary>
    public static void Write(string path, Native.ModelDocument document, McotisRunState? state)
    {
        ArgumentNullException.ThrowIfNull(document);
        var written = document;
        if (state is not null)
        {
            var editor = document.Edit();
            editor.SetApplicationData(RunStateKey, JsonSerializer.Serialize(state, FemInputDatabase.Json));
            written = editor.Commit().Document;
        }
        new NativeDocumentStore().Save(path, written);
    }

    /// <summary>The document a file holds, without its run state, and that run state (null for a file without one — a
    /// model of the native workspace, a file never run). Nothing is changed when the file cannot be read: the
    /// exception (<see cref="DocumentFileException"/>) says why.</summary>
    public static (Native.ModelDocument Document, McotisRunState? State) Read(string path)
    {
        var document = new NativeDocumentStore().Load(path);
        if (!document.ApplicationData.TryGetValue(RunStateKey, out var json)) return (document, null);
        McotisRunState? state;
        try { state = JsonSerializer.Deserialize<McotisRunState>(json, FemInputDatabase.Json); }
        catch (JsonException ex) { throw new DocumentFileException($"The run state of '{Path.GetFileName(path)}' cannot be read: {ex.Message}", ex); }
        var editor = document.Edit();
        editor.SetApplicationData(RunStateKey, null);
        return (editor.Commit().Document, state);
    }
}
