using Mcotis.Desktop.Application;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Mcotis.Desktop.Presentation;

public partial class MainWindow : Window
{
    /// <summary>Edit → TemplateBuilding… (M8, one window): the building the form generates becomes the main window's model,
    /// in the 3D view; the model it replaces is offered to be saved first.</summary>
    private void TemplateBuilding_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var form = new TemplateBuildingWindow { Owner = this };
        if (form.ShowDialog() != true || form.CreatedSession is not { } session || !vm.MayReplaceModel()) return;
        try
        {
            vm.OpenNativeDocument(session.Document, "TemplateBuilding", null, LegacyNativeDocumentMigration.Source.Generated("TemplateBuilding"));
            SharedViewer.ShowPlane("3D");
            SharedViewer.FitView();
        }
        catch (InvalidOperationException ex) { MessageBox.Show(this, ex.Message, "TemplateBuilding", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    /// <summary>The question "Save changes to …?", Yes / No / Cancel — a check answers it in its place.</summary>
    internal static Func<Window, string, MessageBoxResult> AskSaveChanges { get; set; } = (owner, file) =>
        MessageBox.Show(owner, $"Save changes to {file}?", "Mcotis", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);

    /// <summary>The "Save changes?" prompt (M8): Yes saves (asking for a file when the model has none), No drops the
    /// changes, Cancel keeps the model open.</summary>
    private bool ConfirmDiscardChanges(MainViewModel vm)
    {
        switch (AskSaveChanges(this, vm.CurrentFileName))
        {
            case MessageBoxResult.No: return true;
            case MessageBoxResult.Yes:
                var saving = vm.SaveBeforeReplaceAsync();
                if (saving.IsCompleted) return saving.Result;
                MessageBox.Show(this, "The model is still being saved. Try again when the save has finished.", "Mcotis", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            default: return false;
        }
    }
    private void AnalysisOptions_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            new AnalysisOptionsWindow(vm.PlanarAnalysisSettings, vm.ApplyPlanarAnalysisSettings, planar: true, editable: vm.CanEditFemModel,
                cables: vm.HasCableMembers) { Owner = this }.ShowDialog();
    }
    // Remembers the user's dragged split so it survives visits to the full-width tabs.
    private GridLength paramsColumnWidth = new(2, GridUnitType.Star);
    private GridLength viewerColumnWidth = new(3, GridUnitType.Star);
    // The left pane's (x) hides it so the model view takes the full width; any menu item opens it again.
    private bool paneClosed;

    public MainWindow()
    {
        InitializeComponent();
        InitializeGeometryEditing();

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
                vm.ConfirmDiscardChanges = () => ConfirmDiscardChanges(vm);
                vm.FemAnalysisStarted += OpenFemMonitor;
                // Run opens Set Load Cases to Run first; the analysis starts on Run Now
                vm.ChooseFemLoadCases = () => new RunLoadCasesWindow(vm) { Owner = this }.ShowDialog() == true;
            }
        };
        Loaded += (_, _) => ApplyViewerLayout();
        // closing with changes not saved: the same prompt as for opening another model
        Closing += (_, e) => { if (DataContext is MainViewModel vm && !vm.MayReplaceModel()) e.Cancel = true; };
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

    // Tabs 01-05 are archived. The shared model viewer now belongs to the active FEM workspace;
    // the remaining report/reference pages use the full width.
    private void WorkflowTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, WorkflowTabs)) ApplyViewerLayout();
    }

    private void ApplyViewerLayout()
    {
        if (ParamsColumn is null) return;
        bool showViewer = WorkflowTabs.SelectedIndex == 7;
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

    private bool CommitEditors()
    {
        bool valid = FemPatternRowsGrid.CommitEdit(DataGridEditingUnit.Cell, true) &&
            FemPatternRowsGrid.CommitEdit(DataGridEditingUnit.Row, true) &&
            FemCaseRowsGrid.CommitEdit(DataGridEditingUnit.Cell, true) &&
            FemCaseRowsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        bool hiddenErrors = WorkflowTabs.Items.OfType<TabItem>().Any(tab => tab.Content is DependencyObject content && HasErrors(content));
        if (valid && !HasErrors(this) && !hiddenErrors) return true;
        MessageBox.Show(this, "Correct the highlighted numeric inputs before continuing.", "Review inputs", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private static bool HasErrors(DependencyObject item)
    {
        if (Validation.GetHasError(item)) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
            if (HasErrors(VisualTreeHelper.GetChild(item, i))) return true;
        return false;
    }

    // ---- Menu bar: Define / Assign / Run / Display open an FEM pane; Result shows an output ----
    private static int MenuTag(object sender) => sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var value) ? value : -1;

    // Define → Load Cases: Default Setup / SAP Cases replace the table, so they ask first when it holds something else
    private void DefaultLoadCases_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.FemCaseRows.Count > 0 && MessageBox.Show(this,
                "Replace the load cases and combinations with the default setup?\n\n" +
                "Pretension (cable target forces + self-weight) first, then SLS and ULS continuing from it with the assigned model loads.",
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

    private void ForceButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is FrameworkElement { Tag: string tag } &&
            Enum.TryParse<FemResultMode>(tag, out var mode))
            vm.ShowForces(mode);
    }

    private void ClosePane_Click(object sender, RoutedEventArgs e)
    {
        paneClosed = true;
        ApplyViewerLayout();
    }

    /// <summary>Hides the left pane (startup: the model view opens on its own; any menu item opens the pane).</summary>
    /// <summary>How the window opens (M9): the model view alone, in 3D — the tied arch system the app starts with is
    /// analysed in Full 3D, and shown so.</summary>
    internal void ShowStartupView()
    {
        ClosePane();
        SharedViewer.ShowPlane("3D");
    }

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

    /// <summary>Assign → Joint → Restraints: the restraint of the joints selected on the model view.</summary>
    private void AssignRestraints_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var joints = SharedViewer.SelectedJoints.Where(vm.State.Baseline.Points.ContainsKey).ToArray();
        if (joints.Length == 0)
        {
            MessageBox.Show(this, "Select one or more joints on the model view first (click them, or drag a window), then choose Assign → Joint → Restraints.",
                "Assign Joint Restraints", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new JointRestraintWindow(vm, joints) { Owner = this }.ShowDialog();
    }

    /// <summary>Assign → Joint → Masses… (M8): the native workspace's Joint Mass form, on the joints selected on the model view.</summary>
    private void AssignJointMass_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var document = vm.NativeDocumentSession.Document;
        var names = LegacyNames.Of(document);
        var joints = SharedViewer.SelectedJoints.Select(name => names.Point(name)).Where(id => id is not null).Select(id => id!.Value).ToArray();
        if (joints.Length == 0)
        {
            MessageBox.Show(this, "Select one or more joints on the model view first (click them, or drag a window), then choose Assign → Joint → Masses.",
                "Assign Joint Masses", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var form = new NativeJointMassWindow(document, joints) { Owner = this };
        if (form.ShowDialog() != true || form.Definition is not { } mass) return;
        try
        {
            vm.AssignJointMass(form.AppliesTo.Select(id => names[id]).ToArray(),
                new Mcotis.Desktop.Configuration.FemJointMassDto("", mass.UX, mass.UY, mass.UZ, mass.RX, mass.RY, mass.RZ));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Mcotis.Core.DocumentException)
        {
            MessageBox.Show(this, ex.Message, "Assign Joint Masses", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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

    /// <summary>Assign → Frame → Frame Load…: SAP2000's Frame Distributed Loads form on the frames selected on the model view
    /// (docs/loads/FRAME_DISTRIBUTED_LOAD_PLAN.md). Cables in the selection are left out: their loads are not frame loads.</summary>
    private void AssignFrameLoad_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var frames = vm.FemSectionProperties.Where(property => !property.IsCable)
            .Select(property => property.MemberName).ToHashSet(StringComparer.Ordinal);
        var selected = SharedViewer.SelectedMembers.ToArray();
        var chosen = selected.Where(frames.Contains).ToArray();
        if (chosen.Length == 0)
        {
            MessageBox.Show(this, "Select one or more frames on the model view first, then choose Assign → Frame → Frame Load.",
                "Assign Frame Distributed Loads", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var cables = vm.FemSectionProperties.Count(property => property.IsCable && selected.Contains(property.MemberName, StringComparer.Ordinal));
        if (cables > 0)
            MessageBox.Show(this, $"{cables} cable{(cables == 1 ? " is" : "s are")} left out: a frame load goes on frames only.",
                "Assign Frame Distributed Loads", MessageBoxButton.OK, MessageBoxImage.Information);
        new AssignFrameLoadWindow(vm, chosen) { Owner = this }.ShowDialog();
    }

    // File → Import From SAP2000: the active SAP2000 model, complete, as the working model (read-only)
    private async void ImportFromSap_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanImportFromSap) return;
        if (vm.HasWorkingModel && MessageBox.Show(this,
                "Import the model open in SAP2000? It replaces the current Mcotis working model, including its geometry, assignments, loads and cases (save the configuration first to keep it)." +
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
                "Import the model open in ETABS? It replaces the current Mcotis working model, including its geometry, assignments, loads and cases (save the configuration first to keep it)." +
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
                "Load the TAS model (CoT 120 m composite arch deck)? It replaces the current Mcotis working model, including its geometry, assignments, loads and cases (save the configuration first to keep it).",
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
                   $"{Environment.NewLine}{Environment.NewLine}{vm.DeleteConsequences(joints, SharedViewer.SelectedMembers)}" +
                   $"{Environment.NewLine}{Environment.NewLine}FEM results are cleared. Edit → Undo (Ctrl+Z) restores everything deleted; SAP2000 is not changed.";
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

    // Select tool: always returns to selecting (clicking it while on must not switch to pan); a drawing tool goes off too
    private void SelectTool_Click(object sender, RoutedEventArgs e)
    {
        SharedViewer.PanMode = false;
        if (DataContext is MainViewModel vm) vm.DrawingTool = Controls.DrawingTool.None;
    }

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
            DataContext is MainViewModel { ShowSharedViewer: true } selecting)
        {
            SharedViewer.PanMode = false;
            selecting.DrawingTool = Controls.DrawingTool.None;
            e.Handled = true;
            return;
        }
        // Ctrl+W: Display Options (not while typing in a field); Ctrl+Page Up / Down: the next / previous plane
        if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control &&
            System.Windows.Input.Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase && DataContext is MainViewModel { HasUsableModelGeometry: true } viewing)
        {
            if (e.Key == System.Windows.Input.Key.W) { ViewOptions_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
            if (e.Key is System.Windows.Input.Key.PageUp or System.Windows.Input.Key.PageDown)
            {
                viewing.StepViewPlane(e.Key == System.Windows.Input.Key.PageUp ? 1 : -1);
                e.Handled = true;
                return;
            }
        }
        // Esc: a member or move half made on the model view is dropped first (the tool stays on)
        if (e.Key == System.Windows.Input.Key.Escape && System.Windows.Input.Keyboard.FocusedElement is not (System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.ComboBox) &&
            SharedViewer.CancelToolInteraction())
        {
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
        if (DataContext is not MainViewModel vm || !vm.CanEditVcfGeometry) return;
        if (vm.NeedsVcfModelReplacement)
        {
            const string title = "VCF System";
            if (MessageBox.Show(this,
                    "This model is not a VCF system. Replace it with the built-in VCF model?\n\n" +
                    "The current geometry, assignments, loads and cases in Mcotis will be replaced. " +
                    "Save the current configuration first if you need to keep it. SAP2000 will not be changed.",
                    title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            SharedViewer.ClearSelection();
            vm.ReplaceCurrentWithVcfModel();
            if (vm.NeedsVcfModelReplacement || !vm.CanEditVcfGeometry)
            {
                MessageBox.Show(this, "The built-in VCF model could not be loaded. The VCF editor was not opened.\n\n" + vm.Message,
                    title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
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
        if (DataContext is not MainViewModel { CanOptimizeVcf: true } vm) return;   // greyed for any other system
        var dialog = new OptimizationVcfWindow(vm) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } settings) return;
        if (!dialog.RunRequested) { vm.SetVcfOptimization(settings); return; }
        OptimizationResults_Click(sender, e);
        _ = vm.RunVcfOptimizationAsync(settings);
    }

    // Optimization → Optimization TAS: ranges of the tied arch system's inputs and candidate sections; OK keeps them, Run runs them
    private void OptimizationTas_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { CanOptimizeTas: true } vm) return;   // greyed for any other system
        var dialog = new OptimizationTasWindow(vm) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } settings) return;
        if (!dialog.RunRequested) { vm.SetTasOptimization(settings); return; }
        OptimizationResults_Click(sender, e);
        _ = vm.RunTasOptimizationAsync(settings);
    }

    private void CancelOptimization_Click(object sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.CancelVcfOptimization();

    // the Optimization menu shows the model as last edited: the edit still waiting for the autosave is written first, so
    // the generator validates the model the user sees
    private void OptimizationMenu_Opened(object sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.FlushFemWorkingSession();

    // Edit → Re-apply Generator: says what it overwrites, then rebuilds the system from its parameters (one Undo step)
    private async void ReapplyGenerator_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { CanReapplyGenerator: true } vm) return;
        vm.FlushFemWorkingSession();
        if (MessageBox.Show(this, vm.ReapplyGeneratorText(), "Re-apply Generator", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await vm.ReapplyGeneratorAsync();
    }

    // Edit → Detach from Generator: the model becomes an ordinary model (one Undo step)
    private void DetachGenerator_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { CanDetachGenerator: true } vm) return;
        vm.FlushFemWorkingSession();
        vm.DetachGenerator();
    }

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

    // Optimization → Compare with SAP2000: Mcotis and SAP2000 member forces side by side, one instance brought forward on repeat
    private SapCompareWindow? sapCompareWindow;
    private void CompareWithSap_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sapCompareWindow is null)
        {
            sapCompareWindow = new SapCompareWindow(vm) { Owner = this };
            sapCompareWindow.Closed += (_, _) => sapCompareWindow = null;
            sapCompareWindow.Show();
        }
        if (sapCompareWindow.WindowState == WindowState.Minimized) sapCompareWindow.WindowState = WindowState.Normal;
        sapCompareWindow.Activate();
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
            showResultsWindow = new ShowResultsWindow(vm, cableFit => { vm.FemCableFitView = cableFit; OpenFemResultsClick(this, new RoutedEventArgs()); }, kind, SharedViewer) { Owner = this };
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
        // Run Design runs inside the dialog and shows its results in the dialog's Design tab, so everything stays in one window
        IReadOnlyList<Mcotis.Desktop.Application.Design.ColumnMemberDesign> RunDesign(Mcotis.Desktop.Domain.ColumnDesignBasis basis)
        {
            vm.SetDesignBasis(basis);
            if (vm.DesignRunId is null)
                throw new InvalidOperationException("There are no FEM results to design with. Run the FEM analysis (Run → Run FEM Analysis) first; the design basis is kept.");
            return vm.RunColumnDesign(basis);
        }
        new DesignReferenceWindow(vm.DesignBasis(type), vm.DesignMemberOptions(type), vm.DesignCaseNames(), vm.DesignDefaultCases(),
            vm.DesignRunId, member => vm.DesignSectionDefaults(type, member), SharedViewer.SelectedMembers,
            (members, cases) => vm.DesignMemberInputs(type, members, cases), vm.Materials.ToArray(),
            vm.FemResultStateText, vm.FemResultState == Mcotis.Application.ResultState.Stale, RunDesign) { Owner = this }.ShowDialog();
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

    private ModalResultsWindow? modalResultsWindow;
    /// <summary>Result → Modal Results: one window, brought forward on repeat.</summary>
    private void ModalResults_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.HasFemModalResults) return;
        if (modalResultsWindow is null)
        {
            modalResultsWindow = new ModalResultsWindow(vm) { Owner = this };
            modalResultsWindow.Closed += (_, _) => modalResultsWindow = null;
            modalResultsWindow.Show();
        }
        if (modalResultsWindow.WindowState == WindowState.Minimized) modalResultsWindow.WindowState = WindowState.Normal;
        modalResultsWindow.Activate();
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
