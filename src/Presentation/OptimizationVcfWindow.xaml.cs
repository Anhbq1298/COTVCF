using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using COT_VCF.Application;
using COT_VCF.Domain;

namespace COT_VCF.Presentation;

/// <summary>
/// Run → Optimization VCF. The inputs of Edit → VCF Geometry as a search space: min / max / step for every geometry
/// parameter and the storey force inputs (qz per load definition, NStories, B), the cable pretensions — one shared step
/// for every cable, a min / max per cable, the fan cables by their length (<see cref="VcfOptimization.CableTargets"/>) —
/// and the candidate sections per member role and per cable. The Cable Force rows and the preview follow the corner chosen
/// in Preview at, so the cables a smaller or larger number of bays removes or adds show. OK keeps the settings on the view model
/// (<see cref="MainViewModel.VcfOptimizationSettings"/>); Run Optimization keeps them too and asks the owner to run them
/// (<see cref="RunRequested"/>). The working model is never changed here.
/// </summary>
public partial class OptimizationVcfWindow : Window
{
    private readonly MainViewModel viewModel;
    private VcfGeometryAnalysis? analysis;
    private bool loading;

    /// <summary>One Min / Max / Step row and its value count.</summary>
    private sealed record RangeRow(TextBox Min, TextBox Max, TextBox Step, TextBlock Count, bool Whole);

    /// <summary>A cable's Min / Max inputs on the Cable Force sheet.</summary>
    private sealed record SpanRow(TextBox Min, TextBox Max);

    /// <summary>A row shown on the Cable Force sheet: its inputs, or the read-outs that follow the form and the preview.</summary>
    private sealed record CableRow(string Cable, string Tag, TextBlock Length, SpanRow? Inputs, TextBlock? Min, TextBlock? Max, TextBlock Step);

    private readonly Dictionary<string, RangeRow> ranges = new(StringComparer.Ordinal);        // Geometry
    private readonly Dictionary<string, SpanRow> cableSpans = new(StringComparer.Ordinal);     // Cable Force: every cable with a force of its own
    private readonly Dictionary<string, RangeRow> storeyLoads = new(StringComparer.Ordinal);   // Storey Force: qz by load name, Stories, B
    private readonly Dictionary<VcfMemberRole, CandidatePicker> roleSections = [];
    private readonly Dictionary<string, CandidatePicker> cableSections = new(StringComparer.Ordinal);
    private (TextBox Min, TextBox Max)? middle;     // read-only: the middle chord follows the truss depth
    private SpanRow? fanSpan;                       // Cable Force: the shortest fan cable, while the fan is graded by its length
    private HashSet<string> fanNames = new(StringComparer.Ordinal);                            // the model's fan cables
    private CablePretensionConfig[] cableConfigs = [];                                         // Tab 03 rows
    private IReadOnlyDictionary<string, double> modelForces = new Dictionary<string, double>(); // the model's own pretension per cable
    private readonly List<CableRow> cableRows = [];                                            // the rows now on the Cable Force sheet
    private string cableRowsKey = "";
    private bool relabelling;                       // the Preview at items are being renamed

    private static readonly IReadOnlyDictionary<string, string> NoCables = new Dictionary<string, string>(StringComparer.Ordinal);
    /// <summary><see cref="SetRange"/> key of the shortest fan cable on the "cable" sheet (checks).</summary>
    internal const string FanKey = "Fan";

    public OptimizationVcfWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        SetCorners(["Minimum", "Current", "Maximum"]);
        Load(viewModel.VcfOptimizationSettings);
    }

    /// <summary>The settings confirmed with OK (null on Cancel or when the form has a problem).</summary>
    internal VcfOptimizationSettings? Result { get; private set; }

    /// <summary>True when the dialog closed with Run Optimization.</summary>
    internal bool RunRequested { get; private set; }

    // ---- read the model (and the kept settings) into the form ----
    private void Load(VcfOptimizationSettings? kept)
    {
        loading = true;
        try
        {
            foreach (var grid in new[] { GeometryGrid, FanForceGrid, CableForceGrid, StoreyForceGrid, FrameGrid, CableGrid }) { grid.Children.Clear(); grid.RowDefinitions.Clear(); }
            ranges.Clear(); cableSpans.Clear(); storeyLoads.Clear(); roleSections.Clear(); cableSections.Clear(); middle = null;
            fanSpan = null; cableRows.Clear(); cableRowsKey = "";
            try { analysis = VcfGeometry.Analyze(viewModel.State.Geometry, viewModel.State.Truss); }
            catch (Exception ex)
            {
                analysis = null;
                ProblemText.Text = ex.Message;
                Preview.Model = viewModel.State.Geometry;
                OkButton.IsEnabled = false; RunButton.IsEnabled = false;
                return;
            }

            static VcfRange Kept(IReadOnlyDictionary<string, VcfRange>? saved, string key, VcfRange fallback) =>
                saved is not null && saved.TryGetValue(key, out var range) ? range : fallback;

            // Geometry
            var current = VcfOptimization.Current(analysis);
            AddHeader(GeometryGrid, "Parameter");
            foreach (var (parameter, _) in VcfOptimization.Parameters(analysis))
            {
                ranges[parameter.Key] = AddRange(GeometryGrid, parameter.Label, parameter.Unit,
                    Kept(kept?.Geometry, parameter.Key, current.Geometry[parameter.Key]), parameter.Whole);
                if (parameter == VcfOptimization.Depth && analysis.HasMiddleChord) AddMiddleChord();
            }

            // Cable Force: one shared step; Min / Max for every cable with a force of its own; the fan cables by their length.
            // The rows are laid out by ShowCables — they follow the previewed corner.
            var state = viewModel.State;
            var culture = CultureInfo.CurrentCulture;
            cableConfigs = state.Cables.ToArray();
            var modelCables = VcfOptimization.ModelCableForces(cableConfigs);
            modelForces = VcfOptimization.CableTargets(modelCables, 0, analysis, analysis.Geometry, null, cableConfigs);
            var keptCables = kept?.CableForces;
            foreach (var name in Members(VcfMemberRole.Cable))
                if (modelCables.Cables.TryGetValue(name, out var span))
                    cableSpans[name] = SpanBoxes(name, keptCables is not null && keptCables.Cables.TryGetValue(name, out var saved) ? saved : span);
            var fan = VcfGeometry.FanCables(analysis);
            fanNames = fan.Select(cable => cable.Name).ToHashSet(StringComparer.Ordinal);
            FanForcePanel.Visibility = fan.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (fan.Count > 0)
            {
                var shortest = fan[0].Name;
                var start = modelCables.Cables.TryGetValue(shortest, out var own) ? own
                    : VcfForceSpan.Fixed(modelForces.TryGetValue(shortest, out var force) ? force : 0);
                fanSpan = SpanBoxes("Shortest fan cable", keptCables?.Fan ?? start);
            }
            // the fan starts graded by length (+ kN per m, fitted to the model's own forces)
            FanGradingCombo.SelectedIndex = keptCables?.Grading switch { null or VcfFanGrading.Linear => 0, VcfFanGrading.Proportional => 1, _ => 2 };
            FanGradientBox.Text = (keptCables is { Grading: VcfFanGrading.Linear } ? keptCables.GradientKnPerM : VcfOptimization.FitGradient(analysis, modelForces))
                .ToString("0.###", culture);
            CableStepsBox.Text = (keptCables?.Steps ?? modelCables.Steps).ToString(culture);

            // Storey Force: qz per load definition, number of storeys, tributary width
            AddHeader(StoreyForceGrid, "Input");
            foreach (var load in state.LoadDefinitions.Where(load => !string.IsNullOrWhiteSpace(load.LoadName)))
            {
                if (VcfOptimization.LoadRange(load) is not { } range) { AddNote(StoreyForceGrid, $"qz {load.LoadName}", "no readable qz on 01 Loading"); continue; }
                storeyLoads[load.LoadName] = AddRange(StoreyForceGrid, $"qz {load.LoadName}", "kPa", Kept(kept?.StoreyLoads, load.LoadName, range), whole: false);
            }
            var stories = double.IsFinite(state.NumberOfStories) && state.NumberOfStories >= 1 ? Math.Truncate(state.NumberOfStories) : 1;
            storeyLoads[VcfOptimization.StoriesKey] = AddRange(StoreyForceGrid, "Number of storeys (NStories)", "",
                Kept(kept?.StoreyLoads, VcfOptimization.StoriesKey, VcfRange.Fixed(stories, 1)), whole: true);
            storeyLoads[VcfOptimization.TributaryKey] = AddRange(StoreyForceGrid, "Tributary width B", "m",
                Kept(kept?.StoreyLoads, VcfOptimization.TributaryKey, VcfRange.Fixed(state.TributaryWidthB, 0.5)), whole: false);

            // Sections
            var frames = viewModel.VcfSectionNames(cable: false);
            foreach (var role in new[] { VcfMemberRole.TopChord, VcfMemberRole.MiddleChord, VcfMemberRole.BottomChord,
                         VcfMemberRole.VerticalChord, VcfMemberRole.MegaColumn, VcfMemberRole.EndFrame })
            {
                var members = Members(role);
                if (members.Count == 0) continue;
                var assigned = members.Select(viewModel.MemberSection).Where(name => name is not null).Distinct().ToArray();
                var picker = new CandidatePicker(frames, kept?.RoleSections.TryGetValue(role, out var picked) == true ? picked : [], Refresh)
                    { ToolTip = string.Join(", ", members) };
                roleSections[role] = picker;
                AddRow(FrameGrid, VcfGeometry.Title(role), picker, $"{members.Count} mbr · {(assigned.Length == 1 ? assigned[0] : assigned.Length == 0 ? "—" : "mixed")}");
            }
            var cables = viewModel.VcfSectionNames(cable: true);
            foreach (var name in Members(VcfMemberRole.Cable))
            {
                var picker = new CandidatePicker(cables, kept?.CableSections.TryGetValue(name, out var picked) == true ? picked : [], Refresh);
                cableSections[name] = picker;
                AddRow(CableGrid, name, picker, $"{viewModel.MemberSection(name) ?? "—"}");
            }
            // Deflection check
            var (caseNames, combinationNames) = viewModel.VcfDeflectionResults();
            DeflectionResultCombo.Items.Clear();
            foreach (var name in caseNames.Concat(combinationNames)) DeflectionResultCombo.Items.Add(name);
            var chosen = kept?.DeflectionResult ?? VcfOptimization.DefaultDeflectionResult(caseNames, combinationNames);
            DeflectionResultCombo.SelectedItem = chosen is not null && DeflectionResultCombo.Items.Contains(chosen) ? chosen : null;
            SpanRatioBox.Text = (kept?.SpanRatio ?? VcfOptimization.DefaultSpanRatio).ToString("0.###", CultureInfo.CurrentCulture);

            if (frames.Count == 0 && cables.Count == 0)
                SectionHint.Text = "No sections yet — use File → Read FEM Properties from SAP or Define → Section Properties to add candidates.";
        }
        finally { loading = false; }
        Refresh();
    }

    private IReadOnlyList<string> Members(VcfMemberRole role) =>
        analysis!.Roles.Where(pair => pair.Value == role).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();

    private System.Windows.Media.Brush Muted => (System.Windows.Media.Brush)FindResource("Muted");

    private void AddHeader(Grid grid, string first)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(grid, new TextBlock { Text = first, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 0, 0, 2) }, 0, 0);
        foreach (var (text, column) in new[] { ("Min", 1), ("Max", 2), ("Step", 3), ("n", 5) })
            Place(grid, new TextBlock { Text = text, Style = (Style)FindResource("ColumnHeader") }, 0, column);
    }

    private RangeRow AddRange(Grid grid, string label, string unit, VcfRange range, bool whole)
    {
        TextBox Box(double value, string part)
        {
            var box = new TextBox { Text = value.ToString("0.###", CultureInfo.CurrentCulture), TextAlignment = TextAlignment.Right,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), Margin = new Thickness(0, 2, 4, 2) };
            System.Windows.Automation.AutomationProperties.SetName(box, $"{label} {part}");
            box.TextChanged += (_, _) => Refresh();
            return box;
        }
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var (min, max, step) = (Box(range.Min, "min"), Box(range.Max, "max"), Box(range.Step, "step"));
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), FontSize = 11, Foreground = Muted };
        Place(grid, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }, row, 0);
        Place(grid, min, row, 1);
        Place(grid, max, row, 2);
        Place(grid, step, row, 3);
        Place(grid, new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Foreground = Muted }, row, 4);
        Place(grid, count, row, 5);
        return new RangeRow(min, max, step, count, whole);
    }

    /// <summary>A row with no inputs: the label and why (e.g. a Tab 03 follower's sync rule).</summary>
    private void AddNote(Grid grid, string label, string note)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(grid, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = Muted, Margin = new Thickness(0, 5, 0, 5) }, row, 0);
        var text = new TextBlock { Text = note, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = Muted,
            TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = note, Margin = new Thickness(4, 0, 0, 0) };
        Grid.SetColumnSpan(text, 3);
        Place(grid, text, row, 1);
        Place(grid, new TextBlock { Text = "—", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Muted, FontSize = 11 }, row, 5);
    }

    /// <summary>The middle chord row: no inputs, it shows <see cref="VcfOptimization.MiddleChordRatio"/> × the truss depth range.</summary>
    private void AddMiddleChord()
    {
        TextBox Box(string part)
        {
            var box = new TextBox { IsReadOnly = true, IsTabStop = false, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 2, 4, 2),
                FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"),
                Background = (System.Windows.Media.Brush)FindResource("Surface"), Foreground = Muted,
                ToolTip = $"Fixed at {VcfOptimization.MiddleChordRatio:0.##} × truss depth — not an optimization variable" };
            System.Windows.Automation.AutomationProperties.SetName(box, $"{VcfOptimization.Middle.Label} {part}");
            return box;
        }
        var row = GeometryGrid.RowDefinitions.Count;
        GeometryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var (min, max) = (Box("min"), Box("max"));
        Place(GeometryGrid, new TextBlock { Text = VcfOptimization.Middle.Label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = Muted }, row, 0);
        Place(GeometryGrid, min, row, 1);
        Place(GeometryGrid, max, row, 2);
        Place(GeometryGrid, new TextBlock { Text = $"{VcfOptimization.MiddleChordRatio:0.##} × depth", FontSize = 11, Foreground = Muted,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 0) }, row, 3);
        Place(GeometryGrid, new TextBlock { Text = VcfOptimization.Middle.Unit, VerticalAlignment = VerticalAlignment.Center, Foreground = Muted }, row, 4);
        middle = (min, max);
    }

    private static void AddRow(Grid grid, string label, CandidatePicker picker, string note)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(grid, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, row, 0);
        System.Windows.Automation.AutomationProperties.SetName(picker.Button, label + " candidate sections");
        Place(grid, picker, row, 1);
        Place(grid, new TextBlock { Text = note, ToolTip = "Now assigned: " + note, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("Muted") }, row, 2);
    }

    private static void Place(Grid grid, UIElement element, int row, int column)
    {
        Grid.SetRow(element, row); Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    // ---- the form as settings ----
    private static double Number(TextBox box) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ? value : double.NaN;

    private static VcfRange Range(RangeRow row) => new(Number(row.Min), Number(row.Max), Number(row.Step));

    private static Dictionary<string, VcfRange> Ranges(Dictionary<string, RangeRow> rows) =>
        rows.ToDictionary(pair => pair.Key, pair => Range(pair.Value), StringComparer.Ordinal);

    private VcfFanGrading Grading => fanSpan is null ? VcfFanGrading.Manual
        : FanGradingCombo.SelectedIndex switch { 0 => VcfFanGrading.Linear, 1 => VcfFanGrading.Proportional, _ => VcfFanGrading.Manual };

    private static VcfForceSpan Span(SpanRow row) => new(Number(row.Min), Number(row.Max));

    /// <summary>The Cable Force sheet as settings: while the fan is graded its cables are not listed — the shortest one's
    /// span and the rule give them their force.</summary>
    private VcfCableForces FormCableForces()
    {
        var grading = Grading;
        var graded = grading != VcfFanGrading.Manual;
        var spans = cableSpans.Where(pair => !graded || !fanNames.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => Span(pair.Value), StringComparer.Ordinal);
        var steps = int.TryParse(CableStepsBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var count) ? count : 0;
        return new(steps, spans, grading, graded ? Span(fanSpan!) : null, grading == VcfFanGrading.Linear ? Number(FanGradientBox) : 0);
    }

    /// <summary>The settings in the form (unchecked; see <see cref="VcfOptimization.Validate"/>).</summary>
    internal VcfOptimizationSettings FormSettings() => new(
        Ranges(ranges), FormCableForces(), Ranges(storeyLoads),
        roleSections.Where(pair => pair.Value.Selected.Count > 0).ToDictionary(pair => pair.Key, pair => pair.Value.Selected),
        cableSections.Where(pair => pair.Value.Selected.Count > 0).ToDictionary(pair => pair.Key, pair => pair.Value.Selected, StringComparer.Ordinal))
    {
        DeflectionResult = DeflectionResultCombo.SelectedItem as string,
        SpanRatio = Number(SpanRatioBox),
    };

    private void Refresh()
    {
        if (loading || analysis is null) return;
        var settings = FormSettings();
        var linear = settings.CableForces.Grading == VcfFanGrading.Linear;
        FanGradientBox.Visibility = FanGradientUnit.Visibility = linear ? Visibility.Visible : Visibility.Collapsed;
        foreach (var row in ranges.Values.Concat(storeyLoads.Values))
        {
            var range = Range(row);
            row.Count.Text = range.Problem(row.Whole) is null ? range.Count.ToString(CultureInfo.CurrentCulture) : "—";
        }
        if (middle is { } chord && settings.Geometry.TryGetValue(VcfOptimization.Depth.Key, out var depth))
        {
            string Text(double value) => double.IsFinite(value) ? (VcfOptimization.MiddleChordRatio * value).ToString("0.###", CultureInfo.CurrentCulture) : "—";
            chord.Min.Text = Text(depth.Min);
            chord.Max.Text = Text(depth.Max);
        }
        var problems = VcfOptimization.Validate(analysis, settings);
        SpaceSummary.Text = problems.Count > 0 ? ""
            : $"{settings.GeometryCount:N0} geometry × {settings.ForceCount:N0} force × {settings.SectionCount:N0} section variants = {settings.CombinationCount:N0} combinations";
        ProblemText.Text = string.Join("\n", problems);
        OkButton.IsEnabled = problems.Count == 0;
        RunButton.IsEnabled = problems.Count == 0 && settings.CombinationCount <= VcfOptimizationRunner.MaxCases && !viewModel.IsBusy;
        RunButton.ToolTip = settings.CombinationCount > VcfOptimizationRunner.MaxCases
            ? $"{settings.CombinationCount:N0} combinations — one run takes at most {VcfOptimizationRunner.MaxCases:N0}"
            : "Analyse every combination with the internal FEM and list the max deflection of each case (the working model is not changed)";
        ShowStoreyTotals(settings, problems.Count == 0);
        ShowLimit(settings, problems.Count == 0);
        ShowCorners(settings, problems.Count == 0);
        ShowPreview(settings, problems.Count == 0);
    }

    /// <summary>Total Fz per load definition at the lowest and highest corner: −qz × loaded length × B × NStories. The
    /// tributary lengths of the loaded points add up to the distance between the outer loaded points — the truss span when
    /// every top chord point is loaded, else the loaded length of the model scaled with the bay width.</summary>
    private void ShowStoreyTotals(VcfOptimizationSettings settings, bool valid)
    {
        var state = viewModel.State;
        var applied = state.AppliedPoints.Select(point => point.PointName).Where(state.Geometry.Points.ContainsKey).ToArray();
        if (applied.Length == 0) { StoreyForceSummary.Text = "No loaded points on 01 Loading."; return; }
        if (!valid || analysis is null) { StoreyForceSummary.Text = ""; return; }
        var top = state.Truss.TopPoints;
        var allTop = top.Count > 0 && applied.Length == top.Count && top.All(applied.Contains);
        var xs = applied.Select(name => state.Geometry.Points[name].Position.X).ToArray();
        var loadedNow = xs.Max() - xs.Min();
        double Length(Func<VcfRange, double> pick)
        {
            var bay = pick(settings.Geometry[VcfOptimization.BayWidth.Key]);
            return allTop ? pick(settings.Geometry[VcfOptimization.Bays.Key]) * bay : loadedNow * bay / analysis.Parameters.BayWidth;
        }
        var stories = settings.StoreyLoads[VcfOptimization.StoriesKey];
        var width = settings.StoreyLoads[VcfOptimization.TributaryKey];
        static string Kn(double value) => value.ToString("N0", CultureInfo.CurrentCulture);
        var totals = settings.StoreyLoads.Where(pair => pair.Key is not VcfOptimization.StoriesKey and not VcfOptimization.TributaryKey).Select(pair =>
        {
            double At(Func<VcfRange, double> pick) => -pick(pair.Value) * Length(pick) * pick(width) * pick(stories);
            var (low, high) = (At(range => range.Min), At(range => range.Max));
            return $"{pair.Key} {(low == high ? Kn(low) : $"{Kn(low)} … {Kn(high)}")} kN";
        });
        StoreyForceSummary.Text = $"{applied.Length} loaded points · total Fz (min … max corner): " + string.Join(" · ", totals);
    }

    /// <summary>The limit at the shortest and longest span of the ranges: span / ratio.</summary>
    private void ShowLimit(VcfOptimizationSettings settings, bool valid)
    {
        if (!valid) { LimitText.Text = ""; return; }
        double Span(Func<VcfRange, double> pick) => pick(settings.Geometry[VcfOptimization.Bays.Key]) * pick(settings.Geometry[VcfOptimization.BayWidth.Key]);
        var (low, high) = (Span(range => range.Min) * 1000 / settings.SpanRatio, Span(range => range.Max) * 1000 / settings.SpanRatio);
        LimitText.Text = "limit " + (Math.Abs(high - low) < 0.05 ? $"{low:0.0} mm" : $"{low:0.0} … {high:0.0} mm") +
            (settings.DeflectionResult is null ? " · no result chosen: largest over every solved result" : "");
    }

    private void DeflectionCheck_Changed(object sender, RoutedEventArgs e) => Refresh();
    private void CableForce_Changed(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>Names the Preview at corners after what the number of bays does to the fan: fewer bays have fewer fan
    /// cables, more bays more. With the bays fixed at the model's they stay Minimum / Current / Maximum.</summary>
    private void ShowCorners(VcfOptimizationSettings settings, bool valid)
    {
        if (analysis is null || !valid) return;    // keep the names while the ranges are being typed
        var bays = settings.Geometry[VcfOptimization.Bays.Key];
        var (min, max, current) = ((int)bays.Min, (int)bays.Max, analysis.Bays);
        if (min == current && max == current) { SetCorners(["Minimum", "Current", "Maximum"]); return; }
        string Corner(int count, string plain) =>
            (fanNames.Count == 0 || count / 2 == current / 2 ? plain : count / 2 < current / 2 ? "Less cables" : "More cables") + $" · {count} bays";
        SetCorners([Corner(min, "Minimum"), $"Current · {current} bays", Corner(max, "Maximum")]);
    }

    private void SetCorners(string[] labels)
    {
        if (PreviewCorner.Items.OfType<string>().SequenceEqual(labels)) return;
        relabelling = true;
        try
        {
            var index = PreviewCorner.SelectedIndex;
            PreviewCorner.Items.Clear();
            foreach (var label in labels) PreviewCorner.Items.Add(label);
            PreviewCorner.SelectedIndex = index < 0 ? 1 : index;
        }
        finally { relabelling = false; }
    }

    private void ShowPreview(VcfOptimizationSettings settings, bool valid)
    {
        if (analysis is null) return;
        Func<VcfRange, double>? pick = PreviewCorner.SelectedIndex switch { 0 => range => range.Min, 2 => range => range.Max, _ => null };
        if (pick is not null && !valid) return;    // keep the last preview while the ranges are being typed
        try
        {
            var model = analysis;
            VcfTopology? topology = null;
            var form = VcfOptimization.ParametersAt(analysis, VcfOptimization.Current(analysis), range => range.Min);   // the model, middle chord at the rule
            if (pick is not null)
            {
                var bays = (int)pick(settings.Geometry[VcfOptimization.Bays.Key]);
                if (bays != analysis.Bays) { topology = VcfGeometry.ChangeBays(analysis, bays); model = VcfGeometry.Analyze(topology); }
                form = VcfOptimization.ParametersAt(analysis, settings, pick);
            }
            var geometry = VcfGeometry.Build(model, form);
            Preview.SpanPoints = model.TopPoints.Concat(model.MiddlePoints).Concat(model.BottomPoints).ToArray();
            Preview.Model = geometry;
            try { Preview.PinSupportPoints = FemStudyService.FindPinSupportPoints(geometry); } catch { Preview.PinSupportPoints = null; }
            ShowCables(settings.CableForces, model, topology, geometry, model.Bays * form.BayWidth);
        }
        catch (InvalidOperationException ex) { ProblemText.Text = ex.Message; }
    }

    // ---- Cable Force rows and the preview's cables: they follow the previewed corner ----
    /// <summary>
    /// Lays out the Cable Force rows for the previewed model — the fan cables of its number of bays (a cable that number
    /// adds is marked new) and the other cables — and fills their forces at the first and the last step; labels every
    /// cable of the preview with that range and emphasises the new ones. A cable that takes another's value (Same Values on
    /// 03 Pretension; the far side of a graded fan) is not listed. The rows are rebuilt only when their set changes, so
    /// typing in one keeps the caret.
    /// </summary>
    private void ShowCables(VcfCableForces forces, VcfGeometryAnalysis model, VcfTopology? topology, GeometryModel geometry, double span)
    {
        var added = topology?.AddedCables ?? NoCables;
        var configs = cableConfigs.GroupBy(cable => cable.CableName, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var fans = VcfGeometry.FanCables(model);
        var fanSet = fans.Select(cable => cable.Name).ToHashSet(StringComparer.Ordinal);
        var shortest = fans.Count > 0 ? fans[0] : null;
        bool Mirrors(string name)
        {
            var source = added.TryGetValue(name, out var template) ? template : name;
            return !cableSpans.ContainsKey(source) && configs.TryGetValue(source, out var cable) && cable is { IsFollower: true, SyncMode: CableSyncMode.SameValues };
        }
        (string Name, string Tag, SpanRow? Inputs) Row(string name)
        {
            var graded = forces.Graded && fanSet.Contains(name);
            var inputs = graded ? name == shortest?.Name ? fanSpan : null : added.ContainsKey(name) ? null : cableSpans.GetValueOrDefault(name);
            var tag = added.ContainsKey(name) ? "new"
                : graded ? name == shortest?.Name ? "shortest" : ""
                : inputs is not null ? ""
                : configs.TryGetValue(name, out var cable) ? cable.IsFollower ? cable.SyncSummary : "no force on 03 Pretension" : "not on 03 Pretension";
            return (name, tag, inputs);
        }
        var fanRows = fans.Where(cable => forces.Graded ? cable.Side == shortest!.Side : !Mirrors(cable.Name)).Select(cable => Row(cable.Name)).ToArray();
        var otherRows = geometry.Members.Where(member => member.Kind != MemberKind.Frame && !fanSet.Contains(member.Name) && !Mirrors(member.Name))
            .Select(member => member.Name).Order(StringComparer.Ordinal).Select(Row).ToArray();

        static string Key(IEnumerable<(string Name, string Tag, SpanRow? Inputs)> rows) =>
            string.Join("|", rows.Select(row => $"{row.Name}:{row.Tag}:{(row.Inputs is null ? 0 : 1)}"));
        var key = Key(fanRows) + "||" + Key(otherRows);
        if (key != cableRowsKey)
        {
            cableRows.Clear();
            BuildCableRows(FanForceGrid, fanRows);
            BuildCableRows(CableForceGrid, otherRows);
            cableRowsKey = key;
        }

        var valid = VcfOptimization.CableProblems(forces).Count == 0;
        var first = valid ? VcfOptimization.CableTargets(forces, 0, model, geometry, added, cableConfigs) : null;
        var last = valid ? VcfOptimization.CableTargets(forces, forces.StepCount - 1, model, geometry, added, cableConfigs) : null;
        var culture = CultureInfo.CurrentCulture;
        static string Kn(double? value) => value is { } kN ? kN.ToString("N0", CultureInfo.CurrentCulture) : "—";
        foreach (var row in cableRows)
        {
            double? min = first is not null && first.TryGetValue(row.Cable, out var low) ? low : null;
            double? max = last is not null && last.TryGetValue(row.Cable, out var high) ? high : null;
            row.Length.Text = VcfOptimization.CableLength(geometry, row.Cable) is > 0 and var length ? length.ToString("0.0", culture) : "—";
            if (row.Min is not null) row.Min.Text = Kn(min);
            if (row.Max is not null) row.Max.Text = Kn(max);
            row.Step.Text = min is { } a && max is { } b && forces.StepCount > 1 && Math.Abs(b - a) > 1e-9 ? Kn((b - a) / (forces.StepCount - 1)) : "—";
        }

        var rounding = VcfOptimization.ForceRoundingKn.ToString("0", culture);
        FanForceNote.Text = forces.Grading switch
        {
            VcfFanGrading.Linear => $"T = T shortest + increase × (L − L shortest), rounded to {rounding} kN. The other side takes the same by its own length.",
            VcfFanGrading.Proportional => $"T = T shortest × L / L shortest, rounded to {rounding} kN. The other side takes the same by its own length.",
            _ => $"A range per fan cable. A cable more bays add takes the force of the cable it copies × its length ratio, rounded to {rounding} kN.",
        };
        CableForceNote.Text = "The rows follow Preview at. A cable that takes another's value on 03 Pretension (Same Values) is not listed." +
            (valid && forces.Steps == 1 && forces.Varies ? " With 1 step every cable stays at its Min — set 2 or more steps to reach Max." : "");

        // the preview: a fan cable's force under the bottom-chord node it is anchored at (a bay apart — along the cables the
        // labels would run into each other at the corner), any other cable's along it; the cables the bays add emphasised
        Preview.HighlightedMembers = added.Keys.ToArray();
        Dictionary<string, string>? memberNotes = null, jointNotes = null;
        if (first is not null && last is not null)
        {
            (memberNotes, jointNotes) = (new(StringComparer.Ordinal), new(StringComparer.Ordinal));
            var bottom = model.BottomPoints.ToHashSet(StringComparer.Ordinal);
            foreach (var member in geometry.Members)
            {
                if (!first.TryGetValue(member.Name, out var low) || !last.TryGetValue(member.Name, out var high)) continue;
                var note = Controls.ResultDisplayText.MeganewtonRange(low, high);
                if (!fanSet.Contains(member.Name)) { memberNotes[member.Name] = note; continue; }
                var anchor = bottom.Contains(member.End) ? member.End : member.Start;
                jointNotes[anchor] = jointNotes.TryGetValue(anchor, out var other) && other != note ? $"{other} / {note}" : note;   // the mid-span node holds a cable of each side
            }
        }
        Preview.MemberNotes = memberNotes;
        Preview.JointNotes = jointNotes;

        var gone = Members(VcfMemberRole.Cable).Where(name => !model.Roles.ContainsKey(name)).ToArray();
        var sides = fans.GroupBy(cable => cable.Side).Select(side => side.Count().ToString(culture));
        PreviewCaption.Text = $"{model.Bays} bays · span {span.ToString("0.###", culture)} m" +
            (fans.Count > 0 ? " · fan cables " + string.Join(" + ", sides) : "") +
            (added.Count > 0 ? " · new: " + string.Join(", ", fans.Select(cable => cable.Name).Where(added.ContainsKey)) : "") +
            (gone.Length > 0 ? " · not in this variant: " + string.Join(", ", gone) : "") +
            (memberNotes is { Count: > 0 } || jointNotes is { Count: > 0 } ? " · labels: pretension [MN]" + (forces.StepCount > 1 ? ", first–last step" : "") : "");
    }

    private void BuildCableRows(Grid grid, IReadOnlyList<(string Name, string Tag, SpanRow? Inputs)> rows)
    {
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(grid, new TextBlock { Text = "Cable", Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 0, 0, 2) }, 0, 0);
        foreach (var (text, column) in new[] { ("L [m]", 1), ("Model", 2), ("Min", 3), ("Max", 4), ("Step", 5) })
            Place(grid, new TextBlock { Text = text, Style = (Style)FindResource("ColumnHeader") }, 0, column);
        var mono = (System.Windows.Media.FontFamily)FindResource("MonoFont");
        TextBlock Value(string text, Thickness margin, bool muted = true) => new()
        {
            Text = text, FontFamily = mono, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = margin, Foreground = muted ? Muted : (System.Windows.Media.Brush)FindResource("Text"),
        };
        foreach (var (name, tag, inputs) in rows)
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = 24 });
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = tag.Length > 0 ? $"{name} · {tag}" : name };
            label.Inlines.Add(new System.Windows.Documents.Run(name) { Foreground = inputs is null && tag != "new" ? Muted : (System.Windows.Media.Brush)FindResource(tag == "new" ? "Accent" : "Text") });
            if (tag.Length > 0) label.Inlines.Add(new System.Windows.Documents.Run("  " + tag) { FontSize = 10.5, Foreground = tag == "new" ? (System.Windows.Media.Brush)FindResource("Accent") : Muted });
            Place(grid, label, row, 0);
            var length = Value("", new Thickness(0, 0, 4, 0));
            Place(grid, length, row, 1);
            Place(grid, Value(modelForces.TryGetValue(name, out var model) ? model.ToString("N0", CultureInfo.CurrentCulture) : "—", new Thickness(0, 0, 8, 0)), row, 2);
            TextBlock? min = null, max = null;
            if (inputs is not null) { Place(grid, inputs.Min, row, 3); Place(grid, inputs.Max, row, 4); }
            else
            {
                (min, max) = (Value("", new Thickness(0, 0, 9, 0), muted: false), Value("", new Thickness(0, 0, 9, 0), muted: false));
                Place(grid, min, row, 3);
                Place(grid, max, row, 4);
            }
            var step = Value("", new Thickness(0, 0, 2, 0));
            Place(grid, step, row, 5);
            cableRows.Add(new CableRow(name, tag, length, inputs, min, max, step));
        }
    }

    /// <summary>A cable's Min / Max inputs, kept for as long as the form is — the rows that show them come and go with the preview.</summary>
    private SpanRow SpanBoxes(string label, VcfForceSpan span)
    {
        TextBox Box(double value, string part)
        {
            var box = new TextBox { Text = value.ToString("0.###", CultureInfo.CurrentCulture), TextAlignment = TextAlignment.Right,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), Margin = new Thickness(0, 2, 4, 2) };
            System.Windows.Automation.AutomationProperties.SetName(box, $"{label} {part}");
            box.TextChanged += (_, _) => Refresh();
            return box;
        }
        return new SpanRow(Box(span.Min, "min"), Box(span.Max, "max"));
    }

    /// <summary>The middle chord values shown (checks): min and max, or null when the model has no middle chord.</summary>
    internal (string Min, string Max)? MiddleChordShown => middle is { } chord ? (chord.Min.Text, chord.Max.Text) : null;

    /// <summary>The storey force totals line (checks).</summary>
    internal string StoreyTotals => StoreyForceSummary.Text;

    /// <summary>Types into a range input (checks): sheet "geometry", "cable" or "storey"; part "min", "max" or "step". On
    /// the "cable" sheet the key is a cable with a force of its own, or <see cref="FanKey"/> for the shortest fan cable of a
    /// graded fan; a cable has no step of its own.</summary>
    internal void SetRange(string key, string part, string text, string sheet = "geometry")
    {
        if (sheet == "cable")
        {
            var span = key == FanKey ? fanSpan! : cableSpans[key];
            (part == "max" ? span.Max : span.Min).Text = text;
            return;
        }
        var row = (sheet == "storey" ? storeyLoads : ranges)[key];
        (part switch { "min" => row.Min, "max" => row.Max, _ => row.Step }).Text = text;
    }

    /// <summary>The Cable Force rows now shown (checks): cable, tag ("shortest", "new", a sync rule …), min, max and step as displayed.</summary>
    internal IReadOnlyList<(string Cable, string Tag, string Min, string Max, string Step)> CableForceRows =>
        cableRows.Select(row => (row.Cable, row.Tag, row.Inputs?.Min.Text ?? row.Min!.Text, row.Inputs?.Max.Text ?? row.Max!.Text, row.Step.Text)).ToArray();

    // ---- actions ----
    private void PreviewCorner_Changed(object sender, SelectionChangedEventArgs e) { if (!relabelling) Refresh(); }
    private void Reset_Click(object sender, RoutedEventArgs e) => Load(null);

    private void Ok_Click(object sender, RoutedEventArgs e) => Confirm(run: false);
    private void Run_Click(object sender, RoutedEventArgs e) => Confirm(run: true);

    private void Confirm(bool run)
    {
        if (analysis is null) return;
        var settings = FormSettings();
        if (VcfOptimization.Validate(analysis, settings).Count > 0) return;
        Result = settings;
        RunRequested = run;
        DialogResult = true;
    }

    /// <summary>A compact multi-select: a drop-down button naming the ticked sections, with a check list in its popup.</summary>
    private sealed class CandidatePicker : Grid
    {
        private readonly List<CheckBox> boxes = [];
        private readonly ToggleButton button;
        public ToggleButton Button => button;

        public CandidatePicker(IReadOnlyList<string> names, IReadOnlyList<string> selected, Action changed)
        {
            Margin = new Thickness(0, 2, 6, 2);
            IsEnabled = names.Count > 0;
            button = new ToggleButton { HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 0, 6, 0), MinHeight = 24 };
            var list = new StackPanel { Margin = new Thickness(6) };
            foreach (var name in names.Concat(selected.Except(names, StringComparer.Ordinal)))
            {
                var box = new CheckBox { Content = new TextBlock { Text = name }, Tag = name, IsChecked = selected.Contains(name, StringComparer.Ordinal), Margin = new Thickness(0, 2, 0, 2) };
                box.Checked += (_, _) => { Caption(); changed(); };
                box.Unchecked += (_, _) => { Caption(); changed(); };
                boxes.Add(box);
                list.Children.Add(box);
            }
            var popup = new Popup
            {
                PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = false,
                Child = new Border
                {
                    Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("Raised"),
                    BorderBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("Border"),
                    BorderThickness = new Thickness(1),
                    Child = new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                },
            };
            popup.SetBinding(Popup.IsOpenProperty, new System.Windows.Data.Binding(nameof(ToggleButton.IsChecked)) { Source = button, Mode = System.Windows.Data.BindingMode.TwoWay });
            popup.Opened += (_, _) => popup.MinWidth = button.ActualWidth;
            Children.Add(button);
            Children.Add(popup);
            Caption();
        }

        public IReadOnlyList<string> Selected => boxes.Where(box => box.IsChecked == true).Select(box => (string)box.Tag).ToArray();

        private void Caption()
        {
            var selected = Selected;
            button.Content = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, Text = selected.Count switch
            {
                0 => "(keep as assigned)",
                <= 3 => $"({selected.Count}) " + string.Join(", ", selected),
                _ => $"({selected.Count}) {string.Join(", ", selected.Take(2))}, …",
            } };
        }
    }
}
