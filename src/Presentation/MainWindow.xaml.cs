using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace COT_VCF.Presentation;
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { if (DataContext is MainViewModel vm) { vm.CommitEditors = CommitEditors; vm.PropertyChanged += (_, _) => { FxColumn.Header = vm.FxHeader; FyColumn.Header = vm.FyHeader; FzColumn.Header = vm.FzHeader; }; } };
    }
    private void ValidateEditors(object sender, MouseButtonEventArgs e)
    {
        if (!CommitEditors()) e.Handled = true;
    }
    private bool CommitEditors()
    {
        bool valid = Loads.CommitEdit(DataGridEditingUnit.Cell, true) && Loads.CommitEdit(DataGridEditingUnit.Row, true) && Cables.CommitEdit(DataGridEditingUnit.Cell, true) && Cables.CommitEdit(DataGridEditingUnit.Row, true);
        bool hiddenErrors = WorkflowTabs.Items.OfType<TabItem>().Any(tab => tab.Content is DependencyObject content && HasErrors(content));
        if (valid && !HasErrors(this) && !hiddenErrors) return true;
        MessageBox.Show(this, "Correct the highlighted numeric inputs before continuing.", "Review inputs", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }
    private static bool HasErrors(DependencyObject item)
    {
        if (Validation.GetHasError(item)) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) if (HasErrors(VisualTreeHelper.GetChild(item, i))) return true;
        return false;
    }
    private void ShowHelp(object sender, RoutedEventArgs e) => MessageBox.Show(this, "1. Connect to a running SAP2000 model and get geometry.\n2. Add points, select patterns and apply global forces.\n3. Select level chord groups, capture a baseline and preview height changes.\n4. Select cable / tendon force sources and define ranges.\n5. Validate the configuration. Iteration is not implemented yet.\n\nViewer: mouse wheel zooms, drag pans, double-click or Home fits.\nTables: Tab edits, Delete removes selected rows, Ctrl+C copies.\nSAP units are retained. Unlock a model in SAP before applying changes.", "COT VCF tools — Help", MessageBoxButton.OK, MessageBoxImage.Information);
}
