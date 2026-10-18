using System.IO;
using System.Windows;
using System.Windows.Threading;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Infrastructure;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Presentation;
using System.Windows.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
internal static class ResultExportChecks
{
    internal static void Run(bool includePdf = true)
    {
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Mcotis;component/Presentation/Theme.xaml", UriKind.Relative) });
        var folder = Path.GetFullPath(".work/result-figure-checks"); Directory.CreateDirectory(folder);
        var state = StudyComposerChecks.CreateState();
        var sap = new Program.FakeSap { Snapshot = new(state.Model!, state.Geometry, state.LoadPatterns.ToArray()) };
        var workflow = new WorkflowService(sap, state);
        var vm = new MainViewModel(state, workflow, new NoSelection(), new ConfigService(sap, state, workflow), femSessionStore: new FemWorkingSessionStore(Path.Combine(folder, "session")));
        vm.LoadDefaultFemModel(replaceCurrent: true);
        var task = vm.AnalyzeFemAsync();
        while (!task.IsCompleted) { Dispatcher.CurrentDispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle); Thread.Sleep(10); }
        task.GetAwaiter().GetResult();
        Check(vm.HasFemResults, "Default model has solved results for graphical export");
        var before = vm.SelectedFemResultName;
        var run = vm.SelectedFemRun!;
        var cases = vm.FemResultNames.Take(2).ToArray();
        var groups = ResultMemberSummary.Groups(vm.State.Geometry, vm.State.Truss, vm.FemSectionProperties.ToArray());
        var report = ResultReport.Create(vm.CurrentFileName, run, cases, Enum.GetValues<ResultReportOutput>(), (result, mode) => ResultFigureCapture.Capture(vm, null, result, mode), groups);
        Check(groups.Take(6).Select(g => g.Title).SequenceEqual(new[] { "Mega Column", "Top Chord", "Bottom Chord", "Vertical Chord", "Main Cable (External)", "Internal Cable" })
            && groups.Single(g => g.Title == "Main Cable (External)").Members.Count > 0 && groups.Single(g => g.Title == "Internal Cable").Members.Count > 0,
            "VCF summary uses the requested member rows and distinguishes external and internal cables");
        var sample = new FemAnalysisResult([], [], [new FemFrameResponse("F", -1000, 2000, 3000, -4000, 5000, -6000,
            [new FemFrameStation(0.5, -7000, 8000, -9000, 0, 0)])], [new FemCableResponse("C", 12000, true)], 0);
        ResultMemberGroup[] sampleGroups = [new("Frame", ["F"], "Section A; Section B"), new("Cable", ["C"], "192T15.7"), new("Missing", ["F", "absent"], "Unknown")];
        var summary = ResultMemberSummary.Create("ULS", sample, sampleGroups, Enum.GetValues<ResultReportOutput>());
        Check(summary.Rows[0] is { BendingMNm: 9, ShearMN: 8, AxialMN: 7, MemberSize: "Section A; Section B" }
            && summary.Rows[1] is { BendingMNm: null, ShearMN: null, AxialMN: 12 } && summary.Rows[2].AxialMN is null,
            "Summary includes interior station peaks, converts kN/kNm to MN/MNm and withholds incomplete groups");
        var filtered = ResultMemberSummary.Create("ULS", sample, sampleGroups, [ResultReportOutput.BendingMoment]);
        Check(filtered.Rows[0] is { BendingMNm: 9, ShearMN: null, AxialMN: null }, "Summary force columns follow selected result types");
        Check(report.Figures.Count == 10 && report.Figures.Take(5).All(f => f.CaseName == cases[0]) && report.Figures.Skip(5).All(f => f.CaseName == cases[1]), "One figure per selected case and graphical output, in selection order");
        Check(!report.Figures[0].Png.SequenceEqual(report.Figures[1].Png) && !report.Figures[1].Png.SequenceEqual(report.Figures[6].Png), "Changing result type or case changes the captured model diagram");
        Check(vm.SelectedFemResultName == before, "Capturing figures leaves the displayed case unchanged");
        var html = report.Html(); Check(report.MemberTables.Select(t => t.CaseName).SequenceEqual(cases) && html.Contains("<table") && html.Contains("Bending (MNm)") && html.Contains("Member size") && html.Contains("Figure 10.") && html.Contains("data:image/png;base64,"), "Report contains member tables per selected case alongside numbered graphical figures");
        Check(html.IndexOf("<table", StringComparison.Ordinal) > html.LastIndexOf("</figure>", StringComparison.Ordinal), "PDF report places every summary table after all figures");
        bool refused = false; try { ResultReport.Create("test", run, ["missing"], [ResultReportOutput.Reactions], (_, _) => []); } catch (InvalidOperationException) { refused = true; }
        Check(refused, "Unsolved case cannot silently substitute another result");
        var merged = ResultReport.Create("test", run, [new ResultReportSelection("SLS", cases, [ResultReportOutput.Deflection]), new ResultReportSelection("ULS", [cases[0]], [ResultReportOutput.Reactions, ResultReportOutput.Deflection])], (result, mode) => ResultFigureCapture.Capture(vm, null, result, mode), groups);
        Check(merged.Figures.Select(f => (f.CaseName, f.Title)).SequenceEqual(new[] { (cases[0], "Deflection"), (cases[0], "Support reactions"), (cases[1], "Deflection") }) && merged.MemberTables.Count == 2,
            "A case in both SLS and ULS is reported once with the union of their result types");
        for (int i = 0; i < report.Figures.Count; i++) File.WriteAllBytes(Path.Combine(folder, $"Figure {i+1}.png"), report.Figures[i].Png);
        var path = Path.Combine(folder, "Graphical Results.docx"); report.Export(path, false);
        using (var word = WordprocessingDocument.Open(path, false))
        {
            var errors = new OpenXmlValidator().Validate(word).ToArray(); Check(errors.Length == 0, "Word passes Open XML validation: " + string.Join("; ", errors.Select(e => e.Description)));
            var main = word.MainDocumentPart!; var section = main.Document.Body!.Elements<SectionProperties>().Single();
            var page = section.GetFirstChild<PageSize>()!; var margin = section.GetFirstChild<PageMargin>()!;
            Check(page.Width?.Value == 11906 && page.Height?.Value == 16838 && margin.Top?.Value == 1066 && margin.Bottom?.Value == 726 && margin.Left?.Value == 1066 && margin.Right?.Value == 896, "A4 and requested margins preserved");
            var defaults = main.StyleDefinitionsPart!.Styles!.GetFirstChild<DocDefaults>()!;
            Check(defaults.RunPropertiesDefault!.RunPropertiesBaseStyle!.GetFirstChild<RunFonts>()!.Ascii == "Arial" && defaults.RunPropertiesDefault.RunPropertiesBaseStyle.GetFirstChild<FontSize>()!.Val == "22" && defaults.ParagraphPropertiesDefault!.ParagraphPropertiesBaseStyle!.GetFirstChild<SpacingBetweenLines>()!.Line == "276", "Arial 11 and 1.15 spacing preserved");
            Check(main.ImageParts.Count() == 10 && main.Document.Descendants<Drawing>().Count() == 10 && main.Document.Descendants<Table>().Count() == cases.Length && main.Document.InnerText.Contains("Figure 10."), "Word embeds every screenshot, caption and member summary table");
            var blocks = main.Document.Body!.ChildElements.ToArray();
            Check(Array.FindIndex(blocks, b => b is Table) > Array.FindLastIndex(blocks, b => b.Descendants<Drawing>().Any() || b.InnerText.StartsWith("Figure ", StringComparison.Ordinal)), "Word places every summary table after all figures and captions");
        }
        if (includePdf)
        {
            report.Export(Path.Combine(folder, "Graphical Results.pdf"), true);
            Check(File.ReadAllBytes(Path.Combine(folder, "Graphical Results.pdf")).Take(5).SequenceEqual(System.Text.Encoding.ASCII.GetBytes("%PDF-")), "Graphical PDF generated by installed browser");
        }
        else Console.WriteLine("SKIP: installed-browser PDF export (--skip-pdf)");
        var picker = new ShowResultsWindow(vm, _ => {}); picker.Show();
        var export = new ExportResultsWindow(vm) { Owner = picker }; export.Show();
        static CheckBox[] Boxes(Panel panel) => panel.Children.OfType<CheckBox>().ToArray();
        Check(Boxes(export.SlsOutputChoices).All(b => b.IsChecked == true) && Boxes(export.UlsOutputChoices).All(b => b.IsChecked == Equals(b.Tag, ResultReportOutput.Deflection) is false)
            && Boxes(export.SlsCaseChoices).Where(b => b.IsChecked == true).All(b => ((string)b.Content).Contains("SLS")) && Boxes(export.UlsCaseChoices).Where(b => b.IsChecked == true).All(b => ((string)b.Content).Contains("ULS")),
            "Dialog splits SLS (every result, deflection included) from ULS (deflection left unticked but available)");
        foreach (var box in Boxes(export.SlsCaseChoices)) box.IsChecked = false;
        foreach (var box in Boxes(export.UlsCaseChoices)) box.IsChecked = (string)box.Tag == before;
        foreach (var box in Boxes(export.UlsOutputChoices)) box.IsChecked = Equals(box.Tag, ResultReportOutput.BendingMoment);
        Check(export.BuildReport() is { Figures.Count: 1 }, "An empty SLS part is skipped");
        foreach (var box in Boxes(export.SlsCaseChoices)) box.IsChecked = true;
        foreach (var box in Boxes(export.SlsOutputChoices)) box.IsChecked = false;
        refused = false; try { export.BuildReport(); } catch (InvalidOperationException ex) { refused = ex.Message.Contains("SLS"); }
        Check(refused, "SLS cases without a result type are refused with the part named");
        foreach (var box in Boxes(export.SlsCaseChoices)) box.IsChecked = false;
        Check(export.BuildReport() is { Figures.Count: 1, MemberTables.Count: 1 } selected && selected.MemberTables[0].Rows.All(r => r.AxialMN is null && r.ShearMN is null) && vm.SelectedFemResultName == before, "Dialog exports the selected figure and matching member table without changing the live selection");
        export.ChooseFile = _ => false; export.OkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(export.IsVisible && export.OkButton.IsEnabled, "Cancelling Save leaves the export dialog available"); export.Close();
        export = new ExportResultsWindow(vm) { Owner = picker }; string? opened = null; var saved = Path.Combine(folder, "UI Results.docx");
        export.ChooseFile = dialog => { dialog.FileName = saved; return true; }; export.OpenFile = p => opened = p;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => export.OkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))), DispatcherPriority.ApplicationIdle);
        Check(export.ShowDialog() == true && opened == saved && File.Exists(saved), "OK saves figures into the chosen folder and opens the document");
        picker.Close(); app.Shutdown(); Console.WriteLine("Result figure export checks passed: " + folder);
    }
    private sealed class NoSelection : ISelectionService
    {
        public IReadOnlyList<string>? Select(string title, IReadOnlyList<string> available, IReadOnlyList<string> selected) => null;
    }
}
