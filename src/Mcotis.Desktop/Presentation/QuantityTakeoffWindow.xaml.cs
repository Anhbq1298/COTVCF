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
        var takeoff = NativeQuantityTakeoffCalculator.Calculate(viewModel.NativeDocumentSession.Document);
        RowsGrid.ItemsSource = takeoff.Rows;
        NotesList.ItemsSource = takeoff.Notes;
        TotalsLabel.Text = $"TOTAL · {takeoff.Total.MemberCount:N0} members · {takeoff.Total.LengthText} · concrete {takeoff.Total.ConcreteText} · steel {takeoff.Total.SteelText}";
    }
}
