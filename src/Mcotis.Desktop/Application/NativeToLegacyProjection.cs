using System.Text.Json;
using Mcotis.Desktop.Configuration;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Application;

/// <summary>
/// The one bridge between the main window's native document and the model the application still keeps of its own
/// (task of 2026-10-14, M2 — temporary, deleted with the last consumer of the old model in M7).
/// <para><see cref="Write"/> makes the document of a configuration: the engineering model as native objects
/// (<see cref="LegacyNativeDocumentMigration.Convert"/>), and — under <see cref="ConfigurationKey"/> of the document's
/// application data — everything of the configuration the native model has no place for yet: the section and material
/// libraries as the forms edit them, the Tab 01 / Tab 03 setup the storey loads and cable targets are made from, the
/// extras of the load case rows. <see cref="Read"/> gives back that configuration from a document, read-only: the main
/// window's Undo and Redo go back to a document and restore the configuration it holds. As each part of the
/// configuration moves into the native model, it leaves the application data.</para>
/// <para>What changes from one save to the next and is not the model — the revision counter, the time of the save, the
/// lock of a run — is not written: two documents of the same model are equal.</para>
/// </summary>
internal static class NativeToLegacyProjection
{
    public const string ConfigurationKey = "mcotis.desktop.configuration";

    /// <summary>The document of <paramref name="snapshot"/>; refusals as <see cref="LegacyNativeDocumentMigration.Convert"/>.</summary>
    public static Native.ModelDocument Write(FemInputSnapshot snapshot, LegacyNativeDocumentMigration.Source source)
    {
        var document = LegacyNativeDocumentMigration.Convert(snapshot, source);
        var editor = document.Edit();
        WriteConfiguration(editor, snapshot);
        editor.SetGenerator(snapshot.Generator?.ToNative());
        return editor.Commit().Document;
    }

    /// <summary>Updates only the transitional main-window configuration carried by an existing native document.
    /// Native-first commands use this in the same commit as their engineering edit, so Undo restores both views of
    /// the model as one step. Delete this with the bridge after the last legacy consumer moves to the document.</summary>
    public static void WriteConfiguration(Native.DocumentEditor editor, FemInputSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(snapshot);
        editor.SetApplicationData(ConfigurationKey, Serialize(snapshot));
    }

    /// <summary>The configuration a document of the main window holds, or null for a document that holds none.</summary>
    public static FemInputSnapshot? Read(Native.ModelDocument document) =>
        document.ApplicationData.TryGetValue(ConfigurationKey, out var json)
            ? JsonSerializer.Deserialize<FemInputSnapshot>(json, FemInputDatabase.Json) : null;

    /// <summary>Whether the document already carries this exact transitional configuration.</summary>
    public static bool HoldsConfiguration(Native.ModelDocument document, FemInputSnapshot snapshot) =>
        document.ApplicationData.TryGetValue(ConfigurationKey, out var json) &&
        string.Equals(json, Serialize(snapshot), StringComparison.Ordinal);

    private static string Serialize(FemInputSnapshot snapshot)
    {
        var copy = JsonSerializer.Deserialize<FemInputSnapshot>(JsonSerializer.Serialize(snapshot, FemInputDatabase.Json), FemInputDatabase.Json)!;
        copy.Revision = 0;
        copy.SavedUtc = default;
        copy.IsLocked = false;
        copy.LockedRevision = null;
        return JsonSerializer.Serialize(copy, FemInputDatabase.Json);
    }
}
