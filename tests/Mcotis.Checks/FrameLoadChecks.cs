using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Mcotis.Analysis;
using Mcotis.Application;
using Mcotis.Core;
using Mcotis.Persistence;
using static Check;

/// <summary>Assign → Frame → Frame Load (docs/loads/FRAME_DISTRIBUTED_LOAD_PLAN.md): SAP2000's frame distributed loads —
/// the segments a definition gives, Add / Replace / Delete, validation and the file — and the frame element under them,
/// against closed forms worked out here: fixed-fixed and simply supported beams under uniform, triangular and
/// trapezoidal loads, projected loads on inclined frames, and distributed bending and torsional moments on cantilevers.
/// Every expected value comes from statics or beam theory in this file, never from the solver.</summary>
internal static class FrameLoadChecks
{
    private static ObjectId Id(int group, int number = 1) => ObjectId.Parse($"00000000-0000-0000-{group:D4}-{number:D12}");
    private static readonly ObjectId Steel = Id(61), Beam = Id(62), Dead = Id(63), Live = Id(63, 2), Case = Id(64);
    private const double E = 2e8, Nu = .3, A = .01, I = .0008, J = .0012;
    private static readonly double G = E / (2 * (1 + Nu));

    public static void Run()
    {
        SegmentsAsSap();
        AssignModes();
        ValidationAndFile();
        FixedFixedClosedForms();
        SimplySupportedTrapezoidStations();
        ProjectedOnInclinedFrames();
        LocalDirection();
        DistributedMoments();
        InactiveDofsAreRefused();
    }

    // ---- A model: one material, one section with equal bending inertias, two patterns, one linear case ----

    private sealed class Model
    {
        public readonly DocumentEditor Editor = ModelDocument.Create().Edit();
        private int points, frames;

        public Model(DofMask mask = DofMask.Full3D)
        {
            Editor.PutMaterial(new(Steel, "Steel", MaterialType.Steel, E, Nu, 0, 0));
            Editor.PutFrameSection(new(Beam, "Beam", Steel, FrameSectionShape.General, new(A, I, I, J)));
            Editor.PutLoadPattern(new(Dead, "DEAD", LoadPatternType.Dead));
            Editor.PutLoadPattern(new(Live, "LIVE", LoadPatternType.Live));
            Editor.PutLoadCase(new(Case, "LIVE", LoadCaseType.LinearStatic, [new(Live, 1)]));
            Editor.SetSettings(new AnalysisSettings(1e-6, 100, 4, false) { ActiveDofs = mask });
        }

        public ObjectId Point(double x, double y, double z, Restraint? restraint = null)
        {
            var id = Id(65, ++points);
            Editor.AddPoint(new(x, y, z), "P" + points, id);
            if (restraint is { } value) Editor.SetRestraint([id], value);
            return id;
        }

        public ObjectId Frame(ObjectId start, ObjectId end)
        {
            var id = Id(66, ++frames);
            Editor.AddFrame(start, end, "F" + frames, id);
            Editor.SetFrameSection([id], Beam);
            return id;
        }

        public int Assign(ObjectId frame, FrameDistributedLoadDefinition definition, LoadAssignMode mode = LoadAssignMode.Replace) =>
            FrameLoads.Assign(Editor, [frame], definition, mode);

        public ModelDocument Build() => Editor.Commit().Document;
    }

    private static FrameDistributedLoadDefinition Definition(LoadDirection direction, double uniform = 0, (double, double)[]? points = null,
        FrameLoadType type = FrameLoadType.Force, bool projected = false, LoadDistanceType distances = LoadDistanceType.Relative, ObjectId? pattern = null) =>
        new(pattern ?? Live, type, direction, projected, distances,
            (points ?? []).Select(point => new FrameLoadPoint(point.Item1, point.Item2)).ToImmutableArray(), uniform);

    private static readonly Restraint PinTwist = new(true, true, true, true, false, false), Roller = new(false, true, true, false, false, false);

    private static StaticResult Solve(ModelDocument document)
    {
        var compiled = ModelCompiler.Compile(document, 5);
        Assert(compiled.Success, "The model compiles: " + string.Join("; ", compiled.Issues.Select(issue => issue.Message)));
        var outcome = StaticSolver.Solve(compiled.Model!, Case);
        Assert(outcome.Success, "Solved: " + string.Join("; ", outcome.Issues.Select(issue => issue.Message)));
        Assert(outcome.Result!.Diagnostics.EquilibriumError <= 1e-8, "The internal forces of the whole model balance");
        return outcome.Result;
    }

    private static void Near(double actual, double expected, string description, double relative = 1e-9) =>
        Assert(double.IsFinite(actual) && Math.Abs(actual - expected) <= 1e-9 + relative * Math.Abs(expected),
            $"{description}: {actual:G17} vs {expected:G17}");

    private static ImmutableArray<double> Reaction(StaticResult result, ObjectId point) => result.Joints.Single(joint => joint.PointId == point).Reactions;
    private static ImmutableArray<double> U(StaticResult result, ObjectId point) => result.Joints.Single(joint => joint.PointId == point).Displacements;

    // ---- SAP2000's rules for a definition ----

    private static void SegmentsAsSap()
    {
        var frame = Id(66, 99);
        // the ETABS form of the screenshot: absolute 0 → 5 m, 0 → 8 kN/m, then back to 0 at the far end of a 10 m beam
        var segments = FrameLoads.Segments(Definition(LoadDirection.Gravity, points: [(0, 0), (5, 8), (10, 0), (10, 0)],
            distances: LoadDistanceType.Absolute), frame, 10);
        Assert(segments.Length == 2 && segments.All(s => s.TargetId == frame && s.PatternId == Live && s.Direction == LoadDirection.Gravity &&
               s.EnteredAs == LoadDistanceType.Absolute && s.Type == FrameLoadType.Force && !s.Projected),
            "A 4-point trapezoid gives one segment per pair of points; a pair at the same distance gives none");
        Assert(segments[0] is { RelativeStart: 0, RelativeEnd: .5, ValueStart: 0, ValueEnd: 8 } && segments[1] is { RelativeStart: .5, RelativeEnd: 1, ValueStart: 8, ValueEnd: 0 },
            "Absolute distances become relative to the frame length");

        var step = FrameLoads.Segments(Definition(LoadDirection.GlobalZ, uniform: -2, points: [(0, 0), (.4, 0), (.4, -5), (1, -5)]), frame, 4);
        Assert(step.Length == 2 && step[0] is { RelativeStart: .4, RelativeEnd: 1, ValueStart: -5, ValueEnd: -5 } &&
               step[1] is { RelativeStart: 0, RelativeEnd: 1, ValueStart: -2, ValueEnd: -2 },
            "A repeated distance is a step; an all-zero pair gives no segment; the uniform load is its own row 0 → 1");
        Assert(FrameLoads.Segments(Definition(LoadDirection.GlobalX), frame, 3).IsEmpty, "Nothing to load gives no segment");

        Throws<ArgumentException>(() => FrameLoads.Segments(Definition(LoadDirection.GlobalZ, points: [(0, 1), (.6, 1), (.5, 1)]), frame, 3),
            "Distances that decrease are refused");
        var outside = Throws<ArgumentException>(() => FrameLoads.Segments(Definition(LoadDirection.GlobalZ, points: [(0, 1), (12, 1)],
            distances: LoadDistanceType.Absolute), frame, 10), "An absolute distance beyond the frame is refused");
        Assert(outside.Message.Contains("point 2", StringComparison.Ordinal), "The refusal names the point at fault");
        Throws<ArgumentException>(() => FrameLoads.Segments(Definition(LoadDirection.Local2, uniform: 1, projected: true), frame, 3),
            "A projected local load is refused");
        Throws<ArgumentException>(() => FrameLoads.Segments(Definition(LoadDirection.GlobalZ, points: [(0, 1), (.2, 1), (.4, 1), (.6, 1), (1, 1)]), frame, 3),
            "A trapezoid has at most four points");

        Near(FrameLoads.IntensityOf(segments[0], .25), 4, "The value of a segment between its points");
        Assert(FrameLoads.IntensityOf(step[0], .3) == 0, "A segment is zero outside its distances");
        Near(FrameLoads.ProjectionFactor(new(Math.Cos(Math.PI / 6), 0, Math.Sin(Math.PI / 6)), LoadDirection.Gravity), Math.Cos(Math.PI / 6),
            "Projected gravity on a frame at 30°: cos 30°");
        Near(FrameLoads.ProjectionFactor(new(0, 0, 3), LoadDirection.GlobalZ), 0, "A vertical frame projects to nothing under a vertical load");
    }

    private static void AssignModes()
    {
        var m = new Model();
        var a = m.Point(0, 0, 0, Restraint.Fixed); var b = m.Point(6, 0, 0); var c = m.Point(12, 0, 0);
        var f1 = m.Frame(a, b); var f2 = m.Frame(b, c);
        var session = new DocumentSession(m.Build());
        int Count(ObjectId frame, ObjectId pattern) => FrameLoads.Of(session.Document, frame, pattern).Count();

        session.Execute(DocumentCommands.PutLoad(new FramePointLoad(Id(67), Live, f1, LoadDirection.GlobalZ, .5, -10)));
        session.Execute(DocumentCommands.AssignFrameDistributedLoad([f1, f2], Definition(LoadDirection.Gravity, uniform: 3), LoadAssignMode.Replace));
        session.Execute(DocumentCommands.AssignFrameDistributedLoad([f1], Definition(LoadDirection.Gravity, uniform: 1, pattern: Dead), LoadAssignMode.Add));
        Assert(Count(f1, Live) == 1 && Count(f2, Live) == 1 && Count(f1, Dead) == 1, "One assignment loads every selected frame");

        session.Execute(DocumentCommands.AssignFrameDistributedLoad([f1], Definition(LoadDirection.GlobalX, points: [(0, 2), (1, 4)]), LoadAssignMode.Add));
        Assert(Count(f1, Live) == 2, "Add to Existing keeps the loads the frame had");
        Near(FrameLoads.IntensityAt(session.Document, f1, Live, FrameLoadType.Force, LoadDirection.GlobalX, false, .5), 3, "Intensity of the added load at mid-length");

        session.Execute(DocumentCommands.AssignFrameDistributedLoad([f1], Definition(LoadDirection.GlobalZ, uniform: -5), LoadAssignMode.Replace));
        var live = FrameLoads.Of(session.Document, f1, Live).Single();
        Assert(live is { Direction: LoadDirection.GlobalZ, ValueStart: -5 } && Count(f1, Dead) == 1 && Count(f2, Live) == 1 &&
               session.Document.Loads.Values.OfType<FramePointLoad>().Count() == 1,
            "Replace Existing removes only the distributed loads of that pattern on that frame; point loads and other patterns stay");

        var same = session.Execute(DocumentCommands.AssignFrameDistributedLoad([f1], Definition(LoadDirection.GlobalZ, uniform: -5), LoadAssignMode.Replace));
        Assert(same.IsEmpty, "Replacing loads with the same loads is no step of the history");

        session.Execute(DocumentCommands.AssignFrameDistributedLoad([f1, f2], Definition(LoadDirection.GlobalZ), LoadAssignMode.Delete));
        Assert(Count(f1, Live) == 0 && Count(f2, Live) == 0 && Count(f1, Dead) == 1 && session.Document.Loads.Values.OfType<FramePointLoad>().Any(),
            "Delete Existing removes the pattern's distributed loads only");
        session.Undo();
        Assert(Count(f1, Live) == 1 && Count(f2, Live) == 1, "Undo restores every frame of the assignment in one step");

        var editor = session.Document.Edit();
        Throws<ArgumentException>(() => FrameLoads.Assign(editor, [f1], Definition(LoadDirection.GlobalZ, uniform: 1, pattern: Id(99)), LoadAssignMode.Add),
            "A load pattern the model does not have is refused");
        Throws<ArgumentException>(() => FrameLoads.Assign(editor, [a], Definition(LoadDirection.GlobalZ, uniform: 1), LoadAssignMode.Add),
            "Only frames take frame loads");
    }

    private static void ValidationAndFile()
    {
        var m = new Model();
        var a = m.Point(0, 0, 0, Restraint.Fixed); var b = m.Point(5, 0, 0);
        var frame = m.Frame(a, b);
        m.Assign(frame, Definition(LoadDirection.Gravity, points: [(0, 0), (2, 6)], projected: true, distances: LoadDistanceType.Absolute));
        m.Assign(frame, Definition(LoadDirection.Local1, uniform: 1.5, type: FrameLoadType.Moment), LoadAssignMode.Add);
        m.Assign(frame, Definition(LoadDirection.GlobalY, uniform: 2), LoadAssignMode.Add);
        var document = m.Build();

        var bad = document.Edit();
        var projectedLocal = new FrameDistributedLoad(Id(68), Live, frame, LoadDirection.Local2, 0, 1, 1, 1) { Projected = true };
        Assert(Throws<DocumentException>(() => { bad.PutLoad(projectedLocal); bad.Commit(); }, "A projected local load is not a valid document")
                .Issues.Any(issue => issue.Code == "load.value"), "It is refused as a load value");

        var json = NativeDocumentFormat.Serialize(document);
        var read = NativeDocumentFormat.Deserialize(json);
        Assert(read.Loads.Values.OfType<FrameDistributedLoad>().OrderBy(l => l.Id).SequenceEqual(document.Loads.Values.OfType<FrameDistributedLoad>().OrderBy(l => l.Id)),
            "Type, projection and how the distances were entered are saved and opened");
        Assert(read.AnalysisInputHash == document.AnalysisInputHash, "The opened document is the same analysis input");
        var file = (JsonObject)JsonNode.Parse(json)!;
        Assert((int)file["schemaVersion"]! == 8, "The file is schema 8");
        var loads = file["loads"]!.AsArray().Select(node => node!.AsObject()).ToArray();
        Assert(loads.Count(load => load.ContainsKey("loadType")) == 1 && loads.Count(load => load.ContainsKey("projected")) == 1 &&
               loads.Count(load => load.ContainsKey("enteredAs")) == 1, "A member is written only where it differs from a schema-7 load");

        foreach (var load in loads) { load.Remove("loadType"); load.Remove("projected"); load.Remove("enteredAs"); }
        file["schemaVersion"] = 7;
        var old = NativeDocumentFormat.Deserialize(file.ToJsonString());
        Assert(old.Loads.Values.OfType<FrameDistributedLoad>().All(load => load.Type == FrameLoadType.Force && !load.Projected && load.EnteredAs == LoadDistanceType.Relative),
            "A schema-7 file opens: its distributed loads are forces per unit frame length");

        var plain = new Model();
        var p = plain.Frame(plain.Point(0, 0, 0, Restraint.Fixed), plain.Point(5, 0, 0));
        var before = plain.Build();
        var editor = before.Edit();
        editor.PutLoad(new FrameDistributedLoad(Id(69), Live, p, LoadDirection.GlobalZ, 0, 1, -1, -1));
        var force = editor.Commit().Document;
        var momentEditor = before.Edit();
        momentEditor.PutLoad(new FrameDistributedLoad(Id(69), Live, p, LoadDirection.GlobalZ, 0, 1, -1, -1) { Type = FrameLoadType.Moment });
        var asAbsolute = before.Edit();
        asAbsolute.PutLoad(new FrameDistributedLoad(Id(69), Live, p, LoadDirection.GlobalZ, 0, 1, -1, -1) { EnteredAs = LoadDistanceType.Absolute });
        Assert(momentEditor.Commit().Document.AnalysisInputHash != force.AnalysisInputHash && asAbsolute.Commit().Document.AnalysisInputHash == force.AnalysisInputHash,
            "A moment is another analysis input; how the distances were entered is not");
    }

    // ---- Closed forms ----

    /// <summary>Fixed-fixed beam of 6 m along X: q over the span gives qL/2 and qL²/12 at each end; a triangle 0 → q gives
    /// 3qL/20, qL²/30 at the light end and 7qL/20, qL²/20 at the heavy end.</summary>
    private static void FixedFixedClosedForms()
    {
        const double L = 6, q = 10;
        foreach (var (name, definition, rI, rJ, mI, mJ) in new[]
        {
            ("uniform gravity", Definition(LoadDirection.Gravity, uniform: q), q * L / 2, q * L / 2, q * L * L / 12, q * L * L / 12),
            ("triangle 0 → q down", Definition(LoadDirection.GlobalZ, points: [(0, 0), (L, -q)], distances: LoadDistanceType.Absolute),
                3 * q * L / 20, 7 * q * L / 20, q * L * L / 30, q * L * L / 20),
        })
        {
            var m = new Model();
            var a = m.Point(0, 0, 0, Restraint.Fixed); var b = m.Point(L, 0, 0, Restraint.Fixed);
            m.Assign(m.Frame(a, b), definition);
            var result = Solve(m.Build());
            var (ra, rb) = (Reaction(result, a), Reaction(result, b));
            Near(ra[2], rI, $"Fixed-fixed, {name}: vertical reaction at I");
            Near(rb[2], rJ, $"Fixed-fixed, {name}: vertical reaction at J");
            Near(Math.Abs(ra[4]), mI, $"Fixed-fixed, {name}: end moment at I");
            Near(Math.Abs(rb[4]), mJ, $"Fixed-fixed, {name}: end moment at J");
            Assert(Math.Sign(ra[4]) == -Math.Sign(rb[4]), $"Fixed-fixed, {name}: the end moments hog at both ends");
        }
    }

    /// <summary>Simply supported beam of 10 m under the trapezoid of the ETABS form (0 → 8 kN/m at 5 m, back to 0 at 10 m)
    /// plus 2 kN/m uniform, and a partial-span load: reactions and the bending moment at every station from statics.</summary>
    private static void SimplySupportedTrapezoidStations()
    {
        const double L = 10;
        var m = new Model();
        var a = m.Point(0, 0, 0, PinTwist); var b = m.Point(L, 0, 0, Roller);
        var frame = m.Frame(a, b);
        m.Assign(frame, Definition(LoadDirection.Gravity, uniform: 2, points: [(0, 0), (5, 8), (10, 0)], distances: LoadDistanceType.Absolute));
        m.Assign(frame, Definition(LoadDirection.Gravity, points: [(.7, 3), (.9, 3)]), LoadAssignMode.Add);
        var result = Solve(m.Build());

        // the downward intensity, its total and its moment about I, by Simpson on each linear piece (exact)
        static double Q(double x) => 2 + (x <= 5 ? 8 * x / 5 : 8 * (10 - x) / 5) + (x >= 7 && x <= 9 ? 3 : 0);
        double[] breaks = [0, 5, 7, 9, 10];
        double Integral(Func<double, double> f, double upTo) => breaks.Zip(breaks.Skip(1)).Where(pair => pair.First < upTo)
            .Sum(pair => { double lo = pair.First, hi = Math.Min(pair.Second, upTo), mid = (lo + hi) / 2;
                const double e = 1e-12; return (hi - lo) / 6 * (f(lo + e) + 4 * f(mid) + f(hi - e)); });
        double total = Integral(Q, L), aboutI = Integral(x => Q(x) * x, L);
        double rB = aboutI / L, rA = total - rB;
        Near(Reaction(result, a)[2], rA, "Simply supported trapezoid: reaction at I", 1e-8);
        Near(Reaction(result, b)[2], rB, "Simply supported trapezoid: reaction at J", 1e-8);

        foreach (var station in result.Frames.Single().Segments.SelectMany(segment => segment.Stations))
        {
            double x = station.Relative * L;
            double moment = rA * x - Integral(s => Q(s) * (x - s), x);
            double bending = Math.Abs(station.Actions[4]) + Math.Abs(station.Actions[5]);
            Near(bending, Math.Abs(moment), $"Simply supported trapezoid: bending moment at {x:0.##} m", 1e-7);
        }
    }

    /// <summary>A frame at 30° and one at 60°, pinned at I and on a vertical roller at J: projected gravity q carries
    /// q × horizontal length, gravity q carries q × frame length. Projected X on the 30° frame: q × vertical length.</summary>
    private static void ProjectedOnInclinedFrames()
    {
        const double L = 8, q = 5;
        foreach (var degrees in new[] { 30.0, 60.0 })
        {
            double c = Math.Cos(degrees * Math.PI / 180), s = Math.Sin(degrees * Math.PI / 180);
            foreach (var projected in new[] { false, true })
            {
                var m = new Model();
                var a = m.Point(0, 0, 0, PinTwist); var b = m.Point(L * c, 0, L * s, Roller);
                m.Assign(m.Frame(a, b), Definition(LoadDirection.Gravity, uniform: q, projected: projected));
                var result = Solve(m.Build());
                double expected = q * (projected ? L * c : L);
                Near(Reaction(result, a)[2] + Reaction(result, b)[2], expected,
                    $"{degrees}° frame, {(projected ? "projected " : "")}gravity: the vertical reactions carry q × {(projected ? "horizontal" : "frame")} length");
                Near(Reaction(result, b)[2], expected / 2, $"{degrees}° frame, {(projected ? "projected " : "")}gravity: half at the roller (uniform load)");
            }
        }

        var x = new Model();
        double c30 = Math.Cos(Math.PI / 6), s30 = Math.Sin(Math.PI / 6);
        var i = x.Point(0, 0, 0, Restraint.Fixed); var j = x.Point(L * c30, 0, L * s30);
        x.Assign(x.Frame(i, j), Definition(LoadDirection.GlobalX, uniform: q, projected: true));
        Near(Reaction(Solve(x.Build()), i)[0], -q * L * s30, "Projected X on a 30° cantilever: q × vertical length");
    }

    /// <summary>Local 2 of a horizontal frame along X is +Z: a Local 2 load is the same load as Global Z.</summary>
    private static void LocalDirection()
    {
        const double L = 4, q = 3;
        StaticResult Cantilever(LoadDirection direction)
        {
            var m = new Model();
            var a = m.Point(0, 0, 0, Restraint.Fixed); var b = m.Point(L, 0, 0);
            m.Assign(m.Frame(a, b), Definition(direction, points: [(0, q), (1, 2 * q)]));
            return Solve(m.Build());
        }
        var local = Cantilever(LoadDirection.Local2);
        var axis2 = local.Frames.Single().Axis2;
        Assert(Math.Abs(axis2.Z - 1) < 1e-12, "Local 2 of a horizontal frame along X is +Z (SAP2000's default)");
        var global = Cantilever(LoadDirection.GlobalZ);
        Assert(local.Joints.Zip(global.Joints).All(pair => pair.First.Displacements.Zip(pair.Second.Displacements).All(d => Math.Abs(d.First - d.Second) <= 1e-15 + 1e-12 * Math.Abs(d.Second))),
            "A Local 2 load on it gives the displacements of the same load along Global Z");
        Near(Reaction(local, Id(65, 1))[2], -1.5 * q * L, "Its reaction is the total load");
    }

    /// <summary>Cantilever of 4 m along X under a distributed moment m: about Y, M(x) = m(L − x), tip rotation mL²/2EI and
    /// tip deflection −mL³/3EI; about X (torsion), tip twist mL²/2GJ; a fixed-end reaction of mL either way. On a simply
    /// supported beam a uniform distributed moment gives a constant shear m and no bending at all.</summary>
    private static void DistributedMoments()
    {
        const double L = 4, mValue = 2.5;
        StaticResult Cantilever(LoadDirection direction, out ObjectId fixedEnd, out ObjectId tip)
        {
            var model = new Model();
            fixedEnd = model.Point(0, 0, 0, Restraint.Fixed); tip = model.Point(L, 0, 0);
            model.Assign(model.Frame(fixedEnd, tip), Definition(direction, uniform: mValue, type: FrameLoadType.Moment));
            return Solve(model.Build());
        }

        var bending = Cantilever(LoadDirection.GlobalY, out var root, out var end);
        Near(U(bending, end)[4], mValue * L * L / (2 * E * I), "Distributed moment about Y: tip rotation mL²/2EI");
        Near(U(bending, end)[2], -mValue * L * L * L / (3 * E * I), "Distributed moment about Y: tip deflection −mL³/3EI");
        Near(Reaction(bending, root)[4], -mValue * L, "Distributed moment about Y: the fixed end resists mL");
        Near(Reaction(bending, root)[2], 0, "Distributed moment about Y: no vertical reaction");
        foreach (var station in bending.Frames.Single().Segments.SelectMany(segment => segment.Stations))
            Near(Math.Abs(station.Actions[4]) + Math.Abs(station.Actions[5]), mValue * L * (1 - station.Relative),
                $"Distributed moment about Y: M(x) = m(L − x) at x = {station.Relative * L:0.##} m");

        var torsion = Cantilever(LoadDirection.GlobalX, out root, out end);
        Near(U(torsion, end)[3], mValue * L * L / (2 * G * J), "Distributed torque: tip twist mL²/2GJ");
        Near(Reaction(torsion, root)[3], -mValue * L, "Distributed torque: the fixed end resists mL");
        foreach (var station in torsion.Frames.Single().Segments.SelectMany(segment => segment.Stations))
            Near(Math.Abs(station.Actions[3]), mValue * L * (1 - station.Relative), $"Distributed torque: T(x) = m(L − x) at x = {station.Relative * L:0.##} m");

        var m = new Model();
        var a = m.Point(0, 0, 0, PinTwist); var b = m.Point(L, 0, 0, Roller);
        m.Assign(m.Frame(a, b), Definition(LoadDirection.GlobalY, uniform: mValue, type: FrameLoadType.Moment));
        var simple = Solve(m.Build());
        Near(Math.Abs(Reaction(simple, a)[2]), mValue, "Simply supported under a uniform distributed moment: reactions ±m");
        Near(Reaction(simple, a)[2] + Reaction(simple, b)[2], 0, "…forming a couple");
        Assert(new[] { U(simple, a)[4], U(simple, b)[4], U(simple, b)[0] }.All(value => Math.Abs(value) < 1e-15), "…and the beam stays straight");
        foreach (var station in simple.Frames.Single().Segments.SelectMany(segment => segment.Stations))
        {
            Near(Math.Abs(station.Actions[4]) + Math.Abs(station.Actions[5]), 0, $"…no bending at x = {station.Relative * L:0.##} m");
            Near(Math.Abs(station.Actions[1]) + Math.Abs(station.Actions[2]), mValue, $"…a constant shear m at x = {station.Relative * L:0.##} m");
        }
    }

    /// <summary>A load the active degrees of freedom cannot carry is refused, never dropped: a moment about Z in the XZ plane.</summary>
    private static void InactiveDofsAreRefused()
    {
        var m = new Model(DofMask.XZ);
        var a = m.Point(0, 0, 0, Restraint.Fixed); var b = m.Point(4, 0, 0);
        m.Assign(m.Frame(a, b), Definition(LoadDirection.GlobalZ, uniform: 1, type: FrameLoadType.Moment));
        var compiled = ModelCompiler.Compile(m.Build(), 5);
        Assert(!compiled.Success && compiled.Issues.Any(issue => issue.Code == "dof.load.inactive" && issue.Message.Contains("about", StringComparison.Ordinal)),
            "A distributed moment about an inactive rotation is refused by name");

        var plane = new Model(DofMask.XZ);
        var c = plane.Point(0, 0, 0, Restraint.Fixed); var d = plane.Point(4, 0, 0);
        plane.Assign(plane.Frame(c, d), Definition(LoadDirection.GlobalY, uniform: 1, type: FrameLoadType.Moment));
        Assert(ModelCompiler.Compile(plane.Build(), 5).Success, "A distributed moment about Y is in the XZ plane");
    }
}
