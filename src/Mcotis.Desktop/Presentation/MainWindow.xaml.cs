using Mcotis.Desktop.Application;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Mcotis.Desktop.Presentation;

public partial class MainWindow : Window
{
    private void TemplateBuilding_Click(object sender, RoutedEventArgs e) => NativeModelWindow.CreateTemplate(this);
    private void OpenNativeModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Mcotis native model (*.mcotis)|*.mcotis" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var session = new Mcotis.Application.DocumentSession();
            new Mcotis.Application.DocumentLifecycle(session, new Mcotis.Persistence.NativeDocumentStore()).Open(dialog.FileName);
            new NativeModelWindow(session).Show();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open native model", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void AnalysisOptions_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            new AnalysisOptionsWindow(vm.PlanarAnalysisSettings, vm.ApplyPlanarAnalysisSettings, planar: true, editable: vm.CanEditFemModel) { Owner = this }.ShowDialog();
    }
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
            if (e.OldValue is MainViewModel old) { old.FemAnalysisStarted -= OpenFemMonitor; old.ChooseFemLoadCases = null; }
            if (DataContext is MainViewModel vm)
            {
                vm.CommitEditors = CommitEditors;
                vm.FemAnalysisStarted += OpenFemMonitor;
                // Run opens Set Load Cases to Run first; the analysis starts on Run Now
                vm.ChooseFemLoadCases = () => new RunLoadCasesWindow(vm) { Owner = this }.ShowDialog() == true;
            }
        };
        Loaded += (_, _) => ApplyViewerLayout();
        // double-click a member: the same form as a right-click, opened on its assignments
        SharedViewer.MemberDoubleClicked += (_, name) =>
        {
            if (DataContext is not MainViewModel vm || SharedViewer.Model.Members.FirstOrDefault(item => item.Name == name) is not { } member) return;
            new ObjectInformationWindow(vm, member, SharedViewer.Model, openAssignments: true) { Owner = this }.ShowDialog();
        };
        // right-click a member or a point (2D or 3D): its information, as ETABS shows it
        SharedViewer.MemberInformationRequested += (_, name) =>
        {
            if (DataContext is not MainViewModel vm || SharedViewer.Model.Members.FirstOrDefault(item => item.Name == name) is not { } member) return;
            new ObjectInformationWindow(vm, member, SharedViewer.Model) { Owner = this }.ShowDialog();
        };
        SharedViewer.JointInformationRequested += (_, name) =>
        {
            if (DataContext is not MainViewModel vm || !SharedViewer.Model.Points.TryGetValue(name, out var point)) return;
            new ObjectInformationWindow(vm, point, SharedViewer.Model) { Owner = this }.ShowDialog();
        };
        // the status bar counts what is selected, in the 2D view or the 3D view; a new model has its own objects
        SharedViewer.SelectionChanged += (_, _) => UpdateSelectionStatus();
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Controls.Structural2DViewer.ModelProperty, typeof(Controls.Structural2DViewer))
            .AddValueChanged(SharedViewer, (_, _) => UpdateSelectionStatus());
    }

    /// <summary>The status bar's count of the selected objects by kind. Mcotis has no shell objects yet, so that count is 0.</summary>
    private void UpdateSelectionStatus()
    {
        var frameNames = SharedViewer.Model.Members.Where(member => member.Kind == Domain.MemberKind.Frame).Select(member => member.Name).ToHashSet(StringComparer.Ordinal);
        var frames = SharedViewer.SelectedMembers.Count(frameNames.Contains);
        var cables = SharedViewer.SelectedMembers.Count - frames;
        SelectionStatus.Text = SelectionText(frames, cables, SharedViewer.SelectedJoints.Count, shells: 0);
    }

    internal static string SelectionText(int frames, int cables, int points, int shells)
    {
        static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
        return $"Selected: {Count(frames, "Frame")} · {Count(cables, "Cable")} · {Count(points, "Point")} · {Count(shells, "Shell")}";
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
            Cables.CommitEdit(DataGridEditingUnit.Row, true) &&
            FemPatternRowsGrid.CommitEdit(DataGridEditingUnit.Cell, true) &&
            FemPatternRowsGrid.CommitEdit(DataGridEditingUnit.Row, true) &&
            FemCaseRowsGrid.CommitEdit(DataGridEditingUnit.Cell, true) &&
            FemCaseRowsGrid.CommitEdit(DataGridEditingUnit.Row, true);
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

    private void MainMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is MenuItem item && ItemsControl.ItemsControlFromItemContainer(item) == MainMenu)
            SapConnectionControls.Visibility = Equals(item.Header, "_MB") ? Visibility.Visible : Visibility.Collapsed;
    }

    // Define → Load Cases: Default Setup / SAP Cases replace the table, so they ask first when it holds something else
    private void DefaultLoadCases_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.FemCaseRows.Count > 0 && MessageBox.Show(this,
                "Replace the load cases and combinations with the default setup?\n\n" +
                "Pretension (cable target forces + self-weight) first, then SLS and ULS continuing from it with the Tab 01 loads.",
                "Default Load Cases", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        vm.ApplyDefaultFemLoadCases();
    }

    private void SapLoadCases_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.FemCaseRows.Count > 0 && MessageBox.Show(this,
                "Replace the load cases and combinations with the ones saved from SAP2000?",
                "SAP2000 Load Cases", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        vm.ApplySapFemLoadCases();
    }

    // + Add and Modify… open Load Case Data, where the loads of a case are chosen from the load patterns
    private void AddLoadCase_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.NewFemLoadCase() is { } proposed) EditLoadCase(vm, proposed);
    }

    // Modify… acts on the selected case; the … button of a row on that row
    private void ModifyLoadCase_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !CommitEditors()) return;
        if (((sender as FrameworkElement)?.DataContext as FemCaseRow ?? vm.SelectedFemCaseRow) is { Supported: true } row) EditLoadCase(vm, row);
    }

    private void EditLoadCase(MainViewModel vm, FemCaseRow row)
    {
        var dialog = new LoadCaseWindow(vm, row) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Saved is not { } saved) return;
        FemCaseRowsGrid.UpdateLayout();
        FemCaseRowsGrid.ScrollIntoView(saved);
    }

    private void OpenFemHtml_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.FemRuns.LastOrDefault() is not { } run) return;
        try
        {
            var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mcotis", "FemReports");
            System.IO.Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, $"FEM_Results_{Guid.NewGuid():N}.html");
            Mcotis.Desktop.Infrastructure.AtomicFile.WriteAllText(path,
                Mcotis.Desktop.Application.Results.FemRunReport.BuildHtml(run, vm.State.Model?.Name ?? "Model", vm.SelectedFemResultName, vm.PrintSettings, vm.State.Baseline));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "FEM Results", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void RemoveLoadCase_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.RemoveFemLoadCase(vm.SelectedFemCaseRow);
    }

    // Define → Load Patterns: the name and type typed beside + Add become a pattern of the model
    private void AddLoadPattern_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.AddFemLoadPattern(NewPatternName.Text, NewPatternType.Text) is not { } row) return;
        NewPatternName.Clear();
        FemPatternRowsGrid.SelectedItem = row;
        FemPatternRowsGrid.ScrollIntoView(row);
    }

    private void RemoveLoadPattern_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.RemoveFemLoadPattern(FemPatternRowsGrid.SelectedItem as FemPatternRow);
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

    // Display → Show Undeformed Shape (toolbar frame icon, F4, as SAP2000): clear the result overlay, keep the results
    private void ShowUndeformed_Click(object sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.ShowUndeformedShape();

    // Clear Selection (toolbar) — the model view's selected joints and members
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => SharedViewer.ClearSelection();

    // Assign → Frame / Cable → Sections: assign a library section to the members selected on the model view
    private void AssignSections_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var cable = (sender as FrameworkElement)?.Tag as string == "Cable";
        var kinds = vm.FemSectionProperties.ToDictionary(property => property.MemberName, property => property.IsCable, StringComparer.Ordinal);
        var chosen = SharedViewer.SelectedMembers.Where(name => kinds.TryGetValue(name, out var isCable) && isCable == cable).ToArray();
        if (chosen.Length == 0)
        {
            MessageBox.Show(this, $"Select one or more {(cable ? "cables" : "frames")} on the model view first (click them, or drag a window), then choose Assign → {(cable ? "Cable → Cable" : "Frame → Frame")} Sections.",
                cable ? "Assign Cable Sections" : "Assign Frame Sections", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new AssignSectionWindow(vm, chosen, cable) { Owner = this }.ShowDialog();
    }

    private void AssignFrameAxes_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var frames = vm.FemSectionProperties.Where(property => !property.IsCable)
            .Select(property => property.MemberName).ToHashSet(StringComparer.Ordinal);
        var chosen = SharedViewer.SelectedMembers.Where(frames.Contains).ToArray();
        if (chosen.Length == 0)
        {
            MessageBox.Show(this, "Select one or more frames on the model view first, then choose Assign → Frame → Local Axes.",
                "Assign Frame Local Axes", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new AssignFrameAxisWindow(vm, chosen) { Owner = this }.ShowDialog();
    }

    // File → Import From SAP2000: the active SAP2000 model, complete, as the working model (read-only)
    private async void ImportFromSap_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanImportFromSap) return;
        if (vm.HasWorkingModel && MessageBox.Show(this,
                "Import the model open in SAP2000? It replaces the current model and its Tabs 01–05 in Mcotis (save the configuration first to keep it)." +
                $"{Environment.NewLine}{Environment.NewLine}SAP2000 is only read, never written.",
                "Import From SAP2000", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK) != MessageBoxResult.OK) return;
        SharedViewer.ClearSelection();
        try
        {
            var summary = await vm.ImportFromSapAsync();
            MessageBox.Show(this, string.Join(Environment.NewLine, summary.Lines.Select(line => "• " + line)), "Import From SAP2000",
                MessageBoxButton.OK, summary.ReadyToRun ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The import could not be completed: " + ex.Message +
                $"{Environment.NewLine}{Environment.NewLine}Open the model in SAP2000 (unlocked or locked, analysed or not) and try again.",
                "Import From SAP2000", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // File → Import From ETABS: the model open in ETABS as an offline model ready to run (read-only); without ETABS
    // running, an .e2k file ETABS wrote
    private async void ImportFromEtabs_Click(object sender, RoutedEventArgs e)
    {
        const string title = "Import From ETABS";
        if (DataContext is not MainViewModel vm || !vm.CanImportFromEtabs) return;
        if (vm.HasWorkingModel && MessageBox.Show(this,
                "Import the model open in ETABS? It replaces the current model and its Tabs 01–05 in Mcotis (save the configuration first to keep it)." +
                $"{Environment.NewLine}{Environment.NewLine}ETABS is only read, never written.",
                title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK) != MessageBoxResult.OK) return;
        SharedViewer.ClearSelection();
        try
        {
            EtabsImportSummary summary;
            try { summary = await vm.ImportFromEtabsAsync(); }
            catch (Mcotis.Desktop.Application.Etabs.EtabsNotRunningException ex)
            {
                if (MessageBox.Show(this, ex.Message + $"{Environment.NewLine}{Environment.NewLine}Import an .e2k file ETABS wrote instead " +
                        "(ETABS: File → Export → ETABS .e2k Text File)?", title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "ETABS text file (*.e2k)|*.e2k|All files (*.*)|*.*", CheckFileExists = true, Title = title };
                if (dialog.ShowDialog(this) != true) return;
                summary = vm.ImportFromE2kFile(dialog.FileName);
            }
            MessageBox.Show(this, string.Join(Environment.NewLine, summary.Lines.Select(line => "• " + line)), title,
                MessageBoxButton.OK, summary.ReadyToRun ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The import could not be completed: " + ex.Message +
                $"{Environment.NewLine}{Environment.NewLine}ETABS was only read, never written.",
                title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // File → Load TAS…: the built-in CoT 120 m composite arch deck, imported as Import From ETABS imports its .e2k
    private void LoadTas_Click(object sender, RoutedEventArgs e)
    {
        const string title = "Load TAS";
        if (DataContext is not MainViewModel vm || !vm.CanImportFromEtabs) return;
        if (vm.HasWorkingModel && MessageBox.Show(this,
                "Load the TAS model (CoT 120 m composite arch deck)? It replaces the current model and its Tabs 01–05 in Mcotis (save the configuration first to keep it).",
                title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK) != MessageBoxResult.OK) return;
        SharedViewer.ClearSelection();
        try
        {
            // the model shows in the viewer and the status bar says it is ready; the summary only when something is missing
            var summary = vm.LoadTasModel();
            if (!summary.ReadyToRun)
                MessageBox.Show(this, string.Join(Environment.NewLine, summary.Lines.Select(line => "• " + line)), title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The TAS model could not be loaded: " + ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // File → Export to ETABS: preview the changes in the open ETABS model, confirm, write (not saved)
    private async void ExportToEtabs_Click(object sender, RoutedEventArgs e)
    {
        const string title = "Export to ETABS";
        if (DataContext is not MainViewModel vm) return;
        if (!vm.CanExportToEtabs)
        {
            MessageBox.Show(this, "Open or import a model with geometry and member sections first.", title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        EtabsExportPreview preview;
        try { preview = await vm.PreviewEtabsExportAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The export cannot be made: " + ex.Message + $"{Environment.NewLine}{Environment.NewLine}Nothing was written to ETABS.",
                title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var text = "Write the Mcotis model into the model open in ETABS?" + Environment.NewLine + Environment.NewLine +
                   string.Join(Environment.NewLine, preview.Plan.Lines.Select(line => "• " + line)) + Environment.NewLine + Environment.NewLine +
                   "ETABS frames whose ends are not those of a Mcotis member are removed. " +
                   (preview.Plan.Target.IsLocked ? "The model is locked: ETABS deletes its analysis results when it is unlocked. " : "") +
                   "The model is not saved — review it in ETABS and save, or close it without saving to discard the export.";
        if (MessageBox.Show(this, text, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        try
        {
            var report = await vm.ExportToEtabsAsync(preview);
            MessageBox.Show(this, $"Exported to {report.Target.Name}:" + Environment.NewLine + Environment.NewLine +
                                  string.Join(Environment.NewLine, report.Lines.Select(line => "• " + line)) + Environment.NewLine + Environment.NewLine +
                                  "Review the model in ETABS, save it, and run the analysis (Analyze → Run Analysis).",
                title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The export stopped: " + ex.Message + $"{Environment.NewLine}{Environment.NewLine}" +
                                  "Part of the model may already be written. Close the ETABS model without saving to discard the export, then try again.",
                title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // File → Export to SAP2000: preview the changes in the open SAP2000 model, confirm, write (not saved)
    private async void ExportToSap_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (!vm.CanExportToSap)
        {
            MessageBox.Show(this, "Open or import a model with geometry and member sections first.", "Export to SAP2000", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SapExportPreview preview;
        try { preview = await vm.PreviewSapExportAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The export cannot be made: " + ex.Message + $"{Environment.NewLine}{Environment.NewLine}Nothing was written to SAP2000.",
                "Export to SAP2000", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var text = "Write the Mcotis model into the model open in SAP2000?" + Environment.NewLine + Environment.NewLine +
                   string.Join(Environment.NewLine, preview.Plan.Lines.Select(line => "• " + line)) + Environment.NewLine + Environment.NewLine +
                   "SAP2000 members and joints that are not in Mcotis are removed. " +
                   (preview.Target.IsLocked ? "The model is locked: SAP2000 deletes its analysis results when it is unlocked. " : "") +
                   "The model is not saved — review it in SAP2000 and save, or close it without saving to discard the export.";
        if (MessageBox.Show(this, text, "Export to SAP2000", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        try
        {
            var report = await vm.ExportToSapAsync(preview);
            MessageBox.Show(this, $"Exported to {report.ModelPath}:" + Environment.NewLine + Environment.NewLine +
                                  string.Join(Environment.NewLine, report.Lines.Select(line => "• " + line)) + Environment.NewLine + Environment.NewLine +
                                  "Review the model in SAP2000, save it, and run the analysis (Analyze → Run Analysis).",
                "Export to SAP2000", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The export stopped: " + ex.Message + $"{Environment.NewLine}{Environment.NewLine}" +
                                  "Part of the model may already be written. Close the SAP2000 model without saving to discard the export, then try again.",
                "Export to SAP2000", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Edit → Delete (Del): remove the selected joints and members from the working model, after confirming
    private async void DeleteSelection_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanEditFemModel) return;
        var joints = SharedViewer.SelectedJoints.ToArray();
        var members = vm.MembersDeletedWith(SharedViewer.SelectedMembers, joints);
        if (joints.Length + members.Count == 0)
        {
            MessageBox.Show(this, "Select the joints, frames or cables to delete on the model view first (click them, or drag a window), then choose Edit → Delete.",
                "Delete", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var connected = members.Count - SharedViewer.SelectedMembers.Count(members.Contains);
        var text = $"Delete {members.Count} member(s) and {joints.Length} joint(s) from the working model?" +
                   (connected > 0 ? $"{Environment.NewLine}{Environment.NewLine}{connected} of the members are connected to the selected joints." : "") +
                   $"{Environment.NewLine}{Environment.NewLine}FEM results are cleared. There is no undo; SAP2000 is not changed (Connect SAP restores the SAP model).";
        if (MessageBox.Show(this, text, "Delete", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        try
        {
            await vm.DeleteObjectsAsync(SharedViewer.SelectedMembers, joints);
            SharedViewer.ClearSelection();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Delete", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Select tool: always returns to selecting (clicking it while on must not switch to pan)
    private void SelectTool_Click(object sender, RoutedEventArgs e) => SharedViewer.PanMode = false;

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None &&
            DataContext is MainViewModel { HasUsableModelGeometry: true, HasFemResults: true })
        {
            var picker = e.Key switch
            {
                System.Windows.Input.Key.F5 => ShowResultPickerKind.Deflection,
                System.Windows.Input.Key.F6 => ShowResultPickerKind.MemberForces,
                System.Windows.Input.Key.F7 => ShowResultPickerKind.Reactions,
                _ => (ShowResultPickerKind?)null
            };
            if (picker is { } kind) { OpenResultPicker(kind); e.Handled = true; return; }
        }
        if (e.Key == System.Windows.Input.Key.F1 && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
        {
            OpenUserGuide(null);
            e.Handled = true;
            return;
        }
        if (e.Key == System.Windows.Input.Key.F4 && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None &&
            DataContext is MainViewModel { HasWorkingModel: true } vm)
        {
            vm.ShowUndeformedShape();
            e.Handled = true;
            return;
        }
        // Del, as SAP2000: delete the model view's selection (not while typing in a field or list)
        if (e.Key == System.Windows.Input.Key.Delete && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None &&
            System.Windows.Input.Keyboard.FocusedElement is not (System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.ComboBox or System.Windows.Controls.DataGrid or System.Windows.Controls.DataGridCell) &&
            SharedViewer.SelectedMembers.Count + SharedViewer.SelectedJoints.Count > 0)
        {
            DeleteSelection_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        // V: the Select tool, from anywhere in the window (not while typing in a field or a grid, or with a menu open)
        if (e.Key == System.Windows.Input.Key.V && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None &&
            System.Windows.Input.Keyboard.FocusedElement is not (System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.ComboBox or
                System.Windows.Controls.DataGrid or System.Windows.Controls.DataGridCell or MenuItem) &&
            DataContext is MainViewModel { ShowSharedViewer: true })
        {
            SharedViewer.PanMode = false;
            e.Handled = true;
            return;
        }
        // Esc clears the model view's selection (not while typing in a field or list). It no longer changes the tool:
        // Pan stays Pan until V or the Select button.
        if (e.Key == System.Windows.Input.Key.Escape && System.Windows.Input.Keyboard.FocusedElement is not (System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.ComboBox) &&
            SharedViewer.SelectedMembers.Count + SharedViewer.SelectedJoints.Count > 0)
        {
            SharedViewer.ClearSelection();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
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

    // Edit → VCF System: parameters and sections of the VCF system with a live preview
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

    // Edit → Tied Arch System: H1, H2, bays, spacing and the storeys above, with a live preview; Apply rebuilds the TAS model
    private TiedArchWindow? tiedArchWindow;
    private void TiedArch_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (tiedArchWindow is null)
        {
            tiedArchWindow = new TiedArchWindow(vm) { Owner = this };
            tiedArchWindow.Closed += (_, _) => tiedArchWindow = null;
            tiedArchWindow.Show();
        }
        else tiedArchWindow.LoadFromModel();
        if (tiedArchWindow.WindowState == WindowState.Minimized) tiedArchWindow.WindowState = WindowState.Normal;
        tiedArchWindow.Activate();
    }

    // Run → Optimization VCF: ranges (min / max / step) of the VCF inputs and candidate sections; OK keeps them
    private void OptimizationVcf_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var dialog = new OptimizationVcfWindow(vm) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } settings) return;
        if (!dialog.RunRequested) { vm.SetVcfOptimization(settings); return; }
        OptimizationResults_Click(sender, e);
        _ = vm.RunVcfOptimizationAsync(settings);
    }

    // Optimization → Optimization TAS: ranges of the tied arch system's inputs and candidate sections; OK keeps them, Run runs them
    private void OptimizationTas_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var dialog = new OptimizationTasWindow(vm) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } settings) return;
        if (!dialog.RunRequested) { vm.SetTasOptimization(settings); return; }
        OptimizationResults_Click(sender, e);
        _ = vm.RunTasOptimizationAsync(settings);
    }

    private void CancelOptimization_Click(object sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.CancelVcfOptimization();

    // Run → Optimization Results: the table of the last (or running) optimization, one instance brought forward on repeat
    private OptimizationResultsWindow? optimizationResultsWindow;
    private void OptimizationResults_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (optimizationResultsWindow is null)
        {
            optimizationResultsWindow = new OptimizationResultsWindow(vm) { Owner = this };
            optimizationResultsWindow.Closed += (_, _) => optimizationResultsWindow = null;
            optimizationResultsWindow.Show();
        }
        if (optimizationResultsWindow.WindowState == WindowState.Minimized) optimizationResultsWindow.WindowState = WindowState.Normal;
        optimizationResultsWindow.Activate();
    }

    // Toolbar → Show Results: load case / combo and the output to draw on the model view
    private ShowResultsWindow? showResultsWindow;
    private void ShowResults_Click(object sender, RoutedEventArgs e) => OpenResultPicker(ShowResultPickerKind.All);
    private void ShowDeformedShape_Click(object sender, RoutedEventArgs e) => OpenResultPicker(ShowResultPickerKind.Deflection);
    private void ShowMemberForces_Click(object sender, RoutedEventArgs e) => OpenResultPicker(ShowResultPickerKind.MemberForces);
    private void ShowJointReactions_Click(object sender, RoutedEventArgs e) => OpenResultPicker(ShowResultPickerKind.Reactions);

    private void OpenResultPicker(ShowResultPickerKind kind)
    {
        if (DataContext is not MainViewModel vm) return;
        if (showResultsWindow is null)
        {
            showResultsWindow = new ShowResultsWindow(vm, cableFit => { vm.FemCableFitView = cableFit; OpenFemResultsClick(this, new RoutedEventArgs()); }, kind) { Owner = this };
            showResultsWindow.Closed += (_, _) => showResultsWindow = null;
            showResultsWindow.Show();
        }
        else showResultsWindow.Configure(kind);
        if (showResultsWindow.WindowState == WindowState.Minimized) showResultsWindow.WindowState = WindowState.Normal;
        showResultsWindow.Activate();
    }

    private HelpWindow? helpWindow;
    // Design menu: confirm the design basis, section and members, then design them with the FEM forces and show the results.
    private void DesignMenu_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm
            || (sender as FrameworkElement)?.Tag is not string tag
            || !Enum.TryParse<Mcotis.Desktop.Domain.ColumnDesignType>(tag, out var type)) return;
        var dialog = new DesignReferenceWindow(vm.DesignBasis(type), vm.DesignMemberOptions(type), vm.DesignCaseNames(), vm.DesignDefaultCases(),
            vm.DesignRunId, member => vm.DesignSectionDefaults(type, member), SharedViewer.SelectedMembers,
            (members, cases) => vm.DesignMemberInputs(type, members, cases), vm.Materials.ToArray()) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } basis) return;
        vm.SetDesignBasis(basis);
        var title = Mcotis.Desktop.Domain.ColumnDesignCodes.Title(type);
        if (vm.DesignRunId is null)
        {
            MessageBox.Show(this, "There are no FEM results to design with. Run the FEM analysis (Run → Run FEM Analysis) first; the design basis is kept.",
                title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        IReadOnlyList<Mcotis.Desktop.Application.Design.ColumnMemberDesign> results;
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

    private void ShowDevelopmentPlans(object sender, RoutedEventArgs e) => ShowHelpTopic(HelpWindow.Topic.DevelopmentPlans);

    // Help → User Guide (F1): one window, brought to the front when it is open already
    private UserGuideWindow? userGuideWindow;
    private void ShowUserGuide(object sender, RoutedEventArgs e) => OpenUserGuide(null);

    internal void OpenUserGuide(string? article)
    {
        if (userGuideWindow is null)
        {
            userGuideWindow = new UserGuideWindow { Owner = this };
            userGuideWindow.Closed += (_, _) => userGuideWindow = null;
            userGuideWindow.Show();
        }
        if (article is not null) userGuideWindow.Open(article);
        if (userGuideWindow.WindowState == WindowState.Minimized) userGuideWindow.WindowState = WindowState.Normal;
        userGuideWindow.Activate();
    }

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
