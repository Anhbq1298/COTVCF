using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Presentation;

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
    private bool creatingBlankModel;

    private readonly Dictionary<string, TextBox> inputs = [];
    private readonly Dictionary<VcfMemberRole, ComboBox> roleSections = [];
    private readonly Dictionary<string, ComboBox> cableSections = new(StringComparer.Ordinal);

    public VcfGeometryWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        Preview.SelectionChanged += (_, _) => UpdateStoreyPointActions();
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
            cableForceEdits.Clear();
            cableSyncEdits.Clear();
            CableGrid.Children.Clear(); CableGrid.RowDefinitions.Clear(); cableSections.Clear();
            creatingBlankModel = !viewModel.HasModelGeometry;
            try { analysis = viewModel.AnalyzeVcfGeometryForEdit(); }
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

            LoadStoreyInputs();

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
            SectionHint.Text = creatingBlankModel
                ? "New blank model — the selected library sections will create the frame and cable member properties on Apply."
                : viewModel.FemSectionProperties.Count == 0
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
        combo.SelectedItem = current is not null && names.Contains(current) ? current
            : creatingBlankModel && names.Count > 0 ? names[0] : Keep;
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
        if (StoreyInputs(out var storeyProblems) is null)
        {
            ProblemText.Text = string.Join("\n", storeyProblems);
            SetActions(false);
            return;
        }
        ProblemText.Text = "";
        var span = form.BayWidth * model.Bays;
        GeometrySummary.Text = $"Span {span:0.###} m between mega columns · overall {span + 2 * (form.BackSpan ?? 0):0.###} m · " +
            $"top chord at {form.ClearHeight + form.TrussDepth:0.###} m above ground" +
            (topology is null ? "" : $" · {model.Geometry.Points.Count} points, {model.Geometry.Members.Count} members ({topology.AddedCables.Count} new fan cables)") +
            (analysis.UniformBays ? "" : " · the bays are unequal now; Apply makes them equal");
        SetActions(true);
    }

    // ---- Cable Force / Storey Force panes ----
    // The loads and the cable forces are part of the parametric set-up of the model, so they are entered here — the same
    // inputs as 01 Loading and 03 Pretension, but one fixed value each. A range is a matter for Run → Optimization VCF.

    /// <summary>One cable on the Cable Force sheet. A cable with a value of its own takes a new target force here; a
    /// cable that follows another keeps its sync rule, and a cable that is not on 03 Pretension has no target.</summary>
    internal sealed class CableForceRow(string cable, string pattern, string sync, string force, bool editable, string hint, Action<string, double> edited) : Observable
    {
        private string force = force;
        public string Cable { get; } = cable;
        public string Pattern { get; } = pattern;
        public string Sync { get; } = sync;
        public IReadOnlyList<string> SyncChoices { get; init; } = [];
        private string selection = "Independent";
        public Action<string>? SyncEdited { get; init; }
        public string SyncSelection { get => selection; set { if (Set(ref selection, value)) SyncEdited?.Invoke(value); } }
        public bool Editable { get; } = editable;
        public string Hint { get; } = hint;
        /// <summary>The target force shown [kN]; typing a number stages it for Apply / OK.</summary>
        public string Force
        {
            get => force;
            set
            {
                if (value == force) return;
                if (!Editable) throw new ArgumentException(Hint);
                if (!double.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out var kn) || !double.IsFinite(kn) || kn < 0)
                    throw new ArgumentException("Enter the target force in kN, zero or greater.");
                edited(Cable, kn);
                Set(ref force, Kn(kn));
            }
        }
    }

    /// <summary>One load of the model on the Storey Force sheet: a load pattern with its qz [kPa] — one value, no range.</summary>
    internal sealed class StoreyLoadRow(string pattern, string qz, string hint, Action changed) : Observable
    {
        private string pattern = pattern, qz = qz;
        public string Hint { get; } = hint;
        public string Pattern { get => pattern; set { if (Set(ref pattern, value ?? "")) changed(); } }
        public string Qz
        {
            get => qz;
            set
            {
                var text = (value ?? "").Trim();
                if (text == qz) return;
                if (Number(text) is null) throw new ArgumentException("Enter qz in kPa, zero or greater.");
                Set(ref qz, text);
                changed();
            }
        }
        /// <summary>qz as a number, or null while the cell does not hold one.</summary>
        public double? Value => Number(qz);
        private static double? Number(string text) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value) && value >= 0 ? value : null;
    }
    internal sealed record StoreyForceRow(string Point, string X, string Area, IReadOnlyList<string> Forces);

    /// <summary>Target forces typed on the Cable Force sheet and not applied yet (cable → kN).</summary>
    private readonly Dictionary<string, double> cableForceEdits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> cableSyncEdits = new(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, double> CableForceEdits => cableForceEdits;

    /// <summary>The load rows of the Storey Force sheet, and the loaded points chosen there (null: the model's).</summary>
    private readonly ObservableCollection<StoreyLoadRow> storeyLoads = [];
    private IReadOnlyList<string>? storeyPoints;
    internal IReadOnlyList<StoreyLoadRow> StoreyLoads => storeyLoads;

    private static string Kn(double value) => (value == 0 ? 0 : value).ToString("#,0.###", CultureInfo.CurrentCulture);     // no "-0"
    private static string Text(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    /// <summary>Fills the Storey Force inputs from the model. A load with a range on 01 Loading shows its first value —
    /// the one the model is analysed with.</summary>
    private void LoadStoreyInputs()
    {
        var state = viewModel.State;
        StoreyWidthBox.Text = Text(state.TributaryWidthB);
        StoreyCountBox.Text = Text(state.NumberOfStories);
        storeyPoints = null;
        storeyLoads.Clear();
        foreach (var definition in state.LoadDefinitions)
            storeyLoads.Add(new(definition.LoadName, definition.CandidateCount > 0 ? Text(definition.CandidateValues[0]) : "",
                definition.CandidateCount > 1
                    ? $"A range on 01 Loading ({Text(definition.CandidateValues[0])} … {Text(definition.CandidateValues[^1])} kPa): the model uses its first value. A value typed here makes it fixed."
                    : "qz [kPa] of this load", Refresh));
        StoreyLoadGrid.ItemsSource = storeyLoads;
    }

    /// <summary>The storey loads in the form — tributary width, storeys and one qz per load pattern — or the problems.</summary>
    internal (double Width, double Stories, IReadOnlyList<(string Pattern, double Qz)> Loads)? StoreyInputs(out IReadOnlyList<string> problems)
    {
        var found = new List<string>();
        static double? Parsed(TextBox box) =>
            double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value) ? value : null;
        var width = Parsed(StoreyWidthBox);
        var stories = Parsed(StoreyCountBox);
        if (width is not > 0) found.Add("Storey Force · tributary width B: enter a number greater than zero.");
        if (stories is not >= 1 || stories != Math.Floor(stories.Value)) found.Add("Storey Force · NStories: enter a whole number, at least 1.");
        foreach (var row in storeyLoads)
        {
            if (string.IsNullOrWhiteSpace(row.Pattern)) found.Add("Storey Force: choose the load pattern of every load (or remove the row).");
            else if (row.Value is null) found.Add($"Storey Force · {row.Pattern}: enter qz in kPa, zero or greater.");
        }
        foreach (var repeated in storeyLoads.Where(row => !string.IsNullOrWhiteSpace(row.Pattern))
                     .GroupBy(row => row.Pattern, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            found.Add($"Storey Force · {repeated.Key}: a load pattern can be loaded once.");
        problems = found.Distinct().ToArray();
        if (problems.Count > 0) return null;
        return (width!.Value, stories!.Value, storeyLoads.Select(row => (row.Pattern, row.Value!.Value)).ToArray());
    }

    /// <summary>The target force of every cable and the force applied at every loaded point, as the form has them now
    /// (with the tributary lengths of the previewed geometry).</summary>
    private void ShowForces(VcfGeometryAnalysis model, GeometryModel geometry)
    {
        var state = viewModel.State;
        var cables = PreviewCableConfigs(geometry);
        var rows = new List<CableForceRow>();
        var ranged = 0;
        foreach (var name in geometry.Members.Where(member => member.Kind != MemberKind.Frame).Select(member => member.Name).Order(StringComparer.Ordinal))
        {
            var cable = cables.FirstOrDefault(item => item.CableName == name);
            if (cable is null) { rows.Add(new(name, "", "", "not on 03 Pretension", false, "Add the cable on 03 Pretension to give it a target force.", StageCableForce)); continue; }
            var sync = cable.SyncSummary;
            var forces = cable.IsFollower ? CableSyncGraph.ResolvedForces(cable, cables) : cable.GetCandidateForces();
            // one value: the first of a 03 Pretension range is the one the model is analysed with
            var value = forces.Count == 0 ? "—" : Kn(forces[0]);
            if (cable.IsFollower && !cable.IsSameStepFollower)
                // the value comes from the cable it follows; once that is edited here the new value shows after Apply
                rows.Add(new(name, cable.LoadPattern, sync, value, false,
                    $"Follows {cable.ReferenceCableName}: choose Independent in Sync to enter its own force.", StageCableForce));
            else
            {
                var range = forces.Count > 1;
                if (range) ranged++;
                rows.Add(new(name, cable.LoadPattern, sync, cableForceEdits.TryGetValue(name, out var typed) ? Kn(typed) : value, true,
                    range ? $"A range on 03 Pretension ({Kn(forces.Min())} … {Kn(forces.Max())} kN): the model uses its first value. A value typed here makes the target fixed."
                        : "Target force [kN] of this cable.", StageCableForce));
            }
        }
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var cable = cables.First(item => item.CableName == row.Cable);
            var choices = new List<string> { "Independent" };
            foreach (var other in cables.Where(item => item.CableName != row.Cable))
            {
                var node = other;
                var visited = new HashSet<string>();
                while (node.IsFollower && visited.Add(node.CableName) && node.ReferenceCableName != row.Cable)
                {
                    var next = cables.FirstOrDefault(item => item.CableName == node.ReferenceCableName);
                    if (next is null) break;
                    node = next;
                }
                if (node.ReferenceCableName != row.Cable) choices.Add(other.CableName);
            }
            rows[index] = new(row.Cable, row.Pattern, row.Sync, row.Force, row.Editable, row.Hint, StageCableForce)
            {
                SyncChoices = choices,
                SyncSelection = cable.IsFollower ? cable.ReferenceCableName! : "Independent",
                SyncEdited = reference => StageCableSync(row.Cable, reference, CableSyncGraph.ResolvedForces(cable, cables).FirstOrDefault())
            };
        }
        CableForceGrid.ItemsSource = rows;
        CableForceSummary.Text = $"{rows.Count} cables" +
            (ranged == 0 ? "" : $" · {ranged} with a range on 03 Pretension (the model uses the first value)") +
            (cableForceEdits.Count + cableSyncEdits.Count == 0 ? "" : $" · {cableForceEdits.Keys.Concat(cableSyncEdits.Keys).Distinct().Count()} edited, not applied yet");

        // Storey force: the loaded points (all top chord points stay loaded when every one was), tributary length from the preview
        var applied = storeyPoints ?? state.AppliedPoints.Select(point => point.PointName).ToArray();
        var top = state.Truss.TopPoints;
        var allTop = top.Count > 0 && applied.Count == top.Count && top.All(applied.Contains);
        IEnumerable<string> candidates = topology is not null && allTop ? model.TopPoints
            : applied.Select(name => topology is not null && topology.RenamedPoints.TryGetValue(name, out var renamed) ? renamed : name);
        var names = candidates.Where(geometry.Points.ContainsKey).Distinct(StringComparer.Ordinal)
            .OrderBy(name => geometry.Points[name].Position.X).ThenBy(name => name, StringComparer.Ordinal).ToArray();
        // the form's own values; an input that does not read as a number yet leaves its column empty
        var width = double.TryParse(StoreyWidthBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var b) && b > 0 ? b : 0;
        var stories = double.TryParse(StoreyCountBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var n) && n >= 1 ? Math.Truncate(n) : 0;
        var loads = storeyLoads.Where(row => !string.IsNullOrWhiteSpace(row.Pattern)).ToArray();
        var storeyRows = new List<StoreyForceRow>();
        var totals = new double[loads.Length];
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
            var area = length * width;
            var forces = new List<string>();
            for (var d = 0; d < loads.Length; d++)
            {
                if (loads[d].Value is not { } qz) { forces.Add("—"); continue; }
                var force = -qz * area * stories;
                forces.Add(Kn(force));
                totals[d] += force;
            }
            storeyRows.Add(new(names[index], Text(X(index)), Text(area), forces));
        }
        StoreyForceGrid.Columns.Clear();
        StoreyForceGrid.Columns.Add(new DataGridTextColumn { Header = "Point", Binding = new System.Windows.Data.Binding(nameof(StoreyForceRow.Point)), MinWidth = 70 });
        StoreyForceGrid.Columns.Add(new DataGridTextColumn { Header = "X [m]", Binding = new System.Windows.Data.Binding(nameof(StoreyForceRow.X)), ElementStyle = (Style)FindResource("NumericCell") });
        StoreyForceGrid.Columns.Add(new DataGridTextColumn { Header = "Trib. area [m²]", Binding = new System.Windows.Data.Binding(nameof(StoreyForceRow.Area)), ElementStyle = (Style)FindResource("NumericCell") });
        for (var d = 0; d < loads.Length; d++)
            // a TextBlock, so an underscore in the pattern name is shown (a plain header takes it as an access key)
            StoreyForceGrid.Columns.Add(new DataGridTextColumn { Header = new TextBlock { Text = $"{loads[d].Pattern} Fz [kN]" }, MinWidth = 90,
                Binding = new System.Windows.Data.Binding($"{nameof(StoreyForceRow.Forces)}[{d}]"), ElementStyle = (Style)FindResource("NumericCell") });
        StoreyForceGrid.ItemsSource = storeyRows;
        StoreyForceCaption.Text = "The storey loads of the model: one qz per load pattern (a range belongs to Run → Optimization VCF). " +
            "Force at each loaded point: Fz = −qz × trib. length × B × NStories. Apply / OK writes them to the model.";
        // the loaded points show on the preview — an arrow at each — and are chosen there or from the list
        Preview.AppliedPoints = names.Select(Overlay).ToArray();
        StoreyPointsTitle.Text = $"LOADED POINTS ({names.Length})";
        StoreyPointsPanel.ToolTip = topology is null ? null
            : "The number of bays is changed in the form: apply it first, then choose the loaded points of the new model.";
        UpdateStoreyPointActions();
        StoreyForceSummary.Text = (names.Length == 0 ? "No loaded points — + Add them from the list, or select points on the preview and press + Add Selected."
            : $"{names.Length} points" + (loads.Length == 0 ? " · no load" : " · total " + string.Join(" · ", loads.Select((load, d) => $"{load.Pattern} {Kn(totals[d])} kN")))) +
            (storeyPoints is null ? "" : " · points changed, not applied yet");
    }

    private void StoreyInput_Changed(object sender, TextChangedEventArgs e) => Refresh();

    internal StoreyLoadRow AddStoreyLoad()
    {
        var used = storeyLoads.Select(row => row.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var row = new StoreyLoadRow(viewModel.State.LoadPatterns.FirstOrDefault(pattern => !used.Contains(pattern)) ?? "", "0", "qz [kPa] of this load", Refresh);
        storeyLoads.Add(row);
        Refresh();
        return row;
    }

    internal void RemoveStoreyLoad(StoreyLoadRow row)
    {
        if (storeyLoads.Remove(row)) Refresh();
    }

    private void AddStoreyLoad_Click(object sender, RoutedEventArgs e) => AddStoreyLoad();
    private void RemoveStoreyLoad_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StoreyLoadRow row }) RemoveStoreyLoad(row);
    }

    // ---- the loaded points: the user chooses where the storey loads act ----
    // From a list (+ Add), or on the preview as in SAP2000 — click a point or drag a window around several, then
    // + Add Selected; - Remove takes the load off the points selected in the table or on the preview.

    private readonly Dictionary<string, AppliedPoint> pointOverlays = new(StringComparer.Ordinal);
    /// <summary>The arrow drawn at a loaded point on the preview (one object per point, so its highlight is kept).</summary>
    private AppliedPoint Overlay(string name) =>
        pointOverlays.TryGetValue(name, out var point) ? point : pointOverlays[name] = new AppliedPoint { PointName = name };

    /// <summary>The loaded points as the form has them: the ones chosen here, else the model's.</summary>
    internal IReadOnlyList<string> StoreyPoints => storeyPoints ?? viewModel.State.AppliedPoints.Select(point => point.PointName).ToArray();

    /// <summary>Stages the loaded points; Apply / OK writes them to the model.</summary>
    internal void SetStoreyPoints(IReadOnlyList<string> points)
    {
        storeyPoints = points.Distinct(StringComparer.Ordinal).ToArray();
        Refresh();
    }

    internal void AddStoreyPoints(IEnumerable<string> points) => SetStoreyPoints([.. StoreyPoints, .. points]);

    internal void RemoveStoreyPoints(IEnumerable<string> points)
    {
        var gone = points.ToHashSet(StringComparer.Ordinal);
        SetStoreyPoints(StoreyPoints.Where(name => !gone.Contains(name)).ToArray());
    }

    /// <summary>Loads the points selected on the preview.</summary>
    internal void AddSelectedStoreyPoints()
    {
        var picked = Preview.SelectedJoints;
        if (picked.Count == 0 || topology is not null) return;
        AddStoreyPoints(picked);
        Preview.ClearSelection();
    }

    /// <summary>Takes the load off the points selected in the table and off the loaded points selected on the preview.</summary>
    internal void RemoveSelectedStoreyPoints()
    {
        if (topology is not null) return;
        var loaded = StoreyPoints.ToHashSet(StringComparer.Ordinal);
        var names = StoreyForceGrid.SelectedItems.OfType<StoreyForceRow>().Select(row => row.Point)
            .Concat(Preview.SelectedJoints.Where(loaded.Contains)).ToArray();
        if (names.Length == 0) return;
        RemoveStoreyPoints(names);
        Preview.ClearSelection();
    }

    /// <summary>What the loaded-point buttons can do now: with the bays changed in the form the points are those of
    /// another model, so they wait for Apply.</summary>
    private void UpdateStoreyPointActions()
    {
        var editable = topology is null && analysis is not null;
        var loaded = StoreyPoints.ToHashSet(StringComparer.Ordinal);
        var picked = Preview.SelectedJoints;
        var fresh = picked.Count(name => !loaded.Contains(name));
        StoreyAddPointsButton.IsEnabled = editable;
        StoreyAddSelectedButton.IsEnabled = editable && fresh > 0;
        StoreyAddSelectedButton.Content = fresh > 0 ? $"+ Add Selected ({fresh})" : "+ Add Selected";
        StoreyRemovePointsButton.IsEnabled = editable && (StoreyForceGrid.SelectedItems.Count > 0 || picked.Any(loaded.Contains));
    }

    private void AddStoreyPoints_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.ChooseLoadPoints(StoreyPoints) is { Count: > 0 } chosen) AddStoreyPoints(chosen);
    }
    private void AddSelectedStoreyPoints_Click(object sender, RoutedEventArgs e) => AddSelectedStoreyPoints();
    private void RemoveStoreyPoints_Click(object sender, RoutedEventArgs e) => RemoveSelectedStoreyPoints();

    private void StoreyForceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Preview is null) return;
        // the point picked in the table is the highlighted one on the preview
        Preview.SelectedAppliedPoint = StoreyForceGrid.SelectedItem is StoreyForceRow row ? Overlay(row.Point) : null;
        UpdateStoreyPointActions();
    }

    /// <summary>A target force typed on the sheet: kept until Apply / OK, and the rows that depend on it are shown again.</summary>
    private void StageCableForce(string cable, double forceKN)
    {
        cableForceEdits[cable] = forceKN;
        // redraw after the cell has finished its edit: followers and new fan cables of this cable change with it
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            if (working is not null && preview is not null) ShowForces(working, preview);
        }));
    }

    private void StageCableSync(string cable, string reference, double currentForce)
    {
        cableSyncEdits[cable] = reference;
        if (reference == "Independent") cableForceEdits[cable] = currentForce;
        Dispatcher.BeginInvoke(new Action(Refresh));
    }

    private CablePretensionConfig[] PreviewCableConfigs(GeometryModel geometry)
    {
        var configs = geometry.Members.Where(member => member.Kind != MemberKind.Frame).Select(member =>
        {
            var template = topology is not null && topology.AddedCables.TryGetValue(member.Name, out var sourceName) ? sourceName : member.Name;
            var source = viewModel.State.Cables.FirstOrDefault(item => item.CableName == template);
            return new CablePretensionConfig
            {
                CableName = member.Name, LoadPattern = source?.LoadPattern ?? viewModel.State.LoadPatterns.FirstOrDefault() ?? "Pretension",
                ApplyRange = source?.ApplyRange ?? false, TargetForce = source?.TargetForce ?? 0,
                MinForce = source?.MinForce, MaxForce = source?.MaxForce, StepForce = source?.StepForce,
                SyncMode = source?.SyncMode ?? CableSyncMode.Independent,
                ReferenceCableName = source?.ReferenceCableName, SyncFactor = source?.SyncFactor
            };
        }).ToArray();
        foreach (var cable in configs)
        {
            if (topology?.AddedCables.ContainsKey(cable.CableName) == true && cable.ReferenceCableName is { } oldReference)
            {
                var match = System.Text.RegularExpressions.Regex.Match(cable.CableName, @"^(.*?)(\d+)([abAB])(.*)$");
                if (match.Success)
                {
                    var opposite = match.Groups[3].Value.ToLowerInvariant() == "a" ? "b" : "a";
                    var partner = $"{match.Groups[1].Value}{match.Groups[2].Value}{opposite}{match.Groups[4].Value}";
                    if (topology.AddedCables.TryGetValue(partner, out var partnerSource) && partnerSource == oldReference)
                        cable.ReferenceCableName = partner;
                }
            }
            if (cable.ReferenceCableName is { } reference && configs.All(item => item.CableName != reference))
            { cable.SyncMode = CableSyncMode.Independent; cable.TargetForce ??= 0; }
            if (cableSyncEdits.TryGetValue(cable.CableName, out var selected))
            {
                cable.SyncMode = selected == "Independent" ? CableSyncMode.Independent : CableSyncMode.SameValues;
                cable.ReferenceCableName = selected == "Independent" ? null : selected;
            }
            if (cableForceEdits.TryGetValue(cable.CableName, out var force))
            {
                if (cable.IsSameStepFollower) cable.MinForce = cable.MaxForce = force;
                else if (!cable.IsFollower) { cable.ApplyRange = false; cable.TargetForce = force; }
            }
        }
        return configs;
    }

    internal IReadOnlyList<CableForceRow> CableForces => (IReadOnlyList<CableForceRow>?)CableForceGrid.ItemsSource ?? [];
    internal IReadOnlyList<StoreyForceRow> StoreyForces => (IReadOnlyList<StoreyForceRow>?)StoreyForceGrid.ItemsSource ?? [];
    /// <summary>Types into a geometry input (checks).</summary>
    internal void SetInput(string key, string text) => inputs[key].Text = text;

    private static IEnumerable<string>? SafePins(GeometryModel geometry)
    {
        try { return Mcotis.Desktop.Application.FemStudyService.FindPinSupportPoints(geometry); }
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
        var wasBlank = !viewModel.HasModelGeometry;
        // the loads and cable targets first, so a change of bays carries them over: a new fan cable copies the edited
        // value of the cable it is modelled on, and the loaded points follow the new chord
        if (StoreyInputs(out _) is not { } storey) return;
        viewModel.SetCableTargetForces(cableForceEdits.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        if (!wasBlank) viewModel.SetStoreyLoads(storey.Width, storey.Stories, storey.Loads, storeyPoints);
        await viewModel.ApplyVcfGeometryAsync(VcfGeometry.Build(working!, form), FormSections(), topology);
        if (wasBlank && viewModel.HasModelGeometry)
            viewModel.SetStoreyLoads(storey.Width, storey.Stories, storey.Loads, storeyPoints);
        foreach (var config in PreviewCableConfigs(viewModel.State.Geometry))
        {
            var cable = viewModel.State.Cables.FirstOrDefault(item => item.CableName == config.CableName);
            if (cable is null) { cable = new CablePretensionConfig { CableName = config.CableName, LoadPattern = config.LoadPattern }; viewModel.State.Cables.Add(cable); }
            cable.SyncMode = config.SyncMode;
            cable.ReferenceCableName = config.ReferenceCableName;
            cable.SyncFactor = config.SyncFactor;
        }
        viewModel.SetCableTargetForces(cableForceEdits);
        LoadFromModel();
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => LoadFromModel();
    private async void Apply_Click(object sender, RoutedEventArgs e) => await ApplyFormAsync();
    private async void Ok_Click(object sender, RoutedEventArgs e) { await ApplyFormAsync(); Close(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
