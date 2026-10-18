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
        static byte[]? Render(Mcotis.Desktop.Application.Quantities.QuantitySectionFigure figure) => SectionFigureImage.Render(figure);
        var quantities = ResultReportTables.Quantities(vm.QuantityTakeoff(), Render);
        var report = ResultReport.Create(vm.CurrentFileName, run, cases, Enum.GetValues<ResultReportOutput>(), (result, mode) => ResultFigureCapture.Capture(vm, null, result, mode),
            name => vm.ReportCaseTables(name, null, Render), [quantities]);
        Check(groups.Take(6).Select(g => g.Title).SequenceEqual(new[] { "Mega Column", "Top Chord", "Bottom Chord", "Vertical Chord", "Main Cable (External)", "Internal Cable" })
            && groups.Single(g => g.Title == "Main Cable (External)").Members.Count > 0 && groups.Single(g => g.Title == "Internal Cable").Members.Count > 0,
            "VCF summary uses the requested member rows and distinguishes external and internal cables");
        Check(groups.Single(g => g.Title == "Main Cable (External)").Members.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(new[] { "Cable1a-2", "Cable1b-2" })
            && groups.Single(g => g.Title == "Internal Cable").Members.All(n => n.StartsWith("Cable", StringComparison.Ordinal) && !ResultMemberSummary.IsMainCable(n))
            && groups.All(g => g.Title is not ("Other Cables" or "Middle Chord" or "End Frames")) && !ResultMemberSummary.IsMainCable("Cable10a-2") && ResultMemberSummary.IsMainCable("cable1b"),
            "Main Cable (External) is Cable1a/Cable1b and every other cable is an Internal Cable");
        var roles = VcfGeometry.Analyze(vm.State.Geometry, vm.State.Truss).Roles;
        var verticals = groups.Single(g => g.Title == "Vertical Chord").Members.ToHashSet(StringComparer.Ordinal);
        Check(roles.Any(r => r.Value == VcfMemberRole.MiddleChord)
            && roles.Where(r => r.Value is VcfMemberRole.VerticalChord or VcfMemberRole.MiddleChord).All(r => verticals.Contains(r.Key))
            && roles.Where(r => r.Value == VcfMemberRole.EndFrame).All(r => groups.All(g => !g.Members.Contains(r.Key))),
            "Vertical Chord envelopes the vertical and middle chords, and the end frames are left out of the summary");
        var tableRows = vm.FemMemberSummaryRows;
        Check(tableRows.Take(6).Select(r => r.Member).SequenceEqual(new[] { "Mega Column", "Top Chord", "Bottom Chord", "Vertical Chord", "Main Cable (External)", "Internal Cable" })
            && tableRows[4] is { BendingKNmText: "—", ShearKNText: "—" } && tableRows[5].AxialKNText != "—" && tableRows[0].BendingKNmText != "—"
            && tableRows.Count == 6 && tableRows.All(r => r.Sections.Count > 0 && r.Sections.All(s => s.Description.Length > 0 && s.Description != s.Name))
            && tableRows.Any(r => r.Sections.Any(s => s.Figure is not null))
            && tableRows.Single(r => r.Member == "Vertical Chord").Sections.Count == groups.Single(g => g.Title == "Vertical Chord").Sections.Split("; ").Length,
            "Results table Member force summary lists only the six VCF member rows in kN for the shown load case, with no bending or shear for cables, and describes and draws each row's sections");
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
        var html = report.Html(); Check(report.Tables.Where(t => t.Title.StartsWith("Summary of member forces — ", StringComparison.Ordinal)).Select(t => t.Title[27..]).SequenceEqual(cases) && html.Contains("<table") && html.Contains(System.Net.WebUtility.HtmlEncode("Bending (MN·m)")) && html.Contains("Member size") && html.Contains("Figure 10.") && html.Contains("data:image/png;base64,"), "Report contains the force tables of each selected case alongside numbered graphical figures");
        var summaryTable = report.Tables.First(t => t.Title.StartsWith("Summary of member forces", StringComparison.Ordinal));
        var shown = vm.MemberSummaryRows(run.ResultFor(cases[0]), cases[0]);
        Check(summaryTable.Rows.Select(r => r[0].Text).SequenceEqual(["Mega Column", "Top Chord", "Bottom Chord", "Vertical Chord", "Main Cable (External)", "Internal Cable"]) &&
              summaryTable.Rows[0][1].Text == (shown[0].Forces.BendingMNm!.Value).ToString("#,0.0", System.Globalization.CultureInfo.InvariantCulture) &&
              summaryTable.Rows.All(r => r[5].Image is { Length: > 0 }) && summaryTable.Rows[4][1].Text == "—",
            "the summary table repeats the Results Table rows in MN with one decimal and draws each group's section");
        var tableTitles = report.Tables.Select(t => t.Title).ToArray();
        Check(new[] { "Mega Column", "Top Chord", "Bottom Chord", "Vertical Chord", "Cable" }.All(group => tableTitles.Contains($"{group} forces — {cases[0]}")) &&
              report.Tables.All(t => t.Columns.All(c => c.Header != "UR" && c.Header != "Capacity (MN)")) &&
              report.Tables.Where(t => t.Title.Contains(" forces — ", StringComparison.Ordinal)).All(t => t.Rows.All(r => r.Skip(1).All(c => c.Text == "—" || !c.Text.Contains('.') || c.Text.Split('.')[^1].Length == 1 || !char.IsDigit(c.Text[^1])))),
            "each group and the cables get a table of their members' forces in MN to one decimal, with no UR column unless design utilization is asked for");
        var middle = VcfGeometry.Analyze(vm.State.Geometry, vm.State.Truss).Roles.Where(r => r.Value == VcfMemberRole.MiddleChord).Select(r => r.Key).ToHashSet();
        Check(middle.Count > 0 && middle.All(m => report.Tables.Single(t => t.Title == $"Vertical Chord forces — {cases[0]}").Rows.Any(r => r[0].Text == m)),
            "the Vertical Chord table lists the middle chords too");
        var (sections, memberCases) = vm.DesignUtilization();
        var withUr = vm.ReportCaseTables(cases[0], memberCases, Render);
        Check(sections.Count > 0 && withUr.All(t => t.Columns[^1].Header == "UR") && withUr.Single(t => t.Title.StartsWith("Cable forces", StringComparison.Ordinal)).Columns.Any(c => c.Header == "Capacity (MN)") &&
              withUr.Single(t => t.Title.StartsWith("Cable forces", StringComparison.Ordinal)).Rows.All(r => r[^1].Text != "—"),
            "with design utilization every force table gets a UR column, cables also their capacity (area × 2160 MPa × 50 %)");
        Check(quantities is { Landscape: true, TotalRows: 1 } && quantities.Columns[2].Header == "Section drawing" && quantities.Columns[3].Header == "Description" &&
              quantities.Rows.SkipLast(1).Any(r => r[2].Image is not null) && quantities.Rows[^1][0].Text == "Total",
            "the quantities table draws each section with its description beside it, and closes with the totals");
        Check(html.IndexOf("<table", StringComparison.Ordinal) > html.LastIndexOf("</figure>", StringComparison.Ordinal), "PDF report places every summary table after all figures");
        bool refused = false; try { ResultReport.Create("test", run, ["missing"], [ResultReportOutput.Reactions], (_, _) => []); } catch (InvalidOperationException) { refused = true; }
        Check(refused, "Unsolved case cannot silently substitute another result");
        var merged = ResultReport.Create("test", run, [new ResultReportSelection("SLS", cases, [ResultReportOutput.Deflection]), new ResultReportSelection("ULS", [cases[0]], [ResultReportOutput.Reactions, ResultReportOutput.Deflection])], (result, mode) => ResultFigureCapture.Capture(vm, null, result, mode),
            name => vm.ReportCaseTables(name, null, Render));
        Check(merged.Figures.Select(f => (f.CaseName, f.Title)).SequenceEqual(new[] { (cases[0], "Deflection"), (cases[0], "Support reactions"), (cases[1], "Deflection") }) && merged.Tables.Count(t => t.Title.StartsWith("Summary", StringComparison.Ordinal)) == 2,
            "A case in both SLS and ULS is reported once with the union of their result types");
        for (int i = 0; i < report.Figures.Count; i++) File.WriteAllBytes(Path.Combine(folder, $"Figure {i+1}.png"), report.Figures[i].Png);
        var path = Path.Combine(folder, "Graphical Results.docx"); report.Export(path, false);
        using (var word = WordprocessingDocument.Open(path, false))
        {
            var errors = new OpenXmlValidator().Validate(word).ToArray(); Check(errors.Length == 0, "Word passes Open XML validation: " + string.Join("; ", errors.Select(e => e.Description)));
            var main = word.MainDocumentPart!; var section = main.Document.Body!.Descendants<ParagraphProperties>().Select(p => p.GetFirstChild<SectionProperties>()).OfType<SectionProperties>().Single();
            var page = section.GetFirstChild<PageSize>()!; var margin = section.GetFirstChild<PageMargin>()!;
            Check(page.Width?.Value == 11906 && page.Height?.Value == 16838 && margin.Top?.Value == 1066 && margin.Bottom?.Value == 726 && margin.Left?.Value == 1066 && margin.Right?.Value == 896, "A4 and requested margins preserved");
            var last = main.Document.Body!.Elements<SectionProperties>().Single().GetFirstChild<PageSize>()!;
            Check(last.Width?.Value == 16838 && last.Orient?.Value == PageOrientationValues.Landscape, "the quantities table is on a landscape A4 page at the end");
            var defaults = main.StyleDefinitionsPart!.Styles!.GetFirstChild<DocDefaults>()!;
            Check(defaults.RunPropertiesDefault!.RunPropertiesBaseStyle!.GetFirstChild<RunFonts>()!.Ascii == "Arial" && defaults.RunPropertiesDefault.RunPropertiesBaseStyle.GetFirstChild<FontSize>()!.Val == "22" && defaults.ParagraphPropertiesDefault!.ParagraphPropertiesBaseStyle!.GetFirstChild<SpacingBetweenLines>()!.Line == "276", "Arial 11 and 1.15 spacing preserved");
            Check(main.ImageParts.Count() > 10 && main.Document.Descendants<Table>().Count() == report.Tables.Count && main.Document.InnerText.Contains("Figure 10.") &&
                  main.Document.Descendants<Table>().First().Descendants<Drawing>().Any(), "Word embeds every screenshot and caption, and every table with its section drawings");
            var blocks = main.Document.Body!.ChildElements.ToArray();
            Check(Array.FindIndex(blocks, b => b is Table) > Array.FindLastIndex(blocks, b => b is Paragraph && (b.Descendants<Drawing>().Any() || b.InnerText.StartsWith("Figure ", StringComparison.Ordinal))), "Word places every table after all figures and captions");
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
        export.IncludeForces.IsChecked = false;
        refused = false; try { export.BuildReport(); } catch (InvalidOperationException ex) { refused = ex.Message.Contains("SLS"); }
        Check(refused, "without Member forces, SLS cases without a result type are refused with the part named");
        export.IncludeForces.IsChecked = true;
        foreach (var box in Boxes(export.SlsCaseChoices)) box.IsChecked = false;
        Check(export.BuildReport() is { Figures.Count: 1 } selected && selected.Tables.Count(t => t.Title == $"Summary of member forces — {before}") == 1 &&
              selected.Tables.Any(t => t.Title == "Quantities") && !selected.Tables.Any(t => t.Title == "Design utilization") && vm.SelectedFemResultName == before,
            "Dialog exports the selected figure, that case's force tables and the quantities without changing the live selection");
        foreach (var box in Boxes(export.UlsOutputChoices)) box.IsChecked = false;
        Check(export.BuildReport() is { Figures.Count: 0, Tables.Count: > 1 }, "with Member forces ticked a case may be exported as tables only");
        var fromQuantities = new ExportResultsWindow(vm, ExportFocus.Quantities) { Owner = picker };
        var fromTable = new ExportResultsWindow(vm, ExportFocus.ResultsTable) { Owner = picker };
        Check(fromQuantities.IncludeUtilization.IsChecked == true && fromQuantities.IncludeQuantities.IsChecked == true && fromTable.IncludeUtilization.IsChecked != true &&
              Boxes(fromTable.UlsCaseChoices).Any(b => b.IsChecked == true && Equals(b.Tag, vm.SelectedFemResultName)),
            "Export from Show Quantities ticks the quantities and design utilization; Export from the Results Table ticks the case it shows");
        fromQuantities.Close(); fromTable.Close();
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
