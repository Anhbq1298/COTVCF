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
    private VcfGeometryAnalysis? analysis;          // the working model as it is
    private VcfGeometryAnalysis? working;           // … with the bays in the form (same as analysis when unchanged)
    private VcfTopology? topology;                  // set when the form changes the number of bays
    private readonly Dictionary<VcfMemberRole, TextBlock> roleCounts = [];
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
            FrameGrid.Children.Clear(); FrameGrid.RowDefinitions.Clear(); roleSections.Clear(); roleCounts.Clear();
            working = null; topology = null;
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
            AddInput("Bays", "Number of bays", analysis.Bays, unit: "");
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
                roleCounts[role] = AddRow(FrameGrid, VcfGeometry.Title(role), combo, Count(members.Count))!;   // a note is given, so a TextBlock comes back
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
        (working ?? analysis)!.Roles.Where(pair => pair.Value == role).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();

    private static string Count(int members) => $"{members} member{(members == 1 ? "" : "s")}";

    private ComboBox SectionCombo(IReadOnlyList<string> names, string? current)
    {
        var combo = new ComboBox { MinWidth = 150, Margin = new Thickness(0, 2, 6, 2), IsEnabled = names.Count > 0 };
        combo.Items.Add(Keep);
        foreach (var name in names) combo.Items.Add(name);
        combo.SelectedItem = current is not null && names.Contains(current) ? current : Keep;
        combo.SelectionChanged += (_, _) => Refresh();
        return combo;
    }

    private void AddInput(string key, string label, double value, string unit = "m")
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
        Place(GeometryGrid, new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Foreground = (System.Windows.Media.Brush)FindResource("Muted") }, row, 2);
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

    private static TextBlock? AddRow(Grid grid, string label, ComboBox combo, string? note)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(grid, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, row, 0);
        System.Windows.Automation.AutomationProperties.SetName(combo, label + " section");
        Place(grid, combo, row, 1);
        if (note is null) return null;
        var text = new TextBlock { Text = note, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Place(grid, text, row, 2);
        return text;
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
        var bays = Input("Bays")!.Value;
        if (bays != Math.Floor(bays) || bays < 2 || bays > 200) { problems = ["Number of bays: enter a whole number from 2 to 200."]; return null; }
        try
        {
            topology = (int)bays == analysis.Bays ? null : VcfGeometry.ChangeBays(analysis, (int)bays);
            working = topology is null ? analysis : VcfGeometry.Analyze(topology);
        }
        catch (InvalidOperationException ex) { problems = [ex.Message]; return null; }
        var p = analysis.Parameters;
        var form = new VcfGeometryParameters(Input("Bay")!.Value, Input("Depth")!.Value, Input("Middle") ?? p.MiddleChordHeight, Input("Clear")!.Value,
            Input("BackSpan"), Input("BackStay"));
        problems = VcfGeometry.Validate(working, form);
        return problems.Count == 0 ? form : null;
    }

    /// <summary>Section per member chosen in the form (members left on "keep" are not listed).</summary>
    internal IReadOnlyDictionary<string, string> FormSections()
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (role, combo) in roleSections)
            if (combo.SelectedItem is string section && section != Keep)
                foreach (var member in Members(role)) sections[member] = section;
        var existing = (working ?? analysis)!.Geometry.Members.Select(member => member.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, combo) in cableSections)
            if (combo.SelectedItem is string section && section != Keep && existing.Contains(name)) sections[name] = section;
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
        var model = working!;
        preview = VcfGeometry.Build(model, form);
        foreach (var (role, text) in roleCounts) text.Text = Count(Members(role).Count);
        Preview.SpanPoints = model.TopPoints.Concat(model.MiddlePoints).Concat(model.BottomPoints).ToArray();
        Preview.Model = preview;     // geometry and dimensions only — section names would crowd the small preview
        Preview.PinSupportPoints = SafePins(preview);
        ShowForces(model, preview);
        ProblemText.Text = "";
        var span = form.BayWidth * model.Bays;
        GeometrySummary.Text = $"Span {span:0.###} m between mega columns · overall {span + 2 * (form.BackSpan ?? 0):0.###} m · " +
            $"top chord at {form.ClearHeight + form.TrussDepth:0.###} m above ground" +
            (topology is null ? "" : $" · {model.Geometry.Points.Count} points, {model.Geometry.Members.Count} members ({topology.AddedCables.Count} new fan cables)") +
            (analysis.UniformBays ? "" : " · the bays are unequal now; Apply makes them equal");
        SetActions(true);
    }

    // ---- Cable Force / Storey Force panes ----
    internal sealed record CableForceRow(string Cable, string Pattern, string Sync, string Force);
    internal sealed record StoreyForceRow(string Point, string X, string Area, IReadOnlyList<string> Forces);

    private static string Kn(double value) => value.ToString("N0", CultureInfo.CurrentCulture);
    private static string Range(IReadOnlyList<double> values) => values.Count == 0 ? "—"
        : values.Min() == values.Max() ? Kn(values[0]) : $"{Kn(values.Min())} … {Kn(values.Max())} ({values.Count})";

    /// <summary>The pretension of every cable (Tab 03) and the force applied at every loaded point (Tab 01, with the
    /// tributary lengths of the previewed geometry).</summary>
    private void ShowForces(VcfGeometryAnalysis model, GeometryModel geometry)
    {
        var state = viewModel.State;
        var cables = state.Cables.ToArray();
        var rows = new List<CableForceRow>();
        foreach (var name in geometry.Members.Where(member => member.Kind != MemberKind.Frame).Select(member => member.Name).Order(StringComparer.Ordinal))
        {
            var added = topology is not null && topology.AddedCables.TryGetValue(name, out var template) ? template : null;
            var cable = cables.FirstOrDefault(item => item.CableName == (added ?? name));
            if (cable is null) { rows.Add(new(name, "", "", "not on 03 Pretension")); continue; }
            var forces = cable.IsFollower ? CableSyncGraph.ResolvedForces(cable, cables) : cable.GetCandidateForces();
            rows.Add(new(name, cable.LoadPattern, cable.SyncSummary + (added is null ? "" : $" (new, as {added})"), Range(forces)));
        }
        CableForceGrid.ItemsSource = rows;
        CableForceSummary.Text = $"{rows.Count} cables · {rows.Count(row => row.Force.Contains('…'))} with a range of targets";

        // Storey force: the loaded points (all top chord points stay loaded when every one was), tributary length from the preview
        var applied = state.AppliedPoints.Select(point => point.PointName).ToArray();
        var top = state.Truss.TopPoints;
        var allTop = top.Count > 0 && applied.Length == top.Count && top.All(applied.Contains);
        IEnumerable<string> candidates = topology is not null && allTop ? model.TopPoints
            : applied.Select(name => topology is not null && topology.RenamedPoints.TryGetValue(name, out var renamed) ? renamed : name);
        var names = candidates.Where(geometry.Points.ContainsKey).Distinct(StringComparer.Ordinal)
            .OrderBy(name => geometry.Points[name].Position.X).ThenBy(name => name, StringComparer.Ordinal).ToArray();
        var definitions = state.LoadDefinitions.Where(definition => !string.IsNullOrWhiteSpace(definition.LoadName)).ToArray();
        var stories = double.IsFinite(state.NumberOfStories) && state.NumberOfStories >= 1 ? Math.Truncate(state.NumberOfStories) : 0;
        var storeyRows = new List<StoreyForceRow>();
        var totals = new double[definitions.Length, 2];
        double X(int i) => geometry.Points[names[i]].Position.X;
        for (var index = 0; index < names.Length; index++)
        {
            var length = names.Length switch
            {
                1 => 0,
                _ when index == 0 => Math.Max(0, X(1) - X(0)) / 2,
                _ when index == names.Length - 1 => Math.Max(0, X(names.Length - 1) - X(names.Length - 2)) / 2,
                _ => Math.Max(0, X(index + 1) - X(index - 1)) / 2,
            };
            var area = length * state.TributaryWidthB;
            var forces = new List<string>();
            for (var d = 0; d < definitions.Length; d++)
            {
                var values = definitions[d].CandidateValues.Select(qz => -qz * area * stories).ToArray();
                forces.Add(Range(values));
                if (values.Length > 0) { totals[d, 0] += values.Min(); totals[d, 1] += values.Max(); }
            }
            storeyRows.Add(new(names[index], X(index).ToString("0.###", CultureInfo.CurrentCulture), area.ToString("0.###", CultureInfo.CurrentCulture), forces));
        }
        StoreyForceGrid.Columns.Clear();
        StoreyForceGrid.Columns.Add(new DataGridTextColumn { Header = "Point", Binding = new System.Windows.Data.Binding(nameof(StoreyForceRow.Point)), MinWidth = 70 });
        StoreyForceGrid.Columns.Add(new DataGridTextColumn { Header = "X [m]", Binding = new System.Windows.Data.Binding(nameof(StoreyForceRow.X)), ElementStyle = (Style)FindResource("NumericCell") });
        StoreyForceGrid.Columns.Add(new DataGridTextColumn { Header = "Trib. area [m²]", Binding = new System.Windows.Data.Binding(nameof(StoreyForceRow.Area)), ElementStyle = (Style)FindResource("NumericCell") });
        for (var d = 0; d < definitions.Length; d++)
            StoreyForceGrid.Columns.Add(new DataGridTextColumn { Header = $"{definitions[d].LoadName} Fz [kN]", MinWidth = 90,
                Binding = new System.Windows.Data.Binding($"{nameof(StoreyForceRow.Forces)}[{d}]"), ElementStyle = (Style)FindResource("NumericCell") });
        StoreyForceGrid.ItemsSource = storeyRows;
        StoreyForceCaption.Text = $"Applied force per point from 01 Loading: Fz = −qz × trib. length × B ({state.TributaryWidthB:0.###} m) × NStories ({stories:0})";
        StoreyForceSummary.Text = names.Length == 0 ? "No loaded points on 01 Loading."
            : $"{names.Length} points · total " + string.Join(" · ", definitions.Select((definition, d) =>
                $"{definition.LoadName} {(totals[d, 0] == totals[d, 1] ? Kn(totals[d, 0]) : $"{Kn(totals[d, 0])} … {Kn(totals[d, 1])}")} kN"));
    }

    internal IReadOnlyList<CableForceRow> CableForces => (IReadOnlyList<CableForceRow>?)CableForceGrid.ItemsSource ?? [];
    internal IReadOnlyList<StoreyForceRow> StoreyForces => (IReadOnlyList<StoreyForceRow>?)StoreyForceGrid.ItemsSource ?? [];
    /// <summary>Types into a geometry input (checks).</summary>
    internal void SetInput(string key, string text) => inputs[key].Text = text;

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
        await viewModel.ApplyVcfGeometryAsync(VcfGeometry.Build(working!, form), FormSections(), topology);
        LoadFromModel();
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => LoadFromModel();
    private async void Apply_Click(object sender, RoutedEventArgs e) => await ApplyFormAsync();
    private async void Ok_Click(object sender, RoutedEventArgs e) { await ApplyFormAsync(); Close(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
