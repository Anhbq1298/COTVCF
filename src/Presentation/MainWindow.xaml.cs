using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace COT_VCF.Presentation;

public partial class MainWindow : Window
{
    // Remembers the user's dragged split so it survives visits to the full-width tabs.
    private GridLength paramsColumnWidth = new(2, GridUnitType.Star);
    private GridLength viewerColumnWidth = new(3, GridUnitType.Star);

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.PropertyChanged -= OnViewModelPropertyChanged;
            if (DataContext is MainViewModel vm)
            {
                vm.CommitEditors = CommitEditors;
                vm.PropertyChanged += OnViewModelPropertyChanged;
                SyncReportView(vm);
            }
        };
        Loaded += (_, _) => ApplyViewerLayout();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is MainViewModel vm && e.PropertyName is nameof(MainViewModel.DeflectionReportUri) or nameof(MainViewModel.HasDeflectionReport))
            SyncReportView(vm);
    }

    // WebView2 does not re-navigate when Source is set to an equal Uri, so a regenerated report needs an explicit Reload.
    private void SyncReportView(MainViewModel vm)
    {
        if (ReportView is null) return;
        try
        {
            var uri = vm.DeflectionReportUri;
            if (uri is null) return;
            if (Equals(ReportView.Source, uri)) ReportView.Reload();
            else ReportView.Source = uri;
        }
        catch { /* WebView2 runtime may be unavailable; the placeholder stays visible */ }
    }

    // The model preview is one shared viewer hoisted out of the TabControl. Tabs 01-04 (Loading, Truss,
    // Pretension, Output) keep a resizable ~40/60 params/preview split; tab 05 (Iteration) takes the full width.
    private void WorkflowTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, WorkflowTabs)) ApplyViewerLayout();
    }

    private void ApplyViewerLayout()
    {
        if (ParamsColumn is null) return;
        bool showViewer = WorkflowTabs.SelectedIndex is >= 0 and <= 3;

        if (ViewerSplitter.Visibility == Visibility.Visible && ParamsColumn.Width.IsStar && ViewerColumn.Width.IsStar)
        {
            paramsColumnWidth = ParamsColumn.Width;
            viewerColumnWidth = ViewerColumn.Width;
        }

        SharedViewerPane.Visibility = showViewer ? Visibility.Visible : Visibility.Collapsed;
        ViewerSplitter.Visibility = showViewer ? Visibility.Visible : Visibility.Collapsed;

        if (showViewer)
        {
            ParamsColumn.Width = paramsColumnWidth;
            ViewerSplitterColumn.Width = GridLength.Auto;
            ViewerColumn.Width = viewerColumnWidth;
        }
        else
        {
            ParamsColumn.Width = new GridLength(1, GridUnitType.Star);
            ViewerSplitterColumn.Width = new GridLength(0);
            ViewerColumn.Width = new GridLength(0);
        }
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

    private void Cables_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.SelectedCable = Cables.SelectedItem as Domain.CablePretensionConfig;
        if (vm.SelectedCable is not null) Cables.ScrollIntoView(vm.SelectedCable);
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

    private void ViewCandidates_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (!CommitEditors()) return;
        if (vm.CreateLoadingCandidatePreview() is not { } preview) return;

        var dialog = new Window
        {
            Title = "Loading Candidates",
            Owner = this,
            Width = 920,
            Height = 600,
            MinWidth = 720,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new DockPanel { Margin = new Thickness(20) };
        var summary = new TextBlock
        {
            Text = $"{preview.CaseCount} candidates for loading cases, applied at {vm.State.AppliedPoints.Count} locations.",
            Foreground = (Brush)FindResource("Accent"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(summary, Dock.Top);
        panel.Children.Add(summary);

        var close = new Button
        {
            Content = "Close",
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            MinHeight = 30,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 12, 0, 0)
        };
        close.Click += (_, _) => dialog.Close();
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(close);

        var grid = new DataGrid
        {
            ItemsSource = preview.Rows,
            IsReadOnly = true,
            CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Extended,
            FrozenColumnCount = 2
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Applied Pt", Binding = new Binding(nameof(Domain.LoadingCandidate.AppliedPt)), Width = new DataGridLength(1.2, DataGridLengthUnitType.Star), MinWidth = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Case", Binding = new Binding(nameof(Domain.LoadingCandidate.Case)), Width = new DataGridLength(1.4, DataGridLengthUnitType.Star), MinWidth = 130 });
        grid.Columns.Add(new DataGridTextColumn { Header = "qz", Binding = new Binding(nameof(Domain.LoadingCandidate.Qz)) { StringFormat = "F2" }, Width = new DataGridLength(.8, DataGridLengthUnitType.Star), MinWidth = 80, ElementStyle = (Style)FindResource("NumericCell") });
        grid.Columns.Add(new DataGridTextColumn { Header = "TribArea", Binding = new Binding(nameof(Domain.LoadingCandidate.TribArea)) { StringFormat = "F3" }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 95, ElementStyle = (Style)FindResource("NumericCell") });
        grid.Columns.Add(new DataGridTextColumn { Header = "NStories", Binding = new Binding(nameof(Domain.LoadingCandidate.NStories)), Width = new DataGridLength(.8, DataGridLengthUnitType.Star), MinWidth = 80, ElementStyle = (Style)FindResource("NumericCell") });
        grid.Columns.Add(new DataGridTextColumn { Header = "FzkN", Binding = new Binding(nameof(Domain.LoadingCandidate.FzKn)) { StringFormat = "F3" }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 95, ElementStyle = (Style)FindResource("NumericCell") });
        panel.Children.Add(grid);
        dialog.Content = panel;
        dialog.ShowDialog();
    }

    private void ViewTrussCandidates_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (!CommitEditors()) return;
        if (vm.CreateTrussCandidates() is not { } rows) return;

        var dialog = new Window
        {
            Title = "Truss Height Candidates",
            Owner = this,
            Width = 780,
            Height = 560,
            MinWidth = 620,
            MinHeight = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new DockPanel { Margin = new Thickness(16) };
        var summary = new TextBlock
        {
            Text = $"{rows.Count} candidates  ·  baseline H₀ {vm.State.Truss.BaselineHeight:F4} {vm.LengthUnit}  ·  {vm.State.Truss.Reference} chord fixed  ·  select a row to preview it",
            Foreground = (Brush)FindResource("Accent"),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(summary, Dock.Top);
        panel.Children.Add(summary);

        var close = new Button { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, MinHeight = 28, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 10, 0, 0) };
        close.Click += (_, _) => dialog.Close();
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(close);

        var num = (Style)FindResource("NumericCell");
        var grid = new DataGrid { ItemsSource = rows, IsReadOnly = true, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single, FrozenColumnCount = 2 };
        grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(Domain.TrussCandidate.Index)), Width = new DataGridLength(.5, DataGridLengthUnitType.Star), MinWidth = 38, ElementStyle = num });
        grid.Columns.Add(new DataGridTextColumn { Header = "Height (m)", Binding = new Binding(nameof(Domain.TrussCandidate.Height)) { StringFormat = "F3" }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 82, ElementStyle = num });
        grid.Columns.Add(new DataGridTextColumn { Header = "ΔH (m)", Binding = new Binding(nameof(Domain.TrussCandidate.DeltaH)) { StringFormat = "+0.000;-0.000;0.000" }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 76, ElementStyle = num });
        grid.Columns.Add(new DataGridTextColumn { Header = "Reference", Binding = new Binding(nameof(Domain.TrussCandidate.Reference)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 78 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Top ΔZ", Binding = new Binding(nameof(Domain.TrussCandidate.TopDz)) { StringFormat = "+0.000;-0.000;0.000" }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 72, ElementStyle = num });
        grid.Columns.Add(new DataGridTextColumn { Header = "Mid ΔZ", Binding = new Binding(nameof(Domain.TrussCandidate.MidDz)) { StringFormat = "+0.000;-0.000;0.000" }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 72, ElementStyle = num });
        grid.Columns.Add(new DataGridTextColumn { Header = "Bot ΔZ", Binding = new Binding(nameof(Domain.TrussCandidate.BotDz)) { StringFormat = "+0.000;-0.000;0.000" }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 72, ElementStyle = num });
        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedItem is Domain.TrussCandidate c) vm.State.Truss.CurrentHeight = c.Height;
        };
        panel.Children.Add(grid);
        dialog.Content = panel;
        dialog.ShowDialog();
    }

    private static bool HasErrors(DependencyObject item)
    {
        if (Validation.GetHasError(item)) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
            if (HasErrors(VisualTreeHelper.GetChild(item, i))) return true;
        return false;
    }

    private void ShowHelp(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "1. Connect to a running SAP2000 model from the top bar and load geometry.\n" +
        "2. Tab 01: select Point Objects in SAP2000, add them as applied points, and define loading criteria.\n" +
        "3. Tab 02: select level chord groups, capture a baseline and preview height changes.\n" +
        "4. Tab 03: select SAP cable objects and define PRETENSION target-force candidates.\n" +
        "5. Tab 04: define deflection points, member-force groups, foundation reactions, and monitored cables/result case. No SAP group write occurs while editing.\n" +
        "6. Tab 05: select study rows and 1-3 total SAP2000 instances. Run first prepares [O]- groups on the parent baseline and saves it, then workers create children from that prepared parent.\n" +
        "7. Each worker is sequential; workers run in parallel and reopen the immutable baseline before every candidate.\n\n" +
        "Viewer: mouse wheel zooms, drag pans, double-click or Home fits.\n" +
        "Each child runs the load cases enabled in SAP2000 and is saved again before verification; results are not extracted yet.\n" +
        "Existing candidate outputs are never overwritten.",
        "COT VCF tools - Help", MessageBoxButton.OK, MessageBoxImage.Information);
}
