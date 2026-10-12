using COT_VCF.Domain;
using COT_VCF.Geometry;
using Mcotis.Analysis;
using Mcotis.Core;

/// <summary>Tests can reference both engines; the standalone engine itself must never reference the legacy app.</summary>
internal static class SpatialFrameRegressionChecks
{
    public static void Run()
    {
        var beam = new GeometryModel(new Dictionary<string, StructuralPoint>
        {
            ["A"] = new("A", new(0, 0, 0)), ["B"] = new("B", new(5, 0, 0)), ["C"] = new("C", new(10, 0, 0)),
        }, [new("AB", "A", "B", MemberKind.Frame), new("BC", "B", "C", MemberKind.Frame)]);
        Compare("two-element point-loaded beam", beam, ["A", "C"], new([new("B", 0, -100)], [], []));
        var single = new GeometryModel(new Dictionary<string, StructuralPoint>
        { ["A"] = new("A", new(0, 0, 0)), ["C"] = new("C", new(10, 0, 0)) }, [new("Beam", "A", "C", MemberKind.Frame)]);
        var loads = new FemLoadSet([], [new("Beam", FemLoadDirection.Gravity, 0, 1, 10, 10)],
            [new("Beam", FemLoadDirection.GlobalZ, .3, -100)]);
        Compare("distributed and member point loads", single, ["A", "C"], loads);
        Compare("loaded beam with both end moments released", single, ["A", "C"], loads, releases: true);
        var portal = new GeometryModel(new Dictionary<string, StructuralPoint>
        {
            ["A"] = new("A", new(0, 0, 0)), ["B"] = new("B", new(0, 0, 4)),
            ["C"] = new("C", new(6, 0, 4)), ["D"] = new("D", new(6, 0, 0)),
        }, [new("Left", "A", "B", MemberKind.Frame), new("Top", "B", "C", MemberKind.Frame), new("Right", "D", "C", MemberKind.Frame)]);
        var portalLoads = new FemLoadSet([new("B", 0, -30, 1), new("C", 10, -40)], [], []);
        Compare("XZ portal", portal, ["A", "D"], portalLoads);
        Compare("XZ portal with 30-degree section roll", portal, ["A", "D"], portalLoads, angle: 30);
        Compare("XZ portal with 90-degree section roll", portal, ["A", "D"], portalLoads, angle: 90);
    }

    private static void Compare(string name, GeometryModel geometry, string[] pins, FemLoadSet loads, bool releases = false, double angle = 0)
    {
        const double e = 2e8, area = .01, i33 = .0008, i22 = .0004;
        double theta = angle * Math.PI / 180, effectiveInertia = i33 * Math.Pow(Math.Cos(theta), 2) + i22 * Math.Pow(Math.Sin(theta), 2);
        var sections = geometry.Members.ToDictionary(m => m.Name, m => new FemSectionProperties(m.Name, area, e,
            effectiveInertia, releases, releases, LocalAngleDeg: angle));
        var legacy = new PlanarFrameSolver().SolveCases(geometry, sections, new Dictionary<string, FemSectionProperties>(),
            new Dictionary<string, double>(), FemLoadSet.Empty, [new("Compare", loads, 0)], pins, frameMeshSegments: 10)["Compare"];
        var editor = ModelDocument.Create().Edit();
        var material = ObjectId.New(); var section = ObjectId.New(); var pattern = ObjectId.New(); var loadCase = ObjectId.New();
        editor.PutMaterial(new(material, "Steel", MaterialType.Steel, e, .3, 0, 0));
        editor.PutFrameSection(new(section, "Beam section", material, FrameSectionShape.General, new(area, i33, i22, .00001)));
        var pointIds = geometry.Points.Keys.ToDictionary(n => n, _ => ObjectId.New());
        var frameIds = geometry.Members.ToDictionary(m => m.Name, _ => ObjectId.New());
        foreach (var point in geometry.Points.Values)
        {
            editor.AddPoint(new(point.Position.X, point.Position.Y, point.Position.Z), point.Name, pointIds[point.Name]);
            if (pins.Contains(point.Name)) editor.SetRestraint([pointIds[point.Name]], Restraint.Pinned);
        }
        foreach (var member in geometry.Members)
        {
            editor.AddFrame(pointIds[member.Start], pointIds[member.End], member.Name, frameIds[member.Name]);
            editor.SetFrameSection([frameIds[member.Name]], section);
            editor.SetFrameLocalAxis([frameIds[member.Name]], angle);
            if (releases) editor.SetFrameReleases([frameIds[member.Name]], new(
                new(false, false, false, false, false, true), new(false, false, false, false, false, true)));
        }
        editor.PutLoadPattern(new(pattern, "LOAD", LoadPatternType.Other));
        foreach (var load in loads.Joints) editor.PutLoad(new Mcotis.Core.PointLoad(ObjectId.New(), pattern, pointIds[load.PointName],
            new(load.FxKN, 0, load.FzKN), new(0, load.MyKNm, 0)));
        foreach (var load in loads.Members) editor.PutLoad(new FrameDistributedLoad(ObjectId.New(), pattern, frameIds[load.MemberName],
            load.Direction == FemLoadDirection.Gravity ? LoadDirection.Gravity : LoadDirection.GlobalZ,
            load.RelStart, load.RelEnd, load.ValueStart, load.ValueEnd));
        foreach (var load in loads.MemberPoints) editor.PutLoad(new FramePointLoad(ObjectId.New(), pattern, frameIds[load.MemberName],
            LoadDirection.GlobalZ, load.Rel, load.ValueKN));
        editor.PutLoadCase(new(loadCase, "Compare", LoadCaseType.LinearStatic, [new(pattern, 1)]));
        editor.SetSettings(AnalysisSettings.Default with { ActiveDofs = DofMask.XZ, FrameMeshSegments = 1, CableSag = false });
        var compiled = ModelCompiler.Compile(editor.Commit().Document);
        if (!compiled.Success) throw new Exception(name + " compile failed: " + string.Join("; ", compiled.Issues));
        var solved = LinearStaticSolver.Solve(compiled.Model!, loadCase);
        if (!solved.Success) throw new Exception(name + " solve failed: " + string.Join("; ", solved.Issues));
        var spatial = solved.Result!;
        void Near(double actual, double expected, double absolute = 1e-8, double relative = 1e-7)
        {
            if (Math.Abs(actual - expected) > absolute + relative * Math.Abs(expected))
                throw new Exception($"{name}: spatial {actual:G17}, legacy {expected:G17}");
        }
        foreach (var old in legacy.Joints)
        {
            var current = spatial.Joints.Single(j => j.PointId == pointIds[old.PointName]);
            Near(current.Displacements[0], old.UXM, 1e-10);
            Near(current.Displacements[2], old.UZM, 1e-10);
            // Legacy slope is positive toward +Z for +X: it is the negative of physical global RY.
            if (!spatial.Diagnostics.UnconnectedRotations.Any(d => d.PointId == current.PointId && d.Component == DofComponent.RY))
                Near(current.Displacements[4], -old.RotationRad, 1e-10);
        }
        foreach (var old in legacy.SupportReactions)
        {
            var current = spatial.Joints.Single(j => j.PointId == pointIds[old.PointName]);
            Near(current.Reactions[0], old.ReactionXKN); Near(current.Reactions[2], old.ReactionZKN);
        }
        foreach (var old in legacy.Frames)
        {
            var current = spatial.Frames.Single(f => f.ParentObjectId == frameIds[old.MemberName]);
            var member = geometry.Members.Single(m => m.Name == old.MemberName);
            var (start, end) = (geometry.Points[member.Start].Position, geometry.Points[member.End].Position);
            double dx = end.X - start.X, dz = end.Z - start.Z, length = Math.Sqrt(dx * dx + dz * dz);
            Xyz normal = new(-dz / length, 0, dx / length);
            var f = current.LocalEndActions;
            Xyz Force(int start) => current.Axis1 * f[start] + current.Axis2 * f[start + 1] + current.Axis3 * f[start + 2];
            Near(-f[0], old.AxialStartKN); Near(f[6], old.AxialEndKN);
            Near(normal.Dot(Force(0)), old.ShearStartKN); Near(-normal.Dot(Force(6)), old.ShearEndKN);
            Near(Force(3).Y, old.MomentStartKNm); Near(-Force(9).Y, old.MomentEndKNm);
        }
        Console.WriteLine("PASS: spatial versus legacy XZ regression — " + name);
    }
}
