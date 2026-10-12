using System.Text.Json.Nodes;
using Mcotis.Application;
using Mcotis.Core;
using Mcotis.Persistence;
using static Check;

/// <summary>The native file: what is saved is what is on screen, what is opened is all of the file or nothing.</summary>
internal static class PersistenceChecks
{
    /// <summary>A schema-0 file kept its points under "joints"; this is its upgrade to schema 1.</summary>
    private sealed class JointsToPoints : IDocumentMigration
    {
        public int FromVersion => 0;
        public void Upgrade(JsonObject file)
        {
            var joints = file["joints"];
            file.Remove("joints");
            file["points"] = joints;
        }
    }

    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mcotis-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            RoundTrip();
            Lifecycle(folder);
            Refusals();
            Migrations(folder);
            AtomicSave(folder);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void RoundTrip()
    {
        var s = new Sample();
        var saved = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
        var json = NativeDocumentFormat.Serialize(s.Document, saved);
        var read = NativeDocumentFormat.Deserialize(json);
        Assert(SameContent(read, s.Document), "A document read back from its file has every object, definition, assignment, load, case and setting it was saved with");
        Assert(read.Points[s.P3].Name == "P3" && read.Cables[s.C1] is { Name: "C1" } cable && cable.StartPointId == s.P1 && cable.EndPointId == s.P3 &&
               !read.Frames.ContainsKey(s.C1) && read.Frames.Count == 3 &&
               read.Shells[s.S1].PointIds.SequenceEqual(new[] { s.P2, s.P3, s.P5, s.P6 }) && read.Points[s.P5].Origin is { Kind: OriginKind.Generated, GenerationKey: "slab:corner:3" },
            "Ids, names, connectivity and origins survive Save and Open");
        Assert(read.AssignmentOfFrame(s.F3).Modifiers.Inertia33 == 0.7 && read.AssignmentOfFrame(s.F1).LocalAxisAngle == 90 && read.RestraintOf(s.P4) == Restraint.Pinned &&
               read.AssignmentOfShell(s.S1).SectionId == s.Slab && read.FrameSections[s.Beam].Dimensions["tf"] == 0.021 &&
               read.AssignmentOfCable(s.C1) == new CableAssignment(s.CableSection, new(1, 1, 1.05)) && read.CableSections[s.CableSection] is { Name: "19T15.7", Area: 0.00285 } &&
               read.Loads[s.Target] is CableTargetForce { Force: 500 } target && target.TargetId == s.C1,
            "Assignments survive Save and Open — a cable's section, modifiers and target force among them");
        Assert(read.AnalysisInputHash == s.Document.AnalysisInputHash && read.TryFindObject("c1", out var found) && found == s.C1,
            "The reopened document has the same analysis input and a working name index");
        Assert(NativeDocumentFormat.Serialize(read, saved) == json, "Saving the reopened document writes the same file: the order in the file does not depend on history");

        // The name sequence is in the file: a name deleted before the save is not handed out after the open.
        ObjectId gone = ObjectId.New(), next = ObjectId.New();
        s.Session.Execute(DocumentCommands.AddCable(gone, s.P4, s.P2));
        s.Session.Execute(DocumentCommands.AddFrame(ObjectId.New(), s.P1, s.P3));
        s.Session.Execute(DocumentCommands.Remove([gone, s.F3]));
        var reopened = new DocumentSession(NativeDocumentFormat.Deserialize(NativeDocumentFormat.Serialize(s.Document)));
        Assert(reopened.Document.Names.LastNumber(ObjectKind.Cable) == 2 && reopened.Document.Names.LastNumber(ObjectKind.Frame) == 4 &&
               reopened.Document.Frames.Values.Select(frame => frame.Name).Order().SequenceEqual(["F1", "F2", "F4"]),
            "Save and Open keep the names of the frames that remain and how far each sequence has got");
        reopened.Execute(DocumentCommands.AddCable(next, s.P4, s.P2));
        Assert(reopened.Document.Cables[next].Name == "C3", "After Open the next cable continues the sequence past the cable deleted before the save");

        var empty = ModelDocument.Create();
        var emptyAgain = NativeDocumentFormat.Deserialize(NativeDocumentFormat.Serialize(empty));
        Assert(emptyAgain.DocumentId == empty.DocumentId && SameContent(emptyAgain, empty), "An empty model can be saved and opened");
    }

    private static void Lifecycle(string folder)
    {
        var s = new Sample();
        var store = new NativeDocumentStore();
        var lifecycle = new DocumentLifecycle(s.Session, store);
        var path = Path.Combine(folder, "portal" + NativeDocumentFormat.Extension);

        Throws<InvalidOperationException>(lifecycle.Save, "Save without a file asks for Save As");
        Assert(s.Session.IsDirty, "A model that was never saved is dirty");
        lifecycle.SaveAs(path);
        Assert(File.Exists(path) && !s.Session.IsDirty && s.Session.FilePath == path && s.Session.CanUndo, "Save As writes the file, makes it the model's file and keeps the history");

        // Save writes what the session holds — after edits and undo, exactly that.
        s.Session.Execute(DocumentCommands.MovePoints([s.P3], new Xyz(0.5, 0, 0)));
        s.Session.Execute(DocumentCommands.Rename(s.F2, "Rafter"));
        s.Session.Undo();
        lifecycle.Save();
        var onDisk = store.Load(path);
        Assert(SameContent(onDisk, s.Document) && onDisk.Points[s.P3].Position == new Xyz(6.5, 0, 5) && onDisk.Frames[s.F2].Name == "F2" && !s.Session.IsDirty,
            "Save writes the document as it is on screen: the move that stands, not the rename that was undone");
        Assert(Directory.GetFiles(folder).Length == 1, "Save leaves no temporary file behind");

        var other = new DocumentSession();
        var opener = new DocumentLifecycle(other, store);
        opener.Open(path);
        Assert(SameContent(other.Document, s.Document) && other.FilePath == path && !other.IsDirty && !other.CanUndo, "Open makes the file the model: clean, without history");

        // A file that cannot be opened leaves the model that is open exactly as it is.
        other.Execute(DocumentCommands.Rename(s.P1, "Base"));
        var (document, revision, file) = (other.Document, other.Revision, other.FilePath);
        var broken = Path.Combine(folder, "broken" + NativeDocumentFormat.Extension);
        File.WriteAllText(broken, File.ReadAllText(path)[..^40]);
        Throws<DocumentFileException>(() => opener.Open(broken), "A truncated file is refused");
        Throws<DocumentFileException>(() => opener.Open(Path.Combine(folder, "missing.mcotis")), "A file that is not there is refused");
        Assert(ReferenceEquals(other.Document, document) && other.Revision == revision && other.FilePath == file && other.IsDirty && other.UndoName == "Rename",
            "A failed Open leaves the open model, its file, its revision and its history untouched");

        opener.New();
        Assert(other.Document.Points.IsEmpty && other.FilePath is null && !other.IsDirty && !other.CanUndo && other.Document.DocumentId != document.DocumentId,
            "New starts an empty, clean model of its own");
    }

    private static void Refusals()
    {
        var s = new Sample();
        var json = NativeDocumentFormat.Serialize(s.Document);
        static JsonObject Parse(string text) => (JsonObject)JsonNode.Parse(text)!;
        DocumentFileException Refuse(Action<JsonObject> damage, string description)
        {
            var file = Parse(json);
            damage(file);
            return Throws<DocumentFileException>(() => NativeDocumentFormat.Deserialize(file.ToJsonString()), description);
        }
        JsonObject Item(JsonObject file, string list, ObjectId id) => file[list]!.AsArray().Select(node => node!.AsObject()).Single(item => (string?)item["id"] == id.ToString());

        Throws<DocumentFileException>(() => NativeDocumentFormat.Deserialize("not json"), "A file that is not JSON is refused");
        Throws<DocumentFileException>(() => NativeDocumentFormat.Deserialize("[1, 2]"), "A JSON file that is not a document is refused");
        Refuse(file => file["format"] = "COTVCF.FemModel", "A file of another format is refused");
        Refuse(file => file.Remove("schemaVersion"), "A file without a schema version is refused");
        var newer = Refuse(file => file["schemaVersion"] = NativeDocumentFormat.CurrentSchemaVersion + 1, "A file of a newer schema is refused");
        Assert(newer.Message.Contains("newer version"), "… and says it was saved by a newer version");
        Refuse(file => file["units"]!["length"] = "mm", "A file in other units is refused, not read as metres");

        Refuse(file => Item(file, "points", s.P3).Remove("x"), "A point without a coordinate is refused — never read as zero");
        Refuse(file => Item(file, "points", s.P3)["name"] = null, "A point without a name is refused — never renamed on the quiet");
        Refuse(file => Item(file, "points", s.P3)["name"] = "p1", "Two objects with one name are refused");
        Refuse(file => Item(file, "points", s.P3)["colour"] = "red", "A member this version does not know is refused — never dropped on the quiet");
        Refuse(file => file.Remove("loadCases"), "A file without one of its lists is refused");
        Refuse(file => Item(file, "frames", s.F2)["startPointId"] = ObjectId.New().ToString(), "A frame on a point the file does not hold is refused");
        Refuse(file => Item(file, "materials", s.Steel)["type"] = "Timber", "A value outside its list is refused");
        Refuse(file => Item(file, "materials", s.Steel)["type"] = 1, "… also when it is written as a number");
        Refuse(file => Item(file, "frames", s.F2)["kind"] = "Cable", "A frame cannot be marked as a cable: a cable is listed under cables");
        Refuse(file => Item(file, "cables", s.C1)["endPointId"] = ObjectId.New().ToString(), "A cable on a point the file does not hold is refused");
        Refuse(file => Item(file, "cables", s.C1)["id"] = s.F1.ToString(), "A cable and a frame cannot share an id");
        Refuse(file => file["cableAssignments"]![0]!["cableId"] = s.F1.ToString(), "A cable assignment on a frame is refused");
        Refuse(file => file.Remove("cables"), "A file without its list of cables is refused");
        Refuse(file => file.Remove("nameSequence"), "A file without its name sequences is refused");
        Refuse(file => file["nameSequence"]!["Beam"] = 3, "A name sequence of an unknown kind of object is refused");
        Refuse(file => file["nameSequence"]!["Frame"] = -1, "A negative name sequence is refused");
        Refuse(file => Item(file, "frames", s.F2)["id"] = s.F1.ToString(), "Two objects with one id are refused");
        Refuse(file => Item(file, "loads", s.Target).Remove("value"), "A load without the value its type needs is refused");
        Refuse(file => Item(file, "loads", s.Target)["type"] = "wind", "A load of an unknown type is refused");
        Refuse(file => Item(file, "loadCases", s.Sls)["initialCaseId"] = s.Sls.ToString(), "A file whose load cases run in a circle is refused");
        Refuse(file => Item(file, "materials", s.Steel)["elasticModulus"] = 0, "A file whose model is not valid is refused");
        Refuse(file => Item(file, "frameSections", s.Beam)["materialId"] = "steel", "An id that is not an id is refused");
    }

    private static void Migrations(string folder)
    {
        var s = new Sample();
        s.Session.Execute(DocumentCommands.SetSettings(s.Document.Settings with { ActiveDofs = DofMask.XZ }));
        var old = (JsonObject)JsonNode.Parse(NativeDocumentFormat.Serialize(s.Document))!;
        var points = old["points"];
        old.Remove("points");
        old["joints"] = points;
        old["schemaVersion"] = 0;
        old["settings"]!.AsObject().Remove("activeDofs");
        var path = Path.Combine(folder, "schema0" + NativeDocumentFormat.Extension);
        File.WriteAllText(path, old.ToJsonString());
        var bytes = File.ReadAllBytes(path);
        var stamp = File.GetLastWriteTimeUtc(path);

        var upgraded = new NativeDocumentStore(new IDocumentMigration[] { new JointsToPoints() }.Concat(NativeDocumentFormat.Migrations).ToArray()).Load(path);
        Assert(SameContent(upgraded, s.Document), "A file of an older schema is upgraded step by step and opens complete");
        Assert(File.ReadAllBytes(path).SequenceEqual(bytes) && File.GetLastWriteTimeUtc(path) == stamp, "Opening an older file does not write it: the upgrade works on a copy");
        var none = Throws<DocumentFileException>(() => new NativeDocumentStore().Load(path), "Without the upgrade for its schema an older file is refused");
        Assert(none.Message.Contains("schema 0"), "… naming the schema it cannot upgrade");
        Assert(NativeDocumentFormat.Migrations.Count == NativeDocumentFormat.CurrentSchemaVersion - 1, "There is one upgrade for every schema before the current one");
    }

    private static void AtomicSave(string folder)
    {
        var path = Path.Combine(folder, "atomic" + NativeDocumentFormat.Extension);
        File.WriteAllText(path, "the model as it was last saved");
        Throws<IOException>(() => AtomicFile.Write(path, temporary =>
        {
            File.WriteAllText(temporary, "half of the new mod");
            throw new IOException("disk full");
        }), "A save that fails while writing reports the failure");
        Assert(File.ReadAllText(path) == "the model as it was last saved" && Directory.GetFiles(folder, "*.tmp").Length == 0,
            "… and leaves the previous file whole, with no temporary file beside it");

        var s = new Sample();
        var lifecycle = new DocumentLifecycle(s.Session, new FailingStore());
        Throws<IOException>(() => lifecycle.SaveAs(path), "A store that cannot save reports it");
        Assert(s.Session.IsDirty && s.Session.FilePath is null, "A failed save leaves the model unsaved and without that file");

        AtomicFile.WriteAllText(path, "the new model");
        Assert(File.ReadAllText(path) == "the new model", "A save that succeeds replaces the file");
    }

    private sealed class FailingStore : IDocumentStore
    {
        public ModelDocument Load(string path) => throw new IOException("unreadable");
        public void Save(string path, ModelDocument document) => throw new IOException("disk full");
    }
}
