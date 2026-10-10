using System.Windows;
using System.Windows.Controls;
using COT_VCF.Application;

namespace COT_VCF.Presentation;

/// <summary>
/// Toolbar → Show Results: candidate and load case / combination at the top, the six outputs below. Apply / OK draws the
/// chosen output on the model view (as the Result menu does); Cable target fit opens the results table.
/// </summary>
public partial class ShowResultsWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly Action<bool> openTable;

    /// <param name="openTable">Opens the results table; the argument selects the cable target fit.</param>
    public ShowResultsWindow(MainViewModel viewModel, Action<bool> openTable)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.openTable = openTable;
        DataContext = viewModel;
        if (viewModel.SelectedFemRun is null && viewModel.FemRuns.Count > 0) viewModel.SelectedFemRun = viewModel.FemRuns[0];
        var current = viewModel.FemCableFitView ? "CableFit" : viewModel.FemResultMode.ToString();
        Outputs.SelectedItem = Outputs.Items.OfType<ListBoxItem>().FirstOrDefault(item => Equals(item.Tag, current)) ?? Outputs.Items[0];
        var any = viewModel.HasFemResults;
        NoResults.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        Outputs.IsEnabled = TableButton.IsEnabled = ApplyButton.IsEnabled = OkButton.IsEnabled = any;
    }

    /// <summary>Draws the selected output on the model view (cable target fit: the table).</summary>
    internal void ApplySelection()
    {
        if (!viewModel.HasFemResults || Outputs.SelectedItem is not ListBoxItem { Tag: string tag }) return;
        var mode = Enum.TryParse<FemResultMode>(tag, out var value) ? value : (FemResultMode?)null;
        viewModel.ShowFemResult(mode);
        if (mode is null) openTable(true);
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => ApplySelection();
    private void Ok_Click(object sender, RoutedEventArgs e) { ApplySelection(); Close(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Outputs_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => ApplySelection();
    private void Table_Click(object sender, RoutedEventArgs e) => openTable(viewModel.FemCableFitView);
}
