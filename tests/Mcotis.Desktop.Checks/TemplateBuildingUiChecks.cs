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
            options.UY.IsChecked = false;
            options.Close();
            Check(ReferenceEquals(vm.Session.Document, before), "closing the analysis-options form discards uncommitted DOF edits");
            options = new(before.Settings, settings => vm.Session.Execute(DocumentCommands.SetSettings(settings)));
            options.UX.IsChecked = options.UY.IsChecked = options.UZ.IsChecked = false;
            Check(!options.ApplyForm() && ReferenceEquals(vm.Session.Document, before), "rotations-only DOF input is refused atomically");
            options.Preset.SelectedIndex = 1;
            Check(options.ApplyForm() && vm.Session.Document.Settings.ActiveDofs == DofMask.XZ, "Apply commits a DOF preset to the native document");
            options.Close();
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
            Check(!cable.DofEditors.IsEnabled && cable.UX.IsChecked == true && cable.UY.IsChecked == false && cable.RY.IsChecked == true,
                "planar cable workspace states its actual XZ DOFs");
            cable.Tolerance.Text = "5"; cable.Iterations.Text = "150"; cable.Mesh.Text = "8"; cable.Sag.IsChecked = false;
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
            Check(string.IsNullOrWhiteSpace(bindings.ToString()), "new WPF forms and workspace have no reported binding errors: " + bindings);
        }
        finally
        {
            if (window is not null) { window.Model.Session.MarkSaved(window.Model.Session.Document, Path.Combine(folder, "closed.mcotis")); window.Close(); }
            form.Close();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
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
