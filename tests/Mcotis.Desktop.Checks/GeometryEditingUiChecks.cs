using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mcotis.Application;
using Mcotis.Core;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Infrastructure;
using Mcotis.Desktop.Presentation;
using Mcotis.Desktop.Presentation.Controls;
using Mcotis.Desktop.Presentation.GeometryEditing;

/// <summary>
/// Geometry editing in the main window (docs/geometry/GEOMETRY_EDITING_PLAN.md, GRID_LEVEL_CONTROLLER.md; `--geometry-editing`):
/// the pure parts — planes, the grid as a plane shows it, snapping — and then the real window on a TemplateBuilding model,
/// which comes with its grid and levels: the plane navigator and the viewer's projection box are one view; a level shows
/// what lies on it; Draw Frame by clicks snaps to points, makes a point in empty space at the plane's coordinate, chains,
/// refuses a member along another; the view does not move while the model is edited; Move by two points and Delete are
/// one Undo step each; hidden kinds are not picked; grids are kept while results are locked and through Save / Open; in 3D
/// a click lands on the work plane.
/// </summary>
internal static class GeometryEditingUiChecks
{
    internal static void Run()
    {
        Pure();
        Window();
    }

    private static void Pure()
    {
        var model = new GeometryModel(new Dictionary<string, StructuralPoint>
        {
            ["a"] = new("a", new(0, 0, 0)), ["b"] = new("b", new(6, 0, 0)), ["c"] = new("c", new(6, 0, 4)), ["d"] = new("d", new(6, 4, 4)),
        }, [new("ab", "a", "b", MemberKind.Frame), new("bc", "b", "c", MemberKind.Frame), new("cd", "c", "d", MemberKind.Frame)]);
        var slice = ViewPlanes.Slice(model, "XY", 4);
        Check(slice.Points.Keys.Order().SequenceEqual(["c", "d"]) && slice.Members.Single().Name == "cd" &&
              ReferenceEquals(ViewPlanes.Slice(model, "XY", null), model) && ReferenceEquals(ViewPlanes.Slice(model, "3D", 4), model),
            "A plane shows the points on it and the members lying in it — a column crossing it (bc) is not drawn as if connected there; the projection and 3D show all");
        Check(ViewPlanes.DefaultCoordinate(model, "XZ") is null && ViewPlanes.DefaultCoordinate(slice, "XY") == 4 &&
              ViewPlanes.ToWorld(ViewPlanes.Project(new(1, 2, 3), "YZ"), "YZ", 7) == new Position(7, 2, 3),
            "A plane opens on the coordinate every point shares, else on the whole model projected; screen ↔ model keeps the plane's fixed coordinate");

        var reference = new ReferenceGeometry([new GridSystem("GLOBAL", new(0, 0, 0), 0, [new("1", 0), new("2", 6)], [new("A", 0), new("B", 4)])],
            [new Level("Base", 0), new Level("L1", 4)]);
        var choices = ViewPlanes.Choices("XY", reference, model);
        Check(choices[0].Coordinate is null && choices.Skip(1).Select(choice => choice.Label).SequenceEqual(
                [$"Base · Z = {ViewPlanes.Metres(0)} m", $"L1 · Z = {ViewPlanes.Metres(4)} m"]),
            "The XY navigator lists the projection, then the levels — a point's Z that is a level is not listed twice");
        var elevations = ViewPlanes.Choices("XZ", reference, model);
        Check(elevations.Count(choice => choice.ReferenceName is "A" or "B") == 2 && elevations.Any(choice => choice.Label.StartsWith("Grid B", StringComparison.Ordinal)),
            "The XZ navigator lists the Y grid lines A and B (planes of constant Y)");

        var plan = ReferenceLines.For(reference, "XY", 4, model);
        var elevation = ReferenceLines.For(reference, "XZ", 0, model);
        Check(plan.Lines.Count == 4 && plan.Crossings.Count == 4 && plan.Lines.All(line => line.A.Z == 4) &&
              elevation.Lines.Count(line => line.Kind == ReferenceLineKind.GridX) == 2 && elevation.Lines.Count(line => line.Kind == ReferenceLineKind.Level) == 2 &&
              elevation.Crossings.Count == 4 && elevation.Lines.All(line => line.A.Y == 0),
            "In plan the X and Y lines and their 4 crossings at the level; in the XZ elevation at grid A the X lines stand vertical and the levels lie across, crossing 4 times");

        var near = new Point(100, 100);
        var candidates = new[]
        {
            new SnapCandidate(SnapKind.GridIntersection, new(101, 100), new(0, 0, 0), "Grid A/1"),
            new SnapCandidate(SnapKind.Point, new(106, 100), new(0, 0, 0), "P1", "P1"),
            new SnapCandidate(SnapKind.Midpoint, new(130, 100), new(0, 0, 0), "mid"),
        };
        Check(SnapResolver.Pick(near, candidates, SnapResolver.Defaults)?.Kind == SnapKind.Point &&
              SnapResolver.Pick(near, candidates, SnapModes.GridIntersection)?.Kind == SnapKind.GridIntersection &&
              SnapResolver.Pick(near, candidates, SnapModes.Midpoint) is null &&
              SnapResolver.Ortho(new(0, 0), new(50, 8)) == new Point(50, 0),
            "Snapping: a point within reach beats a nearer grid crossing; a kind switched off is never picked; nothing beyond 10 px; Ortho holds the cursor horizontal or vertical");
        var crossing = SnapResolver.AlongSegments(new(50, 50), [
            new("h", new(0, 50), new(100, 50), new(0, 0, 0), new(10, 0, 0)),
            new("v", new(50, 0), new(50, 100), new(5, -5, 0), new(5, 5, 0)),
            new("over", new(40, 60), new(60, 40), new(4, 1, 9), new(6, -1, 9)),
        ], null, null).Where(candidate => candidate.Kind == SnapKind.Intersection).ToArray();
        Check(crossing.Length == 1 && crossing[0].Members!.SequenceEqual(["h", "v"]) && crossing[0].World == new Position(5, 0, 0),
            "Two members that cross in the plane give an intersection snap naming both; one that passes over them at another depth gives none");
    }

    private static void Window()
    {
        var folder = Path.GetFullPath(Path.Combine(".work", "geometry-ui-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder);
        var bindings = new StringWriter();
        var listener = new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        MainWindow? window = null;
        try
        {
            // a building of 2 × 1 bays and two storeys: grid lines 1–3 at X 0 / 4 / 10, A–B at Y 0 / 5, levels Base 0, L1 3, Roof 7
            var building = new DocumentSession();
            building.Execute(TemplateBuilding.Create(new TemplateBuildingViewModel().ReadInput() with
            {
                XBayWidths = [4, 6], YBayWidths = [5], Storeys = [new(3, 10, 7), new(4, 12, 9)],
            }));
            var created = building.Document;
            var state = StudyComposerChecks.CreateState();
            var sap = new Program.FakeSap { Snapshot = new(state.Model!, state.Geometry, state.LoadPatterns.ToArray()) };
            var workflow = new WorkflowService(sap, state);
            var main = new MainViewModel(state, workflow, new NoSelection(), new ConfigService(sap, state, workflow),
                femSessionStore: new FemWorkingSessionStore(Path.Combine(folder, "session")));
            var warnings = new List<string>();
            main.WarningSink = (title, body) => warnings.Add($"{title}: {body}");
            main.LoadDefaultFemModel(replaceCurrent: true);
            window = new MainWindow { DataContext = main, Width = 1400, Height = 860 };
            window.Show(); Idle();
            main.OpenNativeDocument(created, "Building", null, LegacyNativeDocumentMigration.Source.Generated("TemplateBuilding")); Idle();
            var viewer = window.SharedViewer;
            var canvas = viewer.Canvas;
            Check(main.SharedViewerReference.Equals(created.ReferenceGeometry) && main.SharedViewerReference.Levels.Length == 3 && !main.IsModelDirty,
                "A generated building opened in the main window keeps its grid and levels (1–3 × A–B; Base, L1, Roof) — not as an edit");
            var points = main.SharedViewerModel.Points.Count;
            var frames = main.SharedViewerModel.Members.Count;
            var hash = main.NativeDocumentSession.Document.AnalysisInputHash;

            // ---- one view context ----
            viewer.ShowPlane("XY"); Idle();
            Check(main.ViewPlane == "XY" && main.ViewCoordinate is null && main.SharedViewerVisibleModel.Points.Count == points,
                "The viewer's projection box and the window are one view: XY there is XY here, opening on the whole building projected");
            main.SelectedViewPlaneChoice = main.ViewPlaneChoices.Single(choice => choice.ReferenceName == "L1"); Idle();
            Check(main.ViewCoordinate == 3 && viewer.Model.Points.Count == 6 && viewer.Model.Members.Count == 7 &&
                  viewer.Model.Points.Values.All(point => point.Position.Z == 3) && main.ViewPlaneTitle.Contains("L1", StringComparison.Ordinal),
                "Level L1 shows its 6 joints and the 7 beams lying on it — no column — and the view names it");
            Check(main.StepViewPlane(1) && main.ViewCoordinate == 7 && !main.StepViewPlane(1) && main.StepViewPlane(-1) && main.ViewCoordinate == 3,
                "Previous / next step from level to level and stop at the ends");
            main.ViewPlane = "XZ"; Idle();
            Check(viewer.CurrentPlane == "XZ" && main.ViewPlaneChoices.Any(choice => choice.ReferenceName == "B"), "… and the window's plane sets the viewer's");
            main.ViewPlane = "XY"; Idle();
            Check(main.ViewCoordinate == 3, "Back to XY: the level it was left at");
            window.UpdateLayout(); Idle();

            // ---- Draw Frame by clicks on L1 ----
            Point At(double x, double y, double z) => canvas.ToScreen(new(x, y, z)) ?? throw new Exception("FAIL: the canvas has not been drawn");
            var origin = At(0, 0, 3);
            main.DrawingTool = DrawingTool.Frame; Idle();
            Check(viewer.DrawingTool == DrawingTool.Frame && canvas.DrawingTool == DrawingTool.Frame, "Draw → Frame turns the Frame tool on in the view");
            Check(canvas.ClickTool(At(4, 0, 3)) && canvas.HasToolInteraction, "A click on joint 1/A at L1 starts the frame there");
            canvas.ClickTool(At(4, 2.5, 3));
            Check(main.SharedViewerModel.Members.Count == frames && canvas.HasToolInteraction && main.Message.Contains("lie along", StringComparison.Ordinal),
                "A frame along the beam it would overlap (to its midpoint) is refused, with the reason in the status bar; the start stays: " + main.Message);
            Check(canvas.ClickTool(At(7, 2.5, 3)), "A click in empty space ends the frame there");
            var afterFirst = main.NativeDocumentSession.Document;
            var made = main.SharedViewerModel.Points.Values.Where(point => !created.TryFindObject(point.Name, out _)).ToArray();
            Check(made.Length == 1 && made[0].Position.Z == 3 && Math.Abs(made[0].Position.X - 7) < 1e-9 && Math.Abs(made[0].Position.Y - 2.5) < 1e-9 &&
                  main.SharedViewerModel.Members.Count == frames + 1 && main.NativeDocumentSession.UndoName == "Draw Frame" && canvas.HasToolInteraction,
                $"… making exactly one new point ({made.FirstOrDefault()?.Name}) at the level's Z = 3 — one Undo step — and the chain goes on from it");
            var newPoint = made[0].Name;
            canvas.HoverTool(At(10, 5, 3));
            Check(canvas.CursorSnap is { Kind: SnapKind.Point, Joint: "P_1_2_1" }, "Hovering joint 3/B snaps to it: " + canvas.CursorSnap?.Label);
            Idle();
            Snapshot(window, ".work/geometry-editing-draw.png");   // the preview: instruction, member under way, snap glyph and read-out
            canvas.ClickTool(At(10, 5, 3));
            var chained = main.SharedViewerModel.Members.Where(member => member.Start == newPoint || member.End == newPoint).ToArray();
            Check(main.SharedViewerModel.Members.Count == frames + 2 && main.SharedViewerModel.Points.Count == points + 1 && chained.Length == 2 &&
                  chained.Any(member => member.Start == "P_1_2_1" || member.End == "P_1_2_1"),
                "The chained frame starts on the point the first one made: one joint, connected, and ends on joint 3/B");
            Check((At(0, 0, 3) - origin).Length < 1e-6, "The view did not move while the model was edited: a model position is where it was on the screen");
            Check(canvas.CancelToolInteraction() && !canvas.HasToolInteraction && main.DrawingTool == DrawingTool.Frame,
                "Esc drops the member under way; the tool stays on");
            canvas.ToolRightClick(); Idle();
            Check(main.DrawingTool == DrawingTool.None, "A right-click without a member under way leaves the tool");
            Snapshot(window, ".work/geometry-editing-ui.png");

            // ---- Move by two points, Move by DX DY DZ, Delete ----
            canvas.SelectJoint(newPoint);
            main.DrawingTool = DrawingTool.Move; Idle();
            canvas.ClickTool(At(7, 2.5, 3));
            canvas.ClickTool(At(7, 1.5, 3));
            var moved = main.SharedViewerModel.Points[newPoint].Position;
            Check(Math.Abs(moved.X - 7) < 1e-9 && Math.Abs(moved.Y - 1.5) < 1e-9 && moved.Z == 3 && main.NativeDocumentSession.UndoName == "Move" &&
                  main.SharedViewerModel.Members.Count(member => member.Start == newPoint || member.End == newPoint) == 2,
                "Move by two points: the selected point from its base to the destination, the frames on it following — one Undo step");
            main.DrawingTool = DrawingTool.None;
            var refused = Throws(() => main.MoveObjects([newPoint], [], new(-3, -1.5, 0)));
            Check(refused.Contains("land on", StringComparison.Ordinal) && main.SharedViewerModel.Points[newPoint].Position == moved,
                "A move onto joint 1/A is refused — two joints in one place, not connected — and nothing moves: " + refused);
            var firstFrame = chained.First(member => member.Start != "P_1_2_1" && member.End != "P_1_2_1").Name;
            main.DeleteObjectsAsync([firstFrame], []).GetAwaiter().GetResult(); Idle();
            Check(!main.SharedViewerModel.Members.Any(member => member.Name == firstFrame) && main.SharedViewerModel.Points.ContainsKey(newPoint) &&
                  main.SharedViewerModel.Points.ContainsKey("P_1_1_0") && main.NativeDocumentSession.UndoName == "Delete",
                "Deleting a frame leaves its end points; Delete is one Undo step, named");
            foreach (var _ in Enumerable.Range(0, 4)) main.UndoModel();
            Check(main.SharedViewerModel.Points.Count == points && main.SharedViewerModel.Members.Count == frames &&
                  main.NativeDocumentSession.Document.AnalysisInputHash == hash,
                "Undo of the delete, the move and both draws gives back the building exactly — the same analysis input");
            main.RedoModel();
            Check(main.NativeDocumentSession.Document.Frames.Count == afterFirst.Frames.Count && main.SharedViewerModel.Points.Count == points + 1, "Redo draws the first frame again");
            main.UndoModel();

            // ---- points ----
            var added = main.AddPoint(new(2, 2.5, 7));
            Check(main.SharedViewerModel.Points[added].Position == new Position(2, 2.5, 7) && main.NativeDocumentSession.UndoName == "Add Point",
                $"Draw → Point by coordinates makes {added} at (2, 2.5, 7)");
            Check(Throws(() => main.AddPoint(new(2, 2.5, 7.0004))).Contains(added, StringComparison.Ordinal),
                "A point within 1 mm of another is refused, naming it");
            main.UndoModel();
            var generated = main.GeneratePoints(GeometryEditService.GridIntersections(main.SharedViewerReference.GridSystems[0], [10]).Select(p => new Position(p.X, p.Y, p.Z)));
            Check(generated == 6 && main.NativeDocumentSession.UndoName == "Generate Points", "Generate Points at Grid Intersections at Z = 10: 6 points, one step");
            main.UndoModel();

            // ---- the forms ----
            var form = new ReferenceGeometryWindow(main, levels: true) { Owner = window };
            form.Show(); Idle();
            Check(form.Systems.Count == 1 && form.Levels.Count == 3 && form.Tabs.SelectedIndex == 1, "Define → Levels opens the Grids and Levels form on its Levels tab, with the model's grid and levels");
            form.Levels.Single(level => level.Name == "L1").Elevation = 7;
            Check(!form.ApplyForm() && main.SharedViewerReference.Levels.Single(level => level.Name == "L1").Elevation == 3,
                "Two levels at one elevation are not applied: the model keeps L1 at 3");
            form.Levels.Single(level => level.Name == "L1").Elevation = 3.5;
            Check(form.ApplyForm() && main.SharedViewerReference.Levels.Single(level => level.Name == "L1").Elevation == 3.5 &&
                  main.NativeDocumentSession.UndoName == "Edit Levels" && main.SharedViewerModel.Points.Count == points,
                "Apply writes the staged levels as one step (Edit Levels) and moves no joint");
            form.Close();
            main.UndoModel();
            var pointForm = new PointCoordinatesWindow(main, []) { Owner = window };
            pointForm.SetForm("1", "4", "7", "PX");
            Check(pointForm.Add() && main.SharedViewerModel.Points["PX"].Position == new Position(1, 4, 7), "Point by Coordinates adds the named point");
            pointForm.SetTable("1\t1\t7\n2;2;7\n1 1 7\n");
            Check(pointForm.Add() && main.NativeDocumentSession.UndoName == "Add Points" && main.SharedViewerModel.Points.Count == points + 3,
                "… and a pasted table as one step, the repeated row left out");
            pointForm.SetTable("1 2\n");
            Check(!pointForm.Add(), "A table row without three values is refused, naming its line");
            pointForm.Close();
            var memberForm = new MemberCoordinatesWindow(main, []) { Owner = window };
            memberForm.SetEnds(new(1, 4, 7), new(2, 2, 7));
            Check(memberForm.Add() && main.NativeDocumentSession.UndoName == "Draw Frame" &&
                  main.SharedViewerModel.Members.Any(member => member.Start == "PX" || member.End == "PX"),
                "Frame by Coordinates joins the points at its typed ends");
            memberForm.Close();
            var moveForm = new MoveSelectionWindow(main, ["PX"], []) { Owner = window };
            moveForm.SetOffset(0, 0, 0.5);
            Check(moveForm.Apply() && main.SharedViewerModel.Points["PX"].Position.Z == 7.5, "Edit → Move by DZ moves the point");
            moveForm.Close();
            foreach (var _ in Enumerable.Range(0, 4)) main.UndoModel();
            Check(main.SharedViewerModel.Points.Count == points && main.SharedViewerModel.Members.Count == frames, "… each one Undo step");
            var gridPoints = new GenerateGridPointsWindow(main) { Owner = window };
            gridPoints.Tick("Roof");
            Check(gridPoints.Generate() && main.SharedViewerModel.Points.Count == points && main.Message.Contains("already has a point", StringComparison.Ordinal),
                "Generate Points at Grid Intersections at Roof, where every crossing has a joint, makes none and says so");
            gridPoints.Close();

            // ---- what is shown is what can be picked ----
            main.ViewPlane = "XY"; main.ViewCoordinate = 3; Idle();
            var beamMiddle = new Point((At(0, 0, 3).X + At(4, 0, 3).X) / 2, At(0, 0, 3).Y);
            var shown = canvas.HitMemberAt(beamMiddle);
            main.ShowFrames = false; Idle(); window.UpdateLayout(); Idle();
            Check(shown is not null && canvas.HitMemberAt(beamMiddle) is null && canvas.MemberScreenLines.Count == 0 && main.SharedViewerModel.Members.Count == frames,
                "Display Options → Frames off: the beams are not drawn and cannot be picked; they are still in the model");
            main.ShowFrames = true; Idle();

            // ---- grids and levels are references ----
            var run = FemModelFileChecks.Analyze(main).SingleOrDefault() ?? throw new Exception($"FAIL: the building does not run: {main.FemProgressText} | {string.Join(" | ", warnings)}");
            var resultHash = main.NativeDocumentSession.Document.AnalysisInputHash;
            var grid = main.SharedViewerReference.GridSystems[0];
            var edited = main.SharedViewerReference with
            {
                GridSystems = [grid with { XLines = [.. grid.XLines.Select(line => line.Label == "2" ? line with { Coordinate = 4.5 } : line)] }],
            };
            Check(main.HasFemResults && main.IsFemModelLocked && main.ApplyReferenceGeometry(edited) && main.HasFemResults && main.IsFemModelLocked &&
                  main.NativeDocumentSession.Document.AnalysisInputHash == resultHash && main.SharedViewerModel.Points["P_1_1_0"].Position.X == 4,
                "Moving grid line 2 to X = 4.5 on a model its results lock moves no joint and keeps the results: a grid is no analysis input");
            main.UnlockFemModel.Execute(null); Idle();
            var path = Path.Combine(folder, "building.mcotis");
            main.SaveFemModelFileAsync(path).GetAwaiter().GetResult();
            var reopenedState = StudyComposerChecks.CreateState();
            var reopenedWorkflow = new WorkflowService(sap, reopenedState);
            var reopened = new MainViewModel(reopenedState, reopenedWorkflow, new NoSelection(), new ConfigService(sap, reopenedState, reopenedWorkflow),
                femSessionStore: new FemWorkingSessionStore(Path.Combine(folder, "reopened")));
            reopened.OpenFemModelFile(path);
            Check(reopened.SharedViewerReference.Equals(main.SharedViewerReference) && reopened.SharedViewerReference.GridSystems[0].XLines[1].Coordinate == 4.5,
                "Save / Open: the .mcotis keeps the grid systems and levels as they were edited");
            main.AddPoint(new(1, 1, 3));
            Check(main.SharedViewerReference.Equals(edited.Normalized()), "An edit of the model afterwards keeps the grid (the document is made again from the configuration, its grid carried)");
            main.ApplyReferenceGeometry(created.ReferenceGeometry);
            var withPoint = main.SharedViewerModel.Points.Count;
            main.UndoModel();
            Check(main.SharedViewerReference.Equals(edited.Normalized()) && main.SharedViewerModel.Points.Count == withPoint && main.Message.Contains("grids and levels", StringComparison.Ordinal),
                "Undo of a grid edit gives the grid back and leaves the model as it is");

            // ---- 3D: the work plane ----
            main.ViewPlane = "XY"; main.ViewCoordinate = 3; Idle();
            viewer.ShowPlane("3D"); Idle(); window.UpdateLayout(); Idle();
            var target = viewer.ProjectToScreen3D(new(2, 2.5, 3)) ?? throw new Exception("FAIL: the 3D view has no camera");
            Check(main.ViewPlane == "3D" && main.SharedViewerWorkPlane == "XY" && main.SharedViewerWorkCoordinate == 3 &&
                  viewer.RayToWorkPlane(target) is { } onPlane && Math.Abs(onPlane.X - 2) < 1e-6 && Math.Abs(onPlane.Y - 2.5) < 1e-6 && Math.Abs(onPlane.Z - 3) < 1e-9,
                "In 3D the last plane shown is the work plane: the line of sight through the screen meets it at the position drawn there (2, 2.5, 3 on L1) — no depth guessed");
            main.DrawingTool = DrawingTool.Point; Idle();
            var resolved = viewer.Resolve3D(target);
            Check(resolved is { Kind: SnapKind.None } && Math.Abs(resolved.World.Z - 3) < 1e-9,
                $"The Point tool in 3D puts the cursor on the work plane where nothing is to snap to ({resolved?.Kind} {resolved?.Label})");
            main.DrawingTool = DrawingTool.None;

            var problems = bindings.ToString().Split('\n').Where(line => line.Contains("ViewPlane", StringComparison.Ordinal) || line.Contains("SharedViewer", StringComparison.Ordinal) ||
                line.Contains("DrawingTool", StringComparison.Ordinal) || line.Contains("Snap", StringComparison.Ordinal) || line.Contains("DrawFrame", StringComparison.Ordinal)).ToArray();
            Check(problems.Length == 0, "No binding of the new view properties fails" + (problems.Length > 0 ? ": " + string.Join(" | ", problems.Take(3)) : ""));
            Check(warnings.Count == 0, "No warning was raised" + (warnings.Count > 0 ? ": " + string.Join(" | ", warnings) : ""));
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            window?.Close();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string Throws(Action action)
    {
        try { action(); }
        catch (InvalidOperationException ex) { return ex.Message; }
        throw new Exception("FAIL: the edit was not refused");
    }

    private static void Snapshot(Window window, string path)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * dpi.DpiScaleX), (int)(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Idle() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private sealed class NoSelection : ISelectionService
    {
        public IReadOnlyList<string>? Select(string title, IReadOnlyList<string> available, IReadOnlyList<string> selected) => null;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }
}
