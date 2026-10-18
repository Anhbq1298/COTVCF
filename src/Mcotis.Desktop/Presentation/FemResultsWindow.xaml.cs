using System.Windows;

namespace Mcotis.Desktop.Presentation;

/// <summary>Result → Results Table: the member force summary of the load case / combo chosen above it.</summary>
public partial class FemResultsWindow : Window
{
    public FemResultsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Export_Click(object sender, RoutedEventArgs e) =>
        new ExportResultsWindow((MainViewModel)DataContext, ExportFocus.ResultsTable) { Owner = this }.ShowDialog();
}
