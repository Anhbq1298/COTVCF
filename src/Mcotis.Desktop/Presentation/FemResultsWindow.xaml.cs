using System.Windows;

namespace Mcotis.Desktop.Presentation;

public partial class FemResultsWindow : Window
{
    public FemResultsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
    public void SelectCableFit(bool cableFit) => ResultTopics.SelectedIndex = cableFit ? 1 : 0;
}
