using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using COT_VCF.Application.Design;
using COT_VCF.Domain;
using Microsoft.Win32;

namespace COT_VCF.Presentation;

/// <summary>
/// The result of a column design run: one summary row per member (governing case, design actions, utilisations, status)
/// and, below, the step-by-step calculation of the selected member. Copy / Save write the whole report as text.
/// </summary>
public partial class DesignResultsWindow : Window
{
    private readonly ColumnDesignBasis basis;
    private readonly IReadOnlyList<ColumnMemberDesign> results;
    private readonly string? runId;

    public DesignResultsWindow(ColumnDesignBasis basis, IReadOnlyList<ColumnMemberDesign> results, string? runId)
    {
        InitializeComponent();
        this.basis = basis;
        this.results = results;
        this.runId = runId;
        var title = ColumnDesignCodes.Title(basis.Type);
        Title = "Design Results — " + title;
        HeaderText.Text = title;
        BasisText.Text = $"{basis.DesignCode} · {basis.Method} · l0,y = {basis.EffectiveLengthY:0.###} m, l0,z = {basis.EffectiveLengthZ:0.###} m · " +
                         $"FEM candidate {runId ?? "—"} · {basis.LoadCases.Count} load case(s)";
        var failed = results.Count(result => result.Status is "FAIL" or "ERROR");
        var check = results.Count(result => result.Status == "CHECK");
        CountText.Text = $"{results.Count} member(s): {results.Count - failed - check} OK · {check} detailing to check · {failed} failing";
        SummaryGrid.ItemsSource = results;
        if (results.Count > 0) SummaryGrid.SelectedIndex = 0;
    }

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
        var text = new StringBuilder();
        text.AppendLine(ColumnDesignCodes.Title(basis.Type).ToUpperInvariant());
        text.AppendLine(BasisText.Text);
        text.AppendLine();
        text.AppendLine($"{"Member",-10}{"Section",-22}{"Governing case",-24}{"NEd kN",10}{"MEd,y",10}{"MEd,z",10}{"VEd kN",10}{"Max",7}  Status");
        foreach (var result in results)
        {
            var g = result.Governing;
            text.AppendLine($"{result.Member,-10}{Cut(result.Section, 21),-22}{Cut(g?.Case ?? "—", 23),-24}{g?.NEdKN,10:0.0}{g?.MEdYKNm,10:0.0}{g?.MEdZKNm,10:0.0}{g?.VEdKN,10:0.0}" +
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

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private void Copy_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(Report());

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Design Report",
            Filter = "Text report (*.txt)|*.txt",
            FileName = $"{ColumnDesignCodes.Title(basis.Type).Replace(' ', '_').Replace("(", "").Replace(")", "")}_{runId ?? "design"}.txt",
        };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, Report(), Encoding.UTF8);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
