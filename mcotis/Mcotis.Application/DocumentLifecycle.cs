using Mcotis.Core;

namespace Mcotis.Application;

/// <summary>Reads and writes a whole document. Implemented outside this layer (Mcotis.Persistence).</summary>
public interface IDocumentStore
{
    /// <summary>A complete, valid document, or an exception — never a partly read one.</summary>
    ModelDocument Load(string path);
    /// <summary>Replaces the file with this document, or leaves the file as it was.</summary>
    void Save(string path, ModelDocument document);
}

/// <summary>New, Open, Save and Save As on a session. Save writes the document the session holds — it never reads
/// the model again from anywhere else — and Open changes the session only once the file has become a valid document.</summary>
public sealed class DocumentLifecycle(DocumentSession session, IDocumentStore store)
{
    public void New() => session.Replace(ModelDocument.Create(), null);

    public void Open(string path)
    {
        var full = Path.GetFullPath(path);
        var document = store.Load(full);   // throws → the session is untouched
        session.Replace(document, full);
    }

    public void Save() => SaveAs(session.FilePath ?? throw new InvalidOperationException("The document has no file yet; use Save As."));

    public void SaveAs(string path)
    {
        var full = Path.GetFullPath(path);
        var document = session.Document;
        store.Save(full, document);        // throws → the session still counts as unsaved
        session.MarkSaved(document, full);
    }
}
