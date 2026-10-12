using Mcotis.Application;
using Mcotis.Core;
using static Check;

/// <summary>Identity and unique names: one name space for points, frames and shells; case-insensitive; issued in one place.</summary>
internal static class NamingChecks
{
    public static void Run()
    {
        var session = new DocumentSession();
        ObjectId p1 = ObjectId.New(), p2 = ObjectId.New(), p3 = ObjectId.New(), f1 = ObjectId.New(), c1 = ObjectId.New(), c2 = ObjectId.New(), s1 = ObjectId.New();
        session.Execute(DocumentCommands.AddPoint(p1, new(0, 0, 0)));
        session.Execute(DocumentCommands.AddPoint(p2, new(4, 0, 0)));
        session.Execute(DocumentCommands.AddPoint(p3, new(4, 0, 3)));
        session.Execute(DocumentCommands.AddFrame(f1, p1, p2));
        session.Execute(DocumentCommands.AddCable(c1, p1, p3));
        session.Execute(DocumentCommands.AddCable(c2, p2, p3));
        session.Execute(DocumentCommands.AddShell(s1, [p1, p2, p3]));
        var d = session.Document;
        Assert(d.Points[p1].Name == "P1" && d.Points[p2].Name == "P2" && d.Points[p3].Name == "P3" && d.Frames[f1].Name == "F1" &&
               d.Cables[c1].Name == "C1" && d.Cables[c2].Name == "C2" && d.Shells[s1].Name == "S1",
            "New objects are named P1, P2 …, F1, C1, C2 (cables), S1");
        Assert(d.Frames.Count == 1 && d.Cables.Count == 2 && !d.Frames.ContainsKey(c1) && d.TryGetNamed(c1, out var kind, out _) && kind == ObjectKind.Cable,
            "A cable is a cable object of its own, not a frame");

        ObjectId p4 = ObjectId.New(), p5 = ObjectId.New(), p6 = ObjectId.New();
        session.Execute(DocumentCommands.AddPoint(p4, new(9, 0, 0)));
        session.Execute(DocumentCommands.AddPoint(p5, new(9, 0, 1)));
        session.Execute(DocumentCommands.Remove([p4]));
        session.Execute(DocumentCommands.AddPoint(p6, new(9, 0, 2)));
        Assert(session.Document.Points[p5].Name == "P5" && session.Document.Points[p6].Name == "P6" && !session.Document.TryFindObject("P4", out _),
            "An automatic name continues the sequence; it does not take the number a deleted point left");

        var top = ObjectId.New();
        session.Execute(DocumentCommands.AddPoint(top, new(1, 1, 1), "  Café Top  "));
        Assert(session.Document.Points[top].Name == "Café Top", "A name is stored trimmed and in Unicode NFC");
        Assert(session.Document.TryFindObject(" CAFÉ top", out var found) && found == top && session.Document.Names.Contains(NameScope.Object, "café top"),
            "A name is found whatever its case, spacing around it or Unicode composition");

        var collision = Refused(session, DocumentCommands.AddFrame(ObjectId.New(), p1, p2, name: "p1"), DocumentError.DuplicateName,
            "A frame cannot take the name of a point, even in another case");
        Assert(collision.Message.Contains("point 'P1'"), "The refusal names the object that holds the name");
        Refused(session, DocumentCommands.AddShell(ObjectId.New(), [p1, p2, p3], "f1"), DocumentError.DuplicateName, "A shell cannot take the name of a frame");
        Refused(session, DocumentCommands.AddCable(ObjectId.New(), p1, p2, "F1"), DocumentError.DuplicateName, "A cable cannot take the name of a frame");
        Refused(session, DocumentCommands.AddFrame(ObjectId.New(), p2, p3, "c1"), DocumentError.DuplicateName, "A frame cannot take the name of a cable");
        foreach (var bad in new[] { "", "   ", "A\tB" })
            Refused(session, DocumentCommands.AddPoint(ObjectId.New(), new(2, 2, 2), bad), DocumentError.InvalidName, "An empty, blank or control-character name is refused");
        Refused(session, DocumentCommands.AddPoint(p1, new(7, 7, 7)), DocumentError.DuplicateId, "An id cannot be issued twice");

        session.Execute(DocumentCommands.Rename(p1, "Base"));
        d = session.Document;
        Assert(d.Points[p1].Name == "Base" && !d.TryFindObject("P1", out _) && d.Frames[f1].StartPointId == p1 && d.Shells[s1].PointIds[0] == p1,
            "Rename changes the name, keeps the id and leaves connectivity alone");
        session.Execute(DocumentCommands.Rename(p1, "BASE"));
        Assert(session.Document.Points[p1].Name == "BASE", "An object can change the case of its own name");
        Refused(session, DocumentCommands.Rename(p2, "base"), DocumentError.DuplicateName, "Rename to a name in use is refused");

        session.Execute(DocumentCommands.Rename(new Dictionary<ObjectId, string> { [p2] = "P3", [p3] = "P2" }));
        d = session.Document;
        Assert(d.Points[p2].Name == "P3" && d.Points[p3].Name == "P2" && d.TryFindObject("P2", out var holder) && holder == p3,
            "A batch rename may exchange the names of two objects");
        Refused(session, DocumentCommands.Rename(new Dictionary<ObjectId, string> { [p2] = "Node", [p3] = "node" }), DocumentError.DuplicateName,
            "A batch rename whose outcome collides is refused as a whole");
        Refused(session, DocumentCommands.Rename(new Dictionary<ObjectId, string> { [p2] = "Apex", [p3] = "F1" }), DocumentError.DuplicateName,
            "A batch rename that collides with an object outside the batch renames nothing");
        Assert(session.Document.Points[p2].Name == "P3", "… not even the entries that were free");

        ObjectId steel = ObjectId.New(), section = ObjectId.New();
        session.Execute(DocumentCommands.PutMaterial(new(steel, "S355", MaterialType.Steel, 2.1e8, 0.3, 76.97, 7.849)));
        session.Execute(DocumentCommands.PutFrameSection(new(section, "S355", steel, FrameSectionShape.General, new(0.01, 1e-4, 1e-4))));
        Assert(session.Document.FrameSections[section].Name == "S355", "Materials and sections are named in scopes of their own");
        var strand = ObjectId.New();
        session.Execute(DocumentCommands.PutCableSection(new(strand, "S355", steel, 0.003)));
        session.Execute(DocumentCommands.AssignCableSection([c1], strand));
        Assert(session.Document.CableSections[strand].Name == "S355" && session.Document.AssignmentOfCable(c1).SectionId == strand,
            "Cable sections are definitions of their own, in a scope apart from frame sections");
        Refused(session, DocumentCommands.AssignCableSection([c1], section), DocumentError.NotFound, "A frame section cannot be assigned to a cable");
        Refused(session, DocumentCommands.AssignFrameSection([f1], strand), DocumentError.NotFound, "A cable section cannot be assigned to a frame");
        Refused(session, DocumentCommands.AssignFrameSection([c1], section), DocumentError.NotFound, "A cable cannot be given a frame's assignments");
        Refused(session, DocumentCommands.PutMaterial(new(ObjectId.New(), "s355", MaterialType.Steel, 2.1e8, 0.3, 76.97, 7.849)), DocumentError.DuplicateName,
            "Two materials cannot share a name");
        ObjectId pattern = ObjectId.New(), uls = ObjectId.New();
        session.Execute(DocumentCommands.PutLoadPattern(new(pattern, "DEAD", LoadPatternType.Dead, 1)));
        session.Execute(DocumentCommands.PutLoadCase(new(uls, "ULS", LoadCaseType.LinearStatic, [new(pattern, 1.35)])));
        Refused(session, DocumentCommands.PutLoadCombination(new(ObjectId.New(), "uls", CombinationType.LinearAdd, [new(uls, 1)])), DocumentError.DuplicateName,
            "A load combination cannot take the name of a load case: results are chosen by one name");

        session.Execute(DocumentCommands.AssignRestraint([p1], Restraint.Pinned));
        session.Execute(DocumentCommands.AssignFrameSection([f1], section));
        var original = session.Document;
        IReadOnlyDictionary<ObjectId, ObjectId> copies = new Dictionary<ObjectId, ObjectId>();
        session.Execute(DocumentCommands.Edit("Duplicate", editor => copies = editor.Duplicate([f1, c1], new(0, 5, 0))));
        d = session.Document;
        var cableCopy = d.Cables[copies[c1]];
        Assert(cableCopy.Id != c1 && cableCopy.Name == "C3" && cableCopy.StartPointId == copies[p1] && cableCopy.EndPointId == copies[p3] &&
               d.AssignmentOfCable(cableCopy.Id).SectionId == strand,
            "A copied cable is a cable with a new id and name, on the copies of its points, with its section");
        var copy = d.Frames[copies[f1]];
        Assert(copy.Id != f1 && copy.Name == "F2" && d.Points[copies[p1]].Name != "BASE" && d.Points[copies[p1]].Position == new Xyz(0, 5, 0) &&
               copy.StartPointId == copies[p1] && copy.EndPointId == copies[p2],
            "A copy has a new id, a new name and its own points at the offset");
        Assert(d.AssignmentOfFrame(copy.Id).SectionId == section && d.RestraintOf(copies[p1]) == Restraint.Pinned &&
               d.Frames[f1] == original.Frames[f1] && d.Points[p1] == original.Points[p1],
            "A copy carries the assignments of its original, which stays as it was");
        Assert(DocumentValidator.Validate(d).Count == 0, "After all of it the document and its name index are consistent");

        DeleteKeepsTheSequence();
    }

    /// <summary>Deleting a frame or a cable renames nothing and never moves the name sequence back.</summary>
    private static void DeleteKeepsTheSequence()
    {
        var session = new DocumentSession();
        var points = Enumerable.Range(0, 5).Select(_ => ObjectId.New()).ToArray();
        for (var i = 0; i < points.Length; i++) session.Execute(DocumentCommands.AddPoint(points[i], new(4 * i, 0, 0)));
        var apex = ObjectId.New();
        session.Execute(DocumentCommands.AddPoint(apex, new(8, 0, 6)));
        var frames = Enumerable.Range(0, 4).Select(_ => ObjectId.New()).ToArray();
        var cables = Enumerable.Range(0, 4).Select(_ => ObjectId.New()).ToArray();
        for (var i = 0; i < 4; i++) session.Execute(DocumentCommands.AddFrame(frames[i], points[i], points[i + 1]));
        for (var i = 0; i < 4; i++) session.Execute(DocumentCommands.AddCable(cables[i], apex, points[i < 2 ? i : i + 1]));
        string[] Names(ObjectId[] ids) => ids.Where(id => session.Document.TryGetNamed(id, out _, out _))
            .Select(id => session.Document.TryGetNamed(id, out _, out var name) ? name : "").ToArray();
        string AddedFrame(ObjectId start, ObjectId end)
        {
            var id = ObjectId.New();
            session.Execute(DocumentCommands.AddFrame(id, start, end));
            return session.Document.Frames[id].Name;
        }
        string AddedCable(ObjectId end)
        {
            var id = ObjectId.New();
            session.Execute(DocumentCommands.AddCable(id, apex, end));
            return session.Document.Cables[id].Name;
        }
        Assert(Names(frames).SequenceEqual(["F1", "F2", "F3", "F4"]) && Names(cables).SequenceEqual(["C1", "C2", "C3", "C4"]), "Four frames and four cables are F1 … F4 and C1 … C4");

        session.Execute(DocumentCommands.Remove([frames[1], cables[1]]));
        Assert(Names(frames).SequenceEqual(["F1", "F3", "F4"]) && Names(cables).SequenceEqual(["C1", "C3", "C4"]),
            "Deleting a frame and a cable leaves every other frame and cable under the name it had");
        Assert(AddedFrame(points[1], points[2]) == "F5" && AddedCable(points[1]) == "C5" && !session.Document.TryFindObject("F2", out _) && !session.Document.TryFindObject("C2", out _),
            "A new frame or cable — even in the place of the deleted one — continues the sequence instead of taking the number left free");

        session.Execute(DocumentCommands.Remove([session.Document.Names.TryGet(NameScope.Object, "F5", out var f5) ? f5 : default]));
        session.Execute(DocumentCommands.Remove([session.Document.Names.TryGet(NameScope.Object, "C5", out var c5) ? c5 : default]));
        Assert(AddedFrame(points[1], points[2]) == "F6" && AddedCable(points[1]) == "C6", "Deleting the newest frame or cable does not hand its name to the next one either");
        Assert(session.Document.Names.LastNumber(ObjectKind.Frame) == 6 && session.Document.Names.LastNumber(ObjectKind.Cable) == 6 &&
               session.Document.Names.LastNumber(ObjectKind.Point) == 6 && session.Document.Names.LastNumber(ObjectKind.Shell) == 0,
            "Each kind of object keeps count of how far its names have got");

        session.Undo();
        Assert(session.Document.Names.LastNumber(ObjectKind.Cable) == 5 && AddedCable(points[1]) == "C6", "A creation that was undone never happened: its name is the next one again");

        session.Execute(DocumentCommands.Rename(frames[0], "f20"));
        Assert(AddedFrame(points[0], points[2]) == "F21", "A name the user types into the sequence moves the sequence on past it");
        session.Execute(DocumentCommands.Remove([frames[0]]));
        Assert(AddedFrame(points[0], points[2]) == "F22" && Names(frames).SequenceEqual(["F3", "F4"]), "… and it stays past it once that frame is deleted");

        session.Execute(DocumentCommands.Rename(frames[2], "F2"));
        Assert(session.Document.Frames[frames[2]].Name == "F2" && AddedFrame(points[2], points[4]) == "F23",
            "The user may still give a freed name by hand; automatic names do not go back for it");
        Assert(DocumentValidator.Validate(session.Document).Count == 0, "The document stays consistent with its name sequences");
    }
}

/// <summary>References are ids: rename, delete and undo never leave one dangling.</summary>
internal static class ReferenceChecks
{
    public static void Run()
    {
        var s = new Sample();
        var session = s.Session;
        var hash = s.Document.AnalysisInputHash;
        Assert(DocumentValidator.Validate(s.Document).Count == 0, "The sample model is a valid document");

        session.Execute(DocumentCommands.Rename(new Dictionary<ObjectId, string>
        {
            [s.P2] = "Eave-L", [s.F2] = "Rafter", [s.Beam] = "HB 400x400", [s.Steel] = "S355J2", [s.Live] = "IMPOSED", [s.Pretension] = "PT stage",
        }));
        var d = s.Document;
        Assert(d.Points[s.P2].Name == "Eave-L" && d.Frames[s.F1].EndPointId == s.P2 && d.Frames[s.F2].StartPointId == s.P2 && d.Shells[s.S1].PointIds.Contains(s.P2),
            "Renaming a point leaves every frame and shell on it");
        Assert(d.AssignmentOfFrame(s.F2).SectionId == s.Beam && d.FrameSections[s.Beam].MaterialId == s.Steel && d.FrameSections[s.Beam].Name == "HB 400x400" &&
               d.Loads[s.PointLoad].TargetId == s.P2 && d.Loads[s.PointLoad].PatternId == s.Live && d.LoadCases[s.Sls].InitialCaseId == s.Pretension &&
               d.LoadCombinations[s.Envelope].Terms.Any(term => term.SourceId == s.Pretension),
            "Renaming sections, materials, patterns and cases leaves assignments, loads, staging and combinations attached");
        Assert(d.AnalysisInputHash == hash, "Renaming does not change the analysis input");
        Assert(DocumentValidator.Validate(d).Count == 0, "The renamed document is valid");

        var inUse = Refused(session, DocumentCommands.Remove([s.P2]), DocumentError.InUse, "A point under a frame cannot be deleted");
        Assert(inUse.Message.Contains("Point 'Eave-L'"), "The refusal names the point: " + inUse.Message);

        var beforeDelete = s.Document;
        session.Execute(DocumentCommands.Remove([s.F2]));
        d = s.Document;
        Assert(!d.Frames.ContainsKey(s.F2) && !d.FrameAssignments.ContainsKey(s.F2) && !d.Loads.ContainsKey(s.LineLoad) && d.Points.ContainsKey(s.P2) &&
               !d.TryFindObject("Rafter", out _),
            "Deleting a frame takes its assignments and loads with it and leaves its points");
        session.Undo();
        d = s.Document;
        Assert(ReferenceEquals(d, beforeDelete) && d.Frames[s.F2].Name == "Rafter" && d.AssignmentOfFrame(s.F2).Releases.Start.Moment3 && d.Loads.ContainsKey(s.LineLoad),
            "Undo of a delete brings the frame back with its id, name, assignments and loads");

        session.Execute(DocumentCommands.Remove([s.S1, s.P5, s.P6]));
        d = s.Document;
        Assert(!d.Shells.ContainsKey(s.S1) && !d.Points.ContainsKey(s.P5) && !d.Points.ContainsKey(s.P6) && !d.Loads.ContainsKey(s.AreaLoad) && !d.ShellAssignments.ContainsKey(s.S1),
            "A shell and the points only it uses can be deleted together");

        Refused(session, DocumentCommands.Remove([s.Steel]), DocumentError.InUse, "A material used by a section cannot be deleted");
        Refused(session, DocumentCommands.Remove([s.Beam]), DocumentError.InUse, "A section assigned to a frame cannot be deleted");
        Refused(session, DocumentCommands.Remove([s.CableSection]), DocumentError.InUse, "A section assigned to a cable cannot be deleted");
        Refused(session, DocumentCommands.Remove([s.Strand]), DocumentError.InUse, "A material used by a cable section cannot be deleted");
        Refused(session, DocumentCommands.Remove([s.Dead]), DocumentError.InUse, "A load pattern with loads or in a load case cannot be deleted");
        Refused(session, DocumentCommands.Remove([s.Pretension]), DocumentError.InUse, "A load case another case continues from cannot be deleted");
        Refused(session, DocumentCommands.Remove([s.Sls]), DocumentError.InUse, "A load case in a combination cannot be deleted");
        Refused(session, DocumentCommands.Remove([ObjectId.New()]), DocumentError.NotFound, "Deleting what is not there is refused");

        session.Execute(DocumentCommands.Remove([s.Envelope]));
        session.Execute(DocumentCommands.Remove([s.Sls]));
        session.Execute(DocumentCommands.Remove([s.CableSection, s.C1]));
        d = s.Document;
        Assert(!d.LoadCases.ContainsKey(s.Sls) && !d.CableSections.ContainsKey(s.CableSection) && !d.Cables.ContainsKey(s.C1) &&
               !d.CableAssignments.ContainsKey(s.C1) && !d.Loads.ContainsKey(s.Target),
            "Once nothing refers to them they go; a cable takes its assignment and target force, and its section goes with the last cable that used it");
        Assert(DocumentValidator.Validate(d).Count == 0, "No dangling reference is left");
    }
}

/// <summary>What a document refuses to be.</summary>
internal static class ValidationChecks
{
    private static DocumentException Invalid(ModelDocument document, string code, Action<DocumentEditor> edit, string description)
    {
        var refusal = Throws<DocumentException>(() =>
        {
            var editor = document.Edit();
            edit(editor);
            editor.Commit();
        }, description);
        Assert(refusal.Error == DocumentError.Invalid && refusal.Issues.Any(issue => issue.Code == code),
            $"{description} — as '{code}' (was {refusal.Error}: {string.Join(", ", refusal.Issues.Select(issue => issue.Code))})");
        return refusal;
    }

    public static void Run()
    {
        var empty = ModelDocument.Create();
        Invalid(empty, "point.position", e => e.AddPoint(new(double.NaN, 0, 0)), "A point needs finite coordinates");
        Invalid(empty, "point.position", e => e.AddPoint(new(0, 0, double.PositiveInfinity)), "… none of them infinite");
        Invalid(empty, "frame.length", e => { var a = e.AddPoint(new(1, 2, 3)); e.AddFrame(a.Id, a.Id); }, "A frame needs two different points");
        Invalid(empty, "frame.length", e => e.AddFrame(e.AddPoint(new(1, 2, 3)).Id, e.AddPoint(new(1, 2, 3)).Id), "A frame between two points at one position has no length");
        Invalid(empty, "cable.length", e => e.AddCable(e.AddPoint(new(1, 2, 3)).Id, e.AddPoint(new(1, 2, 3)).Id), "Nor has a cable");

        ObjectId[] Points(DocumentEditor e, params (double X, double Y, double Z)[] at) => at.Select(p => e.AddPoint(new(p.X, p.Y, p.Z)).Id).ToArray();
        Invalid(empty, "shell.points", e => e.AddShell(Points(e, (0, 0, 0), (1, 0, 0))), "A shell needs at least three points");
        Invalid(empty, "shell.repeated", e => { var p = Points(e, (0, 0, 0), (1, 0, 0), (1, 1, 0)); e.AddShell([p[0], p[1], p[2], p[1]]); }, "A shell cannot list a point twice");
        Invalid(empty, "shell.degenerate", e => e.AddShell(Points(e, (0, 0, 0), (1, 0, 0), (2, 0, 0))), "Three points on a line are not a shell");
        Invalid(empty, "shell.boundary", e => e.AddShell(Points(e, (0, 0, 0), (4, 0, 0), (1, 3, 0), (4, 1, 0))), "A shell boundary cannot cross itself");

        var editor = empty.Edit();
        var warped = editor.AddShell(Points(editor, (0, 0, 0), (4, 0, 0), (4, 4, 0.05), (0, 4, 0)));
        editor.AddShell(Points(editor, (0, 0, 2), (4, 0, 4), (4, 4, 4), (0, 4, 2)));
        var issues = DocumentValidator.Validate(editor.Commit().Document);
        Assert(issues.Count == 1 && issues[0] is { Severity: ValidationSeverity.Warning, Code: "shell.planarity" } && issues[0].ObjectId == warped.Id,
            "A warped shell is a warning, not a refusal; a flat shell in an inclined plane is neither");

        var s = new Sample();
        Refused(s.Session, DocumentCommands.MovePoints(new Dictionary<ObjectId, Xyz> { [s.P2] = new(0, 0, 0) }), DocumentError.Invalid,
            "Moving a point onto the other end of its frame is refused");

        ObjectId a = ObjectId.New(), b = ObjectId.New(), c = ObjectId.New();
        Invalid(s.Document, "case.cycle", e =>
        {
            e.PutLoadCase(new(a, "A", LoadCaseType.NonlinearStatic, [new(s.Dead, 1)]) { InitialCaseId = b });
            e.PutLoadCase(new(b, "B", LoadCaseType.NonlinearStatic, [new(s.Dead, 1)]) { InitialCaseId = a });
        }, "Load cases cannot continue from each other in a circle");
        Invalid(s.Document, "case.cycle", e => e.PutLoadCase(new(a, "A", LoadCaseType.NonlinearStatic, [new(s.Dead, 1)]) { InitialCaseId = a }), "A load case cannot continue from itself");
        Invalid(s.Document, "case.initial", e => e.PutLoadCase(new(a, "A", LoadCaseType.NonlinearStatic, [new(s.Dead, 1)]) { InitialCaseId = b }), "A load case cannot continue from a case that does not exist");
        Invalid(s.Document, "case.pattern", e => e.PutLoadCase(new(a, "A", LoadCaseType.LinearStatic, [new(c, 1)])), "A load case cannot use a pattern that does not exist");
        Invalid(s.Document, "combination.cycle", e => e.PutLoadCombination(new(a, "X", CombinationType.LinearAdd, [new(a, 1)])), "A combination cannot contain itself");
        Invalid(s.Document, "combination.cycle", e =>
        {
            e.PutLoadCombination(new(a, "X", CombinationType.LinearAdd, [new(s.Sls, 1), new(b, 1)]));
            e.PutLoadCombination(new(b, "Y", CombinationType.LinearAdd, [new(a, 1)]));
        }, "… nor through another combination");
        Invalid(s.Document, "combination.source", e => e.PutLoadCombination(new(a, "X", CombinationType.LinearAdd, [new(c, 1)])), "A combination cannot use a case that does not exist");

        Invalid(s.Document, "section.material", e => e.PutFrameSection(new(a, "Loose", c, FrameSectionShape.General, new(0.01, 1e-4, 1e-4))), "A section needs a material that exists");
        Invalid(s.Document, "section.value", e => e.PutFrameSection(new(a, "Thin", s.Steel, FrameSectionShape.General, new(0, 1e-4, 1e-4))), "A frame section needs an area");
        Invalid(s.Document, "section.value", e => e.PutCableSection(new(a, "Thread", s.Strand, 0)), "A cable section needs an area");
        Invalid(s.Document, "section.material", e => e.PutCableSection(new(a, "Loose", c, 0.003)), "A cable section needs a material that exists");
        Invalid(s.Document, "load.target", e => e.PutLoad(new FrameDistributedLoad(a, s.Dead, s.C1, LoadDirection.GlobalZ, 0, 1, -1, -1)), "A frame load does not belong on a cable");
        Invalid(s.Document, "material.value", e => e.PutMaterial(new(a, "Soft", MaterialType.Other, 0, 0.3, 1, 1)), "A material needs an elastic modulus");
        Invalid(s.Document, "load.pattern", e => e.PutLoad(new PointLoad(a, c, s.P2, new(0, 0, -1))), "A load needs a pattern that exists");
        Invalid(s.Document, "load.target", e => e.PutLoad(new PointLoad(a, s.Live, s.F1, new(0, 0, -1))), "A point load belongs on a point");
        Invalid(s.Document, "load.target", e => e.PutLoad(new CableTargetForce(a, s.Prestress, s.F1, 500)), "A target force belongs on a cable, not on a frame");
        Invalid(s.Document, "load.value", e => e.PutLoad(new FrameDistributedLoad(a, s.Dead, s.F1, LoadDirection.GlobalZ, 0.6, 0.4, -1, -1)), "A distributed load runs from a smaller to a larger relative distance");
        Invalid(s.Document, "load.value", e => e.PutLoad(new CableTargetForce(a, s.Prestress, s.C1, 0)), "A target force is above zero");
        Invalid(s.Document, "settings.value", e => e.SetSettings(s.Document.Settings with { FrameMeshSegments = 0 }), "A frame is meshed into at least one segment");

        var failing = s.Document.Edit();
        Throws<DocumentException>(() => failing.Rename(ObjectId.New(), "x"), "Renaming what is not there is refused");
        Throws<InvalidOperationException>(() => failing.Commit(), "An editor that refused an operation cannot be committed");
        var done = s.Document.Edit();
        done.MovePoint(s.P3, new(6.5, 0, 5));
        done.Commit();
        Throws<InvalidOperationException>(() => done.MovePoint(s.P3, new(7, 0, 5)), "A committed editor cannot be used again");
        Assert(s.Document.Points[s.P3].Position == new Xyz(6, 0, 5), "An edit never changes the document it started from");
    }
}
