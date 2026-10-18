using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mcotis.Desktop.Application;

namespace Mcotis.Desktop.Presentation;

public partial class SapCompareWindow : Window
{
    private static readonly (string Title, string Unit)[] Forces = [("Bending", "MNm"), ("Shear", "MN"), ("Axial", "MN")];
    private readonly MainViewModel viewModel;
    private SapCompareTable? table;
    public SapCompareWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        CaseBox.ItemsSource = viewModel.FemResultNames;
        CaseBox.SelectedItem = viewModel.SelectedFemResultName is { } name && viewModel.FemResultNames.Contains(name) ? name : viewModel.FemResultNames.FirstOrDefault();
        Status.Text = "Export the model to SAP2000 (File → Export to SAP2000), run the analysis there, then choose the load case and click Compare.";
    }

    internal SapCompareTable? Table => table;

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (CaseBox.SelectedItem is not string caseName || viewModel.SelectedFemRun is not { } run) { Status.Text = "Run FEM Analysis first."; return; }
        CompareButton.IsEnabled = CopyButton.IsEnabled = WordButton.IsEnabled = false;
        Status.Text = "Reading the forces from SAP2000 (read-only)…";
        try
        {
            var groups = ResultMemberSummary.Groups(viewModel.State.Geometry, viewModel.State.Truss, viewModel.FemSectionProperties.ToArray());
            var mcotis = ResultMemberSummary.Create(caseName, run.ResultFor(caseName), groups,
                [ResultReportOutput.AxialForce, ResultReportOutput.BendingMoment, ResultReportOutput.Shear]);
            var peaks = await viewModel.ReadSapMemberPeaksAsync(caseName);
            table = SapMemberComparison.Create(caseName, viewModel.State.Model?.Name ?? "SAP2000", mcotis, groups, peaks,
                viewModel.State.Geometry.Members.Where(m => m.Kind != Domain.MemberKind.Frame).Select(m => m.Name).ToHashSet(StringComparer.Ordinal));
            Render(table);
            CopyButton.IsEnabled = WordButton.IsEnabled = true;
            Status.Text = SapMemberComparison.Note + (table.Warnings.Count > 0 ? Environment.NewLine + "SAP2000: " + string.Join("; ", table.Warnings.Take(6)) +
                (table.Warnings.Count > 6 ? $" … (+{table.Warnings.Count - 6})" : "") : "");
        }
        catch (Exception ex) { Status.Text = "Not compared: " + ex.Message; }
        finally { CompareButton.IsEnabled = true; }
    }

    private void Render(SapCompareTable data)
    {
        TableGrid.Children.Clear(); TableGrid.RowDefinitions.Clear(); TableGrid.ColumnDefinitions.Clear();
        double[] widths = [170, 84, 84, 84, 84, 84, 84, 300];
        foreach (var w in widths) TableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        for (int i = 0; i < data.Rows.Count + 2; i++) TableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new SolidColorBrush(Color.FromRgb(0x8F, 0xB3, 0xDF));
        void Cell(string text, int row, int column, int columnSpan = 1, int rowSpan = 1, bool head = false, bool right = false)
        {
            var border = new Border { BorderBrush = (Brush)FindResource("Border"), BorderThickness = new Thickness(0.5), Background = head ? header : Brushes.Transparent, Padding = new Thickness(5, 3, 5, 3) };
            border.Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
                TextAlignment = head ? TextAlignment.Center : right ? TextAlignment.Right : TextAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Foreground = head ? Brushes.Black : (Brush)FindResource("Text") };
            Grid.SetRow(border, row); Grid.SetColumn(border, column); Grid.SetColumnSpan(border, columnSpan); Grid.SetRowSpan(border, rowSpan);
            TableGrid.Children.Add(border);
        }
        Cell("Member", 0, 0, rowSpan: 2, head: true);
        for (int f = 0; f < 3; f++)
        {
            Cell($"{Forces[f].Title} ({Forces[f].Unit})", 0, 1 + 2 * f, 2, head: true);
            Cell("Mcotis", 1, 1 + 2 * f, head: true); Cell("SAP2000", 1, 2 + 2 * f, head: true);
        }
        Cell("Member size", 0, 7, rowSpan: 2, head: true);
        for (int r = 0; r < data.Rows.Count; r++)
        {
            var cells = data.Rows[r].Cells;
            for (int c = 0; c < cells.Length; c++) Cell(cells[c], r + 2, c, right: c is >= 1 and <= 6);
        }
    }

    private void Word_Click(object sender, RoutedEventArgs e)
    {
        if (table is null) return;
        var name = System.IO.Path.GetFileNameWithoutExtension(viewModel.CurrentFileName);
        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Save Comparison", FileName = name + " - SAP2000 comparison", DefaultExt = ".docx",
            Filter = "Word document (*.docx)|*.docx", AddExtension = true, OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            SapMemberComparison.WriteWord(table, dialog.FileName);
            Status.Text = "Saved: " + dialog.FileName;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Status.Text += Environment.NewLine + "The document could not be opened: " + ex.Message; }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Status.Text = "Not saved: " + ex.Message; }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (table is null) return;
        var lines = new List<string> { "Member\tBending Mcotis (MNm)\tBending SAP2000 (MNm)\tShear Mcotis (MN)\tShear SAP2000 (MN)\tAxial Mcotis (MN)\tAxial SAP2000 (MN)\tMember size" };
        lines.AddRange(table.Rows.Select(r => string.Join('\t', r.Cells)));
        try { Clipboard.SetText(string.Join(Environment.NewLine, lines)); Status.Text = "Copied to the clipboard."; }
        catch (System.Runtime.InteropServices.COMException) { Status.Text = "The clipboard is busy; try again."; }
    }
}
