using COT_VCF.Application;
using COT_VCF.Configuration;
using COT_VCF.Domain;
using COT_VCF.Geometry;
using COT_VCF.SAP2000;
using COT_VCF.Presentation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
using System.Diagnostics;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var liveMatrixIndex = Array.IndexOf(args, "--study-live-matrix");
            var liveStudyIndex = liveMatrixIndex >= 0 ? liveMatrixIndex : Array.IndexOf(args, "--study-live");
            if (liveStudyIndex >= 0)
            {
                if (liveStudyIndex + 1 >= args.Length) throw new ArgumentException("--study-live requires a baseline .sdb path.");
                LiveStudyCheck.RunAsync(args[liveStudyIndex + 1], liveMatrixIndex >= 0).GetAwaiter().GetResult();
                return 0;
            }
            if (args.Contains("--fem-property-read"))
            {
                using var sap = new SapGateway();
                var info = sap.ConnectAsync().GetAwaiter().GetResult();
                var snapshot = sap.ReadAsync().GetAwaiter().GetResult();
                var members = snapshot.Geometry.Members
                    .Where(member => member.Kind is MemberKind.Frame or MemberKind.Cable).ToArray();
                var properties = sap.GetFemSectionPropertiesAsync(members, info).GetAwaiter().GetResult();
                var missing = members.Where(member => !properties.ContainsKey(member.Name)).Select(member => member.Name).ToArray();
                if (missing.Length > 0) throw new InvalidOperationException("FEM properties were not returned for: " + string.Join(", ", missing));
                Console.WriteLine($"PASS: read FEM section/material properties for {properties.Count} frame/cable members from {info.Name}; no model writes performed.");
                return 0;
            }
            if (args.Contains("--study-ui"))
            {
                var app = new System.Windows.Application();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/COT VCF tools;component/src/Presentation/Theme.xaml", UriKind.Relative) });
                var bindingLog = new StringWriter();
                var bindingListener = new TextWriterTraceListener(bindingLog);
                PresentationTraceSources.DataBindingSource.Listeners.Add(bindingListener);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

                var uiState = StudyComposerChecks.CreateState();
                var femPoints = uiState.Geometry.Points.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                femPoints["bot2"] = new("bot2", new(4, 0, 0));
                femPoints["mid2"] = new("mid2", new(4, 0, 7.5));
                var femGeometry = new GeometryModel(femPoints, uiState.Geometry.Members.Concat([
                    new StructuralMember("Column1", "bot", "mid", MemberKind.Frame),
                    new StructuralMember("Column2", "bot2", "mid2", MemberKind.Frame)
                ]).ToArray());
                uiState.Geometry = femGeometry;
                uiState.Baseline = femGeometry;
                uiState.SessionBaseline = femGeometry;
                var uiSap = new FakeSap { Snapshot = new(uiState.Model!, uiState.Geometry, uiState.LoadPatterns.ToArray()) };
                var uiWorkflow = new WorkflowService(uiSap, uiState);
                var uiConfig = new COT_VCF.Configuration.ConfigService(uiSap, uiState, uiWorkflow);
                var vm = new MainViewModel(uiState, uiWorkflow, new NoSelection(), uiConfig) { SelectedTab = 0 };
                var window = new COT_VCF.Presentation.MainWindow { DataContext = vm, Width = 1400, Height = 810 };
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                uiState.Connection = "Disconnected";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                MenuItem TopMenu(string header) => window.MainMenu.Items.OfType<MenuItem>().Single(item => (item.Header as string)?.Replace("_", "") == header);
                Assert(window.MainMenu.Items.OfType<MenuItem>().Select(item => (item.Header as string)!.Replace("_", ""))
                           .SequenceEqual(["File", "MB", "Edit", "Define", "Assign", "Run", "Result", "Display", "Help"]),
                    "The menu bar reads File, MB, Edit, Define, Assign, Run, Result, Display, Help");
                Assert(!FindVisuals<TabControl>(window).First(tabs => tabs.Name == "WorkflowTabs").IsEnabled &&
                       !vm.NewConfig.CanExecute(null) && !TopMenu("MB").IsEnabled && !TopMenu("Define").IsEnabled && TopMenu("File").IsEnabled,
                    "Workflow pages, the MB/Define menus and New Config are disabled before SAP connection; File stays open");
                uiState.Connection = "Connected";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert(FindVisuals<TabControl>(window).First(tabs => tabs.Name == "WorkflowTabs").IsEnabled &&
                       vm.NewConfig.CanExecute(null) && TopMenu("MB").IsEnabled && TopMenu("Define").IsEnabled,
                    "Successful SAP connection enables workflow pages, the menus and New Config");
                var mbItems = TopMenu("MB").Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray();
                var fileCommands = TopMenu("File").Items.OfType<MenuItem>().Select(item => item.Command).ToArray();
                var runCommands = TopMenu("Run").Items.OfType<MenuItem>().Select(item => item.Command).ToArray();
                Assert(mbItems.SequenceEqual(["01  Loading", "02  Truss Height", "03  Pretension", "04  Output Settings", "05  Iteration",
                           "06  Post-Processing", "06A  Optimization", "08  Quantities"]) &&
                       new[] { vm.NewConfig, vm.LoadConfig, vm.SaveAllConfig, vm.LoadFemModel, vm.SaveFemModel, vm.ReadFemProperties }.All(fileCommands.Contains) &&
                       new[] { vm.RunStudyFromToolbar, vm.RunFemAnalysis, vm.CancelFemAnalysis }.All(runCommands.Contains),
                    "MB lists pages 01-06A and 08; File holds the config and model commands; Run holds the run commands");
                var workflowTabsRail = FindVisuals<TabControl>(window).First(tabs => tabs.Name == "WorkflowTabs");
                Assert(workflowTabsRail.Items.OfType<TabItem>().All(tab => tab.Visibility == Visibility.Collapsed) &&
                       vm.PaneMenu == "MB" && vm.PaneTitle == "01  LOADING",
                    "The left tab rail is hidden; the pane header names the page opened from MB");
                window.Width = window.MinWidth;
                window.UpdateLayout();
                var toolbarCommands = new[] { vm.Connect, vm.Refresh };
                var toolbarButtons = FindVisuals<Button>(window).Where(button => toolbarCommands.Contains(button.Command)).ToArray();
                var movedCommands = new[] { vm.NewConfig, vm.SaveAllConfig, vm.LoadConfig, vm.RunStudyFromToolbar };
                Assert(toolbarButtons.Length == 2 && toolbarButtons.All(button =>
                {
                    var position = button.TransformToAncestor(window).Transform(new Point());
                    return position.X >= 0 && position.X + button.ActualWidth <= window.ActualWidth;
                }) && movedCommands.Take(3).All(command => FindVisuals<Button>(window.TopToolbar).Any(button => ReferenceEquals(button.Command, command))) &&
                       FindVisuals<Button>(window.TopToolbar).Any(button => ReferenceEquals(button.Command, vm.RunFemAnalysis)) &&
                       FindVisuals<Button>(window.TopToolbar).Select(button => System.Windows.Automation.AutomationProperties.GetName(button)).ToArray() is var toolNames &&
                       new[] { "Zoom extents", "Zoom in", "Zoom out", "3D view", "XY view", "XZ view", "YZ view", "View Options" }.All(toolNames.Contains),
                    "The top bar keeps Connect SAP and Refresh within the minimum window width; the tool icons (new/open/save/print, run, zoom, views) sit on the same line");
                window.Width = 1400;
                window.UpdateLayout();
                var loadPatternPickers = FindVisuals<ComboBox>(window)
                    .Where(combo => combo.Items.Cast<object>().OfType<string>().Contains("LIVE"))
                    .ToArray();
                Assert(loadPatternPickers.Length == uiState.LoadDefinitions.Count && loadPatternPickers.All(combo => !combo.IsEditable),
                    "Tab 1 renders one read-only SAP load-pattern dropdown per definition");
                var loadingBitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                loadingBitmap.Render(window);
                var loadingEncoder = new PngBitmapEncoder(); loadingEncoder.Frames.Add(BitmapFrame.Create(loadingBitmap));
                Directory.CreateDirectory(".work");
                using (var stream = File.Create(".work/loading-tab.png")) loadingEncoder.Save(stream);

                vm.SelectedTab = 2;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var refreshCableSectionsButton = FindVisuals<Button>(window)
                    .Single(button => button.Name == "RefreshCableSectionsButton");
                Assert(refreshCableSectionsButton.Content is Viewbox && vm.RefreshCableSections.CanExecute(null),
                    "Cable tab renders an enabled icon-only section/area refresh action");
                vm.RefreshCableSections.Execute(null);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var refreshedCable = uiState.Cables.Single();
                Assert(uiSap.CableSectionReadCount == 1 && uiSap.Writes == 0 &&
                       refreshedCable.SectionName == "SEC_CABLE" &&
                       Math.Abs((refreshedCable.SectionAreaM2 ?? 0) - 0.001) < 1e-12 &&
                       Math.Abs((refreshedCable.DesignCapacityKN ?? 0) - 930) < 1e-9,
                    "Cable section refresh reads SAP once and updates section, area, and capacity without writing the model");
                vm.CableStrengthMpa = 2160;
                Assert(Math.Abs((refreshedCable.DesignCapacityKN ?? 0) - 1080) < 1e-9,
                    "Changing fpu immediately recomputes the derived cable design capacity");
                var cableGrid = FindVisuals<DataGrid>(window).Single(grid => grid.Name == "Cables");
                var immutableCableColumns = cableGrid.Columns.Where(column =>
                    column.Header is TextBlock header && header.Text is "Section" or "Area m²" or "Capacity kN").ToArray();
                Assert(immutableCableColumns.Length == 3 && immutableCableColumns.All(column =>
                           column is DataGridTextColumn && column.IsReadOnly && column.CellStyle?.Setters
                               .OfType<Setter>().Any(setter => setter.Property == DataGridCell.BackgroundProperty) == true),
                    "Section, area, and capacity render as grey read-only text columns");

                vm.SelectedTab = 4;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert(vm.StudyIterations.Count == 36, "Tab 5 UI binds the expected Fix Pretension rows");
                vm.RequestedWorkerCount = 3;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert(FindVisuals<TextBlock>(window).Any(text => text.Text == "Total SAP2000 Instances") &&
                       vm.RunStudyToolTip.Contains("starts 2 additional processes", StringComparison.Ordinal) &&
                       vm.RunStudyLabel.StartsWith("Build & Analyze", StringComparison.Ordinal),
                    "Tab 5 counts the baseline as instance 1 and clearly labels build plus analysis");
                var pretensionPicker = FindVisuals<ComboBox>(window)
                    .Single(combo => combo.Items.Cast<object>().OfType<StudyPretensionCandidate>().Any());
                pretensionPicker.SelectedValue = "P02";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert(vm.FixedPretensionCandidateId == "P02" &&
                       Equals(pretensionPicker.SelectedValue, "P02") &&
                       pretensionPicker.SelectedItem is StudyPretensionCandidate { Id: "P02" } &&
                       vm.StudyIterations.All(row => row.PretensionCandidateId == "P02"),
                    "Tab 5 switches directly from P01 to P02 without clearing the ComboBox");
                uiState.Study.FixedPretensionCandidateId = "";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert(vm.FixedPretensionCandidateId == "P01" && vm.StudyIterations.Count == 36 &&
                       vm.StudyIterations.All(row => row.PretensionCandidateId == "P01"),
                    "Tab 5 restores P01 when the fixed-pretension selection is transiently cleared");
                uiState.Study.Mode = IterationStudyMode.FixHeight;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var heightPicker = FindVisuals<ComboBox>(window)
                    .Single(combo => combo.Items.Cast<object>().OfType<StudyHeightCandidate>().Any());
                heightPicker.SelectedValue = "H02";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert(vm.FixedHeightCandidateId == "H02" &&
                       Equals(heightPicker.SelectedValue, "H02") &&
                       heightPicker.SelectedItem is StudyHeightCandidate { Id: "H02" } &&
                       vm.StudyIterations.All(row => row.HeightCandidateId == "H02"),
                    "Tab 5 switches directly from H01 to H02 without clearing the ComboBox");
                var grids = FindVisuals<DataGrid>(window).ToArray();
                Assert(grids.Any(grid => grid.Name == "StudyPreviewGrid" && grid.Items.Count == 36 && grid.Columns.Count == 10 &&
                                         grid.Columns.Any(column => Equals(column.Header, "Resume"))),
                    "Study Preview DataGrid renders joint-load, cable-target, worker, status, resume, run, and open columns");

                vm.StudyWorkers.Add(new SapWorkerSnapshot
                {
                    WorkerId = 1,
                    State = SapWorkerState.Busy,
                    CurrentIterationId = "L01_H02_P01",
                    CurrentStage = StudyWorkerStage.ApplyingLoading,
                    CurrentModelPath = @"C:\Models\Study\L01_H02_P01\COT_VCF_L01_H02_P01.sdb",
                    CompletedCount = 3,
                    LastMessage = "Applying loading candidate."
                });
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert(FindVisuals<TextBlock>(window).Any(text => text.Text == "Gate: OCCUPIED") &&
                       FindVisuals<TextBlock>(window).Any(text => text.Text == "Applying Loading"),
                    "Worker monitor renders worker gate and current stage text");
                Assert(window.StudyValidationPanel.ActualHeight > 0 &&
                       window.StudyValidationPanel.TransformToAncestor(window).Transform(new Point()).Y < window.ActualHeight,
                    "Study validation panel remains visible with the worker monitor expanded");

                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(".work");
                using (var stream = File.Create(".work/study-tab.png")) encoder.Save(stream);
                vm.SelectedTab = 5;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert(!FindVisuals<TextBlock>(window).Any(text => text.Text.Contains("SHORTLIST", StringComparison.OrdinalIgnoreCase)) &&
                       FindVisuals<TextBlock>(window).Any(text => text.Text == "06  SUPPORT REACTIONS") &&
                       FindVisuals<TextBlock>(window).Any(text => text.Text == "07  CABLE FORCES · kN"),
                    "Post-processing UI lists the result modules 01–07; the shortlist module is gone");

                // 07 FEM (index 7) is split into menu panes: Define -> Sections / Load Patterns / Load Cases / Result Candidate,
                // Assign -> Joint Restraints, Run -> Analysis Options, Result -> outputs, Display -> View Options
                void OpenMenu(string menu, string item)
                {
                    TopMenu(menu).Items.OfType<MenuItem>().Single(entry => (entry.Header as string)?.Replace("_", "").StartsWith(item, StringComparison.Ordinal) == true)
                        .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                }
                void OpenSubMenu(string menu, string submenu, string item)
                {
                    TopMenu(menu).Items.OfType<MenuItem>().Single(entry => (entry.Header as string)?.Replace("_", "") == submenu).Items.OfType<MenuItem>()
                        .Single(entry => (entry.Header as string)?.Replace("_", "").StartsWith(item, StringComparison.Ordinal) == true)
                        .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                }
                OpenMenu("Define", "Load Patterns");
                Assert(vm.SelectedTab == 7 && vm.SelectedFemPage == MainViewModel.FemInputsPage && vm.PaneMenu == "Define" &&
                       window.SharedViewerPane.Visibility == Visibility.Visible &&
                       vm.ShowFemPinLegend && vm.SharedViewerPinSupportPoints?.SequenceEqual(["bot", "bot2"]) == true,
                    "The FEM inputs panes show the supports in the shared viewer");
                OpenSubMenu("Define", "Section Properties", "Member Sections Table");
                var sectionsWindow = window.OwnedWindows.OfType<DefineSectionsWindow>().Single();
                sectionsWindow.UpdateLayout();
                Assert(sectionsWindow.IsVisible && FindVisuals<DataGrid>(sectionsWindow).Any(grid => grid.Name == "FemPropertiesGrid" && ReferenceEquals(grid.ItemsSource, vm.FemSectionProperties)) &&
                       !FindVisuals<DataGrid>(window).Any(grid => grid.Name == "FemPropertiesGrid") && vm.ReadFemProperties.CanExecute(null),
                    "Define -> Section Properties -> Member Sections Table opens the member-property table in its own window, not in the left pane");
                OpenSubMenu("Define", "Section Properties", "Member Sections Table");
                Assert(window.OwnedWindows.OfType<DefineSectionsWindow>().Count() == 1, "Define -> Sections again brings the same window forward");
                sectionsWindow.Close();
                window.ClosePaneButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Assert(!window.IsPaneOpen && window.SharedViewerPane.Visibility == Visibility.Visible && window.ParamsColumn.ActualWidth == 0,
                    "The (x) on the left pane closes it and the model view takes the full width");
                OpenMenu("Define", "Load Cases");
                Assert(window.IsPaneOpen && vm.PaneTitle == "LOAD CASES / LOAD COMBINATIONS", "Any menu item opens the left pane again");
                Button Tool(string name) => FindVisuals<Button>(window.TopToolbar).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == name);
                foreach (var (tool, plane) in new[] { ("3D view", "3D"), ("XY view", "XY"), ("YZ view", "YZ"), ("XZ view", "XZ") })
                {
                    Tool(tool).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert(window.SharedViewer.CurrentPlane == plane, $"Toolbar {tool} switches the shared viewer to {plane}");
                }
                Tool("Zoom in").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Tool("Zoom out").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Tool("Zoom extents").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(Tool("Zoom in").IsEnabled && Tool("Zoom extents").IsEnabled, "Toolbar zoom in / out / extents act on the shared viewer while it is shown");
                var dimensionsItem = TopMenu("Display").Items.OfType<MenuItem>().Single(item => (item.Header as string)?.Replace("_", "") == "Dimensions");
                Assert(window.SharedViewer.ShowDimensions && dimensionsItem.IsChecked, "Dimensions (spans, level heights, node elevations) show in the viewer by default and Display -> Dimensions is ticked");
                dimensionsItem.IsChecked = false;
                Assert(!window.SharedViewer.ShowDimensions, "Display -> Dimensions turns the dimension overlay off");
                dimensionsItem.IsChecked = true;
                Assert(window.Icon is System.Windows.Media.Imaging.BitmapFrame { Decoder.Frames.Count: >= 8 } && ReferenceEquals(window.Icon, AppIcon.Window), "The window icon is the software logo, from the multi-size icon file");
                OpenMenu("Edit", "VCF Geometry");
                var vcfWindow = window.OwnedWindows.OfType<VcfGeometryWindow>().Single();
                vcfWindow.UpdateLayout();
                Assert(vcfWindow.IsVisible && vcfWindow.Preview.Model.Points.Count == uiState.Geometry.Points.Count,
                    "Edit -> VCF Geometry opens its own window with a preview of the model");
                vcfWindow.Close();
                Assert(TopMenu("Define").Items.OfType<MenuItem>().First().Header as string == "_Materials…",
                    "Define starts with Materials…");
                Assert(TopMenu("Define").Items.OfType<MenuItem>().Single(item => (item.Header as string)?.Replace("_", "") == "Section Properties").Items.OfType<MenuItem>()
                           .Select(item => (item.Header as string)?.Replace("_", "")).Where(header => header is not null)
                           .SequenceEqual(["Frame Sections…", "Tendon Sections…", "Cable Sections…", "Member Sections Table…"]),
                    "Define -> Section Properties offers Frame, Tendon and Cable Sections (and the member table)");
                var frameList = new SectionListWindow(vm, SectionListKind.Frame) { Owner = window };
                frameList.PickShape = () => FrameShape.Box;
                frameList.EditSection = (section, _) => section;
                frameList.Show();
                frameList.UpdateLayout();
                Assert(frameList.Title == "Frame Properties" &&
                       new[] { "Get From SAP2000", "Import New Property...", "Add New Property...", "Add Copy of Property...", "Modify/Show Property...", "Delete Property" }
                           .All(label => FindVisuals<Button>(frameList).Any(button => Equals(button.Content, label))) &&
                       FindVisuals<TextBox>(frameList).Any(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Find this property"),
                    "Frame Properties has Find this property and Get From SAP2000, Import / Add New / Add Copy / Modify/Show / Delete Property");
                var framesBefore = frameList.Working.Count;
                frameList.AddNew();
                frameList.Find("FSEC");
                Assert(frameList.SelectedSection is FrameSectionProperty { Shape: FrameShape.Box } && frameList.Working.Count == framesBefore + 1,
                    "Add New Property adds a section of the chosen shape; Find this property selects it");
                frameList.Commit();
                Assert(vm.FrameSections.Any(section => section.Shape == FrameShape.Box && section.Name.StartsWith("FSEC", StringComparison.Ordinal)), "OK keeps the new frame section");
                var boxForm = new FrameSectionWindow(vm, FrameSectionProperty.Default(FrameShape.Box, "SHS test", "") with { Depth = 1.2, Width = 1.2, FlangeThickness = 0.12, WebThickness = 0.12 },
                    new HashSet<string>()) { Owner = window };
                boxForm.Show();
                boxForm.UpdateLayout();
                Assert(boxForm.Title == "Box/Tube Section" &&
                       FindVisuals<GroupBox>(boxForm).Select(box => box.Header as string).Where(header => header is { Length: > 0 })
                           .SequenceEqual(["Dimensions", "Material", "Property Modifiers", "Section", "Properties"]) &&
                       FindVisuals<COT_VCF.Presentation.Controls.SectionSketch>(boxForm).Single().Section is { } drawn && Math.Abs(drawn.Values().Area - 0.5184) < 1e-9 &&
                       new[] { "Set Modifiers...", "Section Properties...", "Modify/Show Notes..." }.All(label => FindVisuals<Button>(boxForm).Any(button => Equals(button.Content, label))),
                    "Box/Tube Section shows Dimensions, the Section drawing, Material, Set Modifiers and Section Properties (A = 0.5184 m² for 1200×1200×120)");
                boxForm.SetField("Tf", "0.7");
                Assert(boxForm.Read(out _) is null, "the box form refuses flanges thicker than the section");
                boxForm.Close();
                var cableForm = new StrandSectionWindow(vm, new StrandSectionProperty { Name = "Strand", Area = 0.01 }, new HashSet<string>()) { Owner = window };
                cableForm.Show();
                cableForm.SetArea("0.02");
                Assert(cableForm.Read(out _) is { Area: 0.02 } && FindVisuals<TextBox>(cableForm).Any(box => System.Windows.Automation.AutomationProperties.GetName(box)!.StartsWith("Diameter", StringComparison.Ordinal) &&
                           Math.Abs(double.Parse(box.Text, System.Globalization.CultureInfo.CurrentCulture) - Math.Sqrt(0.08 / Math.PI)) < 1e-9),
                    "Cable Section Data links the area and the diameter");
                cableForm.Close();
                var defineMaterials = new DefineMaterialsWindow(vm) { Owner = window };
                var editedMaterials = new Queue<Func<MaterialProperty, MaterialProperty?>>();
                defineMaterials.EditMaterial = (material, _, _) => editedMaterials.Count > 0 ? editedMaterials.Dequeue()(material) : material;
                defineMaterials.Show();
                defineMaterials.UpdateLayout();
                Assert(new[] { "Get From SAP2000", "Add New Material...", "Add Copy of Material...", "Modify/Show Material...", "Delete Material" }
                           .All(label => FindVisuals<Button>(defineMaterials).Any(button => Equals(button.Content, label))) &&
                       FindVisuals<CheckBox>(defineMaterials).Any(box => Equals(box.Content, "Show Advanced Properties")),
                    "Define Materials has Get From SAP2000, Add New, Add Copy, Modify/Show, Delete and Show Advanced Properties");
                var before = defineMaterials.Working.Count;
                defineMaterials.AddNew(MaterialKind.Steel);
                defineMaterials.AddCopy();
                editedMaterials.Enqueue(material => material with { Name = "Edited steel", Fy = 420_000 });
                defineMaterials.Modify();
                Assert(defineMaterials.Working.Count == before + 2 && defineMaterials.Working.Any(material => material.Name == "Edited steel" && material.Fy == 420_000),
                    "Add New, Add Copy and Modify/Show edit the staged list");
                defineMaterials.Delete();
                defineMaterials.Commit();
                Assert(vm.Materials.Count == before + 1 && !vm.Materials.Any(material => material.Name == "Edited steel") && !defineMaterials.IsVisible,
                    "Delete removes an unused material; OK hands the list to the model");
                var propertyForm = new MaterialPropertyWindow(MaterialProperty.Default(MaterialKind.Concrete, "C30/37"), isNew: false, advanced: false, new HashSet<string>()) { Owner = window };
                propertyForm.Show();
                propertyForm.UpdateLayout();
                Assert(FindVisuals<GroupBox>(propertyForm).Select(box => box.Header as string)
                           .SequenceEqual(["General Data", "Weight and Mass", "Isotropic Property Data", "Other Properties For Concrete Materials"]) &&
                       propertyForm.ReadForm() is { Fc: 30_000, ElasticModulus: 33_000_000 },
                    "Material Property Data shows General Data, Weight and Mass, Isotropic Property Data and the concrete properties");
                propertyForm.SetField("E", "-5");
                Assert(propertyForm.ReadForm() is null, "Material Property Data refuses a modulus of elasticity that is not positive");
                propertyForm.Close();
                var tendonForm = new MaterialPropertyWindow(MaterialProperty.Default(MaterialKind.Tendon, "Strand"), isNew: false, advanced: true, new HashSet<string>()) { Owner = window };
                tendonForm.Show();
                tendonForm.UpdateLayout();
                Assert(FindVisuals<GroupBox>(tendonForm).Select(box => box.Header as string)
                           .SequenceEqual(["General Data", "Weight and Mass", "Uniaxial Property Data", "Other Properties For Tendon Materials", "Advanced"]),
                    "a tendon shows Uniaxial Property Data and the advanced display when switched on");
                tendonForm.Close();
                OpenMenu("Display", "View Options");
                var viewOptions = window.OwnedWindows.OfType<ViewOptionsWindow>().Single();
                viewOptions.UpdateLayout();
                Assert(viewOptions.IsVisible && viewOptions.Title == "Display Options" &&
                       viewOptions.OptionTabs.Items.OfType<TabItem>().Select(tab => tab.Header as string).SequenceEqual(["Object Options", "General Options"]) &&
                       viewOptions.Dimensions.IsChecked == true && viewOptions.PlaneXZ.IsChecked == true &&
                       new[] { viewOptions.ResetDefaultsButton, viewOptions.ResetCurrentButton, viewOptions.OkButton, viewOptions.CloseButton, viewOptions.ApplyButton }.All(button => button is not null),
                    "Display -> View Options opens a Display Options window (Object / General Options tabs, reset, OK / Close / Apply) showing the current settings");
                viewOptions.Dimensions.IsChecked = false;
                viewOptions.FrameLabels.IsChecked = true;
                Assert(vm.ShowDimensions && !window.SharedViewer.ShowFrameLabels, "Editing the form changes nothing until Apply");
                viewOptions.ApplyForm();
                Assert(!vm.ShowDimensions && !window.SharedViewer.ShowDimensions && !dimensionsItem.IsChecked && window.SharedViewer.ShowFrameLabels,
                    "Apply writes the form: dimensions off in the viewer and the Display menu, frame labels on");
                viewOptions.ResetToDefaults();
                Assert(viewOptions.Dimensions.IsChecked == true && viewOptions.FrameLabels.IsChecked == false && !vm.ShowDimensions,
                    "Reset Form to Default Values fills the form only");
                viewOptions.ResetToCurrent();
                Assert(viewOptions.Dimensions.IsChecked == false && viewOptions.FrameLabels.IsChecked == true, "Reset Form to Current Window Settings reads the view back");
                viewOptions.ResetToDefaults();
                viewOptions.OkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert(vm.ShowDimensions && !window.SharedViewer.ShowFrameLabels && !viewOptions.IsVisible, "OK applies the form and closes it");
                OpenMenu("Define", "Load Patterns");
                Assert(vm.PaneTitle == "LOAD PATTERNS" && FindVisuals<DataGrid>(window).Any(grid => grid.IsVisible && ReferenceEquals(grid.ItemsSource, vm.FemPatternRows)),
                    "Define -> Load Patterns opens the load-pattern table");
                OpenMenu("Define", "Load Cases");
                Assert(vm.PaneTitle == "LOAD CASES / LOAD COMBINATIONS" &&
                       FindVisuals<DataGrid>(window).Any(grid => grid.IsVisible && ReferenceEquals(grid.ItemsSource, vm.FemCaseRows)) &&
                       FindVisuals<DataGrid>(window).Any(grid => grid.IsVisible && ReferenceEquals(grid.ItemsSource, vm.FemCombinationRows)),
                    "Define -> Load Cases / Load Combinations opens the case and combination tables");
                OpenMenu("Assign", "Joint Restraints");
                Assert(vm.PaneMenu == "Assign" && vm.FemPane == MainViewModel.FemPaneRestraints &&
                       FindVisuals<TextBlock>(window).Any(text => text.IsVisible && text.Text == vm.FemPinSupportSummary),
                    "Assign -> Joint Restraints shows the auto-detected pin supports");
                OpenMenu("Run", "Analysis Options");
                Assert(vm.PaneMenu == "Run" && vm.SelectedFemPage == MainViewModel.FemAnalysisPage &&
                       FindVisuals<Button>(window).Any(button => button.IsVisible && Equals(button.Content, "Run Analysis")) &&
                       vm.FemCandidateSummary.Contains("FEM runs", StringComparison.Ordinal) &&
                       window.SharedViewerPane.Visibility == Visibility.Visible,
                    "Run -> Analysis Options holds the internal 2D frame analysis settings, the run controls and the candidate count");
                var sagBox = FindVisuals<CheckBox>(window).Single(box => Equals(box.Content, "Cable sag"));
                Assert(vm.FemCableSag && sagBox.IsChecked == true, "Tab 07 Analysis analyses cable sag by default");
                sagBox.IsChecked = false;
                Assert(!vm.FemCableSag, "…and the Cable sag switch turns it off for a comparison with straight cables");
                sagBox.IsChecked = true;
                var demoFemRun = new FemStudyRun("L01_H01_P01", "L01", "H01", "P01",
                    new FemAnalysisResult(
                        [new("bot", 0, 0, 0), new("mid", 0.001, -0.02, 0.001), new("top", 0.002, -0.04, 0.002),
                         new("bot2", 0, 0, 0), new("mid2", 0.001, -0.02, 0.001)],
                        [new("bot", 0, 100), new("bot2", 0, 100)],
                        [new("Column1", 100, 100, -10, 10, 0, 0), new("Column2", 100, 100, -10, 10, 0, 0)],
                        [new("Cable-A", 100, true)], 4));
                vm.FemRuns.Add(demoFemRun);
                vm.Notify(nameof(MainViewModel.HasFemResults));
                Assert(TopMenu("Result").Items.OfType<MenuItem>().Select(item => (item.Header as string)?.Replace("_", ""))
                           .SequenceEqual(["Axial forces / cables", "Bending moments", "Shear forces", "Support reactions", "Deflection", "Cable target fit", "Show Results…", "Results Table…"]) &&
                       TopMenu("Result").Items.OfType<MenuItem>().All(item => item.IsEnabled),
                    "The Result menu lists the six outputs and the results table, enabled once a candidate has results");
                OpenMenu("Define", "Result Candidate");
                Assert(vm.PaneTitle == "RESULT CANDIDATE / LOAD CASE" &&
                       FindVisuals<ComboBox>(window).Any(combo => combo.IsVisible && System.Windows.Automation.AutomationProperties.GetName(combo) == "FEM result candidate") &&
                       FindVisuals<ComboBox>(window).Any(combo => combo.IsVisible && System.Windows.Automation.AutomationProperties.GetName(combo) == "FEM load case or combination"),
                    "Define -> Result Candidate / Load Case holds the candidate and load-case dropdowns");
                OpenMenu("Result", "Deflection");
                Assert(vm.FemResultMode == FemResultMode.DeflectedShape && ReferenceEquals(vm.SelectedFemRun, demoFemRun) && vm.PaneMenu == "Result" &&
                       vm.PaneTitle == "DEFLECTION" && !FindVisuals<DataGrid>(window).Any(grid => grid.Name == "FemResultRowsGrid") &&
                       window.SharedViewerPane.Visibility == Visibility.Visible && ReferenceEquals(vm.SharedViewerFemResult, demoFemRun.Result),
                    "Result -> Deflection selects the first completed candidate and draws the diagram without embedding a result table");
                Tool("Show Results").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var showResults = window.OwnedWindows.OfType<ShowResultsWindow>().Single();
                showResults.UpdateLayout();
                Assert(showResults.LoadCasePicker.IsVisible && showResults.CandidatePicker.IsVisible &&
                       showResults.Outputs.Items.OfType<ListBoxItem>().Select(item => (string)item.Content)
                           .SequenceEqual(["Axial forces / cables", "Bending moments", "Shear forces", "Support reactions", "Deflection", "Cable target fit"]),
                    "The Show Results tool opens a window with the load case / combo and candidate at the top and the six outputs below");
                showResults.Outputs.SelectedIndex = 1;
                showResults.ApplySelection();
                Assert(vm.FemResultMode == FemResultMode.BendingMoment && vm.PaneTitle == "BENDING MOMENTS" && ReferenceEquals(vm.SharedViewerFemResult, demoFemRun.Result),
                    "Show Results → Apply draws the chosen output on the model view");
                showResults.Outputs.SelectedIndex = 4;
                showResults.ApplySelection();
                showResults.Close();
                FindVisuals<Button>(window).Single(button => button.IsVisible && Equals(button.Content, "View Results")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var tableWindow = window.OwnedWindows.OfType<FemResultsWindow>().Single();
                tableWindow.UpdateLayout();
                Assert(FindVisuals<DataGrid>(tableWindow).Any(grid => grid.Name == "FemResultRowsGrid" && grid.Items.Count > 0) &&
                       FindVisuals<ComboBox>(tableWindow).Any(combo => System.Windows.Automation.AutomationProperties.GetName(combo) == "FEM result candidate") &&
                       FindVisuals<ComboBox>(tableWindow).Any(combo => System.Windows.Automation.AutomationProperties.GetName(combo) == "FEM load case or combination"),
                    "View Results opens a separate populated table with candidate and load-case dropdowns above it");
                tableWindow.Close();
                var chartShear = FindVisuals<RadioButton>(window).Single(button => button.GroupName == "ViewerResultMode" && Equals(button.Content, "Shear"));
                chartShear.IsChecked = true;
                Assert(vm.FemResultMode == FemResultMode.Shear && vm.PaneTitle == "SHEAR FORCES",
                    "The floating chart buttons switch the result and keep the Result pane header synchronized");
                    vm.FemResultMode = FemResultMode.AxialForce;
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    var axialBitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    axialBitmap.Render(window);
                    var axialEncoder = new PngBitmapEncoder(); axialEncoder.Frames.Add(BitmapFrame.Create(axialBitmap));
                    using (var stream = File.Create(".work/fem-axial-results.png")) axialEncoder.Save(stream);
                     vm.FemResultMode = FemResultMode.DeflectedShape;
                     window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                     window.UpdateLayout();
                     var deflectionCanvas = FindVisuals<COT_VCF.Presentation.Controls.ModelCanvas>(window).Single();
                     var guideToggle = FindVisuals<CheckBox>(window).Single(box => Equals(box.Content, "Undeformed guide"));
                     Assert(deflectionCanvas.ShowUndeformed && deflectionCanvas.ShowDeflectionContour,
                         "Deflection shows a contour and an undeformed guide by default");
                     guideToggle.IsChecked = false;
                     window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                     Assert(!deflectionCanvas.ShowUndeformed, "The undeformed guide can be hidden independently of the deflection contour");
                     guideToggle.IsChecked = true;
                     var resultsBitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                     resultsBitmap.Render(window);
                     var resultsEncoder = new PngBitmapEncoder(); resultsEncoder.Frames.Add(BitmapFrame.Create(resultsBitmap));
                     using (var stream = File.Create(".work/fem-results-tab.png")) resultsEncoder.Save(stream);
                var femBitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                femBitmap.Render(window);
                var femEncoder = new PngBitmapEncoder(); femEncoder.Frames.Add(BitmapFrame.Create(femBitmap));
                using (var stream = File.Create(".work/fem-tab.png")) femEncoder.Save(stream);
                vm.SelectedTab = 5;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                // Tab 06 Print Setup: header / footer of the printed reports, and the Module 07 PDF export
                var printSetup = FindVisuals<Expander>(window).Single(expander =>
                    expander.Header is TextBlock { Text: "PRINT SETUP · HEADER / FOOTER" });
                var cableModule = FindVisuals<Expander>(window).Single(expander =>
                    expander.Header is TextBlock { Text: "07  CABLE FORCES · kN" });
                printSetup.IsExpanded = true; cableModule.IsExpanded = true;
                vm.PrintSettings.CompanyName = "ACME Structures";
                vm.PrintSettings.ProjectName = "COT Transfer Truss";
                vm.PrintSettings.PaperSize = ReportPaperSize.A3;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert(FindVisuals<TextBox>(printSetup).Any(box => box.Text == "ACME Structures" &&
                           System.Windows.Automation.AutomationProperties.GetName(box) == "Company name") &&
                       FindVisuals<TextBox>(printSetup).Count(box => !box.IsReadOnly) == 7 &&
                       FindVisuals<ComboBox>(printSetup).Any(combo => Equals(combo.SelectedItem, ReportPaperSize.A3)) &&
                       FindVisuals<ComboBox>(printSetup).Any(combo => Equals(combo.SelectedItem, ReportPageOrientation.Landscape)) &&
                       FindVisuals<TextBlock>(printSetup).Any(text => text.Text.StartsWith("No logo", StringComparison.Ordinal)),
                    "Print Setup card binds the header / footer fields, the default sheet and the logo status");
                var companyBox = FindVisuals<TextBox>(printSetup).First(box =>
                    System.Windows.Automation.AutomationProperties.GetName(box) == "Company name");
                companyBox.Text = "ACME Structures Pte Ltd";
                Assert(vm.PrintSettings.CompanyName == "ACME Structures Pte Ltd" && !vm.ClearPrintLogo.CanExecute(null),
                    "typing in the Print Setup card updates the settings immediately");
                Assert(FindVisuals<Button>(cableModule).Any(button => Equals(button.Content, "View Report") && !button.IsEnabled) &&
                       !FindVisuals<Button>(cableModule).Any(button => Equals(button.Content, "Export PDF")),
                    "Module 07 offers View Report — printing and PDF happen in the report, not in the application");
                foreach (var module in FindVisuals<Expander>(window).ToArray()) module.IsExpanded = true;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert(!FindVisuals<Button>(window).Any(button => button.Content is string label && label.Contains("PDF", StringComparison.OrdinalIgnoreCase)) &&
                       FindVisuals<Button>(window).Count(button => Equals(button.Content, "View Report")) == 7 &&
                       typeof(MainViewModel).GetProperties().All(property => !property.Name.Contains("Pdf", StringComparison.OrdinalIgnoreCase)) &&
                       FindVisuals<TextBlock>(printSetup).Any(text => text.Text.Contains("View Report → Print Report") && text.Text.Contains("Print All Reports")),
                    "no post-processing module has a PDF button of its own: each one opens its report, and Print Setup says where printing happens");
                var printAll = FindVisuals<Button>(window).Where(button => Equals(button.Content, "Print All Reports…")).ToArray();
                Assert(printAll.Length == 1 && !printAll[0].IsEnabled && !vm.PrintAllReports.CanExecute(null) && !vm.PrintAllOptimizedReports.CanExecute(null),
                    "Tab 06 offers Print All Reports beside Generate Outputs; there is nothing to print before the reports exist");
                var postBitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                postBitmap.Render(window);
                var postEncoder = new PngBitmapEncoder(); postEncoder.Frames.Add(BitmapFrame.Create(postBitmap));
                using (var stream = File.Create(".work/post-processing-tab.png")) postEncoder.Save(stream);

                // Tab 06A Optimization: only available with raw data; criteria edit the shared output settings
                var optimizationTab = window.OptimizationTab;
                Assert(!vm.HasRawData && !optimizationTab.IsEnabled && vm.OptimizationTabStatus == "Needs raw data" &&
                       TopMenu("MB").Items.OfType<MenuItem>().Single(item => Equals(item.Header, "06A  Optimization")) is { IsEnabled: false } &&
                       mbItems.ToList().IndexOf("06A  Optimization") == mbItems.ToList().IndexOf("06  Post-Processing") + 1,
                    "Tab 06A Optimization is listed after Tab 06 under MB and stays disabled until the study has raw data");
                vm.SelectedTab = 6;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var criteriaCard = window.OptimizationCriteria;
                var checks = FindVisuals<CheckBox>(criteriaCard).Select(box => box.Content as string).ToArray();
                var limitBox = FindVisuals<TextBox>(criteriaCard).Single(box =>
                    System.Windows.Automation.AutomationProperties.GetName(box) == "Cable utilisation limit");
                var maxBox = FindVisuals<TextBox>(criteriaCard).Single(box =>
                    System.Windows.Automation.AutomationProperties.GetName(box) == "Qualifying deflection maximum [mm]");
                Assert(checks.SequenceEqual(["Deflection within range", "Cable forces below the limit"]) && limitBox.Text == "1",
                    "Tab 06A shows the two criteria — deflection within range and cable forces below the limit — both on, limit 1.00");
                limitBox.Text = "0.85"; maxBox.Text = "150";
                Assert(vm.State.OutputSettings.MaxCableUtilization == 0.85 && vm.State.OutputSettings.DeflectionLimitMaxMm == 150 &&
                       COT_VCF.Application.Results.QualificationCriteria.From(vm.State.OutputSettings).Label == "Deflection δ ≤ 150 mm · Cable utilisation ≤ 0.85",
                    "typing a limit on Tab 06A updates the criteria immediately — the deflection range is the Tab 04 limit band itself");
                limitBox.Text = "0";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert(vm.HasOptimizationProblem && vm.OptimizationProblem.Contains("greater than 0") && vm.OptimizationTabStatus == "Needs raw data",
                    "an unusable cable limit is reported on the tab");
                limitBox.Text = "1";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var optimizationButtons = FindVisuals<Button>(window).Where(button => button.Content is string label &&
                    (label is "Generate Optimized Reports" or "View Summary" or "Open Optimized Folder" or "Save Criteria")).ToArray();
                Assert(optimizationButtons.Length == 4 && !vm.GenerateOptimizedReports.CanExecute(null) && !vm.OpenOptimizationSummary.CanExecute(null) &&
                       window.OptimizationGrid.Columns.Select(column => column.Header as string)
                           .SequenceEqual(["Iteration", "Load", "Height", "Pretension", "δ [mm]", "Cable util. [%]", "Governing cable", "Status", "Reason"]) &&
                       vm.OptimizedReportsStatus == "No optimized reports generated yet." && vm.OptimizationFilters.Count == 4,
                    "Tab 06A offers Generate Optimized Reports, the report buttons and the candidate table; nothing can be generated without raw data");
                Assert(FindVisuals<Button>(window).Count(button => Equals(button.Content, "Print All Reports…") && !button.IsEnabled) == 1,
                    "Tab 06A offers Print All Reports for the optimized set");
                var optimizationBitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                optimizationBitmap.Render(window);
                var optimizationEncoder = new PngBitmapEncoder(); optimizationEncoder.Frames.Add(BitmapFrame.Create(optimizationBitmap));
                using (var stream = File.Create(".work/optimization-tab.png")) optimizationEncoder.Save(stream);

                // …and with raw data: the metrics load in the background, criteria apply at once, the set is generated
                var optimizationRoot = Path.Combine(Path.GetTempPath(), "cotvcf_optui_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(optimizationRoot);
                    File.WriteAllText(Path.Combine(optimizationRoot, "COT_VCF.sdb"), "baseline");
                    uiState.SessionBaselineModel = uiState.Model! with { Path = Path.Combine(optimizationRoot, "COT_VCF.sdb") };
                    OptimizationChecks.WriteStudy(vm.StudyRoot, vm.BaselineModelPath);
                    void Pump(Func<bool> done)
                    {
                        // always once: a view model change reaches the bound controls on the next dispatcher pass
                        var until = DateTime.UtcNow.AddSeconds(30);
                        do { window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); if (!done()) Thread.Sleep(15); }
                        while (!done() && DateTime.UtcNow < until);
                        window.UpdateLayout();
                    }
                    vm.RefreshDeflectionArtifacts();
                    Pump(() => vm.HasRawData && !vm.OptimizationLoading && vm.OptimizationRows.Count > 0);
                    Assert(vm.HasRawData && optimizationTab.IsEnabled && vm.SelectedTab == 6 && vm.OptimizationRows.Count == 27 &&
                           vm.OptimizationTabStatus.StartsWith("Qualified ") && vm.OptimizationRawSummary.StartsWith("27 candidates") && !vm.HasOptimizationProblem,
                        "with raw data Tab 06A is enabled and lists every candidate of the study with its status");

                    var output = vm.State.OutputSettings;
                    output.DeflectionLimitMinMm = 50; output.DeflectionLimitMaxMm = 100; output.MaxCableUtilization = 0.8;
                    var index = new COT_VCF.Application.Results.StudyMetricsStore().Load(vm.StudyRoot);
                    var current = uiState.Cables.Where(cable => cable.ResolveCapacity(uiState.CableStrengthMpa, uiState.CableStrengthReductionFactor) is > 0)
                        .ToDictionary(cable => cable.CableName, cable => cable.ResolveCapacity(uiState.CableStrengthMpa, uiState.CableStrengthReductionFactor)!.Value);
                    var expected = COT_VCF.Application.Results.StudyQualifier.Evaluate(index.Candidates,
                        COT_VCF.Application.Results.QualificationCriteria.From(output),
                        COT_VCF.Application.Results.CableCapacityResolver.Resolve(current, uiState.Cables.Select(cable => cable.CableName).ToArray(), index.StoredCableCapacities).Capacities).Summary;
                    Assert(expected.Qualified is > 0 and < 27 && vm.OptimizationRows.Count(row => row.Status == "Qualified") == expected.Qualified &&
                           vm.OptimizationTabStatus == $"Qualified {expected.Qualified} / 27" && vm.OptimizationHeadline.StartsWith($"Qualified {expected.Qualified} of 27") &&
                           vm.OptimizationBreakdown.Contains($"failed deflection only {expected.FailedDeflectionOnly}") && vm.DeflectionStudyRange == "δ in this study: 40–140 mm" &&
                           vm.OptimizationCriteriaNote.StartsWith("Active: Deflection δ 50–100 mm · Cable utilisation ≤ 0.80"),
                        "changing a criterion re-qualifies every candidate at once — counts, breakdown and the study's own ranges");
                    vm.OptimizationFilter = "Qualified";
                    Pump(() => true);
                    Assert(vm.OptimizationRows.Count == expected.Qualified && window.OptimizationGrid.Items.Count == expected.Qualified &&
                           vm.OptimizationRows.All(row => row.Kind == COT_VCF.Application.Results.QualificationStatus.Qualified) &&
                           vm.OptimizationTableSummary == $"{expected.Qualified} shown of 27",
                        "the candidate table filters by status");
                    vm.OptimizationFilter = "All";

                    Assert(vm.GenerateOptimizedReports.CanExecute(null) && !vm.OpenOptimizationSummary.CanExecute(null), "Generate Optimized Reports is available once candidates are qualified");
                    vm.GenerateOptimizedReports.Execute(null);
                    Pump(() => !vm.IsBusy && vm.OpenOptimizationSummary.CanExecute(null));
                    var optimizedFolder = Path.Combine(vm.StudyRoot, "Optimized");
                    Assert(File.Exists(Path.Combine(optimizedFolder, "Optimization_Summary.html")) && File.Exists(Path.Combine(optimizedFolder, "Deflection_Report.html")) &&
                           File.Exists(Path.Combine(optimizedFolder, "Top_Chord_Report.html")) && !Directory.EnumerateFiles(vm.StudyRoot, "*.html").Any() &&
                           vm.OpenOptimizedDeflection.CanExecute(null) && vm.OpenOptimizedTopChord.CanExecute(null) && vm.OpenOptimizedSupportReactions.CanExecute(null) &&
                           vm.OpenOptimizedCableForces.CanExecute(null) && vm.OpenOptimizedFolder.CanExecute(null) && !vm.OpenOptimizedBottomChord.CanExecute(null) &&
                           vm.OptimizedReportsStatus.StartsWith($"Optimized reports are up to date · {expected.Qualified} of 27 candidates") && !vm.OptimizedReportsStale &&
                           vm.Message.Contains($"{expected.Qualified} of 27 candidates qualify"),
                        "Generate Optimized Reports writes the set into Study/Optimized, enables its View buttons and reports it up to date");

                    // Print All Reports: asks where to save, then prints the whole folder as one PDF — no print dialog
                    string? askedFolder = null, askedName = null, printedFolder = null, printedPdf = null, printedTitle = null, printedCompany = null;
                    var chosenPdf = Path.Combine(optimizationRoot, "out", "Optimized.pdf");
                    Directory.CreateDirectory(Path.GetDirectoryName(chosenPdf)!);
                    vm.AskPdfPath = (folder, name) => { askedFolder = folder; askedName = name; return chosenPdf; };
                    vm.PrintPackage = (folder, pdf, settings, title) =>
                    {
                        printedFolder = folder; printedPdf = pdf; printedTitle = title; printedCompany = settings.CompanyName;
                        return new COT_VCF.Infrastructure.ReportPdfResult(pdf, "browser", TimeSpan.FromSeconds(1.5), 12);
                    };
                    vm.PrintSettings.CompanyName = "Print All & Co";
                    Assert(vm.PrintAllOptimizedReports.CanExecute(null) && !vm.PrintAllReports.CanExecute(null),
                        "Print All Reports is available for the optimized set once it exists — and not for a study folder without reports");
                    vm.PrintAllOptimizedReports.Execute(null);
                    Pump(() => !vm.IsBusy && printedPdf is not null);
                    Assert(askedFolder == optimizedFolder && askedName is not null && askedName.EndsWith(" - Optimized Reports.pdf", StringComparison.Ordinal) &&
                           askedName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && printedFolder == optimizedFolder && printedPdf == chosenPdf &&
                           printedTitle is not null && printedTitle.EndsWith(" - Optimized Reports", StringComparison.Ordinal) && printedCompany == "Print All & Co" &&
                           vm.Message.Contains("Optimized.pdf written — 12 pages", StringComparison.Ordinal),
                        "Print All Reports asks only where to save, prints the optimized folder with the current Print Setup and reports the pages written");
                    printedPdf = null; askedFolder = null;
                    vm.AskPdfPath = (folder, _) => { askedFolder = folder; return null; };
                    vm.PrintAllOptimizedReports.Execute(null);
                    Pump(() => true);
                    Assert(printedPdf is null && !vm.IsBusy && askedFolder == Path.GetDirectoryName(chosenPdf),
                        "cancelling the Save dialog prints nothing; the dialog reopens in the folder used last");
                    output.MaxCableUtilization = 0.9;
                    Assert(vm.OptimizedReportsStatus.Contains("out of date") && vm.OptimizedReportsStale, "after a criterion changes the optimized reports are flagged out of date");
                    output.MaxCableUtilization = 0.8;
                    Pump(() => true);
                    Assert(!vm.OptimizedReportsStatus.Contains("out of date") && !vm.OptimizedReportsStale, "…and current again when the criterion is set back");
                    var liveBitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    liveBitmap.Render(window);
                    var liveEncoder = new PngBitmapEncoder(); liveEncoder.Frames.Add(BitmapFrame.Create(liveBitmap));
                    using (var stream = File.Create(".work/optimization-tab-data.png")) liveEncoder.Save(stream);

                    // Tab 06: a module's Regenerate rebuilds its own report only; Generate Outputs writes everything
                    var studyWorkbook = Path.Combine(vm.StudyRoot, "Deflection_Results_Source.xlsx");
                    var studyCable = Path.Combine(vm.StudyRoot, "Cable_Forces_Report.html");
                    vm.RegenerateCableForceReport.Execute(null);
                    Pump(() => !vm.IsBusy && vm.HasCableForceReport);
                    Assert(File.Exists(studyCable) && Directory.GetFiles(vm.StudyRoot, "*.html").Length == 1 && !File.Exists(studyWorkbook) &&
                           !vm.HasDeflectionReport && !vm.HasMemberForceReport && !vm.HasSupportReactionReport &&
                           vm.Message.StartsWith("Cable_Forces_Report.html regenerated", StringComparison.Ordinal) && vm.Message.Contains("this report only", StringComparison.Ordinal),
                        "module 07 Regenerate writes the cable report alone — no workbook, no other report");
                    // as in the running application: what follows an await returns to the UI thread
                    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
                    vm.GeneratePostProcessOutputs.Execute(null);
                    Pump(() => !vm.IsBusy && File.Exists(studyWorkbook));
                    var studyDeflection = Path.Combine(vm.StudyRoot, "Deflection_Report.html");
                    Assert(File.Exists(studyWorkbook) && File.Exists(studyDeflection) && File.Exists(Path.Combine(vm.StudyRoot, "Member_Forces_Report.html")) &&
                           File.Exists(Path.Combine(vm.StudyRoot, "Support_Reactions_Report.html")) && vm.Message.StartsWith("Tab 06 outputs generated", StringComparison.Ordinal),
                        "Generate Outputs writes the workbook and every report");
                    var kept = new[] { studyWorkbook, studyCable, Path.Combine(vm.StudyRoot, "Member_Forces_Report.html") }
                        .ToDictionary(path => path, File.GetLastWriteTimeUtc);
                    File.SetLastWriteTimeUtc(studyDeflection, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                    vm.RegenerateDeflectionReport.Execute(null);
                    Pump(() => !vm.IsBusy && vm.Message.StartsWith("Deflection_Report.html regenerated", StringComparison.Ordinal));
                    Assert(File.GetLastWriteTimeUtc(studyDeflection).Year > 2020 && vm.HasDeflectionReport &&
                           kept.All(file => File.GetLastWriteTimeUtc(file.Key) == file.Value),
                        "module 01 Regenerate rewrites the deflection report and leaves the workbook and the other reports as they are");

                    // Print Setup travels with the project file: Save All and the card's own save write it, Load Config restores it
                    var projectFile = Path.Combine(optimizationRoot, "Project.json");
                    string Setup(string key) => System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(projectFile))!["printSetup"]?[key]?.ToString() ?? "(none)";
                    vm.PrintSettings.CompanyName = "Meinhardt"; vm.PrintSettings.ProjectName = "City of Tomorrow"; vm.PrintSettings.JobNumber = "J-100";
                    vm.PrintSettings.PaperSize = ReportPaperSize.A3; vm.PrintSettings.Orientation = ReportPageOrientation.Portrait; vm.PrintSettings.ShowDate = false;
                    Assert(vm.PrintSetupProjectStatus.StartsWith("No project open", StringComparison.Ordinal) && vm.SavePrintSetup.CanExecute(null),
                        "without a project the Print Setup card says it is kept for this Windows user only");
                    var savedAll = uiConfig.SaveAsync(COT_VCF.Configuration.ConfigTabs.All, () => projectFile, vm.PrintSettings).GetAwaiter().GetResult();
                    Assert(savedAll.Result == COT_VCF.Configuration.SaveResult.Saved && Setup("projectName") == "City of Tomorrow" && Setup("jobNumber") == "J-100" &&
                           Setup("companyName") == "Meinhardt" && Setup("paperSize") == "A3" && Setup("orientation") == "Portrait" && Setup("showDate") == "false" &&
                           vm.PrintSetupProjectStatus.StartsWith("Saved in Project.json", StringComparison.Ordinal),
                        "Save All stores the Print Setup in the project file as printSetup");
                    vm.PrintSettings.JobNumber = "J-200";
                    var tabOnly = uiConfig.SaveAsync(COT_VCF.Configuration.ConfigTabs.Loading, null).GetAwaiter().GetResult();
                    Assert(tabOnly.Result == COT_VCF.Configuration.SaveResult.Saved && Setup("jobNumber") == "J-100" &&
                           vm.PrintSetupProjectStatus.StartsWith("Modified *", StringComparison.Ordinal),
                        "a per-tab save keeps the project's Print Setup, and the card shows it as modified");
                    vm.SavePrintSetup.Execute(null);
                    Pump(() => !vm.IsBusy && vm.Message.StartsWith("Print Setup saved", StringComparison.Ordinal));
                    Assert(Setup("jobNumber") == "J-200" && vm.Message == "Print Setup saved · Project.json" &&
                           vm.PrintSetupProjectStatus.StartsWith("Saved in Project.json", StringComparison.Ordinal),
                        "Save Print Setup writes the card alone into the active project");

                    vm.PrintSettings.ProjectName = "Something else"; vm.PrintSettings.PaperSize = ReportPaperSize.A4; vm.PrintSettings.ShowDate = true;
                    vm.OpenProjectFile(projectFile);
                    Pump(() => !vm.IsBusy && vm.Message.StartsWith("Project Project.json opened", StringComparison.Ordinal));
                    Assert(vm.PrintSettings.ProjectName == "City of Tomorrow" && vm.PrintSettings.JobNumber == "J-200" && vm.PrintSettings.CompanyName == "Meinhardt" &&
                           vm.PrintSettings.PaperSize == ReportPaperSize.A3 && vm.PrintSettings.Orientation == ReportPageOrientation.Portrait && !vm.PrintSettings.ShowDate &&
                           vm.Message.EndsWith("Print Setup", StringComparison.Ordinal) && vm.PrintSetupProjectStatus.StartsWith("Saved in Project.json", StringComparison.Ordinal),
                        "Load Config restores the Print Setup with the project");
                    var olderProject = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(projectFile))!.AsObject();
                    olderProject.Remove("printSetup");
                    var olderFile = Path.Combine(optimizationRoot, "Older.json");
                    File.WriteAllText(olderFile, olderProject.ToJsonString());
                    vm.PrintSettings.ProjectName = "Kept";
                    vm.OpenProjectFile(olderFile);
                    Pump(() => !vm.IsBusy && vm.Message.StartsWith("Project Older.json opened", StringComparison.Ordinal));
                    Assert(vm.PrintSettings.ProjectName == "Kept" && vm.PrintSettings.JobNumber == "J-200" && !vm.Message.Contains("Print Setup", StringComparison.Ordinal) &&
                           vm.PrintSetupProjectStatus.StartsWith("Not in Older.json yet", StringComparison.Ordinal),
                        "a project saved before the Print Setup was stored opens as before and leaves the card as it is");

                    // Tab 08 Quantity Take-off: after 07 FEM, on the full width; the connected model live, then the candidates of the baseline's study
                    QuantityTakeoffChecks.WriteCandidates(vm.BaselineModelPath);
                    var workflowTabs = FindVisuals<TabControl>(window).First(tabs => tabs.Name == "WorkflowTabs");
                    var quantityPicker = window.QuantityCandidatePicker;
                    var quantityGrid = window.QuantityGrid;
                    var (tableReadsBefore, writesBefore) = (uiSap.DatabaseTableReads, uiSap.Writes);
                    vm.SelectedTab = 8;
                    Pump(() => vm.QuantityCandidates.Count == 3 && vm.QuantityState == QuantityViewState.Ready && !vm.IsBusy && quantityGrid.Items.Count == 6);
                    Assert(workflowTabs.Items.Count == 9 && workflowTabs.Items.IndexOf(window.QuantitiesTab) == 8 && ReferenceEquals(workflowTabs.SelectedItem, window.QuantitiesTab) &&
                           vm.PaneTitle == "08  QUANTITIES" && window.SharedViewerPane.Visibility == Visibility.Collapsed &&
                           quantityPicker.IsEnabled && quantityPicker.Items.Cast<object>().Select(item => item.ToString()).SequenceEqual(["Connected model", "L01_H01_P01", "L01_H02_P01"]) &&
                           quantityGrid.Columns.Select(column => column.Header as string).SequenceEqual(["Section", "Figure", "Member count", "Total length m", "Concrete volume m³", "Steel mass t"]),
                        "Tab 08 Quantities follows Tab 07 FEM on the full width: a dropdown of the connected model and the Lxx_Hxx_Pxx candidate models, and the agreed table with its section-figure column");
                    Assert(quantityPicker.SelectedIndex == 0 && vm.SelectedQuantityCandidate is { IsConnectedModel: true } && quantityGrid.Items.Count == 6 &&
                           uiSap.DatabaseTableReads == tableReadsBefore + 1 && uiSap.Writes == writesBefore &&
                           FindVisuals<TextBlock>(window).Any(text => text.Text.StartsWith("Connected model · COT_VCF.sdb · read live from the connected SAP2000 at ", StringComparison.Ordinal)) &&
                           FindVisuals<TextBlock>(window).Any(text => text.Text == "Live model") && FindVisuals<TextBlock>(quantityGrid).Any(text => text.Text == "2,862.104"),
                        "the tab opens on the model that is running in SAP2000, read live through the API without writing anything");
                    FindVisuals<Button>(window).Single(button => ReferenceEquals(button.Command, vm.ReloadQuantities)).Command.Execute(null);
                    Pump(() => uiSap.DatabaseTableReads == tableReadsBefore + 2 && vm.QuantityState == QuantityViewState.Ready && !vm.IsBusy);
                    Assert(uiSap.DatabaseTableReads == tableReadsBefore + 2 && uiSap.Writes == writesBefore && quantityGrid.Items.Count == 6,
                        "the Refresh button beside the dropdown reads the connected model from SAP2000 again");
                    quantityPicker.SelectedIndex = 1;
                    Pump(() => vm.QuantityState == QuantityViewState.Ready && vm.SelectedQuantityCandidate?.Id == "L01_H01_P01" && quantityGrid.Items.Count == 6);
                    var quantityCells = FindVisuals<TextBlock>(quantityGrid).Select(text => text.Text).ToArray();
                    Assert(vm.SelectedQuantityCandidate?.Id == "L01_H01_P01" && quantityGrid.Items.Count == 6 && quantityGrid.Items[5] is QuantityRow { IsTotal: true, Section: "Total" } &&
                           new[] { "Total", "54", "1,090.361", "795.000", "2,862.104", "306.000", "1,365.734", "0.000" }.All(quantityCells.Contains) && !quantityCells.Contains("n/a") &&
                           window.QuantityProblemText.Visibility == Visibility.Collapsed && window.QuantityNotesList.Items.Count == 0 &&
                           FindVisuals<TextBlock>(window).Any(text => text.Text.StartsWith("L01_H01_P01 · SAP2000 text export", StringComparison.Ordinal)),
                        "choosing a candidate in the dropdown shows its section breakdown, the total row and where the data came from");
                    var sectionFigures = FindVisuals<COT_VCF.Presentation.Controls.SectionFigureView>(quantityGrid).Where(view => view.IsVisible).ToArray();
                    var quantityRowHeights = Enumerable.Range(0, quantityGrid.Items.Count)
                        .Select(index => ((DataGridRow)quantityGrid.ItemContainerGenerator.ContainerFromIndex(index)).ActualHeight).ToArray();
                    Assert(sectionFigures.Length == 5 && sectionFigures.All(view => view.Figure is { Shapes.Count: > 0 } && view.ActualWidth == 64 && view.ActualHeight == 42) &&
                           new[] { "1200 × 2000 mm", "1500 × 3000 mm", "3000 × 2500 mm", "1200 × 1200 mm", "191.5 × 191.5 mm" }
                               .All(size => FindVisuals<TextBlock>(quantityGrid).Any(text => text.Text == size && text.IsVisible)) &&
                           sectionFigures.Select(view => view.Figure!.Shapes.Count).SequenceEqual([2, 4, 4, 1, 1]) &&
                           quantityRowHeights.Take(5).All(height => height >= 50) && quantityRowHeights[5] < 30,
                        "each section row shows the figure of its section with its overall size in the Figure column; the Total row has none and keeps the compact row height");
                    var quantityBitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    quantityBitmap.Render(window);
                    var quantityEncoder = new PngBitmapEncoder(); quantityEncoder.Frames.Add(BitmapFrame.Create(quantityBitmap));
                    using (var stream = File.Create(".work/quantities-tab.png")) quantityEncoder.Save(stream);
                    quantityPicker.SelectedIndex = 2;
                    Pump(() => vm.QuantityState == QuantityViewState.Ready && vm.SelectedQuantityCandidate?.Id == "L01_H02_P01" && quantityGrid.Items.Count == 7);
                    var unavailableCells = FindVisuals<TextBlock>(quantityGrid).Where(text => text.Text == "n/a").ToArray();
                    Assert(unavailableCells.Length == 4 && unavailableCells.All(cell => ReferenceEquals(cell.Foreground, window.FindResource("Warning")) &&
                               cell.ToolTip is string reason && reason.Length > 0) &&
                           unavailableCells.Count(cell => ((string)cell.ToolTip).Contains("MTrPt005")) == 2 && unavailableCells.Count(cell => ((string)cell.ToolTip).StartsWith("Not a complete total")) == 2 &&
                           FindVisuals<TextBlock>(quantityGrid).Any(text => text.Text == "FSEC1") && window.QuantityNotesList.Items.Count == 4 &&
                           FindVisuals<TextBlock>(window).Any(text => text.Text == "L01_H02_P01 · incomplete"),
                        "switching the candidate replaces the table; a quantity that cannot be established reads n/a with its reason, in its row and in the total");
                    var gapBitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    gapBitmap.Render(window);
                    var gapEncoder = new PngBitmapEncoder(); gapEncoder.Frames.Add(BitmapFrame.Create(gapBitmap));
                    using (var stream = File.Create(".work/quantities-tab-unavailable.png")) gapEncoder.Save(stream);
                    uiState.Connection = "Connection Lost";
                    Pump(() => vm.QuantityCandidates.Count == 0);
                    Assert(!workflowTabs.IsEnabled && !quantityPicker.IsEnabled && quantityPicker.Items.Count == 0 && quantityGrid.Items.Count == 0 &&
                           FindVisuals<TextBlock>(window).Any(text => text.Text == "Connect SAP") && !vm.ReloadQuantities.CanExecute(null),
                        "without a SAP connection Tab 08 is disabled and holds no candidate and no numbers");
                    uiState.Connection = "Connected";
                    Pump(() => vm.QuantityCandidates.Count == 3 && vm.QuantityState == QuantityViewState.Ready && !vm.IsBusy);
                    Assert(workflowTabs.IsEnabled && quantityPicker.IsEnabled && quantityPicker.Items.Count == 3 && quantityPicker.SelectedIndex == 0 && quantityGrid.Items.Count == 6 &&
                           vm.ReloadQuantities.CanExecute(null),
                        "a successful SAP connection enables Tab 08 again and reads the running model");
                }
                finally { try { Directory.Delete(optimizationRoot, true); } catch { } }
                window.Close(); bindingListener.Flush();
                if (bindingLog.ToString().Length > 0) throw new Exception("WPF binding errors: " + bindingLog);
                Console.WriteLine("PASS: Tab 5 rendered with no WPF binding errors.");
                return 0;
            }
            if (args.Contains("--fem"))
            {
                PlanarFrameChecks.Run();
                return 0;
            }
            // Live: starts its own SAP2000 on a copy of the baseline under .work and checks that cable lengths follow the joints.
            var cableLengthLive = Array.IndexOf(args, "--cable-length-live");
            if (cableLengthLive >= 0)
            {
                if (cableLengthLive + 2 >= args.Length) throw new ArgumentException("--cable-length-live needs: <baseline .sdb> <project .json> [--analyse].");
                CableLengthLiveCheck.RunAsync(args[cableLengthLive + 1], args[cableLengthLive + 2], args.Contains("--analyse")).GetAwaiter().GetResult();
                return 0;
            }
            // Tab 08 quantity take-off of every candidate of a study on disk, from the candidates' own text exports. Offline; reads only.
            var quantityStudy = Array.IndexOf(args, "--quantity-study");
            if (quantityStudy >= 0)
            {
                if (quantityStudy + 1 >= args.Length) throw new ArgumentException("--quantity-study needs: <baseline .sdb>.");
                QuantityTakeoffChecks.PrintStudy(args[quantityStudy + 1]);
                return 0;
            }
            // Live, read-only: the Tab 08 take-off of the model open in the running SAP2000, through the API. Attaches; opens and writes nothing.
            if (args.Contains("--quantity-connected"))
            {
                QuantityTakeoffChecks.RunConnectedAsync().GetAwaiter().GetResult();
                return 0;
            }
            // Live: the isolated SAP2000 read of one candidate model — a SAP2000 of its own on a private copy; never attaches to the user's.
            var quantityLive = Array.IndexOf(args, "--quantity-live");
            if (quantityLive >= 0)
            {
                if (quantityLive + 1 >= args.Length) throw new ArgumentException("--quantity-live needs: <candidate .sdb>.");
                QuantityTakeoffChecks.RunLiveAsync(args[quantityLive + 1]).GetAwaiter().GetResult();
                return 0;
            }
            // Internal FEM against SAP2000 on a real study, like for like. Offline: reads the fixture only.
            //   --fem-sap [fixture folder]                                   compare (default tests/fem-sap-benchmark)
            //   --fem-sap-capture <model.$2k> <project.json> <raw results.json> [fixture folder]   refresh the fixture
            var femSapCapture = Array.IndexOf(args, "--fem-sap-capture");
            if (femSapCapture >= 0)
            {
                if (femSapCapture + 3 >= args.Length)
                    throw new ArgumentException("--fem-sap-capture needs: <SAP2000 model text .$2k> <project .json> <Study_Raw_Results.json> [fixture folder].");
                FemSapBenchmark.Capture(args[femSapCapture + 1], args[femSapCapture + 2], args[femSapCapture + 3],
                    femSapCapture + 4 < args.Length ? args[femSapCapture + 4] : FemSapBenchmark.DefaultFolder);
                return 0;
            }
            var femSap = Array.IndexOf(args, "--fem-sap");
            if (femSap >= 0)
            {
                var fixture = femSap + 1 < args.Length && !args[femSap + 1].StartsWith("--", StringComparison.Ordinal) ? args[femSap + 1] : FemSapBenchmark.DefaultFolder;
                FemSapBenchmark.Run(fixture, Path.Combine(".work", "fem-vs-sap"));
                Console.WriteLine("Internal FEM against SAP2000: all like-for-like checks passed.");
                return 0;
            }
            if (args.Contains("--ui-read"))
            {
                using var sap = new SapGateway();
                var app = new System.Windows.Application();
                var bindingLog = new StringWriter();
                var bindingListener = new TextWriterTraceListener(bindingLog);
                PresentationTraceSources.DataBindingSource.Listeners.Add(bindingListener);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/COT VCF tools;component/src/Presentation/Theme.xaml", UriKind.Relative) });
                var uiState = new ApplicationState();
                var uiWorkflow = new WorkflowService(sap, uiState);
                uiWorkflow.ConnectAsync().GetAwaiter().GetResult();
                uiWorkflow.RefreshAsync().GetAwaiter().GetResult();
                foreach (var cable in uiState.Geometry.Members.Where(m => m.Kind == MemberKind.Cable))
                    uiState.Cables.Add(new CablePretensionConfig { CableName = cable.Name });
                if (uiState.Geometry.Points.Count > 0 && uiState.LoadPatterns.Count > 0)
                    uiState.PointLoads.Add(new() { PointName = uiState.Geometry.Points.Keys.First(), LoadPattern = uiState.LoadPatterns[0] });
                var uiConfig = new COT_VCF.Configuration.ConfigService(sap, uiState, uiWorkflow);
                var vm = new MainViewModel(uiState, uiWorkflow, new NoSelection(), uiConfig);
                var window = new COT_VCF.Presentation.MainWindow { DataContext = vm };
                window.Show();
                for (int i = 0; i < 5; i++)
                {
                    vm.SelectedTab = i;
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    Directory.CreateDirectory(".work");
                    using var stream = File.Create($".work/ui-tab-{i + 1}.png"); encoder.Save(stream);
                    Console.WriteLine($"PASS: tab {i + 1} created and rendered with live model geometry.");
                }
                window.Close(); bindingListener.Flush();
                if (bindingLog.ToString().Length > 0) throw new Exception("WPF binding errors: " + bindingLog);
                Console.WriteLine("PASS: no WPF data-binding errors."); return 0;
            }
            if (args.Contains("--selection-ui"))
            {
                var app = new System.Windows.Application();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/COT VCF tools;component/src/Presentation/Theme.xaml", UriKind.Relative) });
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                timer.Tick += (_, _) =>
                {
                    var dialog = app.Windows.Cast<Window>().FirstOrDefault(w => w.Title == "Selection check");
                    if (dialog is null) return;
                    var grid = FindVisual<DataGrid>(dialog)!;
                    Assert(grid.Columns.Count == 1 && grid.Columns[0] is DataGridTextColumn, "Selection dialog has no checkbox column");
                    grid.SelectedItems.Add(grid.Items[1]); grid.SelectedItems.Add(grid.Items[2]);
                    timer.Stop(); dialog.DialogResult = true;
                };
                timer.Start();
                var result = new SelectionService().Select("Selection check", ["A", "B", "C"], []);
                Assert(result!.SequenceEqual(["B", "C"]), "Blue-highlighted rows are returned as the selection");
                return 0;
            }
            if (args.Contains("--report-pdf"))
            {
                // Opt-in: prints a generated report to a real PDF with the installed Edge / Chrome. No SAP2000.
                ReportPrintChecks.RunPdfExport();
                return 0;
            }
            if (args.Contains("--sap-read"))
            {
                using var sap = new SapGateway();
                var info = sap.ConnectAsync().GetAwaiter().GetResult();
                Console.WriteLine($"Connected: {info.Name}; SAP {info.Version}; {info.Units}; locked={info.IsLocked}");
                var snapshot = sap.ReadAsync().GetAwaiter().GetResult();
                Console.WriteLine($"Read: {snapshot.Geometry.Points.Count} points, {snapshot.Geometry.Members.Count} members, {snapshot.LoadPatterns.Count} load patterns.");
                var sources = sap.ReadForcesAsync(snapshot.Geometry.Members.Where(m => m.Kind != MemberKind.Frame).ToArray(), info).GetAwaiter().GetResult();
                Console.WriteLine($"Read {sources.Count} cable/tendon force sources; {sources.Count(s => s.Force.HasValue)} with numeric force.");
                Console.WriteLine("Read-only SAP smoke check passed. No model changes requested.");
                return 0;
            }
            var g = new GeometryModel(new Dictionary<string, StructuralPoint>
            {
                ["top"] = new("top", new(0, 0, 20)), ["mid"] = new("mid", new(0, 0, 10)), ["bot"] = new("bot", new(0, 0, 0)), ["other"] = new("other", new(5, 0, 8))
            }, [new("cable", "top", "bot", MemberKind.Cable)]);
            var t = new TrussHeightConfiguration { TopPoints = ["top"], MiddlePoints = ["mid"], BottomPoints = ["bot"], BaselineHeight = 20, MinimumHeight = 20, MaximumHeight = 30, Increment = 1, CurrentHeight = 25 };
            var service = new TrussGeometryService();
            var preview = service.Preview(g, t);
            Assert(preview.Points["top"].Position.Z == 20 && preview.Points["mid"].Position.Z == 7.5 && preview.Points["bot"].Position.Z == -5, "20 to 25 height transformation");
            Assert(preview.Points["other"].Position == g.Points["other"].Position, "Unselected points stay fixed");
            t.CurrentHeight = 21; preview = service.Preview(g, t);
            Assert(preview.Points["mid"].Position.Z == 9.5 && preview.Points["bot"].Position.Z == -1 && g.Points["bot"].Position.Z == 0, "Repeated previews use immutable baseline");
            t.MiddlePoints = ["top"];
            Assert(service.Validate(g, t).Any(i => i.Message.Contains("more than one")), "Overlapping groups rejected");
            t.MiddlePoints = ["mid"]; t.BottomPoints = ["missing"];
            Assert(service.Validate(g, t).Any(i => i.Message.Contains("no longer exists")), "Missing points rejected");
            t.BottomPoints = ["bot"]; t.CurrentHeight = 21.5;
            Assert(!TrussGeometryService.OnGrid(t.CurrentHeight, t.MinimumHeight, t.MaximumHeight, t.Increment), "Off-grid preview detected");
            Assert(!TrussGeometryService.ValidRange(0, 10, 0) && !TrussGeometryService.ValidRange(0, double.NaN, 1) && !TrussGeometryService.OnGrid(double.PositiveInfinity, 0, 10, 1), "Non-finite and zero-step ranges rejected");
            var state = new ApplicationState { Geometry = g, Baseline = g, Connection = "Connected", Model = new("test.sdb", "26", 6, "kN", "m", false) };
            state.LoadPatterns.Add("LIVE"); state.PointLoads.Add(new() { PointName = "top", LoadPattern = "LIVE", Fx = double.NaN });
            Assert(new ConfigurationValidator().ValidateLoads(state).Count == 1, "Non-finite load rejected");
            state.PointLoads[0].Fx = 10; state.PointLoads.Add(new() { PointName = "top", LoadPattern = "LIVE" });
            Assert(new ConfigurationValidator().ValidateLoads(state).Any(i => i.Message.Contains("Duplicate")), "Duplicate load assignment rejected");
            var fake = new FakeSap { Snapshot = new(state.Model, g, ["LIVE"]) };
            var workflow = new WorkflowService(fake, state);
            state.Truss.TopPoints = ["top"]; state.Truss.MiddlePoints = ["mid"]; state.Truss.BottomPoints = ["bot"];
            workflow.CaptureBaseline(); state.Truss.CurrentHeight = 25; workflow.UpdatePreview();
            Assert(fake.Writes == 0 && state.Geometry.Points["bot"].Position.Z == 0, "Preview does not write to SAP");
            workflow.ApplyGeometryAsync().GetAwaiter().GetResult();
            Assert(fake.Writes == 1 && state.Geometry.Points["bot"].Position.Z == -5 && state.Baseline.Points["bot"].Position.Z == 0, "Apply retains session baseline");
            state.Truss.CurrentHeight = 21; workflow.UpdatePreview();
            Assert(state.Preview.Points["bot"].Position.Z == -1, "Apply followed by preview has no drift");
            try { workflow.RefreshAsync().GetAwaiter().GetResult(); } catch (InvalidOperationException) { }
            Assert(!state.BaselineCaptured && state.Truss.TopPoints.Contains("top") && state.PointLoads.Count == 2, "External geometry changes invalidate baseline but preserve setup");
            NumericInputChecks.Run();
            CheckNewConfiguration();
            OutputSettingsChecks.Run();
            ReliabilityChecks.Run();
            StudyComposerChecks.Run();
            PlanarFrameChecks.Run();
            StudyExecutionChecks.Run();
            OptimizationChecks.Run();
            CableForceReportChecks.Run();
            ReportPrintChecks.Run();
            MemberForceReportChecks.Run();
            SupportReactionReportChecks.Run();
            GatewayChecks.Run();
            QuantityTakeoffChecks.Run();
            FemModelFileChecks.Run();
            Console.WriteLine("All workflow and SAP write-boundary checks passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void CheckNewConfiguration()
    {
        var state = StudyComposerChecks.CreateState();
        var sap = new FakeSap { Snapshot = new(state.Model!, state.Geometry, state.LoadPatterns.ToArray()) };
        var workflow = new WorkflowService(sap, state);
        var config = new ConfigService(sap, state, workflow);
        var vm = new MainViewModel(state, workflow, new NoSelection(), config);
        var model = state.Model;
        var geometry = state.Geometry;
        var baseline = state.SessionBaseline;
        state.OutputSettings.DeflectionPoints.Add("top");
        state.OutputSettings.DeflectionOutputCaseName = "SLS";
        state.Study.UseCandidateFilters = true;
        state.Study.MinHeightM = 20;
        state.Study.SetRunSelected("L01_H01_P01", false);
        config.SetActiveProject("old-config.json", new CotVcfConfigDto());
        config.Session.MarkDirty(ConfigTabs.All);
        foreach (var status in new[] { "Disconnected", "Connecting", "Error", "Connection Lost" })
        {
            state.Connection = status;
            Assert(!vm.HasSapConnection && !vm.NewConfig.CanExecute(null) && !vm.AddLoadDefinition.CanExecute(null) &&
                   !vm.GeneratePostProcessOutputs.CanExecute(null) && !vm.GenerateOptimizedReports.CanExecute(null),
                $"Workflow commands are unavailable while SAP is {status}");
            var count = state.LoadDefinitions.Count;
            vm.AddLoadDefinition.Execute(null);
            vm.NewConfig.Execute(null);
            Assert(state.LoadDefinitions.Count == count && config.Session.HasActiveProject,
                "Disconnected command execution cannot change configuration");
            Assert(vm.Connect.CanExecute(null) && vm.Refresh.CanExecute(null), "Reconnect remains available");
        }
        state.Connection = "Connected";
        Assert(vm.NewConfig.CanExecute(null) && vm.AddLoadDefinition.CanExecute(null), "Workflow commands enable after connection");
        vm.NewConfig.Execute(null);
        Assert(!config.Session.HasActiveProject && !config.Session.IsDirty(ConfigTabs.All) &&
               state.AppliedPoints.Count == 0 && state.Cables.Count == 0 && !state.BaselineCaptured &&
               state.Truss.TopPoints.Count == 0 && !state.OutputSettings.IsConfigured &&
               !state.Study.UseCandidateFilters && state.Study.MinHeightM is null && state.Study.ExcludedIterationKeys.Count == 0,
            "New Config clears tab configuration, project file identity, filters and dirty flags");
        Assert(state.LoadDefinitions.Select(row => row.LoadName).SequenceEqual(new[] { "DL_Above", "LL_Above" }) &&
               ReferenceEquals(model, state.Model) && ReferenceEquals(geometry, state.Geometry) &&
               ReferenceEquals(baseline, state.SessionBaseline) && state.IsSapConnected && sap.Writes == 0,
            "New Config restores loading defaults and preserves SAP connection and baseline without writes");
    }

    private static void Assert(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    private static IEnumerable<T> FindVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var found in FindVisuals<T>(VisualTreeHelper.GetChild(root, i))) yield return found;
    }
    private sealed class NoSelection : ISelectionService
    {
        public IReadOnlyList<string>? Select(string title, IReadOnlyList<string> available, IReadOnlyList<string> selected) => null;
    }
    private sealed class FakeSap : ISapGateway
    {
        public required ModelSnapshot Snapshot { get; init; }
        public int Writes { get; private set; }
        public int CableSectionReadCount { get; private set; }
        public int DatabaseTableReads { get; private set; }
        public Task<COT_VCF.Application.Quantities.SapTableSet> ReadDatabaseTablesAsync(IReadOnlyList<string> tableNamePrefixes, ModelInfo expected)
        {
            DatabaseTableReads++;
            return Task.FromResult(QuantityTakeoffChecks.FixtureTables(expected));
        }
        public Task<ModelInfo> ConnectAsync() => Task.FromResult(Snapshot.Info);
        public Task<ModelInfo> OpenModelAsync(string modelPath) => Task.FromResult(Snapshot.Info with { Path = modelPath });
        public Task<ModelInfo> SaveAsAsync(string targetPath, ModelInfo expected) => Task.FromResult(expected with { Path = targetPath, IsLocked = false });
        public Task SaveAsync(ModelInfo expected) => Task.CompletedTask;
        public Task RunAnalysisAsync(ModelInfo expected) => Task.CompletedTask;
        public Task<ModelInfo> GetCurrentModelInfoAsync() => Task.FromResult(Snapshot.Info);
        public Task<ModelSnapshot> ReadAsync() => Task.FromResult(Snapshot);
        public Task<IReadOnlyList<PointLoad>> ReadPointLoadsAsync(ModelInfo expected) => Task.FromResult<IReadOnlyList<PointLoad>>([]);
        public Task<Position> GetPointPositionAsync(string pointName, ModelInfo expected) => Task.FromResult(Snapshot.Geometry.Points[pointName].Position);
        public Task<IReadOnlyList<PointLoad>> GetPointLoadsAsync(string pointName, ModelInfo expected) => Task.FromResult<IReadOnlyList<PointLoad>>([]);
        public Task<IReadOnlyList<CableTargetForce>> GetCableTargetForcesAsync(string cableName, ModelInfo expected) => Task.FromResult<IReadOnlyList<CableTargetForce>>([]);
        public Task<IReadOnlyList<CableSection>> GetCableSectionsAsync(IReadOnlyList<string> cableNames, ModelInfo expected)
        {
            CableSectionReadCount++;
            return Task.FromResult<IReadOnlyList<CableSection>>(cableNames
                .Select(name => new CableSection(name, 0.001, "SEC_CABLE")).ToArray());
        }
        public Task<IReadOnlyList<COT_VCF.Domain.Results.JointDisplacement>> GetJointDisplacementsAsync(IReadOnlyList<string> pointNames, string outputCase, bool isCombo, ModelInfo expected) => Task.FromResult<IReadOnlyList<COT_VCF.Domain.Results.JointDisplacement>>([]);
        public Task<IReadOnlyList<COT_VCF.Domain.Results.CableForceRead>> GetCableForcesAsync(IReadOnlyList<string> cableNames, string outputCase, bool isCombo, ModelInfo expected) => Task.FromResult<IReadOnlyList<COT_VCF.Domain.Results.CableForceRead>>([]);
        public Task<IReadOnlyList<COT_VCF.Domain.Results.FrameForceRead>> GetFrameForcesAsync(IReadOnlyList<string> frameNames, string outputCase, bool isCombo, ModelInfo expected) => Task.FromResult<IReadOnlyList<COT_VCF.Domain.Results.FrameForceRead>>([]);
        public Task<IReadOnlyList<ForceSource>> ReadForcesAsync(IReadOnlyList<StructuralMember> m, ModelInfo e) => Task.FromResult<IReadOnlyList<ForceSource>>([]);
        public Task ApplyLoadsAsync(IReadOnlyList<PointLoad> l, ModelInfo e) { Writes++; return Task.CompletedTask; }
        public Task MovePointsAsync(GeometryModel a, GeometryModel b, ModelInfo e) { Writes++; return Task.CompletedTask; }
        public Task SetCableTargetForceAsync(string cableName, string loadPattern, double targetForce, ModelInfo expected) { Writes++; return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> GetLoadCaseNamesAsync(ModelInfo expected) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<SapGroupSnapshot>> ReadOutputGroupsAsync(IReadOnlyList<string> groupNames, ModelInfo expected) =>
            Task.FromResult<IReadOnlyList<SapGroupSnapshot>>(groupNames.Select(name => new SapGroupSnapshot(name, false, [], [], [])).ToArray());
        public Task ReplaceOutputGroupsAsync(IReadOnlyList<OutputSapGroupDefinition> groups, ModelInfo expected) => Task.CompletedTask;
        public void Dispose() { }
    }
}
