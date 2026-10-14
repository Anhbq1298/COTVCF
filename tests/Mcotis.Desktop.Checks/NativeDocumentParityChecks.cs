using System.IO;
using Mcotis.Analysis;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Etabs;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Presentation;
using Native = Mcotis.Core;

/// <summary>
/// The native document of the model open in the main window (<see cref="MainViewModel.NativeDocumentSession"/>),
/// solved as it is by the solver of the standalone system, against Run FEM Analysis of the same model.
/// <para>The run still hands the solver its own description of the model (<c>Application/FemSolver.cs</c>): each case
/// as its accumulated loads, the fit of the cable target forces as a stage of its own, a cable's loads at its two
/// ends. The document says the same model the way a user would define it: load patterns, cases that continue from
/// one another, target forces in their pattern, self-weight by the pattern's multiplier
/// (<see cref="LegacyNativeDocumentMigration"/>). The window is to work on the document; this is the evidence that
/// what is analysed is then the same — every static case of every model here, to round-off.</para>
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

        // What is not the same yet. The model imported from the SAP2000 stand-in has frames whose sections hold no
        // bending inertia out of the plane (the fixture gives none). Run FEM Analysis solves it in its plane, where
        // that inertia is not used; the standalone solver asks every frame for both, so the document has no result.
        // The run cannot move onto the document for such a model until one of the two changes.
        var imported = SapImportChecks.ImportedViewModel();
        var run = FemModelFileChecks.Analyze(imported).SingleOrDefault();
        var document = imported.NativeDocumentSession.Document;
        var compiled = ModelCompiler.Compile(document);
        var refused = compiled.Model is { } model
            ? StaticSolver.Solve(model, document.LoadCases.Values.Where(item => run?.Results?.ContainsKey(item.Name) == true).Select(item => item.Id).Take(1)).Values.Single()
            : null;
        var without = refused?.Issues.Where(issue => issue.Code == "static.section.stiffness").Select(issue => document.Frames[issue.ObjectId].Name).ToArray() ?? [];
        Check(run is not null && imported.NativeDocumentProblem is null && refused is { Success: false } && without.Length > 0 &&
              refused.Issues.Where(issue => issue.Severity == Native.ValidationSeverity.Error).All(issue => issue.Code == "static.section.stiffness") &&
              without.All(name => imported.FemSectionProperties.Single(property => property.MemberName == name) is { OutOfPlaneInertiaM4: null or <= 0 }),
            $"known gap — the model imported from SAP2000 runs in its plane, and its native document is refused by the standalone solver for one reason only: " +
            $"{without.Length} frame(s) hold no bending inertia out of the plane ({string.Join(", ", without.Take(3))}{(without.Length > 3 ? ", …" : "")}); nothing is solved with a made-up one");
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
            $"{description}: its native document, solved by the standalone solver, gives the results of Run FEM Analysis in {parity.Cases} static case(s) — " +
            $"{parity.Values:N0} values; largest relative difference: joint displacements {parity.Displacement:0.#E+0}, support reactions {parity.Reaction:0.#E+0}, " +
            $"cable forces {parity.CableForce:0.#E+0}, frame end forces and moments {parity.FrameForce:0.#E+0}");
    }

    /// <summary>Runs the model of <paramref name="vm"/> and solves the native document that was locked for that run.</summary>
    internal static Parity Compare(MainViewModel vm)
    {
        var run = FemModelFileChecks.Analyze(vm).SingleOrDefault() ?? throw new Exception($"FAIL: the model does not run ({vm.FemProgressText})");
        var results = run.Results ?? new Dictionary<string, FemAnalysisResult>(StringComparer.Ordinal) { ["Default"] = run.Result };
        if (vm.NativeDocumentProblem is { } problem) throw new Exception("FAIL: the model has no native document — " + problem);
        var document = vm.NativeDocumentSession.Document;
        var compiled = ModelCompiler.Compile(document);
        if (compiled.Model is not { } model)
            throw new Exception("FAIL: the native document does not compile — " + string.Join(" | ", compiled.Issues.Where(issue => issue.Severity == Native.ValidationSeverity.Error).Take(3).Select(issue => issue.Message)));

        // every result of the run is a case of the document or one of the run's combinations (a superposition of cases)
        var cases = document.LoadCases.Values.Where(item => results.ContainsKey(item.Name)).ToArray();
        var combinations = vm.FemCombinationRows.Select(row => row.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = results.Keys.Where(name => cases.All(item => item.Name != name) && !combinations.Contains(name)).ToArray();
        if (unknown.Length > 0) throw new Exception("FAIL: the native document has no load case for " + string.Join(", ", unknown));

        // as the run: the same limits, the closest fit accepted, a movement the loads do not act on held
        var outcomes = StaticSolver.Solve(model, cases.Select(item => item.Id),
            new StaticOptions(MaximumFullDofs: 6000, MaximumFreeDofs: 3000, AcceptClosestFit: true, HoldUnloadedMechanisms: true));
        var pointNames = document.Points.Values.ToDictionary(point => point.Id, point => point.Name);
        var cableNames = document.Cables.Values.ToDictionary(cable => cable.Id, cable => cable.Name);
        var frameNames = document.Frames.Values.ToDictionary(frame => frame.Id, frame => frame.Name);
        int values = 0;
        double displacement = 0, reaction = 0, cableForce = 0, frameForce = 0;
        foreach (var item in cases)
        {
            var legacy = results[item.Name];
            var solved = outcomes[item.Id].Result
                ?? throw new Exception($"FAIL: case '{item.Name}' of the native document has no result — {string.Join(" | ", outcomes[item.Id].Issues.Take(3).Select(issue => issue.Message))}");
            var joints = solved.Joints.ToDictionary(joint => pointNames[joint.PointId]);
            var cables = solved.Cables.ToDictionary(cable => cableNames[cable.ParentObjectId]);
            var frames = solved.Frames.ToDictionary(frame => frameNames[frame.ParentObjectId]);

            // the largest difference over the largest value: one measure for millimetres and for thousands of kN
            double Worst(IEnumerable<(double Run, double Document)> pairs)
            {
                var list = pairs.ToArray();
                values += list.Length;
                double scale = list.Select(pair => Math.Abs(pair.Run)).DefaultIfEmpty().Max();
                return list.Select(pair => Math.Abs(pair.Run - pair.Document)).DefaultIfEmpty().Max() / Math.Max(scale, 1e-9);
            }
            displacement = Math.Max(displacement, Worst(legacy.Joints.SelectMany(joint =>
            {
                var moved = joints[joint.PointName].Displacements;
                // the application's slope is the opposite of the rotation about global Y
                return new[] { (joint.UXM, moved[0]), (joint.UZM, moved[2]), (joint.RotationRad, -moved[4]) };
            })));
            reaction = Math.Max(reaction, Worst(legacy.SupportReactions.SelectMany(support =>
            {
                var held = joints[support.PointName].Reactions;
                return new[] { (support.ReactionXKN, held[0]), (support.ReactionZKN, held[2]) };
            })));
            cableForce = Math.Max(cableForce, Worst(legacy.Cables.Select(cable => (cable.AxialForceKN, cables[cable.MemberName].Tension))));
            // frame ends by size: the axial force, and the moment in the plane (the document's frame may turn its local axes)
            frameForce = Math.Max(frameForce, Worst(legacy.Frames.SelectMany(frame =>
            {
                var ends = frames[frame.MemberName].LocalEndActions;
                return new[]
                {
                    (Math.Abs(frame.AxialStartKN), Math.Abs(ends[0])), (Math.Abs(frame.AxialEndKN), Math.Abs(ends[6])),
                    (Math.Abs(frame.MomentStartKNm), Math.Sqrt(ends[4] * ends[4] + ends[5] * ends[5])),
                    (Math.Abs(frame.MomentEndKNm), Math.Sqrt(ends[10] * ends[10] + ends[11] * ends[11])),
                };
            })));
        }
        return new(cases.Length, values, displacement, reaction, cableForce, frameForce);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
