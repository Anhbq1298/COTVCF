using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Infrastructure;
using Mcotis.Desktop.Presentation;
using Mcotis.Desktop.Presentation.Controls;
using Mcotis.Desktop.SAP2000;

internal static class ResultToolbarChecks
{
    internal static void RunSpatial3D()
    {
        CheckSpatialDeflectedShape();
        Check3DCentreLineDisplay();
    }

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
            Check(window.Title == MainViewModel.ProductTitle + " — Default model" && window.Title == vm.WindowTitle,
                "the window title shows the product and, beside it, what is open: the default model");

            // Assign → Joint → Restraints: the dialog shows what the selected joints hold and writes what it is set to
            var bases = vm.FemPinSupportPoints;
            var restraintForm = new JointRestraintWindow(vm, [bases[0]]) { Owner = window };
            restraintForm.Show(); Idle();
            var heldBefore = restraintForm.FormRestraint;
            restraintForm.Show(Mcotis.Core.Restraint.Fixed);
            bool applied = restraintForm.ApplyForm();
            restraintForm.Close();
            Check(heldBefore == Mcotis.Core.Restraint.Pinned && applied && vm.FemJointRestraints[bases[0]] == Mcotis.Core.Restraint.Fixed &&
                  window.ResultMenu.Parent is Menu menu && menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "_Assign") &&
                      item.Items.OfType<MenuItem>().Any(joint => joint.Items.OfType<MenuItem>().Any(entry => Equals(entry.Header, "_Restraints…")))),
                $"Assign → Joint → Restraints opens on what the joint holds (the pin of {bases[0]}) and makes it fixed — the model holds the fixed base");
            vm.AssignJointRestraints([bases[0]], Mcotis.Core.Restraint.Pinned);
            // Strand Bundle: the section from its strands — number × steel area of one — named by the convention
            var bundleForm = new StrandSectionWindow(vm, new StrandSectionProperty { Name = "192T15.7", Area = 0.028802317797652074 }, new HashSet<string>()) { Owner = window };
            bundleForm.Show();
            var bundleOpened = bundleForm.StrandBundleShown;
            var bundleUntouched = bundleForm.Read(out _);
            bundleForm.SetStrands("192", 15.7.ToString(CultureInfo.CurrentCulture));
            var bundleSame = bundleForm.Read(out _);
            bundleForm.SetStrands("37", 15.2.ToString(CultureInfo.CurrentCulture));
            var bundleListed = bundleForm.Read(out _);
            var bundleBoxesLocked = Descendants<TextBox>(bundleForm).Where(box => System.Windows.Automation.AutomationProperties.GetName(box) is { } name &&
                (name.StartsWith("Diameter", StringComparison.Ordinal) || name == "Area")).All(box => !box.IsEnabled);
            bundleForm.SetStrands("61", 16.5.ToString(CultureInfo.CurrentCulture));
            var bundleUnlisted = bundleForm.Read(out var bundleAsks);
            bundleForm.SetStrands("61", 16.5.ToString(CultureInfo.CurrentCulture), "165");
            var bundleEntered = bundleForm.Read(out _);
            Check(bundleOpened is { On: false, Count: "192", StrandAreaMm2: "150" } && bundleUntouched is { Name: "192T15.7" } && Math.Abs(bundleUntouched.Area - 0.0288023178) < 1e-12 &&
                   bundleSame is { Name: "192T15.7" } && Math.Abs(bundleSame.Area - 0.0288) < 1e-12 &&
                   bundleListed is { Name: "37T15.2" } && Math.Abs(bundleListed.Area - 37 * 139e-6) < 1e-12 && bundleBoxesLocked &&
                   bundleUnlisted is null && bundleAsks.Contains("not a listed size", StringComparison.Ordinal) &&
                   bundleEntered is { Name: "61T16.5" } && Math.Abs(bundleEntered.Area - 61 * 165e-6) < 1e-12 &&
                   bundleForm.StrandBundleShown.Summary.Contains("61T16.5", StringComparison.Ordinal),
                "Cable Section Data → Strand Bundle: a section named 192T15.7 opens with its strands filled in and nothing changed; ticked, the area is " +
                "number × steel area of one strand (192 × 150 mm² = 0.0288 m²; 37 × 139 for 15.2 mm) and the name follows the convention (37T15.2); " +
                "a strand that is not listed asks for its steel area (61 × 165 mm² → 61T16.5)");
            bundleForm.Close();
            var toolNames = toolButtons.Select(AutomationProperties.GetName).ToArray();
            var fileItems = ((MenuItem)window.MainMenu.Items[0]).Items.OfType<MenuItem>().ToArray();
            ICommand? Shortcut(ModifierKeys modifiers) => window.InputBindings.OfType<KeyBinding>()
                .SingleOrDefault(binding => binding.Gesture is KeyGesture { Key: Key.S } gesture && gesture.Modifiers == modifiers)?.Command;
            Check(Array.IndexOf(toolNames, "Save Config As") == Array.IndexOf(toolNames, "Save Config") + 1 &&
                  ReferenceEquals(Tool("Save Config").Command, vm.SaveFemModel) && ReferenceEquals(Tool("Save Config As").Command, vm.SaveFemModelAs) &&
                  Tool("Save Config As").IsEnabled &&
                  fileItems.Single(item => ReferenceEquals(item.Command, vm.SaveFemModel)).InputGestureText == "Ctrl+S" &&
                  fileItems.Single(item => ReferenceEquals(item.Command, vm.SaveFemModelAs)).InputGestureText == "Ctrl+Shift+S" &&
                  Array.IndexOf(fileItems, fileItems.Single(item => ReferenceEquals(item.Command, vm.SaveFemModelAs))) ==
                      Array.IndexOf(fileItems, fileItems.Single(item => ReferenceEquals(item.Command, vm.SaveFemModel))) + 1 &&
                  ReferenceEquals(Shortcut(ModifierKeys.Control), vm.SaveFemModel) && ReferenceEquals(Shortcut(ModifierKeys.Control | ModifierKeys.Shift), vm.SaveFemModelAs),
                "Save Config As sits beside Save on the toolbar and in the File menu; Ctrl+S saves and Ctrl+Shift+S saves as");
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

            var loadPattern = vm.FrameLoadPatternNames.First();
            Check(vm.AssignFrameDistributedLoad(["170"], loadPattern, Mcotis.Core.FrameLoadType.Force, Mcotis.Core.LoadDirection.GlobalZ,
                    projected: false, Mcotis.Core.LoadDistanceType.Relative, [], uniform: 12, Mcotis.Core.LoadAssignMode.Replace) == 1,
                "Assign Frame Load writes one distributed load to the native model");
            Idle();
            var displayedFrameLoad = vm.SharedViewerFrameLoads.Single(load => load.MemberName == "170" && load.PatternName == loadPattern);
            var loadInformation = new ObjectInformationWindow(vm, window.SharedViewer.Model.Members.Single(member => member.Name == "170"), window.SharedViewer.Model);
            var loadRows = loadInformation.Shown.Single(tab => tab.Title == "Loads").Sections.SelectMany(section => section.Rows).ToArray();
            window.SharedViewer.ShowPlane("3D"); Idle();
            Check(displayedFrameLoad is { ValueStart: 12, ValueEnd: 12, RelativeStart: 0, RelativeEnd: 1 } && displayedFrameLoad.PatternName == loadPattern &&
                  window.SharedViewer.Canvas.FrameLoads?.Any(load => load.MemberName == "170") == true &&
                  loadRows.Any(row => row.Name == loadPattern && row.Value.Contains("12 … 12 kN/m", StringComparison.Ordinal)) &&
                  ((Canvas)window.SharedViewer.FindName("FrameLoads3D")!).Children.Count > 0,
                "Display → Loading shows native frame loads in 2D and 3D; Frame Information lists the same pattern, direction, range and values");
            window.SharedViewer.ShowPlane("XZ"); Idle();

            var axesToggle = Descendants<ToggleButton>(window.SharedViewer)
                .Single(button => AutomationProperties.GetName(button) == "Show frame local axes");
            axesToggle.IsChecked = true;
            Idle();
            Check(vm.ShowFrameLocalAxes && window.SharedViewer.Canvas.ShowFrameLocalAxes,
                "the visualization A toggle enables frame local-axis markers in the 2D model view");
            window.SharedViewer.ShowPlane("3D");
            Idle();
            var axesVisual = (ModelVisual3D)window.SharedViewer.FindName("LocalAxes3D")!;
            var allAxes = (Model3DGroup)axesVisual.Content;
            var allTriangles = allAxes.Children.OfType<GeometryModel3D>()
                .Sum(model => ((MeshGeometry3D)model.Geometry).TriangleIndices.Count);
            window.SharedViewer.Canvas.ToggleMemberSelection("170");
            Idle();
            var selectedAxes = (Model3DGroup)axesVisual.Content;
            var selectedTriangles = selectedAxes.Children.OfType<GeometryModel3D>()
                .Sum(model => ((MeshGeometry3D)model.Geometry).TriangleIndices.Count);
            Check(allAxes.Children.Count == 3 && selectedAxes.Children.Count == 3 && selectedTriangles > 0 && selectedTriangles < allTriangles,
                "3D local-axis markers show axes 1/2/3 and isolate the selected frame");
            axesToggle.IsChecked = false;
            Idle();
            Check(!vm.ShowFrameLocalAxes && axesVisual.Content is null,
                "turning off the A toggle removes the local-axis overlay");
            var extrusionToggle = Descendants<ToggleButton>(window.SharedViewer)
                .Single(button => AutomationProperties.GetName(button) == "Extruded 3D members");
            extrusionToggle.IsChecked = false;
            Idle();
            Check(!vm.ShowExtruded3D && !window.SharedViewer.ShowExtruded3D,
                "the 3D E toggle and Display setting share the centre-line view state");
            window.SharedViewer.ClearSelection();
            var lineViewport = (Viewport3D)window.SharedViewer.FindName("View3D")!;
            var lineVisual = (ModelVisual3D)window.SharedViewer.FindName("Structure3D")!;
            Point LineScreen(Position position) => lineVisual.TransformToAncestor(lineViewport)!.Transform(new Point3D(position.X, position.Y, position.Z));
            var lineJointPositions = window.SharedViewer.Model.Points.Values.Select(point => LineScreen(point.Position)).ToArray();
            var lineMiddle = window.SharedViewer.Model.Members.Where(member => member.Kind == MemberKind.Frame)
                .Select(member => LineScreen(window.SharedViewer.Model.Points[member.Start].Position) +
                    (LineScreen(window.SharedViewer.Model.Points[member.End].Position) - LineScreen(window.SharedViewer.Model.Points[member.Start].Position)) / 2)
                .First(middle => lineJointPositions.All(joint => (joint - middle).Length > 12));
            Check(window.SharedViewer.Select3DAt(lineMiddle) && window.SharedViewer.SelectedMembers.Count == 1,
                "a 3D centre-line can still be selected near its projected axis");
            extrusionToggle.IsChecked = true;
            Idle();
            Check(vm.ShowExtruded3D && window.SharedViewer.ShowExtruded3D,
                "the 3D E toggle restores extruded member sections");
            window.SharedViewer.ClearSelection();
            Idle();
            // the 3D view extrudes the real sections; a composite section's enclosing material is see-through (front faces only)
            var structure3D = (ModelVisual3D)window.SharedViewer.FindName("Structure3D")!;
            int SeeThrough() => ((Model3DGroup)structure3D.Content).Children.OfType<GeometryModel3D>().Count(piece => piece.BackMaterial is null);
            var shells = SeeThrough();
            window.SharedViewer.Canvas.ToggleMemberSelection("TopChord");
            Idle();
            var shellsWhileSelected = SeeThrough();
            window.SharedViewer.ClearSelection();
            Idle();
            Check(window.SharedViewer.SectionDrawings is { Count: > 0 } && shells > 0 && shellsWhileSelected == shells - 1 && SeeThrough() == shells &&
                  Math.Abs(structure3D.Content.Bounds.Z + structure3D.Content.Bounds.SizeZ - 21.0) < 1e-6 &&
                  ((TextBlock)window.SharedViewer.FindName("View3DInfo")!).Text.Contains("composite sections see-through", StringComparison.Ordinal),
                "the 3D view shows the real sections of the default model (CFT chords 2.0 m deep, see-through tube around the concrete core); " +
                "selecting a composite member highlights it whole and deselecting it makes its shell see-through again");
            window.SharedViewer.ShowPlane("XZ");
            Idle();

            // right-click a member: its information, as ETABS shows it. The window opens the form modally, so a queued
            // action reads it and closes it.
            var column = window.SharedViewer.Canvas.MemberScreenLines["170"];
            ObjectInformationWindow? information = null;
            IReadOnlyList<InformationTab> informationTabs = [];
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                information = window.OwnedWindows.OfType<ObjectInformationWindow>().SingleOrDefault();
                if (information is null) return;
                informationTabs = information.Shown;
                information.Close();
            }));
            var asked = window.SharedViewer.Canvas.RequestInformationAt(column.Start + (column.End - column.Start) * 0.3);
            Idle();
            Check(asked && information?.Title == "Frame Information - 170" && informationTabs.Select(tab => tab.Title).SequenceEqual(["Geometry", "Assignments", "Loads"]) &&
                  informationTabs[0].Sections[0].Rows.Any(row => row is { Name: "Label", Value: "170" }) &&
                  window.SharedViewer.SelectedMembers.SequenceEqual(["170"]) && !window.OwnedWindows.OfType<ObjectInformationWindow>().Any() &&
                  !window.SharedViewer.Canvas.RequestInformationAt(new Point(3, 3)),
                "a right-click on a frame selects it and opens Frame Information with Geometry, Assignments and Loads; a right-click on empty space does nothing");
            window.SharedViewer.ClearSelection();
            CheckObjectSelection(window, vm);
            CheckIntegratedAssignments(window, vm);
            Check3DDeflectedShape();
            Check3DCentreLineDisplay();
            CheckSpatialDeflectedShape();

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
                  picker.PickerKind == ShowResultPickerKind.Deflection && picker.ShapePicker.IsVisible &&
                  picker.ShapeCasePicker.Items.Count == 2 &&
                  picker.DeformationOptions.IsVisible && picker.CaseRadio.IsChecked == true &&
                  picker.OutputGroup.Visibility == Visibility.Collapsed && (string)((ListBoxItem)picker.Outputs.SelectedItem).Tag == "DeflectedShape",
                "F5 opens the deformed-shape box with case selection, scaling, contours and shape options");
            picker.ShapeCasePicker.SelectedItem = picker.ShapeCasePicker.Items.Cast<object>().Single(item => item.ToString() == "Case B");
            picker.UserScale.IsChecked = true;
            picker.UserScale.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            picker.ScaleFactor.Text = "0";
            Check(!picker.ApplySelection() && picker.OptionError.Text.Contains("positive", StringComparison.Ordinal) &&
                  window.SharedViewer.DeflectionScaleFactor == 0,
                "an invalid user-defined magnification is refused before changing the view");
            picker.ScaleFactor.Text = "12";
            picker.ContourComponent.SelectedItem = picker.ContourComponent.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag == "UZ");
            picker.MinimumRange.Text = "-5";
            picker.MaximumRange.Text = "5";
            picker.WireShadow.IsChecked = false;
            picker.CubicCurve.IsChecked = false;
            picker.ApplySelection();
            Check(vm.FemResultMode == FemResultMode.DeflectedShape && vm.SelectedFemResultName == "Case B" &&
                  window.SharedViewer.DeflectionScaleFactor == 12 &&
                  window.SharedViewer.DeflectionContourComponent == DisplacementContourComponent.UZ &&
                  window.SharedViewer.DeflectionRangeMinMm == -5 && window.SharedViewer.DeflectionRangeMaxMm == 5 &&
                  !window.SharedViewer.ShowUndeformed && !window.SharedViewer.DeflectionCubicCurve,
                "F5 applies the selected case and display-only magnification, UZ range, wire shadow and curve style");

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

            // the Optimization menu follows the system that is open: the tied arch system, then the VCF system again
            var (optimizeVcf, optimizeTas) = (OptimizationItem(window, "Optimization VCF…"), OptimizationItem(window, "Optimization TAS…"));
            // … and what shows the fit of cable target forces is there for a model with cables only
            var fitItem = window.ResultMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "CableFit"));
            vm.ShowFemResult(null);   // the cable target fit was the result on show
            vm.LoadTasModel();
            Idle();
            bool onTas = vm.CanOptimizeTas && !vm.CanOptimizeVcf && optimizeTas.IsEnabled && !optimizeVcf.IsEnabled;
            var fitPicker = new ShowResultsWindow(vm, _ => { }) { Owner = window };
            var fitTable = new FemResultsWindow(vm) { Owner = window };
            var fitMonitor = new FemRunMonitorWindow(vm) { Owner = window };
            fitTable.Show(); fitMonitor.Show(); Idle();
            fitTable.SelectCableFit(true);
            ListBoxItem PickerFit() => fitPicker.Outputs.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, "CableFit"));
            bool hidden = !vm.HasCableMembers && !vm.FemCableFitView && fitItem.Visibility == Visibility.Collapsed && PickerFit().Visibility == Visibility.Collapsed &&
                          fitTable.CableFitTab.Visibility == Visibility.Collapsed && fitTable.ResultTopics.SelectedIndex == 0 && fitMonitor.ConvergenceTab.Visibility == Visibility.Collapsed;
            vm.ReplaceCurrentWithVcfModel();
            Idle();
            fitPicker.Configure(ShowResultPickerKind.All);
            bool shown = vm.HasCableMembers && fitItem.Visibility == Visibility.Visible && PickerFit().Visibility == Visibility.Visible &&
                         fitTable.CableFitTab.Visibility == Visibility.Visible && fitMonitor.ConvergenceTab.Visibility == Visibility.Visible;
            fitPicker.Close(); fitTable.Close(); fitMonitor.Close();
            Check(onTas && vm.CanOptimizeVcf && !vm.CanOptimizeTas && optimizeVcf.IsEnabled && !optimizeTas.IsEnabled,
                "with the tied arch system open Optimization TAS is offered and Optimization VCF is greyed; back on the VCF system it is the other way round");
            Check(hidden && shown,
                "the tied arch system has no cables: Result → Cable target fit, its choice in Show Results, its tab of the results table and the cable convergence chart of the run monitor are not shown; " +
                "on the VCF system, which has cables, they all are");
        }
        finally { window.Close(); }
    }

    private static MenuItem OptimizationItem(MainWindow window, string name) =>
        window.MainMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "_Optimization"))
            .Items.OfType<MenuItem>().Single(item => ((string)item.Header).Replace("_", "") == name);

    /// <summary>Selecting on the model view (asked for by the user, 2026-10-12): a left click chooses points as well as
    /// frames and cables, a right-click shows the information of a point too, V is the Select tool, Esc only clears, the 3D
    /// view selects like the 2D view, and the status bar counts what is selected. Also the Optimization tab of the menu bar.</summary>
    private static void CheckObjectSelection(MainWindow window, MainViewModel vm)
    {
        var viewer = window.SharedViewer;
        var canvas = viewer.Canvas;
        var status = (TextBlock)window.FindName("SelectionStatus")!;
        const string nothing = "Selected: 0 Frames · 0 Cables · 0 Points · 0 Shells";
        Check(status.Text == nothing && MainWindow.SelectionText(1, 2, 1, 0) == "Selected: 1 Frame · 2 Cables · 1 Point · 0 Shells",
            "the status bar counts the selected frames, cables, points and shells — none to begin with");

        // right-click a point: its information. The window opens the form modally, so a queued action reads it and closes it.
        (string? Title, IReadOnlyList<InformationTab> Tabs) Information(Func<bool> rightClick)
        {
            string? title = null;
            IReadOnlyList<InformationTab> tabs = [];
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                if (window.OwnedWindows.OfType<ObjectInformationWindow>().SingleOrDefault() is not { } form) return;
                (title, tabs) = (form.Title, form.Shown);
                form.Close();
            }));
            var asked = rightClick();
            Idle();
            return asked ? (title, tabs) : (null, []);
        }
        var corner = Information(() => canvas.RequestInformationAt(canvas.JointScreenPoints["TTrPt001"]));
        Check(corner.Title == "Point Information - TTrPt001" && corner.Tabs.Select(tab => tab.Title).SequenceEqual(["Geometry", "Assignments", "Loads"]) &&
              corner.Tabs[0].Sections.Select(section => section.Title).SequenceEqual(["Identification", "Coordinates", "Connectivity"]) &&
              corner.Tabs[0].Sections[0].Rows.Any(row => row is { Name: "Label", Value: "TTrPt001" }) &&
              corner.Tabs[0].Sections[2].Rows.Single(row => row.Name == "Frames").Value != "none" &&
              viewer.SelectedJoints.SequenceEqual(["TTrPt001"]) && viewer.SelectedMembers.Count == 0 &&
              status.Text == "Selected: 0 Frames · 0 Cables · 1 Point · 0 Shells" && !window.OwnedWindows.OfType<ObjectInformationWindow>().Any(),
            "a right-click on a point selects it and opens Point Information with its coordinates and the members that meet at it");
        var support = vm.FemPinSupportPoints.First();
        var loaded = vm.State.AppliedPoints.First().PointName;
        Check(vm.PointInformation(vm.State.Geometry.Points[support])[1].Sections[0].Rows[0].Value.StartsWith("Pin", StringComparison.Ordinal) &&
              vm.PointInformation(vm.State.Geometry.Points["TTrPt001"])[1].Sections[0].Rows[0].Value.StartsWith("None", StringComparison.Ordinal) &&
              vm.PointInformation(vm.State.Geometry.Points[loaded])[2].Sections.Single(section => section.Title == "Storey force").Rows is { Count: > 0 } storey &&
              storey.All(row => row.Value.StartsWith("Fz ", StringComparison.Ordinal)) &&
              vm.PointInformation(vm.State.Geometry.Points[support])[2].Sections[0].Rows[0] is { Name: "Assigned loads", Value: "none" },
            $"Point Information names a pin support ({support}), a free point, and the storey forces on a loaded point ({loaded})");

        // a left click chooses: points, frames and cables alike
        viewer.ClearSelection();
        var cable = canvas.MemberScreenLines.First(item => item.Value.Kind == MemberKind.Cable).Key;
        canvas.ToggleMemberSelection("170");
        canvas.ToggleMemberSelection(cable);
        Check(canvas.SelectAt(canvas.JointScreenPoints["TTrPt001"]) && canvas.SelectAt(canvas.JointScreenPoints["TTrPt002"]) &&
              status.Text == "Selected: 1 Frame · 1 Cable · 2 Points · 0 Shells",
            "left clicks add points to the frames and cables selected; the status bar follows");
        Check(canvas.SelectAt(canvas.JointScreenPoints["TTrPt002"]) && status.Text == "Selected: 1 Frame · 1 Cable · 1 Point · 0 Shells",
            "a second click on a selected point deselects it");

        // V is the Select tool; Esc clears the selection and leaves the tool alone
        void Press(Key key) => window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        canvas.Focus();
        viewer.PanMode = true;
        Press(Key.Escape);
        var afterEscape = (viewer.PanMode, status.Text);
        Press(Key.V);
        Check(afterEscape == (true, nothing) && !viewer.PanMode && canvas.Cursor == Cursors.Arrow,
            "Esc clears the selection and leaves the Pan tool on; V returns to the Select tool");

        // the 3D view selects as the 2D view does: a click on a point, a frame or a cable
        viewer.ShowPlane("3D");
        Idle();
        var view3D = (Viewport3D)viewer.FindName("View3D")!;
        var structure = (ModelVisual3D)viewer.FindName("Structure3D")!;
        var markers = (JointMarkers3D)viewer.FindName("Joints3D")!;
        Point OnScreen(Position position) => structure.TransformToAncestor(view3D)!.Transform(new Point3D(position.X, position.Y, position.Z));
        var cornerAt = OnScreen(viewer.Model.Points["TTrPt001"].Position);
        var picked = viewer.Select3DAt(cornerAt);
        Idle();
        Check(picked && viewer.SelectedJoints.SequenceEqual(["TTrPt001"]) && markers.Markers.Count == viewer.Model.Points.Count &&
              markers.Markers.Count(marker => marker.Selected) == 1 && (markers.Markers.Single(marker => marker.Selected).At - cornerAt).Length < 1.5 &&
              status.Text == "Selected: 0 Frames · 0 Cables · 1 Point · 0 Shells",
            "a click on a point of the 3D view selects it: every point is drawn there, the selected one marked where the camera shows it");
        var joints = viewer.Model.Points.Values.Select(point => OnScreen(point.Position)).ToArray();
        var clear = viewer.Model.Members.Where(member => member.Kind == MemberKind.Frame)
            .Select(member => OnScreen(viewer.Model.Points[member.Start].Position) + (OnScreen(viewer.Model.Points[member.End].Position) - OnScreen(viewer.Model.Points[member.Start].Position)) / 2)
            .First(middle => joints.All(joint => (joint - middle).Length > 12));
        Check(viewer.Select3DAt(clear) && viewer.SelectedMembers.Count == 1 && viewer.SelectedJoints.Count == 1 && status.Text.Contains("1 Point", StringComparison.Ordinal) &&
              !viewer.Select3DAt(new Point(2, 2)) && viewer.SelectedMembers.Count == 1,
            "a click on a member of the 3D view adds it to the selection; a click on empty space changes nothing");
        var corner3D = Information(() => viewer.RequestInformation3DAt(cornerAt));
        Check(corner3D.Title == "Point Information - TTrPt001" && viewer.SelectedJoints.SequenceEqual(["TTrPt001"]) && viewer.SelectedMembers.Count == 0,
            "a right-click on a point of the 3D view selects it alone and opens Point Information");
        viewer.ClearSelection();
        viewer.ShowPlane("XZ");
        Idle();
        Check(status.Text == nothing && markers.Markers.Count == 0, "leaving the 3D view takes its point markers away; nothing is selected");

        // the menu bar: Optimization has a tab of its own, set apart and highlighted, last on the bar (the MB menu is archived)
        var bar = window.MainMenu.Items.Cast<object>().ToArray();
        var optimization = window.MainMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "_Optimization"));
        var at = Array.IndexOf(bar, optimization);
        Check(bar[at - 1] is Separator && bar[at - 2] is MenuItem { Header: "_Help" } && at == bar.Length - 1 &&
              !window.MainMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "_MB")) &&
              ReferenceEquals(optimization.HeaderTemplate, window.FindResource("HighlightedMenuHeader")) &&
              optimization.Items.OfType<MenuItem>().Select(item => ((string)item.Header).Replace("_", "")).SequenceEqual(["Optimization VCF…", "Optimization TAS…", "Optimization Results…", "Compare with SAP2000…", "Cancel Optimization"]) &&
              !window.MainMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "_Run")).Items.OfType<MenuItem>()
                  .Any(item => (item.Header as string)?.Contains("Optimization", StringComparison.Ordinal) == true),
            "Optimization is a tab of its own on the menu bar — after a separator, highlighted, the last one — and Run no longer lists it");
        // each optimization varies its own system: on the VCF model only Optimization VCF is offered
        var (optimizeVcf, optimizeTas) = (OptimizationItem(window, "Optimization VCF…"), OptimizationItem(window, "Optimization TAS…"));
        Check(vm.CanOptimizeVcf && !vm.CanOptimizeTas && optimizeVcf.IsEnabled && !optimizeTas.IsEnabled && OptimizationItem(window, "Optimization Results…").IsEnabled &&
              ToolTipService.GetShowOnDisabled(optimizeTas) && ToolTipService.GetShowOnDisabled(optimizeVcf) && ((string)optimizeTas.ToolTip).StartsWith("For the tied arch system", StringComparison.Ordinal),
            "on a VCF system Optimization VCF is offered and Optimization TAS is greyed; its tooltip, shown while greyed, says which system it is for");

        // Optimization → Optimization TAS: the form starts from the TAS system with the storeys' ranges; it runs only on the tied arch system
        var tas = new OptimizationTasWindow(vm) { Owner = window };
        tas.Show();
        Idle();
        static string Kn(double value) => value.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        var startCount = tas.SpaceText;
        var loads = tas.LoadText;
        var limit = tas.LimitShown;
        tas.SetRange("Bays", "max", "14");
        Idle();
        Check(startCount.EndsWith($"{Kn(4 * 1296)} combinations", StringComparison.Ordinal) && limit.StartsWith("limit L/250 = 480.0 mm", StringComparison.Ordinal) &&
              loads.Contains($"SLS {Kn(9_000)} … {Kn(42_000)} kN", StringComparison.Ordinal) && loads.Contains($"ULS {Kn(12_600)} … {Kn(58_500)} kN", StringComparison.Ordinal) &&
              tas.SpaceText.EndsWith($"{Kn(2 * 4 * 1296)} combinations", StringComparison.Ordinal) && tas.OkButton.IsEnabled && !tas.RunButton.IsEnabled &&
              tas.FormSettings().Geometry["Bays"].Values().SequenceEqual([12.0, 14.0]) && tas.Preview.Model.Points.Count == 30,
            $"Optimization TAS opens on the TAS system with 5,184 combinations (h 2 … 5 m), the limit L/250 ({limit}) and the SLS / ULS point load range ({loads}); 12 … 14 bays doubles them; " +
            "Run waits for the tied arch system to be open");
        tas.Close();

        // with the elevated cable: Edit → Tied Arch System shows H0 and its Cable Force sheet; Optimization TAS has H0, the
        // tie's pretension and the tie among its section roles
        var elevated = EtabsImportChecks.NewViewModel(out _);
        elevated.ApplyTiedArchSystem(Mcotis.Desktop.Application.Etabs.TiedArchParameters.Default with { ElevatedCable = true });
        var edit = new TiedArchWindow(elevated) { Owner = window };
        edit.Show();
        Idle();
        var shown = edit.ElevatedCableBox.IsChecked == true && edit.CableHeightBox.Visibility == Visibility.Visible && edit.CableSheet.IsEnabled &&
                    edit.CableHeightBox.Text == "10" && edit.CablePretensionBox.Text == "20000" && edit.CableSectionText.Text == "192T15.7" && edit.Preview.Model.Members.Any(member => member.Kind == MemberKind.Cable);
        edit.ElevatedCableBox.IsChecked = false;
        Idle();
        var hidden = edit.CableHeightBox.Visibility == Visibility.Collapsed && !edit.CableSheet.IsEnabled && edit.Preview.Model.Members.All(member => member.Kind != MemberKind.Cable);
        edit.Close();
        var optimize = new OptimizationTasWindow(elevated) { Owner = window };
        optimize.Show();
        Idle();
        optimize.CableStepsBox.Text = "3";
        optimize.CableMaxBox.Text = "30000";
        Idle();
        var settings = optimize.FormSettings();
        Check(shown && hidden && optimize.CableSheet.IsEnabled && settings.ElevatedCable && settings.Geometry.ContainsKey("H0") && settings.CableStepCount == 3 &&
              optimize.SpaceText.EndsWith($"{Kn(4 * 1296 * 3)} combinations", StringComparison.Ordinal) && optimize.Preview.Model.Members.Any(member => member.Kind == MemberKind.Cable),
            "with the elevated cable, Edit → Tied Arch System shows H0 = 10 m and the tie (192T15.7, 20,000 kN) on Cable Force, and hides both without it; " +
            "Optimization TAS adds H0 and the tie's pretension steps (3 steps triple the cases)");
        optimize.Close();
    }

    /// <summary>One form per member (asked for by the user, 2026-10-12: the separate Member Assignments form was redundant):
    /// the information form is also where the assignments are changed — editors in place while the model is unlocked, the
    /// same lines read-only while it is locked.</summary>
    private static void CheckIntegratedAssignments(MainWindow window, MainViewModel vm)
    {
        var model = window.SharedViewer.Model;
        static string?[] Buttons(Window form) => Descendants<Button>(form).Select(button => button.Content as string).ToArray();
        FemSectionProperties Property(string name) => vm.FemSectionProperties.Single(item => item.MemberName == name);
        var column = model.Members.Single(member => member.Name == "170");
        var before = Property("170");

        var form = new ObjectInformationWindow(vm, column, model, openAssignments: true) { Owner = window };
        form.Show();
        Idle();
        Check(form.Title == "Frame Information - 170" && form.OpenTab == "Assignments" && form.Editable && form.Status == "" &&
              form.SectionEditor?.SelectedItem as string == before.SectionName && form.AngleEditor is { Text: "0" } &&
              form.ReleaseStartEditor?.IsChecked == before.ReleaseStartRotation && form.ReleaseEndEditor?.IsChecked == before.ReleaseEndRotation &&
              form.TargetForceEditor is null && Buttons(form).SequenceEqual(["OK", "Cancel", "Apply"]),
            "a double-click opens the member's one form on Assignments: section, local-axis angle and end releases are editors in place, with OK / Cancel / Apply");

        form.AngleEditor!.Text = 90.ToString(CultureInfo.CurrentCulture);
        form.ReleaseEndEditor!.IsChecked = !before.ReleaseEndRotation;
        var applied = form.Apply();
        Idle();
        var after = Property("170");
        Check(applied && after.LocalAngleDeg == 90 && after.ReleaseEndRotation == !before.ReleaseEndRotation && after.SectionName == before.SectionName &&
              form.Shown[1].Sections.SelectMany(section => section.Rows).Single(row => row.Edit == InformationEdit.LocalAxisAngle).Value.StartsWith("90°", StringComparison.Ordinal) &&
              form.AngleEditor is { Text: "90" } && form.OpenTab == "Assignments" && form.Status.StartsWith("Assignments updated", StringComparison.Ordinal),
            "Apply writes the changed angle and release to the model; the form stays open on Assignments and shows the model as it now is");
        form.AngleEditor!.Text = "steep";
        Check(!form.Apply() && form.Status.Contains("local-axis angle", StringComparison.Ordinal) && Property("170").LocalAngleDeg == 90,
            "an angle that is not a number is refused with the reason and nothing is written");
        form.AngleEditor!.Text = "0";
        form.ReleaseEndEditor!.IsChecked = before.ReleaseEndRotation;
        Check(form.Apply() && Property("170") is { LocalAngleDeg: 0 } restored && restored.ReleaseEndRotation == before.ReleaseEndRotation &&
              form.Apply() && form.Status == "Assignments unchanged.",
            "the member is set back the same way; Apply without a change says so");
        form.Close();

        var cable = model.Members.First(member => member.Kind == MemberKind.Cable && vm.State.Cables.Any(item => item.CableName == member.Name && !item.IsFollower));
        var target = vm.State.Cables.Single(item => item.CableName == cable.Name);
        var (range, force, minimum, pattern) = (target.ApplyRange, target.TargetForce, target.MinForce, target.LoadPattern);
        var cableForm = new ObjectInformationWindow(vm, cable, model, openAssignments: true) { Owner = window };
        cableForm.Show();
        Idle();
        var cableShown = (cableForm.Title, Section: cableForm.SectionEditor?.SelectedItem as string, Angle: cableForm.AngleEditor is null,
            Pattern: cableForm.TargetPatternEditor?.SelectedItem as string, Force: cableForm.TargetForceEditor?.Text);
        var cableApplied = cableForm.Apply();
        Check(cableShown.Title == $"Cable Information - {cable.Name}" && cableShown.Section == Property(cable.Name).SectionName && cableShown.Angle &&
              cableShown.Pattern == pattern && cableShown.Force is { Length: > 0 } && cableApplied && cableForm.Status == "Assignments unchanged." &&
              (target.ApplyRange, target.TargetForce, target.MinForce, target.LoadPattern) == (range, force, minimum, pattern),
            $"a cable's form ({cable.Name}) edits its section, target pattern and target force; applying without a change leaves the cable — a range included — as it was " +
            $"[{cableShown} · applied {cableApplied} · '{cableForm.Status}' · range {target.ApplyRange} force {target.TargetForce} min {target.MinForce}]");
        cableForm.Close();

        vm.ToggleFemModelLock.Execute(null);
        var locked = new ObjectInformationWindow(vm, column, model, openAssignments: true) { Owner = window };
        locked.Show();
        Idle();
        Check(vm.IsFemModelLocked && !locked.Editable && locked.SectionEditor is null && locked.AngleEditor is null && Buttons(locked).SequenceEqual(["Close"]) &&
              locked.Status == "Model locked. Unlock it to change member assignments." && locked.OpenTab == "Assignments" &&
              locked.Shown[1].Sections.SelectMany(section => section.Rows).Any(row => row.Edit == InformationEdit.Section && row.Value == before.SectionName),
            "while the model is locked the same form shows the assignments read-only, says why, and offers Close");
        locked.Close();
        vm.ToggleFemModelLock.Execute(null);
        Idle();

        var pointForm = new ObjectInformationWindow(vm, model.Points["TTrPt001"], model) { Owner = window };
        pointForm.Show();
        Idle();
        Check(!vm.IsFemModelLocked && !pointForm.Editable && pointForm.Status == "" && pointForm.OpenTab == "Geometry" && Buttons(pointForm).SequenceEqual(["OK"]),
            "a point's form has nothing to apply: it opens on Geometry and OK closes it");
        pointForm.Close();
    }

    /// <summary>The 3D view draws the deflected shape, with its legend and options, and a right-click on a member there asks
    /// for its information — on a two-span beam with a 20 mm sag.</summary>
    private static void Check3DDeflectedShape()
    {
        var beam = new GeometryModel(new Dictionary<string, StructuralPoint>(StringComparer.Ordinal)
        {
            ["A"] = new("A", new(0, 0, 0)), ["B"] = new("B", new(5, 0, 0)), ["C"] = new("C", new(10, 0, 0)),
        }, [new("AB", "A", "B", MemberKind.Frame), new("BC", "B", "C", MemberKind.Frame)]);
        static FemFrameStation Station(double fraction, double uz) => new(fraction, 0, 0, 0, 0, uz);
        var sag = new FemAnalysisResult([new("A", 0, 0, 0), new("B", 0, -0.02, 0), new("C", 0, 0, 0)], [],
        [
            new("AB", 0, 0, 0, 0, 0, 0, [Station(0, 0), Station(0.5, -0.012), Station(1, -0.02)]),
            new("BC", 0, 0, 0, 0, 0, 0, [Station(0, -0.02), Station(0.5, -0.012), Station(1, 0)]),
        ], [], 0);
        var box = new FemSectionShape(SectionShapeKind.Box, 0.6, 0.4, 0.03, 0.02);
        var viewer = new Structural2DViewer
        {
            Model = beam,
            MemberSections = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal)
            {
                ["AB"] = new("AB", 0.05, 2e8, 0.002, Shape: box), ["BC"] = new("BC", 0.05, 2e8, 0.002, Shape: box),
            },
        };
        var host = new Window { Content = viewer, Width = 720, Height = 460, ShowInTaskbar = false };
        try
        {
            host.Show();
            viewer.ShowPlane("3D");
            Idle();
            var structure = (ModelVisual3D)viewer.FindName("Structure3D")!;
            var info = (TextBlock)viewer.FindName("View3DInfo")!;
            var legend = (FrameworkElement)viewer.FindName("Deflection3DLegend")!;
            var asBuilt = structure.Content.Bounds;
            Check(info.Text.StartsWith("Isometric 3D", StringComparison.Ordinal) && !legend.IsVisible && ((Model3DGroup)structure.Content).Children.Count == 2,
                "the 3D view shows the model as built while no deflected shape is asked for");

            viewer.FemResult = sag;
            viewer.FemResultMode = FemResultMode.DeflectedShape;
            Idle();
            Check(info.Text.StartsWith("Deflected shape 3D", StringComparison.Ordinal) && info.Text.Contains("20 mm", StringComparison.Ordinal) && legend.IsVisible &&
                  ((TextBlock)viewer.FindName("Deflection3DRange")!).Text == "|U| 0 to 20 mm" &&
                  ((TextBlock)viewer.FindName("Deflection3DScale")!).Text == "Shape magnified × 25" &&
                  ((Model3DGroup)structure.Content).Children.Count == 3 && Math.Abs(structure.Content.Bounds.Z - (asBuilt.Z - 0.5)) < 1e-6,
                "with a deflected shape shown, the 3D view draws it (20 mm at mid-span, × 25 = 0.5 m down), the model as built as a guide, and the |U| legend");

            viewer.ShowUndeformed = false;
            viewer.ShowDeflectionContour = false;
            Idle();
            Check(((Model3DGroup)structure.Content).Children.Count == 2 && !legend.IsVisible,
                "the Undeformed guide and Contour |U| options act on the 3D deflected shape too");

            string? requested = null;
            viewer.MemberInformationRequested += (_, name) => requested = name;
            var member = ((Model3DGroup)structure.Content).Children[0].Bounds;
            var centre = new Point3D(member.X + member.SizeX / 2, member.Y + member.SizeY / 2, member.Z + member.SizeZ / 2);
            var onScreen = structure.TransformToAncestor((Viewport3D)viewer.FindName("View3D")!)!.Transform(centre);
            Check(viewer.RequestInformation3DAt(onScreen) && requested == "AB" && viewer.SelectedMembers.SequenceEqual(["AB"]) &&
                  !viewer.RequestInformation3DAt(new Point(2, 2)),
                "a right-click on a member of the 3D deflected shape selects it and asks for its information; empty space does nothing");

            viewer.FemResultMode = FemResultMode.AxialForce;
            Idle();
            Check(info.Text.StartsWith("Isometric 3D", StringComparison.Ordinal) && Math.Abs(structure.Content.Bounds.Z - asBuilt.Z) < 1e-9,
                "leaving the deflected shape returns the 3D view to the model as built");
        }
        finally { host.Close(); }
    }

    private static void CheckSpatialDeflectedShape()
    {
        var beam = new GeometryModel(new Dictionary<string, StructuralPoint>(StringComparer.Ordinal)
        {
            ["A"] = new("A", new(0, 0, 0)), ["B"] = new("B", new(10, 0, 0)),
        }, [new("AB", "A", "B", MemberKind.Frame)]);
        var box = new FemSectionShape(SectionShapeKind.Rectangle, 0.1, 0.1);
        var viewer = new Structural2DViewer
        {
            Model = beam,
            MemberSections = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal)
                { ["AB"] = new("AB", 0.01, 2e8, 0.001, Shape: box) },
        };
        var host = new Window { Content = viewer, Width = 720, Height = 460, ShowInTaskbar = false };
        try
        {
            host.Show();
            viewer.ShowPlane("3D");
            Idle();
            var structure = (ModelVisual3D)viewer.FindName("Structure3D")!;
            var markers = (JointMarkers3D)viewer.FindName("Joints3D")!;
            var card = (Border)viewer.FindName("Joint3DReadout")!;
            var values = (TextBlock)viewer.FindName("Joint3DReadoutValues")!;
            var staticResult = new FemAnalysisResult(
                [new("A", 0, 0, 0) { RXRad = 0.003 }, new("B", 0, 0, 0)], [],
                [new("AB", 0, 0, 0, 0, 0, 0,
                    [new FemFrameStation(0, 0, 0, 0, 0, 0),
                     new FemFrameStation(0.5, 0, 0, 0, 0, 0) { UYM = 0.02 },
                     new FemFrameStation(1, 0, 0, 0, 0, 0)])], [], 0);
            viewer.FemResult = staticResult;
            viewer.FemResultMode = FemResultMode.DeflectedShape;
            Idle();
            Check(structure.Content.Bounds.Y + structure.Content.Bounds.SizeY > 0.45,
                "the 3D deflected frame uses native out-of-plane station UY, not a planar station with linearly interpolated Y");
            viewer.ShowJoint3DReadout(markers.Markers[0].At);
            Check(card.Visibility == Visibility.Visible && values.Text.Contains("UX", StringComparison.Ordinal) &&
                  values.Text.Contains("UY", StringComparison.Ordinal) && values.Text.Contains("UZ", StringComparison.Ordinal) &&
                  values.Text.Contains("RX 3.0000", StringComparison.Ordinal) && values.Text.Contains("RY", StringComparison.Ordinal) &&
                  values.Text.Contains("RZ", StringComparison.Ordinal) && values.Text.Contains("[mm]", StringComparison.Ordinal) &&
                  values.Text.Contains("[mrad]", StringComparison.Ordinal),
                "hovering a 3D joint shows all six global components with physical units in the upper-left card");
            viewer.ShowJoint3DReadout(new Point(2, 2));
            Check(card.Visibility == Visibility.Collapsed, "the 3D joint card hides when the cursor leaves the joint");

            viewer.DeflectionScaleFactor = 10;
            viewer.DeflectionContourComponent = DisplacementContourComponent.UY;
            viewer.DeflectionRangeMinMm = -10;
            viewer.DeflectionRangeMaxMm = 20;
            Idle();
            var componentLegend = (TextBlock)viewer.FindName("Deflection3DRange")!;
            Check(structure.Content.Bounds.Y + structure.Content.Bounds.SizeY is > 0.15 and < 0.35 &&
                  componentLegend.Text.Contains("UY -10 to 20 mm", StringComparison.Ordinal),
                "user-defined scale and signed UY contours act on the 3D deformed shape with mm units");
            viewer.DeflectionCubicCurve = false;
            Idle();
            Check(structure.Content.Bounds.Y + structure.Content.Bounds.SizeY < 0.1,
                "turning cubic curve off connects displaced frame joints without the interior station bend");
            viewer.DeflectionScaleFactor = 0;
            viewer.DeflectionCubicCurve = true;

            // Zero joint translations still allow a modal frame to bend from its end rotations.
            viewer.FemResult = new FemAnalysisResult(
                [new("A", 0, 0, 0) { RZRad = 0.01 }, new("B", 0, 0, 0) { RZRad = -0.01 }], [], [], [], 0);
            viewer.DeflectionIsModeShape = true;
            Idle();
            Check(structure.Content.Bounds.Y + structure.Content.Bounds.SizeY > 0.45,
                "a spatial mode shape curves between frame joints using RZ even when both joint translations are zero");
            viewer.ShowJoint3DReadout(markers.Markers[0].At);
            Check(card.Visibility == Visibility.Visible && values.Text.Contains("[relative]", StringComparison.Ordinal) &&
                  values.Text.Contains("RZ 0.01", StringComparison.Ordinal),
                "modal hover values are marked relative rather than given physical displacement units");
        }
        finally { host.Close(); }
    }

    private static void Check3DCentreLineDisplay()
    {
        var model = new GeometryModel(new Dictionary<string, StructuralPoint>(StringComparer.Ordinal)
        {
            ["A"] = new("A", new(0, 0, 0)), ["B"] = new("B", new(10, 0, 0)), ["C"] = new("C", new(10, 4, 0)),
        }, [new("AB", "A", "B", MemberKind.Frame), new("BC", "B", "C", MemberKind.Cable)]);
        var viewer = new Structural2DViewer
        {
            Model = model,
            MemberSections = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal)
                { ["AB"] = new("AB", 1, 2e8, 0.1, Shape: new FemSectionShape(SectionShapeKind.Rectangle, 1.5, 1.2)) },
        };
        var host = new Window { Content = viewer, Width = 720, Height = 460, ShowInTaskbar = false };
        try
        {
            host.Show();
            viewer.ShowPlane("3D");
            Idle();
            var visual = (ModelVisual3D)viewer.FindName("Structure3D")!;
            var info = (TextBlock)viewer.FindName("View3DInfo")!;
            var thick = ((Model3DGroup)visual.Content).Children.OfType<GeometryModel3D>().First().Bounds.SizeY;
            viewer.ShowExtruded3D = false;
            Idle();
            var lines = ((Model3DGroup)visual.Content).Children.OfType<GeometryModel3D>().ToArray();
            Check(thick > 1 && lines.Length == 2 && lines[0].Bounds.SizeY < 0.1 &&
                  info.Text.Contains("member centre-lines", StringComparison.Ordinal),
                "3D line view replaces frame section extrusion and cable size with thin centre-lines");
            viewer.FemResult = new FemAnalysisResult([new("A", 0, 0, 0), new("B", 0, 0, 0), new("C", 0, 0, 0)], [],
                [new("AB", 0, 0, 0, 0, 0, 0,
                    [new FemFrameStation(0, 0, 0, 0, 0, 0), new FemFrameStation(0.5, 0, 0, 0, 0, -0.02),
                     new FemFrameStation(1, 0, 0, 0, 0, 0)])], [], 0);
            viewer.FemResultMode = FemResultMode.DeflectedShape;
            Idle();
            var deformedLine = ((Model3DGroup)visual.Content).Children.OfType<GeometryModel3D>().First();
            Check(deformedLine.Bounds.SizeY < 0.1 && deformedLine.Bounds.Z < -0.45 &&
                  info.Text.Contains("centre-lines", StringComparison.Ordinal),
                "3D line view follows the curved deformed result instead of reverting to extruded sections");
            viewer.ShowExtruded3D = true;
            Idle();
            Check(((Model3DGroup)visual.Content).Children.OfType<GeometryModel3D>().First().Bounds.SizeY > 1,
                "the 3D display option can restore the original extruded section view");
        }
        finally { host.Close(); }
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
