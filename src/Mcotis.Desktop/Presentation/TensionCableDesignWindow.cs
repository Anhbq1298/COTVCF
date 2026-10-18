using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Design;
using Mcotis.Desktop.Domain;
using Microsoft.Win32;

namespace Mcotis.Desktop.Presentation;

/// <summary>
/// Design → Tension Cable Design: checks each model cable's assigned section and iteratively optimizes strand counts, as well as sizing
/// catalogue stay systems — VSL SSI 2000 strand groups, FATZER full-locked-coil ropes or (unqualified) Utracon PT tendons — with the
/// stay's stiffness, elongation, mass and indicative cost (<see cref="TensionCableDesigner"/>).
/// </summary>
public sealed class TensionCableDesignWindow : Window
{
    public sealed class CableRow(MainViewModel.CableDesignOption option, bool isChecked) : Observable
    {
        private bool selected = isChecked;
        public bool Selected { get => selected; set => Set(ref selected, value); }
        public string Member { get; } = option.Member;
        public string Section { get; } = option.Section;
        public string Length { get; } = option.LengthM > 0 ? option.LengthM.ToString("0.###", CultureInfo.InvariantCulture) : "";
        public string Area { get; } = option.AreaMm2 > 0 ? option.AreaMm2.ToString("#,0", CultureInfo.InvariantCulture) : "";
        public string Capacity { get; } = option.CapacityKN is { } kn ? kn.ToString("#,0", CultureInfo.InvariantCulture) : "";
    }

    public sealed record SectionCheckRow(string Member, string Section, double AreaMm2, double? TensionKN,
        string GoverningCombo, double? CapacityKN, double? Utilization, string Status)
    {
        public string AreaText => AreaMm2 > 0 ? AreaMm2.ToString("#,0.##", CultureInfo.CurrentCulture) : "—";
        public string TensionText => TensionKN is { } tension ? tension.ToString("#,0.##", CultureInfo.CurrentCulture) : "—";
        public string CapacityText => CapacityKN is { } capacity ? capacity.ToString("#,0.##", CultureInfo.CurrentCulture) : "—";
        public string UtilizationText => Utilization is { } utilization ? utilization.ToString("0.00", CultureInfo.CurrentCulture) : "—";
    }

    /// <summary>A row of the Iterate tab: the suggestion of the last iteration and, once applied and re-analysed, the force and UR of the new section.</summary>
    public sealed class IterateRow(CableSectionOptimizationRow row) : Observable
    {
        private double? afterForce, afterUr;
        public CableSectionOptimizationRow Row { get; } = row;
        public string Member => Row.TendonName;
        public string Current => $"{Row.CurrentStrands} × Ø15.7";
        public string CurrentForce => Row.CurrentForceKN.ToString("#,0", CultureInfo.CurrentCulture);
        public string CurrentUr => Row.CurrentUr.ToString("0.00", CultureInfo.CurrentCulture);
        public string Suggested => $"{Row.SuggestedStrands} × Ø15.7";
        public string Product => Row.Product ?? "—";
        public string Case => Row.GoverningCase;
        public string Force => Row.CalculatedForceKN.ToString("#,0", CultureInfo.CurrentCulture);
        public string SuggestedUr => Row.SuggestedUr.ToString("0.00", CultureInfo.CurrentCulture);
        public string Cost => Row.CostSgd is { } cost ? cost.ToString("#,0", CultureInfo.CurrentCulture) : "—";
        public string Status => Row.Status;
        public string AfterForce => afterForce is { } value ? value.ToString("#,0", CultureInfo.CurrentCulture) : "";
        public string AfterUr => afterUr is { } value ? value.ToString("0.00", CultureInfo.CurrentCulture) : "";
        public double? AfterUrValue => afterUr;
        public void SetAfter(double? force, double? ur) { afterForce = force; afterUr = ur; Notify(nameof(AfterForce)); Notify(nameof(AfterUr)); }
    }

    public sealed class RateRow(TensionCableRates.Entry entry, double value) : Observable
    {
        private string text = value.ToString("0.###", CultureInfo.InvariantCulture);
        public TensionCableRates.Entry Entry { get; } = entry;
        public string Label { get; } = entry.Label;
        public string Unit { get; } = entry.Unit;
        public string Value { get => text; set => Set(ref text, value); }
    }

    public sealed class ResultRow(TensionCableMemberDesign design)
    {
        public TensionCableMemberDesign Design { get; } = design;
        public string Member => Design.Member;
        public string Case => Design.Case;
        public string Tension => Design.TensionKN.ToString("#,0", CultureInfo.InvariantCulture);
        public string Length => Design.LengthM > 0 ? Design.LengthM.ToString("0.##", CultureInfo.InvariantCulture) : "";
        public string Assembly => Design.Selected?.Assembly ?? "—";
        public string Strands => Design.Selected is { StrandsPerUnit: > 0 } s ? s.TotalStrands.ToString(CultureInfo.InvariantCulture) : Design.Selected is null ? "" : "rope";
        public string Capacity => Design.Selected is { } s ? s.GroupCapacityKN.ToString("#,0", CultureInfo.InvariantCulture) : "";
        public string Use => Design.Selected is { } s ? s.Utilisation.ToString("0.00", CultureInfo.InvariantCulture) : "";
        public string Elongation => Design.Selected is { } s ? s.ElongationMm.ToString("0.0", CultureInfo.InvariantCulture) : "";
        public string CostPerM => Design.Selected is { } s ? s.CostPerMSgd.ToString("#,0", CultureInfo.InvariantCulture) : "";
        public string Cost => Design.Selected is { } s ? s.CostSgd.ToString("#,0", CultureInfo.InvariantCulture) : "";
        public string Status => Design.Status;
    }

    private readonly MainViewModel viewModel;
    private readonly List<CableRow> cables;
    private readonly List<RateRow> rates;
    private readonly List<DesignReferenceWindow.CheckItem> cases;
    private readonly CheckBox vsl = new() { Content = "VSL SSI 2000 — parallel strand stay, 12 to 187 strands per unit", Margin = new Thickness(0, 0, 0, 4) };
    private readonly CheckBox fatzer = new() { Content = "FATZER full-locked-coil rope — Ø80 to Ø150 mm", Margin = new Thickness(0, 0, 0, 4) };
    private readonly CheckBox utracon = new() { Content = "Utracon / DSI-UPS 37-strand PT tendon — NOT shown to be a qualified exposed stay; a provisional alternative only", Margin = new Thickness(0, 0, 0, 4) };
    private readonly ComboBox screening = new() { MinWidth = 330 };
    private readonly ComboBox priority = new() { MinWidth = 330 };
    private readonly ComboBox forceBasis = new() { MinWidth = 330 };
    private readonly TextBox maxUse = new() { Width = 70 };
    private readonly TextBox maxUnits = new() { Width = 70 };
    private readonly TextBox manualForce = new() { Text = "60", Width = 80 };
    private readonly TextBox manualLength = new() { Text = "50", Width = 80 };
    private readonly TextBox sectionStress = new() { Text = "2160", Width = 90 };
    private readonly TextBox sectionReduction = new() { Text = "50", Width = 90 };
    private readonly DataGrid cableGrid = new() { AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Extended, HeadersVisibility = DataGridHeadersVisibility.Column, CanUserAddRows = false };
    private readonly DataGrid sectionCheckGrid = new() { AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, CanUserAddRows = false };
    private readonly DataGrid rateGrid = new() { AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, CanUserAddRows = false };
    private readonly DataGrid resultGrid = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column, CanUserAddRows = false };
    private readonly TextBox detail = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12 };
    private readonly TextBlock countText = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock sectionCheckSummary = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 560, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
    private readonly TextBox iterateMaximum = new() { Text = "20", Width = 70 };
    private readonly TextBox iterateTolerance = new() { Text = "1.0", Width = 70 };
    private readonly DataGrid iterateGrid = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column, CanUserAddRows = false };
    private readonly DataGrid historyGrid = new() { AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, CanUserAddRows = false };
    private readonly TextBlock iterateSummary = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private readonly Button iterateButton = new() { Content = "Iterate", MinWidth = 90, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10, 3, 10, 3) };
    private readonly Button stopButton = new() { Content = "Stop", MinWidth = 70, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10, 3, 10, 3), IsEnabled = false };
    private readonly Button applyButton = new() { Content = "Apply & Re-analyse", MinWidth = 140, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(10, 3, 10, 3), IsEnabled = false };
    private readonly List<IterateRow> iterateRows = [];
    private readonly System.Collections.ObjectModel.ObservableCollection<CableSectionOptimizationIteration> iterations = [];
    private CableSectionOptimizationRun? iteration;
    private CancellationTokenSource? iterationCancellation;
    private readonly TabControl tabs = new();
    private readonly TabItem designTab = new() { Header = "Design" };
    private readonly Button closeButton = new() { Content = "Cancel", MinWidth = 80, IsCancel = true };
    private readonly HashSet<string> viewerSelected;
    private TensionCableBasis? lastBasis;
    private IReadOnlyList<TensionCableMemberDesign> lastResults = [];

    private static readonly double[] Screens = [0.5, 0.6];

    public TensionCableDesignWindow(MainViewModel viewModel, IReadOnlyList<string> viewerSelection)
    {
        this.viewModel = viewModel;
        viewerSelected = viewerSelection.ToHashSet(StringComparer.Ordinal);
        Title = "Tension Cable Design";
        Icon = AppIcon.Window;
        Width = 1080; Height = 760; MinWidth = 760; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Surface");

        var basis = viewModel.CableDesignBasis;
        var selected = viewerSelection.ToHashSet(StringComparer.Ordinal);
        var options = viewModel.CableDesignOptions();
        var picked = basis.Members.Count > 0 ? basis.Members.ToHashSet(StringComparer.Ordinal) : selected.Count > 0 && options.Any(option => selected.Contains(option.Member)) ? selected : options.Select(option => option.Member).ToHashSet(StringComparer.Ordinal);
        cables = options.Select(option => new CableRow(option, picked.Contains(option.Member))).ToList();
        var defaultCases = basis.LoadCases.Count > 0 ? basis.LoadCases : viewModel.CableDesignDefaultCases();
        cases = viewModel.DesignCaseNames().Select(name => new DesignReferenceWindow.CheckItem(name, "", defaultCases.Contains(name))).ToList();
        rates = TensionCableRates.Entries.Select(entry => new RateRow(entry, entry.Get(basis.Rates))).ToList();

        // For now the stays are VSL SSI 2000 only: FATZER and Utracon are switched off with the Design Basis tab hidden.
        vsl.IsChecked = true;
        fatzer.IsChecked = false;
        utracon.IsChecked = false;
        screening.ItemsSource = new[] { "50 % of the breaking load — reference (VSL admissible load)", "60 % of the breaking load — subject to supplier verification" };
        screening.SelectedIndex = basis.ScreeningFactor > 0.55 ? 1 : 0;
        priority.ItemsSource = new[] { "Lowest estimated installed cost", "Fewest parallel units (fewest anchorages)", "Least steel" };
        priority.SelectedIndex = (int)basis.Priority;
        forceBasis.ItemsSource = new[] { "Service (SLS) forces", "Factored (ULS) forces — a caution is printed", "Unknown / other" };
        forceBasis.SelectedIndex = (int)basis.ForceBasis;
        maxUse.Text = (basis.MaxUtilisation * 100).ToString("0.###", CultureInfo.InvariantCulture);
        maxUnits.Text = basis.MaxUnits.ToString(CultureInfo.InvariantCulture);
        System.Windows.Automation.AutomationProperties.SetName(screening, "Screening level");
        System.Windows.Automation.AutomationProperties.SetName(priority, "Selection priority");
        System.Windows.Automation.AutomationProperties.SetName(forceBasis, "Force basis");
        System.Windows.Automation.AutomationProperties.SetName(maxUse, "Maximum use");
        System.Windows.Automation.AutomationProperties.SetName(maxUnits, "Most units per stay");
        System.Windows.Automation.AutomationProperties.SetName(sectionStress, "Cable check breaking stress MPa");
        System.Windows.Automation.AutomationProperties.SetName(sectionReduction, "Cable check reduction factor percent");
        System.Windows.Automation.AutomationProperties.SetName(cableGrid, "Cables");
        System.Windows.Automation.AutomationProperties.SetName(sectionCheckGrid, "Cable section checks");
        System.Windows.Automation.AutomationProperties.SetName(rateGrid, "Cost rates");
        System.Windows.Automation.AutomationProperties.SetName(resultGrid, "Cable designs");
        System.Windows.Automation.AutomationProperties.SetName(iterateGrid, "Iterated cable sections");
        System.Windows.Automation.AutomationProperties.SetName(historyGrid, "Iteration history");
        System.Windows.Automation.AutomationProperties.SetName(iterateMaximum, "Maximum iterations");
        System.Windows.Automation.AutomationProperties.SetName(iterateTolerance, "Convergence tolerance percent");
        Closing += (_, e) => { if (iterationCancellation is not null) { iterationCancellation.Cancel(); e.Cancel = true; Message("Stopping the iteration…"); } };

        var root = new DockPanel { Margin = new Thickness(10) };
        var banner = new TextBlock { Text = "Tension Cable Design" };
        banner.SetResourceReference(StyleProperty, "BannerTitle");
        banner.Margin = new Thickness(2, 0, 0, 8);
        DockPanel.SetDock(banner, Dock.Top);
        root.Children.Add(banner);

        var runButton = new Button { Content = "Run Design", MinWidth = 90, Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
        runButton.Click += (_, _) => Run();
        runButton.Visibility = Visibility.Collapsed;   // with the Design tab hidden, Iterate and Section Check have their own commands
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { runButton, closeButton } };
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Error");
        bottom.Children.Add(status);
        bottom.Children.Add(buttons);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        // Shown for now: Section Check, Cost Rates and Iterate (VSL catalogue). Design Basis, Cables & Loads, Single Stay and Design
        // are kept but hidden; their settings (the stored basis, every cable, the default load cases) still feed the checks and Iterate.
        tabs.Items.Add(new TabItem { Header = "Section Check", Content = BuildSectionCheckTab() });
        tabs.Items.Add(new TabItem { Header = "Design Basis", Content = BuildBasisTab(), Visibility = Visibility.Collapsed });
        tabs.Items.Add(new TabItem { Header = "Cables & Loads", Content = BuildCablesTab(), Visibility = Visibility.Collapsed });
        tabs.Items.Add(new TabItem { Header = "Cost Rates", Content = BuildRatesTab() });
        tabs.Items.Add(new TabItem { Header = "Single Stay", Content = BuildSingleTab(), Visibility = Visibility.Collapsed });
        tabs.Items.Add(new TabItem { Header = "Iterate", Content = BuildIterateTab() });
        designTab.Visibility = Visibility.Collapsed;
        designTab.Content = BuildResultTab();
        tabs.Items.Add(designTab);
        root.Children.Add(tabs);
        Content = root;

        foreach (var row in cables.Where(row => row.Selected)) cableGrid.SelectedItems.Add(row);
        Message(viewModel.DesignRunId is null ? "There are no FEM results to check or iterate the model's cables with. Run the FEM analysis (Run → Run FEM Analysis) first." : "");
        // the check of the assigned sections is on screen from the start; Check Sections reruns it with other values
        if (viewModel.DesignRunId is not null) RunSectionCheck();
    }

    private static TextBlock Hint(string text, Thickness margin)
    {
        var hint = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin };
        hint.SetResourceReference(StyleProperty, "Hint");
        return hint;
    }

    private static UIElement Field(string label, UIElement control, string? unit = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 8) };
        row.Children.Add(new TextBlock { Text = label, Width = 190, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(control);
        if (unit is not null) row.Children.Add(new TextBlock { Text = unit, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private UIElement BuildSectionCheckTab()
    {
        var root = new DockPanel { Margin = new Thickness(8, 6, 8, 0) };
        var top = new StackPanel();
        sectionCheckSummary.Text = "Check the assigned cable sections against the selected FEM load cases.";
        top.Children.Add(sectionCheckSummary);
        top.Children.Add(Hint("Capacity [kN] = section area [mm²] × breaking stress [MPa] × reduction factor ÷ 1,000. UR = maximum tensile force ÷ section capacity. Area comes from each assigned cable section; force comes from the selected FEM results.", new Thickness(0, 0, 0, 8)));

        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(Field("Breaking stress", sectionStress, "MPa"));
        controls.Children.Add(Field("Reduction factor", sectionReduction, "%"));
        var check = new Button { Content = "Check Sections", MinWidth = 110, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10, 3, 10, 3), IsDefault = true };
        check.Click += (_, _) => RunSectionCheck();
        controls.Children.Add(check);
        top.Children.Add(controls);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        foreach (var (header, path, width) in new[]
        {
            ("Cable", nameof(SectionCheckRow.Member), 100.0), ("Section", nameof(SectionCheckRow.Section), 150.0),
            ("Area [mm²]", nameof(SectionCheckRow.AreaText), 100.0), ("Governing combo", nameof(SectionCheckRow.GoverningCombo), 1.0),
            ("Tension [kN]", nameof(SectionCheckRow.TensionText), 110.0), ("Capacity [kN]", nameof(SectionCheckRow.CapacityText), 110.0),
            ("UR", nameof(SectionCheckRow.UtilizationText), 70.0), ("Status", nameof(SectionCheckRow.Status), 100.0),
        })
            sectionCheckGrid.Columns.Add(new DataGridTextColumn { Header = header, IsReadOnly = true,
                Binding = new System.Windows.Data.Binding(path), Width = new DataGridLength(width, width == 1 ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel) });
        sectionCheckGrid.ItemsSource = Array.Empty<SectionCheckRow>();
        root.Children.Add(sectionCheckGrid);
        return root;
    }

    private bool TryReadSectionCheckInputs(out double breakingStressMpa, out double reductionFactor)
    {
        breakingStressMpa = 0;
        reductionFactor = 0;
        if (!TryReadNumber(sectionStress.Text, out breakingStressMpa) || breakingStressMpa <= 0 || !double.IsFinite(breakingStressMpa))
        {
            Message("Breaking stress: enter a positive value in MPa.");
            sectionStress.Focus();
            return false;
        }
        if (!TryReadNumber(sectionReduction.Text, out var reductionPercent) || reductionPercent <= 0 || reductionPercent > 100 || !double.IsFinite(reductionPercent))
        {
            Message("Reduction factor: enter a percentage greater than 0 and at most 100.");
            sectionReduction.Focus();
            return false;
        }
        reductionFactor = reductionPercent / 100;
        return true;
    }

    private static bool TryReadNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private void RunSectionCheck()
    {
        if (!TryReadSectionCheckInputs(out var breakingStress, out var reductionFactor)) return;
        var members = cables.Where(row => row.Selected).Select(row => row.Member).ToArray();
        if (members.Length == 0) { Message("Tick at least one cable on the Cables & Loads tab."); return; }
        var selectedCases = cases.Where(item => item.IsChecked).Select(item => item.Name).ToArray();
        if (cases.Count > 0 && selectedCases.Length == 0) { Message("Tick at least one load case or combination on the Cables & Loads tab."); return; }

        var run = viewModel.DesignRunId is not null ? viewModel.SelectedFemRun : null;
        var options = viewModel.CableDesignOptions().Where(option => members.Contains(option.Member, StringComparer.Ordinal)).ToArray();
        var rows = new List<SectionCheckRow>();
        foreach (var option in options)
        {
            var area = option.AreaMm2;
            var capacity = area > 0 && double.IsFinite(area) ? area * breakingStress * reductionFactor / 1000 : (double?)null;
            if (capacity is not > 0)
            {
                rows.Add(new(option.Member, option.Section, area, null, "—", null, null, "NO SECTION"));
                continue;
            }
            if (run is null)
            {
                rows.Add(new(option.Member, option.Section, area, null, "—", capacity, null, "NO RESULT"));
                continue;
            }

            var forces = selectedCases.SelectMany(caseName => run.ResultFor(caseName).Cables
                .Where(cable => cable.MemberName == option.Member)
                .Select(cable => (Case: caseName, Force: cable.AxialForceKN)))
                .Where(sample => double.IsFinite(sample.Force)).ToArray();
            if (forces.Length == 0)
            {
                rows.Add(new(option.Member, option.Section, area, null, "—", capacity, null, "NO RESULT"));
                continue;
            }
            var governing = forces.MaxBy(sample => sample.Force);
            var tension = Math.Max(0, governing.Force);
            var utilization = tension / capacity.Value;
            var statusText = tension <= 1e-6 ? "SLACK" : utilization <= 1 ? "OK" : "FAIL";
            rows.Add(new(option.Member, option.Section, area, tension, governing.Case, capacity, utilization, statusText));
        }

        sectionCheckGrid.ItemsSource = rows;
        var failCount = rows.Count(row => row.Status == "FAIL");
        var slackCount = rows.Count(row => row.Status == "SLACK");
        var missingCount = rows.Count(row => row.Status is "NO SECTION" or "NO RESULT");
        sectionCheckSummary.Text = $"Section property check · fpu {breakingStress:0.##} MPa · φ {reductionFactor:P0} · {rows.Count - failCount - slackCount - missingCount} OK · {failCount} FAIL · {slackCount} slack · {missingCount} unavailable";
        Message(run is null ? "Run FEM Analysis for the current model before checking cable utilization." : "");
    }

    private void RefreshCableRows()
    {
        var selected = cables.Where(row => row.Selected).Select(row => row.Member).ToHashSet(StringComparer.Ordinal);
        cables.Clear();
        cables.AddRange(viewModel.CableDesignOptions().Select(option => new CableRow(option, selected.Contains(option.Member))));
        if (cableGrid.ItemsSource is null) return;
        cableGrid.ItemsSource = null;
        cableGrid.ItemsSource = cables;
        foreach (var row in cables.Where(row => row.Selected)) cableGrid.SelectedItems.Add(row);
    }

    private UIElement BuildBasisTab()
    {
        var panel = new StackPanel { Margin = new Thickness(8, 10, 8, 0) };
        panel.Children.Add(new TextBlock { Text = "Stay systems offered", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(vsl);
        panel.Children.Add(fatzer);
        panel.Children.Add(utracon);
        panel.Children.Add(Hint("The study recommends VSL as the base option, FATZER as the certified rope alternative, and Utracon only as a local specialist bid that must name an approved exposed stay system.", new Thickness(0, 2, 0, 14)));
        panel.Children.Add(Field("Screening level", screening));
        panel.Children.Add(Field("Maximum use of it", maxUse, "%"));
        panel.Children.Add(Field("Selection priority", priority));
        panel.Children.Add(Field("Tension is", forceBasis));
        panel.Children.Add(Field("Most units per stay", maxUnits));
        panel.Children.Add(Hint("A stay of T passes when n × f × MBL ≥ T with f = 0.50 (or 0.60): a first-pass proxy for an admissible load, NOT an EN 1993-1-11 ultimate resistance, fatigue check or anchorage design. " +
                                "Every system is sized from its standard units; the stay is n identical units in parallel, each with its own anchorage at both ends. " + TensionCableBasis.Code + ".", new Thickness(0, 8, 0, 0)));
        return panel;
    }

    private UIElement BuildCablesTab()
    {
        var layout = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });

        cableGrid.ItemsSource = cables;
        cableGrid.Columns.Add(new DataGridCheckBoxColumn { Header = "Design", Binding = new System.Windows.Data.Binding(nameof(CableRow.Selected)) { Mode = System.Windows.Data.BindingMode.TwoWay } });
        foreach (var (header, path, width) in new[] { ("Cable", nameof(CableRow.Member), 90.0), ("Section", nameof(CableRow.Section), 170.0), ("L [m]", nameof(CableRow.Length), 70.0),
                     ("Steel area [mm²]", nameof(CableRow.Area), 110.0), ("Model capacity [kN]", nameof(CableRow.Capacity), 130.0) })
            cableGrid.Columns.Add(new DataGridTextColumn { Header = header, IsReadOnly = true, Binding = new System.Windows.Data.Binding(path), Width = new DataGridLength(width, DataGridLengthUnitType.Pixel) });

        var all = new Button { Content = "All", Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 2, 10, 2) };
        all.Click += (_, _) => { foreach (var row in cables) row.Selected = true; };
        var none = new Button { Content = "None", Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 2, 10, 2) };
        none.Click += (_, _) => { foreach (var row in cables) row.Selected = false; };
        var inViewer = new Button { Content = "Selected in viewer", Padding = new Thickness(10, 2, 10, 2), ToolTip = "Design only the cables selected in the model viewer" };
        inViewer.Click += (_, _) => { var chosen = viewerSelected; foreach (var row in cables) row.Selected = chosen.Contains(row.Member); };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8), Children = { all, none, inViewer } };

        var left = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        left.Children.Add(bar);
        var hint = Hint("Tick the tension cables to design. The tension of each is its largest axial force over the ticked load cases of the FEM run; the stay is sized for that force at the cable's own length. The model section is not changed.", new Thickness(0, 6, 0, 0));
        DockPanel.SetDock(hint, Dock.Bottom);
        left.Children.Add(hint);
        left.Children.Add(cableGrid);
        layout.Children.Add(left);

        var caseList = new ListBox { ItemsSource = cases };
        var template = new FrameworkElementFactory(typeof(CheckBox));
        template.SetBinding(CheckBox.IsCheckedProperty, new System.Windows.Data.Binding(nameof(DesignReferenceWindow.CheckItem.IsChecked)));
        template.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding(nameof(DesignReferenceWindow.CheckItem.Name)));
        caseList.ItemTemplate = new DataTemplate { VisualTree = template };
        System.Windows.Automation.AutomationProperties.SetName(caseList, "Load cases");
        var side = new GroupBox { Header = "Load cases / combinations", Content = caseList };
        Grid.SetColumn(side, 2);
        layout.Children.Add(side);
        return layout;
    }

    private UIElement BuildRatesTab()
    {
        rateGrid.ItemsSource = rates;
        rateGrid.Columns.Add(new DataGridTextColumn { Header = "Rate", IsReadOnly = true, Binding = new System.Windows.Data.Binding(nameof(RateRow.Label)), Width = new DataGridLength(360, DataGridLengthUnitType.Pixel) });
        rateGrid.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = new System.Windows.Data.Binding(nameof(RateRow.Value)) { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(110, DataGridLengthUnitType.Pixel) });
        rateGrid.Columns.Add(new DataGridTextColumn { Header = "Unit", IsReadOnly = true, Binding = new System.Windows.Data.Binding(nameof(RateRow.Unit)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var dock = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var hint = Hint("Indicative Singapore allowances of the Technical Options Study (SGD, excluding GST and the supporting anchorage-box steelwork). They are the study author's budgeting assumptions, " +
                        "not VSL, FATZER or Utracon prices: obtain three comparable supplier bids before any procurement decision. A stay's rate per metre is its units × (material + pipe + 2 × anchorage ÷ length + installation).", new Thickness(0, 6, 0, 0));
        DockPanel.SetDock(hint, Dock.Bottom);
        dock.Children.Add(hint);
        dock.Children.Add(rateGrid);
        return dock;
    }

    private UIElement BuildSingleTab()
    {
        var panel = new StackPanel { Margin = new Thickness(8, 10, 8, 0) };
        panel.Children.Add(Field("Tension in the stay", manualForce, "MN"));
        panel.Children.Add(Field("Length of the stay", manualLength, "m"));
        var size = new Button { Content = "Size this stay", MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 3, 10, 3) };
        size.Click += (_, _) => RunSingle();
        panel.Children.Add(size);
        panel.Children.Add(Hint("Sizes one stay from a force you enter — for example a demand of 10–120 MN from another model — with the Design Basis and Cost Rates of the other tabs, without a FEM run. The result appears in the Design tab as \"Manual\".", new Thickness(0, 12, 0, 0)));
        return panel;
    }

    private UIElement BuildResultTab()
    {
        resultGrid.SelectionChanged += (_, _) => { if (resultGrid.SelectedItem is ResultRow row) { detail.Text = row.Design.Error ?? row.Design.Detail; detail.ScrollToHome(); } };
        foreach (var (header, path, width) in new[] { ("Cable", nameof(ResultRow.Member), 80.0), ("Governing case", nameof(ResultRow.Case), 130.0), ("T [kN]", nameof(ResultRow.Tension), 80.0), ("L [m]", nameof(ResultRow.Length), 60.0),
                     ("Suggested stay", nameof(ResultRow.Assembly), 190.0), ("Strands", nameof(ResultRow.Strands), 62.0), ("Capacity [kN]", nameof(ResultRow.Capacity), 90.0), ("Use", nameof(ResultRow.Use), 50.0),
                     ("ΔL [mm]", nameof(ResultRow.Elongation), 62.0), ("S$/m", nameof(ResultRow.CostPerM), 70.0), ("Cost [S$]", nameof(ResultRow.Cost), 90.0), ("Status", nameof(ResultRow.Status), 60.0) })
            resultGrid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(path), Width = new DataGridLength(width, DataGridLengthUnitType.Pixel) });

        var copy = new Button { Content = "Copy Report", MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => { if (lastBasis is null) return; try { Clipboard.SetText(TensionCableDesigner.Report(lastBasis, lastResults)); } catch (System.Runtime.InteropServices.COMException) { } };
        var save = new Button { Content = "Save Report…", MinWidth = 100 };
        save.Click += (_, _) =>
        {
            if (lastBasis is null) return;
            var dialog = new SaveFileDialog { Filter = "Text (*.txt)|*.txt", FileName = "tension-cable-design.txt" };
            if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, TensionCableDesigner.Report(lastBasis, lastResults));
        };
        var word = new Button { Content = "Export to Word…", MinWidth = 120, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Save the summary table and every cable calculation as a Word document" };
        word.Click += (_, _) => ExportWord();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { copy, save, word } };

        var layout = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.2, GridUnitType.Star), MinHeight = 120 });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(countText);
        Grid.SetRow(resultGrid, 1);
        layout.Children.Add(resultGrid);
        var box = new GroupBox { Header = "Calculation of the selected cable", Content = detail };
        Grid.SetRow(box, 3);
        layout.Children.Add(box);
        Grid.SetRow(bar, 4);
        layout.Children.Add(bar);
        countText.Text = "Run Design to see the results here.";
        return layout;
    }

    private UIElement BuildIterateTab()
    {
        var root = new DockPanel { Margin = new Thickness(8, 6, 8, 0) };
        var top = new StackPanel();
        iterateSummary.Text = "Iterate to size every cable group of the model as VSL SSI 2000 Ø15.7 mm strand stays.";
        top.Children.Add(iterateSummary);
        var basis = viewModel.CableDesignBasis;
        var sizedFor = cases.Where(item => item.IsChecked).Select(item => item.Name).ToArray();
        top.Children.Add(Hint($"Basis: VSL SSI 2000 catalogue, Ø15.7 mm strand (279 kN breaking load) · screening {basis.ScreeningFactor:0%} of the breaking load × maximum use {basis.MaxUtilisation:0%} · " +
                              $"at most {basis.MaxUnits} units per stay · lowest estimated installed cost (Cost Rates) · sized for: {(sizedFor.Length == 0 ? "every load case" : string.Join(", ", sizedFor))}.",
            new Thickness(0, 0, 0, 4)));
        top.Children.Add(Hint("Each group is sized for its largest tension over the load cases ticked on Cables & Loads, with the Design Basis (screening level, maximum use, " +
                              "most units per stay, selection priority) and the Cost Rates, rounded up to a standard VSL unit (6-12 … 6-187, n units in parallel). " +
                              "Each pass analyses a copy of the model with the trial sections — a stiffer cable attracts more force — until the strand counts stop changing and the " +
                              "forces and pretension-stage deflections settle within the tolerance. Each Cable Force Sync group (a cable and the cables that follow it) gets one size; " +
                              "cables that only share a section are sized for their own force. Every pass keeps the target pretensions and fits them again. " +
                              "Nothing changes in the model until Apply & Re-analyse, which assigns the sections as one undoable edit and runs FEM Analysis again so the forces and UR are those of the new sections.",
            new Thickness(0, 0, 0, 8)));
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(Field("Maximum iterations", iterateMaximum));
        controls.Children.Add(Field("Convergence tolerance", iterateTolerance, "%"));
        iterateButton.Click += async (_, _) => await IterateAsync();
        stopButton.Click += (_, _) => iterationCancellation?.Cancel();
        applyButton.Click += async (_, _) => await ApplyIterationAsync();
        controls.Children.Add(new Border { Width = 16 });
        controls.Children.Add(iterateButton);
        controls.Children.Add(stopButton);
        controls.Children.Add(applyButton);
        top.Children.Add(controls);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        foreach (var (header, path, width) in new[]
        {
            ("Cable", nameof(IterateRow.Member), 90.0), ("Current", nameof(IterateRow.Current), 90.0), ("Force now [kN]", nameof(IterateRow.CurrentForce), 100.0),
            ("UR now", nameof(IterateRow.CurrentUr), 60.0), ("Suggested", nameof(IterateRow.Suggested), 90.0), ("VSL stay", nameof(IterateRow.Product), 180.0),
            ("Governing case", nameof(IterateRow.Case), 120.0), ("Force [kN]", nameof(IterateRow.Force), 90.0), ("UR", nameof(IterateRow.SuggestedUr), 55.0),
            ("Cost [S$]", nameof(IterateRow.Cost), 90.0), ("Status", nameof(IterateRow.Status), 110.0),
            ("Force after [kN]", nameof(IterateRow.AfterForce), 110.0), ("UR after", nameof(IterateRow.AfterUr), 70.0),
        })
            iterateGrid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(path), Width = new DataGridLength(width, DataGridLengthUnitType.Pixel) });
        iterateGrid.ItemsSource = iterateRows;

        foreach (var (header, path, width) in new[]
        {
            ("Pass", nameof(CableSectionOptimizationIteration.Number), 50.0), ("Changed groups", nameof(CableSectionOptimizationIteration.AdjustedGroup), 1.0),
            ("Max force change %", nameof(CableSectionOptimizationIteration.MaximumForceChangePercent), 130.0),
            ("Max deflection change %", nameof(CableSectionOptimizationIteration.MaximumDeflectionChangePercent), 150.0),
        })
            historyGrid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(path) { StringFormat = path.StartsWith("Maximum", StringComparison.Ordinal) ? "0.###" : null },
                Width = new DataGridLength(width, width == 1 ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel) });
        historyGrid.ItemsSource = iterations;

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 120 });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 90 });
        layout.Children.Add(iterateGrid);
        var history = new GroupBox { Header = "Iteration history", Content = historyGrid };
        Grid.SetRow(history, 2);
        layout.Children.Add(history);
        root.Children.Add(layout);
        return root;
    }

    /// <summary>The settings of an iteration from the Design Basis, the ticked load cases and this tab; null with the reason shown.</summary>
    internal CableSectionOptimizationSettings? ReadIterationSettings()
    {
        if (ReadBasis() is not { } basis) return null;
        var chosen = cases.Where(item => item.IsChecked).Select(item => item.Name).ToArray();
        if (chosen.Length == 0) { Message("Tick at least one load case or combination on the Cables & Loads tab to size the cables for."); return null; }
        if (!int.TryParse(iterateMaximum.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maximum) || maximum is < 1 or > 50)
        { Message("Maximum iterations: enter a whole number from 1 to 50."); return null; }
        if (!TryReadNumber(iterateTolerance.Text, out var tolerance) || tolerance <= 0 || tolerance > 100)
        { Message("Convergence tolerance: enter a percentage above 0 and at most 100."); return null; }
        // The VSL screen of the Design Basis: a Ø15.7 strand breaks at 279 kN (150 mm² × 1,860 MPa); it may carry f × MBL × η.
        return new CableSectionOptimizationSettings(basis.MaxUtilisation, TensionCableCatalogue.StrandBreakingKN * 1000 / TensionCableCatalogue.StrandAreaMm2,
            basis.ScreeningFactor, maximum, tolerance, basis with { LoadCases = chosen }, chosen, SyncGroupsOnly: true);
    }

    private async Task IterateAsync()
    {
        if (iterationCancellation is not null || ReadIterationSettings() is not { } settings) return;
        Message("");
        iterationCancellation = new CancellationTokenSource();
        iterateButton.IsEnabled = applyButton.IsEnabled = false;
        stopButton.IsEnabled = true;
        iterations.Clear();
        iterateSummary.Text = "Iterating — each pass analyses the model with the trial sections…";
        try
        {
            var progress = new Progress<CableSectionOptimizationProgress>(report =>
            {
                iterations.Add(report.Iteration);
                ShowIterationRows(report.Rows);
                iterateSummary.Text = $"Pass {report.Iteration.Number} · {report.Status}";
            });
            iteration = await viewModel.RunCableSectionOptimizationAsync(settings, progress, iterationCancellation.Token);
            iterations.Clear();
            foreach (var pass in iteration.Iterations) iterations.Add(pass);
            ShowIterationRows(iteration.Rows);
            iterateSummary.Text = IterationSummary(iteration);
            if (iteration.Failure is { Length: > 0 } failure) Message(failure);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Message(ex.Message);
            iterateSummary.Text = "The iteration could not be completed.";
        }
        finally
        {
            iterationCancellation.Dispose();
            iterationCancellation = null;
            iterateButton.IsEnabled = true;
            stopButton.IsEnabled = false;
            applyButton.IsEnabled = CanApplyIteration();
        }
    }

    private static string IterationSummary(CableSectionOptimizationRun run)
    {
        var changed = run.Rows.GroupBy(row => row.GroupKey).Count(group => group.Any(row => row.CurrentStrands != row.SuggestedStrands));
        var outcome = run.Failure is not null ? "Failed" : run.Cancelled ? "Stopped" : run.Converged ? "Converged" : "Not converged within the maximum iterations";
        var cost = run.Rows.Sum(row => row.CostSgd ?? 0);
        return $"{outcome} after {run.IterationCount} pass(es) · {changed} group(s) to change · largest UR {run.Rows.Select(row => row.SuggestedUr).DefaultIfEmpty().Max():0.00} · indicative cost S${cost:#,0}";
    }

    private void ShowIterationRows(IReadOnlyList<CableSectionOptimizationRow> rows)
    {
        iterateRows.Clear();
        iterateRows.AddRange(rows.Select(row => new IterateRow(row)));
        iterateGrid.ItemsSource = null;
        iterateGrid.ItemsSource = iterateRows;
    }

    private bool CanApplyIteration() => iteration is { Failure: null, Cancelled: false } run && run.Rows.Any(row => row.CurrentStrands != row.SuggestedStrands) &&
                                        viewModel.CanApplyCableSectionOptimization && viewModel.CableSectionOptimizationIsCurrent(run);

    private async Task ApplyIterationAsync()
    {
        if (iteration is not { } run) return;
        if (!viewModel.CableSectionOptimizationIsCurrent(run)) { Message("The model changed after this iteration. Iterate again before applying."); applyButton.IsEnabled = false; return; }
        var groups = run.Rows.GroupBy(row => row.GroupKey).Count(group => group.Any(row => row.CurrentStrands != row.SuggestedStrands));
        var text = $"Assign the suggested Ø15.7 mm strand sections to {groups} cable group(s) and run FEM Analysis again?\n\n" +
                   "The current FEM results are replaced by those of the new sections. To go back, unlock the model and use Edit → Undo." +
                   (run.Converged ? "" : "\n\nThe iteration did NOT converge: the forces may still move with these sections.");
        if (MessageBox.Show(this, text, "Apply & Re-analyse", MessageBoxButton.YesNo, run.Converged ? MessageBoxImage.Question : MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        applyButton.IsEnabled = iterateButton.IsEnabled = false;
        try
        {
            iterateSummary.Text = "Applying the sections and re-running FEM Analysis…";
            var analysed = await viewModel.ApplyCableSectionOptimizationAndAnalyzeAsync(run, confirmedUnconverged: !run.Converged);
            RefreshCableRows();
            if (!analysed) { Message("The sections were applied, but FEM Analysis did not produce results. See the run monitor."); return; }
            ShowAfterAnalysis(run.Settings);
            RunSectionCheck();   // the assigned sections are now the new ones, with the forces of the new analysis
        }
        catch (InvalidOperationException ex) { Message(ex.Message); }
        finally
        {
            iteration = null;
            iterateButton.IsEnabled = true;
            applyButton.IsEnabled = false;
        }
    }

    /// <summary>The force of each cable in the re-analysed model and the UR of its new section.</summary>
    private void ShowAfterAnalysis(CableSectionOptimizationSettings settings)
    {
        if (viewModel.SelectedFemRun is not { } fem) return;
        var caseNames = settings.DesignCases ?? [];
        foreach (var row in iterateRows)
        {
            var force = caseNames.SelectMany(name => fem.Results is not null && fem.Results.TryGetValue(name, out var result) ? result.Cables : [])
                .Where(cable => cable.MemberName == row.Member && double.IsFinite(cable.AxialForceKN)).Select(cable => cable.AxialForceKN)
                .DefaultIfEmpty(double.NaN).Max();
            row.SetAfter(double.IsNaN(force) ? null : force, double.IsNaN(force) ? null : settings.Utilisation(force, row.Row.SuggestedStrands));
        }
        var urs = iterateRows.Select(row => row.AfterUrValue).OfType<double>().ToArray();
        var over = urs.Count(ur => ur > settings.TargetUr + 1e-9);
        iterateSummary.Text = $"Applied and re-analysed · largest UR {urs.DefaultIfEmpty().Max():0.00}" +
            (over == 0 ? " · every cable within the maximum use" : $" · {over} cable(s) above the maximum use — Iterate again");
        Message("");
    }

    private void Message(string text) => status.Text = text;

    private void ExportWord()
    {
        if (lastBasis is null || lastResults.Count == 0) { Message("Run Design first; there is nothing to export yet."); return; }
        var dialog = new SaveFileDialog { Title = "Export to Word", Filter = "Word document (*.docx)|*.docx", FileName = "tension-cable-design.docx", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            DesignReportWord.Export(TensionCableDesigner.WordReport(lastBasis, lastResults), dialog.FileName);
            Message("");
            if (MessageBox.Show(this, $"Saved to\n{dialog.FileName}\n\nOpen it now?", "Export to Word", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Message("The document could not be saved: " + ex.Message + " Is it open in Word? Close it, or choose another name."); }
    }

    /// <summary>The basis the tabs describe, or why it cannot be read.</summary>
    private TensionCableBasis? ReadBasis()
    {
        var systems = new List<CableSystem>();
        if (vsl.IsChecked == true) systems.Add(CableSystem.VslSsi2000);
        if (fatzer.IsChecked == true) systems.Add(CableSystem.FatzerFlc);
        if (utracon.IsChecked == true) systems.Add(CableSystem.UtraconPt);
        if (!double.TryParse(maxUse.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var use)) { Message("Maximum use: enter a percentage."); return null; }
        if (!int.TryParse(maxUnits.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var units)) { Message("Most units per stay: enter a whole number."); return null; }
        var rateSet = new TensionCableRates();
        foreach (var row in rates)
        {
            if (!double.TryParse(row.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) { Message($"Rate {row.Label}: enter a number."); return null; }
            rateSet = row.Entry.Set(rateSet, value);
        }
        var basis = new TensionCableBasis
        {
            Systems = systems, ScreeningFactor = Screens[Math.Max(0, screening.SelectedIndex)], MaxUtilisation = use / 100, MaxUnits = units,
            Priority = (CablePriority)Math.Max(0, priority.SelectedIndex), ForceBasis = (CableForceBasis)Math.Max(0, forceBasis.SelectedIndex), Rates = rateSet,
        };
        if (basis.Problems() is { Count: > 0 } problems) { Message(problems[0]); return null; }
        return basis;
    }

    private void Show(TensionCableBasis basis, IReadOnlyList<TensionCableMemberDesign> designs, string basisText)
    {
        lastBasis = basis;
        lastResults = designs;
        resultGrid.ItemsSource = designs.Select(design => new ResultRow(design)).ToList();
        var failed = designs.Count(design => design.Status == "ERROR");
        var check = designs.Count(design => design.Status == "CHECK");
        var none = designs.Count(design => design.Status == "—");
        countText.Text = $"{designs.Count} cable(s): {designs.Count - failed - check - none} OK · {check} to check · {none} without tension · {failed} not designed · indicative cost S${designs.Sum(design => design.Selected?.CostSgd ?? 0):#,0} — {basisText}";
        if (designs.Count > 0) resultGrid.SelectedIndex = 0;
        Message("");
        closeButton.Content = "Close";
        tabs.SelectedItem = designTab;
    }

    private void Run()
    {
        if (ReadBasis() is not { } basis) return;
        if (viewModel.DesignRunId is null) { Message("There are no FEM results to design the model's cables with. Run the FEM analysis (Run → Run FEM Analysis) first, or size one stay on the Single Stay tab."); return; }
        var members = cables.Where(row => row.Selected).Select(row => row.Member).ToArray();
        if (members.Length == 0) { Message(cables.Count == 0 ? "The model has no tension cables." : "Tick at least one cable."); return; }
        var chosen = cases.Where(item => item.IsChecked).Select(item => item.Name).ToArray();
        if (cases.Count > 0 && chosen.Length == 0) { Message("Tick at least one load case or combination."); return; }
        basis = basis with { Members = members, LoadCases = chosen };
        viewModel.CableDesignBasis = basis;
        try { Show(basis, viewModel.RunTensionCableDesign(basis), $"FEM analysis of the model · {chosen.Length} load case(s)"); }
        catch (Exception ex) { Message(ex is InvalidOperationException ? ex.Message : "The design could not be completed: " + ex.Message); }
    }

    private void RunSingle()
    {
        if (ReadBasis() is not { } basis) return;
        if (!double.TryParse(manualForce.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var force) || !double.IsFinite(force) || force <= 0) { Message("The tension must be a number greater than 0 MN."); return; }
        if (!double.TryParse(manualLength.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var length) || !double.IsFinite(length) || length <= 0) { Message("The length must be a number greater than 0 m."); return; }
        Show(basis, [TensionCableDesigner.Design(basis, "Manual", "", length, "", force * 1000)], $"entered force, no FEM run");
        viewModel.CableDesignBasis = basis with { Members = viewModel.CableDesignBasis.Members, LoadCases = viewModel.CableDesignBasis.LoadCases };
    }
}
