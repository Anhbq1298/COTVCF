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
/// (<see cref="MainViewModel.OptimizationReport"/>): numbered figures, then the summary and conclusions.
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
        viewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelChanged;
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
    private Style CheckStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center));
        foreach (var (value, brush) in new[] { ("OK", "Success"), ("NG", "Error") })
        {
            var trigger = new DataTrigger { Binding = new Binding(nameof(VcfOptimizationRow.Check)), Value = value };
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
