using Mcotis.Analysis;
using Mcotis.Core;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Presentation;

/// <summary>
/// A modal case run from the main window (task of 2026-10-14, M1): Run FEM Analysis solves the model's native document,
/// and with it the modal cases of the Load Cases table. What the main window shows is what the modal solver gives for
/// that document — the solver validated against closed forms and OpenSeesPy (docs/analysis/modal-eigen-verification.md)
/// — with the static results of the run unchanged; a mode is drawn as a relative amplitude, and refusals stay explicit.
/// <c>--main-modal</c>, and in <c>--full</c>.
/// </summary>
internal static class MainWindowModalChecks
{
    public static void Run()
    {
        // the tied arch system: frames only, its sections with their mass
        var before = EtabsImportChecks.NewViewModel(out _);
        before.LoadTasModel();
        var staticOnly = FemModelFileChecks.Analyze(before).Single();

        var vm = EtabsImportChecks.NewViewModel(out _);
        vm.LoadTasModel();
        var warnings = new List<string>();
        vm.WarningSink = (title, body) => warnings.Add(body);
        var options = new FemCaseOptions(CaseKind.ModalEigen, MassSource: MainViewModel.DefaultMassSourceName, RequestedModes: 6);
        // a new case in Load Case Data (ETABS's own MODAL row is another program's case, kept as not supported)
        var row = vm.SaveFemLoadCase(null, "MODES", null, [], options);
        Check(row is { Run: true } && row.Options.NotSolvedYet(row.Name) is null,
            "Load Case Data: a modal case on the unstressed stiffness is available in the main window, set to run" + (warnings.Count > 0 ? " — " + string.Join(" | ", warnings) : $" (row {row?.Run}, {vm.Message})"));

        var run = FemModelFileChecks.Analyze(vm).SingleOrDefault() ?? throw new Exception("FAIL: the run with the modal case has no result — " + vm.FemProgressText);
        var modal = run.Native?.Modal.GetValueOrDefault("MODES");
        Check(modal is { Modes.Length: 6 } && modal.Modes.All(mode => mode.FrequencyHz is > 0 && mode.PeriodSeconds is > 0 && mode.ScaledResidual < 1e-8) &&
              vm.HasFemModalResults && vm.ModalResults.CaseNames.SequenceEqual(["MODES"]) && vm.ModalResults.Modes.Count() == 6,
            $"Run FEM Analysis solves the modal case with the static ones: 6 positive modes ({string.Join(", ", modal?.Modes.Take(3).Select(mode => $"{mode.FrequencyHz:0.###} Hz") ?? [])} …), shown under Result → Modal Results");

        // the main window shows what the modal solver gives for the model's document, solved directly
        var compiled = ModelCompiler.Compile(vm.NativeDocumentSession.Document);
        var direct = ModalSolver.Solve(compiled.Model!, vm.NativeDocumentSession.Document.LoadCases.Values.Single(item => item.Name == "MODES").Id);
        Check(direct.Success && direct.Result!.Modes.Zip(modal!.Modes).All(pair =>
                  Math.Abs(pair.First.EigenvaluePerSecondSquared - pair.Second.EigenvaluePerSecondSquared) <= 1e-9 * pair.First.EigenvaluePerSecondSquared),
            "the modes shown are those of the modal solver on the model's native document, solved directly");

        // participation: every row is a share of the free mass, and the inventory states the frame self mass
        var cumulative = vm.ModalResults.Participation.Where(item => item.Direction == "UZ").Select(item => item.Cumulative ?? 0).ToArray();
        Check(cumulative.Zip(cumulative.Skip(1)).All(pair => pair.Second >= pair.First - 1e-9) && cumulative[^1] <= 100 + 1e-6 &&
              vm.ModalResults.ModalMass.First().Item == "Frame self mass" && vm.ModalResults.ModalMass.First().Value > 0,
            $"participation accumulates to at most 100% (UZ {cumulative[^1]:0.0}% in 6 modes); the frame self mass is listed ({vm.ModalResults.ModalMass.First().Value:N1} t)");

        // the static cases are those of a run without the modal case, value for value
        var worst = staticOnly.Results!.Keys.Max(name =>
        {
            var (a, b) = (staticOnly.Results[name], run.Results![name]);
            return a.Joints.Zip(b.Joints).Max(pair => Math.Abs(pair.First.UZM - pair.Second.UZM));
        });
        // two loads of the model are two documents with their own identities: equal to round-off, the solver orders its equations by identity
        double scale = staticOnly.Results!.Values.SelectMany(result => result.Joints).Max(joint => Math.Abs(joint.UZM));
        Check(worst <= 1e-9 * scale && !run.Results!.ContainsKey("MODES") && !vm.FemResultNames.Contains("MODES"),
            "the static results of the run are those of a run without the modal case; the modal case is not offered as a static result");

        // the mode drawn on the model is a relative amplitude, said as such; any static result takes it away
        vm.ModalResults.SelectedMode = 2;
        vm.ShowModeShape();
        var drawn = vm.SharedViewerFemResult;
        bool shape = vm.SharedViewerIsModeShape && vm.SharedViewerFemResultMode == FemResultMode.DeflectedShape &&
                     drawn is not null && drawn.Joints.Count == vm.ModalResults.ModeShape.Count();
        vm.ShowFemResult(FemResultMode.BendingMoment);
        Check(shape && !vm.SharedViewerIsModeShape && ReferenceEquals(vm.SharedViewerFemResult, vm.SelectedFemResult),
            "Show Mode Shape draws mode 2 on the model as a mass-normalized eigenvector; showing a static result replaces it");

        // what is not solved stays refused, with the reason: modes of a stressed state
        var stressed = options with { StiffnessSourceCase = run.Results!.Keys.First() };
        Check(stressed.NotSolvedYet("MODAL-PD") is { } reason && reason.Contains("not substituted", StringComparison.Ordinal),
            "a modal case on the stiffness at the end of another case is still refused, with the reason");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
