using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Mcotis.Desktop.Application;

namespace Mcotis.Desktop.Presentation;

public partial class ExportResultsWindow : Window
{
    private readonly MainViewModel viewModel;
    private bool exporting;
    internal Func<SaveFileDialog, bool?> ChooseFile { get; set; }
    internal Action<string> OpenFile { get; set; } = path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    public ExportResultsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        ChooseFile = dialog => dialog.ShowDialog(this);
        foreach (var name in viewModel.FemResultNames)
        {
            var box = new CheckBox { Content = name, Tag = name, IsChecked = name == viewModel.SelectedFemResultName, Margin = new Thickness(0, 2, 0, 2) };
            box.Click += (_, _) => AllCases.IsChecked = CaseChoices.Children.OfType<CheckBox>().All(c => c.IsChecked == true);
            CaseChoices.Children.Add(box);
        }
        AllCases.IsChecked = CaseChoices.Children.Count > 0 && CaseChoices.Children.OfType<CheckBox>().All(c => c.IsChecked == true);
        (ResultReportOutput Output, string Name)[] outputs = [(ResultReportOutput.AxialForce, "Axial forces / cables"), (ResultReportOutput.BendingMoment, "Bending moments"), (ResultReportOutput.Shear, "Shear forces"), (ResultReportOutput.Reactions, "Support reactions"), (ResultReportOutput.Deflection, "Deflection")];
        foreach (var (output, name) in outputs)
        {
            var box = new CheckBox { Content = name, Tag = output, IsChecked = true, Margin = new Thickness(0, 3, 0, 3) };
            box.Click += (_, _) => AllOutputs.IsChecked = OutputChoices.Children.OfType<CheckBox>().All(c => c.IsChecked == true);
            OutputChoices.Children.Add(box);
        }
        Closing += (_, e) => { if (exporting) e.Cancel = true; };
    }
    private void AllCases_Click(object sender, RoutedEventArgs e)
    {
        foreach (var box in CaseChoices.Children.OfType<CheckBox>()) box.IsChecked = AllCases.IsChecked == true;
    }
    private void AllOutputs_Click(object sender, RoutedEventArgs e)
    {
        foreach (var box in OutputChoices.Children.OfType<CheckBox>()) box.IsChecked = AllOutputs.IsChecked == true;
    }
    internal ResultReport BuildReport()
    {
        if (!viewModel.IsIdle || !viewModel.HasFemResults || viewModel.SelectedFemRun is not { } run)
            throw new InvalidOperationException("Run analysis and wait for it to finish before exporting results.");
        var main = Owner as MainWindow ?? Owner?.Owner as MainWindow;
        return ResultReport.Create(viewModel.CurrentFileName, run,
            CaseChoices.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray(),
            OutputChoices.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (ResultReportOutput)c.Tag).ToArray(),
            (result, mode) => ResultFigureCapture.Capture(viewModel, main?.SharedViewer.Canvas, result, mode));
    }
    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (exporting) return;
        try
        {
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
