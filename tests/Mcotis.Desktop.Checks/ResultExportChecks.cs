using System.IO;
using System.Windows;
using System.Windows.Threading;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Infrastructure;
using Mcotis.Desktop.Presentation;
using System.Windows.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
internal static class ResultExportChecks
{
    internal static void Run()
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
        var report = ResultReport.Create(vm.CurrentFileName, run, cases, Enum.GetValues<ResultReportOutput>(), (result, mode) => ResultFigureCapture.Capture(vm, null, result, mode));
        Check(report.Figures.Count == 10 && report.Figures.Take(5).All(f => f.CaseName == cases[0]) && report.Figures.Skip(5).All(f => f.CaseName == cases[1]), "One figure per selected case and graphical output, in selection order");
        Check(!report.Figures[0].Png.SequenceEqual(report.Figures[1].Png) && !report.Figures[1].Png.SequenceEqual(report.Figures[6].Png), "Changing result type or case changes the captured model diagram");
        Check(vm.SelectedFemResultName == before, "Capturing figures leaves the displayed case unchanged");
        var html = report.Html(); Check(!html.Contains("<table") && html.Contains("Figure 10.") && html.Contains("data:image/png;base64,"), "Report contains numbered graphical figures, without result tables");
        bool refused = false; try { ResultReport.Create("test", run, ["missing"], [ResultReportOutput.Reactions], (_, _) => []); } catch (InvalidOperationException) { refused = true; }
        Check(refused, "Unsolved case cannot silently substitute another result");
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
            Check(main.ImageParts.Count() == 10 && main.Document.Descendants<Drawing>().Count() == 10 && !main.Document.Descendants<Table>().Any() && main.Document.InnerText.Contains("Figure 10."), "Word embeds every screenshot with figure captions and no tables");
        }
        report.Export(Path.Combine(folder, "Graphical Results.pdf"), true);
        Check(File.ReadAllBytes(Path.Combine(folder, "Graphical Results.pdf")).Take(5).SequenceEqual(System.Text.Encoding.ASCII.GetBytes("%PDF-")), "Graphical PDF generated by installed browser");
        var picker = new ShowResultsWindow(vm, _ => {}); picker.Show();
        var export = new ExportResultsWindow(vm) { Owner = picker }; export.Show();
        foreach (var box in export.OutputChoices.Children.OfType<CheckBox>()) box.IsChecked = Equals(box.Tag, ResultReportOutput.BendingMoment);
        export.AllOutputs.IsChecked = false;
        Check(export.BuildReport().Figures.Count == 1 && vm.SelectedFemResultName == before, "Dialog exports an individual graphical result without changing the live selection");
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