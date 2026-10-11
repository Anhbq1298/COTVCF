using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using COT_VCF.Application;
using COT_VCF.Configuration;
using COT_VCF.Geometry;
using COT_VCF.Infrastructure;
using COT_VCF.Presentation;
using COT_VCF.SAP2000;

internal static class ResultToolbarChecks
{
    internal static void Run()
    {
        var state = StudyComposerChecks.CreateState();
        var sap = new Program.FakeSap { Snapshot = new(state.Model!, state.Geometry, state.LoadPatterns.ToArray()) };
        var workflow = new WorkflowService(sap, state);
        var sessionPath = Path.GetFullPath(Path.Combine(".work", "result-toolbar-" + Guid.NewGuid().ToString("N")));
        var vm = new MainViewModel(state, workflow, new NoSelection(), new ConfigService(sap, state, workflow),
            femSessionStore: new FemWorkingSessionStore(sessionPath));
        vm.LoadDefaultFemModel(replaceCurrent: true);
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 800 };
        try
        {
            window.Show();
            Idle();
            Button Tool(string name) => window.TopToolbar.Children.OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == name);
            var lockButton = Tool("Lock or unlock FEM model");
            var toolButtons = window.TopToolbar.Children.OfType<Button>().ToArray();
            var resultTools = new[] { Tool("Show Deformed Shape F5"), Tool("Show Member Forces F6"), Tool("Show Joint Reactions F7") };
            Check(Array.IndexOf(toolButtons, lockButton) < Array.IndexOf(toolButtons, Tool("Run FEM Analysis")) &&
                  toolButtons.All(button => button.IsEnabled || button != lockButton),
                "the lock icon precedes Run Analysis and is available on the default model");
            Check(resultTools.All(button => button.Visibility == Visibility.Visible && !button.IsEnabled) &&
                  window.ResultMenu.Items.OfType<MenuItem>().Take(3).All(item => !item.IsEnabled),
                "F5/F6/F7 stay visible but disabled before analysis has results");
            vm.ToggleFemModelLock.Execute(null);
            Check(vm.IsFemModelLocked && vm.FemLockActionText.StartsWith("Unlock", StringComparison.Ordinal),
                "the toolbar lock command freezes the FEM model");
            vm.ToggleFemModelLock.Execute(null);
            Check(!vm.IsFemModelLocked && !vm.HasFemResults && vm.FemLockActionText.StartsWith("Lock", StringComparison.Ordinal),
                "unlock returns the model to edit mode with no output results");

            var loadsToggle = Descendants<ToggleButton>(window.SharedViewer)
                .Single(button => AutomationProperties.GetName(button) == "Show applied loads");
            loadsToggle.IsChecked = false;
            Idle();
            Check(!vm.ShowFemLoads && !window.SharedViewer.Canvas.ShowLoads,
                "the canvas L toggle hides applied load arrows through the same Display setting");
            loadsToggle.IsChecked = true;
            Idle();
            Check(vm.ShowFemLoads && window.SharedViewer.Canvas.ShowLoads, "the canvas L toggle restores applied loads");

            Tool("Show Results").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Idle();
            var picker = window.OwnedWindows.OfType<ShowResultsWindow>().Single();
            Check(picker.NoResults.IsVisible && !picker.ApplyButton.IsEnabled,
                "the result picker explains that analysis is required when no output exists");

            var result = new FemAnalysisResult([], [], [], [], 0);
            vm.FemRuns.Add(new FemStudyRun(FemStudyService.ModelRunId, "", "", "", result,
                Results: new Dictionary<string, FemAnalysisResult> { ["Case A"] = result, ["Case B"] = result }));
            vm.SelectedFemRun = vm.FemRuns[0];
            vm.SelectedFemResultName = "Case A";
            vm.Notify(nameof(MainViewModel.HasFemResults));
            Idle();
            Check(resultTools.All(button => button.Visibility == Visibility.Visible && button.IsEnabled) &&
                  window.ResultMenu.Items.OfType<MenuItem>().Take(3).All(item => item.IsEnabled),
                "F5/F6/F7 become enabled when analysis results are available");
            window.Width = window.MinWidth;
            window.UpdateLayout();
            var lastTool = toolButtons[^1];
            var lastPosition = lastTool.TransformToAncestor(window).Transform(new Point());
            Check(lastPosition.X + lastTool.ActualWidth <= window.ActualWidth,
                "the expanded toolbar fits within the minimum window width");
            window.Width = 1400;
            window.UpdateLayout();

            Tool("Show Deformed Shape F5").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Idle();
            Check(ReferenceEquals(picker, window.OwnedWindows.OfType<ShowResultsWindow>().Single()) &&
                  !picker.NoResults.IsVisible && picker.ApplyButton.IsEnabled &&
                  picker.PickerKind == ShowResultPickerKind.Deflection && picker.LoadCasePicker.IsVisible &&
                  picker.LoadCasePicker.Items.Count == 2 &&
                  picker.OutputGroup.Visibility == Visibility.Collapsed && (string)((ListBoxItem)picker.Outputs.SelectedItem).Tag == "DeflectedShape",
                "F5 opens a load-case picker fixed to deformed shape");
            picker.LoadCasePicker.SelectedItem = "Case B";
            picker.ApplySelection();
            Check(vm.FemResultMode == FemResultMode.DeflectedShape && vm.SelectedFemResultName == "Case B",
                "F5 applies the selected load case's deformed-shape result");

            Tool("Show Member Forces F6").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Idle();
            Check(ReferenceEquals(picker, window.OwnedWindows.OfType<ShowResultsWindow>().Single()) &&
                  picker.PickerKind == ShowResultPickerKind.MemberForces && picker.LoadCasePicker.IsVisible &&
                  picker.Outputs.Items.OfType<ListBoxItem>().Where(item => item.Visibility == Visibility.Visible)
                      .Select(item => (string)item.Tag).OrderBy(tag => tag)
                      .SequenceEqual(new[] { "AxialForce", "BendingMoment", "Shear" }.OrderBy(tag => tag)),
                "F6 reuses the picker and offers only axial, shear and moment diagrams");
            picker.Outputs.SelectedItem = picker.Outputs.Items.OfType<ListBoxItem>().Single(item => (string)item.Tag == "Shear");
            picker.ApplySelection();
            Check(vm.FemResultMode == FemResultMode.Shear, "F6 applies the selected member-force component");

            Tool("Show Joint Reactions F7").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Idle();
            Check(picker.PickerKind == ShowResultPickerKind.Reactions && picker.LoadCasePicker.IsVisible &&
                  picker.OutputGroup.Visibility == Visibility.Collapsed && (string)((ListBoxItem)picker.Outputs.SelectedItem).Tag == "Reactions",
                "F7 opens a load-case picker fixed to joint reactions");
            picker.ApplySelection();
            Check(vm.FemResultMode == FemResultMode.Reactions, "F7 applies the joint-reaction result");
            void Press(Key key, ShowResultPickerKind expected)
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                window.RaiseEvent(args);
                Check(args.Handled && picker.PickerKind == expected, $"{key} opens its matching result picker");
            }
            Press(Key.F5, ShowResultPickerKind.Deflection);
            Press(Key.F6, ShowResultPickerKind.MemberForces);
            Press(Key.F7, ShowResultPickerKind.Reactions);
            picker.Close();
            vm.ToggleFemModelLock.Execute(null);
            vm.ToggleFemModelLock.Execute(null);
            Idle();
            Check(!vm.HasFemResults && resultTools.All(button => button.Visibility == Visibility.Visible && !button.IsEnabled),
                "unlock removes results and greys out F5/F6/F7 again");
        }
        finally { window.Close(); }
    }

    private static void Idle() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }

    private sealed class NoSelection : ISelectionService
    {
        public IReadOnlyList<string>? Select(string title, IReadOnlyList<string> available, IReadOnlyList<string> selected) => null;
    }
}
