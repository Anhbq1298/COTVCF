using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace COT_VCF.Presentation;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.CommitEditors = CommitEditors;
        };
    }

    private void ValidateEditors(object sender, MouseButtonEventArgs e)
    {
        if (!CommitEditors()) e.Handled = true;
    }

    private bool CommitEditors()
    {
        bool valid = LoadDefinitionsGrid.CommitEdit(DataGridEditingUnit.Cell, true) &&
            LoadDefinitionsGrid.CommitEdit(DataGridEditingUnit.Row, true) &&
            Cables.CommitEdit(DataGridEditingUnit.Cell, true) &&
            Cables.CommitEdit(DataGridEditingUnit.Row, true);
        bool hiddenErrors = WorkflowTabs.Items.OfType<TabItem>().Any(tab => tab.Content is DependencyObject content && HasErrors(content));
        if (valid && !HasErrors(this) && !hiddenErrors) return true;
        MessageBox.Show(this, "Correct the highlighted numeric inputs before continuing.", "Review inputs", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private void AppliedPoints_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.SelectedAppliedPoint = AppliedPointsGrid.SelectedItem as Domain.AppliedPoint;
        if (vm.SelectedAppliedPoint is not null) AppliedPointsGrid.ScrollIntoView(vm.SelectedAppliedPoint);
    }

    private void AddLoad_Click(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (LoadDefinitionsGrid.Items.Count == 0) return;
            var row = LoadDefinitionsGrid.Items[^1];
            LoadDefinitionsGrid.SelectedItem = row;
            LoadDefinitionsGrid.ScrollIntoView(row);
            LoadDefinitionsGrid.CurrentCell = new DataGridCellInfo(row, LoadDefinitionsGrid.Columns[0]);
            LoadDefinitionsGrid.BeginEdit();
        }, DispatcherPriority.Input);
    }

    private static bool HasErrors(DependencyObject item)
    {
        if (Validation.GetHasError(item)) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
            if (HasErrors(VisualTreeHelper.GetChild(item, i))) return true;
        return false;
    }

    private void ShowHelp(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "1. Connect to a running SAP2000 model and load geometry.\n" +
        "2. Select Point Objects in SAP2000, add them as applied points, and define loading criteria.\n" +
        "3. Select level chord groups, capture a baseline and preview height changes.\n" +
        "4. Select cable / tendon force sources and define ranges.\n" +
        "5. Validate the configuration. Iteration is not implemented yet.\n\n" +
        "Viewer: mouse wheel zooms, drag pans, double-click or Home fits.\n" +
        "SAP units are retained. Unlock a model in SAP before applying changes.",
        "COT VCF tools - Help", MessageBoxButton.OK, MessageBoxImage.Information);
}
