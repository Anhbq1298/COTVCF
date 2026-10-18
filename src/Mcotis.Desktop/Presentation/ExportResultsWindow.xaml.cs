using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Quantities;

namespace Mcotis.Desktop.Presentation;

/// <summary>Where Export was chosen: it ticks that window's own content first.</summary>
public enum ExportFocus { Results, ResultsTable, Quantities }

public partial class ExportResultsWindow : Window
{
    private readonly MainViewModel viewModel;
    private bool exporting;
    internal Func<SaveFileDialog, bool?> ChooseFile { get; set; }
    internal Action<string> OpenFile { get; set; } = path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    public ExportResultsWindow(MainViewModel viewModel, ExportFocus focus = ExportFocus.Results)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        ChooseFile = dialog => dialog.ShowDialog(this);
        // No case carries a limit state, so both parts list every solved case; the names only choose the initial ticks.
        bool Named(string name, string tag) => name.Contains(tag, StringComparison.OrdinalIgnoreCase);
        var names = viewModel.FemResultNames;
        bool anyUls = names.Any(n => Named(n, "ULS"));
        Wire(SlsAllCases, SlsCaseChoices, names.Select(n => (n, (object)n, Named(n, "SLS"))), SlsAllOutputs, SlsOutputChoices, _ => true);
        Wire(UlsAllCases, UlsCaseChoices, names.Select(n => (n, (object)n, anyUls ? Named(n, "ULS") : n == viewModel.SelectedFemResultName)), UlsAllOutputs, UlsOutputChoices,
            output => output != ResultReportOutput.Deflection);
        Closing += (_, e) => { if (exporting) e.Cancel = true; };
        if (focus == ExportFocus.ResultsTable && viewModel.SelectedFemResultName is { } shown)
            foreach (var box in UlsCaseChoices.Children.OfType<CheckBox>().Where(box => Equals(box.Tag, shown))) box.IsChecked = true;
        if (focus == ExportFocus.Quantities) IncludeUtilization.IsChecked = true;
    }
    private static readonly (ResultReportOutput Output, string Name)[] Outputs = [(ResultReportOutput.AxialForce, "Axial forces / cables"), (ResultReportOutput.BendingMoment, "Bending moments"), (ResultReportOutput.Shear, "Shear forces"), (ResultReportOutput.Reactions, "Support reactions"), (ResultReportOutput.Deflection, "Deflection")];
    private static void Wire(CheckBox allCases, Panel cases, IEnumerable<(string Name, object Tag, bool Ticked)> caseItems, CheckBox allOutputs, Panel outputs, Func<ResultReportOutput, bool> ticked)
    {
        Fill(allCases, cases, caseItems, new Thickness(0, 2, 0, 2));
        Fill(allOutputs, outputs, Outputs.Select(o => (o.Name, (object)o.Output, ticked(o.Output))), new Thickness(0, 3, 0, 3));
    }
    private static void Fill(CheckBox all, Panel panel, IEnumerable<(string Name, object Tag, bool Ticked)> items, Thickness margin)
    {
        void Sync() => all.IsChecked = panel.Children.Count > 0 && panel.Children.OfType<CheckBox>().All(c => c.IsChecked == true);
        foreach (var (name, tag, ticked) in items)
        {
            var box = new CheckBox { Content = name, Tag = tag, IsChecked = ticked, Margin = margin };
            box.Click += (_, _) => Sync();
            panel.Children.Add(box);
        }
        all.Click += (_, _) => { foreach (var box in panel.Children.OfType<CheckBox>()) box.IsChecked = all.IsChecked == true; };
        Sync();
    }
    private static T[] Ticked<T>(Panel panel) => panel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (T)c.Tag).ToArray();
    internal ResultReport BuildReport()
    {
        if (!viewModel.IsIdle || !viewModel.HasFemResults || viewModel.SelectedFemRun is not { } run)
            throw new InvalidOperationException("Run analysis and wait for it to finish before exporting results.");
        var main = Owner as MainWindow ?? Owner?.Owner as MainWindow;
        var images = new Dictionary<QuantitySectionFigure, byte[]?>(ReferenceEqualityComparer.Instance);
        byte[]? Image(QuantitySectionFigure figure) => images.TryGetValue(figure, out var png) ? png : images[figure] = SectionFigureImage.Render(figure);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>? memberCases = null;
        var modelTables = new List<ReportTable>();
        if (IncludeQuantities.IsChecked == true) modelTables.Add(ResultReportTables.Quantities(viewModel.QuantityTakeoff(), Image));
        if (IncludeUtilization.IsChecked == true)
        {
            if (viewModel.FemResultState != Mcotis.Application.ResultState.Current)
                throw new InvalidOperationException("The FEM results are not those of the current model. Run analysis before exporting the design utilization.");
            var (sections, members) = viewModel.DesignUtilization();
            memberCases = members;
            modelTables.Add(ResultReportTables.Utilization(sections));
        }
        return ResultReport.Create(viewModel.CurrentFileName, run,
            [new ResultReportSelection("SLS", Ticked<string>(SlsCaseChoices), Ticked<ResultReportOutput>(SlsOutputChoices)),
             new ResultReportSelection("ULS", Ticked<string>(UlsCaseChoices), Ticked<ResultReportOutput>(UlsOutputChoices))],
            (result, mode) => ResultFigureCapture.Capture(viewModel, main?.SharedViewer.Canvas, result, mode),
            IncludeForces.IsChecked == true ? name => viewModel.ReportCaseTables(name, memberCases, Image) : null, modelTables);
    }

    /// <summary>Design utilization needs the forces of the current model: offers to run the analysis when they are not.</summary>
    private async Task<bool> EnsureCurrentResultsAsync()
    {
        if (IncludeUtilization.IsChecked != true || viewModel.HasFemResults && viewModel.FemResultState == Mcotis.Application.ResultState.Current) return true;
        if (MessageBox.Show(this, "The FEM results are not those of the current model, so the design utilization would use out-of-date forces.\n\nRun FEM Analysis now and then export?",
                "Export Results", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        Status.Text = "Running FEM Analysis…";
        Options.IsEnabled = OkButton.IsEnabled = false;
        try { await viewModel.AnalyzeFemAsync(); }
        finally { Options.IsEnabled = OkButton.IsEnabled = true; }
        if (viewModel.HasFemResults && viewModel.FemResultState == Mcotis.Application.ResultState.Current) { Status.Text = ""; return true; }
        Status.Text = "FEM Analysis did not produce current results; the report was not exported.";
        return false;
    }
    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (exporting) return;
        try
        {
            if (!await EnsureCurrentResultsAsync()) return;
            var report = BuildReport(); // Freeze the selections and existing solved values before awaiting.
            bool pdf = PdfFormat.IsChecked == true;
            var name = Path.GetFileNameWithoutExtension(viewModel.CurrentFileName);
            foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            var dialog = new SaveFileDialog { Title = "Save Results", FileName = name + " - Results", DefaultExt = pdf ? ".pdf" : ".docx", Filter = pdf ? "PDF document (*.pdf)|*.pdf" : "Word document (*.docx)|*.docx", AddExtension = true, OverwritePrompt = true };
            if (ChooseFile(dialog) != true) return;
            exporting = true; Options.IsEnabled = OkButton.IsEnabled = CancelButton.IsEnabled = false;
            Status.Text = "Exporting results…";
            await Task.Run(() => report.Export(dialog.FileName, pdf));
            Status.Text = "Saved: " + dialog.FileName;
            try { OpenFile(dialog.FileName); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            { Status.Text = $"Saved: {dialog.FileName}\nThe document could not be opened: {ex.Message}"; return; }
            exporting = false;
            DialogResult = true;
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { exporting = false; Options.IsEnabled = OkButton.IsEnabled = CancelButton.IsEnabled = true; }
    }
}
