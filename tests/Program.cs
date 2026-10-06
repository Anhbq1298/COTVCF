using COT_VCF.Application;
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
            if (args.Contains("--study-ui"))
            {
                var app = new System.Windows.Application();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/COT VCF tools;component/src/Presentation/Theme.xaml", UriKind.Relative) });
                var bindingLog = new StringWriter();
                var bindingListener = new TextWriterTraceListener(bindingLog);
                PresentationTraceSources.DataBindingSource.Listeners.Add(bindingListener);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

                var uiState = StudyComposerChecks.CreateState();
                var uiSap = new FakeSap { Snapshot = new(uiState.Model!, uiState.Geometry, uiState.LoadPatterns.ToArray()) };
                var uiWorkflow = new WorkflowService(uiSap, uiState);
                var uiConfig = new COT_VCF.Configuration.ConfigService(uiSap, uiState, uiWorkflow);
                var vm = new MainViewModel(uiState, uiWorkflow, new NoSelection(), uiConfig) { SelectedTab = 0 };
                var window = new COT_VCF.Presentation.MainWindow { DataContext = vm, Width = 1400, Height = 810 };
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
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
                       FindVisuals<TextBlock>(window).Any(text => text.Text == "06A  OPTIMIZATION"),
                    "Tab 06A Optimization is listed after Tab 06 and stays disabled until the study has raw data");
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
                }
                finally { try { Directory.Delete(optimizationRoot, true); } catch { } }
                window.Close(); bindingListener.Flush();
                if (bindingLog.ToString().Length > 0) throw new Exception("WPF binding errors: " + bindingLog);
                Console.WriteLine("PASS: Tab 5 rendered with no WPF binding errors.");
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
            OutputSettingsChecks.Run();
            ReliabilityChecks.Run();
            StudyComposerChecks.Run();
            StudyExecutionChecks.Run();
            OptimizationChecks.Run();
            CableForceReportChecks.Run();
            ReportPrintChecks.Run();
            MemberForceReportChecks.Run();
            SupportReactionReportChecks.Run();
            GatewayChecks.Run();
            Console.WriteLine("All workflow and SAP write-boundary checks passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
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
