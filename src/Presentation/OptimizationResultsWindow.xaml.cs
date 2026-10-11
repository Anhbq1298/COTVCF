using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using COT_VCF.Application;

namespace COT_VCF.Presentation;

/// <summary>
/// Optimization VCF results: one row per analysed case — the values of the inputs that vary in the run, the largest
/// downward deflection, the load case / combination and point where it occurs, and the case status. Columns follow the
/// run (<see cref="MainViewModel.VcfOptimizationColumns"/>).
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
        viewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.VcfOptimizationColumns)) BuildColumns();
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
        var ratio = viewModel.VcfOptimizationSettings?.SpanRatio ?? VcfOptimization.DefaultSpanRatio;
        ResultsGrid.Columns.Add(new DataGridTextColumn { Header = $"Limit span/{ratio:0.###} [mm]", MinWidth = 110, ElementStyle = numeric, Binding = new Binding(nameof(VcfOptimizationRow.Limit)) });
        ResultsGrid.Columns.Add(new DataGridTextColumn { Header = "δ / limit", MinWidth = 70, ElementStyle = numeric,
            Binding = new Binding(nameof(VcfOptimizationRow.Ratio)), SortMemberPath = nameof(VcfOptimizationRow.Utilisation) });
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

    private void CancelRun_Click(object sender, RoutedEventArgs e) => viewModel.CancelVcfOptimization();
}
