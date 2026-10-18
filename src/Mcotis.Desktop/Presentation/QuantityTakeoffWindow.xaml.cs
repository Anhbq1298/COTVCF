using System.Windows;
using Mcotis.Desktop.Application.Quantities;

namespace Mcotis.Desktop.Presentation;

public partial class QuantityTakeoffWindow : Window
{
    private readonly MainViewModel viewModel;

    public QuantityTakeoffWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        ModelLabel.Text = viewModel.WindowTitle;
        Refresh();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.HasFemResults) { MessageBox.Show(this, "Run FEM Analysis first: the report starts from the solved results.", "Export Results", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        new ExportResultsWindow(viewModel, ExportFocus.Quantities) { Owner = this }.ShowDialog();
    }

    private void Refresh()
    {
        var takeoff = viewModel.QuantityTakeoff();
        RowsGrid.ItemsSource = takeoff.Rows;
        UtilizationGrid.ItemsSource = viewModel.QuantityUtilizationSummary();
        NotesList.ItemsSource = takeoff.Notes;
        TotalsLabel.Text = $"TOTAL · {takeoff.Total.MemberCount:N0} members · {takeoff.Total.LengthText} · concrete {takeoff.Total.ConcreteText} · steel {takeoff.Total.SteelText} · estimated material cost {takeoff.Total.EstimatedCostText}";
    }
}
