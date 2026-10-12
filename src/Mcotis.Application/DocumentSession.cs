using Mcotis.Core;

namespace Mcotis.Application;

/// <summary>One change of the model the user can undo. Every change of the model is one of these.</summary>
public interface IDocumentCommand
{
    /// <summary>What Undo and Redo call it.</summary>
    string Name { get; }
    void Apply(DocumentEditor editor);
}

public sealed class DocumentCommand(string name, Action<DocumentEditor> apply) : IDocumentCommand
{
    public string Name { get; } = name;
    public void Apply(DocumentEditor editor) => apply(editor);
}

public enum DocumentChangeReason { Command, Undo, Redo, Replaced }

public sealed class DocumentChangedEventArgs(DocumentChangeReason reason, string? commandName, DocumentChangeSet changes,
    ModelDocument before, ModelDocument after) : EventArgs
{
    public DocumentChangeReason Reason { get; } = reason;
    public string? CommandName { get; } = commandName;
    /// <summary>What differs between <see cref="Before"/> and <see cref="After"/>.</summary>
    public DocumentChangeSet Changes { get; } = changes;
    public ModelDocument Before { get; } = before;
    public ModelDocument After { get; } = after;
}

/// <summary>The open model: the current document, its history and its saved state. It belongs to one thread (the UI's);
/// the documents it hands out never change, so an analysis or a save may read one from any thread.</summary>
public sealed class DocumentSession
{
    public const int DefaultHistoryLimit = 200;

    private sealed record Step(string Name, ModelDocument Before, ModelDocument After, DocumentChangeSet Changes);

    private readonly LinkedList<Step> undo = new();
    private readonly Stack<Step> redo = new();
    private readonly int historyLimit;
    private ModelDocument saved;

    public DocumentSession(ModelDocument? document = null, int historyLimit = DefaultHistoryLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(historyLimit);
        this.historyLimit = historyLimit;
        Document = saved = document ?? ModelDocument.Create();
    }

    public ModelDocument Document { get; private set; }
    /// <summary>Counts every change of the document, Undo and Redo included. It only ever goes up.</summary>
    public long Revision { get; private set; }
    public string? FilePath { get; private set; }
    /// <summary>The document differs from what its file holds. Undoing back to the saved document is clean again.</summary>
    public bool IsDirty => !ReferenceEquals(Document, saved);
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public string? UndoName => undo.Last?.Value.Name;
    public string? RedoName => redo.TryPeek(out var step) ? step.Name : null;

    public event EventHandler<DocumentChangedEventArgs>? Changed;
    /// <summary>The file or the saved state changed while the document stayed as it is (Save, Save As).</summary>
    public event EventHandler? SavedStateChanged;

    /// <summary>Applies a command as one undoable step. A command that is refused throws
    /// <see cref="DocumentException"/> and leaves the document, the history and the revision as they were.</summary>
    public DocumentChangeSet Execute(IDocumentCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var editor = Document.Edit();
        command.Apply(editor);
        var commit = editor.Commit();
        if (commit.Changes.IsEmpty) return commit.Changes;

        undo.AddLast(new Step(command.Name, Document, commit.Document, commit.Changes));
        while (undo.Count > historyLimit) undo.RemoveFirst();
        redo.Clear();
        MoveTo(commit.Document, DocumentChangeReason.Command, command.Name, commit.Changes);
        return commit.Changes;
    }

    /// <summary>Back to the document before the last command — the same objects with the ids and names they had.</summary>
    public bool Undo()
    {
        if (undo.Last is not { } last) return false;
        undo.RemoveLast();
        redo.Push(last.Value);
        MoveTo(last.Value.Before, DocumentChangeReason.Undo, last.Value.Name, last.Value.Changes.Inverse());
        return true;
    }

    public bool Redo()
    {
        if (!redo.TryPop(out var step)) return false;
        undo.AddLast(step);
        MoveTo(step.After, DocumentChangeReason.Redo, step.Name, step.Changes);
        return true;
    }

    /// <summary>Makes a complete, valid document the open one (New, Open): no history, clean.</summary>
    public void Replace(ModelDocument document, string? filePath)
    {
        ArgumentNullException.ThrowIfNull(document);
        undo.Clear();
        redo.Clear();
        saved = document;
        FilePath = filePath;
        MoveTo(document, DocumentChangeReason.Replaced, null, DocumentChangeSet.Reset);
    }

    /// <summary>Records that <paramref name="written"/> — the document that was handed to the store — is now what the file holds.</summary>
    public void MarkSaved(ModelDocument written, string filePath)
    {
        saved = written;
        FilePath = filePath;
        SavedStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MoveTo(ModelDocument next, DocumentChangeReason reason, string? commandName, DocumentChangeSet changes)
    {
        var before = Document;
        Document = next;
        Revision++;
        Changed?.Invoke(this, new(reason, commandName, changes, before, next));
    }
}
