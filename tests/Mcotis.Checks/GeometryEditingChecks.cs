using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Mcotis.Application;
using Mcotis.Core;
using Mcotis.Persistence;
using static Check;

/// <summary>
/// Geometry editing on the native document (docs/geometry/GEOMETRY_EDITING_PLAN.md, GRID_LEVEL_CONTROLLER.md, 2026-10-15):
/// grid systems and levels are reference data — saved, undone, never analysis input, never points — and drawing, moving
/// and deleting keep connectivity: a position on a point is that point, a member has a length and is never doubled, a
/// move moves each point once, a delete takes what stands on a deleted point and leaves the end points of a deleted member.
/// </summary>
internal static class GeometryEditingChecks
{
    public static void Run()
    {
        ReferenceGeometryIsReferenceData();
        ReferenceGeometryRules();
        Generators();
        Drawing();
        Moving();
        Deleting();
    }

    /// <summary>The verification scenario of the plan: unequal X / Y / Z spacings, an offset origin, a negative level.</summary>
    internal static ReferenceGeometry Irregular() => new(
        [new GridSystem("GLOBAL", new(0, 0, 0), 0,
            [new("1", 0), new("2", 6), new("3", 14)], [new("A", 0), new("B", 4), new("C", 9)])],
        [new("Base", 0), new("L1", 4.5), new("Roof", 9.3), new("Basement", -3)]);

    private static void ReferenceGeometryIsReferenceData()
    {
        var s = new Sample();
        var (hash, points, revision) = (s.Document.AnalysisInputHash, s.Document.Points, s.Session.Revision);
        var changes = s.Session.Execute(GeometryEditService.SetReferenceGeometry(Irregular()));
        var reference = s.Document.ReferenceGeometry;
        Assert(reference.GridSystems.Single().XLines.Select(line => line.Coordinate).SequenceEqual([0d, 6, 14]) &&
               reference.Levels.Select(level => level.Name).SequenceEqual(["Basement", "Base", "L1", "Roof"]) &&
               reference.Levels[0].Elevation == -3,
            "Grid lines at unequal spacings and levels with a negative elevation are kept, the levels from the lowest up");
        Assert(s.Document.AnalysisInputHash == hash && ReferenceEquals(s.Document.Points, points) && s.Session.Revision == revision + 1 &&
               changes.Affects(ObjectKind.ReferenceGeometry) && !changes.Affects(ObjectKind.Point) && s.Session.UndoName == "Edit Grids and Levels",
            "Defining grids and levels is one undoable step that makes no point and leaves the analysis input — and so every result — as it was");

        var renamed = reference with { GridSystems = [reference.GridSystems[0] with { XLines = [new("X1", 0), new("X2", 6.5), new("X3", 14)] }] };
        s.Session.Execute(GeometryEditService.SetReferenceGeometry(renamed));
        Assert(s.Document.AnalysisInputHash == hash && s.Document.Points[s.P3].Position == new Xyz(6, 0, 5),
            "Renaming grid lines and moving one moves no joint and leaves the results current");
        s.Session.Undo();
        Assert(s.Document.ReferenceGeometry.Equals(reference), "Undo returns the grid as it was");
        s.Session.Undo();
        Assert(s.Document.ReferenceGeometry.IsEmpty, "… and once more, to no grid at all");
        s.Session.Redo();
        Assert(s.Document.ReferenceGeometry.Equals(reference), "Redo restores it");
        Assert(s.Session.Execute(GeometryEditService.SetReferenceGeometry(Irregular())).IsEmpty, "Setting the same grids and levels again is no step");

        var read = NativeDocumentFormat.Deserialize(NativeDocumentFormat.Serialize(s.Document));
        Assert(read.ReferenceGeometry.Equals(reference) && SameContent(read, s.Document),
            "Grid systems and levels are saved and opened with the document (schema 7)");
        var file = (JsonObject)JsonNode.Parse(NativeDocumentFormat.Serialize(s.Document))!;
        Assert((int)file["schemaVersion"]! >= 7 && file["referenceGeometry"]!["gridSystems"]!.AsArray().Count == 1 &&
               file["referenceGeometry"]!["levels"]!.AsArray().Count == 4, "The file states them under referenceGeometry");

        var old = (JsonObject)JsonNode.Parse(NativeDocumentFormat.Serialize(new Sample().Document))!;
        old.Remove("referenceGeometry");
        old["schemaVersion"] = 6;
        Assert(NativeDocumentFormat.Deserialize(old.ToJsonString()).ReferenceGeometry.IsEmpty,
            "A schema-6 file opens with no grid and no level: none is made up from its points");
        var damaged = (JsonObject)JsonNode.Parse(NativeDocumentFormat.Serialize(s.Document))!;
        damaged["referenceGeometry"]!["levels"]![1]!["name"] = "basement";
        Throws<DocumentFileException>(() => NativeDocumentFormat.Deserialize(damaged.ToJsonString()), "A file with two levels of one name is refused");
        damaged = (JsonObject)JsonNode.Parse(NativeDocumentFormat.Serialize(s.Document))!;
        damaged.Remove("referenceGeometry");
        Throws<DocumentFileException>(() => NativeDocumentFormat.Deserialize(damaged.ToJsonString()), "A schema-7 file without its reference geometry is refused");

        // a content replacement (the main window's bridge) carries the reference geometry with the rest
        var before = s.Document;
        var target = before.Edit();
        target.SetReferenceGeometry(ReferenceGeometry.Empty);
        var bare = target.Commit().Document;
        s.Session.Execute(DocumentCommands.Edit("Replace", editor => editor.ReplaceContent(bare)));
        Assert(s.Document.ReferenceGeometry.IsEmpty && s.Session.Undo() && s.Document.ReferenceGeometry.Equals(reference),
            "Replacing the content replaces the reference geometry too, as one undoable step");
    }

    private static void ReferenceGeometryRules()
    {
        var s = new Sample();
        var grid = Irregular().GridSystems[0];
        Refused(s.Session, GeometryEditService.SetReferenceGeometry(new([grid with { YLines = [new("a", 0), new("1", 4)] }], [])),
            DocumentError.Invalid, "A label used by two lines of one system (any case, either axis) is refused");
        Refused(s.Session, GeometryEditService.SetReferenceGeometry(new([grid with { XLines = [new("1", 0), new("2", 0.0000001)] }], [])),
            DocumentError.Invalid, "Two lines of one axis at the same coordinate are refused");
        Refused(s.Session, GeometryEditService.SetReferenceGeometry(new([grid with { XLines = [new("1", double.NaN)] }], [])),
            DocumentError.Invalid, "A line without a finite coordinate is refused");
        Refused(s.Session, GeometryEditService.SetReferenceGeometry(new([grid, grid with { Name = "global" }], [])),
            DocumentError.Invalid, "Two grid systems of one name are refused");
        Refused(s.Session, GeometryEditService.SetReferenceGeometry(new([], [new("L1", 3), new("l1", 6)])),
            DocumentError.Invalid, "Two levels of one name are refused");
        Refused(s.Session, GeometryEditService.SetReferenceGeometry(new([], [new("L1", 3), new("L2", 3)])),
            DocumentError.Invalid, "Two levels at one elevation are refused");
        Refused(s.Session, GeometryEditService.SetReferenceGeometry(new([], [new("  ", 3)])),
            DocumentError.Invalid, "A level without a name is refused");
        s.Session.Execute(GeometryEditService.SetReferenceGeometry(new([grid with { XLines = [new(" 3 ", 14), new("1", 0)] }], [new("Top", 8), new("Ground", 0)])));
        Assert(s.Document.ReferenceGeometry.GridSystems[0].XLines.Select(line => line.Label).SequenceEqual(["1", "3"]) &&
               s.Document.ReferenceGeometry.Levels.Select(level => level.Name).SequenceEqual(["Ground", "Top"]),
            "Labels are stored trimmed, lines by coordinate and levels from the lowest up");
    }

    private static void Generators()
    {
        Assert(GeometryEditService.GridLabel(false, 0) == "A" && GeometryEditService.GridLabel(false, 25) == "Z" &&
               GeometryEditService.GridLabel(false, 26) == "AA" && GeometryEditService.GridLabel(false, 27) == "AB" &&
               GeometryEditService.GridLabel(true, 0) == "1" && GeometryEditService.GridLabel(true, 11, "G") == "G12",
            "Default labels: X lines 1, 2, 3 …; Y lines A … Z, AA, AB …");
        var lines = GeometryEditService.LinesFromSpacings(true, -2, [6, 8]);
        Assert(lines.Select(line => (line.Label, line.Coordinate)).SequenceEqual([("1", -2d), ("2", 4d), ("3", 12d)]),
            "Quick Grid with unequal spacings puts each line at the running sum from the first");
        Assert(GeometryEditService.UniformLines(false, 0, 3, 4.5).Select(line => line.Coordinate).SequenceEqual([0d, 4.5, 9, 13.5]),
            "Quick Grid with equal bays: bays + 1 lines");
        Throws<DocumentException>(() => GeometryEditService.LinesFromSpacings(true, 0, [6, 0]), "A spacing of zero is refused");
        var levels = GeometryEditService.LevelsFromHeights(-1.5, [4.5, 4.8]);
        Assert(levels.Select(level => (level.Name, level.Elevation)).SequenceEqual([("Base", -1.5), ("L1", 3d), ("Roof", 7.8)]),
            "Generate Levels: Base, L1 … and Roof at the running elevations");

        var rotated = new GridSystem("R", new(10, 20, 3), 90, [new("1", 0), new("2", 6)], [new("A", 0), new("B", 4, Visible: false)]);
        var crossings = GeometryEditService.GridIntersections(rotated, [3, 7]);
        Assert(crossings.Length == 4 && crossings.Contains(new Xyz(10, 26, 7)) && crossings.All(p => p.X == 10) &&
               GeometryEditService.GridIntersections(rotated, includeHidden: true).Length == 4 && !rotated.IsAxisAligned,
            "Grid intersections are global positions — a rotated, offset system included — at each level; hidden lines are left out unless asked");

        var s = new Sample();
        var suggestion = GeometryEditService.SuggestFromPoints(s.Document);
        Assert(suggestion.GridSystems.Single().XLines.Select(line => line.Coordinate).SequenceEqual([0d, 6]) &&
               suggestion.GridSystems[0].YLines.Select(line => line.Coordinate).SequenceEqual([0d, 4]) &&
               suggestion.Levels.Select(level => level.Elevation).SequenceEqual([0d, 5]),
            "The grids a model's points suggest: a line at each distinct X and Y, a level at each distinct Z");

        var generated = GeometryEditService.AddPoints(s.Document, GeometryEditService.GridIntersections(suggestion.GridSystems[0], [5, 10]));
        Assert(generated.Made.Length == 4 && generated.Made.All(p => p.Z == 10),
            "Generate Points at the grid intersections leaves out the positions points already hold (P2, P3, P5, P6 at Z = 5)");
        var count = s.Document.Points.Count;
        s.Session.Execute(generated.Command);
        Assert(s.Document.Points.Count == count + 4 && s.Session.UndoName == "Generate Points", "… and makes the others as one step");
    }

    private static void Drawing()
    {
        var s = new Sample();
        var count = (Points: s.Document.Points.Count, Frames: s.Document.Frames.Count);
        // between two existing joints: their ids are reused, nothing new but the frame
        var plan = GeometryEditService.PlanFrame(s.Document, GeometryEditService.Resolve(s.Document, new(0, 0, 5.0004)),
            GeometryEditService.Resolve(s.Document, new(0, 4, 5)));
        Assert(plan.Start.ExistingId == s.P2 && plan.End.ExistingId == s.P6 && plan.NewPoints == 0 && Math.Abs(plan.Length - 4) < 1e-12,
            "A frame drawn from within the tolerance of P2 to P6 uses those two points");
        var frame = ObjectId.New();
        s.Session.Execute(GeometryEditService.DrawMember(plan, frame, sectionId: s.Beam));
        Assert(s.Document.Points.Count == count.Points && s.Document.Frames[frame] is { StartPointId: var a, EndPointId: var b } && a == s.P2 && b == s.P6 &&
               s.Document.AssignmentOfFrame(frame).SectionId == s.Beam && s.Session.UndoName == "Draw Frame",
            "… and the draw is one step, the frame on the two points with the chosen section");

        // to empty space at a plan level: exactly one new point, at the level's elevation
        var level = 12.0;
        var started = GeometryEditService.Resolve(s.Document, new(6, 4, 5));
        var toSpace = GeometryEditService.PlanFrame(s.Document, started, GeometryEditService.Resolve(s.Document, new(6, 4, level)));
        ObjectId newEnd = ObjectId.New(), column = ObjectId.New();
        s.Session.Execute(GeometryEditService.DrawMember(toSpace, column, newEndId: newEnd));
        Assert(s.Document.Points.Count == count.Points + 1 && s.Document.Points[newEnd].Position == new Xyz(6, 4, level) &&
               s.Document.Frames[column].StartPointId == s.P5,
            "A frame drawn to empty space makes exactly one new point, at the coordinate of the plane it is drawn on (Z = 12)");

        // chained: the next segment starts on the point the last one made — one id, connected
        var next = GeometryEditService.PlanFrame(s.Document, GeometryEditService.Resolve(s.Document, new(6, 4, level)),
            GeometryEditService.Resolve(s.Document, new(0, 4, level)));
        Assert(next.Start.ExistingId == newEnd && next.NewPoints == 1, "Drawing on from the end of the last segment starts on its point: one joint, connected");

        Throws<DocumentException>(() => GeometryEditService.PlanFrame(s.Document, GeometryEditService.Resolve(s.Document, new(1, 1, 1)),
            GeometryEditService.Resolve(s.Document, new(1, 1, 1.0005))), "A member shorter than the tolerance is refused");
        Throws<DocumentException>(() => GeometryEditService.PlanFrame(s.Document, new(s.P3, s.Document.Points[s.P3].Position),
            new(s.P2, s.Document.Points[s.P2].Position)), "A frame between two points another frame joins is refused (F2, drawn backwards)");
        Throws<DocumentException>(() => GeometryEditService.PlanFrame(s.Document, new(s.P1, new(0, 0, 0)), new(s.P3, new(6, 0, 5))),
            "… and so is one where a cable joins them");
        var overlap = Throws<DocumentException>(() => GeometryEditService.PlanFrame(s.Document, GeometryEditService.Resolve(s.Document, new(0, 0, 2)),
            GeometryEditService.Resolve(s.Document, new(0, 0, 7))), "A frame along a frame it would overlap is refused");
        Assert(overlap.Message.Contains("'F1'"), "… naming the frame: " + overlap.Message);
        var extension = GeometryEditService.PlanFrame(s.Document, GeometryEditService.Resolve(s.Document, new(0, 0, 5)),
            GeometryEditService.Resolve(s.Document, new(0, 0, 8)));
        Assert(extension.Start.ExistingId == s.P2, "A frame that only continues one on its line, from its end, is drawn");
        Throws<DocumentException>(() => GeometryEditService.AddPoint(s.Document, new(6, 0, 5.0002), ObjectId.New()),
            "A point within the tolerance of a point is refused, not made beside it");
        var lone = ObjectId.New();
        s.Session.Execute(GeometryEditService.AddPoint(s.Document, new(14, 4, 9.3), lone));
        Assert(s.Document.Points[lone].Position == new Xyz(14, 4, 9.3) && s.Session.UndoName == "Add Point", "A point by coordinates is one step");

        s.Session.Undo();
        s.Session.Undo();
        Assert(!s.Document.Frames.ContainsKey(column) && !s.Document.Points.ContainsKey(newEnd) && s.Document.Frames.ContainsKey(frame),
            "Undo of a draw takes back the member and the point it made, and nothing before it");
    }

    private static void Moving()
    {
        var s = new Sample();
        var hash = s.Document.AnalysisInputHash;
        var before = s.Document;
        // F1 (P1–P2) and F2 (P2–P3) share P2: it moves once
        var plan = GeometryEditService.PlanMove(s.Document, [s.F1, s.F2], new(0.5, 0, 0));
        Assert(plan.PointIds.Length == 3 && plan.PointIds.Contains(s.P2) && plan.MembersChanged.Contains(s.F3) && plan.MembersChanged.Contains(s.C1),
            "Moving two frames that share a point moves their three points; the members on them change with them");
        s.Session.Execute(GeometryEditService.Move(plan));
        Assert(s.Document.Points[s.P2].Position == new Xyz(0.5, 0, 5) && s.Document.Points[s.P3].Position == new Xyz(6.5, 0, 5) &&
               s.Document.Points[s.P4].Position == new Xyz(6, 0, 0) && s.Document.Frames[s.F2].StartPointId == s.P2,
            "… each by the offset exactly once; the connectivity stays as it was");
        Assert(s.Document.AnalysisInputHash != hash && s.Session.UndoName == "Move", "A move is one step and changes the analysis input");
        s.Session.Undo();
        Assert(ReferenceEquals(s.Document, before), "Undo puts every point back");

        Throws<DocumentException>(() => GeometryEditService.PlanMove(s.Document, [s.P4], new(-6, 0, 0)),
            "A move that puts a point on another point (P4 onto P1) is refused");
        Throws<DocumentException>(() => GeometryEditService.PlanMove(s.Document, [s.P2], new(6, 0, 0)),
            "A move that would collapse a member (P2 onto P3, the far end of F2) is refused");
        Throws<DocumentException>(() => GeometryEditService.PlanMove(s.Document, [s.P2], new(0, 0, 0)), "A move of nothing is refused");
        Throws<DocumentException>(() => GeometryEditService.PlanMove(s.Document, [], new(1, 0, 0)), "A move without a selection is refused");
    }

    private static void Deleting()
    {
        var s = new Sample();
        var before = s.Document;
        // a frame alone: its end points stay, so does every other member on them, and its load goes with it
        var plan = GeometryEditService.PlanDelete(s.Document, [s.F2]);
        Assert(plan.Frames.SequenceEqual([s.F2]) && plan.Points.IsEmpty && plan.ConnectedMembers.IsEmpty && plan.Loads.SequenceEqual([s.LineLoad]),
            "Deleting a frame takes the frame and its load, and no point");
        s.Session.Execute(GeometryEditService.Delete(plan));
        Assert(!s.Document.Frames.ContainsKey(s.F2) && s.Document.Points.ContainsKey(s.P2) && s.Document.Points.ContainsKey(s.P3) &&
               s.Document.Frames.ContainsKey(s.F1) && s.Document.Frames.ContainsKey(s.F3) && !s.Document.Loads.ContainsKey(s.LineLoad) &&
               s.Document.Loads.Values.All(load => s.Document.TryGetNamed(load.TargetId, out _, out _)) && s.Session.UndoName == "Delete",
            "… its end points and the frames on them stay, and no load is left on an object that is gone");
        s.Session.Undo();
        Assert(ReferenceEquals(s.Document, before) && s.Document.Loads.ContainsKey(s.LineLoad), "Undo restores the frame with its id, name and load");

        // a point: the members and the shell on it go too, its restraint with it
        var point = GeometryEditService.PlanDelete(s.Document, [s.P1]);
        Assert(point.Points.SequenceEqual([s.P1]) && point.ConnectedMembers.ToHashSet().SetEquals([s.F1, s.C1]) && point.Restraints == 1 &&
               point.Loads.SequenceEqual([s.Target]),
            "Deleting a point takes the frame and the cable on it (with the cable's target force) and its restraint");
        var shellPoint = GeometryEditService.PlanDelete(s.Document, [s.P6]);
        Assert(shellPoint.Shells.SequenceEqual([s.S1]) && shellPoint.Loads.SequenceEqual([s.AreaLoad]), "… and a shell on a deleted point goes with its load");
        s.Session.Execute(GeometryEditService.Delete(point));
        Assert(!s.Document.Points.ContainsKey(s.P1) && !s.Document.Frames.ContainsKey(s.F1) && !s.Document.Cables.ContainsKey(s.C1) &&
               !s.Document.Restraints.ContainsKey(s.P1) && s.Document.Points.ContainsKey(s.P2), "The delete is made as planned");
        Throws<DocumentException>(() => GeometryEditService.Delete(GeometryEditService.PlanDelete(s.Document, [])), "Delete without a selection is refused");
    }
}
