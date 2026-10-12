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
