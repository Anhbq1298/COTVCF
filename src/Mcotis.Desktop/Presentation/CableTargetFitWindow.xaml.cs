using System.Windows;

namespace Mcotis.Desktop.Presentation;

/// <summary>Result → Cable target fit: each cable's target, fitted pretension and tension in the load case / combo chosen.</summary>
public partial class CableTargetFitWindow : Window
{
    public CableTargetFitWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
