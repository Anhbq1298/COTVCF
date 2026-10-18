using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Design;
using Mcotis.Desktop.Domain;
using Microsoft.Win32;

namespace Mcotis.Desktop.Presentation;

/// <summary>
/// The result of a column design run, shown in the Design tab of the Design Reference dialog: one summary row per member
/// (governing case, design actions, utilisations, status) and, below, the step-by-step calculation of the selected member.
/// Copy / Save write the whole report as text.
/// </summary>
public partial class DesignResultsPanel : UserControl
{
    private ColumnDesignBasis? basis;
    private IReadOnlyList<ColumnMemberDesign> results = [];
    private string? runId;
    private string? title;

    public DesignResultsPanel()
    {
        InitializeComponent();
        CountText.Text = "Run Design to see the results here.";
    }

    /// <summary>Shows the results of a design run, replacing the last ones.</summary>
    public void Show(ColumnDesignBasis basis, IReadOnlyList<ColumnMemberDesign> results, string? runId, string? title = null, string? basisText = null)
    {
        this.basis = basis;
        this.results = results;
        this.runId = runId;
        this.title = title;
        HeaderText.Text = title ?? ColumnDesignCodes.Title(basis.Type);
        BasisText.Text = basisText ?? $"{basis.DesignCode} · {basis.Method} · l0,y = {basis.EffectiveLengthY:0.###} m, l0,z = {basis.EffectiveLengthZ:0.###} m · " +
                         $"{(runId is null ? "no FEM results" : "FEM analysis of the model")} · {basis.LoadCases.Count} load case(s)";
        ProcedureColumn.Visibility = results.Any(result => result.Procedure.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
        var failed = results.Count(result => result.Status is "FAIL" or "ERROR");
        var check = results.Count(result => result.Status == "CHECK");
        CountText.Text = $"{results.Count} member(s): {results.Count - failed - check} OK · {check} detailing to check · {failed} failing";
        SummaryGrid.ItemsSource = results;
        if (results.Count > 0) SummaryGrid.SelectedIndex = 0;
    }

    internal bool HasResults => basis is not null;

    private void SummaryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SummaryGrid.SelectedItem is not ColumnMemberDesign result) return;
        DetailBox.Header = $"Detailed calculation — {result.Member}";
        DetailText.Text = result.Error ?? result.Detail;
        DetailText.ScrollToHome();
    }

    /// <summary>The summary table followed by every member's calculation.</summary>
    internal string Report()
    {
        if (basis is null) return "";
        var text = new StringBuilder();
        text.AppendLine((title ?? ColumnDesignCodes.Title(basis.Type)).ToUpperInvariant());
        text.AppendLine(BasisText.Text);
        text.AppendLine();
        var procedures = results.Any(result => result.Procedure.Length > 0);
        text.AppendLine($"{"Member",-10}{(procedures ? "Procedure".PadRight(30) : "")}{"Role",-16}{"Section",-22}{"Governing case",-24}{"NEd kN",10}{"MEd,y",10}{"MEd,z",10}{"VEd kN",10}{"Max",7}  Status");
        foreach (var result in results)
        {
            var g = result.Governing;
            text.AppendLine($"{result.Member,-10}{(procedures ? Cut(result.Procedure, 29).PadRight(30) : "")}{Cut(result.Role, 15),-16}{Cut(result.Section, 21),-22}{Cut(g?.Case ?? "—", 23),-24}{g?.NEdKN,10:0.0}{g?.MEdYKNm,10:0.0}{g?.MEdZKNm,10:0.0}{g?.VEdKN,10:0.0}" +
                            $"{(double.IsFinite(result.MaxUtilization) ? result.MaxUtilization.ToString("0.00") : ">9.99"),7}  {result.Status}");
        }
        foreach (var result in results)
        {
            text.AppendLine();
            text.AppendLine(new string('═', 92));
            text.AppendLine(result.Error is null ? result.Detail : $"MEMBER {result.Member}: {result.Error}");
        }
        return text.ToString();
    }

    /// <summary>The summary table and every calculation as the Word report of Export to Word….</summary>
    internal DesignReport WordReport()
    {
        string Number(double? value, string format) => value is { } number ? number.ToString(format, System.Globalization.CultureInfo.InvariantCulture) : "—";
        string Use(double value) => double.IsFinite(value) ? value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : ">9.99";
        var procedures = results.Any(result => result.Procedure.Length > 0);
        string[] header = [.. new[] { "Member" }, .. (procedures ? new[] { "Procedure" } : []), "Role", "Section", "Governing case", "NEd kN", "MEd,y kNm", "MEd,z kNm", "VEd kN", "N", "M-y", "M-z/biax", "V", "Max", "Status"];
        var rows = results.Select(result =>
        {
            var g = result.Governing;
            return (IReadOnlyList<string>)[.. new[] { result.Member }, .. (procedures ? new[] { result.Procedure } : []), result.Role, result.Section, g?.Case ?? "—",
                Number(g?.NEdKN, "0.0"), Number(g?.MEdYKNm, "0.0"), Number(g?.MEdZKNm, "0.0"), Number(g?.VEdKN, "0.0"),
                g is null ? "—" : Use(g.AxialUtilization), g is null ? "—" : Use(g.MajorUtilization), g is null ? "—" : Use(g.MinorUtilization), g is null ? "—" : Use(g.ShearUtilization),
                Use(result.MaxUtilization), result.Status];
        }).ToArray();
        return new(title ?? (basis is null ? "Design" : ColumnDesignCodes.Title(basis.Type)), [BasisText.Text, CountText.Text], header, rows,
            results.Select(result => ($"Member {result.Member}", result.Error is null ? result.Detail : $"MEMBER {result.Member}: {result.Error}")).ToArray(), []);
    }

    private void ExportWord_Click(object sender, RoutedEventArgs e)
    {
        if (basis is null || results.Count == 0) { MessageBox.Show(Window.GetWindow(this), "Run Design first; there is nothing to export yet.", "Export to Word"); return; }
        var dialog = new SaveFileDialog
        {
            Title = "Export to Word", Filter = "Word document (*.docx)|*.docx", AddExtension = true,
            FileName = $"{ColumnDesignCodes.Title(basis.Type).Replace(' ', '_').Replace("(", "").Replace(")", "").Replace("/", "-")}_{runId ?? "design"}.docx",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            Mcotis.Desktop.Application.DesignReportWord.Export(WordReport(), dialog.FileName);
            if (MessageBox.Show(Window.GetWindow(this), $"Saved to\n{dialog.FileName}\n\nOpen it now?", "Export to Word", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), "The document could not be saved: " + ex.Message + "\n\nIs it open in Word? Close it, or choose another name.", "Export to Word", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private void Copy_Click(object sender, RoutedEventArgs e) { if (basis is not null) Clipboard.SetText(Report()); }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (basis is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Save Design Report",
            Filter = "Text report (*.txt)|*.txt",
            FileName = $"{ColumnDesignCodes.Title(basis.Type).Replace(' ', '_').Replace("(", "").Replace(")", "").Replace("/", "-")}_{runId ?? "design"}.txt",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        File.WriteAllText(dialog.FileName, Report(), Encoding.UTF8);
    }
}
