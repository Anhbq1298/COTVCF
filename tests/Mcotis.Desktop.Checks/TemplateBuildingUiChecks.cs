using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Infrastructure;
using Mcotis.Desktop.Presentation;
using Mcotis.Application;
using Mcotis.Core;

internal static class TemplateBuildingUiChecks
{
    internal static void Run()
    {
        var folder = Path.GetFullPath(Path.Combine(".work", "template-ui-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder);
        var bindings = new StringWriter();
        var listener = new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var form = new TemplateBuildingWindow();
        NativeModelWindow? window = null;
        try
        {
            form.Show(); Idle();
            Check(form.Form.XWidths.Count == 2 && form.Form.YWidths.Count == 2 && form.Form.Storeys.Count == 3,
                "TemplateBuilding opens with editable bay widths and storey height / line load tables");
            Snapshot(form, ".work/templatebuilding-ui.png");
            form.Form.BaysX = "2"; form.Form.BaysY = "1"; form.Form.StoreyCount = "2";
            Check(form.Form.UpdateTables(), "counts resize the staged input tables");
            form.Form.XWidths[0].Width = "4"; form.Form.XWidths[1].Width = "6";
            form.Form.YWidths[0].Width = "5"; form.Form.Storeys[1].Height = "4";
            form.Form.Storeys[0].LoadX = "10"; form.Form.Storeys[0].LoadY = "7";
            form.Form.Storeys[1].LoadX = "12"; form.Form.Storeys[1].LoadY = "9";
            form.Form.Storeys[0].Height = "NaN";
            Check(!form.CreateModel() && form.CreatedSession is null, "invalid height cannot create a partial model");
            form.Form.Storeys[0].Height = "3";
            Check(form.CreateModel() && form.CreatedSession!.Document.Points.Count == 18, "Create Model produces a native connected building");

            // The template's material has a mass density: γ / g until a value is typed. It used to be 0, so a modal
            // case of self mass alone had no mass at all (modal.mass.none) unless joint masses were assigned.
            var massForm = new TemplateBuildingViewModel();
            var startMass = massForm.MassDensity;
            massForm.WeightDensity = "25";
            var followed = massForm.MassDensity;
            massForm.MassDensity = "2.5";
            massForm.WeightDensity = "24";
            Check(startMass == (78.5 / TemplateBuildingViewModel.Gravity).ToString("0.#####") && followed == (25 / TemplateBuildingViewModel.Gravity).ToString("0.#####") &&
                  massForm.MassDensity == "2.5" && massForm.ReadInput().Material.MassDensity == 2.5 &&
                  new TemplateBuildingViewModel().ReadInput().Material.MassDensity == 78.5 / TemplateBuildingViewModel.Gravity,
                $"the template form has a Mass density (t/m³): {startMass} = 78.5 / 9.80665 to start with, it follows the weight density ({followed} for 25 kN/m³) until a value is typed, which is kept");
            var selfMass = new DocumentSession();
            selfMass.Execute(TemplateBuilding.Create(new TemplateBuildingViewModel().ReadInput()));
            var selfModes = new NativeModelViewModel(selfMass);
            selfModes.DefineModal(new("SelfModes", "SelfMass", IncludeSelfMass: true, IncludeAdditionalMass: false, RequestedModes: 3));
            Pump(selfModes.RunAsync()); Idle();
            Check(selfModes.Results.State == ResultState.Current && selfModes.Modes.Count() == 3 &&
                  selfModes.Modes.All(mode => mode.Frequency is > 0 && mode.Period is > 0 && mode.Residual < 1e-8),
                $"a TemplateBuilding model with its self mass only (no joint mass) gives positive modes: {string.Join(", ", selfModes.Modes.Select(mode => $"{mode.Frequency:0.###} Hz"))}");
            window = new NativeModelWindow(form.CreatedSession!);
            window.Show(); Idle();
            var vm = window.Model;
            Check(window.Viewer.Model.Points.Count == 18 && window.Viewer.Model.Members.Count == 26 && window.Viewer.CurrentPlane == "3D",
                "the native workspace displays the generated spatial geometry");
            Check(vm.Session.CanUndo && vm.Loads.Count() == 14, "generation is undoable and line loads appear in the workspace");
            var nativeMenu = (Menu)((DockPanel)window.Content).Children[0];
            Check(nativeMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "_Define")) &&
                  nativeMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "_Assign")),
                "the native workspace exposes compact Define and Assign workflows for modal cases and joint mass");
            vm.Session.Execute(DocumentCommands.Edit("Test modal mass", editor =>
            {
                foreach (var point in vm.Session.Document.Points.Values) editor.SetJointMass(new(point.Id, 1, 1, 1));
            }));
            vm.DefineModal(new("BuildingModes", "BuildingMass", false, true, 3));
            Pump(vm.RunAsync()); Idle();
            Check(vm.Results.State == ResultState.Current && vm.Modes.Count() == 3 && vm.Participation.Count() == 9 &&
                  vm.Modes.All(mode => mode.Frequency is > 0 && mode.Period is > 0 && mode.Residual < 1e-8),
                "the native modal run reports three positive frequencies, periods, residuals and directional participation rows");
            Check(vm.ModalDisplayResult is not null && vm.ModeShape.Count() == 18,
                "the selected mass-normalized eigenvector is available to both the mode-shape table and viewport adapter");
            Check(vm.ModalMass.Any(row => row.Item == "Free UX" && row.Value > 0) &&
                  vm.ModalMass.Count(row => row.Item.StartsWith("Centre of mass", StringComparison.Ordinal)) == 3,
                "the modal mass inventory states assigned/free components and the physical centre of mass");
            vm.SelectedCase = vm.Cases.Single(item => item.Kind == CaseKind.LinearStatic);
            var before = vm.Session.Document;
            var options = new AnalysisOptionsWindow(before.Settings, settings => vm.Session.Execute(DocumentCommands.SetSettings(settings)));
            options.Show(); Idle(); Snapshot(options, ".work/analysis-options-ui.png");
            Check(options.DofTab.Visibility == Visibility.Visible && options.UX.IsVisible && options.CableUX.IsVisible,
                "the native analysis options open on the degrees of freedom, those of cable joints included");
            options.UY.IsChecked = false;
            options.Close();
            Check(ReferenceEquals(vm.Session.Document, before), "closing the analysis-options form discards uncommitted DOF edits");
            options = new(before.Settings, settings => vm.Session.Execute(DocumentCommands.SetSettings(settings)));
            options.UX.IsChecked = options.UY.IsChecked = options.UZ.IsChecked = false;
            Check(!options.ApplyForm() && ReferenceEquals(vm.Session.Document, before), "rotations-only DOF input is refused atomically");
            options.Preset.SelectedIndex = 1;
            Check(options.ApplyForm() && vm.Session.Document.Settings.ActiveDofs == DofMask.XZ, "Apply commits a DOF preset to the native document");
            options.Close();
            // the degrees of freedom of cable joints, and the settings the native run now reads
            var cableOptions = new AnalysisOptionsWindow(before.Settings, settings => vm.Session.Execute(DocumentCommands.SetSettings(settings)));
            Check(cableOptions.CableDofEditors.IsEnabled && cableOptions.CableUX.IsChecked == true && cableOptions.CableUY.IsChecked == true && cableOptions.CableUZ.IsChecked == true &&
                  cableOptions.CableRX.IsChecked == false && cableOptions.CableRY.IsChecked == false && cableOptions.CableRZ.IsChecked == false &&
                  cableOptions.Tolerance.IsEnabled && cableOptions.Iterations.IsEnabled && cableOptions.Mesh.IsEnabled && cableOptions.Sag.IsEnabled,
                "the native options offer the degrees of freedom of cable joints — the translations by default — and the cable settings the run reads");
            cableOptions.CableUX.IsChecked = cableOptions.CableUY.IsChecked = cableOptions.CableUZ.IsChecked = false;
            var unchanged = vm.Session.Document;
            Check(!cableOptions.ApplyForm() && ReferenceEquals(vm.Session.Document, unchanged), "cable joints without a translation are refused atomically");
            cableOptions.CableUZ.IsChecked = cableOptions.CableRY.IsChecked = true;
            cableOptions.Tolerance.Text = "2"; cableOptions.Mesh.Text = "6"; cableOptions.Sag.IsChecked = false;
            Check(cableOptions.ApplyForm() && vm.Session.Document.Settings is { CableDofs: DofMask.UZ | DofMask.RY, CableFitTolerancePercent: 2, FrameMeshSegments: 6, CableSag: false },
                "Apply commits the degrees of freedom of cable joints and the cable settings to the native document");
            cableOptions.Close();
            vm.Session.Undo();
            Pump(vm.RunAsync());
            Check(vm.Results.State == ResultState.Failed && vm.Message.Contains("plane", StringComparison.OrdinalIgnoreCase), "running a spatial building with planar DOFs reports a capability error");
            vm.Session.Undo();
            Pump(vm.RunAsync()); Idle();
            Check(vm.Results.State == ResultState.Current && vm.Joints.Count() == 18 && vm.Frames.Count() == 52,
                "native 3D Run produces joint results and both ends of every frame");
            Check(Math.Abs(vm.Joints.Sum(j => j.FZ) - 680) < 1e-6, "WPF-run reactions balance independently counted 680 kN line loads");
            Snapshot(window, ".work/native-building-ui.png");
            vm.Session.Execute(DocumentCommands.SetSettings(vm.Session.Document.Settings with { CableFitTolerancePercent = 11 }));
            Check(vm.Results.State == ResultState.Stale && vm.Joints.Count() == 18 && vm.Summary.Contains("Stale"), "editing preserves results with an explicit stale state");
            vm.Session.Undo(); Check(vm.Results.State == ResultState.Current, "Undo restores current results");
            var path = Path.Combine(folder, "building.mcotis");
            vm.Files.SaveAs(path); var hash = vm.Session.Document.AnalysisInputHash;
            vm.Files.Open(path); Idle();
            Check(!vm.Session.IsDirty && vm.Joints.Count() == 0 && vm.Session.Document.AnalysisInputHash == hash && window.Title.Contains("building.mcotis"),
                "Save/Open preserves native input, updates the title and clears unpersisted results");
            Check(!vm.Session.CanUndo && !((MenuItem)((MenuItem)((Menu)((DockPanel)window.Content).Children[0]).Items[1]).Items[2]).IsEnabled,
                "Undo availability refreshes after opening a file");

            var state = StudyComposerChecks.CreateState();
            var sap = new Program.FakeSap { Snapshot = new(state.Model!, state.Geometry, state.LoadPatterns.ToArray()) };
            var workflow = new WorkflowService(sap, state);
            // What the application itself starts a model with — not the XZ plane these checks otherwise start in.
            var checksStart = MainViewModel.StartingDofs;
            MainViewModel.StartingDofs = Program.ProductStartingDofs;
            MainViewModel legacy;
            try
            {
                legacy = new MainViewModel(state, workflow, new NoSelection(), new ConfigService(sap, state, workflow),
                    femSessionStore: new FemWorkingSessionStore(Path.Combine(folder, "legacy")));
                legacy.LoadDefaultFemModel(replaceCurrent: true);
            }
            finally { MainViewModel.StartingDofs = checksStart; }
            Check(Program.ProductStartingDofs == (DofMask.Full3D, DofMask.Translations) && legacy.FemActiveDofs == DofMask.Full3D && legacy.FemCableDofs == DofMask.Translations,
                "the application starts a model in full 3D — all six degrees of freedom, the translations for cable joints — and so the default model starts");
            var cable = new AnalysisOptionsWindow(legacy.PlanarAnalysisSettings, legacy.ApplyPlanarAnalysisSettings, planar: true);
            cable.Show(); Idle(); Snapshot(cable, ".work/analysis-options-main-window.png");
            CheckBox[] model = [cable.UX, cable.UY, cable.UZ, cable.RX, cable.RY, cable.RZ];
            CheckBox[] joints = [cable.CableUX, cable.CableUY, cable.CableUZ, cable.CableRX, cable.CableRY, cable.CableRZ];
            Check(cable.DofTab.IsVisible && ReferenceEquals(cable.Tabs.SelectedItem, cable.DofTab) && cable.Tabs.Items.Count == 2 &&
                  cable.Preset is { IsVisible: true, IsEnabled: true, SelectedIndex: 0 } && Equals(((ComboBoxItem)cable.Preset.SelectedItem).Content, "Full 3D") &&
                  model.All(box => box.IsVisible && box.IsEnabled && box.IsChecked == true) && joints.All(box => box.IsVisible && box.IsEnabled) &&
                  joints.Select(box => box.IsChecked == true).SequenceEqual([true, true, true, false, false, false]),
                "the main window's analysis options open on the same Degrees of Freedom tab as the native workspace: the preset — Full 3D — the six degrees of freedom, all on, and those of cable joints, the translations on; every box a choice");
            Check(cable.ActualHeight < 640 && cable.DofNote.Text.Contains("held at every joint", StringComparison.Ordinal) && cable.DofNote.Text.Contains("in Full 3D nothing holds it out of that plane", StringComparison.Ordinal) &&
                  cable.DofNote.Text.Contains("the run holds it, solves, and lists it in the Run Monitor as a warning", StringComparison.Ordinal) &&
                  cable.DofNote.Text.Contains("the results are those of the XZ plane", StringComparison.Ordinal) && cable.DofNote.Text.Contains("the run reports it", StringComparison.Ordinal),
                $"… the form is as tall as what is on it ({cable.ActualHeight:0} px), and says what full 3D means for a model in a plane — the movement out of it is held and listed as a warning, the results are those of the plane — and that a set the model cannot be analysed with is reported by the run, not refused here");
            cable.Tabs.SelectedItem = cable.FittingTab; Idle();
            cable.Tolerance.Text = "5"; cable.Iterations.Text = "150"; cable.Mesh.Text = "8"; cable.Sag.IsChecked = false;
            Check(cable.Tolerance.IsVisible && cable.ApplyForm() && legacy.FemTargetFitPercent == 5 && legacy.FemTargetFitIterations == 150 && legacy.FemFrameMeshSegments == 8 && !legacy.FemCableSag &&
                  legacy.FemActiveDofs == DofMask.Full3D && legacy.FemCableDofs == DofMask.Translations,
                "cable fitting form updates the actual planar analysis inputs, and leaves the degrees of freedom as they were");
            cable.Mesh.Text = "101";
            Check(!cable.ApplyForm() && legacy.FemFrameMeshSegments == 8, "invalid cable-form mesh count leaves applied settings unchanged");
            cable.Mesh.Text = "8";
            cable.Tabs.SelectedItem = cable.DofTab; Idle();
            cable.Preset.SelectedIndex = 1;
            Check(cable.ApplyForm() && legacy.FemActiveDofs == DofMask.XZ && legacy.FemCableDofs == DofMask.Translations &&
                  model.Select(box => box.IsChecked == true).SequenceEqual([true, false, true, false, true, false]) &&
                  legacy.PlanarAnalysisSettings is { ActiveDofs: DofMask.XZ, CableDofs: DofMask.Translations, CableFitTolerancePercent: 5 },
                "the XZ plane preset switches UY, RX and RZ off, and Apply makes it the run's: the model is then analysed in its plane");
            cable.RY.IsChecked = false; cable.CableUX.IsChecked = false; cable.CableUY.IsChecked = false;
            Check(cable.ApplyForm() && legacy.FemActiveDofs == (DofMask.UX | DofMask.UZ) && legacy.FemCableDofs == DofMask.UZ,
                "any box can be changed, and Apply takes the set as it is: RY off for the model, UZ alone for cable joints");
            cable.CableUZ.IsChecked = false;
            Check(!cable.ApplyForm() && cable.ErrorText.Text.Contains("cable joints", StringComparison.Ordinal) && legacy.FemCableDofs == DofMask.UZ,
                "cable joints without a translation are refused, and what was applied stays: " + cable.ErrorText.Text);
            cable.CableUZ.IsChecked = true; cable.RY.IsChecked = true; cable.UX.IsChecked = false;
            Check(cable.ApplyForm() && cable.ErrorText.Text == "Applied." && legacy.FemActiveDofs == (DofMask.UZ | DofMask.RY) && legacy.FemCableDofs == DofMask.UZ,
                "a translation of the plane can be switched off as well: the form keeps the user's set — whether this model can be analysed with it is for the run to say, in the Run Monitor");
            Check(MainViewModel.DofProblem(DofMask.Full3D, DofMask.Translations) is null && MainViewModel.DofProblem(DofMask.UZ | DofMask.RY, DofMask.UX | DofMask.UZ) is null &&
                  MainViewModel.DofProblem(DofMask.UX, DofMask.UZ) is null && MainViewModel.DofProblem(DofMask.RY, DofMask.UX | DofMask.UZ) is not null &&
                  MainViewModel.DofProblem(DofMask.XZ, DofMask.None) is not null && MainViewModel.DofProblem(DofMask.XZ, DofMask.RY) is not null,
                "the main window keeps any set that has a translation in it, for the model and for cable joints; one without is refused, whoever asks");
            cable.Preset.SelectedIndex = 0;
            foreach (var box in joints.Take(3)) box.IsChecked = true;
            Check(cable.ApplyForm() && legacy.FemActiveDofs == DofMask.Full3D && legacy.FemCableDofs == DofMask.Translations, "and Full 3D can be chosen again");
            cable.Close();
            // a model without cables: the second tab is the frame mesh alone, and what is not shown keeps its value
            var held = legacy.PlanarAnalysisSettings;
            var noCables = new AnalysisOptionsWindow(held, legacy.ApplyPlanarAnalysisSettings, planar: true, cables: false);
            noCables.Show(); Idle();
            noCables.Tabs.SelectedItem = noCables.FittingTab; Idle(); Snapshot(noCables, ".work/analysis-options-no-cables.png");
            bool meshAlone = Equals(noCables.FittingTab.Header, "Frame Mesh") && noCables.Mesh.IsVisible && !noCables.Tolerance.IsVisible && !noCables.Iterations.IsVisible &&
                             !noCables.Sag.IsVisible && !noCables.CableTargetNote.IsVisible && noCables.CableScope.Text.Contains("The model has no cables", StringComparison.Ordinal);
            noCables.Mesh.Text = "6";
            Check(Equals(cable.FittingTab.Header, "Cable Force Fitting") && meshAlone && noCables.ApplyForm() && legacy.PlanarAnalysisSettings == held with { FrameMeshSegments = 6 },
                "Analysis Options shows the cable force fitting — tolerance, iterations, cable sag — for a model with cables only: without cables the tab is Frame Mesh, with the mesh alone, " +
                "and Apply leaves the settings that are not shown as they were");
            noCables.Close();
            var shell = new MainWindow { DataContext = legacy };
            shell.Show(); Idle();
            var edit = shell.MainMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "_Edit"));
            Check(edit.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "_TemplateBuilding…")).IsEnabled,
                "Edit → TemplateBuilding is available without VCF prerequisites");
            legacy.InitializeBlankMcotisConfiguration(); Idle();
            Check(edit.IsEnabled && edit.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "_TemplateBuilding…")).IsEnabled,
                "TemplateBuilding also remains available on a blank model");
            shell.Close();
            RunMonitor(folder);
            Check(string.IsNullOrWhiteSpace(bindings.ToString()), "new WPF forms and workspace have no reported binding errors: " + bindings);
        }
        finally
        {
            if (window is not null) { window.Model.Session.MarkSaved(window.Model.Session.Document, Path.Combine(folder, "closed.mcotis")); window.Close(); }
            form.Close();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }
    /// <summary>The run monitor alone, with the binding errors it reports.</summary>
    internal static void RunMonitorOnly()
    {
        var folder = Path.GetFullPath(Path.Combine(".work", "run-monitor-ui-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder);
        var bindings = new StringWriter();
        var listener = new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            RunMonitor(folder);
            Check(string.IsNullOrWhiteSpace(bindings.ToString()), "the run monitor has no reported binding errors: " + bindings);
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); }
    }

    /// <summary>The run monitor of the main window: what it says before a run; after Run Analysis on the default model,
    /// the run, the model in numbers and one entry per load case that opens on its diagnostics and its log; and, for a
    /// run the solver refuses, the table of the joints and degrees of freedom it reports.</summary>
    private static void RunMonitor(string folder)
    {
        var state = StudyComposerChecks.CreateState();
        var sap = new Program.FakeSap { Snapshot = new(state.Model!, state.Geometry, state.LoadPatterns.ToArray()) };
        var workflow = new WorkflowService(sap, state);
        var vm = new MainViewModel(state, workflow, new NoSelection(), new ConfigService(sap, state, workflow),
            femSessionStore: new FemWorkingSessionStore(Path.Combine(folder, "run-monitor")));
        vm.LoadDefaultFemModel(replaceCurrent: true);
        var shell = new MainWindow { DataContext = vm };
        shell.Show(); Idle();
        try
        {
            var run = shell.MainMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "_Run"));
            run.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Analysis _Monitor")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Idle();
            var monitor = shell.OwnedWindows.OfType<FemRunMonitorWindow>().Single();
            var bar = monitor.RunProgress;
            static double Top(FrameworkElement element, Window window) => element.TransformToAncestor(window).Transform(new Point()).Y;
            Check(monitor.Views.Items.OfType<TabItem>().Select(tab => tab.Header).SequenceEqual(["Load Cases", "Cable Target-Force Convergence"]) &&
                  ReferenceEquals(monitor.Views.SelectedItem, monitor.RunTab) && monitor.CaseList.Items.Count == 0 && monitor.NoCasesText.IsVisible &&
                  monitor.RunStatusText.Text == "No analysis run yet" && !monitor.DofText.IsVisible && !monitor.ModelPanel.IsVisible && bar.Value == 0 && !bar.IsIndeterminate,
                "before a run the monitor has two views — Load Cases and the cable convergence, no equilibrium table — and says that nothing has run: no cases, no model, no degrees of freedom");
            Check(monitor.StabilityNote.IsVisible && monitor.StabilityNoteText.IsVisible &&
                  monitor.StabilityNoteText.Text.Contains("Numerical stability is assessed using estimated digits of accuracy lost during matrix solution.", StringComparison.Ordinal) &&
                  monitor.StabilityNoteText.Text.Contains("A value of ∞ indicates that a singularity has been identified", StringComparison.Ordinal) &&
                  monitor.StabilityNoteText.Text.Contains("reaches 6 digits", StringComparison.Ordinal) && monitor.StabilityNoteText.Text.Contains("above 11 digits", StringComparison.Ordinal) &&
                  Top(monitor.StabilityNote, monitor) < Top(monitor.CaseList, monitor),
                "the note on numerical stability stands once, before the load cases, with the thresholds the run reports by: a warning from 6 digits lost, severe above 11");
            monitor.StabilityNoteToggle.IsChecked = false; Idle();
            Check(!monitor.StabilityNoteText.IsVisible && monitor.StabilityNote.IsVisible && !vm.FemMonitor.IsNoteExpanded, "… and it can be closed: its heading stays, its text goes");

            var runs = FemModelFileChecks.Analyze(vm); Idle();
            Snapshot(monitor, ".work/run-monitor-ui.png");
            var shown = vm.FemMonitor;
            var headers = Visuals<ToggleButton>(monitor.CaseList).ToArray();
            Check(runs.Count == 1 && shown.Cases.Count >= 2 && headers.Length == shown.Cases.Count && monitor.CaseList.Items.Count == shown.Cases.Count &&
                  monitor.RunStatusText.Text == shown.StatusText && shown.Status is FemRunStatus.Completed or FemRunStatus.CompletedWithWarnings && bar.Value == 1 && !bar.IsIndeterminate &&
                  !monitor.NoCasesText.IsVisible && !monitor.StabilityNoteText.IsVisible,
                $"Run Analysis fills the monitor: {shown.StatusText}, the bar full, {shown.Cases.Count} load cases each under its own header; the note stays closed");
            Check(monitor.DofText.IsVisible && Regex.IsMatch(monitor.DofText.Text, @"^\d+ active \| \d+ solved \| \d+ restrained$") && monitor.DofText.Text == shown.DofText &&
                  Regex.IsMatch(monitor.ElapsedText.Text, @"^\d\d:\d\d:\d\d$") && monitor.ModelPanel.IsVisible && monitor.ModelNameText.Text == vm.CurrentFileName &&
                  monitor.ModelLeft.Items.Count == 5 && monitor.ModelRight.Items.Count == 5 &&
                  Visuals<TextBlock>(monitor).All(text => !text.Text.Contains("SAP2000", StringComparison.OrdinalIgnoreCase) && !text.Text.Contains("solver", StringComparison.OrdinalIgnoreCase)),
                $"… above them the run — {monitor.DofText.Text}, elapsed {monitor.ElapsedText.Text} — and the model information in two columns under the name of its file, '{monitor.ModelNameText.Text}'; no solver name and no SAP2000 anywhere");
            var statuses = Visuals<TextBlock>(monitor.CaseList).Where(text => text.Name == "CaseStatusText").ToArray();
            Check(statuses.Length == shown.Cases.Count && statuses.Zip(shown.Cases).All(pair => pair.First.Text == pair.Second.StatusText &&
                      ReferenceEquals(pair.First.Foreground, monitor.FindResource(pair.Second.Status == FemCaseStatus.Converged ? "Success" : "Warning"))),
                "… every load case has its own status, in its colour: " + string.Join(", ", shown.Cases.Select(item => $"{item.Number} {item.Name} — {item.StatusText}")));
            var diagnostics = Visuals<TextBlock>(monitor.CaseList).Where(text => text.Name == "CaseDiagnosticText").ToArray();
            Check(diagnostics.Length == shown.Cases.Count && diagnostics.Zip(shown.Cases).All(pair => pair.First.IsVisible && pair.First.Text == pair.Second.DiagnosticText &&
                      pair.First.Text.StartsWith("Instabilities: 0", StringComparison.Ordinal)) &&
                  headers.Zip(shown.Cases).All(pair => System.Windows.Automation.AutomationProperties.GetName(pair.First) == pair.Second.Summary && pair.Second.Summary.EndsWith(pair.Second.HeaderText, StringComparison.Ordinal)),
                "… and, closed, says after its status what was found of it: " + string.Join(" | ", shown.Cases.Select(item => item.HeaderText)));
            // at its smallest the window still shows every status whole; the name and the diagnostic give way
            double wide = monitor.Width;
            monitor.Width = monitor.MinWidth; Idle();
            Snapshot(monitor, ".work/run-monitor-narrow-ui.png");
            double Right(FrameworkElement element) => element.TransformToAncestor(monitor).Transform(new Point(element.ActualWidth, 0)).X;
            Check(monitor.ActualWidth == 720 && statuses.All(text => text.ActualWidth > 0 && Right(text) <= Right((FrameworkElement)VisualTreeHelper.GetParent(text)) + 0.5) &&
                  headers.All(header => Right(header) <= Right(monitor.CaseList) + 0.5 && Visuals<TextBlock>(header).Count(text => text.IsVisible && text.ActualWidth > 0) >= 5),
                "… at the window's smallest width, 720, each row still has its number, name, whole status, diagnostic and time: the name and the diagnostic are what is trimmed");
            monitor.Width = wide; Idle();
            var lines = Enumerable.Range(0, monitor.CaseList.Items.Count).Select(index => (DependencyObject)monitor.CaseList.ItemContainerGenerator.ContainerFromIndex(index))
                .Select(container => Visuals<Border>(container).Last(border => border.Height == 1)).ToArray();
            Check(lines.Length == shown.Cases.Count && lines.All(line => ReferenceEquals(line.Background, monitor.FindResource("Border")) && line.ActualWidth >= monitor.CaseList.ActualWidth - 1),
                "… the load cases are separated by a solid line across the list");
            Check(!Visuals<TextBox>(monitor.CaseList).Any() && !Visuals<DataGrid>(monitor.CaseList).Any() && shown.Cases.All(item => !item.IsExpanded),
                "closed, a load case is one line: nothing of its diagnostics or its log is built until it is opened");

            headers[0].IsChecked = true; headers[^1].IsChecked = true; Idle();
            Snapshot(monitor, ".work/run-monitor-open-ui.png");
            var logs = Visuals<TextBox>(monitor.CaseList).ToArray();
            Check(shown.Cases[0].IsExpanded && shown.Cases[^1].IsExpanded && shown.Cases.Count(item => item.IsExpanded) == 2 && logs.Length == 2,
                "a load case opens from its header, and two stay open at once");
            string[] Detail() => Visuals<TextBlock>(monitor.CaseList.ItemContainerGenerator.ContainerFromIndex(0)).Where(text => text.IsVisible).Select(text => text.Text).ToArray();
            var detail = Detail();
            int At(string label) => Array.IndexOf(detail, label);
            string[] measured = ["Smallest scaled pivot", "Resultant of internal forces", "Largest loss of accuracy"];
            Check(At("Convergence") >= 0 && detail[At("Convergence") + 1] == "Passed" && detail[At("Numerical Stability") + 1] == shown.Cases[0].StabilityText &&
                  detail[At("Instabilities") + 1] == "0" && detail.Contains("ANALYSIS LOG") && detail.Contains("Nonlinear convergence") && detail.Contains("Iterations") &&
                  At("TECHNICAL DIAGNOSTICS") > At("Instabilities") && At("TECHNICAL DIAGNOSTICS") < At("ANALYSIS LOG") && !measured.Any(detail.Contains),
                $"… on Convergence: Passed · Numerical Stability: {shown.Cases[0].StabilityText} · Instabilities: 0, its convergence and iterations, and its own analysis log — the pivot, the resultant and the largest loss of accuracy are not on show");
            shown.Cases[0].IsTechnicalExpanded = true; Idle();
            detail = Detail();
            Check(measured.All(detail.Contains) && shown.Cases[0].Technical.All(field => detail.Contains(field.Label) && detail.Contains(field.Value)) && !shown.Cases[^1].IsTechnicalExpanded,
                "… Technical diagnostics opens on them, as the solver reported them: " + string.Join(" · ", shown.Cases[0].Technical.Select(field => $"{field.Label} {field.Value}")));
            shown.Cases[0].IsTechnicalExpanded = false; Idle();
            Check(logs[0].IsReadOnly && logs[0].Text == shown.Cases[0].LogText && logs[0].Text.Contains("Analysis started.", StringComparison.Ordinal) &&
                  ReferenceEquals(logs[0].FontFamily, monitor.FindResource("UiFont")) && ReferenceEquals(logs[0].Foreground, monitor.FindResource("Text")) &&
                  logs[0].TextWrapping == TextWrapping.Wrap && monitor.FontFamily.Source == "Segoe UI" && monitor.FontSize == 12 &&
                  Visuals<TextBox>(monitor).All(box => !ReferenceEquals(box.Foreground, monitor.FindResource("Accent")) && !ReferenceEquals(box.FontFamily, monitor.FindResource("MonoFont"))),
                "… the log is in the application's own type — Segoe UI 12, dark on light, wrapped — not a blue fixed-width console");

            // A run the solver refuses, as the monitor holds it: a portal that sways freely.
            var refused = RunMonitorChecks.SwayingPortal();
            vm.FemMonitor.Apply(refused.Run); Idle();
            Snapshot(monitor, ".work/run-monitor-instability-ui.png");
            var grid = Visuals<DataGrid>(monitor.CaseList).Single();
            Check(monitor.RunStatusText.Text == "Failed" && ReferenceEquals(monitor.RunStatusText.Foreground, monitor.FindResource("Error")) && shown.Cases.Single().IsExpanded &&
                  grid.Columns.Select(column => column.Header as string).SequenceEqual(["Joint", "X (m)", "Y (m)", "Z (m)", "UX", "UY", "UZ", "RX", "RY", "RZ", "Remarks"]) && grid.Items.Count == 4 &&
                  Visuals<TextBlock>(monitor.CaseList).Single(text => text.Name == "CaseDiagnosticText") is { Text: "6 affected DOFs" } affected && ReferenceEquals(affected.Foreground, monitor.FindResource("Error")),
                "a load case that failed reads Numerical Instability · 6 affected DOFs in red and opens itself on the table of its joints: Joint · X (m) · Y (m) · Z (m) · UX · UY · UZ · RX · RY · RZ · Remarks");
            var cells = Visuals<DataGridRow>(grid).SelectMany(row => Visuals<TextBlock>(row)).ToArray();
            var infinite = cells.Where(text => text.Text == "∞").ToArray();
            Check(infinite.Length == 6 && infinite.All(text => text.FontWeight == FontWeights.Bold && ReferenceEquals(text.Foreground, monitor.FindResource("Error"))) &&
                  cells.Count(text => text.Text == "-") == 12 && cells.Count(text => text.Text == "R") == 4 && cells.Count(text => text.Text == "N/A") == 2 &&
                  cells.Where(text => text.Text is "-" or "R" or "N/A").All(text => ReferenceEquals(text.Foreground, monitor.FindResource("Muted"))) &&
                  cells.Any(text => text.Text == "UX, RY: Singular (∞) — possible mechanism") && cells.Any(text => text.Text == "3.000"),
                "… ∞ in bold red where a singularity was identified, - for the degrees of freedom the plane switches off, R at the supports, N/A without an estimate, coordinates to the millimetre and the remarks naming the degrees of freedom");

            monitor.Views.SelectedItem = monitor.ConvergenceTab; Idle();
            Check(Visuals<Mcotis.Desktop.Presentation.Controls.ConvergenceChart>(monitor).Single() is { IsVisible: true, Ratios.Count: > 0 },
                "… and the second view is the cable target-force convergence chart, with the fitting runs of the monitored cable");

            // Degrees of freedom the model cannot be analysed with: Analysis Options keeps them, the run reports them.
            monitor.Views.SelectedItem = monitor.RunTab;
            vm.UnlockFemModel.Execute(null);
            vm.ApplyPlanarAnalysisSettings(vm.PlanarAnalysisSettings with { ActiveDofs = DofMask.UZ | DofMask.RY });
            var none = FemModelFileChecks.Analyze(vm); Idle();
            Snapshot(monitor, ".work/run-monitor-refused-ui.png");
            var notRun = Visuals<TextBlock>(monitor.CaseList).Where(text => text.Name == "CaseDiagnosticText").ToArray();
            Check(none.Count == 0 && monitor.RunStatusText.Text == "Failed" && !monitor.DofText.IsVisible && monitor.RunMessage.IsVisible && ReferenceEquals(monitor.RunMessage.Foreground, monitor.FindResource("Error")) &&
                  monitor.RunMessage.Text.StartsWith("UX is switched off, but the joints of the model are not all at one X", StringComparison.Ordinal) &&
                  monitor.RunMessage.Text.EndsWith("Switch UX on under Run → Analysis Options → Degrees of Freedom.", StringComparison.Ordinal) &&
                  shown.Cases.Count >= 2 && notRun.Length == shown.Cases.Count && notRun.All(text => text.Text == "Not run") &&
                  shown.Cases.All(item => item is { StatusText: "Cancelled", InstabilitiesText: "N/A" } && item.LogText.Contains("UX is switched off", StringComparison.Ordinal)),
                "with UX switched off in Analysis Options the default model — which spans X — is not analysed and not corrected: the run reads Failed with the reason in red, and every load case Cancelled · Not run with the reason in its log");

            // Full 3D — what the application starts a model with. The default model lies in a plane on pins.
            if (vm.IsFemModelLocked) vm.UnlockFemModel.Execute(null);
            vm.ApplyPlanarAnalysisSettings(vm.PlanarAnalysisSettings with { ActiveDofs = Program.ProductStartingDofs.Model, CableDofs = Program.ProductStartingDofs.CableJoints });
            var spatial = FemModelFileChecks.Analyze(vm); Idle();
            Snapshot(monitor, ".work/run-monitor-3d-ui.png");
            int jointCount = int.Parse(shown.ModelLeft[0].Value), pins = int.Parse(shown.ModelLeft[1].Value);
            Check(spatial.Count == 1 && monitor.RunStatusText.Text == "Completed with Warnings" && ReferenceEquals(monitor.RunStatusText.Foreground, monitor.FindResource("Warning")) &&
                  monitor.DofText.Text == $"{6 * jointCount} active | {6 * jointCount - 3 * pins} solved | {3 * pins} restrained" && !monitor.RunMessage.IsVisible,
                $"in full 3D — what the application starts a model with — the default model is analysed and has its results: the run reads Completed with Warnings over {monitor.DofText.Text}");
            Check(shown.Cases.Count >= 2 && shown.Cases.All(item => item is { Status: FemCaseStatus.ConvergedWithWarnings, StabilityText: "Singular stiffness — 1 movement without stiffness, not loaded: held", IsExpanded: false } &&
                      item.DiagnosticText.StartsWith($"Instabilities: {2 * jointCount - pins}  ·  1 unloaded movement held", StringComparison.Ordinal) &&
                      item.Rows.Count == jointCount && item.Rows.All(row => row.RX.Text == "∞" && row.RY.Text != "∞" && row.UX.Text != "∞" && row.UZ.Text != "∞") &&
                      item.Rows.Count(row => row.UY.Text == "∞") == jointCount - pins &&
                      item.Rows.Count(row => row.UY.Text == "R" && row.Remarks == "RX: Singular (∞) — possible mechanism, not loaded: held") == pins),
                $"… every load case reads Converged with Warnings · Instabilities: {2 * jointCount - pins} · 1 unloaded movement held, and lists the one movement the solver held, joint by joint: the model turning about the line of its {pins} pins — " +
                $"RX of all {jointCount} joints and UY of the {jointCount - pins} off that line, nothing against UX, UZ or RY");
            var statusTexts = Visuals<TextBlock>(monitor.CaseList).Where(text => text.Name is "CaseStatusText" or "CaseDiagnosticText").ToArray();
            Check(statusTexts.Length == 2 * shown.Cases.Count && statusTexts.All(text => ReferenceEquals(text.Foreground, monitor.FindResource("Warning"))),
                "… in amber, as warnings: none of them a failure");

            // The same model in its plane: the results of the two runs are the same.
            if (vm.IsFemModelLocked) vm.UnlockFemModel.Execute(null);
            vm.ApplyPlanarAnalysisSettings(vm.PlanarAnalysisSettings with { ActiveDofs = DofMask.XZ });
            var inPlane = FemModelFileChecks.Analyze(vm); Idle();
            Check(inPlane.Count == 1 && vm.FemCableDofs == DofMask.Translations && shown.Status is FemRunStatus.Completed or FemRunStatus.CompletedWithWarnings &&
                  monitor.DofText.Text == $"{3 * jointCount} active | {3 * jointCount - 2 * pins} solved | {2 * pins} restrained" && shown.Cases.All(item => item.DiagnosticText.StartsWith("Instabilities: 0", StringComparison.Ordinal)),
                $"with the XZ plane chosen the same model is analysed in its plane: {shown.StatusText}, {monitor.DofText.Text}, every load case with zero instabilities");
            var names = inPlane[0].Results!.Keys.ToArray();
            var gaps = names.Select(name => RunMonitorChecks.Difference(inPlane[0].Results![name], spatial[0].Results![name])).ToArray();
            Check(names.Length >= 3 && spatial[0].Results!.Keys.SequenceEqual(names) && gaps.Max(gap => gap.Moved) <= 1e-8 && gaps.Max(gap => gap.Force) <= 1e-3 &&
                  inPlane[0].Result.Cables.Any(cable => cable.AxialForceKN > 100),
                $"… and the results of the run in full 3D are those of the run in the plane, for every load case and combination ({string.Join(", ", names)}): joints, reactions, every frame station and every cable — " +
                $"displacements within {gaps.Max(gap => gap.Moved):0.0E+00} m, forces within {gaps.Max(gap => gap.Force):0.0E+00} kN");
        }
        finally { shell.Close(); }
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var found in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return found;
    }
    private sealed class NoSelection : ISelectionService
    {
        public IReadOnlyList<string>? Select(string title, IReadOnlyList<string> available, IReadOnlyList<string> selected) => null;
    }
    private static void Idle() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (task.IsCompleted || watch.Elapsed.TotalSeconds > 30) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        if (!task.IsCompleted) throw new TimeoutException("Native analysis exceeded the UI check timeout.");
        task.GetAwaiter().GetResult();
    }
    private static void Snapshot(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
}
