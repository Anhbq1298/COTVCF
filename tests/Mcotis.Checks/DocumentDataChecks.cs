using System.Text.Json.Nodes;
using Mcotis.Application;
using Mcotis.Core;
using Mcotis.Persistence;
using static Check;

/// <summary>
/// What a document holds beyond the engineering model (2026-10-14, schema 6): the application data an application keeps
/// with a model while it moves onto the document, and the generator flag that makes a model optimizable. Both are saved
/// and opened with the document, undone and redone with it, and are no part of the analysis input; a content replacement
/// is one undoable step that records what differs.
/// </summary>
internal static class DocumentDataChecks
{
    public static void Run()
    {
        var s = new Sample();
        var hash = s.Document.AnalysisInputHash;
        var flag = GeneratorOrigin.Create("TAS", 1, new Dictionary<string, string> { ["H1"] = "30", ["Bays"] = "12" });
        s.Session.Execute(DocumentCommands.Edit("Flag", editor =>
        {
            editor.SetApplicationData("mcotis.test", "{\"a\":1}");
            editor.SetGenerator(flag);
        }));
        Assert(s.Document.ApplicationData["mcotis.test"] == "{\"a\":1}" && s.Document.Generator == flag && s.Document.AnalysisInputHash == hash,
            "Application data and the generator flag are kept with the document and are no part of the analysis input");

        var read = NativeDocumentFormat.Deserialize(NativeDocumentFormat.Serialize(s.Document));
        Assert(read.ApplicationData.SequenceEqual(s.Document.ApplicationData) && read.Generator == flag && SameContent(read, s.Document),
            "Saved and opened, the document keeps its application data and its generator flag (schema 6)");

        s.Session.Undo();
        Assert(s.Document.ApplicationData.IsEmpty && s.Document.Generator is null, "Undo takes them back as any edit");
        s.Session.Redo();
        Assert(s.Document.Generator == flag, "Redo restores them");
        s.Session.Execute(DocumentCommands.Edit("Detach", editor => editor.SetGenerator(null)));
        Assert(s.Document.Generator is null && s.Session.CanUndo, "Detach removes the flag as one undoable step");
        s.Session.Undo();
        Assert(s.Document.Generator == flag, "… and Undo restores the flag");

        // a schema-5 file opens with no application data and no flag
        var old = (JsonObject)JsonNode.Parse(NativeDocumentFormat.Serialize(new Sample().Document))!;
        old.Remove("applicationData");
        old.Remove("generator");
        old["schemaVersion"] = 5;
        var upgraded = NativeDocumentFormat.Deserialize(old.ToJsonString());
        Assert(upgraded.ApplicationData.IsEmpty && upgraded.Generator is null, "A schema-5 file is upgraded: no application data, not flagged");

        // a content replacement: one step, what differs recorded, undone as a whole
        var before = s.Document;
        var target = before.Edit();
        target.MovePoint(s.P2, before.Points[s.P2].Position + new Xyz(0.5, 0, 0));
        target.SetApplicationData("mcotis.test", "{\"a\":2}");
        var next = target.Commit().Document;
        var changes = s.Session.Execute(DocumentCommands.Edit("Replace content", editor => editor.ReplaceContent(next)));
        Assert(s.Document.Points[s.P2] == next.Points[s.P2] && s.Document.ApplicationData["mcotis.test"] == "{\"a\":2}" &&
               changes.Affects(s.P2) && s.Document.AnalysisInputHash == next.AnalysisInputHash && SameContent(s.Document, next),
            "Replacing the content of a document with that of another revision of the same model is one step that records the point that moved");
        s.Session.Undo();
        Assert(ReferenceEquals(s.Document, before), "Undo returns to the content before the replacement");
        Assert(s.Session.Execute(DocumentCommands.Edit("Same", editor => editor.ReplaceContent(before))).IsEmpty,
            "Replacing the content with the same content changes nothing and adds no step");
        Refused(s.Session, DocumentCommands.Edit("Other model", editor => editor.ReplaceContent(new Sample().Document)), DocumentError.Invalid,
            "The content of another model cannot replace this one's");
    }
}
