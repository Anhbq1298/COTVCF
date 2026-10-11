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
using COT_VCF.Application;
using COT_VCF.Configuration;
using COT_VCF.Domain;
using COT_VCF.Geometry;
using COT_VCF.Infrastructure;
using COT_VCF.Presentation;
using COT_VCF.Presentation.Controls;
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
            Check(window.Title == MainViewModel.ProductTitle + " — Default model" && window.Title == vm.WindowTitle,
                "the window title shows the product and, beside it, what is open: the default model");
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
            Check3DDeflectedShape();

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
