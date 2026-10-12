using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    /// <summary>The run monitor of the main window: empty before a run; after Run Analysis on the default model, the
    /// degrees of freedom of the run and one line per load case with its equilibrium.</summary>
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
            TextBlock Line(string property) => Visuals<TextBlock>(monitor).Single(text =>
                System.Windows.Data.BindingOperations.GetBindingExpression(text, TextBlock.TextProperty)?.ParentBinding.Path.Path == property);
            var bar = Visuals<ProgressBar>(monitor).Single();
            Check(monitor.EquilibriumGrid.IsVisible && monitor.EquilibriumGrid.Items.Count == 0 && !Line(nameof(MainViewModel.FemMonitorDofText)).IsVisible &&
                  !Line(nameof(MainViewModel.FemMonitorEquilibriumText)).IsVisible && bar.Value == 0 && !bar.IsIndeterminate,
                "before a run the monitor's equilibrium table is empty, and the lines that describe a run take no room");

            var runs = FemModelFileChecks.Analyze(vm); Idle();
            Snapshot(monitor, ".work/run-monitor-ui.png");
            Check(runs.Count == 1 && ReferenceEquals(shell.OwnedWindows.OfType<FemRunMonitorWindow>().Single(), monitor) &&
                  vm.FemEquilibriumRows.Count >= 2 && monitor.EquilibriumGrid.Items.Count == vm.FemEquilibriumRows.Count &&
                  monitor.EquilibriumGrid.Columns.Select(column => column.Header is TextBlock header ? header.Text : column.Header as string)
                      .SequenceEqual(["Load case", "Solved DOFs", "Iterations", "Out-of-balance", "Accepted", "Joints converged", "Largest at", "Status"]),
                $"Run Analysis fills the monitor's equilibrium table: {vm.FemEquilibriumRows.Count} load cases, each with its solved degrees of freedom, iterations, out-of-balance, joints converged and status");
            var shown = Visuals<DataGridRow>(monitor.EquilibriumGrid).ToArray();
            var statuses = shown.SelectMany(row => Visuals<TextBlock>(row)).Where(text => text.Name == "StatusText").ToArray();
            Check(shown.Length > 0 && statuses.Length == shown.Length &&
                  statuses.All(text => text.Text == FemEquilibriumRow.Converged && ReferenceEquals(text.Foreground, monitor.FindResource("Success"))),
                $"… every line shown ({shown.Length}) reads Converged, in the colour of a good result");
            var dofs = Line(nameof(MainViewModel.FemMonitorDofText));
            var outcome = Line(nameof(MainViewModel.FemMonitorEquilibriumText));
            Check(dofs.IsVisible && dofs.Text == vm.FemMonitorDofText && dofs.Text.Contains(" active", StringComparison.Ordinal) && dofs.Text.Contains(" solved", StringComparison.Ordinal) &&
                  outcome.IsVisible && outcome.Text.StartsWith("Equilibrium reached in every load case", StringComparison.Ordinal) && bar.Value == 1 && !bar.IsIndeterminate,
                $"… above it the degrees of freedom of the run and the outcome, with the bar full: {dofs.Text} {outcome.Text}");
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
