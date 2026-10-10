using COT_VCF.Application;
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
    // The left pane's (x) hides it so the model view takes the full width; any menu item opens it again.
    private bool paneClosed;

    public MainWindow()
    {
        InitializeComponent();

        // Default launch size: 90% of the available work area, centred.
        var work = SystemParameters.WorkArea;
        Width = System.Math.Max(MinWidth, work.Width * 0.9);
        Height = System.Math.Max(MinHeight, work.Height * 0.9);
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;

        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.FemAnalysisStarted -= OpenFemMonitor;
            if (DataContext is MainViewModel vm)
            {
                vm.CommitEditors = CommitEditors;
                vm.FemAnalysisStarted += OpenFemMonitor;
            }
        };
        Loaded += (_, _) => ApplyViewerLayout();
    }

    // The model preview is one shared viewer hoisted out of the TabControl. Tabs 01-04 (Loading, Truss,
        // The shared preview appears on tabs 01-04 and 07 FEM; tabs 05-06A and 08 Quantities use full width.
    private void WorkflowTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, WorkflowTabs)) ApplyViewerLayout();
    }

    private void ApplyViewerLayout()
    {
        if (ParamsColumn is null) return;
        bool showViewer = WorkflowTabs.SelectedIndex is >= 0 and <= 3 || WorkflowTabs.SelectedIndex == 7;     // 01-04 and 07 FEM
        if (!showViewer) paneClosed = false;     // a full-width page is the pane itself
        bool showPane = !paneClosed;

        if (ViewerSplitter.Visibility == Visibility.Visible && ParamsColumn.Width.IsStar && ParamsColumn.Width.Value > 0 && ViewerColumn.Width.IsStar)
        {
            paramsColumnWidth = ParamsColumn.Width;
            viewerColumnWidth = ViewerColumn.Width;
        }

        SharedViewerPane.Visibility = showViewer ? Visibility.Visible : Visibility.Collapsed;
        ViewerSplitter.Visibility = showViewer ? Visibility.Visible : Visibility.Collapsed;

        if (showViewer && !showPane)
        {
            ParamsColumn.MinWidth = 0;     // the XAML minimum (150) would keep a strip of the pane
            ParamsColumn.Width = new GridLength(0);
            ViewerSplitterColumn.Width = new GridLength(0);
            ViewerColumn.Width = new GridLength(1, GridUnitType.Star);
            ViewerSplitter.Visibility = Visibility.Collapsed;
        }
        else if (showViewer)
        {
            ParamsColumn.MinWidth = 150;
            ParamsColumn.Width = paramsColumnWidth;
            ViewerSplitterColumn.Width = GridLength.Auto;
            ViewerColumn.Width = viewerColumnWidth;
        }
        else
        {
            ParamsColumn.MinWidth = 150;
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
            Icon = AppIcon.Window,
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
            Icon = AppIcon.Window,
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

        var close = new Button { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
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

    // ---- Menu bar: MB opens a workflow page; Define / Assign / Run / Display open an FEM pane; Result shows an output ----
    private static int MenuTag(object sender) => sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var value) ? value : -1;

    private void MbMenu_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && MenuTag(sender) is >= 0 and var tab) vm.SelectedTab = tab;
        OpenPane();
    }

    private void FemPaneMenu_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || MenuTag(sender) is not (>= 0 and var pane)) return;
        vm.SelectedTab = 7;
        vm.FemPane = pane;
        OpenPane();
    }

    private void FemResultMenu_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not FrameworkElement { Tag: string tag }) return;
        vm.ShowFemResult(Enum.TryParse<FemResultMode>(tag, out var mode) ? mode : null);
        OpenPane();
        if (vm.FemCableFitView) OpenFemResultsClick(sender, e);   // the cable target fit is a table
    }

    // ---- Top-bar tool icons that act on the shared viewer ----
    private void ToolbarFit_Click(object sender, RoutedEventArgs e) => SharedViewer.FitView();

    private void ToolbarZoom_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var factor))
            SharedViewer.ZoomBy(factor);
    }

    private void ToolbarPlane_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string plane }) SharedViewer.ShowPlane(plane);
    }

    private void ClosePane_Click(object sender, RoutedEventArgs e)
    {
        paneClosed = true;
        ApplyViewerLayout();
    }

    /// <summary>Hides the left pane (startup: the model view opens on its own; any menu item opens the pane).</summary>
    internal void ClosePane()
    {
        paneClosed = true;
        ApplyViewerLayout();
    }

    private void OpenPane()
    {
        paneClosed = false;
        ApplyViewerLayout();
    }

    internal bool IsPaneOpen => ParamsColumn.Width.Value > 0;

    // Define → Materials: the material library (modal, like SAP2000's Define Materials)
    private void DefineMaterials_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        new DefineMaterialsWindow(vm) { Owner = this }.ShowDialog();
    }

    // Define → Section Properties → Frame / Tendon / Cable Sections (modal, like SAP2000's Frame Properties)
    private void SectionList_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<SectionListKind>(tag, out var kind)) return;
        new SectionListWindow(vm, kind) { Owner = this }.ShowDialog();
    }

    // Define → Sections: the member-properties table in its own window
    private DefineSectionsWindow? sectionsWindow;
    private void DefineSections_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sectionsWindow is null)
        {
            sectionsWindow = new DefineSectionsWindow(vm) { Owner = this };
            sectionsWindow.Closed += (_, _) => sectionsWindow = null;
            sectionsWindow.Show();
        }
        if (sectionsWindow.WindowState == WindowState.Minimized) sectionsWindow.WindowState = WindowState.Normal;
        sectionsWindow.Activate();
    }

    // Display → View Options: dimensions and FEM overlays in their own window
    private ViewOptionsWindow? viewOptionsWindow;
    private void ViewOptions_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (viewOptionsWindow is null)
        {
            viewOptionsWindow = new ViewOptionsWindow(vm, SharedViewer) { Owner = this };
            viewOptionsWindow.Closed += (_, _) => viewOptionsWindow = null;
            viewOptionsWindow.Show();
        }
        if (viewOptionsWindow.WindowState == WindowState.Minimized) viewOptionsWindow.WindowState = WindowState.Normal;
        viewOptionsWindow.Activate();
    }

    // Edit → VCF Geometry: parameters and sections of the VCF system with a live preview
    private VcfGeometryWindow? vcfGeometryWindow;
    private void VcfGeometry_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vcfGeometryWindow is null)
        {
            vcfGeometryWindow = new VcfGeometryWindow(vm) { Owner = this };
            vcfGeometryWindow.Closed += (_, _) => vcfGeometryWindow = null;
            vcfGeometryWindow.Show();
        }
        else vcfGeometryWindow.LoadFromModel();
        if (vcfGeometryWindow.WindowState == WindowState.Minimized) vcfGeometryWindow.WindowState = WindowState.Normal;
        vcfGeometryWindow.Activate();
    }

    // Run → Optimization VCF: ranges (min / max / step) of the VCF inputs and candidate sections; OK keeps them
    private void OptimizationVcf_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var dialog = new OptimizationVcfWindow(vm) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } settings) vm.SetVcfOptimization(settings);
    }

    // Toolbar → Show Results: load case / combo and the output to draw on the model view
    private ShowResultsWindow? showResultsWindow;
    private void ShowResults_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (showResultsWindow is null)
        {
            showResultsWindow = new ShowResultsWindow(vm, cableFit => { vm.FemCableFitView = cableFit; OpenFemResultsClick(this, new RoutedEventArgs()); }) { Owner = this };
            showResultsWindow.Closed += (_, _) => showResultsWindow = null;
            showResultsWindow.Show();
        }
        if (showResultsWindow.WindowState == WindowState.Minimized) showResultsWindow.WindowState = WindowState.Normal;
        showResultsWindow.Activate();
    }

    private HelpWindow? helpWindow;
    // Design menu: confirm the design basis, section and members, then design them with the FEM forces and show the results.
    private void DesignMenu_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm
            || (sender as FrameworkElement)?.Tag is not string tag
            || !Enum.TryParse<COT_VCF.Domain.ColumnDesignType>(tag, out var type)) return;
        var dialog = new DesignReferenceWindow(vm.DesignBasis(type), vm.DesignMemberOptions(type), vm.DesignCaseNames(), vm.DesignDefaultCases(),
            vm.DesignRunId, member => vm.DesignSectionDefaults(type, member)) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } basis) return;
        vm.SetDesignBasis(basis);
        var title = COT_VCF.Domain.ColumnDesignCodes.Title(type);
        if (vm.DesignRunId is null)
        {
            MessageBox.Show(this, "There are no FEM results to design with. Run the FEM analysis (Run → Run FEM Analysis) first; the design basis is kept.",
                title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        IReadOnlyList<COT_VCF.Application.Design.ColumnMemberDesign> results;
        try { results = vm.RunColumnDesign(basis); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The design could not be completed: " + ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        new DesignResultsWindow(basis, results, vm.DesignRunId) { Owner = this }.Show();
    }

    private void ShowWorkflowHelp(object sender, RoutedEventArgs e) => ShowHelpTopic(HelpWindow.Topic.Workflow);

    private void ShowFemHelp(object sender, RoutedEventArgs e) => ShowHelpTopic(HelpWindow.Topic.FemProcedure);

    private void ShowHelpTopic(HelpWindow.Topic topic)
    {
        if (helpWindow is null)
        {
            helpWindow = new HelpWindow { Owner = this };
            helpWindow.Closed += (_, _) => helpWindow = null;
            helpWindow.Show();
        }
        helpWindow.Select(topic);
        helpWindow.Activate();
    }

    private FemResultsWindow? femResultsWindow;
    private void OpenFemResultsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.SelectedFemRun is null && vm.FemRuns.Count > 0) vm.SelectedFemRun = vm.FemRuns[0];
        if (vm.SelectedFemRun is null) return;
        if (femResultsWindow is null)
        {
            femResultsWindow = new FemResultsWindow(vm) { Owner = this };
            femResultsWindow.Closed += (_, _) => femResultsWindow = null;
            femResultsWindow.Show();
        }
        femResultsWindow.SelectCableFit(vm.FemCableFitView);
        if (femResultsWindow.WindowState == WindowState.Minimized) femResultsWindow.WindowState = WindowState.Normal;
        femResultsWindow.Activate();
    }

    private FemRunMonitorWindow? femMonitor;
    private void OpenFemMonitor(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.PrepareFemMonitor();
        if (femMonitor is null)
        {
            femMonitor = new FemRunMonitorWindow(vm) { Owner = this };
            femMonitor.Closed += (_, _) => femMonitor = null;
            femMonitor.Show();
        }
        else if (femMonitor.WindowState == WindowState.Minimized) femMonitor.WindowState = WindowState.Normal;
        femMonitor.Activate();
    }

    private void OpenFemMonitorClick(object sender, RoutedEventArgs e) => OpenFemMonitor(sender, e);
}
