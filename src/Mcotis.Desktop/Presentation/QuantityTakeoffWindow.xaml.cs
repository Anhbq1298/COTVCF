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

    private void Refresh()
    {
        viewModel.EnsureMaterials();
        var materialRates = viewModel.Materials
            .GroupBy(material => material.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var takeoff = NativeQuantityTakeoffCalculator.Calculate(viewModel.NativeDocumentSession.Document, materialRates);
        RowsGrid.ItemsSource = takeoff.Rows;
        UtilizationGrid.ItemsSource = viewModel.QuantityUtilizationSummary();
        NotesList.ItemsSource = takeoff.Notes;
        TotalsLabel.Text = $"TOTAL · {takeoff.Total.MemberCount:N0} members · {takeoff.Total.LengthText} · concrete {takeoff.Total.ConcreteText} · steel {takeoff.Total.SteelText} · estimated material cost {takeoff.Total.EstimatedCostText}";
    }
}
