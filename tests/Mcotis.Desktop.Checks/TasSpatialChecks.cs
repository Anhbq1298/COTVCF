using Mcotis.Analysis;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Etabs;
using Mcotis.Desktop.Presentation;
using Native = Mcotis.Core;

/// <summary>
/// The tied arch system in full 3D (task of 2026-10-14, M9 of the main-window migration): a plane frame held out of
/// its plane by its own data — the sections' bending inertia about their other axis and torsion constant, the
/// restraints of the source model and its bearings — never by the solver.
/// </summary>
internal static class TasSpatialChecks
{
    public static void Run()
    {
        var checksStartingDofs = MainViewModel.StartingDofs;
        MainViewModel vm;
        try
        {
            MainViewModel.StartingDofs = Program.ProductStartingDofs;
            vm = EtabsImportChecks.NewViewModel(out _);
            vm.LoadStartupModel();
        }
        finally { MainViewModel.StartingDofs = checksStartingDofs; }

        var document = vm.NativeDocumentSession.Document;
        Check(Program.ProductStartingDofs == (Native.DofMask.Full3D, Native.DofMask.Translations) &&
              vm.TiedArchParameters == TiedArchParameters.Default && vm.FemActiveDofs == Native.DofMask.Full3D &&
              document.Settings.ActiveDofs == Native.DofMask.Full3D && document.Points.Count == 30 && document.Frames.Count == 59,
            "Mcotis starts on the tied arch system as its central native document, ready to analyse in Full 3D");

        var app = System.Windows.Application.Current ?? new System.Windows.Application();
        if (!app.Resources.MergedDictionaries.Any(dictionary => dictionary.Source?.OriginalString.Contains("Theme.xaml", StringComparison.Ordinal) == true))
            app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                { Source = new Uri("/Mcotis;component/Presentation/Theme.xaml", UriKind.Relative) });
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.ShowStartupView();
            Check(window.SharedViewer.CurrentPlane == "3D", "the startup model opens in the 3D view");
        }
        finally { window.DataContext = null; window.Close(); }

        var uyOnly = new Native.Restraint(false, true, false, false, false, false);
        var assignments = document.FrameAssignments.Values.ToArray();
        Check(document.FrameSections.Values.All(section => section.Properties is { Inertia22: > 0, TorsionConstant: > 0 }) &&
              assignments.Any(assignment => assignment.Releases.Start.Moment2 || assignment.Releases.End.Moment2) &&
              document.Restraints.Values.Count(restraint => restraint == Native.Restraint.Pinned) == 2 &&
              document.Restraints.Values.Count(restraint => restraint == uyOnly) == 24,
            "the source model supplies every frame's other-axis inertia and torsion, its out-of-plane end releases, " +
            "two pinned bases and 24 bearing restraints in UY; no solver restraint is substituted");

        var compiled = ModelCompiler.Compile(document);
        Check(compiled.Model is not null && compiled.Issues.All(issue => issue.Severity != Native.ValidationSeverity.Error),
            "the tied arch compiles in Full 3D from those model properties");
        var cases = document.LoadCases.Values.Where(item => item.Kind != Native.CaseKind.ModalEigen).ToArray();
        var outcomes = StaticSolver.Solve(compiled.Model!, cases.Select(item => item.Id),
            new StaticOptions(MaximumFullDofs: 6000, MaximumFreeDofs: 3000, AcceptClosestFit: true, HoldUnloadedMechanisms: false));
        Check(outcomes.Count == cases.Length && outcomes.Values.All(outcome => outcome.Result is { Diagnostics.Stability: { ZeroStiffnessModes: 0, MovementsHeld: 0 } }),
            $"all {cases.Length} static cases solve in Full 3D without holding an unloaded mechanism");

        var spatialRun = FemModelFileChecks.Analyze(vm).SingleOrDefault()
            ?? throw new Exception($"FAIL: the startup TAS model did not run ({vm.FemMonitor.Message})");
        Check(vm.FemMonitor.Status == FemRunStatus.Completed && vm.FemMonitor.Cases.All(item => item.Status == FemCaseStatus.Converged),
            "Run FEM Analysis completes every TAS case without an instability warning");
        var outOfPlane = spatialRun.Native!.Static.Values.SelectMany(result => result.Joints)
            .SelectMany(joint => new[] { joint.Displacements[1], joint.Displacements[3], joint.Displacements[5] })
            .Select(Math.Abs).DefaultIfEmpty().Max();
        Check(outOfPlane < 1e-12,
            $"the planar source and its in-plane loads produce no artificial out-of-plane movement ({outOfPlane:0.#E+0} m/rad)");

        var planar = EtabsImportChecks.NewViewModel(out _);
        planar.LoadTasModel();
        planar.ApplyPlanarAnalysisSettings(planar.PlanarAnalysisSettings with { ActiveDofs = Native.DofMask.XZ });
        var planarRun = FemModelFileChecks.Analyze(planar).Single();
        var names = planarRun.Results!.Keys.Order(StringComparer.Ordinal).ToArray();
        Check(names.SequenceEqual(spatialRun.Results!.Keys.Order(StringComparer.Ordinal)),
            "Full 3D and XZ solve the same TAS cases and combinations");
        var gaps = names.Select(name => RunMonitorChecks.Difference(planarRun.Results![name], spatialRun.Results![name])).ToArray();
        var moved = gaps.Select(gap => gap.Moved).DefaultIfEmpty().Max();
        var force = gaps.Select(gap => gap.Force).DefaultIfEmpty().Max();
        Check(moved < 1e-9 && force < 1e-4,
            $"Full 3D preserves the frozen in-plane TAS results (largest movement gap {moved:0.#E+0}, force gap {force:0.#E+0})");

        ProductionBaselineChecks.RunTasInFullThreeD();
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
