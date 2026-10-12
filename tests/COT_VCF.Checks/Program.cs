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
            if (args.Contains("--spatial-frame-regression"))
            {
                SpatialFrameRegressionChecks.Run();
                return 0;
            }
            if (args.Contains("--fem-db"))
            {
                FemDatabaseChecks.Run();
                return 0;
            }
            if (args.Contains("--sap-import"))
            {
                SapImportChecks.Run();
                return 0;
            }
            if (args.Contains("--dev-log"))
            {
                DevelopmentLogChecks.Run();
                return 0;
            }
            if (args.Contains("--etabs-export"))
            {
                EtabsExportChecks.Run();
                return 0;
            }
            // opt-in, live: exports into a private hidden ETABS this check starts (never the user's), imports it back and runs it
            if (args.Contains("--etabs-export-live"))
            {
                EtabsExportChecks.RunLive();
                return 0;
            }
            if (args.Contains("--etabs-import"))
            {
                EtabsImportChecks.Run();
                return 0;
            }
            // opt-in, live: imports the model open in the running ETABS (read-only — ETABS is never written)
            if (args.Contains("--etabs-live"))
            {
                EtabsImportChecks.RunLive();
                return 0;
            }
            if (args.Contains("--fem-model"))
            {
                FemModelFileChecks.Run();
                return 0;
            }
            if (args.Contains("--blank-ui"))
            {
                var app = new System.Windows.Application();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Mcotis;component/Presentation/Theme.xaml", UriKind.Relative) });
                CheckBlankTemplateUi();
                return 0;
            }
            if (args.Contains("--result-tools-ui"))
            {
                var app = new System.Windows.Application();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Mcotis;component/Presentation/Theme.xaml", UriKind.Relative) });
                ResultToolbarChecks.Run();
                return 0;
            }
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
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Mcotis;component/Presentation/Theme.xaml", UriKind.Relative) });
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
                CheckBlankTemplateUi(window);
                uiState.Connection = "Disconnected";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                MenuItem TopMenu(string header) => window.MainMenu.Items.OfType<MenuItem>().Single(item => (item.Header as string)?.Replace("_", "") == header);
                Assert(window.MainMenu.Items.OfType<MenuItem>().Select(item => (item.Header as string)!.Replace("_", ""))
                           .SequenceEqual(["File", "Define", "Edit", "Assign", "Run", "Result", "Post-Processing", "Display", "Design", "Help", "Optimization", "MB"]) &&
                       window.MainMenu.Items[^2] is Separator && ReferenceEquals(window.MainMenu.Items[^1], TopMenu("MB")) &&
                       window.MainMenu.Items[^4] is Separator && ReferenceEquals(window.MainMenu.Items[^3], TopMenu("Optimization")),
                    "The menu bar puts Define before Edit, followed by Assign and the analysis/result menus; Optimization, then MB, come last, each after a separator");
                Assert(!FindVisuals<TabControl>(window).First(tabs => tabs.Name == "WorkflowTabs").IsEnabled &&
                       vm.NewConfig.CanExecute(null) && !TopMenu("MB").IsEnabled && TopMenu("Define").IsEnabled && TopMenu("File").IsEnabled,
                    "Before a model exists, model workflows stay disabled while New Config and Define remain available");
                uiState.Connection = "Connected";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert(FindVisuals<TabControl>(window).First(tabs => tabs.Name == "WorkflowTabs").IsEnabled &&
                       vm.NewConfig.CanExecute(null) && TopMenu("MB").IsEnabled && TopMenu("Define").IsEnabled && TopMenu("Edit").IsEnabled,
                    "A model with geometry enables workflow pages and the Define/Edit sequence");
                var mbItems = TopMenu("MB").Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray();
                var fileCommands = TopMenu("File").Items.OfType<MenuItem>().Select(item => item.Command).ToArray();
                var runCommands = TopMenu("Run").Items.OfType<MenuItem>().Select(item => item.Command).ToArray();
                Assert(mbItems.SequenceEqual(["01  Loading", "02  Truss Height", "03  Pretension", "04  Output Settings", "05  Iteration",
                           "06A  Optimization", "08  Quantities"]) &&
                       new[] { vm.NewConfig, vm.LoadFemModel, vm.SaveFemModel, vm.ReadFemProperties }.All(fileCommands.Contains) &&
                       !fileCommands.Contains(vm.LoadConfig) && !fileCommands.Contains(vm.SaveAllConfig) &&
                       new[] { vm.RunStudyFromToolbar, vm.RunFemAnalysis, vm.CancelFemAnalysis }.All(runCommands.Contains),
                    "MB lists pages 01-05, 06A and 08; File holds config commands; Run holds analysis commands");
                var workflowTabsRail = FindVisuals<TabControl>(window).First(tabs => tabs.Name == "WorkflowTabs");
                Assert(workflowTabsRail.Items.OfType<TabItem>().All(tab => tab.Visibility == Visibility.Collapsed) &&
                       vm.PaneMenu == "MB" && vm.PaneTitle == "01  LOADING",
                    "The left tab rail is hidden; the pane header names the page opened from MB");
                window.Width = window.MinWidth;
                window.UpdateLayout();
                var toolbarCommands = new[] { vm.Connect, vm.Refresh };
                var toolbarButtons = FindVisuals<Button>(window).Where(button => toolbarCommands.Contains(button.Command)).ToArray();
                var movedCommands = new[] { vm.NewConfig, vm.SaveFemModel, vm.LoadFemModel, vm.RunStudyFromToolbar };
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

                // 07 FEM (index 7) is split into menu panes: Define -> Sections / Load Patterns / Load Cases / Result Load Case,
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
                OpenMenu("Edit", "VCF System");
                var vcfWindow = window.OwnedWindows.OfType<VcfGeometryWindow>().Single();
                vcfWindow.UpdateLayout();
                Assert(vcfWindow.IsVisible && vcfWindow.Preview.Model.Points.Count == uiState.Geometry.Points.Count,
                    "Edit -> VCF System opens its own window with a preview of the model");
                vcfWindow.Close();
                // Cable Force / Storey Force panes on the default model (10 bays of 10 m, B 12 m, 10 storeys)
                var (defaultVm, _, _, _) = FemModelFileChecks.DefaultModelApp();
                var forcesWindow = new VcfGeometryWindow(defaultVm) { Owner = window };
                forcesWindow.Show();
                forcesWindow.UpdateLayout();
                var sheetTabs = forcesWindow.ParameterTabs.Items.OfType<TabItem>().ToArray();
                Assert(sheetTabs.Select(tab => tab.TranslatePoint(new Point(0, 0), forcesWindow).Y).Distinct().Count() == 1 &&
                       FindVisuals<GridSplitter>(forcesWindow).Any(splitter => System.Windows.Automation.AutomationProperties.GetName(splitter) == "Resize sheets"),
                    "the four VCF sheets sit on one line, and the sheets / preview split can be dragged");
                var cableRows = forcesWindow.CableForces;
                var storeyRows = forcesWindow.StoreyForces;
                string Kn(double value) => value.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
                Assert(forcesWindow.ParameterTabs.Items.OfType<TabItem>().Select(tab => tab.Header as string).SequenceEqual(["Geometry", "Cable Force", "Storey Force", "Sections"]) &&
                       cableRows.Count == 16 && cableRows.Single(row => row.Cable == "Cable1a-2").Force == Kn(100_000) &&
                       cableRows.Single(row => row.Cable == "Cable2a-2").Force == Kn(30_000) && cableRows.Single(row => row.Cable == "Cable1b-2").Sync.StartsWith("=", StringComparison.Ordinal),
                    "VCF Geometry has sheets Geometry | Cable Force | Storey Force | Sections, and shows each cable's pretension on Cable Force (Cable1a-2 100,000 kN, Cable2a-2 30,000 kN, Cable1b-2 following Cable1a-2)");
                Assert(storeyRows.Count == 11 && storeyRows[0].Area == "60" && storeyRows[5].Area == "120" &&
                       storeyRows[5].Forces[0] == Kn(-12_000) && storeyRows[0].Forces[0] == Kn(-6_000) && storeyRows[5].Forces[1] == Kn(-6_000) &&
                       forcesWindow.StoreyLoads.Select(row => (row.Pattern, row.Qz)).SequenceEqual([("DEAD_ABOVE", "10"), ("LL_ABOVE", "5")]) &&
                       forcesWindow.StoreyWidthBox.Text == "12" && forcesWindow.StoreyCountBox.Text == "10",
                    "Storey Force holds the loads of the model as one value each — B 12 m, 10 storeys, DEAD_ABOVE 10 kPa, LL_ABOVE 5 kPa (the first value of its 01 Loading range) — " +
                    "and shows the force at each loaded point: −10 kPa × 120 m² × 10 storeys = −12,000 kN inside, half at the ends");
                forcesWindow.SetInput("Bay", "12");
                forcesWindow.UpdateLayout();
                Assert(forcesWindow.StoreyForces[5].Area == "144" && forcesWindow.StoreyForces[5].Forces[0] == Kn(-14_400),
                    "Storey Force follows the previewed geometry (12 m bays: 144 m², −14,400 kN)");
                forcesWindow.Close();
                // Cable Force is where the target force of the model's cables is set: typed there, written on Apply
                var (forceVm, forceState, _, _) = FemModelFileChecks.DefaultModelApp();
                var forceWindow = new VcfGeometryWindow(forceVm) { Owner = window };
                forceWindow.Show();
                forceWindow.UpdateLayout();
                var leaderRow = forceWindow.CableForces.Single(row => row.Cable == "Cable1a-2");
                var followerRow = forceWindow.CableForces.Single(row => row.Cable == "Cable1b-2");
                var followerRefused = false;
                try { followerRow.Force = "1"; } catch (ArgumentException) { followerRefused = true; }
                var badNumber = false;
                try { leaderRow.Force = "-5"; } catch (ArgumentException) { badNumber = true; }
                leaderRow.Force = "90000";
                forceWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert(leaderRow.Editable && !followerRow.Editable && followerRefused && badNumber && leaderRow.Force == Kn(90_000) &&
                       forceWindow.CableForceEdits.Count == 1 && forceWindow.CableForceEdits["Cable1a-2"] == 90_000 &&
                       forceWindow.CableForces.Single(row => row.Cable == "Cable1b-2").Force == Kn(90_000) &&
                       forceState.Cables.Single(cable => cable.CableName == "Cable1a-2").GetCandidateForces()[0] == 100_000,
                    "Cable Force takes a target force for an independent cable (staged until Apply); a follower and a negative value are refused");
                Assert(!forceWindow.CableForces.Single(row => row.Cable == "Cable1a-2").SyncChoices.Contains("Cable1b-2"),
                    "Cable Sync excludes dependencies that would create a cycle");
                forceWindow.CableForces.Single(row => row.Cable == "Cable1b-2").SyncSelection = "Independent";
                forceWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var independent = forceWindow.CableForces.Single(row => row.Cable == "Cable1b-2");
                independent.Force = "80000";
                Assert(independent.Editable && forceState.Cables.Single(cable => cable.CableName == "Cable1b-2").IsFollower,
                    "Cable Sync stages Independent and unlocks its force without changing the model");
                independent.SyncSelection = "Cable1a-2";
                forceWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                forceWindow.SetInput("Bays", "12");
                Assert(forceWindow.CableForces.Single(row => row.Cable == "Cable9a-2").Editable &&
                       forceWindow.CableForces.Single(row => row.Cable == "Cable9b-2").SyncSelection == "Cable9a-2",
                    "Additional bays create an editable sequenced symmetric cable pair with its matching dependency");
                forceWindow.SetInput("Bays", "10");
                Assert(forceWindow.CableForces.All(row => row.Cable != "Cable9a-2" && row.Cable != "Cable9b-2"),
                    "Reducing bays removes the extra symmetric cable pair from the form");
                // Storey Force is where the loads of the model are set: one qz per load pattern, B, storeys, loaded points
                var liveRow = forceWindow.StoreyLoads.Single(row => row.Pattern == "LL_ABOVE");
                var badQz = false;
                try { liveRow.Qz = "-1"; } catch (ArgumentException) { badQz = true; }
                liveRow.Qz = "7.5";
                forceWindow.StoreyWidthBox.Text = "0";
                var zeroWidth = !forceWindow.ApplyButton.IsEnabled && forceWindow.ProblemText.Text.Contains("tributary width B", StringComparison.Ordinal);
                forceWindow.StoreyWidthBox.Text = "10";
                forceWindow.StoreyCountBox.Text = "8";
                var extra = forceWindow.AddStoreyLoad();
                var twice = !forceWindow.ApplyButton.IsEnabled || extra.Pattern != "DEAD_ABOVE";
                extra.Pattern = "DEAD_ABOVE";
                var repeated = !forceWindow.ApplyButton.IsEnabled && forceWindow.ProblemText.Text.Contains("can be loaded once", StringComparison.Ordinal);
                forceWindow.RemoveStoreyLoad(extra);
                forceWindow.SetStoreyPoints(forceState.AppliedPoints.Select(point => point.PointName).Skip(1).ToArray());
                forceWindow.UpdateLayout();
                Assert(badQz && zeroWidth && twice && repeated && forceWindow.ApplyButton.IsEnabled && forceWindow.StoreyForces.Count == 10 &&
                       forceWindow.StoreyForces[5].Forces[1] == Kn(-7.5 * 100 * 8) && forceWindow.StoreyForces[0].Forces[0] == Kn(-10 * 50 * 8) &&
                       forceState.TributaryWidthB == 12 && forceState.LoadDefinitions.Single(load => load.LoadName == "LL_ABOVE").ApplyIncrementStep,
                    "Storey Force takes B, the storeys, the loaded points and one qz per load pattern; the forces follow at once (7.5 kPa × 100 m² × 8 storeys = −6,000 kN), " +
                    "bad input greys Apply, and the model is untouched until Apply");
                // the user chooses the loaded points: on the preview (click, then + Add Selected) or in the table (- Remove)
                forceWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var forceCanvas = forceWindow.Preview.Canvas;
                var arrows = forceWindow.Preview.AppliedPoints!.Select(point => point.PointName).ToArray();
                var idle = !forceWindow.StoreyAddSelectedButton.IsEnabled && !forceWindow.StoreyRemovePointsButton.IsEnabled && forceWindow.StoreyAddPointsButton.IsEnabled;
                forceCanvas.SelectAt(forceCanvas.JointScreenPoints["TTrPt001"]);
                var picked = forceWindow.StoreyAddSelectedButton.IsEnabled && Equals(forceWindow.StoreyAddSelectedButton.Content, "+ Add Selected (1)");
                forceWindow.AddSelectedStoreyPoints();
                forceWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert(arrows.Length == 10 && !arrows.Contains("TTrPt001") && idle && picked && forceWindow.StoreyPoints.Count == 11 && forceWindow.StoreyForces[0].Point == "TTrPt001" &&
                       forceWindow.Preview.AppliedPoints!.Count() == 11 && forceWindow.Preview.SelectedJoints.Count == 0 && forceWindow.StoreyPointsTitle.Text == "LOADED POINTS (11)",
                    "the loaded points are drawn on the preview; a point clicked there is loaded with + Add Selected");
                forceWindow.StoreyForceGrid.SelectedItem = forceWindow.StoreyForces[0];
                var rowPicked = forceWindow.StoreyRemovePointsButton.IsEnabled && forceWindow.Preview.SelectedAppliedPoint?.PointName == "TTrPt001";
                forceWindow.RemoveSelectedStoreyPoints();
                Assert(rowPicked && forceWindow.StoreyPoints.Count == 10 && !forceWindow.StoreyPoints.Contains("TTrPt001") && forceWindow.StoreyForces[0].Point == "TTrPt002" &&
                       forceState.AppliedPoints.Count == 11,
                    "- Remove takes the load off the point selected in the table (highlighted on the preview); the model still waits for Apply");
                forceWindow.ApplyFormAsync().GetAwaiter().GetResult();
                forceWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert(forceState.Cables.Single(cable => cable.CableName == "Cable1a-2") is { ApplyRange: false, TargetForce: 90_000.0 } &&
                       forceWindow.CableForceEdits.Count == 0 && forceWindow.CableForces.Single(row => row.Cable == "Cable1a-2").Force == Kn(90_000) &&
                       forceWindow.CableForces.Single(row => row.Cable == "Cable1b-2").Force == Kn(90_000),
                    "Apply writes the target force to the model: Cable1a-2 90,000 kN, and Cable1b-2 follows it");
                Assert(forceState.TributaryWidthB == 10 && forceState.NumberOfStories == 8 && forceState.AppliedPoints.Count == 10 &&
                       forceState.LoadDefinitions.Single(load => load.LoadName == "LL_ABOVE") is { ApplyIncrementStep: false, Qz: 7.5 } &&
                       forceState.LoadDefinitions.Single(load => load.LoadName == "DEAD_ABOVE") is { ApplyIncrementStep: false, Qz: 10.0 } && forceState.LoadDefinitions.Count == 2 &&
                       forceWindow.StoreyLoads.Single(row => row.Pattern == "LL_ABOVE").Qz == "7.5" && forceVm.FemRunSummary.StartsWith("1 FEM run", StringComparison.Ordinal) &&
                       !forceVm.FemRunSummary.Contains("ranges", StringComparison.Ordinal),
                    "…and the storey loads: B 10 m, 8 storeys, 10 loaded points, LL_ABOVE fixed at 7.5 kPa — the model has no range left, so Run uses exactly what the sheets show");
                forceWindow.Close();
                // Run asks which load cases to analyse (Set Load Cases to Run)
                var runCases = new RunLoadCasesWindow(forceVm) { Owner = window };
                runCases.Show();
                runCases.UpdateLayout();
                var runItems = runCases.Items;
                Assert(runItems.Select(item => (item.Name, item.From, item.Status, item.Action)).SequenceEqual([
                           (FemLoadPlan.DefaultPretensionCase, "", "Not Run", "Run"), (FemLoadPlan.DefaultServiceCase, FemLoadPlan.DefaultPretensionCase, "Not Run", "Run"),
                           (FemLoadPlan.DefaultUltimateCase, FemLoadPlan.DefaultPretensionCase, "Not Run", "Run")]) &&
                       runCases.RunNowButton.IsEnabled && FindVisuals<DataGrid>(runCases).Single().Items.Count == 3,
                    "Set Load Cases to Run lists the load cases — Pretension, then SLS and ULS continuing from it — with their status and action");
                runCases.Toggle(runItems[1]);
                var oneOff = runItems.Select(item => item.Action).ToArray();
                runCases.ToggleAll();
                var allOn = runItems.All(item => item.Run);
                runCases.ToggleAll();
                var noneOn = runItems.All(item => !item.Run) && !runCases.RunNowButton.IsEnabled && !runCases.RunNow();
                Assert(oneOff.SequenceEqual(["Run", "Do Not Run", "Run"]) && allOn && noneOn && forceVm.FemCaseRows.All(row => row.Run),
                    "Run/Do Not Run Case and Run/Do Not Run All change the action; nothing is kept until Run Now or OK, and Run Now needs a case set to Run");
                runCases.Toggle(runItems[0]);
                runCases.Toggle(runItems[2]);
                Assert(runCases.RunNow() && forceVm.FemCaseRows.Select(row => row.Run).SequenceEqual([true, false, true]),
                    "Run Now keeps the choice: the cases set to Run are the ones the analysis solves");
                runCases.Close();
                // Run -> Optimization VCF: min / max / step per geometry input, candidate sections, preview at a corner
                Assert(new VcfRange(10, 14, 1.5).Values().SequenceEqual([10, 11.5, 13]) && new VcfRange(10, 13, 1.5).Values().SequenceEqual([10, 11.5, 13]) && new VcfRange(8, 8, 0).Count == 1 &&
                       new VcfRange(9, 8, 1).Problem() is not null && new VcfRange(2, 6, 0.5).Problem(whole: true) is not null,
                    "A VCF range runs Min, Min + Step, … up to Max without adding an off-step Max (the Tab 01 / 03 rule); Min = Max is one value; Max < Min and fractional bay steps are refused");
                Assert(TopMenu("Optimization").Items.OfType<MenuItem>().Any(item => (item.Header as string)?.Replace("_", "") == "Optimization VCF…") &&
                       !TopMenu("Run").Items.OfType<MenuItem>().Any(item => (item.Header as string)?.Contains("Optimization") == true),
                    "The Optimization tab offers Optimization VCF…; Run no longer lists it");
                var optimization = new OptimizationVcfWindow(defaultVm) { Owner = window };
                optimization.Show();
                optimization.UpdateLayout();
                var fixedSpace = optimization.FormSettings();
                optimization.SetRange("Bay", "max", "12");
                optimization.SetRange("Bay", "step", "1");
                optimization.SetRange("Bays", "max", "12");
                optimization.PreviewCorner.SelectedIndex = 2;
                optimization.UpdateLayout();
                var space = optimization.FormSettings();
                Assert(fixedSpace.GeometryCount == 1 && fixedSpace.SectionCount == 1 && space.GeometryCount == 9 && optimization.OkButton.IsEnabled &&
                       optimization.ParameterTabs.Items.OfType<TabItem>().Select(tab => tab.Header as string).SequenceEqual(["Geometry", "Cable Force", "Storey Force", "Sections"]) &&
                       optimization.Preview.Model.Points.Count > defaultVm.State.Geometry.Points.Count,
                    "Optimization VCF starts fixed at the model, counts 3 bay widths × 3 bay counts = 9 geometry variants, and previews the maximum corner (12 bays)");
                optimization.SetRange("Depth", "min", "16");
                optimization.SetRange("Depth", "max", "24");
                optimization.SetRange("Depth", "step", "4");
                var withDepth = optimization.FormSettings();
                Assert(optimization.MiddleChordShown == ("8", "12") && !withDepth.Geometry.ContainsKey("Middle") && withDepth.GeometryCount == 27 &&
                       FindVisuals<TextBox>(optimization).Where(box => System.Windows.Automation.AutomationProperties.GetName(box).StartsWith("Middle chord", StringComparison.Ordinal)).All(box => box.IsReadOnly) &&
                       VcfOptimization.ParametersAt(VcfGeometry.Analyze(defaultVm.State.Geometry, defaultVm.State.Truss), withDepth, range => range.Max).MiddleChordHeight == 12,
                    "The middle chord is not an optimization input: read-only at 0.5 × truss depth (depth 16 … 24 → 8 … 12 m) and not counted in the variants");
                // Cable Force starts from Tab 03: one shared step, the fan cables graded by their length; Storey Force from Tab 01
                var forceStart = optimization.FormSettings();
                var startCables = forceStart.CableForces;
                Assert(startCables is { Steps: 1, Grading: VcfFanGrading.Linear, GradientKnPerM: 318 } && startCables.Fan == VcfForceSpan.Fixed(30_000) &&
                       startCables.Cables.Keys.Order().SequenceEqual(["Cable1a-2", "Cable7a-2", "Cable8a-2"]) && startCables.Cables["Cable1a-2"] == new VcfForceSpan(100_000, 105_000) &&
                       forceStart.StoreyLoads[VcfOptimization.StoriesKey].Min == 10 && forceStart.StoreyLoads[VcfOptimization.TributaryKey].Min == 12,
                    "Cable Force starts with one step and the fan graded by length: the shortest fan cable at its 03 Pretension force (30,000 kN) + 318 kN per m, " +
                    "fitted from the shortest to the longest fan cable of the model; the other cables with a force of their own keep their 03 Pretension span " +
                    "(Cable1a-2 100,000 … 105,000); a follower (Cable1b-2) is not listed; Storey Force starts at NStories 10, B 12 m");
                // the rows follow Preview at — here the maximum corner: 12 bays of 12 m, 24 m deep, which adds Cable9a-2 / 9b-2
                static double Shown(string text) => double.Parse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.CurrentCulture);
                var moreRows = optimization.CableForceRows;
                var moreFan = moreRows.Where(row => row.Cable is "Cable2a-2" or "Cable3a-2" or "Cable4a-2" or "Cable5a-2" or "Cable6a-2" or "Cable9a-2").ToArray();
                Assert(optimization.PreviewCorner.Items.OfType<string>().SequenceEqual(["Minimum · 10 bays", "Current · 10 bays", "More cables · 12 bays"]) &&
                       moreFan.Select(row => row.Cable).SequenceEqual(["Cable2a-2", "Cable3a-2", "Cable4a-2", "Cable5a-2", "Cable6a-2", "Cable9a-2"]) &&
                       moreFan[0].Tag == "shortest" && moreFan[0].Min == "30000" && moreFan[^1].Tag == "new" &&
                       moreFan.Skip(1).Select(row => Shown(row.Min)).Zip(moreFan.Skip(2).Select(row => Shown(row.Min))).All(pair => pair.Second > pair.First) &&
                       moreFan.Skip(1).All(row => Shown(row.Min) % 100 == 0 && Shown(row.Min) > 30_000 && row.Min == row.Max && row.Step == "—") &&
                       moreRows.All(row => row.Cable is not ("Cable1b-2" or "Cable2b-2" or "Cable9b-2")) &&
                       optimization.PreviewCaption.Text.Contains("12 bays", StringComparison.Ordinal) && optimization.PreviewCaption.Text.Contains("fan cables 6 + 6", StringComparison.Ordinal) &&
                       optimization.PreviewCaption.Text.Contains("new: Cable9a-2, Cable9b-2", StringComparison.Ordinal) &&
                       optimization.Preview.HighlightedMembers!.Order().SequenceEqual(["Cable9a-2", "Cable9b-2"]) &&
                       optimization.Preview.JointNotes!.Keys.Order().SequenceEqual(Enumerable.Range(2, 11).Select(line => $"BTrPt{line:000}")) &&
                       optimization.Preview.JointNotes["BTrPt002"] == "30" && optimization.Preview.JointNotes["BTrPt007"] == (Shown(moreFan[^1].Min) / 1000).ToString("0.##") &&
                       optimization.Preview.JointNotes["BTrPt012"] == "30" &&
                       optimization.Preview.MemberNotes!.Keys.Order().SequenceEqual(["Cable1a-2", "Cable1b-2", "Cable7a-2", "Cable7b-2", "Cable8a-2", "Cable8b-2"]) &&
                       optimization.Preview.MemberNotes["Cable1b-2"] == "100" && optimization.PreviewCaption.Text.Contains("labels: pretension [MN]", StringComparison.Ordinal),
                    "Cable Force follows Preview at: at More cables · 12 bays the fan lists six cables of one side, the shortest with its inputs and the others by their length " +
                    $"(rounded to 100 kN, growing to {moreFan[^1].Min} kN for the new Cable9a-2); cables that take another's value are not listed; the preview names the new cables, " +
                    "emphasises them and writes each fan cable's force [MN] under the bottom-chord node it is anchored at (one at the mid-span node the two sides share), the other cables' along them");
                optimization.FanGradingCombo.SelectedIndex = 2;
                optimization.UpdateLayout();
                var manualRows = optimization.CableForceRows;
                var copied = Shown(manualRows.Single(row => row.Cable == "Cable9a-2").Min);
                Assert(optimization.FormSettings().CableForces is { Grading: VcfFanGrading.Manual, Fan: null } manual && manual.Cables.ContainsKey("Cable2a-2") && manual.Cables.ContainsKey("Cable4b-2") &&
                       !manual.Cables.ContainsKey("Cable2b-2") && manualRows.Single(row => row.Cable == "Cable6a-2") is var longest && longest.Min == "40000" && longest.Tag == "" &&
                       manualRows.Single(row => row.Cable == "Cable9a-2").Tag == "new" && copied > 40_000 && copied % 100 == 0 &&
                       optimization.FanGradientBox.Visibility != Visibility.Visible,
                    $"Manual gives every fan cable with a force of its own its inputs (Cable6a-2 40,000 kN); the new Cable9a-2 takes Cable6a-2's force × its length ratio ({copied:N0} kN)");
                optimization.FanGradingCombo.SelectedIndex = 0;
                optimization.SetRange("Bays", "min", "8");
                optimization.PreviewCorner.SelectedIndex = 0;
                optimization.UpdateLayout();
                Assert(optimization.PreviewCorner.Items.OfType<string>().First() == "Less cables · 8 bays" &&
                       optimization.CableForceRows.All(row => row.Cable is not ("Cable6a-2" or "Cable9a-2")) && optimization.CableForceRows.Any(row => row.Cable == "Cable5a-2") &&
                       optimization.PreviewCaption.Text.Contains("fan cables 4 + 4", StringComparison.Ordinal) &&
                       optimization.PreviewCaption.Text.Contains("not in this variant: Cable6a-2, Cable6b-2", StringComparison.Ordinal) &&
                       !optimization.Preview.HighlightedMembers!.Any() && !optimization.Preview.Model.Members.Any(member => member.Name == "Cable6a-2"),
                    "At Less cables · 8 bays the fan has four cables a side: Cable6a-2 / 6b-2 leave the rows and the preview, and are named as not in this variant");
                optimization.SetRange("Bays", "min", "10");
                optimization.PreviewCorner.SelectedIndex = 2;
                optimization.UpdateLayout();
                optimization.SetRange("Cable1a-2", "min", "90000", "cable");
                optimization.SetRange("Cable1a-2", "max", "110000", "cable");
                Assert(optimization.FormSettings().ForceCount == forceStart.ForceCount && optimization.CableForceRows.Single(row => row.Cable == "Cable1a-2").Step == "—",
                    "With one step a cable's Max is not reached: no force variants are added");
                optimization.CableStepsBox.Text = "3";
                optimization.SetRange(OptimizationVcfWindow.FanKey, "max", "34000", "cable");
                optimization.SetRange(VcfOptimization.StoriesKey, "max", "12", "storey");
                optimization.SetRange(VcfOptimization.StoriesKey, "step", "2", "storey");
                var forceSpace = optimization.FormSettings();
                var stepped = optimization.CableForceRows;
                Assert(forceSpace.ForceCount == forceStart.ForceCount * 3 * 2 && forceSpace.CombinationCount == forceSpace.GeometryCount * forceSpace.ForceCount &&
                       stepped.Single(row => row.Cable == "Cable1a-2").Step == 10_000.ToString("N0") && stepped.Single(row => row.Cable == "Cable2a-2").Step == 2_000.ToString("N0") &&
                       stepped.Single(row => row.Cable == "Cable9a-2").Step == 2_000.ToString("N0") &&
                       Shown(stepped.Single(row => row.Cable == "Cable9a-2").Max) - Shown(stepped.Single(row => row.Cable == "Cable9a-2").Min) == 4_000 &&
                       optimization.StoreyTotals.Contains("kN", StringComparison.Ordinal),
                    "All cables step together: 3 steps take Cable1a-2 from 90,000 to 110,000 (10,000 a step) and the shortest fan cable from 30,000 to 34,000 — " +
                    "every fan cable by the same 2,000 a step — as 3 force variants, not 3 × 3; a storey range (10 … 12 @ 2 → 2) multiplies them; the storey totals show");
                optimization.CableStepsBox.Text = "0";
                Assert(!optimization.OkButton.IsEnabled && optimization.ProblemText.Text.Contains("Cable force steps", StringComparison.Ordinal), "Zero cable force steps are refused");
                optimization.CableStepsBox.Text = "3";
                optimization.SetRange(VcfOptimization.TributaryKey, "min", "0", "storey");
                Assert(!optimization.OkButton.IsEnabled && optimization.ProblemText.Text.Contains("Tributary width B", StringComparison.Ordinal),
                    "A tributary width of zero is refused");
                optimization.SetRange(VcfOptimization.TributaryKey, "min", "12", "storey");
                Assert(optimization.DeflectionResultCombo.SelectedItem as string == FemLoadPlan.DefaultServiceCase && optimization.SpanRatioBox.Text == "250" &&
                       optimization.UpliftLimitBox.Text == "0" &&
                       optimization.FormSettings() is { DeflectionResult: FemLoadPlan.DefaultServiceCase, SpanRatio: 250, UpliftLimitMm: 0 },
                    "The deflection check starts on the SLS load case of the default setup, span / 250 and an uplift limit of 0 mm");
                optimization.SpanRatioBox.Text = "0";
                Assert(!optimization.OkButton.IsEnabled && !optimization.RunButton.IsEnabled, "A span ratio of zero is refused");
                optimization.SpanRatioBox.Text = "250";
                optimization.UpliftLimitBox.Text = "-5";
                Assert(!optimization.OkButton.IsEnabled && optimization.ProblemText.Text.Contains("Uplift limit", StringComparison.Ordinal), "A negative uplift limit is refused");
                optimization.UpliftLimitBox.Text = "25";
                Assert(optimization.OkButton.IsEnabled && optimization.FormSettings().UpliftLimitMm == 25, "An uplift limit of 25 mm is taken");
                optimization.UpliftLimitBox.Text = "0";
                optimization.SetRange("Bays", "max", "1");
                Assert(!optimization.OkButton.IsEnabled && optimization.ProblemText.Text.Length > 0, "An invalid range greys OK and names the problem");
                optimization.Close();
                Assert(defaultVm.VcfOptimizationSettings is null, "Optimization VCF does not change the view model before OK");
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
                Assert(SectionListWindow.ShapeChoices.Contains(FrameShape.FilledTube) && SectionListWindow.ShapeChoices.Contains(FrameShape.Encased),
                    "Add New Property offers Concrete Filled Tube (CFT) and Steel Encased Composite");
                defaultVm.EnsureMaterials();
                var concreteName = defaultVm.Materials.Where(item => item.Kind == MaterialKind.Concrete).MaxBy(item => item.Fc ?? 0)?.Name ?? "";
                var steelName = defaultVm.Materials.FirstOrDefault(item => item.Kind == MaterialKind.Steel)?.Name ?? "";
                var cftForm = new CompositeSectionWindow(defaultVm, FrameSectionProperty.Default(FrameShape.FilledTube, "CFT test", concreteName) with { SteelMaterial = steelName },
                    new HashSet<string>()) { Owner = window };
                cftForm.Show();
                cftForm.UpdateLayout();
                Assert(cftForm.Title == "Concrete Filled Tube (CFT) Section" &&
                       FindVisuals<GroupBox>(cftForm).Select(box => box.Header as string).Where(header => header is { Length: > 0 })
                           .SequenceEqual(["Steel Tube", "Materials", "Design Data (EN 1994-1-1 §6.7.3.3)", "Section Limits", "Property Modifiers", "Section", "Composite Properties", "Material Quantity and Cost (per m)", "N–M Interaction (Design algorithm)"]) &&
                       cftForm.Analysis is { Cost.CostPerM: > 0 },
                    "the CFT form shows the tube, materials, design data, section limits, drawing, composite properties and cost per metre");
                void SaveForm(Window form, string file)
                {
                    var formBitmap = new RenderTargetBitmap((int)form.ActualWidth, (int)form.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    formBitmap.Render(form);
                    var formEncoder = new PngBitmapEncoder(); formEncoder.Frames.Add(BitmapFrame.Create(formBitmap));
                    using var formStream = File.Create(Path.Combine(".work", file)); formEncoder.Save(formStream);
                }
                SaveForm(cftForm, "cft-section.png");
                Assert(cftForm.Interaction is { Series.Count: 2, NplRdKN: > 0, MplYRdKNm: > 0 } cftNm && FindVisuals<COT_VCF.Presentation.Controls.InteractionChart>(cftForm).Single().Diagram == cftNm &&
                       Math.Abs(cftNm.NplRdKN - COT_VCF.Application.Design.ColumnDesignEngine.Interaction(defaultVm.DesignBasis(ColumnDesignType.ConcreteFilledTube) with { Section = cftForm.Read(out _)!.Composite! }).NplRdKN) < 1e-6,
                    "the CFT form draws the N–M interaction (y-y and z-z) from the Design menu's algorithm");
                cftForm.SetField("TubeThickness", "40");
                var thick = cftForm.Analysis;
                cftForm.SetTubeShape(TubeShape.Circular);
                Assert(thick is { Parts.SteelArea: > 0.12 } && cftForm.Read(out _) is { Composite.Tube: TubeShape.Circular, Shape: FrameShape.FilledTube } &&
                       cftForm.Analysis!.Parts.SteelArea < thick.Parts.SteelArea, "the CFT form recalculates as the wall and the tube shape change");
                cftForm.SetField("TubeThickness", "500");
                Assert(cftForm.Read(out _) is null, "the CFT form refuses a wall that does not fit the tube");
                cftForm.Close();
                var srcForm = new CompositeSectionWindow(defaultVm, FrameSectionProperty.Default(FrameShape.Encased, "SRC test", concreteName) with
                    { SteelMaterial = steelName, RebarMaterial = defaultVm.Materials.FirstOrDefault(item => item.Kind == MaterialKind.Rebar)?.Name ?? "" }, new HashSet<string>()) { Owner = window };
                srcForm.Show();
                srcForm.UpdateLayout();
                Assert(FindVisuals<GroupBox>(srcForm).Select(box => box.Header as string).Where(header => header is { Length: > 0 }).Take(3).SequenceEqual(["Concrete Section", "Encased Steel I Section (strong axis in-plane)", "Reinforcement"]) &&
                       srcForm.Analysis is { Parts.Bars.Count: 16 } && srcForm.Read(out _) is { Composite.Fck: > 0, Given.Area: > 1 },
                    "the steel encased form takes the concrete outline, the I section and the bars, with strengths from the materials");
                SaveForm(srcForm, "encased-section.png");
                srcForm.Close();
                Assert(SectionListWindow.ShapeChoices.Contains(FrameShape.MegaComposite) && FrameSectionProperty.Title(FrameShape.MegaComposite) == "Mega Composite Column",
                    "Add New Property offers Mega Composite Column");
                var megaForm = new CompositeSectionWindow(defaultVm, FrameSectionProperty.Default(FrameShape.MegaComposite, "MEGA test", concreteName) with
                    { SteelMaterial = steelName }, new HashSet<string>()) { Owner = window };
                megaForm.Show();
                megaForm.UpdateLayout();
                Assert(megaForm.Title == "Mega Composite Column Section" &&
                       FindVisuals<GroupBox>(megaForm).Select(box => box.Header as string).Where(header => header is { Length: > 0 }).Take(3)
                           .SequenceEqual(["Concrete Section", "Encased Steel Profiles (each; strong axis in-plane)", "Reinforcement"]) &&
                       megaForm.Analysis is { Parts.Bars.Count: 64, Cost.CostPerM: > 0 } mega && Math.Abs(mega.Parts.SteelArea - 4 * 65_660e-6) < 1e-9 &&
                       FindVisuals<TextBlock>(megaForm).Any(text => text.Text.StartsWith("Mpl,y,Rd", StringComparison.Ordinal)),
                    "the mega column form takes the outline, 4 profiles at ±dsx / ±dsy and the bars, with the CTBUH Mpl and the cost per metre");
                SaveForm(megaForm, "mega-section.png");
                Assert(megaForm.Interaction is { Series.Count: 3 } megaNm && megaNm.Series[0].Name.Contains("CTBUH", StringComparison.Ordinal) && megaNm.Series[1].Dashed &&
                       megaNm.Series[0].Points.Count == 5 && megaNm.Series[0].Points[0].NKN < 0,
                    "the mega column form draws the CTBUH A–C–D–B polygon with the exact plastic curve dashed, and z-z");
                megaForm.SetField("ProfileOffsetY", "1000");
                Assert(megaForm.Read(out _) is null, "the mega column form refuses profiles outside the reinforcement cage");
                megaForm.Close();
                Assert(TopMenu("Design").Items.OfType<MenuItem>().Select(item => System.Windows.Automation.AutomationProperties.GetName(item)).Contains("Mega Composite Column Design"),
                    "Design offers Mega Composite Column Design…");
                var megaDesign = new DesignReferenceWindow(defaultVm.DesignBasis(ColumnDesignType.MegaComposite), defaultVm.DesignMemberOptions(ColumnDesignType.MegaComposite),
                    defaultVm.DesignCaseNames(), defaultVm.DesignDefaultCases(), defaultVm.DesignRunId, member => defaultVm.DesignSectionDefaults(ColumnDesignType.MegaComposite, member)) { Owner = window };
                megaDesign.Show();
                megaDesign.Tabs.SelectedIndex = 1;
                megaDesign.UpdateLayout();
                Assert(megaDesign.Title == "Design Reference — Mega Composite Column Design" &&
                       new[] { "Number of profiles (4 or 6)", "dsy  offset in-plane", "dsx  offset across", "Bar layers per b face" }
                           .All(label => FindVisuals<TextBox>(megaDesign).Any(box => System.Windows.Automation.AutomationProperties.GetName(box) == label)),
                    "the Mega Composite Column Design dialog asks for the profile count, offsets and bar layers");
                megaDesign.Close();
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
                           .SequenceEqual(["General Data", "Weight and Mass", "Isotropic Property Data", "Other Properties For Concrete Materials", "Cost"]) &&
                       propertyForm.ReadForm() is { Fc: 30_000, ElasticModulus: 33_000_000 },
                    "Material Property Data shows General Data, Weight and Mass, Isotropic Property Data and the concrete properties");
                propertyForm.SetField("E", "-5");
                Assert(propertyForm.ReadForm() is null, "Material Property Data refuses a modulus of elasticity that is not positive");
                propertyForm.Close();
                var tendonForm = new MaterialPropertyWindow(MaterialProperty.Default(MaterialKind.Tendon, "Strand"), isNew: false, advanced: true, new HashSet<string>()) { Owner = window };
                tendonForm.Show();
                tendonForm.UpdateLayout();
                Assert(FindVisuals<GroupBox>(tendonForm).Select(box => box.Header as string)
                           .SequenceEqual(["General Data", "Weight and Mass", "Uniaxial Property Data", "Other Properties For Tendon Materials", "Cost", "Advanced"]),
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
                Assert(window.NewPatternName.IsVisible && window.NewPatternType.IsVisible && window.NewPatternType.Text == "Dead" &&
                       new[] { "Add load pattern", "Remove load pattern" }.All(name => FindVisuals<Button>(window).Any(button => button.IsVisible &&
                           System.Windows.Automation.AutomationProperties.GetName(button) == name)) &&
                       window.NewPatternName.IsEnabled == vm.CanDefineLoadPatterns,
                    "Define -> Load Patterns defines the patterns of the model: a name, a type, + Add and - Remove (the model's own, not while SAP2000 is connected)");
                OpenMenu("Define", "Load Cases");
                Assert(vm.PaneTitle == "LOAD CASES / LOAD COMBINATIONS" &&
                       FindVisuals<DataGrid>(window).Any(grid => grid.IsVisible && ReferenceEquals(grid.ItemsSource, vm.FemCaseRows)) &&
                       FindVisuals<DataGrid>(window).Any(grid => grid.IsVisible && ReferenceEquals(grid.ItemsSource, vm.FemCombinationRows)),
                    "Define -> Load Cases / Load Combinations opens the case and combination tables");
                Assert(new[] { "Default Setup", "SAP Cases", "+ Add", "- Remove" }.All(label => FindVisuals<Button>(window).Any(button => button.IsVisible && Equals(button.Content, label))) &&
                       FindVisuals<Button>(window).Single(button => Equals(button.Content, "SAP Cases")).IsEnabled == vm.HasSapLoadCases &&
                       FindVisuals<ComboBox>(window).All(combo => !combo.IsVisible || !ReferenceEquals(combo.ItemsSource, vm.FemInitialCaseChoices) || combo.DataContext is FemCaseRow),
                    "Define -> Load Cases defines the cases in the app: Default Setup, SAP Cases (when the model holds SAP2000's), + Add and - Remove");
                OpenSubMenu("Assign", "Joint", "Restraints");
                Assert(vm.PaneMenu == "Assign" && vm.FemPane == MainViewModel.FemPaneRestraints &&
                       FindVisuals<TextBlock>(window).Any(text => text.IsVisible && text.Text == vm.FemPinSupportSummary),
                    "Assign -> Joint Restraints shows the auto-detected pin supports");
                OpenMenu("Run", "Analysis Options");
                Assert(vm.PaneMenu == "Run" && vm.SelectedFemPage == MainViewModel.FemAnalysisPage &&
                       FindVisuals<Button>(window).Any(button => button.IsVisible && Equals(button.Content, "Run Analysis")) &&
                       FindVisuals<TextBlock>(window).Any(text => text.IsVisible && text.Text == vm.FemRunSummary) &&
                       window.SharedViewerPane.Visibility == Visibility.Visible,
                    "Run -> Analysis Options holds the internal 2D frame analysis settings, the run controls and what the one run solves");
                var sagBox = FindVisuals<CheckBox>(window).Single(box => Equals(box.Content, "Cable sag"));
                Assert(vm.FemCableSag && sagBox.IsChecked == true, "Tab 07 Analysis analyses cable sag by default");
                sagBox.IsChecked = false;
                Assert(!vm.FemCableSag, "…and the Cable sag switch turns it off for a comparison with straight cables");
                sagBox.IsChecked = true;
                var demoFemRun = new FemStudyRun(FemStudyService.ModelRunId, "", "", "",
                    new FemAnalysisResult(
                        [new("bot", 0, 0, 0), new("mid", 0.001, -0.02, 0.001), new("top", 0.002, -0.04, 0.002),
                         new("bot2", 0, 0, 0), new("mid2", 0.001, -0.02, 0.001)],
                        [new("bot", 0, 100), new("bot2", 0, 100)],
                        [new("Column1", 100, 100, -10, 10, 0, 0), new("Column2", 100, 100, -10, 10, 0, 0)],
                        [new("Cable-A", 100, true)], 4));
                vm.FemRuns.Add(demoFemRun);
                vm.Notify(nameof(MainViewModel.HasFemResults));
                Assert(TopMenu("Result").Items.OfType<MenuItem>().Select(item => (item.Header as string)?.Replace("_", ""))
                           .SequenceEqual(["Show Deformed Shape…", "Show Member Forces…", "Show Joint Reactions…", "Axial forces / cables", "Bending moments", "Shear forces", "Support reactions", "Deflection", "Cable target fit", "Show Results…", "Results Table…"]) &&
                       TopMenu("Result").Items.OfType<MenuItem>().All(item => item.IsEnabled),
                    "The Result menu lists the six outputs and the results table, enabled once the run has results");
                // Run Analysis is one run of the model: nowhere in the FEM results is there a candidate to choose
                bool HasCandidatePicker(DependencyObject root) => FindVisuals<ComboBox>(root).Any(combo => ReferenceEquals(combo.ItemsSource, vm.FemRuns));
                OpenMenu("Define", "Result Load Case");
                Assert(vm.PaneTitle == "RESULT LOAD CASE" && !HasCandidatePicker(window) &&
                       FindVisuals<ComboBox>(window).Any(combo => combo.IsVisible && System.Windows.Automation.AutomationProperties.GetName(combo) == "FEM load case or combination"),
                    "Define -> Result Load Case holds the load-case dropdown and no candidate dropdown");
                OpenMenu("Result", "Deflection");
                Assert(vm.FemResultMode == FemResultMode.DeflectedShape && ReferenceEquals(vm.SelectedFemRun, demoFemRun) && vm.PaneMenu == "Result" &&
                       vm.PaneTitle == "DEFLECTION" && !FindVisuals<DataGrid>(window).Any(grid => grid.Name == "FemResultRowsGrid") &&
                       window.SharedViewerPane.Visibility == Visibility.Visible && ReferenceEquals(vm.SharedViewerFemResult, demoFemRun.Result) &&
                       !vm.FemResultDiagramTitle.Contains(demoFemRun.Id, StringComparison.Ordinal),
                    "Result -> Deflection draws the diagram of the run without embedding a result table");
                Tool("Show Results").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var showResults = window.OwnedWindows.OfType<ShowResultsWindow>().Single();
                showResults.UpdateLayout();
                Assert(showResults.LoadCasePicker.IsVisible && !HasCandidatePicker(showResults) &&
                       showResults.Outputs.Items.OfType<ListBoxItem>().Select(item => (string)item.Content)
                           .SequenceEqual(["Axial forces / cables", "Bending moments", "Shear forces", "Support reactions", "Deflection", "Cable target fit"]),
                    "The Show Results tool opens a window with the load case / combo at the top and the six outputs below");
                showResults.Outputs.SelectedIndex = 1;
                showResults.ApplySelection();
                Assert(vm.FemResultMode == FemResultMode.BendingMoment && vm.PaneTitle == "BENDING MOMENTS" && ReferenceEquals(vm.SharedViewerFemResult, demoFemRun.Result),
                    "Show Results → Apply draws the chosen output on the model view");
                // SAP2000-style selection: Select is the default tool; a click selects a member / joint, a window selects many; Pan moves
                var canvas = window.SharedViewer.Canvas;
                canvas.UpdateLayout();
                Assert(!window.SharedViewer.PanMode && FindVisuals<System.Windows.Controls.Primitives.ToggleButton>(window).Any(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Select" && t.IsChecked == true),
                    "the model view starts in Select mode (no click-drag panning)");
                var (memberName, line) = canvas.MemberScreenLines.First(item => item.Value.Kind == COT_VCF.Domain.MemberKind.Frame && (item.Value.End - item.Value.Start).Length > 40);
                var middle = line.Start + (line.End - line.Start) / 2;
                var nearJoint = canvas.JointScreenPoints.Values.Any(point => (point - middle).Length <= 6);
                Assert(nearJoint || canvas.SelectAt(middle) && canvas.SelectedMembers.SequenceEqual([memberName]), $"a click on frame {memberName} selects it [{string.Join(",", canvas.SelectedMembers)}|{string.Join(",", canvas.SelectedJoints)}]");
                canvas.ClearSelection();
                canvas.SelectWindow(new Point(0, 0), new Point(canvas.ActualWidth, canvas.ActualHeight));
                Assert(canvas.SelectedJoints.Count == canvas.JointScreenPoints.Count && canvas.SelectedMembers.Count == canvas.MemberScreenLines.Count,
                    "a window around the whole model selects every joint and member");
                FindVisuals<System.Windows.Controls.Primitives.ToggleButton>(window).Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Pan").IsChecked = true;
                Assert(window.SharedViewer.PanMode && canvas.PanMode && canvas.Cursor == System.Windows.Input.Cursors.SizeAll, "the Pan tool switches the model view to panning");
                window.SharedViewer.PanMode = false;
                // Assign → Frame → Frame Sections on the selection (as SAP2000)
                canvas.ClearSelection();
                defaultVm.EnsureSections();
                var target = defaultVm.FemSectionProperties.First(p => !p.IsCable);
                var assignTo = defaultVm.FrameSections.Select(section => section.Name).First(name => name != target.SectionName);
                var assign = new AssignSectionWindow(defaultVm, [target.MemberName], cable: false) { Owner = window };
                assign.Show();
                assign.Choose(assignTo);
                var assignedSection = defaultVm.FrameSections.Single(section => section.Name == assignTo);
                Assert(assign.Apply() && assign.Assigned == 1 && defaultVm.FemSectionProperties.Single(p => p.MemberName == target.MemberName) is { } after &&
                       after.SectionName == assignTo && Math.Abs(after.GrossAreaM2 - assignedSection.Values().Area) < 1e-9 &&
                       defaultVm.FemSectionProperties.Where(p => p.MemberName != target.MemberName).All(p => p.SectionName != null),
                    $"Assign → Frame → Frame Sections gives the selected frame {target.MemberName} the section {assignTo} (its area reaches the FEM)");
                assign.Close();
                Assert(TopMenu("Assign").Items.OfType<MenuItem>().Select(item => (item.Header as string)?.Replace("_", "")).SequenceEqual(["Joint", "Frame", "Cable"]),
                    "Assign is organised as SAP2000: Joint, Frame, Cable");
                canvas.ClearSelection();
                Assert(canvas.SelectedMembers.Count == 0 && canvas.SelectedJoints.Count == 0 && !window.SharedViewer.PanMode, "Clear Selection empties the selection; the Select tool is on");
                Tool("Show Undeformed Shape").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(vm.ShowUndeformed && vm.SharedViewerFemResult is null && vm.FemRuns.Contains(demoFemRun) && demoFemRun.Result is not null,
                    "Show Undeformed Shape (frame icon) clears the result display and keeps the results");
                showResults.ApplySelection();
                Assert(!vm.ShowUndeformed && ReferenceEquals(vm.SharedViewerFemResult, demoFemRun.Result), "Show Results draws the result again after the undeformed shape");
                showResults.Outputs.SelectedIndex = 4;
                showResults.ApplySelection();
                showResults.Close();
                FindVisuals<Button>(window).Single(button => button.IsVisible && Equals(button.Content, "View Results")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var tableWindow = window.OwnedWindows.OfType<FemResultsWindow>().Single();
                tableWindow.UpdateLayout();
                Assert(FindVisuals<DataGrid>(tableWindow).Any(grid => grid.Name == "FemResultRowsGrid" && grid.Items.Count > 0) && !HasCandidatePicker(tableWindow) &&
                       FindVisuals<ComboBox>(tableWindow).Any(combo => System.Windows.Automation.AutomationProperties.GetName(combo) == "FEM load case or combination"),
                    "View Results opens a separate populated table with the load-case dropdown above it");
                tableWindow.Close();
                Assert(!FindVisuals<RadioButton>(window).Any(button => button.GroupName == "ViewerResultMode"),
                    "The model view has no floating result buttons — the result type is chosen in Show Results / the Result menu");
                vm.ShowFemResult(FemResultMode.Shear);
                Assert(vm.FemResultMode == FemResultMode.Shear && vm.PaneTitle == "SHEAR FORCES",
                    "Choosing a result switches the diagram and keeps the Result pane header synchronized");
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
                       mbItems.ToList().IndexOf("06A  Optimization") == mbItems.ToList().IndexOf("05  Iteration") + 1,
                    "Tab 06A Optimization follows Tab 05 under MB and stays disabled until the study has raw data");
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
            if (args.Contains("--column-design"))
            {
                ColumnDesignChecks.Run();
                MegaColumnChecks.Run();
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
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Mcotis;component/Presentation/Theme.xaml", UriKind.Relative) });
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
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Mcotis;component/Presentation/Theme.xaml", UriKind.Relative) });
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
            SpatialFrameRegressionChecks.Run();
            ViewerResultDisplayChecks.Run();
            StudyExecutionChecks.Run();
            OptimizationChecks.Run();
            CableForceReportChecks.Run();
            ReportPrintChecks.Run();
            FemRunReportChecks.Run();
            MemberForceReportChecks.Run();
            SupportReactionReportChecks.Run();
            GatewayChecks.Run();
            QuantityTakeoffChecks.Run();
            MegaColumnChecks.Run();
            FemDatabaseChecks.Run();
            FemModelFileChecks.Run();
            SapImportChecks.Run();
            EtabsImportChecks.Run();
            EtabsExportChecks.Run();
            DevelopmentLogChecks.Run();
            ColumnDesignChecks.Run();
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
        state.OutputSettings.DeflectionPoints.Add("top");
        state.OutputSettings.DeflectionOutputCaseName = "SLS";
        state.Study.UseCandidateFilters = true;
        state.Study.MinHeightM = 20;
        state.Study.SetRunSelected("L01_H01_P01", false);
        config.SetActiveProject("old-config.json", new CotVcfConfigDto());
        config.Session.MarkDirty(ConfigTabs.All);
        Assert(vm.NewConfig.CanExecute(null), "New Config is available without requiring a SAP2000 connection");
        vm.NewConfig.Execute(null);
        Assert(!config.Session.HasActiveProject && !config.Session.IsDirty(ConfigTabs.All) &&
               state.AppliedPoints.Count == 0 && state.Cables.Count == 0 && !state.BaselineCaptured &&
               state.Truss.TopPoints.Count == 0 && !state.OutputSettings.IsConfigured &&
               !state.Study.UseCandidateFilters && state.Study.MinHeightM is null && state.Study.ExcludedIterationKeys.Count == 0,
            "New Config clears tab configuration, project file identity, filters and dirty flags");
        Assert(state.LoadDefinitions.Select(row => row.LoadName).SequenceEqual(new[] { "DL_Above", "LL_Above" }) &&
               vm.IsOfflineModel && !state.IsSapConnected && state.Model?.Name == "Unsaved model" &&
               state.Geometry.Points.Count == 0 && state.Geometry.Members.Count == 0 &&
               state.SessionBaseline.Points.Count == 0 && state.SessionBaseline.Members.Count == 0 &&
               vm.CanDefineFemModel && !vm.CanEditVcfGeometry && sap.Writes == 0,
            "New Config restores loading defaults and opens a blank Define-first model without SAP writes");
        Assert(vm.NewConfig.CanExecute(null) && vm.Connect.CanExecute(null) && vm.Refresh.CanExecute(null),
            "The blank model can be restarted or connected to SAP2000");
    }

    private static void CheckBlankTemplateUi(Window? owner = null)
    {
        var blankRoot = Path.Combine(Path.GetTempPath(), "cotvcf-blank-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var blankState = new ApplicationState();
            var blankSap = new FakeSap { Snapshot = new(new ModelInfo("", "MCOTiS", 6, "kN", "m", false), GeometryModel.Empty, []) };
            var blankWorkflow = new WorkflowService(blankSap, blankState);
            var blankVm = new MainViewModel(blankState, blankWorkflow, new NoSelection(), new ConfigService(blankSap, blankState, blankWorkflow),
                femSessionStore: new COT_VCF.Infrastructure.FemWorkingSessionStore(blankRoot));
            blankVm.LoadDefaultFemModel();
            var blankWindow = new COT_VCF.Presentation.MainWindow { DataContext = blankVm, Width = 1100, Height = 700 };
            if (owner is not null) blankWindow.Owner = owner;
            try
            {
                blankWindow.Show();
                blankWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                blankWindow.UpdateLayout();
                var templateButton = FindVisuals<Button>(blankWindow).Single(button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == "Load default template");
                var continueButton = FindVisuals<Button>(blankWindow).Single(button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == "Continue with blank model");
                Assert(blankVm.IsDefaultModel && blankVm.HasModelGeometry && !templateButton.IsVisible &&
                       !continueButton.IsVisible && blankWindow.WorkflowTabs.IsEnabled,
                    "Opening with the default model shows geometry without a blank-model choice");
                blankVm.InitializeBlankMcotisConfiguration();
                blankWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var fileMenu = (MenuItem)blankWindow.MainMenu.Items[0];
                var defineMenu = blankWindow.MainMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "_Define"));
                Assert(blankWindow.Title == "Mcotis — Meinhardt Cities of Tomorrow Integrated Structures — Untitled" &&
                       fileMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "_Load Config…")) &&
                       defineMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "_Materials…")) &&
                       templateButton.IsVisible && templateButton.IsEnabled && continueButton.IsVisible && continueButton.IsEnabled &&
                       !blankWindow.WorkflowTabs.IsEnabled,
                    "The correctly labelled blank viewport offers both template and blank-model choices");
                var continuePeer = new System.Windows.Automation.Peers.ButtonAutomationPeer(continueButton);
                ((System.Windows.Automation.Provider.IInvokeProvider)continuePeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke))
                    .Invoke();
                blankWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert(!blankVm.HasModelGeometry && !templateButton.IsVisible && !continueButton.IsVisible && blankVm.CanDefineFemModel,
                    "Continue with Blank Model dismisses the chooser without creating geometry");
                blankVm.InitializeBlankMcotisConfiguration();
                blankWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var templatePeer = new System.Windows.Automation.Peers.ButtonAutomationPeer(templateButton);
                ((System.Windows.Automation.Provider.IInvokeProvider)templatePeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke))
                    .Invoke();
                blankWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert(blankVm.IsDefaultModel && blankVm.HasModelGeometry && !templateButton.IsVisible && blankWindow.WorkflowTabs.IsEnabled,
                    "Clicking the blank-view button opens the default template and enables model workflow pages");
            }
            finally { blankWindow.Close(); }
        }
        finally { if (Directory.Exists(blankRoot)) Directory.Delete(blankRoot, recursive: true); }
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
    internal sealed class FakeSap : ISapGateway
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
