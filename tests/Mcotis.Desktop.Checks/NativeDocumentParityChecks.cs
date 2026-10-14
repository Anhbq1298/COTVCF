using System.IO;
using Mcotis.Analysis;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Etabs;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Presentation;
using Native = Mcotis.Core;

/// <summary>
/// Run FEM Analysis reads the native document of the model open in the main window since 2026-10-14 (M1:
/// <see cref="FemDocumentRun"/>). This is the evidence that it gives what the run gave before, when it handed the solver
/// the application's own description of the model (<c>Application/FemSolver.cs</c>: each case as its accumulated loads,
/// the fit of the cable target forces as a stage of its own, a cable's loads at its two ends) — the path
/// <c>--general-solver</c> validated against the archived planar solver. The document says the same model the way a
/// user defines it: load patterns, cases that continue from one another, target forces in their pattern, self-weight
/// by the pattern's multiplier (<see cref="LegacyNativeDocumentMigration"/>). Every case and combination of every
/// model here, to round-off.
/// </summary>
internal static class NativeDocumentParityChecks
{
    /// <summary>The largest difference of each kind over the cases compared, each relative to the largest value of
    /// its kind in the same case, and how many values were compared.</summary>
    internal sealed record Parity(int Cases, int Values, double Displacement, double Reaction, double CableForce, double FrameForce);

    public static void Run()
    {
        Model("the VCF system (41 joints, frames and 16 cables, cable target forces fitted in the Pretension case, SLS and ULS continuing from it)",
            vm => vm.LoadDefaultFemModel(replaceCurrent: true));
        Model("the tied arch system (frames only, joints held out of the plane in UY)", vm => vm.LoadTasModel());
        Model("the tied arch system with an elevated cable (columns, a tie with a target force)",
            vm => vm.ApplyTiedArchSystem(TiedArchParameters.Default with { ElevatedCable = true }));
        Model("the ETABS model imported from its .e2k file (frames as ETABS has them, 9 static cases)",
            vm => vm.ImportFromE2kFile(Path.Combine(EtabsImportChecks.Folder, "CoT120.e2k")));

        // restraints the model holds itself, component by component: a fixed base changes the results, in both alike
        Model("the VCF system on joint restraints it holds — one column base fixed, the others pinned", vm =>
        {
            vm.LoadDefaultFemModel(replaceCurrent: true);
            var bases = vm.FemPinSupportPoints;
            vm.State.JointRestraints = bases.ToDictionary(name => name,
                name => name == bases[0] ? Native.Restraint.Fixed : Native.Restraint.Pinned, StringComparer.Ordinal);
        });

        // The model imported from the SAP2000 stand-in has frames whose sections hold no bending inertia out of the plane
        // (the fixture gives none). In its plane that inertia is not called on (FrameStiffnessDemand, 2026-10-14): the
        // document solves as it is and gives the results of the run — the gap this check used to record is closed.
        var imported = SapImportChecks.ImportedViewModel();
        var parity = Compare(imported);
        var locked = imported.NativeDocumentSession.Document;
        var without = locked.Frames.Values.Where(frame =>
                locked.AssignmentOfFrame(frame.Id) is var a && !(locked.FrameSections[a.SectionId!.Value].Properties.Inertia22 * a.Modifiers.Inertia22 > 0))
            .Select(frame => frame.Name).Order(StringComparer.Ordinal).ToArray();
        Check(without.Length > 0 && locked.Settings.ActiveDofs == Native.DofMask.XZ,
            $"the model imported from SAP2000, as it is run in its XZ plane, has {without.Length} frame(s) whose section holds no inertia out of the plane ({string.Join(", ", without.Take(3))}{(without.Length > 3 ? ", …" : "")})");
        Report("the model imported from SAP2000, analysed in its XZ plane, where the inertia out of the plane its frames lack is not used", parity);

        // in full 3D that inertia acts: the document is refused by name, nothing is made up for it
        var document = locked.Edit();
        document.SetSettings(locked.Settings with { ActiveDofs = Native.DofMask.Full3D });
        var spatial = document.Commit().Document;
        var compiled = ModelCompiler.Compile(spatial);
        var refused = compiled.Model is { } model ? StaticSolver.Solve(model, spatial.LoadCases.Values.Where(item => item.Kind != Native.CaseKind.ModalEigen).Select(item => item.Id).Take(1)).Values.Single() : null;
        var named = refused?.Issues.Where(issue => issue.Code == "static.section.stiffness").Select(issue => spatial.Frames[issue.ObjectId].Name).Order(StringComparer.Ordinal).ToArray() ?? [];
        Check(refused is { Success: false } && named.SequenceEqual(without),
            $"the same document in full 3D is refused, frame by frame, for the inertia it lacks ({named.Length} frames named, the {without.Length} without it); nothing is solved with a made-up one");
    }

    private static void Model(string description, Action<MainViewModel> load)
    {
        var vm = EtabsImportChecks.NewViewModel(out _);
        load(vm);
        Report(description, Compare(vm));
    }

    private static void Report(string description, Parity parity)
    {
        const double tolerance = 1e-8;
        Check(parity.Cases > 0 && parity.Displacement < tolerance && parity.Reaction < tolerance && parity.CableForce < tolerance && parity.FrameForce < tolerance,
            $"{description}: Run FEM Analysis on its native document gives what the run gave before it read the document, in {parity.Cases} result(s) — " +
            $"{parity.Values:N0} values; largest relative difference: joint displacements {parity.Displacement:0.#E+0}, support reactions {parity.Reaction:0.#E+0}, " +
            $"cable forces {parity.CableForce:0.#E+0}, frame end forces and moments {parity.FrameForce:0.#E+0}");
    }

    /// <summary>Runs the model of <paramref name="vm"/> — Run FEM Analysis, which solves the model's native document since
    /// 2026-10-14 — and analyses the same model on the path the run took before (<see cref="MainViewModel.LegacyRunInputs"/>:
    /// the application's own description handed to <see cref="FemStudyService.Analyze"/>, the path <c>--general-solver</c>
    /// validated against the archived planar solver); every case and combination of the two, value for value.</summary>
    internal static Parity Compare(MainViewModel vm)
    {
        var run = FemModelFileChecks.Analyze(vm).SingleOrDefault() ?? throw new Exception($"FAIL: the model does not run ({vm.FemProgressText})");
        if (run.Native is null) throw new Exception("FAIL: Run FEM Analysis did not solve the model's native document");
        var inputs = vm.LegacyRunInputs();
        var before = new FemStudyService().Analyze(vm.State.Baseline, [], [], [], inputs.Supports, [inputs.Model], inputs.Properties, null, default,
            vm.FemTargetFitPercent, vm.FemTargetFitIterations, vm.FemFrameMeshSegments, inputs.Plan, vm.FemCableSag, null, vm.FemActiveDofs, vm.FemCableDofs, inputs.Restraints)
            .SingleOrDefault() ?? throw new Exception("FAIL: the model does not run on the path the run took before");
        var results = run.Results ?? throw new Exception("FAIL: the run has no results by case");
        var reference = before.Results ?? throw new Exception("FAIL: the earlier path has no results by case");
        var missing = reference.Keys.Where(name => !results.ContainsKey(name)).Concat(results.Keys.Where(name => !reference.ContainsKey(name))).ToArray();
        if (missing.Length > 0) throw new Exception("FAIL: the two paths do not give the same results: " + string.Join(", ", missing));

        int values = 0;
        double displacement = 0, reaction = 0, cableForce = 0, frameForce = 0;
        foreach (var (name, now) in results)
        {
            var then = reference[name];
            // the largest difference over the largest value: one measure for millimetres and for thousands of kN
            double Worst(IEnumerable<(double Then, double Now)> pairs)
            {
                var list = pairs.ToArray();
                values += list.Length;
                double scale = list.Select(pair => Math.Abs(pair.Then)).DefaultIfEmpty().Max();
                return list.Select(pair => Math.Abs(pair.Then - pair.Now)).DefaultIfEmpty().Max() / Math.Max(scale, 1e-9);
            }
            var joints = now.Joints.ToDictionary(joint => joint.PointName);
            displacement = Math.Max(displacement, Worst(then.Joints.SelectMany(joint =>
                new[] { (joint.UXM, joints[joint.PointName].UXM), (joint.UZM, joints[joint.PointName].UZM), (joint.RotationRad, joints[joint.PointName].RotationRad) })));
            if (!then.SupportReactions.Select(item => item.PointName).SequenceEqual(now.SupportReactions.Select(item => item.PointName)))
                throw new Exception($"FAIL: '{name}': the supports differ — {string.Join(", ", then.SupportReactions.Select(item => item.PointName))} against {string.Join(", ", now.SupportReactions.Select(item => item.PointName))}");
            reaction = Math.Max(reaction, Worst(then.SupportReactions.Zip(now.SupportReactions).SelectMany(pair =>
                new[] { (pair.First.ReactionXKN, pair.Second.ReactionXKN), (pair.First.ReactionZKN, pair.Second.ReactionZKN), (pair.First.MomentYKNm, pair.Second.MomentYKNm) })));
            var cables = now.Cables.ToDictionary(cable => cable.MemberName);
            cableForce = Math.Max(cableForce, Worst(then.Cables.Select(cable => (cable.AxialForceKN, cables[cable.MemberName].AxialForceKN))));
            var frames = now.Frames.ToDictionary(frame => frame.MemberName);
            frameForce = Math.Max(frameForce, Worst(then.Frames.SelectMany(frame =>
            {
                var other = frames[frame.MemberName];
                return new[]
                {
                    (frame.AxialStartKN, other.AxialStartKN), (frame.AxialEndKN, other.AxialEndKN), (frame.ShearStartKN, other.ShearStartKN),
                    (frame.ShearEndKN, other.ShearEndKN), (frame.MomentStartKNm, other.MomentStartKNm), (frame.MomentEndKNm, other.MomentEndKNm),
                };
            })));
        }
        return new(results.Count, values, displacement, reaction, cableForce, frameForce);
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
