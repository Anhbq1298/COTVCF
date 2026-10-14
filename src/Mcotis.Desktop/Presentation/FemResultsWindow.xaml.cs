using System.ComponentModel;
using System.Windows;

namespace Mcotis.Desktop.Presentation;

public partial class FemResultsWindow : Window
{
    public FemResultsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        // the support moment is a column of the reactions only: 0 at a pin, the moment of a support that holds RY
        void ShowMoment() => SupportMomentColumn.Visibility = viewModel.FemResultMode == Mcotis.Desktop.Application.FemResultMode.Reactions
            ? Visibility.Visible : Visibility.Collapsed;
        PropertyChangedEventHandler follow = (_, e) => { if (e.PropertyName == nameof(MainViewModel.FemResultMode)) ShowMoment(); };
        viewModel.PropertyChanged += follow;
        Closed += (_, _) => viewModel.PropertyChanged -= follow;
        ShowMoment();
    }
    /// <summary>Shows the cable target fit, or the results of the members. A model without cables has no fit to show.</summary>
    public void SelectCableFit(bool cableFit) =>
        ResultTopics.SelectedIndex = cableFit && DataContext is MainViewModel { HasCableMembers: true } ? 1 : 0;
}