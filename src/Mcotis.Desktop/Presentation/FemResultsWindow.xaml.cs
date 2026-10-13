using System.Windows;

namespace Mcotis.Desktop.Presentation;

public partial class FemResultsWindow : Window
{
    public FemResultsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
    /// <summary>Shows the cable target fit, or the results of the members. A model without cables has no fit to show.</summary>
    public void SelectCableFit(bool cableFit) =>
        ResultTopics.SelectedIndex = cableFit && DataContext is MainViewModel { HasCableMembers: true } ? 1 : 0;
}
