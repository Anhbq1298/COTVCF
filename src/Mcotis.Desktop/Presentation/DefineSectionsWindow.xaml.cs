using System.Windows;

namespace Mcotis.Desktop.Presentation;

/// <summary>Define → Sections: the SAP member properties the FEM uses, in a window of its own (read-only).</summary>
public partial class DefineSectionsWindow : Window
{
    public DefineSectionsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
