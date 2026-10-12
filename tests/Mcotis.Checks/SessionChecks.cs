using Mcotis.Application;
using Mcotis.Core;
using static Check;

/// <summary>Every change is a command: undoable, counted, described by what it changed.</summary>
internal static class SessionChecks
{
    public static void Run()
    {
        var s = new Sample();
        var session = s.Session;
        static bool Only(DocumentChangeSet changes, ObjectId id, ObjectKind kind, ChangeAspects aspects) =>
            changes.Changes.Count == 1 && changes.Changes[0] == new ObjectChange(id, kind, aspects);

        var point = ObjectId.New();
        var events = new List<DocumentChangedEventArgs>();
        session.Changed += (_, change) => events.Add(change);

        var revision = session.Revision;
        var added = session.Execute(DocumentCommands.AddPoint(point, new(3, 0, 5)));
        Assert(Only(added, point, ObjectKind.Point, ChangeAspects.Added) && added.Affects(ObjectKind.Point) && !added.Affects(ObjectKind.Frame),
            "Adding a point reports that point as added and nothing else");
        Assert(session.Revision == revision + 1 && session.UndoName == "Add Point" && events.Count == 1 && events[0].Reason == DocumentChangeReason.Command &&
               ReferenceEquals(events[0].After, session.Document),
            "A command raises the revision by one, is named for Undo and is announced once");
        Assert(Only(session.Execute(DocumentCommands.Rename(point, "Ridge")), point, ObjectKind.Point, ChangeAspects.Name), "Rename reports a name change");
        Assert(Only(session.Execute(DocumentCommands.MovePoints([point], new Xyz(0, 0, 1))), point, ObjectKind.Point, ChangeAspects.Geometry) &&
               session.Document.Points[point].Position == new Xyz(3, 0, 6), "Moving a point reports a geometry change");
        Assert(Only(session.Execute(DocumentCommands.AssignRestraint([point], Restraint.Fixed)), point, ObjectKind.Point, ChangeAspects.Assignment),
            "Assigning a restraint reports an assignment change");

        var load = ObjectId.New();
        var loaded = session.Execute(DocumentCommands.PutLoad(new PointLoad(load, s.Live, point, new(0, 0, -10))));
        Assert(loaded.Changes.Count == 2 && loaded.Ids(ObjectKind.Load, ChangeAspects.Added).Single() == load && loaded.Ids(ObjectKind.Point, ChangeAspects.Loads).Single() == point,
            "A new load reports the load and the object it loads");
        var edited = session.Execute(DocumentCommands.PutMaterial(s.Document.Materials[s.Steel] with { ElasticModulus = 2.0e8 }));
        Assert(Only(edited, s.Steel, ObjectKind.Material, ChangeAspects.Definition), "Editing a material reports its definition");

        revision = session.Revision;
        var announced = events.Count;
        var nothing = session.Execute(DocumentCommands.Rename(point, " Ridge "));
        Assert(nothing.IsEmpty && session.Revision == revision && events.Count == announced && session.UndoName == "Define Material",
            "A command that changes nothing leaves no revision, no event and no undo step");

        // Undo / Redo
        var withPoint = session.Document;
        session.Execute(DocumentCommands.Remove([point]));
        var deleted = session.Document;
        Assert(!deleted.Points.ContainsKey(point) && !deleted.Loads.ContainsKey(load) && !deleted.Restraints.ContainsKey(point), "Delete removes the point, its restraint and its load");
        revision = session.Revision;
        Assert(session.Undo() && ReferenceEquals(session.Document, withPoint) && session.Revision == revision + 1 && session.RedoName == "Delete",
            "Undo returns to the very document before the command and still counts as a revision");
        Assert(session.Document.Points[point] is { Name: "Ridge" } restored && restored.Id == point && session.Document.RestraintOf(point) == Restraint.Fixed &&
               session.Document.Loads.ContainsKey(load),
            "Undo of a delete restores the id, the name, the restraint and the load");
        var undone = events[^1];
        Assert(undone.Reason == DocumentChangeReason.Undo && undone.Changes.Ids(ObjectKind.Point, ChangeAspects.Added).Single() == point,
            "Undo announces the delete in reverse: the point is added");
        Assert(session.Redo() && ReferenceEquals(session.Document, deleted) && !session.CanRedo, "Redo returns to the document after the command");
        session.Undo();
        session.Execute(DocumentCommands.Rename(point, "Apex"));
        Assert(!session.CanRedo && session.UndoName == "Rename", "A new command ends what could be redone");

        var steps = 0;
        while (session.Undo()) steps++;
        Assert(steps > 30 && session.Document.Points.IsEmpty && session.Document.Materials.IsEmpty && !session.CanUndo, "Every step back to the empty model can be undone");
        while (session.Redo()) { }
        Assert(session.Document.Points[point].Name == "Apex" && DocumentValidator.Validate(session.Document).Count == 0, "… and redone to where it was");

        // Dirty state
        var fresh = new DocumentSession();
        Assert(!fresh.IsDirty && fresh.FilePath is null && !fresh.CanUndo, "A new model is clean");
        var first = ObjectId.New();
        fresh.Execute(DocumentCommands.AddPoint(first, new(0, 0, 0)));
        Assert(fresh.IsDirty, "A change makes it dirty");
        var savedEvents = 0;
        fresh.SavedStateChanged += (_, _) => savedEvents++;
        fresh.MarkSaved(fresh.Document, @"C:\models\a.mcotis");
        Assert(!fresh.IsDirty && fresh.FilePath == @"C:\models\a.mcotis" && savedEvents == 1 && fresh.CanUndo, "Saving makes it clean and keeps the history");
        fresh.Execute(DocumentCommands.Rename(first, "Origin"));
        Assert(fresh.IsDirty, "A rename makes the document dirty");
        fresh.Undo();
        Assert(!fresh.IsDirty, "Undoing back to the saved document is clean again");
        fresh.Undo();
        Assert(fresh.IsDirty, "Undoing past the saved document is dirty");

        var limited = new DocumentSession(historyLimit: 2);
        for (var i = 0; i < 4; i++) limited.Execute(DocumentCommands.AddPoint(ObjectId.New(), new(i, 0, 0)));
        Assert(limited.Undo() && limited.Undo() && !limited.Undo() && limited.Document.Points.Count == 2, "The history keeps a limited number of steps");

        var replaced = new DocumentSession();
        replaced.Execute(DocumentCommands.AddPoint(ObjectId.New(), new(0, 0, 0)));
        DocumentChangedEventArgs? reset = null;
        replaced.Changed += (_, change) => reset = change;
        replaced.Replace(s.Document, @"C:\models\b.mcotis");
        Assert(ReferenceEquals(replaced.Document, s.Document) && !replaced.IsDirty && !replaced.CanUndo && reset is { Reason: DocumentChangeReason.Replaced } &&
               reset.Changes.IsReset && reset.Changes.Affects(ObjectKind.Material),
            "Opening a document replaces the model whole: no history, clean, everything to be read again");
    }
}

/// <summary>Results belong to the analysis input they were computed from, not to a name or a revision number.</summary>
internal static class ResultStateChecks
{
    public static void Run()
    {
        ObjectId a = ObjectId.New(), b = ObjectId.New(), frame = ObjectId.New();
        ModelDocument Build(Action<DocumentEditor> edit)
        {
            var editor = ModelDocument.Create().Edit();
            edit(editor);
            return editor.Commit().Document;
        }
        var one = Build(e => { e.AddPoint(new(0, 0, 0), "A", a); e.AddPoint(new(1, 0, 0), "B", b); e.AddFrame(a, b, id: frame); });
        var two = Build(e => { e.AddPoint(new(1, 0, 0), "Right", b); e.AddPoint(new(0, 0, 0), "Left", a); e.AddFrame(a, b, name: "Tie", id: frame); });
        var three = Build(e => { e.AddPoint(new(0, 0, 0), "A", a); e.AddPoint(new(1.001, 0, 0), "B", b); e.AddFrame(a, b, id: frame); });
        Assert(one.DocumentId != two.DocumentId && one.AnalysisInputHash == two.AnalysisInputHash,
            "The analysis input hash ignores names and the order the model was built in");
        Assert(one.AnalysisInputHash != three.AnalysisInputHash && one.AnalysisInputHash.Length == 64, "… and tells a millimetre apart");

        var s = new Sample();
        var session = s.Session;
        var tracker = new AnalysisResultTracker(session);
        var states = new List<ResultState>();
        tracker.StateChanged += (_, _) => states.Add(tracker.State);
        Assert(tracker.State == ResultState.NotRun && tracker.ResultSource is null, "Before a run there are no results");

        var run = tracker.Begin();
        var input = session.Document;
        Assert(tracker.State == ResultState.Running && ReferenceEquals(run.Input, input) && run.Revision == session.Revision && run.InputHash == input.AnalysisInputHash,
            "A run takes the document, its revision and its input hash as they are when it starts");
        session.Execute(DocumentCommands.MovePoints([s.P3], new Xyz(0.5, 0, 0)));
        Assert(ReferenceEquals(run.Input, input) && run.Input.Points[s.P3].Position == new Xyz(6, 0, 5) && session.Document.Points[s.P3].Position == new Xyz(6.5, 0, 5),
            "An edit made while the analysis runs does not reach the input of that run");
        tracker.Complete(run);
        Assert(tracker.State == ResultState.Stale, "Results of a run whose input has since been edited are stale as soon as they arrive");
        session.Undo();
        Assert(tracker.State == ResultState.Current && tracker.ResultSource == run, "Undoing that edit makes them current: the input is what was analysed");

        session.Execute(DocumentCommands.Rename(new Dictionary<ObjectId, string> { [s.P3] = "Eave-R", [s.Steel] = "S355J2", [s.Sls] = "Service" }));
        Assert(tracker.State == ResultState.Current && session.IsDirty, "A rename makes the document dirty and leaves the results current");

        (string What, IDocumentCommand Command)[] invalidating =
        [
            ("geometry", DocumentCommands.MovePoints([s.P2], new Xyz(0, 0, 0.25))),
            ("a load", DocumentCommands.PutLoad(new PointLoad(s.PointLoad, s.Live, s.P2, new(0, 0, -120)))),
            ("a restraint", DocumentCommands.AssignRestraint([s.P4], Restraint.Fixed)),
            ("a section's stiffness", DocumentCommands.PutFrameSection(s.Document.FrameSections[s.Beam] with { Properties = new(0.0219, 7e-4, 2.24e-4) })),
            ("a material", DocumentCommands.PutMaterial(s.Document.Materials[s.Steel] with { ElasticModulus = 2.0e8 })),
            ("a cable section's area", DocumentCommands.PutCableSection(s.Document.CableSections[s.CableSection] with { Area = 0.003 })),
            ("a cable's modifiers", DocumentCommands.AssignCableModifiers([s.C1], CableModifiers.Identity)),
            ("a cable's target force", DocumentCommands.PutLoad(new CableTargetForce(s.Target, s.Prestress, s.C1, 650))),
            ("a section assignment", DocumentCommands.AssignFrameSection([s.F3], null)),
            ("a release", DocumentCommands.AssignFrameReleases([s.F1], new(default, new(false, false, false, false, false, true)))),
            ("a load case", DocumentCommands.PutLoadCase(s.Document.LoadCases[s.Sls] with { Components = [new(s.Dead, 1), new(s.Live, 0.7)] })),
            ("the analysis settings", DocumentCommands.SetSettings(s.Document.Settings with { FrameMeshSegments = 20 })),
        ];
        foreach (var (what, command) in invalidating)
        {
            session.Execute(command);
            Assert(tracker.State == ResultState.Stale, $"Changing {what} makes the results stale");
            session.Undo();
            Assert(tracker.State == ResultState.Current, $"… and undoing it makes them current");
        }
        session.Redo();
        Assert(tracker.State == ResultState.Stale, "Redo re-applies the change and the results are stale again");

        var second = tracker.Begin();
        tracker.Cancel(second);
        Assert(tracker.State == ResultState.Stale && tracker.ResultSource == run, "A cancelled run leaves the earlier results as they were");
        second = tracker.Begin();
        tracker.Fail(second);
        Assert(tracker.State == ResultState.Failed && tracker.ResultSource is null, "A failed run has no results");
        session.Execute(DocumentCommands.MovePoints([s.P2], new Xyz(0, 0, 0.1)));
        Assert(tracker.State == ResultState.NotRun, "After an edit the failure no longer describes the model: not run");
        var third = tracker.Begin();
        Throws<InvalidOperationException>(() => tracker.Begin(), "Two analyses cannot run at once");
        tracker.Complete(third);
        Assert(tracker.State == ResultState.Current && tracker.ResultSource!.Revision == session.Revision, "A completed run on the model as it is gives current results, traceable to its revision");
        Assert(states.SequenceEqual(new[]
            {
                ResultState.Running, ResultState.Stale, ResultState.Current,
            }.Concat(Enumerable.Repeat(new[] { ResultState.Stale, ResultState.Current }, invalidating.Length).SelectMany(pair => pair)).Concat(new[]
            {
                ResultState.Stale, ResultState.Running, ResultState.Stale, ResultState.Running, ResultState.Failed, ResultState.NotRun, ResultState.Running, ResultState.Current,
            })),
            "Every change of the result state is announced once: " + string.Join(" ", states));

        session.Replace(ModelDocument.Create(), null);
        Assert(tracker.State == ResultState.NotRun, "Another model has no results");
    }
}
