using System.IO;
using System.Reflection;
using Mcotis.Core;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Checks.Archive;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Infrastructure;
using Mcotis.Desktop.Presentation;
using NativePointLoad = Mcotis.Core.PointLoad;
using NativeTargetForce = Mcotis.Core.CableTargetForce;

/// <summary>The application's analysis (<see cref="FemSolver"/>, which runs the general solver
/// <c>Mcotis.Analysis.StaticSolver</c>) against the archived planar solver it replaced, on the same input: a cable truss
/// made for the purpose, and the default model exactly as the application runs it. Every value the application shows
/// of a run is compared — joints, reactions, frame ends, the stations along every frame, cables and the fitting.</summary>
internal static class GeneralSolverParityChecks
{
    public static void Run()
    {
        CableTruss();
        DefaultModel();
        NativeWorkspace();
    }

    private sealed record Differences
    {
        public double Displacement, Rotation, Reaction, FrameForce, CableForce, Pretension, Sag;
        public int Compared;
        public override string ToString() => $"{Compared} values; max |Δ| displacement {Displacement:G3} m, rotation {Rotation:G3} rad, reaction {Reaction:G3} kN, " +
            $"frame action {FrameForce:G3} kN·(m), cable force {CableForce:G3} kN, pretension {Pretension:G3} kN, sag {Sag:G3} m";
    }

    /// <param name="relative">Accepted difference as a fraction of the larger value, on top of small absolute floors.</param>
    /// <param name="fitting">Compare what the fitting did as well: the applied pretension, each fitting run and their number.</param>
    private static void Compare(string name, FemAnalysisResult planar, FemAnalysisResult general, double relative, bool fitting, Differences total)
    {
        void Near(double actual, double expected, double absolute, string what, ref double largest)
        {
            double difference = Math.Abs(actual - expected);
            if (!(difference <= absolute + relative * Math.Max(Math.Abs(actual), Math.Abs(expected))))
                throw new Exception($"FAIL: {name}: {what}: general {actual:G12}, planar {expected:G12}");
            largest = Math.Max(largest, difference);
            total.Compared++;
        }
        void Same(bool condition, string what)
        {
            if (!condition) throw new Exception($"FAIL: {name}: {what}");
        }
        Same(general.Joints.Select(joint => joint.PointName).SequenceEqual(planar.Joints.Select(joint => joint.PointName)) &&
             general.SupportReactions.Select(item => item.PointName).SequenceEqual(planar.SupportReactions.Select(item => item.PointName)) &&
             general.Frames.Select(frame => frame.MemberName).SequenceEqual(planar.Frames.Select(frame => frame.MemberName)) &&
             general.Cables.Select(cable => cable.MemberName).SequenceEqual(planar.Cables.Select(cable => cable.MemberName)),
            "the same joints, supports, frames and cables in the same order");
        foreach (var (now, old) in general.Joints.Zip(planar.Joints))
        {
            Near(now.UXM, old.UXM, 1e-9, $"UX of {old.PointName}", ref total.Displacement);
            Near(now.UZM, old.UZM, 1e-9, $"UZ of {old.PointName}", ref total.Displacement);
            Near(now.RotationRad, old.RotationRad, 1e-9, $"rotation of {old.PointName}", ref total.Rotation);
        }
        foreach (var (now, old) in general.SupportReactions.Zip(planar.SupportReactions))
        {
            Near(now.ReactionXKN, old.ReactionXKN, 1e-6, $"X reaction at {old.PointName}", ref total.Reaction);
            Near(now.ReactionZKN, old.ReactionZKN, 1e-6, $"Z reaction at {old.PointName}", ref total.Reaction);
        }
        foreach (var (now, old) in general.Frames.Zip(planar.Frames))
        {
            Near(now.AxialStartKN, old.AxialStartKN, 1e-6, $"axial force at the start of {old.MemberName}", ref total.FrameForce);
            Near(now.AxialEndKN, old.AxialEndKN, 1e-6, $"axial force at the end of {old.MemberName}", ref total.FrameForce);
            Near(now.ShearStartKN, old.ShearStartKN, 1e-6, $"shear at the start of {old.MemberName}", ref total.FrameForce);
            Near(now.ShearEndKN, old.ShearEndKN, 1e-6, $"shear at the end of {old.MemberName}", ref total.FrameForce);
            Near(now.MomentStartKNm, old.MomentStartKNm, 1e-6, $"moment at the start of {old.MemberName}", ref total.FrameForce);
            Near(now.MomentEndKNm, old.MomentEndKNm, 1e-6, $"moment at the end of {old.MemberName}", ref total.FrameForce);
            Same(now.Stations!.Count == old.Stations!.Count, $"{old.MemberName} is read at {now.Stations.Count} stations by the general solver and {old.Stations.Count} by the planar solver");
            foreach (var (station, was) in now.Stations.Zip(old.Stations))
            {
                string at = $"{old.MemberName} at {was.Fraction:0.###}";
                Same(Math.Abs(station.Fraction - was.Fraction) <= 1e-12, $"station of {at} is placed at {station.Fraction:G12}");
                Near(station.AxialKN, was.AxialKN, 1e-6, $"axial force of {at}", ref total.FrameForce);
                Near(station.ShearKN, was.ShearKN, 1e-6, $"shear of {at}", ref total.FrameForce);
                Near(station.MomentKNm, was.MomentKNm, 1e-6, $"moment of {at}", ref total.FrameForce);
                Near(station.UXM, was.UXM, 1e-9, $"UX of {at}", ref total.Displacement);
                Near(station.UZM, was.UZM, 1e-9, $"UZ of {at}", ref total.Displacement);
            }
        }
        foreach (var (now, old) in general.Cables.Zip(planar.Cables))
        {
            Near(now.AxialForceKN, old.AxialForceKN, 1e-6, $"tension of {old.MemberName}", ref total.CableForce);
            Same(now.Active == old.Active || Math.Max(now.AxialForceKN, old.AxialForceKN) <= 1e-6, $"cable {old.MemberName} is {(now.Active ? "taut" : "slack")} in the general solver and {(old.Active ? "taut" : "slack")} in the planar solver");
            Near(now.TargetForceKN, old.TargetForceKN, 0, $"target of {old.MemberName}", ref total.Pretension);
            Same((now.SagM is null) == (old.SagM is null) && (now.PretensionStageForceKN is null) == (old.PretensionStageForceKN is null) &&
                 (now.FitHistoryKN is null) == (old.FitHistoryKN is null), $"cable {old.MemberName} reports the same kinds of result");
            if (now.SagM is { } sag)
            {
                double ratio = 0;
                Near(sag, old.SagM!.Value, 1e-9, $"sag of {old.MemberName}", ref total.Sag);
                Near(now.TangentStiffnessRatio!.Value, old.TangentStiffnessRatio!.Value, 1e-9, $"tangent stiffness ratio of {old.MemberName}", ref ratio);
            }
            if (now.PretensionStageForceKN is { } atStage) Near(atStage, old.PretensionStageForceKN!.Value, 1e-6, $"fit-stage tension of {old.MemberName}", ref total.CableForce);
            if (!fitting) continue;
            Near(now.AppliedPretensionKN, old.AppliedPretensionKN, 1e-6, $"applied pretension of {old.MemberName}", ref total.Pretension);
            if (now.FitHistoryKN is not { } history) continue;
            Same(history.Count == old.FitHistoryKN!.Count, $"cable {old.MemberName} has {history.Count} fitting runs in the general solver and {old.FitHistoryKN.Count} in the planar solver");
            foreach (var (run, was) in history.Zip(old.FitHistoryKN)) Near(run, was, 1e-6, $"a fitting run of {old.MemberName}", ref total.CableForce);
        }
        Same((general.TargetFitFailure is null) == (planar.TargetFitFailure is null), $"the fit {(general.TargetFitFailure is null ? "succeeded" : "failed")} in the general solver only: {general.TargetFitFailure ?? planar.TargetFitFailure}");
        if (fitting) Same(general.TargetFitIterations == planar.TargetFitIterations, $"fitted in {general.TargetFitIterations} runs by the general solver and {planar.TargetFitIterations} by the planar solver");
    }

    /// <summary>A two-chord truss of four 6 m bays with crossed cable diagonals. Each chord is one frame, so the
    /// verticals and the cables meet it at joints lying inside its length.</summary>
    private static void CableTruss()
    {
        var points = new Dictionary<string, StructuralPoint>(StringComparer.Ordinal);
        for (int i = 0; i <= 4; i++)
        {
            points["B" + i] = new("B" + i, new(6 * i, 0, 0));
            points["T" + i] = new("T" + i, new(6 * i, 0, 3));
        }
        var members = new List<StructuralMember> { new("Bottom", "B0", "B4", MemberKind.Frame), new("Top", "T0", "T4", MemberKind.Frame) };
        for (int i = 0; i <= 4; i++) members.Add(new("V" + i, "B" + i, "T" + i, MemberKind.Frame));
        for (int i = 0; i < 4; i++)
        {
            members.Add(new($"D{i}a", "B" + i, "T" + (i + 1), MemberKind.Cable));
            members.Add(new($"D{i}b", "T" + i, "B" + (i + 1), MemberKind.Cable));
        }
        var geometry = new GeometryModel(points, members);
        const double steel = 2e8, strand = 1.6e8, weight = 77;
        var frameSections = members.Where(member => member.Kind == MemberKind.Frame).ToDictionary(member => member.Name, member =>
            member.Name is "Bottom" or "Top"
                ? new FemSectionProperties(member.Name, .012, steel, 6e-4, UnitWeightKNPerM3: weight, GrossAreaM2: .014)
                : new FemSectionProperties(member.Name, .004, steel, 5e-5, member.Name is "V1" or "V2" or "V3", member.Name is "V1" or "V2" or "V3",
                    UnitWeightKNPerM3: weight, Modifiers: [1, 1, 1, 1, 1, 1, 1, 1.2]), StringComparer.Ordinal);
        var cableSections = members.Where(member => member.Kind == MemberKind.Cable).ToDictionary(member => member.Name, member =>
            new FemSectionProperties(member.Name, 8e-4, strand, null, UnitWeightKNPerM3: weight, GrossAreaM2: 9e-4), StringComparer.Ordinal);
        string[] pins = ["B0", "B4"];
        FemSectionProperties SectionOf(string member) => frameSections.TryGetValue(member, out var section) ? section : cableSections[member];
        var dead = new FemLoadSet([], members.Select(member => new FemMemberLoad(member.Name, FemLoadDirection.Gravity, 0, 1,
            SectionOf(member.Name).WeightPerLengthKNPerM, SectionOf(member.Name).WeightPerLengthKNPerM)).ToArray(), []);
        var live = new FemLoadSet(
            [new("T1", 0, -80), new("T2", 5, -40), new("T3", 0, -10, 6), new("T4", 15, 0)],
            [new("Top", FemLoadDirection.Gravity, .1, .8, 4, 9), new("V2", FemLoadDirection.Local2, 0, 1, 1.5, 1.5), new("Top", FemLoadDirection.ProjectedGravity, 0, .5, 2, 2)],
            [new("Bottom", FemLoadDirection.GlobalZ, .4, -20), new("Top", FemLoadDirection.Local1, .7, 8), new("Top", FemLoadDirection.GlobalZ, .25, -6)]);
        var service = FemLoadSet.Sum([dead, live.Scaled(1.2)]);
        var (planar, general) = (new PlanarFrameSolver(), new FemSolver());

        void Both(string name, IReadOnlyDictionary<string, double> pretension, FemLoadSet fitLoads, FemCaseLoads[] cases, double? percent, bool sag,
            double relative, bool fitting, Action<IReadOnlyDictionary<string, FemAnalysisResult>>? require = null)
        {
            var old = planar.SolveCases(geometry, frameSections, cableSections, pretension, fitLoads, cases, pins, default, percent, 100, 4, sag);
            var now = general.SolveCases(geometry, frameSections, cableSections, pretension, fitLoads, cases, pins, default, percent, 100, 4, sag);
            require?.Invoke(old);
            var total = new Differences();
            foreach (var item in cases) Compare($"{name}, case {item.Name}", old[item.Name], now[item.Name], relative, fitting, total);
            string fit = old[cases[0].Name].TargetFitIterations is > 0 and var runs ? $" ({runs} fitting runs)" : "";
            Console.WriteLine($"PASS: general solver versus archived planar solver — {name}{fit}: {total}");
        }
        var none = new Dictionary<string, double>();
        var given = cableSections.Keys.ToDictionary(cable => cable, cable => cable.EndsWith('a') ? 60.0 : 45.0, StringComparer.Ordinal);
        var targets = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (bool sag in new[] { false, true })
        {
            string with = sag ? ", cable sag on" : "";
            // tension-only equilibrium of the same structure, some diagonals slack
            Both("cable truss without pretension" + with, none, FemLoadSet.Empty, [new("Dead", dead, 0), new("Service", service, 0), new("Live", live, 0)], null, sag, 2e-6, true,
                old => { if (!sag && old["Live"].Cables.All(cable => cable.Active)) throw new Exception("FAIL: the comparison needs a case with a slack cable"); });
            // a pretension applied as it is, carried into the next case
            Both("cable truss with a given pretension" + with, given, dead, [new("Stage", dead, 1), new("Service", service, 1), new("Unstressed", live, 0)], null, sag, 2e-6, true,
                old =>
                {
                    // whole kilonewtons near what this pretension gives: targets that can certainly be reached
                    if (sag) foreach (var cable in old["Stage"].Cables) targets[cable.MemberName] = Math.Round(cable.AxialForceKN);
                });
        }
        // target forces fitted by both, to the same tolerance, with the same fitting runs
        foreach (var (percent, relative) in new[] { (10.0, 1e-5), (1.0, 1e-5), (1e-3, 1e-4) })
            Both($"cable truss with target forces fitted within {percent:G3}%, cable sag on", targets, dead,
                [new("Pretension", dead, 1), new("Service", service, 1), new("Unstressed", live, 0)], percent, true, relative, true,
                old => { if (!old["Pretension"].TargetFitConverged) throw new Exception("FAIL: the planar solver did not fit the targets of the comparison model: " + old["Pretension"].TargetFitFailure); });
        // target forces that cannot be met: both give the closest pretension found, and say so
        var unreachable = cableSections.Keys.ToDictionary(cable => cable, cable => cable.EndsWith('a') ? 40.0 : 2.0, StringComparer.Ordinal);
        Both("cable truss with target forces that cannot be met, cable sag on", unreachable, dead,
            [new("Pretension", dead, 1), new("Service", service, 1)], 1.0, true, 1e-4, false,
            old => { if (old["Pretension"].TargetFitConverged) throw new Exception("FAIL: the comparison needs targets that cannot be met"); });

        // What the application is told of the equilibrium while a run goes: the fitted run again, followed.
        FemCaseLoads[] staged = [new("Pretension", dead, 1), new("Service", service, 1), new("Unstressed", live, 0)];
        var reports = new EquilibriumReports();
        var followed = general.SolveCases(geometry, frameSections, cableSections, targets, dead, staged, pins, default, 1.0, 100, 4, true, reports);
        var unfollowed = general.SolveCases(geometry, frameSections, cableSections, targets, dead, staged, pins, default, 1.0, 100, 4, true);
        void Require(bool condition, string description)
        {
            if (!condition) throw new Exception("FAIL: " + description);
            Console.WriteLine("PASS: " + description);
        }
        Require(staged.All(item => followed[item.Name].Joints.Zip(unfollowed[item.Name].Joints).All(pair => pair.First == pair.Second) &&
                    followed[item.Name].Cables.Zip(unfollowed[item.Name].Cables).All(pair => pair.First.AxialForceKN == pair.Second.AxialForceKN && pair.First.AppliedPretensionKN == pair.Second.AppliedPretensionKN)),
            "following the equilibrium of a run changes no digit of its results");
        static string Label(FemEquilibriumProgress item) => item.FitStage ? "Target fit" : item.CaseName;
        var labels = reports.Items.Select(Label).Distinct().ToArray();
        Require(labels.SequenceEqual(["Target fit", "Pretension", "Service", "Unstressed"]),
            "the equilibrium is reported case by case, the fit of the cable target forces first: " + string.Join(", ", labels));
        // 10 joints, every one on a chord: UX, UZ and RY each. The two pins hold UX and UZ.
        Require(reports.Items.All(item => item is { ActiveDofs: 30, RestrainedDofs: 4, SolvedDofs: 26 }),
            "… with the degrees of freedom of the model: 30 active (UX, UZ, RY at 10 joints), 4 restrained at the two pins, 26 solved");
        var ended = labels.Select(label => reports.Items.Last(item => Label(item) == label)).ToArray();
        Require(ended.All(item => item is { State: FemEquilibriumState.Converged, Joints: 10, JointsInEquilibrium: 10, Linear: false } && item.OutOfBalance <= item.Tolerance && item.Iteration >= 1) &&
                ended[0].FitRun == followed["Pretension"].TargetFitIterations && ended[3].FitRun == 0,
            $"… every case ends converged with all 10 joints in equilibrium, the fit after its {ended[0].FitRun} fitting runs");
        var running = reports.Items.Where(item => item.State == FemEquilibriumState.Running && item.OutOfBalance is not null).ToArray();
        Require(running.Any(item => item.FitRun > 0 && item.FitStage) && running.Any(item => item.JointsInEquilibrium < item.Joints && points.ContainsKey(item.WorstJoint!) && item.WorstDof is "UX" or "UZ" or "RY") &&
                running.All(item => item.JointsInEquilibrium == item.Joints == (item.OutOfBalance <= item.Tolerance)),
            "… and while it iterates, each report counts the joints within the accepted out-of-balance and names the joint and movement where it is largest");
        var fractions = reports.Items.Select(item => item.Fraction).ToArray();
        Require(fractions.Zip(fractions.Skip(1)).All(pair => pair.Second >= pair.First) && fractions[0] == 0 && fractions[^1] == 1 &&
                reports.Items.Last(item => item.FitStage).Fraction == .25,
            "… the part of the run that has ended counts the load cases: a quarter after the fit, all of it at the end");
        // The same reports as the run monitor holds them: one entry per solve, with its status, the numerical
        // stability the solver assessed and its own log.
        var run = MonitorOf(reports.Items);
        Require(run is { Status: FemRunStatus.Completed, Fraction: 1, ActiveDofs: 30, SolvedDofs: 26, RestrainedDofs: 4 } &&
                run.Cases.Select(item => item.Label).SequenceEqual(["Cable target-force fit", "Pretension", "Service", "Unstressed"]) &&
                run.Cases.Select(item => item.Number).SequenceEqual([1, 2, 3, 4]) &&
                run.Cases.All(item => item is { Status: FemCaseStatus.Converged, Started: not null, Ended: not null, LeftOutDofs: 0,
                    Stability: { Passed: true, Instabilities: 0, Warnings: 0, Equations: 26, State: Mcotis.Analysis.StabilityState.PositiveDefinite, Joints.Count: 0, LargestDigitsLost: < 6 } }),
            "the run monitor holds one entry per solve — " + string.Join(", ", run.Cases.Select(item => $"{item.Number} {item.Label}")) +
            $" — each converged, with the numerical stability the solver assessed on its 26 equations: zero instabilities, largest loss {run.Cases.Max(item => item.Stability!.LargestDigitsLost):0.0} digits");
        var fitLog = run.Cases[0].Log.Select(entry => entry.Text).ToArray();
        Require(fitLog[0] == "Analysis started." && fitLog[1] == "Stiffness and loads assembled: 26 equations (30 active degrees of freedom, 4 restrained)." &&
                fitLog[2] == "Fitting run 1: equilibrium under the cable pretension of the run." &&
                System.Text.RegularExpressions.Regex.IsMatch(fitLog[3], @"^Iteration 1: out-of-balance \d\.\d{3}E[+-]\d\d kN(·m)? \(accepted \d\.\d{3}E[+-]\d\d kN\), \d+ of 10 joints in equilibrium(, largest at [BT]\d (UX|UZ|RY))?\.$") &&
                fitLog.Any(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"^Fitting run \d+: \d+ trial pretensions? for the response of the cables \(\d+ iterations?\)\.$")) &&
                fitLog.Contains("Equilibrium of the case under the fitted cable pretension.") &&
                fitLog.Any(line => line.StartsWith($"Cable target forces met after {ended[0].FitRun} fitting run", StringComparison.Ordinal)),
            $"… the log of the fit says what the solver did, in order: the equations assembled, each fitting run with its iterations — '{fitLog[3]}' — the trial pretensions counted in one line, the case's own equilibrium, the targets met");
        Require(run.Cases.All(item => item.Log.Select(entry => entry.Text).ToArray() is var log &&
                    log.Count(line => line.StartsWith("Equilibrium reached in ", StringComparison.Ordinal)) == 1 &&
                    log.Any(line => line.StartsWith("Stiffness at the converged state factorized: positive definite (smallest scaled pivot ", StringComparison.Ordinal)) &&
                    log.Any(line => line.StartsWith("Internal forces checked: relative resultant ", StringComparison.Ordinal)) &&
                    log.Any(line => line.StartsWith("Numerical stability checked: no instability detected; largest loss of accuracy ", StringComparison.Ordinal)) &&
                    log[^1] == "Analysis completed in 0.00 s." && item.Log.All(entry => entry.Level == FemLogLevel.Information)) &&
                run.Cases[2].Log.All(entry => !entry.Text.StartsWith("Fitting run", StringComparison.Ordinal)),
            "… every case's log ends with what the solver checked before it returned a result — the stiffness at the converged state, the internal forces, the numerical stability — and a case continuing from the fit has no fitting runs of its own");
    }

    /// <summary>The run the monitor holds after these reports, with a clock that stands still.</summary>
    internal static FemRunSnapshot MonitorOf(IEnumerable<FemEquilibriumProgress> reports, bool completed = true)
    {
        var monitor = new FemRunMonitor(() => new DateTime(2026, 10, 12, 23, 35, 1));
        foreach (var report in reports) monitor.Report(report);
        monitor.End(completed);
        return monitor.Snapshot();
    }

    internal sealed class EquilibriumReports : IProgress<FemEquilibriumProgress>
    {
        public readonly List<FemEquilibriumProgress> Items = [];
        public void Report(FemEquilibriumProgress value) => Items.Add(value);
    }

    /// <summary>The application's default model: the run the application makes (through <see cref="FemSolver"/>) against
    /// the archived planar solver given the same solver input.</summary>
    private static void DefaultModel()
    {
        var state = new ApplicationState();
        var silent = FemModelFileChecks.SapDouble.Create(null!, GeometryModel.Empty, refuseAll: true);
        var workflow = new WorkflowService(silent.Gateway, state);
        var sessions = Path.GetFullPath(Path.Combine(".work", "general-solver-" + Guid.NewGuid().ToString("N")));
        try
        {
            var vm = new MainViewModel(state, workflow, new NoSelection(), new ConfigService(silent.Gateway, state, workflow),
                femSessionStore: new FemWorkingSessionStore(sessions));
            vm.LoadDefaultFemModel();
            var runs = FemModelFileChecks.Analyze(vm);
            if (runs.Count != 1 || runs[0].Results is not { } shown) throw new Exception("FAIL: the default model did not run in the application: " + vm.FemProgressText);

            // What the application handed to its solver, rebuilt here the way MainViewModel.AnalyzeFemAsync builds it.
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            T Hidden<T>(string member) => (T)(typeof(MainViewModel).GetField(member, hidden)?.GetValue(vm)
                ?? typeof(MainViewModel).GetProperty(member, hidden)?.GetValue(vm)
                ?? typeof(MainViewModel).GetMethod(member, hidden, Type.EmptyTypes)?.Invoke(vm, null)
                ?? throw new Exception($"FAIL: MainViewModel.{member} is not there any more"));
            var properties = Hidden<IReadOnlyDictionary<string, FemSectionProperties>>("HeldFemProperties");
            var plan = FemLoadPlan.Build(vm.FemPatternRows.ToArray(), vm.FemCaseRows.ToArray(), vm.FemCombinationRows.ToArray(),
                Hidden<SapFemLoading>("femSapLoading"), properties, state.Baseline, Hidden<IReadOnlyList<string>>("Tab01Patterns"),
                Hidden<IReadOnlyList<string>>("PretensionPatterns"), state.AppliedPoints.Select(point => point.PointName).ToArray());
            var model = Hidden<FemStudyService>("femStudy").ComposeModel(state, vm.IsOfflineModel);
            var supports = FemStudyService.FindPinSupportPoints(state.Baseline);
            var frameSections = properties.Where(pair => pair.Value.InPlaneInertiaM4 is not null).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var cableSections = properties.Where(pair => pair.Value.InPlaneInertiaM4 is null).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

            var total = new Differences();
            var deflections = new List<string>();
            int solved = 0;
            foreach (var (fitStage, fitLoads, cases) in plan.StagesFor(model.Loads))
            {
                var planar = new PlanarFrameSolver().SolveCases(state.Baseline, frameSections, cableSections,
                    fitStage is null ? new Dictionary<string, double>() : model.PretensionTargetsKN, fitLoads, cases, supports, default,
                    vm.FemTargetFitPercent, vm.FemTargetFitIterations, vm.FemFrameMeshSegments, vm.FemCableSag);
                foreach (var item in cases)
                {
                    Compare($"default model, case '{item.Name}'", planar[item.Name], shown[item.Name], 1e-5, fitting: true, total);
                    deflections.Add($"{item.Name} {shown[item.Name].MaximumDownwardDeflectionMm:0.0} mm");
                    solved++;
                }
            }
            if (solved != plan.CaseNames.Count) throw new Exception("FAIL: not every case of the default model was compared");
            Console.WriteLine($"PASS: general solver versus archived planar solver — the default model as the application runs it " +
                $"({state.Baseline.Points.Count} joints, {frameSections.Count} frames, {cableSections.Count} cables, {solved} cases; " +
                $"target forces within {vm.FemTargetFitPercent:G3}%, cable sag {(vm.FemCableSag ? "on" : "off")}, {vm.FemFrameMeshSegments} stations per frame piece): {total}");
            Console.WriteLine("      largest downward deflection the application shows: " + string.Join(" · ", deflections));

            // What the run monitor shows of that run: the degrees of freedom, and the equilibrium of every load case.
            // Counted here from the model: UX, UZ and RY at a joint a frame reaches (at an end, or lying on it), UX and
            // UZ at a joint only cables reach; the pin supports hold UX and UZ.
            var members = state.Baseline.Members.Where(member => member.Kind == MemberKind.Frame || member.Kind == MemberKind.Cable && cableSections.ContainsKey(member.Name)).ToArray();
            var positions = members.SelectMany(member => new[] { member.Start, member.End }).Distinct(StringComparer.Ordinal)
                .ToDictionary(name => name, name => state.Baseline.Points[name].Position, StringComparer.Ordinal);
            var framed = members.Where(member => member.Kind == MemberKind.Frame).SelectMany(member => FrameJoints.Pieces(member, positions))
                .SelectMany(piece => new[] { piece.Member.Start, piece.Member.End }).ToHashSet(StringComparer.Ordinal);
            int active = 3 * framed.Count + 2 * (positions.Count - framed.Count), restrained = 2 * supports.Count;
            void Monitor(bool condition, string description)
            {
                if (!condition) throw new Exception("FAIL: " + description);
                Console.WriteLine("PASS: " + description);
            }
            var shownRun = vm.FemMonitor;
            Monitor(shownRun.DofText == FormattableString.Invariant($"{active:N0} active | {active - restrained:N0} solved | {restrained:N0} restrained"),
                $"the run monitor states the degrees of freedom of the run, as counted from the model ({framed.Count} frame joints, {positions.Count - framed.Count} cable-only joints, {supports.Count} pins) — {shownRun.DofText}");
            var expectedRows = plan.StagesFor(model.Loads).SelectMany(stage => (stage.FitCase is { } fit ? new[] { $"{fit} — cable target-force fit" } : []).Concat(stage.Cases.Select(item => item.Name))).ToArray();
            Monitor(shownRun.Cases.Select(item => item.Name).SequenceEqual(expectedRows) && shownRun.Cases.Select(item => item.Number).SequenceEqual(Enumerable.Range(1, expectedRows.Length)) &&
                    shownRun.Cases.All(item => item.Status is FemCaseStatus.Converged or FemCaseStatus.ConvergedWithWarnings && item is { ConvergenceText: "Passed", InstabilitiesText: "0", HasFailure: false } &&
                        item.Details.Any(field => field is { Label: "Nonlinear convergence", Value: "Converged" }) && item.Details.Any(field => field is { Label: "Convergence tolerance", Value: "Satisfied" }) &&
                        item.Technical.Any(field => field.Label == "Largest loss of accuracy")),
                $"… and every solve of the run, the fit of the cable target forces first, each converged with zero instabilities: " +
                string.Join(" | ", shownRun.Cases.Select(item => $"{item.Number} {item.Name}: {item.StatusText}, stability {item.StabilityText}, {item.Technical.Single(field => field.Label == "Largest loss of accuracy").Value}")));
            Monitor(shownRun.Status is FemRunStatus.Completed or FemRunStatus.CompletedWithWarnings && shownRun.Progress == 1 && !shownRun.IsIndeterminate &&
                    (shownRun.Status == FemRunStatus.Completed) == shownRun.Cases.All(item => item.Status == FemCaseStatus.Converged),
                "… with the outcome of the run and the bar full: " + shownRun.StatusText);

            // The model in numbers, by the name of its file, and no combination among the solves.
            int frames = members.Count(member => member.Kind == MemberKind.Frame);
            string Shown(string label) => shownRun.ModelLeft.Concat(shownRun.ModelRight).Single(field => field.Label == label).Value;
            Monitor(shownRun.ModelName == vm.CurrentFileName && Shown("Number of Joints") == positions.Count.ToString() && Shown("Number of Restrained Joints") == supports.Count.ToString() &&
                    Shown("Number of Frame Members") == frames.ToString() && Shown("Number of Cable Members") == (members.Length - frames).ToString() && Shown("Number of Shell Elements") == "0" &&
                    Shown("Number of Load Cases") == plan.CaseNames.Count.ToString() && Shown("Number of Load Combinations") == plan.CombinationNames.Count.ToString() &&
                    shownRun.SupportSummary.StartsWith($"{supports.Count} pin supports", StringComparison.Ordinal) && supports.All(name => shownRun.SupportSummary.Contains(name, StringComparison.Ordinal)) &&
                    plan.CombinationNames.All(name => shownRun.Cases.All(item => item.Name != name)),
                $"the model information is counted from the model that is run — '{shownRun.ModelName}', {positions.Count} joints ({supports.Count} restrained), {frames} frames, {members.Length - frames} cables, " +
                $"{plan.CaseNames.Count} load cases, {plan.CombinationNames.Count} combinations — and a combination, which needs no solve of its own, is not listed among the load cases");
            File.WriteAllLines(Path.Combine(".work", "analysis-log.txt"), shownRun.Cases.SelectMany(item => new[] { $"[{item.Number}] {item.Name} — {item.StatusText}" }.Concat(item.LogText.Split(Environment.NewLine)).Append("")));
            Monitor(shownRun.Cases.All(item => item.LogText.Split(Environment.NewLine) is var log &&
                        System.Text.RegularExpressions.Regex.IsMatch(log[0], @"^\d\d:\d\d:\d\d  Analysis started\.$") && log.Any(line => line.Contains("Stiffness and loads assembled: ", StringComparison.Ordinal)) &&
                        log.Any(line => line.Contains("Numerical stability checked: ", StringComparison.Ordinal)) && log[^1].Contains("Analysis completed in ", StringComparison.Ordinal)) &&
                    shownRun.Cases.Where(item => item.Name.EndsWith("cable target-force fit", StringComparison.Ordinal)).All(item => item.LogText.Contains("trial pretension", StringComparison.Ordinal) && item.LogText.Contains("Cable target forces met after ", StringComparison.Ordinal)),
                "… every load case has its own log, from 'Analysis started' through the equations, the iterations and the numerical stability to 'Analysis completed'; the trial pretensions of a fitting run are counted in one line");
        }
        finally
        {
            try { if (Directory.Exists(sessions)) Directory.Delete(sessions, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A native document of a model with a cable — a cantilever of 4 m with a cable from its tip to an anchor 3 m
    /// above, a 50 kN target force, then 100 kN down or 80 kN up on the tip — solved by the engine, and as the main window
    /// shows it once it takes the document in (M8; the native workspace that showed it before is archived).</summary>
    private static void NativeWorkspace()
    {
        ModelDocument Document(bool linear)
        {
            var editor = ModelDocument.Create().Edit();
            var (steel, strand, beam, rope) = (ObjectId.New(), ObjectId.New(), ObjectId.New(), ObjectId.New());
            var (live, wind, prestress) = (ObjectId.New(), ObjectId.New(), ObjectId.New());
            editor.PutMaterial(new(steel, "Steel", MaterialType.Steel, 2e8, .3, 0, 0));
            editor.PutMaterial(new(strand, "Strand", MaterialType.Tendon, 1.6e8, .3, 0, 0));
            editor.PutFrameSection(new(beam, "Beam", steel, FrameSectionShape.General, new(.01, .0008, .0004, .00001)));
            editor.PutCableSection(new(rope, "Rope", strand, .002));
            var root = editor.AddPoint(new(0, 0, 0), "Root").Id;
            var tip = editor.AddPoint(new(4, 0, 0), "Tip").Id;
            var anchor = editor.AddPoint(new(4, 0, 3), "Anchor").Id;
            editor.SetRestraint([root], Restraint.Fixed);
            editor.SetRestraint([anchor], Restraint.Pinned);
            editor.SetFrameSection([editor.AddFrame(root, tip, "Arm").Id], beam);
            var stay = editor.AddCable(tip, anchor, "Stay").Id;
            editor.SetCableSection([stay], rope);
            editor.PutLoadPattern(new(live, "LIVE", LoadPatternType.Live));
            editor.PutLoadPattern(new(wind, "WIND", LoadPatternType.Other));
            editor.PutLoadPattern(new(prestress, "PT", LoadPatternType.Prestress));
            editor.PutLoad(new NativePointLoad(ObjectId.New(), live, tip, new(0, 0, -100)));
            editor.PutLoad(new NativePointLoad(ObjectId.New(), wind, tip, new(0, 0, 80)));
            editor.PutLoad(new NativeTargetForce(ObjectId.New(), prestress, stay, 50));
            var fit = ObjectId.New();
            editor.PutLoadCase(new(fit, "Pretension", LoadCaseType.NonlinearStatic, [new(prestress, 1)]) { FitCableTargetForces = true });
            editor.PutLoadCase(new(ObjectId.New(), "Service", LoadCaseType.NonlinearStatic, [new(live, 1)]) { InitialCaseId = fit });
            editor.PutLoadCase(new(ObjectId.New(), "Uplift", LoadCaseType.NonlinearStatic, [new(wind, 1)]) { InitialCaseId = fit });
            if (linear) editor.PutLoadCase(new(ObjectId.New(), "Linear", LoadCaseType.LinearStatic, [new(live, 1)]));
            editor.SetSettings(new AnalysisSettings(1e-6, 100, 1, false));
            return editor.Commit().Document;
        }
        var document = Document(linear: true);
        var model = Mcotis.Analysis.ModelCompiler.Compile(document).Model!;
        Mcotis.Analysis.StaticOutcome Solve(string name) =>
            Mcotis.Analysis.StaticSolver.Solve(model, document.LoadCases.Values.Single(item => item.Name == name).Id);
        void Check(bool condition, string description)
        {
            if (!condition) throw new Exception("FAIL: " + description);
            Console.WriteLine("PASS: " + description);
        }
        const double arm = 3 * 2e8 * .0008 / 64, cable = 1.6e8 * .002 / 3;
        var service = Solve("Service");
        var taut = service.Result?.Cables.Single() ?? throw new Exception("FAIL: the staged case has no result: " + string.Join(" | ", service.Issues.Select(issue => issue.Message)));
        Check(service.Success && taut is { Active: true, TargetForce: 50 } &&
              Math.Abs(taut.Tension - (50 + 100 * cable / (arm + cable))) < 1e-5 && Math.Abs(taut.AppliedPretension - 50 * (arm + cable) / arm) < 1e-4 &&
              Math.Abs(taut.FitStageTension!.Value - 50) < 1e-6 && taut.Sag is null && service.Result.Diagnostics.CableFit is not null,
            "a native document with a cable: the engine gives the cable's tension, target, applied pretension and fit-stage tension after a staged case");
        var uplift = Solve("Uplift");
        var tip = document.Points.Values.Single(point => point.Name == "Tip").Id;
        Check(uplift.Success && uplift.Result!.Cables.Single() is { Active: false, Tension: 0 } &&
              Math.Abs(uplift.Result.Joints.Single(joint => joint.PointId == tip).Displacements[2] - 80 / arm) < 1e-10,
            "… a slack cable is slack, with the frame carrying the load alone");
        var refused = Solve("Linear");
        Check(!refused.Success && refused.Result is null && refused.Issues.Any(issue => issue.Code == "static.cable.linearCase"),
            "… and a linear case of a model with cables is refused with the reason, no numbers");

        // the main window takes the document in and shows the same numbers (the linear case is left out: Run would stop on it)
        var state = new ApplicationState();
        var sap = FemModelFileChecks.SapDouble.Create(null!, GeometryModel.Empty, refuseAll: true);
        var workflow = new WorkflowService(sap.Gateway, state);
        var vm = new MainViewModel(state, workflow, new NoSelection(), new ConfigService(sap.Gateway, state, workflow));
        vm.OpenNativeDocument(Document(linear: false), "Cantilever", null, LegacyNativeDocumentMigration.Source.Imported("Mcotis native model"));
        var shown = FemModelFileChecks.Analyze(vm).Single().ResultFor("Service").Cables.Single();
        Check(shown.MemberName == "Stay" && Math.Abs(shown.AxialForceKN - taut.Tension) < 1e-6 && Math.Abs(shown.AppliedPretensionKN - taut.AppliedPretension) < 1e-5 &&
              Math.Abs((shown.PretensionStageForceKN ?? 0) - 50) < 1e-6 && shown.TargetForceKN == 50,
            $"… the main window, which takes the document in, shows them: tension {shown.AxialForceKN:0.###} kN, applied pretension {shown.AppliedPretensionKN:0.###} kN, fit stage {shown.PretensionStageForceKN:0.###} kN");
    }

    private sealed class NoSelection : ISelectionService
    {
        public IReadOnlyList<string>? Select(string title, IReadOnlyList<string> available, IReadOnlyList<string> selected) => null;
    }
}
