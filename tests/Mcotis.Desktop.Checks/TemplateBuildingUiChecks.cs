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
            window = new NativeModelWindow(form.CreatedSession!);
            window.Show(); Idle();
            var vm = window.Model;
            Check(window.Viewer.Model.Points.Count == 18 && window.Viewer.Model.Members.Count == 26 && window.Viewer.CurrentPlane == "3D",
                "the native workspace displays the generated spatial geometry");
            Check(vm.Session.CanUndo && vm.Loads.Count() == 14, "generation is undoable and line loads appear in the workspace");
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
            var legacy = new MainViewModel(state, workflow, new NoSelection(), new ConfigService(sap, state, workflow),
                femSessionStore: new FemWorkingSessionStore(Path.Combine(folder, "legacy")));
            legacy.LoadDefaultFemModel(replaceCurrent: true);
            var cable = new AnalysisOptionsWindow(legacy.PlanarAnalysisSettings, legacy.ApplyPlanarAnalysisSettings, planar: true);
            cable.Show(); Idle(); Snapshot(cable, ".work/analysis-options-main-window.png");
            Check(cable.DofTab.Visibility != Visibility.Visible && !cable.UX.IsVisible && !cable.CableUX.IsVisible &&
                  ReferenceEquals(cable.Tabs.SelectedItem, cable.FittingTab) && cable.Tolerance.IsVisible && cable.Sag.IsVisible,
                "the main window's analysis options have no Degrees of Freedom tab — nothing on it could be changed there — and open on the cable settings");
            Check(cable.ActualHeight < 500 && cable.CableScope.Text.Contains("XZ plane", StringComparison.Ordinal),
                $"… the form is as tall as what is left on it ({cable.ActualHeight:0} px against the 560 px of the two-tab form), and says the model is analysed in its plane");
            cable.Tolerance.Text = "5"; cable.Iterations.Text = "150"; cable.Mesh.Text = "8"; cable.Sag.IsChecked = false;
            // Apply still hands over the XZ degrees of freedom the form no longer shows; the main window refuses any other.
            Check(cable.ApplyForm() && legacy.FemTargetFitPercent == 5 && legacy.FemTargetFitIterations == 150 && legacy.FemFrameMeshSegments == 8 && !legacy.FemCableSag,
                "cable fitting form updates the actual planar analysis inputs");
            cable.Mesh.Text = "101";
            Check(!cable.ApplyForm() && legacy.FemFrameMeshSegments == 8, "invalid cable-form mesh count leaves applied settings unchanged");
            cable.Close();
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
            var detail = Visuals<TextBlock>(monitor.CaseList.ItemContainerGenerator.ContainerFromIndex(0)).Select(text => text.Text).ToArray();
            int At(string label) => Array.IndexOf(detail, label);
            Check(At("Convergence") >= 0 && detail[At("Convergence") + 1] == "Passed" && detail[At("Numerical Stability") + 1] == shown.Cases[0].StabilityText &&
                  detail[At("Instabilities") + 1] == "0" && detail.Contains("ANALYSIS LOG") && detail.Contains("Nonlinear convergence") && detail.Contains("Largest loss of accuracy"),
                $"… on Convergence: Passed · Numerical Stability: {shown.Cases[0].StabilityText} · Instabilities: 0, the details of its convergence, and its own analysis log");
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
                  grid.Columns.Select(column => column.Header as string).SequenceEqual(["Joint", "X", "Y", "Z", "UX", "UY", "UZ", "RX", "RY", "RZ", "Remarks"]) && grid.Items.Count == 4,
                "a load case that failed opens itself on the table of its joints: Joint · X · Y · Z · UX · UY · UZ · RX · RY · RZ · Remarks");
            var cells = Visuals<DataGridRow>(grid).SelectMany(row => Visuals<TextBlock>(row)).ToArray();
            var infinite = cells.Where(text => text.Text == "∞").ToArray();
            Check(infinite.Length == 6 && infinite.All(text => text.FontWeight == FontWeights.Bold && ReferenceEquals(text.Foreground, monitor.FindResource("Error"))) &&
                  cells.Count(text => text.Text == "-") == 12 && cells.Count(text => text.Text == "R") == 4 && cells.Count(text => text.Text == "N/A") == 2 &&
                  cells.Where(text => text.Text is "-" or "R" or "N/A").All(text => ReferenceEquals(text.Foreground, monitor.FindResource("Muted"))) &&
                  cells.Any(text => text.Text == "UX, RY: Singular (∞) — possible mechanism") && cells.Any(text => text.Text == "3.000"),
                "… ∞ in bold red where a singularity was identified, - for the degrees of freedom the plane switches off, R at the supports, N/A without an estimate, coordinates to the millimetre and the remarks naming the degrees of freedom");

            monitor.Views.SelectedItem = monitor.ConvergenceTab; Idle();
            Check(Visuals<Mcotis.Desktop.Presentation.Controls.ConvergenceChart>(monitor).Single() is { IsVisible: true } chart && chart.Ratios.Count > 0,
                "… and the second view is the cable target-force convergence chart, with the fitting runs of the monitored cable");
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
