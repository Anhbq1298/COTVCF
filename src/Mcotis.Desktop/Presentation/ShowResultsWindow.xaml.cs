using System.Windows;
using System.Windows.Controls;
using Mcotis.Desktop.Application;

namespace Mcotis.Desktop.Presentation;

public enum ShowResultPickerKind { All, Deflection, MemberForces, Reactions }

/// <summary>
/// Toolbar → Show Results: the load case / combination at the top, the six outputs below. Apply / OK draws the chosen
/// output on the model view (as the Result menu does); Cable target fit opens the results table.
/// </summary>
public partial class ShowResultsWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly Action<bool> openTable;
    public ShowResultPickerKind PickerKind { get; private set; }

    /// <param name="openTable">Opens the results table; the argument selects the cable target fit.</param>
    public ShowResultsWindow(MainViewModel viewModel, Action<bool> openTable, ShowResultPickerKind kind = ShowResultPickerKind.All)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.openTable = openTable;
        DataContext = viewModel;
        if (viewModel.SelectedFemRun is null && viewModel.FemRuns.Count > 0) viewModel.SelectedFemRun = viewModel.FemRuns[0];
        Configure(kind);
    }

    public void Configure(ShowResultPickerKind kind)
    {
        PickerKind = kind;
        var any = viewModel.HasFemResults;
        NoResults.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        Outputs.IsEnabled = TableButton.IsEnabled = ApplyButton.IsEnabled = OkButton.IsEnabled = any;
        Title = kind switch
        {
            ShowResultPickerKind.Deflection => "Show Deformed Shape (F5)",
            ShowResultPickerKind.MemberForces => "Show Member Forces (F6)",
            ShowResultPickerKind.Reactions => "Show Joint Reactions (F7)",
            _ => "Show Results"
        };
        OutputGroup.Visibility = kind is ShowResultPickerKind.All or ShowResultPickerKind.MemberForces
            ? Visibility.Visible : Visibility.Collapsed;
        TableButton.Visibility = kind == ShowResultPickerKind.All ? Visibility.Visible : Visibility.Collapsed;
        var current = viewModel.FemCableFitView ? "CableFit" : viewModel.FemResultMode.ToString();
        foreach (var item in Outputs.Items.OfType<ListBoxItem>())
        {
            var tag = item.Tag as string;
            // the cable target fit is offered for a model with cables only
            item.Visibility = (tag != "CableFit" || viewModel.HasCableMembers) && (kind == ShowResultPickerKind.All ||
                kind == ShowResultPickerKind.Deflection && tag == "DeflectedShape" ||
                kind == ShowResultPickerKind.Reactions && tag == "Reactions" ||
                kind == ShowResultPickerKind.MemberForces && tag is "AxialForce" or "Shear" or "BendingMoment")
                ? Visibility.Visible : Visibility.Collapsed;
        }
        var preferred = kind switch
        {
            ShowResultPickerKind.Deflection => "DeflectedShape",
            ShowResultPickerKind.Reactions => "Reactions",
            ShowResultPickerKind.MemberForces when current is "AxialForce" or "Shear" or "BendingMoment" => current,
            ShowResultPickerKind.MemberForces => "AxialForce",
            _ => current
        };
        Outputs.SelectedItem = Outputs.Items.OfType<ListBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, preferred) && item.Visibility == Visibility.Visible)
            ?? Outputs.Items.OfType<ListBoxItem>().First(item => item.Visibility == Visibility.Visible);
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
    private void Export_Click(object sender, RoutedEventArgs e) => new ExportResultsWindow(viewModel) { Owner = this }.ShowDialog();
}
