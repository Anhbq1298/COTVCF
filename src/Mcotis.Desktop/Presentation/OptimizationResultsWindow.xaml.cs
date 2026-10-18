using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Presentation.Controls;

namespace Mcotis.Desktop.Presentation;

/// <summary>
/// Optimization VCF / Optimization TAS results: one row per analysed case — the values of the inputs that vary in the run, the largest
/// downward deflection of a bottom chord point against span / ratio, its largest uplift against the uplift limit, the
/// check of both, the load case / combination and point where the deflection occurs, and the case status. Columns follow the
/// run (<see cref="MainViewModel.VcfOptimizationColumns"/>). The Chart sheet shows the run's parametric study
/// (<see cref="MainViewModel.OptimizationReport"/>): numbered figures, then the summary and conclusions. The Member Utilization sheet
/// lists every member of a chosen case with its utilization (<see cref="MemberUtilizationCheck"/>).
/// </summary>
public partial class OptimizationResultsWindow : Window
{
    private readonly MainViewModel viewModel;

    public OptimizationResultsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        BuildColumns();
        BuildChartPage();
        BuildMemberColumns();
        MemberCaseBox.ItemsSource = viewModel.VcfOptimizationRows;
        ResultsGrid.SelectionChanged += (_, _) => { if (ResultsGrid.SelectedItem is VcfOptimizationRow row) MemberCaseBox.SelectedItem = row; };
        if (viewModel.VcfOptimizationRows.Count > 0) MemberCaseBox.SelectedIndex = 0;
        viewModel.VcfOptimizationRows.CollectionChanged += OnRowsChanged;
        viewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => { viewModel.PropertyChanged -= OnViewModelChanged; viewModel.VcfOptimizationRows.CollectionChanged -= OnRowsChanged; };
    }

    // ---- Member Utilization sheet ----

    /// <summary>A run lists its cases as they complete: the first one is shown until the user picks another.</summary>
    private void OnRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (viewModel.VcfOptimizationRows.Count == 0) { MemberCaseBox.SelectedItem = null; ShowMembers(null); }
        else if (MemberCaseBox.SelectedItem is null) MemberCaseBox.SelectedIndex = 0;
    }

    private void MemberCase_Changed(object sender, SelectionChangedEventArgs e) => ShowMembers(MemberCaseBox.SelectedItem as VcfOptimizationRow);

    private void BuildMemberColumns()
    {
        var numeric = (Style)FindResource("NumericCell");
        MemberGrid.Columns.Add(new DataGridTextColumn { Header = "Member", Binding = new Binding(nameof(MemberUtilization.Member)), MinWidth = 90 });
        MemberGrid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new Binding(nameof(MemberUtilization.Type)), MinWidth = 70 });
        MemberGrid.Columns.Add(new DataGridTextColumn { Header = new TextBlock { Text = "Section" }, Binding = new Binding(nameof(MemberUtilization.Section)), MinWidth = 140 });
        MemberGrid.Columns.Add(new DataGridTextColumn { Header = "Utilization", MinWidth = 90, ElementStyle = numeric,
            Binding = new Binding(nameof(MemberUtilization.UtilizationText)), SortMemberPath = nameof(MemberUtilization.Utilization) });
        MemberGrid.Columns.Add(new DataGridTextColumn { Header = "Check", Binding = new Binding(nameof(MemberUtilization.Check)), MinWidth = 54, ElementStyle = CheckStyle(nameof(MemberUtilization.Check)) });
        MemberGrid.Columns.Add(new DataGridTextColumn { Header = new TextBlock { Text = "Governing result" }, Binding = new Binding(nameof(MemberUtilization.Case)), MinWidth = 130 });
        MemberGrid.Columns.Add(new DataGridTextColumn { Header = "Note", Binding = new Binding(nameof(MemberUtilization.Note)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 160 });
    }

    /// <summary>The members of <paramref name="row"/>'s case, the most utilized first.</summary>
    private void ShowMembers(VcfOptimizationRow? row)
    {
        if (row is null) { MemberGrid.ItemsSource = null; MemberSummary.Text = ""; return; }
        if (row.Members is not { Count: > 0 } members)
        {
            MemberGrid.ItemsSource = null;
            MemberSummary.Text = row.MaxDeflectionMm is null ? "This case could not be analysed — no members." : "No member utilization saved for this case (a study run before this sheet existed): run it again.";
            return;
        }
        MemberGrid.ItemsSource = members.OrderByDescending(item => item.Utilization ?? -1).ThenBy(item => item.Member, StringComparer.Ordinal).ToArray();
        var checkedMembers = members.Where(item => item.Utilization is not null).ToArray();
        var worst = checkedMembers.MaxBy(item => item.Utilization);
        MemberSummary.Text = $"{members.Count} members · {checkedMembers.Length} checked · {checkedMembers.Count(item => item.Check == "OK")} OK" +
            (worst is null ? "" : $" · governing {worst.Member} {worst.UtilizationText} ({worst.Check})");
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.VcfOptimizationColumns) or nameof(MainViewModel.OptimizationTitle)) BuildColumns();
        if (e.PropertyName is nameof(MainViewModel.OptimizationReport)) BuildChartPage();
    }

    private void BuildColumns()
    {
        var numeric = (Style)FindResource("NumericCell");
        ResultsGrid.Columns.Clear();
        ResultsGrid.Columns.Add(new DataGridTextColumn { Header = "Case", Binding = new Binding(nameof(VcfOptimizationRow.Case)), ElementStyle = numeric, MinWidth = 48 });
        var columns = viewModel.VcfOptimizationColumns;
        for (var index = 0; index < columns.Count; index++)
            ResultsGrid.Columns.Add(new DataGridTextColumn { Header = new TextBlock { Text = columns[index].Label }, MinWidth = 70,   // TextBlock: an underscore is not an access key
                Binding = new Binding($"{nameof(VcfOptimizationRow.Values)}[{index}]"), ElementStyle = numeric });
        ResultsGrid.Columns.Add(new DataGridTextColumn { Header = "Max defl. [mm]", MinWidth = 96, ElementStyle = numeric,
            Binding = new Binding(nameof(VcfOptimizationRow.Deflection)), SortMemberPath = nameof(VcfOptimizationRow.MaxDeflectionMm) });
        ResultsGrid.Columns.Add(new DataGridTextColumn { Header = "Span [m]", MinWidth = 70, ElementStyle = numeric, Binding = new Binding(nameof(VcfOptimizationRow.Span)) });
        var ratio = viewModel.OptimizationSpanRatio;
        ResultsGrid.Columns.Add(new DataGridTextColumn { Header = viewModel.OptimizationTitle == "Optimization TAS" ? $"Limit L/{ratio:0.###} [mm]" : $"Limit span/{ratio:0.###} [mm]", MinWidth = 110, ElementStyle = numeric, Binding = new Binding(nameof(VcfOptimizationRow.Limit)) });
        ResultsGrid.Columns.Add(new DataGridTextColumn { Header = "δ / limit", MinWidth = 70, ElementStyle = numeric,
            Binding = new Binding(nameof(VcfOptimizationRow.Ratio)), SortMemberPath = nameof(VcfOptimizationRow.Utilisation) });
        var uplift = viewModel.OptimizationUpliftLimitMm;
        ResultsGrid.Columns.Add(new DataGridTextColumn { Header = $"Uplift ≤ {uplift:0.###} [mm]", MinWidth = 96, ElementStyle = numeric,
            Binding = new Binding(nameof(VcfOptimizationRow.Uplift)), SortMemberPath = nameof(VcfOptimizationRow.MaxUpliftMm) });
        ResultsGrid.Columns.Add(new DataGridTextColumn { Header = "Check", Binding = new Binding(nameof(VcfOptimizationRow.Check)), MinWidth = 54, ElementStyle = CheckStyle() });
        ResultsGrid.Columns.Add(new DataGridTextColumn { Header = "Total cost [SGD]", MinWidth = 110, ElementStyle = numeric,
            Binding = new Binding(nameof(VcfOptimizationRow.Cost)), SortMemberPath = nameof(VcfOptimizationRow.CostSgd) });
        ResultsGrid.Columns.Add(new DataGridTextColumn { Header = new TextBlock { Text = "Result" }, Binding = new Binding(nameof(VcfOptimizationRow.Governing)), MinWidth = 90 });
        ResultsGrid.Columns.Add(new DataGridTextColumn { Header = "Point", Binding = new Binding(nameof(VcfOptimizationRow.Point)), MinWidth = 70 });
        ResultsGrid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding(nameof(VcfOptimizationRow.Status)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 120 });
    }

    /// <summary>OK in the success colour, NG in the error colour.</summary>
    private Style CheckStyle(string path = nameof(VcfOptimizationRow.Check))
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center));
        foreach (var (value, brush) in new[] { ("OK", "Success"), ("NG", "Error"), ("FAIL", "Error"), ("ERROR", "Error") })
        {
            var trigger = new DataTrigger { Binding = new Binding(path), Value = value };
            trigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, TryFindResource(brush) ?? System.Windows.Media.Brushes.Black));
            style.Triggers.Add(trigger);
        }
        return style;
    }

    // ---- Chart sheet ----
    private System.Windows.Media.Brush Resource(string key) => (System.Windows.Media.Brush)FindResource(key);

    /// <summary>The figures (two per row), then the summary and the numbered conclusions, like the results section of a paper.</summary>
    private void BuildChartPage()
    {
        ChartPage.Children.Clear();
        var report = viewModel.OptimizationReport;
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var copy = new Button { Content = "Copy Summary", MinWidth = 110, IsEnabled = report.Summary.Count > 0 };
        copy.Click += (_, _) => { try { Clipboard.SetText(SummaryText(report)); } catch (System.Runtime.InteropServices.COMException) { } };
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);
        header.Children.Add(new TextBlock { Text = $"{viewModel.OptimizationTitle} — parametric study", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        ChartPage.Children.Add(header);

        if (report.Charts.Count == 0)
        {
            ChartPage.Children.Add(new TextBlock
            {
                Text = report.Summary.Count > 0 ? report.Summary[0].Text : "The charts appear here when a run finishes.",
                Foreground = Resource("Muted"), TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var figures = new UniformGrid { Columns = 2 };
        foreach (var chart in report.Charts)
        {
            var card = new StackPanel { Margin = new Thickness(0, 0, 12, 14) };
            card.Children.Add(new TextBlock
            {
                Inlines = { new Run($"Figure {chart.Number}. ") { FontWeight = FontWeights.SemiBold }, new Run(chart.Title) },
                FontSize = 12, Margin = new Thickness(0, 0, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var view = new StudyChartView { Chart = chart, Height = 250 };
            System.Windows.Automation.AutomationProperties.SetName(view, $"Figure {chart.Number}");
            card.Children.Add(new Border { BorderBrush = Resource("Border"), BorderThickness = new Thickness(1), Child = view });
            card.Children.Add(new TextBlock { Text = chart.Caption, FontSize = 11, Foreground = Resource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            figures.Children.Add(card);
        }
        ChartPage.Children.Add(figures);

        ChartPage.Children.Add(new TextBlock { Text = "SUMMARY", Style = (Style)FindResource("Subheading"), Margin = new Thickness(0, 8, 0, 6) });
        foreach (var (heading, text) in report.Summary)
            ChartPage.Children.Add(new TextBlock
            {
                Inlines = { new Run(heading + ". ") { FontWeight = FontWeights.SemiBold }, new Run(text) },
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left,
            });
        if (report.Conclusions.Count > 0)
        {
            ChartPage.Children.Add(new TextBlock { Text = "CONCLUSIONS", Style = (Style)FindResource("Subheading"), Margin = new Thickness(0, 6, 0, 6) });
            for (var i = 0; i < report.Conclusions.Count; i++)
                ChartPage.Children.Add(new TextBlock
                {
                    Text = $"{i + 1}.  {report.Conclusions[i]}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
                    MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left,
                });
        }
    }

    /// <summary>The summary and conclusions as plain text (Copy Summary).</summary>
    internal static string SummaryText(StudyReport report) =>
        string.Join(Environment.NewLine + Environment.NewLine, report.Summary.Select(entry => $"{entry.Heading}. {entry.Text}")) +
        (report.Conclusions.Count == 0 ? "" : Environment.NewLine + Environment.NewLine + "Conclusions" + Environment.NewLine +
            string.Join(Environment.NewLine, report.Conclusions.Select((text, i) => $"{i + 1}. {text}")));

    private void CancelRun_Click(object sender, RoutedEventArgs e) => viewModel.CancelVcfOptimization();
}
