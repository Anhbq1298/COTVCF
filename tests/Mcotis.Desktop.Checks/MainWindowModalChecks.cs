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

        // participation: every row is a share of the free mass, and the inventory states member self mass
        var cumulative = vm.ModalResults.Participation.Where(item => item.Direction == "UZ").Select(item => item.Cumulative ?? 0).ToArray();
        Check(cumulative.Zip(cumulative.Skip(1)).All(pair => pair.Second >= pair.First - 1e-9) && cumulative[^1] <= 100 + 1e-6 &&
              vm.ModalResults.ModalMass.First().Item == "Member self mass" && vm.ModalResults.ModalMass.First().Value > 0,
            $"participation accumulates to at most 100% (UZ {cumulative[^1]:0.0}% in 6 modes); the member self mass is listed ({vm.ModalResults.ModalMass.First().Value:N1} t)");

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

        vm.SelectedFemResultName = run.Native!.Static.Keys.First();
        var selectedNative = run.Native.Static[vm.SelectedFemResultName];
        var firstJoint = selectedNative.Joints.First();
        var jointRow = vm.NativeJointResultRows.Single(item => item.Joint == run.Native.Names[firstJoint.PointId]);
        Check(vm.HasNativeStaticResults && vm.NativeJointResultRows.Count > 0 && vm.NativeFrameStationResultRows.Count > 0 &&
              jointRow.UY == firstJoint.Displacements[1] && jointRow.RX == firstJoint.Displacements[3] && jointRow.MZ == firstJoint.Reactions[5] &&
              vm.NativeFrameStationResultRows.All(item => double.IsFinite(item.F1) && double.IsFinite(item.M3)),
            "Results Table exposes the run's six global joint DOFs and six local frame components without putting them in the planar columns");

        // the mode drawn on the model is a relative amplitude, said as such; any static result takes it away
        vm.ModalResults.SelectedMode = 2;
        vm.ShowModeShape();
        var drawn = vm.SharedViewerFemResult;
        bool shape = vm.SharedViewerIsModeShape && vm.SharedViewerFemResultMode == FemResultMode.DeflectedShape &&
                     drawn is not null && drawn.Joints.Count == vm.ModalResults.ModeShape.Count();
        vm.ShowFemResult(FemResultMode.BendingMoment);
        Check(shape && !vm.SharedViewerIsModeShape && ReferenceEquals(vm.SharedViewerFemResult, vm.SelectedFemResult),
            "Show Mode Shape draws mode 2 on the model as a mass-normalized eigenvector; showing a static result replaces it");

        // The same picker offers static deflections and solved modal cases, then asks for a mode number.
        var picker = new ShowResultsWindow(vm, _ => { }, ShowResultPickerKind.Deflection);
        var modalChoice = picker.ShapeCasePicker.Items.Cast<object>().Single(item => item.ToString() == "MODES (modal)");
        Check(picker.ShapePicker.Visibility == System.Windows.Visibility.Visible &&
              picker.ShapeCasePicker.Items.Count == vm.FemResultNames.Count + 1,
            "Show Deformed Shape offers static and modal cases in one case dropdown");
        picker.ShapeCasePicker.SelectedItem = modalChoice;
        Check(picker.ModePicker.Visibility == System.Windows.Visibility.Visible && picker.ModeNumberPicker.Items.Count == 6,
            "selecting a modal case asks which mode number to show");
        picker.ModeNumberPicker.SelectedItem = 2;
        picker.ApplySelection();
        Check(vm.SharedViewerIsModeShape && vm.ModalResults.SelectedMode == 2 && vm.SharedViewerFemResult is not null,
            "Show Deformed Shape applies the chosen modal mode to the shared viewer");
        picker.ShapeCasePicker.SelectedItem = picker.ShapeCasePicker.Items.Cast<object>().First(item => item.ToString() == vm.FemResultNames[0]);
        picker.ApplySelection();
        Check(!vm.SharedViewerIsModeShape && vm.FemResultMode == FemResultMode.DeflectedShape,
            "the same dropdown returns to a static deformed shape");

        var stressed = options with { StiffnessSourceCase = run.Results!.Keys.First() };
        Check(stressed.NotSolvedYet("MODAL-STATE") is null,
            "Load Case Data offers a modal case using the stiffness of a converged static case");

        CheckFrameCableRun();
    }

    private static void CheckFrameCableRun()
    {
        var editor = ModelDocument.Create().Edit();
        var material = ObjectId.New(); var frameSection = ObjectId.New(); var cableSection = ObjectId.New();
        var root = ObjectId.New(); var tip = ObjectId.New(); var pattern = ObjectId.New();
        var staticCase = ObjectId.New(); var modalCase = ObjectId.New(); var massSource = ObjectId.New();
        editor.PutMaterial(new(material, "Steel", MaterialType.Steel, 200_000_000, .3, 0, 0));
        editor.PutFrameSection(new(frameSection, "Frame section", material, FrameSectionShape.General, new(.02, .0005, .0005, .00005)));
        editor.PutCableSection(new(cableSection, "Stay section", material, .02));
        editor.AddPoint(new(0, 0, 0), "Root", root); editor.AddPoint(new(4, 0, 0), "Tip", tip);
        editor.SetRestraint([root], Restraint.Fixed);
        editor.SetFrameSection([editor.AddFrame(root, tip, "Frame").Id], frameSection);
        editor.SetCableSection([editor.AddCable(root, tip, "Stay").Id], cableSection);
        editor.SetJointMass(new(tip, 3, 3, 3));
        editor.PutLoadPattern(new(pattern, "PULL", LoadPatternType.Other));
        editor.PutLoad(new PointLoad(ObjectId.New(), pattern, tip, new(10_000, 0, 0)));
        editor.PutLoadCase(new(staticCase, "PULL", CaseKind.NonlinearStatic, [new(pattern, 1)]) { Run = false });
        editor.PutMassSource(new(massSource, "MS", IncludeSelfMass: false, IncludeAdditionalMass: true));
        editor.PutLoadCase(new(modalCase, "MODES", CaseKind.ModalEigen, [])
            { StiffnessSourceCaseId = staticCase, ModalOptions = new(massSource, 3) });
        editor.SetSettings(AnalysisSettings.Default with { ActiveDofs = DofMask.Full3D, CableSag = false });

        var vm = EtabsImportChecks.NewViewModel(out _);
        var warnings = new List<string>();
        vm.WarningSink = (_, body) => warnings.Add(body);
        Console.WriteLine("INFO: opening the Full-3D frame-and-cable modal fixture");
        vm.OpenNativeDocument(editor.Commit().Document, "Frame and cable", null,
            LegacyNativeDocumentMigration.Source.Imported("Mcotis native model"));
        Console.WriteLine("INFO: running its modal case and static prerequisite");
        var run = FemModelFileChecks.Analyze(vm).SingleOrDefault()
            ?? throw new Exception("FAIL: frame-and-cable modal run produced no result: " + vm.FemProgressText + " | " + string.Join(" | ", warnings));
        var staticState = run.Native!.Static["PULL"];
        var modes = run.Native.Modal["MODES"];
        Check(staticState.Cables.Single().Tension > 0 && modes.Modes.Length == 3 && modes.Modes.All(mode => mode.FrequencyHz > 0) &&
              modes.StiffnessProducerCaseId == staticState.LoadCaseId,
            $"main-window Run FEM Analysis solves the unticked PULL prerequisite before a Full-3D frame-and-cable modal case " +
            $"(T={staticState.Cables.Single().Tension:G6}, modes={modes.Modes.Length}, frequencies={string.Join(",", modes.Modes.Select(mode => mode.FrequencyHz?.ToString("G6") ?? "none"))}, source={modes.StiffnessProducerCaseId}, static={staticState.LoadCaseId})");
        vm.ModalResults.SelectedMode = modes.Modes.First(mode => mode.Shape.Any(joint => Math.Abs(joint.UY) > 1e-9)).ModeNumber;
        vm.ShowModeShape();
        Check(vm.SharedViewerIsModeShape && vm.SharedViewerFemResult!.Joints.Any(joint => Math.Abs(joint.UYM) > 0),
            "the shared 3D viewer receives the selected mode's UY amplitude");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
