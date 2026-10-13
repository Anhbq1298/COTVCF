using System.IO;
using System.Windows;
using Mcotis.Analysis.Cases;
using Mcotis.Core;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Presentation;

/// <summary>The load-case form and the run plan of the main window (docs/analysis/LOAD_CASE_STATE_REFACTOR_PLAN.md,
/// PR-03): the load tables are planned by the planner of the native model; the case a case continues from and the
/// case whose stiffness it uses stay two things, and the second is never turned into loads; the cases that were
/// solved before are solved as before, in the order of the table, with the same numbers; Load Case Data defines a
/// case of every type and gets stuck on none; and a type that is not solved yet is refused with its reason.</summary>
internal static class CaseEditorChecks
{
    /// <param name="windows">Also Load Case Data and Set Load Cases to Run themselves; they need the application's
    /// resources, which the flag --case-editor loads.</param>
    public static void Run(bool windows)
    {
        RunPlan();
        EstablishedRun();
        if (windows) Form();
        PrerequisiteInTheApplication();
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }

    private static Guid Id(int number) => Guid.Parse($"00000000-0000-0000-0300-{number:000000000000}");

    private static FemLoadPlan Build(IReadOnlyList<FemPatternRow> patterns, IReadOnlyList<FemCaseRow> cases, IReadOnlyList<FemCombinationRow> combinations) =>
        FemLoadPlan.Build(patterns, cases, combinations, SapFemLoading.Empty, new Dictionary<string, FemSectionProperties>(), GeometryModel.Empty, [], [], []);

    private static string Refused(Action action, string description)
    {
        try { action(); }
        catch (InvalidOperationException refused) { return refused.Message; }
        throw new Exception("FAIL: " + description + " — it was accepted");
    }

    /// <summary>The graph of the plan's worked model: PDELTA_GRAVITY continues NL_PRET; LIN_WIND uses its stiffness.</summary>
    private static void RunPlan()
    {
        var dead = new FemPatternRow("DEAD", "Dead", 0, "", "");
        var wind = new FemPatternRow("WIND", "Other", 0, "", "");
        var pret = new FemCaseRow("NL_PRET", "Nonlinear Static", "1 DEAD", "", false, true, "App", "") { Id = Id(1) };
        var pdelta = new FemCaseRow("PDELTA_GRAVITY", "Initial P-Delta", "1 DEAD", "NL_PRET", false, true, "App", "")
            { Id = Id(2), Options = new(CaseKind.InitialPDelta, GeometricNonlinearity.PDelta) };
        var linear = new FemCaseRow("LIN_WIND", "Linear Static", "1 WIND", "", true, true, "App", "")
            { Id = Id(3), Options = new(CaseKind.LinearStatic, StiffnessSourceCase: "PDELTA_GRAVITY") };

        var plan = CaseExecutionCoordinator.Plan([pret, pdelta, linear], []);
        Check(plan.IsValid && plan.Cases.Select(item => item.Row.Name).SequenceEqual(["NL_PRET", "PDELTA_GRAVITY", "LIN_WIND"]) &&
              plan.Cases.Select(item => item.Role).SequenceEqual([CaseRunRole.Prerequisite, CaseRunRole.Prerequisite, CaseRunRole.Requested]) &&
              plan.Cases[1].Dependencies.Single() == (CaseDependencyKind.ContinueState, "NL_PRET") &&
              plan.Cases[2].Dependencies.Single() == (CaseDependencyKind.FrozenStiffness, "PDELTA_GRAVITY") &&
              plan.Cases[0].UnavailableReason is null && plan.Cases[1].UnavailableReason!.Contains("P-Delta", StringComparison.Ordinal) &&
              plan.Cases[2].UnavailableReason!.Contains("linear static", StringComparison.Ordinal) && linear.Definition == "1 WIND",
            "LIN_WIND set to run: the plan holds NL_PRET, then PDELTA_GRAVITY that continues it, then LIN_WIND on its stiffness — the first two as prerequisites, " +
            "the two references each of its own kind — and says which of them are not solved yet; LIN_WIND keeps its own load and no other");

        string refused = Refused(() => Build([dead, wind], [pret, pdelta, linear], []), "a run that needs a P-Delta case");
        Check(refused.Contains("'PDELTA_GRAVITY' is analysed with P-Delta", StringComparison.Ordinal) && refused.Contains("not replaced by a first-order analysis", StringComparison.Ordinal) &&
              !refused.Contains("LIN_WIND", StringComparison.Ordinal),
            "Run with that plan stops at the case that cannot be solved yet — the P-Delta case LIN_WIND needs — with its reason; nothing is solved in its place, " +
            "and the loads of PDELTA_GRAVITY are not put on LIN_WIND: " + refused);

        var nothing = new FemAnalysisResult([], [], [], [], 0);
        bool modalRefused = false;
        try { _ = new FemStaticCaseExecutionResult(Id(5), CaseKind.ModalEigen, nothing); }
        catch (ArgumentException) { modalRefused = true; }
        Check(modalRefused && new FemStaticCaseExecutionResult(Id(3), CaseKind.LinearStatic, nothing) is IFemCaseExecutionResult { Kind: CaseKind.LinearStatic },
            "the result of a case is of the kind of the case: a static result cannot be given to a modal case, whose result will be a record of its own");

        // a case that depends on itself through the two kinds of reference together
        var loop = new FemCaseRow("LOOP", "Linear Static", "1 WIND", "", true, true, "App", "") { Id = Id(4), Options = new(CaseKind.LinearStatic, StiffnessSourceCase: "NL_PRET") };
        var circular = new FemCaseRow("NL_PRET", "Nonlinear Static", "1 DEAD", "LOOP", false, true, "App", "") { Id = Id(1) };
        var broken = CaseExecutionCoordinator.Plan([circular, loop], []);
        Check(!broken.IsValid && broken.Cases.Count == 0 && broken.Issues.Count > 0, "cases that do not make a plan — one continues from a linear case that uses its stiffness — give no plan, and the reason: " + broken.Issues.FirstOrDefault());
    }

    /// <summary>The cases this workspace has always solved: what the run holds and in which order, and that a row
    /// outside the run has no say in it.</summary>
    private static void EstablishedRun()
    {
        FemPatternRow[] patterns = [new("DEAD", "Dead", 0, "", ""), new("LIVE", "Live", 0, "", "")];
        // identities in the opposite order of the table: the table decides the order of the run, not the identities
        FemCaseRow Case(string name, string definition, string from, bool run, int id) => new(name, FemLoadPlan.StagedCaseType, definition, from, run, true, "App", "") { Id = Id(id) };
        FemCaseRow[] Cases(bool stage, bool other) =>
        [
            Case("Stage", "1 DEAD", "", stage, 9), Case("Service", "1 LIVE", "Stage", true, 6), Case("Ultimate", "1.5 LIVE", "Stage", true, 3),
            Case("Other", "1 LIVE", "", other, 1),
        ];

        var all = Build(patterns, Cases(stage: true, other: true), []);
        Check(all.CaseNames.SequenceEqual(["Stage", "Service", "Ultimate", "Other"]) && all.PrerequisiteRemarks.Count == 0 && all.Notes.Count == 0 &&
              all.PrimaryResultName == "Stage" && all.DefaultResultName == "Other",
            "every case set to run: the run solves them in the order of the Load Cases table, whatever their identities are; none is a prerequisite");

        var some = Build(patterns, Cases(stage: false, other: false), []);
        Check(some.CaseNames.SequenceEqual(["Stage", "Service", "Ultimate"]) && some.PrerequisiteRemarks.Keys.SequenceEqual(["Stage"]) &&
              some.PrerequisiteRemarks["Stage"] == "Not set to run: solved because 'Service' and 'Ultimate' continue from it." &&
              some.Notes.Single().Contains("'Stage' is not set to run", StringComparison.Ordinal) && some.PrimaryResultName == "Service" && some.DefaultResultName == "Ultimate",
            "Stage not set to run, the two cases that continue from it set to run: Stage is part of the run all the same, in its place in the table, and the plan says why — " +
            some.PrerequisiteRemarks["Stage"] + " The result shown first is of a case that was asked for");

        var combined = Build(patterns, Cases(stage: false, other: false), [new("Envelope", "Linear Additive", "1 Service + 1 Other", true, true, "App", "")]);
        Check(combined.CaseNames.SequenceEqual(["Stage", "Service", "Ultimate", "Other"]) && combined.CombinationNames.SequenceEqual(["Envelope"]) &&
              combined.PrerequisiteRemarks["Other"] == "Not set to run: solved because load combination 'Envelope' adds it up." && combined.DefaultResultName == "Envelope",
            "a case only a combination needs is solved as it always was, and the plan says why: " + combined.PrerequisiteRemarks["Other"]);

        // rows that are not part of the run: a case on a load pattern the model does not have, a combination of nothing known
        FemCaseRow[] withStray = [.. Cases(stage: true, other: false), Case("Stray", "1 GHOST", "", false, 12)];
        FemCombinationRow[] strayCombination = [new("Loose", "Linear Additive", "1 Nowhere", false, true, "App", "")];
        var untouched = Build(patterns, withStray, strayCombination);
        string ghost = Refused(() => Build(patterns, [.. Cases(stage: true, other: false), Case("Stray", "1 GHOST", "", true, 12)], []), "a case on an unknown load pattern, set to run");
        Check(untouched.CaseNames.SequenceEqual(["Stage", "Service", "Ultimate"]) && ghost.Contains("'Stray' refers to unknown load pattern 'GHOST'", StringComparison.Ordinal),
            "a case on a load pattern the model does not have, and a combination of a case it does not have, keep nothing from running while they are not part of the run; set to run, the case is refused by name: " + ghost);
    }

    /// <summary>Load Case Data on the default model: every type can be defined, a type that is not solved yet says so,
    /// and a field of another type never holds the form back.</summary>
    private static void Form()
    {
        var (vm, _, _, warnings) = FemModelFileChecks.DefaultModelApp();
        var (pretension, service, ultimate) = (vm.FemCaseRows[0], vm.FemCaseRows[1], vm.FemCaseRows[2]);

        var window = new LoadCaseWindow(vm, service);
        Check(window.Kind == CaseKind.NonlinearStatic && window.Reference == pretension.Name && window.ReferenceLabel.Text == "Continue from" &&
              window.GeometricBox.Visibility == Visibility.Visible && window.PurposeBox.Visibility == Visibility.Visible &&
              window.ControlsPanel.Visibility == Visibility.Visible && window.ControlsBox.IsChecked == false &&
              window.ModalPanel.Visibility == Visibility.Collapsed && window.AvailabilityPanel.Visibility == Visibility.Collapsed && window.Problem is null &&
              window.TypeBox.Items.Cast<string>().SequenceEqual(["Linear Static", "Nonlinear Static", "Initial P-Delta", "Modal (Eigenvectors)"]),
            "Load Case Data of a default case: Nonlinear Static, continuing from Pretension, with nothing of another type shown and nothing it cannot run with; Type offers the four kinds");
        // what a nonlinear case can have and a linear case cannot is set first, then the type is changed
        window.PurposeBox.SelectedIndex = 1;
        window.GeometricBox.SelectedIndex = 1;
        window.ControlsBox.IsChecked = true;
        window.StepsBox.Text = "0";
        bool held = window.Problem is not null && window.AvailabilityPanel.Visibility == Visibility.Visible && window.AvailabilityText.Text.Contains("P-Delta", StringComparison.Ordinal);
        window.Kind = CaseKind.LinearStatic;
        Check(held && window.Problem is null && window.OkButton.IsEnabled && window.ReferenceLabel.Text == "Stiffness from" && window.Reference == pretension.Name &&
              window.LoadsPanel.Visibility == Visibility.Visible && window.GeometricBox.Visibility == Visibility.Collapsed && window.PurposeBox.Visibility == Visibility.Collapsed &&
              window.ControlsPanel.Visibility == Visibility.Collapsed && window.AvailabilityPanel.Visibility == Visibility.Visible &&
              window.AvailabilityText.Text.Contains("Run does not solve it yet", StringComparison.Ordinal) && window.AvailabilityText.Text.Contains("linear static", StringComparison.Ordinal),
            "changed to Linear Static, the form shows a stiffness source and the loads, keeps the case that was named, and says the type is not solved yet; " +
            "P-Delta, the cable fit and iteration controls it held as a nonlinear case — one of them wrong — are no part of it and do not hold it back");
        Check(window.Accept() && warnings.Count == 0 && service.Options == new FemCaseOptions(CaseKind.LinearStatic, StiffnessSourceCase: pretension.Name) &&
              service.InitialCase.Length == 0 && !service.CableTargetForces && service.CaseType == "Linear Static" && service.StartsFrom == $"Stiffness of {pretension.Name}" &&
              service.Availability == FemCaseOptions.Unavailable,
            "OK saves a linear case on the stiffness of Pretension — not a nonlinear case that continues from it — and the Load Cases table shows it as that");
        window.Close();

        var producer = new LoadCaseWindow(vm, pretension) { Kind = CaseKind.ModalEigen };
        Check(producer.Problem is { } kept && kept.Contains($"'{service.Name}'", StringComparison.Ordinal) && kept.Contains($"'{ultimate.Name}'", StringComparison.Ordinal) && !producer.OkButton.IsEnabled,
            "a case others continue from or take their stiffness from cannot be made a linear or modal case under them: " + producer.Problem);
        producer.Close();

        foreach (var row in vm.FemCaseRows) row.Run = ReferenceEquals(row, service);
        var runWindow = new RunLoadCasesWindow(vm);
        var preview = runWindow.Items.ToDictionary(item => item.Name);
        Check(preview[pretension.Name] is { Role: "Prerequisite", Action: "Do Not Run", Availability: "Available" } &&
              preview[service.Name] is { Role: "Requested", Type: "Linear Static", Availability: FemCaseOptions.Unavailable } && preview[service.Name].From == pretension.Name &&
              preview[service.Name].DependsOn == $"stiffness of {pretension.Name}" && preview[ultimate.Name].Role.Length == 0 && runWindow.RunNowButton.IsEnabled &&
              runWindow.Hint.Text.Contains("1 more solved first, as a prerequisite", StringComparison.Ordinal) && runWindow.Hint.Text.Contains("is not solved yet", StringComparison.Ordinal),
            "Set Load Cases to Run shows the plan: the linear case as asked for and not solved yet, Pretension — not set to run — as its prerequisite, by its stiffness; " +
            "Run Now stays on, so that the run says why it stops — " + runWindow.Hint.Text);
        runWindow.Close();
        var runs = FemModelFileChecks.Analyze(vm);
        Check(runs.Count == 0 && vm.FemMonitor.Status == FemRunStatus.Failed && vm.FemMonitor.Message.Contains($"'{service.Name}' is linear static on the stiffness at the end of '{pretension.Name}'", StringComparison.Ordinal),
            "… and Run stops with the reason, without a result: " + vm.FemMonitor.Message);
        warnings.Clear();
        if (vm.IsFemModelLocked) vm.UnlockFemModel.Execute(null);

        // a modal case: no loads, a mass source and modes; the model has no mass source and is offered one
        var proposed = vm.NewFemLoadCase()!;
        var modal = new LoadCaseWindow(vm, proposed) { Kind = CaseKind.ModalEigen };
        modal.NameBox.Text = "MODES";
        bool carried = modal.Reference == pretension.Name;   // + Add proposes a case that continues from the pretension case
        modal.Reference = "";
        Check(carried && modal.LoadsPanel.Visibility == Visibility.Collapsed && modal.ModalPanel.Visibility == Visibility.Visible && modal.ReferenceLabel.Text == "Stiffness from" &&
              modal.MassSourceBox.Items.Cast<string>().SequenceEqual([MainViewModel.DefaultMassSourceName]) && modal.MassSource == MainViewModel.DefaultMassSourceName &&
              modal.Problem is null && vm.FemMassSourceReferences.Count == 0,
            "Modal (Eigenvectors) hides the loads and shows a stiffness source, a mass source and a number of modes; a model without a mass source is offered MS1, the self mass, and nothing is added to the model before OK");
        modal.ModesBox.Text = "0";
        string? noModes = modal.Problem;
        modal.ModesBox.Text = "6";
        modal.MassSource = null;
        string? noMass = modal.Problem;
        modal.MassSource = MainViewModel.DefaultMassSourceName;
        Check(noModes?.Contains("at least one mode", StringComparison.Ordinal) == true && noMass?.Contains("mass source", StringComparison.Ordinal) == true && modal.Problem is null,
            "… it needs at least one mode and a mass source: " + noModes + " | " + noMass);
        Check(modal.Accept() && warnings.Count == 0 && modal.Saved is { Name: "MODES", Definition: "", CaseType: "Modal (Eigenvectors)", LoadsOrMass: "MS1 · 6 modes", InitialCase: "" } saved &&
              saved.Options == new FemCaseOptions(CaseKind.ModalEigen, MassSource: "MS1", RequestedModes: 6) && vm.FemCaseRows.Contains(saved) && !saved.Run &&
              vm.FemMassSourceReferences.Single().Name == "MS1" && vm.Message.Contains("Run does not solve this type yet", StringComparison.Ordinal),
            "OK adds the modal case — no loads, MS1, 6 modes, on the unstressed stiffness — and MS1 becomes a mass source of the configuration; " +
            "a new case of a type that is not solved yet is not set to run, so the cases beside it go on running");
        modal.Close();

        // an initial P-Delta case: a nonlinear case whose geometric nonlinearity is P-Delta, that can continue another
        var pdelta = new LoadCaseWindow(vm, vm.NewFemLoadCase()!) { Kind = CaseKind.InitialPDelta };
        pdelta.NameBox.Text = "PDELTA_GRAVITY";
        pdelta.Reference = pretension.Name;
        Check(pdelta.Problem is null && pdelta.GeometricBox is { IsEnabled: false, SelectedIndex: 1 } && pdelta.PurposeBox.Visibility == Visibility.Collapsed &&
              pdelta.ReferenceLabel.Text == "Continue from" && pdelta.Accept() &&
              pdelta.Saved!.Options == new FemCaseOptions(CaseKind.InitialPDelta, GeometricNonlinearity.PDelta) && pdelta.Saved.InitialCase == pretension.Name,
            "Initial P-Delta is a nonlinear case with P-Delta fixed on, that can continue from another; it has no cable fit of its own");
        pdelta.Close();

        // the configuration with the three new kinds is saved and opened again as it was defined
        string folder = Path.Combine(Path.GetTempPath(), $"mcotis-cases-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "kinds" + FemInputDatabase.Extension);
            vm.SaveFemModelFileAsync(path).GetAwaiter().GetResult();
            var written = FemInputDatabase.Read(path).Snapshot;
            var rows = written.CaseRows.ToDictionary(row => row.Name);
            Check(warnings.Count == 0 && rows[service.Name] is { CaseKind: "LinearStatic", Type: "Linear Static", InitialCase: "", InitialCaseId: null } linear &&
                  linear.StiffnessSourceCase == pretension.Name && linear.StiffnessSourceCaseId == rows[pretension.Name].Id &&
                  rows["MODES"] is { CaseKind: "ModalEigen", Definition: "", Modal: { MassSource: "MS1", RequestedModes: 6 } } modes && modes.Modal.MassSourceId == written.MassSources.Single().Id &&
                  rows["PDELTA_GRAVITY"] is { CaseKind: "InitialPDelta" } initial && initial.InitialCaseId == rows[pretension.Name].Id &&
                  rows[pretension.Name] is { CaseKind: null, Type: FemLoadPlan.StagedCaseType } && rows[ultimate.Name] is { CaseKind: null },
                "saved, the configuration holds the cases as the form defined them — kinds, the two references by identity, the mass source — and the cases that were not touched as they were" +
                (warnings.Count > 0 ? " — " + string.Join(" | ", warnings) : ""));
            var (reopened, _, _, reopenedWarnings) = FemModelFileChecks.DefaultModelApp();
            reopened.OpenFemModelFile(path);
            Check(reopenedWarnings.Count == 0 && reopened.FemCaseRows.Select(row => (row.Name, row.Options, row.InitialCase)).SequenceEqual(vm.FemCaseRows.Select(row => (row.Name, row.Options, row.InitialCase))),
                "… and opened again, they are the same cases");
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch (IOException) { } }
    }

    /// <summary>The default model with only its last case set to run: the case it continues from is solved too, said
    /// to be a prerequisite, and the results are those of a run of everything.</summary>
    private static void PrerequisiteInTheApplication()
    {
        var (full, _, _, fullWarnings) = FemModelFileChecks.DefaultModelApp();
        var everything = FemModelFileChecks.Analyze(full).Single().Results!;
        var (vm, _, _, warnings) = FemModelFileChecks.DefaultModelApp();
        var (pretension, service, ultimate) = (vm.FemCaseRows[0].Name, vm.FemCaseRows[1].Name, vm.FemCaseRows[2].Name);
        foreach (var row in vm.FemCaseRows) row.Run = row.Name == ultimate;
        var results = FemModelFileChecks.Analyze(vm).Single().Results!;

        static double Difference(FemAnalysisResult a, FemAnalysisResult b) => a.Joints.Zip(b.Joints).Max(pair => Math.Max(Math.Abs(pair.First.UXM - pair.Second.UXM), Math.Abs(pair.First.UZM - pair.Second.UZM)));
        static double ForceDifference(FemAnalysisResult a, FemAnalysisResult b) => a.Cables.Zip(b.Cables).Max(pair => Math.Abs(pair.First.AxialForceKN - pair.Second.AxialForceKN));
        Check(fullWarnings.Count == 0 && warnings.Count == 0 && everything.Keys.SequenceEqual([pretension, service, ultimate]) && results.Keys.SequenceEqual([pretension, ultimate]) &&
              vm.SelectedFemResultName == ultimate && full.SelectedFemResultName == ultimate,
            $"the default model with only '{ultimate}' set to run: the run solves '{pretension}', which it continues from, and '{ultimate}', in the order of the table, and shows the case that was asked for");
        double moved = new[] { pretension, ultimate }.Max(name => Difference(results[name], everything[name]));
        double forces = new[] { pretension, ultimate }.Max(name => ForceDifference(results[name], everything[name]));
        Check(moved < 1e-9 && forces < 1e-6, $"… with the results of a run of every case (largest difference {moved:0.###E+0} m, {forces:0.###E+0} kN)");
        var remarked = vm.FemMonitor.Cases.Where(item => item.LogText.Contains("Not set to run", StringComparison.Ordinal)).ToArray();
        Check(remarked.Length == 1 && remarked[0].Name == pretension && remarked[0].LogText.Contains($"Not set to run: solved because '{ultimate}' continues from it.", StringComparison.Ordinal) &&
              vm.FemMonitor.ModelNotes.Contains($"'{pretension}' is not set to run", StringComparison.Ordinal),
            "… and the run monitor says of that case, in its log and in the notes of the run, that it is not set to run and why it is solved");
    }
}
