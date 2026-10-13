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
        var labels = reports.Items.Select(FemEquilibriumRow.CaseLabel).Distinct().ToArray();
        Require(labels.SequenceEqual(["Target fit", "Pretension", "Service", "Unstressed"]),
            "the equilibrium is reported case by case, the fit of the cable target forces first: " + string.Join(", ", labels));
        // 10 joints, every one on a chord: UX, UZ and RY each. The two pins hold UX and UZ.
        Require(reports.Items.All(item => item is { ActiveDofs: 30, RestrainedDofs: 4, SolvedDofs: 26 }),
            "… with the degrees of freedom of the model: 30 active (UX, UZ, RY at 10 joints), 4 restrained at the two pins, 26 solved");
        var ended = labels.Select(label => reports.Items.Last(item => FemEquilibriumRow.CaseLabel(item) == label)).ToArray();
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
        var rows = ended.Select(item => FemEquilibriumRow.From(item)).ToArray();
        Require(rows.All(row => row is { Status: FemEquilibriumRow.Converged, SolvedDofs: "26", Joints: "10 / 10", Detail: null } &&
                    (row.OutOfBalance.EndsWith(" kN", StringComparison.Ordinal) || row.OutOfBalance.EndsWith(" kN·m", StringComparison.Ordinal)) && row.Accepted.EndsWith(" kN", StringComparison.Ordinal)) &&
                rows[0].Iterations.Contains($"· {ended[0].FitRun} fit run", StringComparison.Ordinal) && rows[1].Iterations == ended[1].Iteration.ToString(),
            $"… which the run monitor shows as one line per case: {rows[0].Case} — {rows[0].Iterations} iterations, out-of-balance {rows[0].OutOfBalance} against {rows[0].Accepted}, joints {rows[0].Joints}, largest at {rows[0].LargestAt}, {rows[0].Status}");

        // The same reports as the analysis log, in the layout of SAP2000's.
        // a title: its letters spaced out from column 2, the time ending at column 78
        static string Titled(string spaced, string stamp = "23:35:01") => spaced.PadRight(78 - stamp.Length) + stamp;
        Require(FemRunLog.Title("Nonlinear static analysis", new DateTime(2026, 10, 12, 23, 35, 1)) == Titled(" N O N L I N E A R   S T A T I C   A N A L Y S I S") &&
                FemRunLog.Title("Begin analysis", new DateTime(2026, 10, 12, 23, 35, 1), date: true) == Titled(" B E G I N   A N A L Y S I S", "2026/10/12  23:35:01") &&
                FemRunLog.Entry("TOTAL TIME FOR THIS ANALYSIS", "0.04") == " TOTAL TIME FOR THIS ANALYSIS              =        0.04" &&
                FemRunLog.Entry("WITH RESTRAINTS", 4, 4) == "     WITH RESTRAINTS                       =           4",
            "the analysis log is laid out like SAP2000's: section titles spaced out with the time at the right, NAME = value lines in two columns");
        var log = LogOf(reports.Items);
        int At(string line, int from = 0) => Array.IndexOf(log, line, from) is >= 0 and var found ? found : throw new Exception("FAIL: the analysis log has no line '" + line + "'");
        int fitBlock = At(Titled(" C A B L E   T A R G E T - F O R C E   F I T"));
        Require(At(Titled(" E Q U A T I O N S")) < fitBlock &&
                log[At(FemRunLog.Entry("NUMBER OF ACTIVE DEGREES OF FREEDOM", 30)) + 1] == FemRunLog.Entry("RESTRAINED BY SUPPORTS", 4, 4) &&
                log[At(FemRunLog.Entry("NUMBER OF ACTIVE DEGREES OF FREEDOM", 30)) + 2] == FemRunLog.Entry("TOTAL NUMBER OF EQUILIBRIUM EQUATIONS", 26) &&
                log[fitBlock + 2] == " CASE: (THE LOADING OF THE RUN)" && log[fitBlock + 4] == " STARTING FROM ZERO (UNSTRESSED) INITIAL CONDITIONS" &&
                At(FemRunLog.Entry("USE LINE SEARCH", "YES"), fitBlock) > fitBlock && At(FemRunLog.Entry("FORCE CONVERGENCE TOLERANCE (RELATIVE)", "1.000E-09"), fitBlock) > fitBlock,
            "… the equations of the run, then the block of the cable target-force fit with what this solver does: force control, iteration with a line search, a relative force tolerance of 1e-9");
        int table = At("    Fit  Iteration      Unbalance    Relative      Joints  Largest", fitBlock);
        var firstRow = log[table + 2];
        var lastOfFit = log.Skip(table).TakeWhile(line => line.Length > 0).Last();
        Require(log[table + 1] == "    Run  this Step     [kN, kN-m]   Unbalance   Converged  Unbalance At" &&
                System.Text.RegularExpressions.Regex.IsMatch(firstRow, @"^      1          1   \d\.\d{6}E[+-]\d\d   \d\.\d{3}E[+-]\d\d  +\d+ / 10  [BT]\d (UX|UZ|RY)$") &&
                System.Text.RegularExpressions.Regex.IsMatch(lastOfFit, @"^      -  Conv +\d+   \d\.\d{6}E-\d\d   \d\.\d{3}E-\d\d     10 / 10  [BT]\d (UX|UZ|RY)$") &&
                log.Skip(table).TakeWhile(line => line.Length > 0).Count(line => line.Contains("trial pretensions for the response of the cables", StringComparison.Ordinal)) >= 1,
            $"… one line per equilibrium iteration — fitting run, iteration, unbalance, relative unbalance, joints converged, where it is largest — e.g. '{firstRow.Trim()}' … '{lastOfFit.Trim()}'");
        int stable = At(FemRunLog.Stable, table);
        Require(log[stable - 5] == " " + new string('-', 39) && log[stable - 4] == "    NUMBER OF NEGATIVE STIFFNESS EIGENVALUES SHOULD BE ZERO FOR STABILITY." &&
                log[stable - 1] == "" && log[stable - 2] == "    (NOTE: FURTHER CHECKS SHOULD BE CONSIDERED AS DEEMED NECESSARY)" &&
                log[stable + 1] == " " + new string('-', 39) && log[stable + 3].StartsWith(" SMALLEST SCALED PIVOT    ", StringComparison.Ordinal) &&
                log[stable + 5] == FemRunLog.Entry("NUMBER OF FITTING RUNS", ended[0].FitRun) && log[stable + 6].StartsWith(" LARGEST DEVIATION FROM A TARGET (PERCENT) =", StringComparison.Ordinal) &&
                log[stable + 7] == FemRunLog.Entry("TOTAL TIME FOR THIS ANALYSIS", "0.00"),
            "… and the case ends as in SAP2000's log: the stability check of the converged state, then what the fit reached and the time of the case");
        Require(new[] { "Pretension", "Service", "Unstressed" }.All(name => log[At(" CASE: " + name) - 2] == Titled(" N O N L I N E A R   S T A T I C   A N A L Y S I S")) &&
                log[At(" CASE: Service") + 2] == " STARTING FROM THE CABLE PRETENSION FITTED FOR THIS STAGE" &&
                log[At(" CASE: Unstressed") + 2] == " STARTING FROM ZERO (UNSTRESSED) INITIAL CONDITIONS" &&
                log.Count(line => line == FemRunLog.Stable) == 4 && !log.Contains(FemRunLog.Error),
            "… each load case after it has its own block, saying whether it starts from the fitted pretension or from zero");
    }

    /// <summary>The analysis log written from the reports of a run, with a clock that stands still.</summary>
    internal static string[] LogOf(IEnumerable<FemEquilibriumProgress> reports)
    {
        var log = new FemRunLog(() => new DateTime(2026, 10, 12, 23, 35, 1));
        foreach (var report in reports) log.Report(report);
        return [.. log.Take()];
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
            Monitor(vm.FemMonitorDofText.Contains($"{active:N0} active", StringComparison.Ordinal) && vm.FemMonitorDofText.Contains($"{active - restrained:N0} solved", StringComparison.Ordinal) &&
                    vm.FemMonitorDofText.Contains($"{restrained:N0} restrained", StringComparison.Ordinal) && !vm.FemMonitorDofText.Contains("left out", StringComparison.Ordinal),
                $"the run monitor states the degrees of freedom of the run, as counted from the model ({framed.Count} frame joints, {positions.Count - framed.Count} cable-only joints, {supports.Count} pins) — {vm.FemMonitorDofText}");
            var expectedRows = plan.StagesFor(model.Loads).SelectMany(stage => (stage.FitCase is { } fit ? new[] { $"{fit} · target fit" } : []).Concat(stage.Cases.Select(item => item.Name))).ToArray();
            string everyJoint = $"{positions.Count:N0} / {positions.Count:N0}";
            Monitor(vm.FemEquilibriumRows.Select(row => row.Case).SequenceEqual(expectedRows) &&
                    vm.FemEquilibriumRows.All(row => row.Status == FemEquilibriumRow.Converged && row.Joints == everyJoint && row.SolvedDofs == $"{active - restrained:N0}"),
                $"… and the equilibrium of every load case, the fit of the cable target forces first, each converged with every joint in equilibrium ({everyJoint}): " +
                string.Join(" | ", vm.FemEquilibriumRows.Select(row => $"{row.Case}: {row.Iterations} it., {row.OutOfBalance} ≤ {row.Accepted}")));
            Monitor(vm.FemMonitorEquilibriumText.StartsWith("Equilibrium reached in every load case", StringComparison.Ordinal) && vm.FemMonitorProgress == 1 && !vm.FemMonitorIndeterminate,
                "… with the outcome in one line and the bar full: " + vm.FemMonitorEquilibriumText);

            // The analysis log of that run, in the layout of SAP2000's.
            var log = vm.FemLog.ToArray();
            File.WriteAllLines(Path.Combine(".work", "analysis-log.txt"), log);
            int frames = members.Count(member => member.Kind == MemberKind.Frame);
            Monitor(log[0].StartsWith(" B E G I N   A N A L Y S I S ", StringComparison.Ordinal) && log[^1].StartsWith(" A N A L Y S I S   C O M P L E T E ", StringComparison.Ordinal) &&
                    log.Contains(FemRunLog.Entry("NUMBER OF JOINTS", positions.Count)) && log.Contains(FemRunLog.Entry("WITH RESTRAINTS", supports.Count, 4)) &&
                    log.Contains(FemRunLog.Entry("NUMBER OF FRAME MEMBERS", frames)) && log.Contains(FemRunLog.Entry("NUMBER OF CABLE MEMBERS", members.Length - frames)) &&
                    log.Contains(FemRunLog.Entry("NUMBER OF LOAD CASES", plan.CaseNames.Count)) && log.Contains(FemRunLog.Entry("CABLE SAG", vm.FemCableSag ? "YES" : "NO")),
                $"the analysis log opens like SAP2000's — BEGIN ANALYSIS, the model in numbers ({positions.Count} joints, {frames} frames, {members.Length - frames} cables), the options — and closes with ANALYSIS COMPLETE");
            Monitor(log.Count(line => line.StartsWith(" E Q U A T I O N S ", StringComparison.Ordinal)) == 1 &&
                    log.Contains(FemRunLog.Entry("NUMBER OF ACTIVE DEGREES OF FREEDOM", active)) && log.Contains(FemRunLog.Entry("RESTRAINED BY SUPPORTS", restrained, 4)) &&
                    log.Contains(FemRunLog.Entry("TOTAL NUMBER OF EQUILIBRIUM EQUATIONS", active - restrained)),
                $"… it states the equations of the run once: {active} active degrees of freedom, {restrained} restrained, {active - restrained} equations");
            var blocks = log.Select((line, index) => (line, index)).Where(item => item.line.StartsWith(" CASE: ", StringComparison.Ordinal) &&
                (log[item.index - 2].StartsWith(" N O N L I N E A R   S T A T I C   A N A L Y S I S ", StringComparison.Ordinal) ||
                 log[item.index - 2].StartsWith(" C A B L E   T A R G E T - F O R C E   F I T ", StringComparison.Ordinal))).Select(item => item.line[7..]).ToArray();
            Monitor(blocks.SequenceEqual(plan.StagesFor(model.Loads).SelectMany(stage => (stage.FitCase is { } fitted ? new[] { fitted } : []).Concat(stage.Cases.Select(item => item.Name)))) &&
                    log.Count(line => line == FemRunLog.Stable) == blocks.Length &&
                    log.Count(line => line.StartsWith(FemRunLog.Entry("TOTAL TIME FOR THIS ANALYSIS", "")[..44], StringComparison.Ordinal)) == blocks.Length &&
                    log.Count(line => line.StartsWith(" E Q U I L I B R I U M   I T E R A T I O N ", StringComparison.Ordinal)) == blocks.Length &&
                    !log.Contains(FemRunLog.Error) && !log.Contains(FemRunLog.Warning),
                $"… then one block per solve, the cable target-force fit first ({string.Join(", ", blocks)}): its iteration table, the stability check — no negative stiffness eigenvalue — and its time");
            var converged = log.Where(line => line.Length > 14 && line.Substring(9, 4) == "Conv").ToArray();
            Monitor(converged.Length >= blocks.Length && converged.All(line => line.Contains($"{positions.Count} / {positions.Count}", StringComparison.Ordinal)) &&
                    log.Any(line => line.Contains("trial pretension", StringComparison.Ordinal)) &&
                    log.Contains(FemRunLog.Entry("CABLE TARGET FORCES", "MET")) && log.Any(line => line.StartsWith(" OUTPUT DATABASE: ", StringComparison.Ordinal)) &&
                    plan.ResultNames.All(name => log.Any(line => line.StartsWith($" {name,-38}  ", StringComparison.Ordinal) &&
                        line.Contains(shown[name].MaximumDownwardDeflectionMm.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))) &&
                    log.All(line => line.Length <= 78 || line.StartsWith(" OUTPUT DATABASE: ", StringComparison.Ordinal)),
                $"… every equilibrium ends on a Conv line with all {positions.Count} joints converged; the trial pretensions of a fitting run are counted in one line; " +
                "the results close the log with the largest values of every load case, and no line is wider than 78 columns");
        }
        finally
        {
            try { if (Directory.Exists(sessions)) Directory.Delete(sessions, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>What the native workspace shows of a model with a cable: a cantilever of 4 m with a cable from its tip
    /// to an anchor 3 m above, a 50 kN target force, then 100 kN down or 80 kN up on the tip.</summary>
    private static void NativeWorkspace()
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
        editor.PutLoadCase(new(ObjectId.New(), "Linear", LoadCaseType.LinearStatic, [new(live, 1)]));
        editor.SetSettings(new AnalysisSettings(1e-6, 100, 1, false));
        var vm = new NativeModelViewModel(new Mcotis.Application.DocumentSession(editor.Commit().Document));
        void RunCase(string name)
        {
            vm.SelectedCase = vm.Cases.Single(item => item.Name == name);
            vm.RunAsync().GetAwaiter().GetResult();
        }
        void Check(bool condition, string description)
        {
            if (!condition) throw new Exception("FAIL: " + description + " — " + vm.Message);
            Console.WriteLine("PASS: " + description);
        }
        const double arm = 3 * 2e8 * .0008 / 64, cable = 1.6e8 * .002 / 3;
        RunCase("Service");
        Check(vm.Results.State == Mcotis.Application.ResultState.Current && vm.Cables.Single() is { Cable: "Stay", State: "Taut", Target: 50 } taut &&
              Math.Abs(taut.Tension - (50 + 100 * cable / (arm + cable))) < 1e-5 && Math.Abs(taut.Pretension - 50 * (arm + cable) / arm) < 1e-4 &&
              Math.Abs(taut.FitStage!.Value - 50) < 1e-6 && taut.Sag is null,
            "the native workspace shows a cable's tension, target, applied pretension and fit-stage tension after a staged case");
        Check(vm.Message.Contains("cable target forces met within", StringComparison.Ordinal) && vm.Message.Contains("cable equilibrium in", StringComparison.Ordinal) &&
              vm.Summary.Contains("1 cables", StringComparison.Ordinal),
            "… and says how the cable equilibrium and the fit went");
        RunCase("Uplift");
        Check(vm.Cables.Single() is { State: "Slack", Tension: 0 } && Math.Abs(vm.Joints.Single(joint => joint.Point == "Tip").UZ - 80 / arm) < 1e-10,
            "… a slack cable is shown as slack, with the frame carrying the load alone");
        RunCase("Linear");
        Check(vm.Results.State == Mcotis.Application.ResultState.Failed && !vm.Cables.Any() && !vm.Joints.Any() &&
              vm.Message.Contains("static.cable.linearCase", StringComparison.Ordinal),
            "… and a case the solver refuses shows the reason and no numbers");
    }

    private sealed class NoSelection : ISelectionService
    {
        public IReadOnlyList<string>? Select(string title, IReadOnlyList<string> available, IReadOnlyList<string> selected) => null;
    }
}
