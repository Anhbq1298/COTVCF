using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using COT_VCF.Application;
using COT_VCF.Domain;

namespace COT_VCF.Presentation;

/// <summary>
/// Edit → VCF Geometry. Reads the VCF system from the working model (02 Truss Height chord groups), shows its parameters
/// and member sections with a live preview, and writes them back on Apply / OK. Point and member names never change.
/// </summary>
public partial class VcfGeometryWindow : Window
{
    private const string Keep = "(keep as assigned)";
    private readonly MainViewModel viewModel;
    private VcfGeometryAnalysis? analysis;
    private GeometryModel? preview;
    private bool loading;

    private readonly Dictionary<string, TextBox> inputs = [];
    private readonly Dictionary<VcfMemberRole, ComboBox> roleSections = [];
    private readonly Dictionary<string, ComboBox> cableSections = new(StringComparer.Ordinal);

    public VcfGeometryWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        LoadFromModel();
    }

    // ---- read the working model into the form ----
    internal void LoadFromModel()
    {
        loading = true;
        try
        {
            GeometryGrid.Children.Clear(); GeometryGrid.RowDefinitions.Clear(); inputs.Clear();
            FrameGrid.Children.Clear(); FrameGrid.RowDefinitions.Clear(); roleSections.Clear();
            CableGrid.Children.Clear(); CableGrid.RowDefinitions.Clear(); cableSections.Clear();
            try { analysis = VcfGeometry.Analyze(viewModel.State.Geometry, viewModel.State.Truss); }
            catch (Exception ex)
            {
                analysis = null;
                ProblemText.Text = ex.Message;
                Preview.Model = viewModel.State.Geometry;
                SetActions(false);
                return;
            }

            var p = analysis.Parameters;
            AddReadOnly(GeometryGrid, "Bays", analysis.Bays.ToString(CultureInfo.CurrentCulture) + (analysis.UniformBays ? "" : " (unequal — Apply makes them equal)"));
            AddInput("Bay", "Bay width (span per bay)", p.BayWidth);
            AddInput("Depth", "Truss depth (bottom → top chord)", p.TrussDepth);
            if (analysis.HasMiddleChord) AddInput("Middle", "Middle chord above bottom chord", p.MiddleChordHeight);
            AddInput("Clear", "Clear height (ground → bottom chord)", p.ClearHeight);
            if (p.BackSpan is { } span) AddInput("BackSpan", "Back-span (mega column → outer column)", span);
            if (p.BackStayHeight is { } stay) AddInput("BackStay", "Back-stay node height (above ground)", stay);

            var frames = viewModel.VcfSectionNames(cable: false);
            foreach (var role in new[] { VcfMemberRole.TopChord, VcfMemberRole.MiddleChord, VcfMemberRole.BottomChord,
                         VcfMemberRole.VerticalChord, VcfMemberRole.MegaColumn, VcfMemberRole.EndFrame })
            {
                var members = Members(role);
                if (members.Count == 0) continue;
                var current = members.Select(viewModel.MemberSection).Distinct().ToArray();
                var combo = SectionCombo(frames, current.Length == 1 ? current[0] : null);
                combo.ToolTip = string.Join(", ", members);
                roleSections[role] = combo;
                AddRow(FrameGrid, VcfGeometry.Title(role), combo, $"{members.Count} member{(members.Count == 1 ? "" : "s")}");
            }
            var cables = viewModel.VcfSectionNames(cable: true);
            foreach (var name in Members(VcfMemberRole.Cable).Order(StringComparer.Ordinal))
            {
                var combo = SectionCombo(cables, viewModel.MemberSection(name));
                cableSections[name] = combo;
                AddRow(CableGrid, name, combo, null);
            }
            SectionHint.Text = viewModel.FemSectionProperties.Count == 0
                ? "No member properties yet — sections are read from SAP2000 on Apply (read-only), or use File → Read FEM Properties from SAP."
                : "Sections are the ones assigned in the model; a member keeps its own local axis and end releases.";
        }
        finally { loading = false; }
        Refresh();
    }

    private IReadOnlyList<string> Members(VcfMemberRole role) =>
        analysis!.Roles.Where(pair => pair.Value == role).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();

    private ComboBox SectionCombo(IReadOnlyList<string> names, string? current)
    {
        var combo = new ComboBox { MinWidth = 150, Margin = new Thickness(0, 2, 6, 2), IsEnabled = names.Count > 0 };
        combo.Items.Add(Keep);
        foreach (var name in names) combo.Items.Add(name);
        combo.SelectedItem = current is not null && names.Contains(current) ? current : Keep;
        combo.SelectionChanged += (_, _) => Refresh();
        return combo;
    }

    private void AddInput(string key, string label, double value)
    {
        var box = new TextBox { Text = value.ToString("0.###", CultureInfo.CurrentCulture), TextAlignment = TextAlignment.Right,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), Margin = new Thickness(0, 2, 4, 2) };
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        box.TextChanged += (_, _) => Refresh();
        inputs[key] = box;
        var row = GeometryGrid.RowDefinitions.Count;
        GeometryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(GeometryGrid, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }, row, 0);
        Place(GeometryGrid, box, row, 1);
        Place(GeometryGrid, new TextBlock { Text = "m", VerticalAlignment = VerticalAlignment.Center, Foreground = (System.Windows.Media.Brush)FindResource("Muted") }, row, 2);
    }

    private void AddReadOnly(Grid grid, string label, string value)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(grid, new TextBlock { Text = label, Margin = new Thickness(0, 3, 0, 3) }, row, 0);
        var text = new TextBlock { Text = value, Margin = new Thickness(0, 3, 0, 3), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumnSpan(text, 2);
        Place(grid, text, row, 1);
    }

    private static void AddRow(Grid grid, string label, ComboBox combo, string? note)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(grid, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, row, 0);
        System.Windows.Automation.AutomationProperties.SetName(combo, label + " section");
        Place(grid, combo, row, 1);
        if (note is not null) Place(grid, new TextBlock { Text = note, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }, row, 2);
    }

    private static void Place(Grid grid, UIElement element, int row, int column)
    {
        Grid.SetRow(element, row); Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    // ---- live preview ----
    private double? Input(string key) =>
        inputs.TryGetValue(key, out var box) && double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ? value : null;

    /// <summary>The parameters in the form, or null with the problem shown.</summary>
    internal VcfGeometryParameters? FormParameters(out IReadOnlyList<string> problems)
    {
        problems = [];
        if (analysis is null) { problems = ["The model is not a VCF system the editor can read."]; return null; }
        var unreadable = inputs.Where(pair => Input(pair.Key) is null).Select(pair => System.Windows.Automation.AutomationProperties.GetName(pair.Value)).ToArray();
        if (unreadable.Length > 0) { problems = unreadable.Select(name => $"{name}: enter a number.").ToArray(); return null; }
        var p = analysis.Parameters;
        var form = new VcfGeometryParameters(Input("Bay")!.Value, Input("Depth")!.Value, Input("Middle") ?? p.MiddleChordHeight, Input("Clear")!.Value,
            Input("BackSpan"), Input("BackStay"));
        problems = VcfGeometry.Validate(analysis, form);
        return problems.Count == 0 ? form : null;
    }

    /// <summary>Section per member chosen in the form (members left on "keep" are not listed).</summary>
    internal IReadOnlyDictionary<string, string> FormSections()
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (role, combo) in roleSections)
            if (combo.SelectedItem is string section && section != Keep)
                foreach (var member in Members(role)) sections[member] = section;
        foreach (var (name, combo) in cableSections)
            if (combo.SelectedItem is string section && section != Keep) sections[name] = section;
        return sections;
    }

    private void Refresh()
    {
        if (loading || analysis is null) return;
        var form = FormParameters(out var problems);
        if (form is null)
        {
            ProblemText.Text = string.Join("\n", problems);
            SetActions(false);
            return;
        }
        preview = VcfGeometry.Build(analysis, form);
        Preview.Model = preview;     // geometry and dimensions only — section names would crowd the small preview
        Preview.PinSupportPoints = SafePins(preview);
        ProblemText.Text = "";
        var span = form.BayWidth * analysis.Bays;
        GeometrySummary.Text = $"Truss span {span:0.###} m · overall {span + 2 * (form.BackSpan ?? 0):0.###} m · " +
            $"top chord at {form.ClearHeight + form.TrussDepth:0.###} m above ground";
        SetActions(true);
    }

    private static IEnumerable<string>? SafePins(GeometryModel geometry)
    {
        try { return COT_VCF.Application.FemStudyService.FindPinSupportPoints(geometry); }
        catch { return null; }
    }

    private void SetActions(bool enabled)
    {
        ApplyButton.IsEnabled = enabled && viewModel.CanEditVcfGeometry;
        OkButton.IsEnabled = ApplyButton.IsEnabled;
    }

    // ---- actions ----
    internal async Task ApplyFormAsync()
    {
        if (analysis is null || FormParameters(out _) is not { } form) return;
        await viewModel.ApplyVcfGeometryAsync(VcfGeometry.Build(analysis, form), FormSections());
        LoadFromModel();
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => LoadFromModel();
    private async void Apply_Click(object sender, RoutedEventArgs e) => await ApplyFormAsync();
    private async void Ok_Click(object sender, RoutedEventArgs e) { await ApplyFormAsync(); Close(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
