using System.Collections.Immutable;
using Mcotis.Analysis;
using Mcotis.Application;
using Mcotis.Core;
using Mcotis.Persistence;

internal static class TemplateBuildingChecks
{
    internal static TemplateBuildingInput Input()
    {
        var m = new MaterialDefinition(ObjectId.New(), "Material", MaterialType.Steel, 2e8, .3, 78.5, 7.85);
        return new([4, 6], [5], [new(3, 10, 7), new(4, 12, 9)], m,
            new(ObjectId.New(), "Columns", m.Id, FrameSectionShape.General, new(.02, .0004, .0002, .00001)),
            new(ObjectId.New(), "Beams", m.Id, FrameSectionShape.General, new(.02, .0004, .0002, .00001)), Restraint.Fixed);
    }
    public static void Run()
    {
        var session = new DocumentSession();
        var original = session.Document;
        var input = Input();
        session.Execute(TemplateBuilding.Create(input));
        var d = session.Document;
        Check.Assert(d.Points.Count == 18 && d.Frames.Count == 26, "2X × 1Y × 2 storeys has 18 points, 12 columns, 8 X beams and 6 Y beams");
        Check.Assert(d.Points.Values.Single(p => p.Name == "P_2_2_1").Position == new Xyz(10, 5, 7), "bay widths and storey heights accumulate independently");
        Check.Assert(d.ReferenceGeometry.GridSystems.Single().XLines.Select(line => (line.Label, line.Coordinate)).SequenceEqual([("1", 0d), ("2", 4d), ("3", 10d)]) &&
               d.ReferenceGeometry.GridSystems[0].YLines.Select(line => (line.Label, line.Coordinate)).SequenceEqual([("A", 0d), ("B", 5d)]) &&
               d.ReferenceGeometry.Levels.Select(level => (level.Name, level.Elevation)).SequenceEqual([("Base", 0d), ("L1", 3d), ("Roof", 7d)]),
            "the building comes with its grid (1–3 × A–B on the bay lines) and its levels (Base, L1, Roof) to draw and navigate on");
        Check.Assert(d.Restraints.Count == 6 && d.Restraints.Values.All(r => r == Restraint.Fixed), "only six base points are restrained");
        Check.Assert(d.FrameAssignments.Count == 26 && d.Frames.Values.All(f => d.AssignmentOfFrame(f.Id).SectionId ==
            (f.Name.StartsWith("Column_") ? input.ColumnSection.Id : input.BeamSection.Id)), "columns and beams use the requested sections");
        Check.Assert(d.Loads.Count == 14 && d.Loads.Values.OfType<FrameDistributedLoad>().All(l => l.Direction == LoadDirection.GlobalZ && l.ValueStart == l.ValueEnd && l.ValueStart < 0 && l.RelativeStart == 0 && l.RelativeEnd == 1), "floor beams carry full-length downward UDLs; columns have no imposed line load");
        double imposed = 2 * (4 + 6) * (10 + 12) + 3 * 5 * (7 + 9); // 680 kN, direct independent grid accounting
        var compiled = ModelCompiler.Compile(d);
        Check.Assert(compiled.Success, "spatial template compiles with full 3D DOFs");
        var solved = StaticSolver.Solve(compiled.Model!, d.LoadCases.Keys.Single());
        Check.Assert(solved.Success && Math.Abs(solved.Result!.Joints.Sum(j => j.Reactions[2]) - imposed) < imposed * 1e-9,
            "summed vertical reactions balance the independently counted 680 kN line loading");
        Check.Assert(solved.Result!.Joints.All(j => j.Displacements.All(double.IsFinite)) &&
            solved.Result.Joints.Where(j => d.Restraints.ContainsKey(j.PointId)).All(j => j.Displacements.All(v => v == 0)), "finite displacement results with fixed base DOFs zero");
        double frameWeight = d.Frames.Values.Sum(f => d.Points[f.StartPointId].Position.DistanceTo(d.Points[f.EndPointId].Position) * .02 * 78.5);
        var weighted = new DocumentSession();
        weighted.Execute(TemplateBuilding.Create(input with { IncludeSelfWeight = true }));
        var weightedResult = StaticSolver.Solve(ModelCompiler.Compile(weighted.Document).Model!, weighted.Document.LoadCases.Keys.Single());
        Check.Assert(weightedResult.Success && Math.Abs(weightedResult.Result!.Joints.Sum(j => j.Reactions[2]) - imposed - frameWeight) < 1e-6, "optional member self-weight adds once to the entered beam line loads");
        session.Undo(); Check.Assert(ReferenceEquals(session.Document, original), "template generation is one undoable transaction");
        session.Redo(); Check.Assert(ReferenceEquals(session.Document, d), "redo restores exactly the same generated IDs and loads");
        Check.Throws<InvalidOperationException>(() => session.Execute(TemplateBuilding.Create(input)), "generation refuses to overwrite existing geometry");
        Check.Assert(ReferenceEquals(session.Document, d), "refused generation leaves the existing model intact");
        foreach (var invalid in new[] { input with { XBayWidths = [0] }, input with { YBayWidths = [double.NaN] },
            input with { Storeys = [new(3, -1, 0)] }, input with { XBayWidths = [], YBayWidths = [] },
            input with { Storeys = [] }, input with { XBayWidths = Enumerable.Repeat(5d, 200).ToImmutableArray() } })
            Check.Throws<ArgumentException>(() => TemplateBuilding.Create(invalid), "invalid template dimensions, loads or size rejected before mutation");
        foreach (bool xz in new[] { true, false })
        {
            var plane = new DocumentSession();
            plane.Execute(TemplateBuilding.Create(input with { XBayWidths = xz ? [4] : [], YBayWidths = xz ? [] : [5], BaseRestraint = Restraint.Pinned }));
            var pc = ModelCompiler.Compile(plane.Document);
            Check.Assert(pc.Success && plane.Document.Settings.ActiveDofs == (xz ? DofMask.XZ : DofMask.YZ), "zero bays in one direction selects the corresponding planar DOFs");
            Check.Assert(StaticSolver.Solve(pc.Model!, plane.Document.LoadCases.Keys.Single()).Success, "pinned-base planar building is stable and solvable");
        }
        var folder = Path.Combine(Path.GetTempPath(), "Mcotis-template-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "building.mcotis");
            var files = new DocumentLifecycle(session, new NativeDocumentStore()); files.SaveAs(path);
            var reopened = new DocumentSession(); new DocumentLifecycle(reopened, new NativeDocumentStore()).Open(path);
            Check.Assert(Check.SameContent(d, reopened.Document) && !reopened.IsDirty, "native Save/Open preserves the generated geometry, assignments, line loads, DOFs and IDs");
        }
        finally { Directory.Delete(folder, true); }
    }
}
