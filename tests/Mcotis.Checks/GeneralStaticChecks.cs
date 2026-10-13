using System.Collections.Immutable;
using System.Text.Json;
using Mcotis.Analysis;
using Mcotis.Core;
using static Check;

/// <summary>The general static solver against results worked out here by hand: frames split at the points lying on
/// them, tension-only cables, pretension fitted to target forces, staged cases and cable sag. Every expected value
/// comes from a closed form or from an independent scalar solution in this file, never from the solver.</summary>
internal static class GeneralStaticChecks
{
    private static ObjectId Id(int group, int number = 1) => ObjectId.Parse($"00000000-0000-0000-{group:D4}-{number:D12}");
    private static readonly ObjectId Steel = Id(21), Strand = Id(21, 2), Beam = Id(22), Rope = Id(22, 2);
    private static readonly ObjectId Dead = Id(23), Live = Id(23, 2), Prestress = Id(23, 3), Wind = Id(23, 4);
    private const double Es = 2e8, A = .01, Iz = .0008, Iy = .0004, J = .00001;
    private const double Ec = 1.6e8, Ac = .002, Gamma = 77, CableEA = Ec * Ac, CableWeight = Gamma * Ac;
    private const double TipStiffness = 3 * Es * Iz / (4.0 * 4 * 4);   // cantilever of 4 m: 3EI/L³

    public static void Run()
    {
        Numerics();
        InteriorJoints();
        Stations();
        NonlinearFrameCases();
        HangingCable();
        CableOnCantilever();
        TargetForcesAndStages();
        InteractingAndSeriesCables();
        RigidRotationAndPlanePreset();
        CableSag();
        Refusals();
        ProgressReports();
        CrossCheckOpenSees();
    }

    private sealed class Model
    {
        public readonly DocumentEditor Editor = ModelDocument.Create().Edit();
        private readonly Func<Xyz, Xyz> place;
        private int points, frames, cables, loads, cases;

        public Model(DofMask mask = DofMask.Full3D, bool sag = false, double fitTolerancePercent = 1e-6, double inertia22 = Iy, Func<Xyz, Xyz>? place = null, int mesh = 1,
            DofMask cableDofs = DofMask.Translations)
        {
            this.place = place ?? (xyz => xyz);
            Editor.PutMaterial(new(Steel, "Steel", MaterialType.Steel, Es, .3, 0, 0));
            Editor.PutMaterial(new(Strand, "Strand", MaterialType.Tendon, Ec, .3, Gamma, 7.85));
            Editor.PutFrameSection(new(Beam, "Beam", Steel, FrameSectionShape.General, new(A, Iz, inertia22, J)));
            Editor.PutCableSection(new(Rope, "Rope", Strand, Ac));
            Editor.PutLoadPattern(new(Dead, "DEAD", LoadPatternType.Dead, 1));
            Editor.PutLoadPattern(new(Live, "LIVE", LoadPatternType.Live));
            Editor.PutLoadPattern(new(Prestress, "PT", LoadPatternType.Prestress));
            Editor.PutLoadPattern(new(Wind, "WIND", LoadPatternType.Other));
            Editor.SetSettings(new AnalysisSettings(fitTolerancePercent, 100, mesh, sag) { ActiveDofs = mask, CableDofs = cableDofs });
        }

        public ObjectId Point(double x, double y, double z, Restraint? restraint = null)
        {
            var id = Id(31, ++points);
            Editor.AddPoint(place(new(x, y, z)), "P" + points, id);
            if (restraint is { } value) Editor.SetRestraint([id], value);
            return id;
        }

        public ObjectId Frame(ObjectId start, ObjectId end, EndReleases? releases = null)
        {
            var id = Id(32, ++frames);
            Editor.AddFrame(start, end, "F" + frames, id);
            Editor.SetFrameSection([id], Beam);
            if (releases is { } value) Editor.SetFrameReleases([id], value);
            return id;
        }

        public ObjectId Cable(ObjectId start, ObjectId end, double? target = null, ObjectId? pattern = null)
        {
            var id = Id(33, ++cables);
            Editor.AddCable(start, end, "C" + cables, id);
            Editor.SetCableSection([id], Rope);
            if (target is { } force) Editor.PutLoad(new CableTargetForce(Id(34, ++loads), pattern ?? Prestress, id, force));
            return id;
        }

        public void Load(ObjectId point, double x, double y, double z, ObjectId? pattern = null)
        {
            // a rotated model is loaded by the rotated force, so that it is the same structure turned in space
            var origin = place(default);
            Editor.PutLoad(new PointLoad(Id(34, ++loads), pattern ?? Live, point, place(new(x, y, z)) - origin));
        }

        public void LineLoad(ObjectId frame, double start, double end, double valueStart, double valueEnd) =>
            Editor.PutLoad(new FrameDistributedLoad(Id(34, ++loads), Live, frame, LoadDirection.GlobalZ, start, end, valueStart, valueEnd));

        public void MemberLoad(ObjectId frame, double at, double value) =>
            Editor.PutLoad(new FramePointLoad(Id(34, ++loads), Live, frame, LoadDirection.GlobalZ, at, value));

        public ObjectId Case(string name, LoadCaseType type, (ObjectId Pattern, double Factor)[] components, ObjectId? initial = null, bool fit = false)
        {
            var id = Id(35, ++cases);
            Editor.PutLoadCase(new(id, name, type, components.Select(c => new LoadCaseComponent(c.Pattern, c.Factor)).ToImmutableArray())
                { InitialCaseId = initial, FitCableTargetForces = fit });
            return id;
        }

        public ModelDocument Build() => Editor.Commit().Document;
    }

    private static readonly (ObjectId, double)[] LiveOnly = [(Live, 1)];

    private static void Near(double actual, double expected, string description, double absolute = 1e-10, double relative = 1e-9) =>
        Assert(double.IsFinite(actual) && Math.Abs(actual - expected) <= absolute + relative * Math.Abs(expected),
            $"{description}: {actual:G17} vs {expected:G17}");

    private static StaticOutcome Run(ModelDocument document, ObjectId caseId)
    {
        var compiled = ModelCompiler.Compile(document, 5);
        Assert(compiled.Success, "Model compiles: " + string.Join("; ", compiled.Issues));
        return StaticSolver.Solve(compiled.Model!, caseId);
    }

    private static StaticResult Solve(ModelDocument document, ObjectId caseId)
    {
        var outcome = Run(document, caseId);
        Assert(outcome.Success, "Static solution available: " + string.Join("; ", outcome.Issues));
        Assert(outcome.Result!.InputHash == document.AnalysisInputHash && outcome.Result.Revision == 5 && outcome.Result.LoadCaseId == caseId,
            "Results retain the snapshot, the revision and the case");
        Assert(outcome.Result.Diagnostics.EquilibriumError <= 1e-8, "The internal forces of the whole model balance");
        return outcome.Result;
    }

    private static void Refuse(ModelDocument document, ObjectId caseId, string code)
    {
        var hash = document.AnalysisInputHash;
        var outcome = Run(document, caseId);
        Assert(outcome.Result is null && outcome.Issues.Any(issue => issue.Code == code && issue.Severity == ValidationSeverity.Error),
            $"Refused with diagnostic {code} and no result: " + string.Join("; ", outcome.Issues.Select(issue => issue.Code)));
        Assert(document.AnalysisInputHash == hash, "Refusing never edits the snapshot");
    }

    private static ImmutableArray<double> U(StaticResult result, ObjectId point) => result.Joints.Single(joint => joint.PointId == point).Displacements;
    private static ImmutableArray<double> Reaction(StaticResult result, ObjectId point) => result.Joints.Single(joint => joint.PointId == point).Reactions;
    private static StaticCableResult CableOf(StaticResult result, ObjectId cable) => result.Cables.Single(item => item.ParentObjectId == cable);
    private static Xyz Translation(StaticResult result, ObjectId point) => new(U(result, point)[0], U(result, point)[1], U(result, point)[2]);

    /// <summary>The free body: support reactions and the forces of suppressed DOFs against a total applied force.</summary>
    private static Xyz ReactionSum(StaticResult result) => result.Joints.Aggregate(default(Xyz), (sum, joint) => sum +
        new Xyz(joint.Reactions[0] + joint.SuppressedDofForces[0], joint.Reactions[1] + joint.SuppressedDofForces[1], joint.Reactions[2] + joint.SuppressedDofForces[2]));

    private static double Bisect(Func<double, double> increasing, double low, double high)
    {
        for (int i = 0; i < 200; i++)
        {
            double middle = (low + high) / 2;
            if (increasing(middle) > 0) high = middle; else low = middle;
        }
        return (low + high) / 2;
    }

    private static void Numerics()
    {
        var even = DenseAlgebra.MinimumNormSolve(new double[,] { { 1, 1 }, { 1, 1 } }, [2, 2]);
        Near(even[0], 1, "A system that fixes only a sum is solved by the smallest correction"); Near(even[1], 1, "… split evenly");
        var regular = DenseAlgebra.MinimumNormSolve(new double[,] { { 4, 1, 1 }, { 1, 3, 0 }, { 1, 0, 2 } }, [5, -5, 7]);
        for (int i = 0; i < 3; i++) Near(regular[i], new[] { 1.0, -2, 3 }[i], "A regular system gets its ordinary solution");
        var deficient = DenseAlgebra.MinimumNormSolve(new double[,] { { 2, 0, 0 }, { 0, 0, 0 }, { 0, 0, 5 } }, [4, 0, 10]);
        Near(deficient[0], 2, "A rank-deficient system: the determined parts are solved"); Near(deficient[1], 0, "… and the undetermined part gets nothing"); Near(deficient[2], 2, "… (third)");
        var nearly = DenseAlgebra.MinimumNormSolve(new double[,] { { 1, 1 + 1e-11 }, { 1 - 1e-11, 1 } }, [2, 2 + 1e-12], 1e-7);
        Near(nearly[0], 1, "Columns equal to within the accuracy of the matrix count as dependent", 1e-9); Near(nearly[1], 1, "… and share the correction", 1e-9);
        var inconsistent = DenseAlgebra.MinimumNormSolve(new double[,] { { 1, 1 }, { 1, 1 } }, [1, 3]);
        Near(inconsistent[0], 1, "An inconsistent system gets its least-squares solution"); Near(inconsistent[1], 1, "… of smallest length");

        // the sagging-cable tension against an independent bisection of T/k − a/T² = s/k
        foreach (var (k, a, s) in new[] { (6400.0, 123.5, 0.0), (6400.0, 123.5, 80.0), (6400.0, 123.5, -500.0), (106666.7, .02, 760.0), (800.0, 4000.0, -3.0) })
        {
            double tension = CableElement.SaggingTension(k, a, s);
            double reference = Bisect(t => t / k - a / (t * t) - s / k, 1e-9, Math.Abs(s) + Math.Cbrt(k * a) + 1);
            Near(tension, reference, "Sagging cable tension equals an independent bisection of the cable equation", 1e-9, 1e-10);
            Assert(tension > 0 && tension >= s, "A cable with weight never goes slack, and sag only adds to its straight tension");
        }

        // The backward error equation by equation. K = diag(4, 9), u = (1, 1), f = (4, 8): the second equation is 1 out.
        // Scaled by √K_ii the residuals are (0, 1/3); the scale is ‖K‖·‖u‖ + ‖f‖ = 1·3 + 8/3 = 17/3.
        var (stiff, moved, loaded) = (new double[,] { { 4, 0 }, { 0, 9 } }, new double[] { 1, 1 }, new double[] { 4, 8 });
        var byEquation = DenseAlgebra.ScaledResiduals(stiff, moved, loaded);
        Near(byEquation[0], 0, "The scaled backward error of a balanced equation is zero"); Near(byEquation[1], 1.0 / 17, "… and that of an unbalanced one is its residual over the scale of the system");
        Assert(DenseAlgebra.ScaledResidual(stiff, moved, loaded) == byEquation.Max(), "The backward error of a solution is the largest of its equations'");
    }

    private sealed class Reports : IProgress<StaticProgress>
    {
        public readonly List<StaticProgress> Items = [];
        public void Report(StaticProgress value) => Items.Add(value);
    }

    /// <summary>What a run tells a view that follows it: the degrees of freedom, every equilibrium iteration joint by
    /// joint, and how the case ended — without changing a digit of the result.</summary>
    private static void ProgressReports()
    {
        var s = BuildTipCable();
        var compiled = ModelCompiler.Compile(s.Document, 5).Model!;
        ObjectId[] staged = [s.Pretension, s.Service];
        var silent = StaticSolver.Solve(compiled, staged);
        var reports = new Reports();
        var followed = StaticSolver.Solve(compiled, staged, progress: reports);
        Assert(staged.All(caseId =>
                   followed[caseId].Result!.Joints.Zip(silent[caseId].Result!.Joints).All(pair => pair.First.Displacements.SequenceEqual(pair.Second.Displacements) && pair.First.Reactions.SequenceEqual(pair.Second.Reactions)) &&
                   followed[caseId].Result!.Cables.Zip(silent[caseId].Result!.Cables).All(pair => pair.First.Tension == pair.Second.Tension && pair.First.AppliedPretension == pair.Second.AppliedPretension) &&
                   followed[caseId].Result!.Diagnostics.NonlinearIterations == silent[caseId].Result!.Diagnostics.NonlinearIterations),
            "Following a run changes no digit of its results");

        var fit = reports.Items.Where(item => item.CaseId == s.Pretension).ToArray();
        Assert(fit[0] is { Kind: StaticProgressKind.Started, ActiveDofs: 15, RestrainedDofs: 9, SolvedDofs: 6, FitRun: 0, Balance: null },
            "A case starts by reporting its degrees of freedom: 15 on (6 at each frame joint, 3 at the cable's anchor), 9 restrained, 6 solved");
        Assert(fit[^1] is { Kind: StaticProgressKind.Finished, FitRun: 2, Balance: { Joints: 1, JointsInBalance: 1, Linear: false } last } && last.OutOfBalance <= last.Tolerance &&
               last.Iteration == followed[s.Pretension].Result!.Diagnostics.NonlinearIterations && last.Worst?.PointId == s.Tip,
            "… and ends with its last equilibrium, every joint in balance, and the number of fitting runs its pretension took");
        var iterations = fit[1..^1];
        Assert(iterations.Length > 0 && iterations.All(item => item is { Kind: StaticProgressKind.Equilibrium, Balance: { Joints: 1 } balance } &&
                   balance.JointsInBalance == (balance.OutOfBalance <= balance.Tolerance ? 1 : 0)),
            "In between, every equilibrium iteration reports the out-of-balance and the joints within the tolerance");
        Assert(iterations[0] is { FitRun: 1, Balance: { Iteration: 1, JointsInBalance: 0 } first } && first.Worst == new DofKey(s.Tip, DofComponent.UZ) &&
               Math.Abs(first.OutOfBalance - 50) < 1e-9 && first.Tolerance == 1e-9 * 50,
            "The first iteration of the first fitting run: the 50 kN pretension is out of balance at the tip, vertically, against 1e-9 of it");
        var runs = iterations.Select(item => item.FitRun).ToArray();
        Assert(runs.Max() == 2 && runs.SequenceEqual(runs.OrderBy(run => run == 0 ? int.MaxValue : run)),
            "The fitting runs are numbered as they come, and the case's own equilibrium follows them");
        // an equilibrium counts its iterations from 1: where the count starts again, the one before has ended
        var ends = iterations.Where((_, index) => index == iterations.Length - 1 || iterations[index + 1].Balance!.Iteration == 1).ToArray();
        Assert(ends.Length == 4 && ends.All(item => item.Balance!.OutOfBalance <= item.Balance.Tolerance),
            "Every equilibrium of the fit is iterated to within the tolerance: two fitting runs, the trial pretension of the first, and the case's own");

        var service = reports.Items.Where(item => item.CaseId == s.Service).ToArray();
        Assert(service.Length >= 3 && service[0].Kind == StaticProgressKind.Started && service[^1] is { Kind: StaticProgressKind.Finished, FitRun: 2 } &&
               service[1..^1].All(item => item is { Kind: StaticProgressKind.Equilibrium, FitRun: 0 }),
            "A case continuing from the fit case reports its own equilibrium only: the fit is shared, not repeated");
        Assert(reports.Items.Take(fit.Length).All(item => item.CaseId == s.Pretension) && reports.Items.Count == fit.Length + service.Length,
            "Cases are reported one after the other, in the order they were asked for");

        // a linear case is solved in one step, and judged by its backward error
        var m = new Model();
        var root = m.Point(0, 0, 0, Restraint.Fixed);
        var tip = m.Point(4, 0, 0);
        m.Frame(root, tip);
        m.Load(tip, 0, 0, -100);
        var linearCase = m.Case("Linear", LoadCaseType.LinearStatic, LiveOnly);
        var direct = new Reports();
        var linear = StaticSolver.Solve(ModelCompiler.Compile(m.Build(), 5).Model!, linearCase, progress: direct);
        Assert(linear.Success && direct.Items.Select(item => item.Kind).SequenceEqual([StaticProgressKind.Started, StaticProgressKind.Equilibrium, StaticProgressKind.Finished]) &&
               direct.Items[1].Balance is { Linear: true, Iteration: 0, Joints: 1, JointsInBalance: 1, Tolerance: 1e-10 } solved &&
               solved.OutOfBalance == linear.Result!.Diagnostics.ScaledResidual && direct.Items[2] is { ActiveDofs: 12, RestrainedDofs: 6, SolvedDofs: 6, FitRun: 0 },
            "A linear case is solved directly: one report, with the scaled backward error it is accepted by");

        // joints are counted one by one
        var c = new Model(cableDofs: DofMask.UZ);
        var fixedEnd = c.Point(-4, 0, 0, Restraint.Fixed);
        var loadedTip = c.Point(0, 0, 0);
        var link = c.Point(0, 0, 3);
        var anchor = c.Point(0, 0, 6, Restraint.Pinned);
        c.Frame(fixedEnd, loadedTip);
        c.Cable(loadedTip, link);
        c.Cable(link, anchor);
        c.Load(loadedTip, 0, 0, -50);
        var pulled = c.Case("Down", LoadCaseType.NonlinearStatic, LiveOnly);
        var two = new Reports();
        Assert(StaticSolver.Solve(ModelCompiler.Compile(c.Build(), 5).Model!, pulled, progress: two).Success &&
               two.Items[1].Balance is { Iteration: 1, Joints: 2, JointsInBalance: 1 } start && start.Worst == new DofKey(loadedTip, DofComponent.UZ) &&
               two.Items[^1].Balance is { Joints: 2, JointsInBalance: 2 },
            "Joints are counted one by one: with a load at one of two joints, the other is in balance at the first iteration; at the end both are");

        // a run that gives no result says so, with its first error
        var h = new Model();
        var top = h.Point(0, 0, 5, Restraint.Pinned);
        var node = h.Point(0, 0, 0);
        h.Cable(node, top);
        h.Load(node, 0, 0, -100);
        var hang = h.Case("Hang", LoadCaseType.NonlinearStatic, LiveOnly);
        var linearWithCable = h.Case("Linear", LoadCaseType.LinearStatic, LiveOnly);
        var hanging = ModelCompiler.Compile(h.Build(), 5).Model!;
        var unstable = new Reports();
        Assert(!StaticSolver.Solve(hanging, hang, progress: unstable).Success && unstable.Items.Count(item => item.Kind == StaticProgressKind.Failed) == 1 &&
               unstable.Items[^1] is { Kind: StaticProgressKind.Failed, Issue.Code: "static.matrix.unstable", ActiveDofs: 6, RestrainedDofs: 3, SolvedDofs: 3 },
            "An unstable model ends its report with the failure and its first error");
        var refused = new Reports();
        Assert(!StaticSolver.Solve(hanging, linearWithCable, progress: refused).Success &&
               refused.Items is [{ Kind: StaticProgressKind.Failed, Issue.Code: "static.cable.linearCase", SolvedDofs: 0, Balance: null }],
            "A case refused before it is solved reports the refusal alone");
    }

    private static void InteriorJoints()
    {
        (ModelDocument Document, ObjectId Left, ObjectId Right, ObjectId Middle, ObjectId Case) Beam(bool explicitFrames)
        {
            var m = new Model();
            var left = m.Point(0, 0, 0, new(true, true, true, true, false, false));
            var right = m.Point(10, 0, 0, new(false, true, true, false, false, false));
            var middle = m.Point(5, 0, 0);
            if (explicitFrames) { m.Frame(left, middle); m.Frame(middle, right); }
            else m.Frame(left, right);
            m.Load(middle, 0, 0, -100);
            var loadCase = m.Case("Live", LoadCaseType.LinearStatic, LiveOnly);
            return (m.Build(), left, right, middle, loadCase);
        }
        var one = Beam(false);
        var two = Beam(true);
        var split = Solve(one.Document, one.Case);
        var modelled = Solve(two.Document, two.Case);
        Near(U(split, one.Middle)[2], -100 * 1000.0 / (48 * Es * Iz), "A load on a point lying on a beam: PL³/48EI at that point");
        Near(Reaction(split, one.Left)[2], 50, "… and half the load at each support", 1e-8);
        Near(Reaction(split, one.Right)[2], 50, "… (right)", 1e-8);
        foreach (var point in new[] { one.Left, one.Right, one.Middle })
            for (int c = 0; c < 6; c++)
            {
                Near(U(split, point)[c], U(modelled, point)[c], "A frame split at a point on it equals the same beam modelled as two frames (displacement)", 1e-13, 1e-10);
                Near(Reaction(split, point)[c], Reaction(modelled, point)[c], "… (reaction)", 1e-9);
            }
        var beam = split.Frames.Single();
        Assert(beam.Segments.Length == 2 && beam.Segments[0].EndPointId == one.Middle && beam.Segments[1].StartPointId == one.Middle &&
               beam.Segments[0] is { RelativeStart: 0, RelativeEnd: 0.5 } && beam.Segments[1] is { RelativeStart: 0.5, RelativeEnd: 1 },
            "The frame's result holds its pieces in order");
        Near(Math.Abs(beam.Segments[0].LocalEndActions[11]), 250, "Bending moment where the frame was split: PL/4", 1e-8);
        Near(beam.Segments[0].LocalEndActions[11] + beam.Segments[1].LocalEndActions[5], 0, "… the same on both sides of the split", 1e-8);
        Assert(beam.LocalEndActions.Take(6).SequenceEqual(beam.Segments[0].LocalEndActions.Take(6)) &&
               beam.LocalEndActions.Skip(6).SequenceEqual(beam.Segments[1].LocalEndActions.Skip(6)),
            "The frame's own end actions are those of its first and last piece");

        // loads that run across the split, on a cantilever of 4 m with a point at its middle
        ObjectId root = default, tip = default;
        StaticResult Cantilever(Action<Model, ObjectId> load)
        {
            var m = new Model();
            root = m.Point(0, 0, 0, Restraint.Fixed);
            tip = m.Point(4, 0, 0);
            m.Point(2, 0, 0);
            load(m, m.Frame(root, tip));
            var loadCase = m.Case("Live", LoadCaseType.LinearStatic, LiveOnly);
            return Solve(m.Build(), loadCase);
        }
        const double L = 4;
        double from = .2 * L, to = .9 * L, slope = (-7 + 2) / (to - from), intercept = -2 - slope * from;
        double Deflection(double x) => (intercept * (L * Math.Pow(x, 3) - Math.Pow(x, 4) / 4) + slope * (.75 * L * Math.Pow(x, 4) - Math.Pow(x, 5) / 5)) / (6 * Es * Iz);
        var trapezoid = Cantilever((m, frame) => m.LineLoad(frame, .2, .9, -2, -7));
        Near(U(trapezoid, tip)[2], Deflection(to) - Deflection(from), "A varying line load across a split frame: the tip deflection of the whole member");
        Near(Reaction(trapezoid, root)[2], -(intercept * (to - from) + slope * (to * to - from * from) / 2), "… and its whole resultant at the support", 1e-8);
        double TipUnderPoint(double a, double p) => p * a * a * (3 * L - a) / (6 * Es * Iz);
        Near(U(Cantilever((m, frame) => m.MemberLoad(frame, .3, -9)), tip)[2], TipUnderPoint(.3 * L, -9), "A member point load before the split: Pa²(3L − a)/6EI");
        Near(U(Cantilever((m, frame) => m.MemberLoad(frame, .8, -9)), tip)[2], TipUnderPoint(.8 * L, -9), "A member point load after the split");
        Near(U(Cantilever((m, frame) => m.MemberLoad(frame, .5, -9)), tip)[2], TipUnderPoint(.5 * L, -9), "A member point load exactly at the split is applied once");

        // a release belongs to the frame's own end, not to the place where the frame was split
        var hinged = new Model();
        var start = hinged.Point(0, 0, 0, Restraint.Fixed);
        var end = hinged.Point(4, 0, 0, new(false, true, true, false, false, false));
        var centre = hinged.Point(2, 0, 0);
        var released = hinged.Frame(start, end, new(new(false, false, false, false, false, true), EndRelease.None));
        hinged.LineLoad(released, 0, 1, -6, -6);
        var hingedCase = hinged.Case("Live", LoadCaseType.LinearStatic, LiveOnly);
        var simple = Solve(hinged.Build(), hingedCase);
        Near(U(simple, centre)[2], -5 * 6 * Math.Pow(L, 4) / (384 * Es * Iz), "A beam hinged at its start and split at midspan is the simply supported beam: 5wL⁴/384EI");
        var pieces = simple.Frames.Single().Segments;
        Near(pieces[0].LocalEndActions[5], 0, "… no moment at the released end", 1e-8);
        Near(Math.Abs(pieces[0].LocalEndActions[11]), 6 * L * L / 8, "… and the full wL²/8 through the split, which is not a hinge", 1e-8);
    }

    /// <summary>Internal actions and the deflected line along a frame, between its joints.</summary>
    private static void Stations()
    {
        // a simply supported beam of 10 m under a uniform load, read at eight intervals
        const double span = 10, w = -6, EI = Es * Iz;
        var m = new Model(mesh: 8);
        var left = m.Point(0, 0, 0, new(true, true, true, true, false, false));
        var right = m.Point(span, 0, 0, new(false, true, true, false, false, false));
        m.LineLoad(m.Frame(left, right), 0, 1, w, w);
        var loadCase = m.Case("Live", LoadCaseType.LinearStatic, LiveOnly);
        var stations = Solve(m.Build(), loadCase).Frames.Single().Segments.Single().Stations;
        Assert(stations.Length == 9 && stations[0].Relative == 0 && stations[8].Relative == 1 && stations[3].Relative == .375,
            "A frame is read at as many equal intervals as the model's frame mesh setting, both ends included");
        foreach (var station in stations)
        {
            double x = station.Relative * span;
            Near(station.LocalDisplacements[1], w * x * (Math.Pow(span, 3) - 2 * span * x * x + Math.Pow(x, 3)) / (24 * EI), "Deflected line of a simply supported beam under a uniform load", 1e-14, 1e-10);
            Near(station.LocalDisplacements[5], w * (Math.Pow(span, 3) - 6 * span * x * x + 4 * Math.Pow(x, 3)) / (24 * EI), "… its slope", 1e-14, 1e-10);
            Near(station.Actions[5], w * x * (x - span) / 2, "… its bending moment wx(L − x)/2", 1e-9, 1e-10);
            Near(station.Actions[1], w * (span / 2 - x), "… its shear force w(L/2 − x)", 1e-9, 1e-10);
            Near(station.Actions[0], 0, "… and no axial force", 1e-9);
        }

        // Every kind of load in both bending planes, on a rolled section: the stations of one frame against the joints
        // of the same frame with points put where the stations are. The second model computes those values as joint
        // results and end actions, which do not use the station code.
        (StaticResult Result, ObjectId Frame, ObjectId[] Points) Loaded(bool refined)
        {
            var c = new Model(mesh: refined ? 1 : 4);
            var root = c.Point(0, 0, 0, Restraint.Fixed);
            var tip = c.Point(4, 0, 0);
            var between = refined ? new[] { c.Point(1, 0, 0), c.Point(2, 0, 0), c.Point(3, 0, 0) } : [];
            var frame = c.Frame(root, tip);
            c.Editor.SetFrameLocalAxis([frame], 30);
            c.LineLoad(frame, .2, .9, -2, -7);
            c.MemberLoad(frame, .3, -9);
            c.Editor.PutLoad(new FramePointLoad(Id(39, 1), Live, frame, LoadDirection.GlobalY, .6, 5));
            c.Editor.PutLoad(new FramePointLoad(Id(39, 2), Live, frame, LoadDirection.Local1, .6, 12));
            c.Editor.PutLoad(new FrameDistributedLoad(Id(39, 3), Live, frame, LoadDirection.Local3, .1, .7, 1, 3));
            c.Editor.PutLoad(new PointLoad(Id(39, 4), Live, tip, new(4, -3, 2), new(6, -1, 2)));
            var id = c.Case("Live", LoadCaseType.LinearStatic, LiveOnly);
            return (Solve(c.Build(), id), frame, [root, .. between, tip]);
        }
        var (whole, refinedModel) = (Loaded(false), Loaded(true));
        var frameResult = whole.Result.Frames.Single();
        var read = frameResult.Segments.Single().Stations;
        var pieces = refinedModel.Result.Frames.Single().Segments;
        Xyz Global(ImmutableArray<double> local, int at) => frameResult.Axis1 * local[at] + frameResult.Axis2 * local[at + 1] + frameResult.Axis3 * local[at + 2];
        for (int i = 0; i < 5; i++)
        {
            var joint = U(refinedModel.Result, refinedModel.Points[i]);
            Near(Global(read[i].LocalDisplacements, 0).DistanceTo(new(joint[0], joint[1], joint[2])), 0, "A station's displacement is that of a joint put at the same place", 1e-13);
            Near(Global(read[i].LocalDisplacements, 3).DistanceTo(new(joint[3], joint[4], joint[5])), 0, "… and so is its rotation", 1e-13);
            for (int c = 0; c < 6; c++)
                Near(read[i].Actions[c], i == 0 ? -pieces[0].LocalEndActions[c] : pieces[i - 1].LocalEndActions[6 + c],
                    "A station's actions are the end actions of the frame cut there", 1e-9, 1e-10);
        }
        Assert(read.Any(station => Math.Abs(station.Actions[3]) > 1) && read.Any(station => Math.Abs(station.Actions[4]) > 1) && read.Any(station => Math.Abs(station.Actions[2]) > 1),
            "… with torsion and bending about both axes present in the comparison");
        for (int c = 0; c < 6; c++)
            Near(U(whole.Result, whole.Points[^1])[c], U(refinedModel.Result, refinedModel.Points[^1])[c], "The number of stations changes no joint result", 1e-14, 1e-11);

        // a point load exactly at a station is beyond that section; at the end of the frame the section is the end itself
        var p = new Model(mesh: 4);
        var fixedEnd = p.Point(0, 0, 0, Restraint.Fixed);
        var freeEnd = p.Point(4, 0, 0);
        var arm = p.Frame(fixedEnd, freeEnd);
        p.MemberLoad(arm, .5, -8);
        p.MemberLoad(arm, 1, -3);
        var pointCase = p.Case("Live", LoadCaseType.LinearStatic, LiveOnly);
        var shear = Solve(p.Build(), pointCase).Frames.Single().Segments.Single().Stations.Select(station => station.Actions[1]).ToArray();
        Near(shear[1], -11, "Before a point load the shear carries it", 1e-9);
        Near(shear[2], -11, "… at the load, still: the load is beyond the section", 1e-9);
        Near(shear[3], -3, "… and after it, no longer", 1e-9);
        Near(shear[4], 0, "… and at the free end, past the load applied there, nothing", 1e-9);

        // a frame split at a point: stations run along the whole frame; a released end keeps its own rotation
        var h = new Model(mesh: 2);
        var start = h.Point(0, 0, 0, Restraint.Fixed);
        var end = h.Point(4, 0, 0, new(false, true, true, false, false, false));
        h.Point(2, 0, 0);
        h.LineLoad(h.Frame(start, end, new(new(false, false, false, false, false, true), EndRelease.None)), 0, 1, -6, -6);
        var hingedCase = h.Case("Live", LoadCaseType.LinearStatic, LiveOnly);
        var hinged = Solve(h.Build(), hingedCase).Frames.Single().Segments;
        Assert(hinged.Length == 2 && hinged[0].Stations.Select(station => station.Relative).SequenceEqual(new[] { 0, .25, .5 }) &&
               hinged[1].Stations.Select(station => station.Relative).SequenceEqual(new[] { .5, .75, 1 }),
            "The stations of a split frame are placed along the whole frame, piece by piece");
        foreach (var station in hinged.SelectMany(piece => piece.Stations))
        {
            double x = station.Relative * 4;
            Near(station.LocalDisplacements[1], -6 * x * (64 - 8 * x * x + Math.Pow(x, 3)) / (24 * EI), "A frame hinged at its start deflects as the simply supported beam, from the hinge's own rotation", 1e-14, 1e-10);
            Near(station.Actions[5], -6 * x * (x - 4) / 2, "… with the simply supported beam's moments", 1e-9, 1e-10);
        }
        Near(hinged[0].Stations[0].LocalDisplacements[5], -6 * 64 / (24 * EI), "… the released end's rotation is the member's, wL³/24EI, not the fixed joint's", 1e-14, 1e-10);
    }

    private static void NonlinearFrameCases()
    {
        var m = new Model();
        var root = m.Point(0, 0, 0, Restraint.Fixed);
        var tip = m.Point(4, 0, 0);
        m.Frame(root, tip);
        m.Load(tip, 3, -4, -100);
        m.Load(tip, 0, 2, 10, Wind);
        var linear = m.Case("Linear", LoadCaseType.LinearStatic, LiveOnly);
        var nonlinear = m.Case("Nonlinear", LoadCaseType.NonlinearStatic, LiveOnly);
        var continued = m.Case("Continued", LoadCaseType.NonlinearStatic, [(Live, .5), (Wind, 2)], nonlinear);
        var windOnly = m.Case("Wind", LoadCaseType.LinearStatic, [(Wind, 1)]);
        var document = m.Build();
        var (first, second, third, wind) = (Solve(document, linear), Solve(document, nonlinear), Solve(document, continued), Solve(document, windOnly));
        for (int c = 0; c < 6; c++)
        {
            Near(U(second, tip)[c], U(first, tip)[c], "A nonlinear static case on frames alone is the linear result", 0, 0);
            Near(U(third, tip)[c], 1.5 * U(first, tip)[c] + 2 * U(wind, tip)[c], "A case continuing from another carries its loads: 1 + 0.5 of one pattern and 2 of the other", 1e-14, 1e-11);
        }
        Assert(first.Diagnostics.NonlinearIterations == 0 && first.Cables.IsEmpty && first.Diagnostics.CableFit is null, "A model without cables is solved in one linear step");
    }

    private static void HangingCable()
    {
        // A weight on a vertical cable. The cable holds its end along its own line only.
        (ModelDocument Document, ObjectId Top, ObjectId Node, ObjectId Cable, ObjectId Nonlinear, ObjectId Linear) Hanging(double z,
            DofMask cableDofs = DofMask.Translations, Restraint? node = null, double x = 0, Restraint? anchor = null)
        {
            var m = new Model(cableDofs: cableDofs);
            var top = m.Point(0, 0, 5, anchor ?? Restraint.Pinned);
            var joint = m.Point(0, 0, 0, node);
            var cable = m.Cable(joint, top);
            m.Load(joint, x, 0, z);
            var nonlinear = m.Case("Hang", LoadCaseType.NonlinearStatic, LiveOnly);
            var linear = m.Case("Linear", LoadCaseType.LinearStatic, LiveOnly);
            return (m.Build(), top, joint, cable, nonlinear, linear);
        }
        void Hangs(string how, (ModelDocument Document, ObjectId Top, ObjectId Node, ObjectId Cable, ObjectId Nonlinear, ObjectId Linear) model, int equations)
        {
            var outcome = Run(model.Document, model.Nonlinear);
            Assert(outcome.Success, how + ": a weight hanging on a cable is solved: " + string.Join("; ", outcome.Issues.Select(issue => issue.Message)));
            var result = outcome.Result!;
            Near(CableOf(result, model.Cable).Tension, 100, how + ": a weight on a cable: its tension is the weight", 1e-7);
            Near(U(result, model.Node)[2], -100 * 5 / CableEA, "… and it stretches by PL/EA", 1e-12);
            Near(Reaction(result, model.Top)[2], 100, "… carried by the anchor", 1e-7);
            Assert(CableOf(result, model.Cable) is { Active: true, Sag: null, TargetForce: null, AppliedPretension: 0 }, "A weightless cable is straight and has no pretension of its own");
            Assert(result.Diagnostics.NonlinearIterations >= 2 && result.Diagnostics.SolvedDofCount == equations && U(result, model.Node)[0] == 0 && U(result, model.Node)[1] == 0,
                "… by iteration on the one equation that has stiffness");
        }
        void Unstable(string how, ModelDocument document, ObjectId caseId, string mentions)
        {
            var outcome = Run(document, caseId);
            Assert(outcome.Result is null && outcome.Issues.Any(issue => issue.Code == "static.matrix.unstable" && issue.Severity == ValidationSeverity.Error &&
                       issue.Message.Contains("unstable", StringComparison.Ordinal) && issue.Message.Contains(mentions, StringComparison.Ordinal)),
                how + ": reported as unstable, with no result: " + string.Join("; ", outcome.Issues.Select(issue => issue.Message)));
        }

        // With the translations of a cable joint all on, nothing holds the weight across the cable's line: unstable.
        var free = Hanging(-100);
        Unstable("Cable joints with UX, UY, UZ and nothing across the cable's line", free.Document, free.Nonlinear, "has no stiffness and no restraint");
        Assert(Run(free.Document, free.Nonlinear).Issues.Count(issue => issue.Code == "static.matrix.unstable") == 2, "… naming both movements that nothing holds, UX and UY");
        // The user restrains those movements …
        Hangs("Restrained across the cable", Hanging(-100, node: new(true, true, false, false, false, false)), 1);
        // … or switches them off for cable joints.
        var vertical = Hanging(-100, DofMask.UZ);
        Hangs("Cable joints with UZ only", vertical, 1);
        Assert(ModelCompiler.Compile(vertical.Document).Model!.DofIndex[new(vertical.Node, DofComponent.UX)] is { State: DofState.Inactive, InactiveReason: InactiveDofReason.ElementUnused },
            "A movement switched off for cable joints is inactive at a joint only cables reach");
        // Rotations switched on for cable joints are movements too, at the hanging joint and at the anchor alike:
        // nothing holds them unless they are restrained.
        var rotations = DofMask.UZ | DofMask.RX | DofMask.RY | DofMask.RZ;
        var turning = Hanging(-100, rotations);
        Unstable("Cable joints with rotations and no restraint", turning.Document, turning.Nonlinear, "RX");
        var anchored = Hanging(-100, rotations, new(false, false, false, true, true, true));
        Unstable("Cable joints with rotations, restrained at the hanging joint only", anchored.Document, anchored.Nonlinear, "'P1' RX");
        Hangs("Cable joints with rotations, restrained at both joints", Hanging(-100, rotations, new(false, false, false, true, true, true), anchor: Restraint.Fixed), 1);

        Refuse(vertical.Document, vertical.Linear, "static.cable.linearCase");
        var up = Hanging(100, DofMask.UZ);
        Refuse(up.Document, up.Nonlinear, "static.nonlinear.noConvergence");
        // a load across the cable, where nothing holds it
        var sideways = Hanging(-100, x: 1);
        Unstable("A load across a cable's line", sideways.Document, sideways.Nonlinear, "UX");

        // a joint between two cables that both go slack has nothing holding it in that case
        var s = new Model(cableDofs: DofMask.UZ);
        var root = s.Point(-4, 0, 0, Restraint.Fixed);
        var tip = s.Point(0, 0, 0);
        var link = s.Point(0, 0, 3);
        var anchor = s.Point(0, 0, 6, Restraint.Pinned);
        s.Frame(root, tip);
        s.Cable(tip, link);
        s.Cable(link, anchor);
        s.Load(tip, 0, 0, -50);
        s.Load(tip, 0, 0, 50, Wind);
        var pulled = s.Case("Down", LoadCaseType.NonlinearStatic, LiveOnly);
        var lifted = s.Case("Up", LoadCaseType.NonlinearStatic, [(Wind, 1)]);
        var chain = s.Build();
        Assert(Solve(chain, pulled).Cables.All(cable => cable.Active), "Two cables in line, pulled: both taut, and the joint between them is held");
        Unstable("Two cables in line, both slack", chain, lifted, "held by slack cables only");

        // a cable that pulls in a movement switched off at its joint is held there as by a support, and that is said
        var held = new Model(cableDofs: DofMask.UZ);
        var above = held.Point(3, 0, 4, Restraint.Pinned);
        var below = held.Point(0, 0, 0);
        var inclined = held.Cable(below, above);
        held.Load(below, 0, 0, -80);
        var heldCase = held.Case("Hang", LoadCaseType.NonlinearStatic, LiveOnly);
        var heldDocument = held.Build();
        Assert(ModelCompiler.Compile(heldDocument).Issues.Any(issue => issue.Code == "dof.cable.held" && issue.Severity == ValidationSeverity.Warning && issue.Message.Contains("UX", StringComparison.Ordinal)),
            "A cable pulling in a movement that is switched off at its joint: the compiler says the joint is held there as by a support");
        var heldResult = Solve(heldDocument, heldCase);
        Near(CableOf(heldResult, inclined).Tension, 80 / .8, "… the cable carries the load through its vertical component, T = P / sin θ", 1e-7);
        Near(heldResult.Joints.Single(joint => joint.PointId == below).SuppressedDofForces[0], -60, "… and the force that holds the joint sideways is reported, apart from the reactions", 1e-7);
        Assert(heldResult.Joints.Single(joint => joint.PointId == below).Reactions.All(value => value == 0), "… it is not a support reaction");
    }

    private sealed record TipCable(ModelDocument Document, ObjectId Root, ObjectId Tip, ObjectId Anchor, ObjectId Cable,
        ObjectId Down, ObjectId Up, ObjectId Pretension, ObjectId Service, ObjectId Uplift, ObjectId Doubled);

    /// <summary>A cantilever of 4 m along X with a cable from its tip up to an anchor 3 m above. Live: 100 kN down on the
    /// tip. Wind: 80 kN up. PT: a 50 kN target force in the cable.</summary>
    private static TipCable BuildTipCable(DofMask mask = DofMask.Full3D, double inertia22 = Iy, Func<Xyz, Xyz>? place = null, double fitTolerancePercent = 1e-6, bool target = true)
    {
        var m = new Model(mask, fitTolerancePercent: fitTolerancePercent, inertia22: inertia22, place: place);
        var root = m.Point(0, 0, 0, Restraint.Fixed);
        var tip = m.Point(4, 0, 0);
        var anchor = m.Point(4, 0, 3, Restraint.Pinned);
        m.Frame(root, tip);
        var cable = m.Cable(tip, anchor, target ? 50 : null);
        m.Load(tip, 0, 0, -100);
        m.Load(tip, 0, 0, 80, Wind);
        var down = m.Case("Down", LoadCaseType.NonlinearStatic, LiveOnly);
        var up = m.Case("Up", LoadCaseType.NonlinearStatic, [(Wind, 1)]);
        var pretension = m.Case("Pretension", LoadCaseType.NonlinearStatic, [(Prestress, 1)], fit: true);
        var service = m.Case("Service", LoadCaseType.NonlinearStatic, LiveOnly, pretension);
        var uplift = m.Case("Uplift", LoadCaseType.NonlinearStatic, [(Wind, 1)], pretension);
        var doubled = m.Case("Doubled", LoadCaseType.NonlinearStatic, [(Prestress, 2)], fit: true);
        return new(m.Build(), root, tip, anchor, cable, down, up, pretension, service, uplift, doubled);
    }

    private static void CableOnCantilever()
    {
        var s = BuildTipCable(target: false);
        double cableStiffness = CableEA / 3;
        var down = Solve(s.Document, s.Down);
        double deflection = -100 / (TipStiffness + cableStiffness);
        Near(U(down, s.Tip)[2], deflection, "A cable above a loaded cantilever tip shares the load by stiffness: P/(3EI/L³ + EA/L)", 1e-13);
        Near(CableOf(down, s.Cable).Tension, -cableStiffness * deflection, "… its tension is its stiffness times its extension", 1e-7);
        Near(Reaction(down, s.Anchor)[2], -cableStiffness * deflection, "… held by the anchor", 1e-7);
        Near(Reaction(down, s.Root)[2], 100 + cableStiffness * deflection, "… and the cantilever carries the rest", 1e-7);
        var up = Solve(s.Document, s.Up);
        Near(U(up, s.Tip)[2], 80 / TipStiffness, "Pushed towards its anchor the cable goes slack: the cantilever alone, PL³/3EI", 1e-13);
        Assert(CableOf(up, s.Cable) is { Tension: 0, Active: false } && Reaction(up, s.Anchor).All(value => value == 0), "A slack cable carries nothing and its anchor has no reaction");
        Near(Reaction(up, s.Root)[2], -80, "… the whole load goes to the cantilever's support", 1e-8);
    }

    private static void TargetForcesAndStages()
    {
        var s = BuildTipCable();
        double cableStiffness = CableEA / 3, lackOfFit = 50 * (TipStiffness + cableStiffness) / TipStiffness;
        var pretension = Solve(s.Document, s.Pretension);
        var stressed = CableOf(pretension, s.Cable);
        Near(stressed.Tension, 50, "The fit case ends with the cable at its target force", 1e-6);
        Near(stressed.AppliedPretension, lackOfFit, "… reached by the pretension that the cantilever's give requires: T(k_b + k_c)/k_b", 1e-6, 1e-8);
        Near(U(pretension, s.Tip)[2], 50 / TipStiffness, "… which lifts the tip by T/k_b", 1e-11);
        Assert(stressed is { TargetForce: 50, Active: true } && pretension.Diagnostics.CableFit is { Iterations: 2 } fit && fit.FitCaseId == s.Pretension && fit.WorstDeviationPercent <= 1e-6,
            "The fit is reported: its case, its iterations and how close it came");
        Near(stressed.FitStageTension!.Value, 50, "… with the tension at the end of the fit stage", 1e-6);

        var service = Solve(s.Document, s.Service);
        Near(CableOf(service, s.Cable).Tension, 50 + 100 * cableStiffness / (TipStiffness + cableStiffness), "A case continuing from the fit case keeps the pretension: target plus the cable's share of the new load", 1e-6);
        Near(U(service, s.Tip)[2], (lackOfFit - 100) / (TipStiffness + cableStiffness), "… tip displacement (T0 − P)/(k_b + k_c)", 1e-11);
        Near(CableOf(service, s.Cable).AppliedPretension, lackOfFit, "… with the same applied pretension", 1e-6, 1e-8);
        Near(CableOf(service, s.Cable).FitStageTension!.Value, 50, "… and the fit stage still reported", 1e-6);

        var uplift = Solve(s.Document, s.Uplift);
        Assert(CableOf(uplift, s.Cable) is { Tension: 0, Active: false }, "An uplift larger than the pretension can hold slackens the cable");
        Near(U(uplift, s.Tip)[2], 80 / TipStiffness, "… leaving the cantilever alone under the uplift", 1e-12);

        var doubled = Solve(s.Document, s.Doubled);
        Near(CableOf(doubled, s.Cable).Tension, 100, "A target-force pattern applied with a factor of 2 targets twice the force", 1e-6);
        Assert(CableOf(doubled, s.Cable).TargetForce == 100, "… and reports that target");

        var loose = BuildTipCable(fitTolerancePercent: 10);
        var withinTen = Solve(loose.Document, loose.Pretension);
        Assert(Math.Abs(CableOf(withinTen, loose.Cable).Tension - 50) / 50 <= .10 && withinTen.Diagnostics.CableFit!.WorstDeviationPercent <= 10,
            "With the model's default 10% tolerance the fit stops inside that tolerance");
    }

    private static void InteractingAndSeriesCables()
    {
        // two cables on one simply supported beam of 12 m, at its third points: tensioning one changes the other
        var m = new Model();
        var left = m.Point(0, 0, 0, new(true, true, true, true, false, false));
        var right = m.Point(12, 0, 0, new(false, true, true, false, false, false));
        var (first, second) = (m.Point(4, 0, 0), m.Point(8, 0, 0));
        m.Frame(left, right);
        var one = m.Cable(first, m.Point(4, 0, 3, Restraint.Pinned), 40);
        var two = m.Cable(second, m.Point(8, 0, 3, Restraint.Pinned), 70);
        var fit = m.Case("Pretension", LoadCaseType.NonlinearStatic, [(Prestress, 1)], fit: true);
        var result = Solve(m.Build(), fit);
        // deflection at x of a simply supported beam under a unit load at a (x ≤ a): x·b·(L² − x² − b²)/(6·L·EI), b = L − a
        double Influence(double x, double a) => x * (12 - a) * (144 - x * x - (12 - a) * (12 - a)) / (6 * 12 * Es * Iz);
        double own = Influence(4, 4), other = Influence(4, 8), cableStiffness = CableEA / 3;
        double liftOne = own * 40 + other * 70, liftTwo = other * 40 + own * 70;
        Near(CableOf(result, one).Tension, 40, "Two interacting cables both end at their targets (first)", 1e-5);
        Near(CableOf(result, two).Tension, 70, "… (second)", 1e-5);
        Near(U(result, first)[2], liftOne, "… the beam lifted by the two tensions through its influence coefficients", 1e-10, 1e-7);
        Near(U(result, second)[2], liftTwo, "… (second point)", 1e-10, 1e-7);
        Near(CableOf(result, one).AppliedPretension, 40 + cableStiffness * liftOne, "… each pretension is its target plus what the beam's lift takes away", 1e-5, 1e-7);
        Near(CableOf(result, two).AppliedPretension, 70 + cableStiffness * liftTwo, "… (second)", 1e-5, 1e-7);
        Near(ReactionSum(result).Length, 0, "… and with no applied load the reactions balance each other", 1e-7);

        // two cables in line share one tension: only the sum of their pretensions is determined
        var s = new Model(cableDofs: DofMask.UZ);
        var root = s.Point(-4, 0, 0, Restraint.Fixed);
        var tip = s.Point(0, 0, 0);
        var link = s.Point(0, 0, 3);
        var top = s.Point(0, 0, 6, Restraint.Pinned);
        s.Frame(root, tip);
        var lower = s.Cable(tip, link, 50);
        var upper = s.Cable(link, top, 50);
        var seriesCase = s.Case("Pretension", LoadCaseType.NonlinearStatic, [(Prestress, 1)], fit: true);
        var series = Solve(s.Build(), seriesCase);
        double expected = 50 * (1 + cableStiffness / 2 / TipStiffness);
        Near(CableOf(series, lower).Tension, 50, "Cables in series reach their common target (lower)", 1e-5);
        Near(CableOf(series, upper).Tension, 50, "… (upper)", 1e-5);
        Near(CableOf(series, lower).AppliedPretension, expected, "… with the pretension split evenly between them: T(1 + k_series/k_b) each", 1e-4, 1e-7);
        Near(CableOf(series, upper).AppliedPretension, expected, "… (upper)", 1e-4, 1e-7);
        Near(U(series, link)[2], (U(series, tip)[2] + 0) / 2, "… and the link between them moves half as far as the tip", 1e-12);
    }

    private static void RigidRotationAndPlanePreset()
    {
        // an isotropic section, so that the default transverse axes may turn with the member without changing it
        var upright = BuildTipCable(inertia22: Iz);
        Xyz axis = new Xyz(1, 2, 3) * (1 / Math.Sqrt(14));
        const double angle = .73;
        Xyz Rotate(Xyz v) => v * Math.Cos(angle) + axis.Cross(v) * Math.Sin(angle) + axis * (axis.Dot(v) * (1 - Math.Cos(angle)));
        var turned = BuildTipCable(inertia22: Iz, place: v => Rotate(v) + new Xyz(7, -3, 2));
        foreach (var pick in new Func<TipCable, ObjectId>[] { s => s.Down, s => s.Up, s => s.Pretension, s => s.Service, s => s.Uplift })
        {
            var (a, b) = (Solve(upright.Document, pick(upright)), Solve(turned.Document, pick(turned)));
            Near(CableOf(b, turned.Cable).Tension, CableOf(a, upright.Cable).Tension, "Turning and moving the whole structure in space leaves the cable tension unchanged", 1e-6, 1e-9);
            Near(CableOf(b, turned.Cable).AppliedPretension, CableOf(a, upright.Cable).AppliedPretension, "… and the fitted pretension", 1e-6, 1e-9);
            Near(Translation(b, turned.Tip).DistanceTo(Rotate(Translation(a, upright.Tip))), 0, "… and turns the displacement with it", 1e-11);
            Near(CableOf(b, turned.Cable).Axis1.DistanceTo(Rotate(CableOf(a, upright.Cable).Axis1)), 0, "… as it turns the cable's axis", 1e-12);
        }

        var spatial = BuildTipCable();
        var planar = BuildTipCable(DofMask.XZ);
        foreach (var pick in new Func<TipCable, ObjectId>[] { s => s.Down, s => s.Up, s => s.Service })
        {
            var (a, b) = (Solve(spatial.Document, pick(spatial)), Solve(planar.Document, pick(planar)));
            Near(CableOf(b, planar.Cable).Tension, CableOf(a, spatial.Cable).Tension, "A structure lying in XZ gives the same cable force with the XZ preset as in full 3D", 1e-7, 1e-9);
            foreach (int c in new[] { 0, 2, 4 }) Near(U(b, planar.Tip)[c], U(a, spatial.Tip)[c], "… and the same in-plane displacements", 1e-13, 1e-9);
        }
    }

    private static void CableSag()
    {
        (ModelDocument Document, ObjectId Left, ObjectId Right, ObjectId Cable, ObjectId Case) Span(Xyz end, bool sag, double weightModifier = 1)
        {
            var m = new Model(sag: sag);
            var left = m.Point(0, 0, 0, Restraint.Pinned);
            var right = m.Point(end.X, end.Y, end.Z, Restraint.Pinned);
            var cable = m.Cable(left, right);
            if (weightModifier != 1) m.Editor.SetCableModifiers([cable], new(1, 1, weightModifier));
            var loadCase = m.Case("Dead", LoadCaseType.NonlinearStatic, [(Dead, 1)]);
            return (m.Build(), left, right, cable, loadCase);
        }
        // a level cable of 50 m between fixed anchors, cut to the chord length, under its own weight
        const double span = 50;
        var level = Span(new(span, 0, 0), sag: true);
        var hanging = Solve(level.Document, level.Case);
        var cable = CableOf(hanging, level.Cable);
        double stiffness = CableEA / span, sagParameter = CableWeight * CableWeight * Math.Pow(span, 3) / 24;
        Near(cable.Tension, Math.Cbrt(stiffness * sagParameter), "A cable cut to its chord and hung by its weight: T³ = EA·w²L²/24", 1e-8, 1e-10);
        Near(cable.Sag!.Value, CableWeight * span * span / (8 * cable.Tension), "… its sag is wL²/8T", 1e-12);
        Near(cable.TangentStiffnessRatio!.Value, 1.0 / 3, "… and there Ernst's equivalent stiffness is exactly a third of EA/L", 1e-9);
        Near(Reaction(hanging, level.Left)[2] + Reaction(hanging, level.Right)[2], CableWeight * span, "… the anchors carry its weight", 1e-9);
        Near(Reaction(hanging, level.Left)[0], -cable.Tension, "… and its pull, at one end", 1e-8);
        Near(Reaction(hanging, level.Right)[0], cable.Tension, "… and at the other", 1e-8);
        // the exact elastic catenary of the same cable (Irvine 1981): span = H·L0/EA + (2H/w)·asinh(w·L0/2H)
        double catenary = Bisect(h => h * span / CableEA + 2 * h / CableWeight * Math.Asinh(CableWeight * span / (2 * h)) - span, 1, 1e4);
        Assert(Math.Abs(cable.Tension - catenary) / catenary < 2e-3 && cable.Sag.Value / span < .02,
            $"The shallow-parabola tension is within 0.2% of the exact elastic catenary at a sag of {cable.Sag.Value / span:P2} of the span: {cable.Tension:G8} vs {catenary:G8} kN");

        var heavy = Span(new(span, 0, 0), sag: true, weightModifier: 3);
        Near(CableOf(Solve(heavy.Document, heavy.Case), heavy.Cable).Tension, Math.Cbrt(stiffness * sagParameter * 9), "The weight modifier scales the weight that makes the cable sag", 1e-8, 1e-10);

        var inclined = Span(new(40, 0, 30), sag: true);
        var sloping = CableOf(Solve(inclined.Document, inclined.Case), inclined.Cable);
        Near(sloping.Tension, Math.Cbrt(stiffness * sagParameter * .64), "On a slope only the weight normal to the chord makes the cable sag: w·cos θ", 1e-8, 1e-10);
        Near(sloping.Sag!.Value, .8 * CableWeight * span * span / (8 * sloping.Tension), "… and so its sag", 1e-12);

        var straight = Span(new(span, 0, 0), sag: false);
        var taut = Solve(straight.Document, straight.Case);
        Assert(CableOf(taut, straight.Cable) is { Tension: 0, Active: false, Sag: null, TangentStiffnessRatio: null }, "With cable sag switched off the same cable is a straight bar at its unstressed length");
        Near(Reaction(taut, straight.Left)[2] + Reaction(taut, straight.Right)[2], CableWeight * span, "… whose weight still reaches the anchors", 1e-9);

        // a sagging cable inside a structure: the cable's own equation, re-evaluated here from the result
        var m = new Model(sag: true, fitTolerancePercent: 1e-4);
        var root = m.Point(0, 0, 0, Restraint.Fixed);
        var tip = m.Point(4, 0, 0);
        var anchor = m.Point(28, 0, 10, Restraint.Pinned);
        m.Frame(root, tip);
        var stay = m.Cable(tip, anchor, 120);
        m.Load(tip, 0, 0, -60);
        var fit = m.Case("Pretension", LoadCaseType.NonlinearStatic, [(Prestress, 1), (Dead, 1)], fit: true);
        var service = m.Case("Service", LoadCaseType.NonlinearStatic, LiveOnly, fit);
        var document = m.Build();
        var stage = Solve(document, fit);
        Near(CableOf(stage, stay).Tension, 120, "A sagging cable is fitted to its target under the self-weight", 2e-4);
        var loaded = Solve(document, service);
        var result = CableOf(loaded, stay);
        double length = 26, k = CableEA / length, normal = CableWeight * 24 / 26, a = normal * normal * Math.Pow(length, 3) / 24;
        double extension = result.Axis1.Dot(Translation(loaded, anchor) - Translation(loaded, tip));
        Near(result.Tension / k - a / (result.Tension * result.Tension), result.AppliedPretension / k + extension, "A sagging cable in a structure satisfies T/k − a/T² = T0/k + e at the solved displacements", 1e-10, 1e-9);
        Near(result.TangentStiffnessRatio!.Value, 1 / (1 + 2 * k * a / Math.Pow(result.Tension, 3)), "… with Ernst's stiffness at that tension", 1e-12);
        Near(result.Sag!.Value, normal * length * length / (8 * result.Tension), "… and its sag", 1e-12);
        var total = ReactionSum(loaded);
        Near(total.Z, 60 + CableWeight * length, "… and the supports carry the load and the cable's weight", 1e-7);
        Near(Math.Sqrt(total.X * total.X + total.Y * total.Y), 0, "… with no net horizontal reaction", 1e-7);
        Assert(loaded.Diagnostics.MinimumScaledPivot > 0 && loaded.Diagnostics.ScaledResidual <= 1e-9,
            "The equilibrium found is stable and inside the force tolerance");
    }

    private static void Refusals()
    {
        var s = BuildTipCable();
        var edit = s.Document.Edit();
        var (unfitted, fitWithout, nested, inner) = (Id(36, 1), Id(36, 2), Id(36, 3), Id(36, 4));
        edit.PutLoadCase(new(unfitted, "Unfitted", LoadCaseType.NonlinearStatic, [new(Prestress, 1)]));
        edit.PutLoadCase(new(fitWithout, "No targets", LoadCaseType.NonlinearStatic, [new(Live, 1)]) { FitCableTargetForces = true });
        edit.PutLoadCase(new(inner, "Second fit", LoadCaseType.NonlinearStatic, [new(Prestress, 1)]) { FitCableTargetForces = true, InitialCaseId = s.Pretension });
        edit.PutLoadCase(new(nested, "After two fits", LoadCaseType.NonlinearStatic, [new(Live, 1)]) { InitialCaseId = inner });
        var negative = Id(36, 5);
        edit.PutLoadCase(new(negative, "Negative", LoadCaseType.NonlinearStatic, [new(Prestress, -1)]) { FitCableTargetForces = true });
        var document = edit.Commit().Document;
        Refuse(document, unfitted, "static.targetForce.unfitted");
        Refuse(document, fitWithout, "static.fit.noTargets");
        Refuse(document, nested, "static.fit.nested");
        Refuse(document, negative, "static.fit.target");

        var twice = s.Document.Edit();
        twice.PutLoad(new CableTargetForce(Id(36, 6), Prestress, s.Cable, 30));
        Refuse(twice.Commit().Document, s.Pretension, "static.fit.duplicateTarget");

        var soft = s.Document.Edit();
        soft.SetCableModifiers([s.Cable], new(0, 1, 1));
        Refuse(soft.Commit().Document, s.Down, "static.cable.stiffness");

        // a node on a roller between two cables in line: they share one tension, so two different targets cannot both be met
        var m = new Model();
        var a = m.Point(0, 0, 0, Restraint.Pinned);
        var b = m.Point(5, 0, 0, new(false, true, true, false, false, false));
        var c = m.Point(10, 0, 0, Restraint.Pinned);
        m.Cable(a, b, 50);
        m.Cable(b, c, 100);
        var impossible = m.Case("Pretension", LoadCaseType.NonlinearStatic, [(Prestress, 1)], fit: true);
        var unreachable = m.Build();
        Refuse(unreachable, impossible, "static.fit.notConverged");
        // … unless the closest state is asked for: then it is returned, and marked as not fitted
        var closest = StaticSolver.Solve(ModelCompiler.Compile(unreachable).Model!, impossible, new(AcceptClosestFit: true));
        Assert(closest.Result is { } nearest && nearest.Diagnostics.CableFit is { Converged: false, Iterations: 100, Failure: { } why } &&
               why.StartsWith("Not fitted within", StringComparison.Ordinal) &&
               closest.Issues.Any(issue => issue.Code == "static.fit.closest" && issue.Severity == ValidationSeverity.Warning) &&
               nearest.Cables.All(cable => Math.Abs(cable.Tension - 75) < 1e-6 && cable.FitHistory.Length == 100),
            "Asked to accept the closest fit, the solver returns it marked as not fitted, with the reason and every fitting run");

        // several cases at once are each what they are alone, with one fit between them
        var compiled = ModelCompiler.Compile(s.Document).Model!;
        var together = StaticSolver.Solve(compiled, [s.Pretension, s.Service, s.Uplift, s.Down]);
        foreach (var id in new[] { s.Pretension, s.Service, s.Uplift, s.Down })
        {
            var (alone, shared) = (StaticSolver.Solve(compiled, id).Result!, together[id].Result!);
            Assert(shared.Cables.Single().Tension == alone.Cables.Single().Tension && shared.Cables.Single().AppliedPretension == alone.Cables.Single().AppliedPretension &&
                   U(shared, s.Tip).SequenceEqual(U(alone, s.Tip)) && shared.Diagnostics.CableFit == alone.Diagnostics.CableFit,
                "Cases solved together are the cases solved alone");
        }
        var history = together[s.Service].Result!.Cables.Single().FitHistory;
        Assert(history.Length == 2 && Math.Abs(history[0] - 50 * TipStiffness / (TipStiffness + CableEA / 3)) < 1e-9 && Math.Abs(history[1] - 50) < 1e-6,
            "The fitting runs are kept: the first with the target itself as pretension, the last at the target");

        Throws<OperationCanceledException>(() => StaticSolver.Solve(ModelCompiler.Compile(s.Document).Model!, s.Service, cancellationToken: new(true)),
            "Cancellation never returns a completed result");
        Throws<ArgumentException>(() => StaticSolver.Solve(ModelCompiler.Compile(s.Document).Model!, s.Service, new(NonlinearForceTolerance: 0)),
            "Solver tolerances outside (0, 1) are refused");
    }

    /// <summary>Frames with straight tension-only cables against independent OpenSees models
    /// (verification/cable-static/generate_opensees.py). The reference is given each cable's pretension; the native model
    /// is given the tensions that pretension produced as target forces, and has to find the pretension again.</summary>
    private static void CrossCheckOpenSees()
    {
        using var specFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cable-static-spec.json")));
        using var referenceFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cable-static-opensees.json")));
        var (section, rope) = (specFile.RootElement.GetProperty("section"), specFile.RootElement.GetProperty("cable"));
        Assert(section.GetProperty("E").GetDouble() == Es && section.GetProperty("A").GetDouble() == A && section.GetProperty("Iz").GetDouble() == Iz &&
               section.GetProperty("Iy").GetDouble() == Iy && section.GetProperty("J").GetDouble() == J && section.GetProperty("nu").GetDouble() == .3 &&
               rope.GetProperty("E").GetDouble() == Ec && rope.GetProperty("A").GetDouble() == Ac,
            "The reference models have the frame section and the cable of these checks");
        int compared = 0, solutions = 0, slack = 0, loadNumber = 0;
        double largestDisplacement = 0, largestForce = 0;
        foreach (var item in specFile.RootElement.GetProperty("cases").EnumerateArray())
        {
            string name = item.GetProperty("name").GetString()!;
            var reference = referenceFile.RootElement.GetProperty("cases").EnumerateArray()
                .Single(solved => solved.GetProperty("name").GetString() == name).GetProperty("loads");
            var m = new Model(Enum.Parse<DofMask>(item.GetProperty("mask").GetString()!), fitTolerancePercent: 1e-5);
            var (nodes, fix) = (item.GetProperty("nodes"), item.GetProperty("fix"));
            var points = new List<ObjectId>();
            for (int i = 0; i < nodes.GetArrayLength(); i++)
            {
                bool Fixed(int component) => fix[i][component].GetInt32() != 0;
                points.Add(m.Point(nodes[i][0].GetDouble(), nodes[i][1].GetDouble(), nodes[i][2].GetDouble(),
                    new Restraint(Fixed(0), Fixed(1), Fixed(2), Fixed(3), Fixed(4), Fixed(5))));
            }
            // a frame with nodes lying on it is one native frame: the engine has to connect it there itself
            foreach (var frame in item.GetProperty("frames").EnumerateArray())
                m.Frame(points[frame.GetProperty("i").GetInt32() - 1], points[frame.GetProperty("j").GetInt32() - 1]);
            bool pretensioned = reference.TryGetProperty("stage", out var stage);
            var cables = new List<(ObjectId Id, double Pretension)>();
            foreach (var cable in item.GetProperty("cables").EnumerateArray())
            {
                double? target = null;
                if (pretensioned)
                {
                    target = stage.GetProperty("cables")[cables.Count].GetProperty("tension").GetDouble();
                    Assert(target > 0, name + ": every cable is taut at the pretension stage of the reference, so its pretension is determined");
                }
                cables.Add((m.Cable(points[cable.GetProperty("i").GetInt32() - 1], points[cable.GetProperty("j").GetInt32() - 1], target),
                    cable.GetProperty("pretension").GetDouble()));
            }
            var cases = new List<(string Name, ObjectId Id)>();
            ObjectId? stageCase = pretensioned ? m.Case("stage", LoadCaseType.NonlinearStatic, [(Prestress, 1)], fit: true) : null;
            if (stageCase is { } fitted) cases.Add(("stage", fitted));
            int patternNumber = 0;
            foreach (var loads in item.GetProperty("loads").EnumerateObject().Where(loads => loads.Name != "stage"))
            {
                var pattern = Id(37, ++patternNumber);
                m.Editor.PutLoadPattern(new(pattern, loads.Name, LoadPatternType.Other));
                foreach (var load in loads.Value.EnumerateArray())
                {
                    var v = load.GetProperty("values");
                    m.Editor.PutLoad(new PointLoad(Id(38, ++loadNumber), pattern, points[load.GetProperty("node").GetInt32() - 1],
                        new(v[0].GetDouble(), v[1].GetDouble(), v[2].GetDouble()), new(v[3].GetDouble(), v[4].GetDouble(), v[5].GetDouble())));
                }
                cases.Add((loads.Name, m.Case(loads.Name, LoadCaseType.NonlinearStatic, [(pattern, 1)], stageCase)));
            }
            var document = m.Build();
            foreach (var (loadName, caseId) in cases)
            {
                var result = Solve(document, caseId);
                var expected = reference.GetProperty(loadName);
                string label = $"{name}, {loadName}";
                solutions++;
                foreach (var node in expected.GetProperty("nodes").EnumerateArray())
                {
                    var joint = result.Joints.Single(joint => joint.PointId == points[node.GetProperty("node").GetInt32() - 1]);
                    for (int c = 0; c < 6; c++)
                    {
                        double displacement = node.GetProperty("displacement")[c].GetDouble(), reaction = node.GetProperty("reaction")[c].GetDouble();
                        double held = joint.Reactions[c] + joint.SuppressedDofForces[c];
                        largestDisplacement = Math.Max(largestDisplacement, Math.Abs(joint.Displacements[c] - displacement));
                        largestForce = Math.Max(largestForce, Math.Abs(held - reaction));
                        Near(joint.Displacements[c], displacement, label + ": OpenSees nodal displacement/rotation", 1e-10, 2e-6);
                        Near(held, reaction, label + ": OpenSees support reaction", 1e-6, 2e-6);
                        compared += 2;
                    }
                }
                for (int c = 0; c < cables.Count; c++)
                {
                    double tension = expected.GetProperty("cables")[c].GetProperty("tension").GetDouble();
                    var actual = CableOf(result, cables[c].Id);
                    largestForce = Math.Max(largestForce, Math.Abs(actual.Tension - tension));
                    Near(actual.Tension, tension, label + ": OpenSees cable tension", 1e-6, 2e-6);
                    Assert(actual.Active == tension > 0, label + ": the same cables are slack as in OpenSees");
                    if (tension == 0) slack++;
                    if (pretensioned) Near(actual.AppliedPretension, cables[c].Pretension, label + ": the pretension found from the target forces is the one the reference was given", 1e-5, 2e-6);
                    compared += pretensioned ? 2 : 1;
                }
            }
        }
        Assert(slack >= 3 && solutions == 11, "The reference covers slack cables, and every reference solution was compared");
        Console.WriteLine($"OpenSees cable reference: {solutions} solutions of 4 models, {compared} scalar comparisons, {slack} slack cables; max |delta u/rotation| {largestDisplacement:G6}, max |delta force/moment| {largestForce:G6} (mixed units; see verification report).");
    }
}
