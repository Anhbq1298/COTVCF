using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Etabs;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Presentation;

/// <summary>
/// Optimization → Optimization TAS. The inputs of Edit → Tied Arch System as a search space: min / max / step for H1, H2,
/// the number of bays and their spacing, and for the loads of the storeys above (SW, SDL, LL, floors n, tributary width
/// B), the candidate sections per member role, and the deflection check — as Optimization VCF. OK keeps the settings on
/// the view model (<see cref="MainViewModel.TasOptimizationSettings"/>); Run Optimization keeps them too and asks the owner
/// to run them (<see cref="RunRequested"/>). The open model is never changed here.
/// </summary>
public partial class OptimizationTasWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly TiedArchParameters current;
    private bool loading;

    private sealed record RangeRow(TextBox Min, TextBox Max, TextBox Step, TextBlock Count, bool Whole);
    private readonly Dictionary<string, RangeRow> geometry = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RangeRow> loads = new(StringComparer.Ordinal);
    private readonly Dictionary<TiedArchRole, CandidatePicker> roleSections = [];

    public OptimizationTasWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        current = viewModel.TiedArchParameters ?? TiedArchParameters.Default;
        Load(viewModel.TasOptimizationSettings);
    }

    /// <summary>The settings confirmed with OK or Run Optimization (null on Cancel).</summary>
    internal TasOptimizationSettings? Result { get; private set; }

    /// <summary>True when the dialog closed with Run Optimization.</summary>
    internal bool RunRequested { get; private set; }

    private void Load(TasOptimizationSettings? kept)
    {
        loading = true;
        try
        {
            foreach (var grid in new[] { GeometryGrid, LoadGrid, FrameGrid }) { grid.Children.Clear(); grid.RowDefinitions.Clear(); }
            geometry.Clear(); loads.Clear(); roleSections.Clear();
            // kept settings of a system with (or without) the elevated cable do not fit the other
            var start = kept is not null && kept.ElevatedCable == current.ElevatedCable ? kept : TasOptimization.Start(current);
            AddHeader(GeometryGrid, "Input");
            foreach (var parameter in TasOptimization.GeometryParametersOf(current.ElevatedCable))
                geometry[parameter.Key] = AddRange(GeometryGrid, parameter, start.Geometry[parameter.Key]);
            AddHeader(LoadGrid, "Load");
            foreach (var parameter in TasOptimization.LoadParameters)
                loads[parameter.Key] = AddRange(LoadGrid, parameter, start.Loads[parameter.Key]);

            var frames = viewModel.TasSectionNames();
            foreach (var role in Enum.GetValues<TiedArchRole>().Where(role => role != TiedArchRole.Cable || current.ElevatedCable))
            {
                var names = role == TiedArchRole.Cable ? viewModel.TasCableSectionNames() : frames;
                var picker = new CandidatePicker(names, start.RoleSections.TryGetValue(role, out var picked) ? picked : [], Refresh);
                roleSections[role] = picker;
                var row = FrameGrid.RowDefinitions.Count;
                FrameGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Place(FrameGrid, new TextBlock { Text = TiedArchSystem.Title(role), VerticalAlignment = VerticalAlignment.Center }, row, 0);
                System.Windows.Automation.AutomationProperties.SetName(picker.Button, TiedArchSystem.Title(role) + " candidate sections");
                Place(FrameGrid, picker, row, 1);
                Place(FrameGrid, new TextBlock { Text = TiedArchSystem.RoleSections[role], FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center, Foreground = Muted, ToolTip = "TAS section: " + TiedArchSystem.RoleSections[role] }, row, 2);
            }

            var (caseNames, combinationNames) = viewModel.VcfDeflectionResults();
            DeflectionResultCombo.Items.Clear();
            foreach (var name in caseNames.Concat(combinationNames)) DeflectionResultCombo.Items.Add(name);
            var chosen = kept?.DeflectionResult ?? VcfOptimization.DefaultDeflectionResult(caseNames, combinationNames);
            DeflectionResultCombo.SelectedItem = chosen is not null && DeflectionResultCombo.Items.Contains(chosen) ? chosen : null;
            SpanRatioBox.Text = (kept?.SpanRatio ?? VcfOptimization.DefaultSpanRatio).ToString("0.###", CultureInfo.CurrentCulture);
            UpliftLimitBox.Text = (kept?.UpliftLimitMm ?? VcfOptimization.DefaultUpliftLimitMm).ToString("0.###", CultureInfo.CurrentCulture);
            // Cable Force: the tie of a system with the elevated cable
            CableSheet.IsEnabled = current.ElevatedCable;
            CableNameText.Text = $"{TiedArchSystem.CableName} (tie at H0)";
            CableStepsBox.Text = start.CableSteps.ToString(CultureInfo.CurrentCulture);
            CableMinBox.Text = start.CableForce.Min.ToString("0.###", CultureInfo.CurrentCulture);
            CableMaxBox.Text = start.CableForce.Max.ToString("0.###", CultureInfo.CurrentCulture);
            CableNote.Text = current.ElevatedCable
                ? "The tie is fitted to this force in the Pretension case of each case (as the VCF cables); SLS and ULS continue from it. Min = Max keeps it fixed."
                : "The open system has no elevated cable — tick With elevated cable on Edit → Tied Arch System to tie the arch.";
        }
        finally { loading = false; }
        Refresh();
    }

    private System.Windows.Media.Brush Muted => (System.Windows.Media.Brush)FindResource("Muted");

    private void AddHeader(Grid grid, string first)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(grid, new TextBlock { Text = first, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 0, 0, 2) }, 0, 0);
        foreach (var (text, column) in new[] { ("Min", 1), ("Max", 2), ("Step", 3), ("n", 5) })
            Place(grid, new TextBlock { Text = text, Style = (Style)FindResource("ColumnHeader") }, 0, column);
    }

    private RangeRow AddRange(Grid grid, VcfOptimization.Parameter parameter, VcfRange range)
    {
        TextBox Box(double value, string part)
        {
            var box = new TextBox { Text = value.ToString("0.###", CultureInfo.CurrentCulture), TextAlignment = TextAlignment.Right,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), Margin = new Thickness(0, 2, 4, 2) };
            System.Windows.Automation.AutomationProperties.SetName(box, $"{parameter.Label} {part}");
            box.TextChanged += (_, _) => Refresh();
            return box;
        }
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var (min, max, step) = (Box(range.Min, "min"), Box(range.Max, "max"), Box(range.Step, "step"));
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), FontSize = 11, Foreground = Muted };
        Place(grid, new TextBlock { Text = parameter.Label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }, row, 0);
        Place(grid, min, row, 1);
        Place(grid, max, row, 2);
        Place(grid, step, row, 3);
        Place(grid, new TextBlock { Text = parameter.Unit, VerticalAlignment = VerticalAlignment.Center, Foreground = Muted }, row, 4);
        Place(grid, count, row, 5);
        return new RangeRow(min, max, step, count, parameter.Whole);
    }

    private static void Place(Grid grid, UIElement element, int row, int column)
    {
        Grid.SetRow(element, row); Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static double Number(TextBox box) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ? value : double.NaN;

    private static VcfRange Range(RangeRow row) => new(Number(row.Min), Number(row.Max), Number(row.Step));

    internal TasOptimizationSettings FormSettings() => new(
        geometry.ToDictionary(pair => pair.Key, pair => Range(pair.Value), StringComparer.Ordinal),
        loads.ToDictionary(pair => pair.Key, pair => Range(pair.Value), StringComparer.Ordinal),
        roleSections.Where(pair => pair.Value.Selected.Count > 0).ToDictionary(pair => pair.Key, pair => pair.Value.Selected))
    {
        DeflectionResult = DeflectionResultCombo.SelectedItem as string,
        SpanRatio = Number(SpanRatioBox),
        UpliftLimitMm = Number(UpliftLimitBox),
        ElevatedCable = current.ElevatedCable,
        CableForce = new VcfForceSpan(Number(CableMinBox), Number(CableMaxBox)),
        CableSteps = int.TryParse(CableStepsBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var steps) ? steps : 0,
    };

    private void Input_Changed(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (loading || !IsInitialized || SpanRatioBox is null) return;
        var settings = FormSettings();
        foreach (var row in geometry.Values.Concat(loads.Values))
        {
            var range = Range(row);
            row.Count.Text = range.Problem(row.Whole) is null ? range.Count.ToString(CultureInfo.CurrentCulture) : "—";
        }
        var problems = TasOptimization.Validate(settings);
        var valid = problems.Count == 0;
        SpaceSummary.Text = valid
            ? $"{settings.GeometryCount:N0} geometry × {settings.ForceCount:N0} load × {settings.SectionCount:N0} section variants = {settings.CombinationCount:N0} combinations"
            : "";
        ProblemText.Text = string.Join("\n", problems);
        OkButton.IsEnabled = valid;
        RunButton.IsEnabled = valid && settings.CombinationCount <= ParametricOptimization.MaxCases && !viewModel.IsBusy && viewModel.TiedArchParameters is not null;
        RunButton.ToolTip = viewModel.TiedArchParameters is null ? "Open the tied arch system first: File → Load TAS… or Edit → Tied Arch System…"
            : settings.CombinationCount > ParametricOptimization.MaxCases ? $"{settings.CombinationCount:N0} combinations — one run takes at most {ParametricOptimization.MaxCases:N0}"
            : "Analyse every combination with the internal FEM and list the max deflection of each case (the open model is not changed)";
        ShowLoads(settings, valid);
        ShowLimit(settings, valid);
        ShowPreview(settings, valid);
    }

    /// <summary>The point load on an inner top chord joint at the lowest and highest corner (SLS and ULS).</summary>
    private void ShowLoads(TasOptimizationSettings settings, bool valid)
    {
        if (!valid) { LoadSummary.Text = ""; return; }
        var (low, high) = (TasOptimization.ParametersAt(settings, range => range.Min), TasOptimization.ParametersAt(settings, range => range.Max));
        static string Kn(double value) => value.ToString("N0", CultureInfo.CurrentCulture);
        string Span(double a, double b) => Math.Abs(a - b) < 0.5 ? Kn(a) : $"{Kn(a)} … {Kn(b)}";
        LoadSummary.Text = $"Point load per inner top chord joint (min … max corner): SLS {Span(low.SlsPointLoad, high.SlsPointLoad)} kN · " +
                           $"ULS {Span(low.UlsPointLoad, high.UlsPointLoad)} kN";
    }

    private void ShowLimit(TasOptimizationSettings settings, bool valid)
    {
        if (!valid) { LimitText.Text = ""; return; }
        var (low, high) = (TasOptimization.ParametersAt(settings, range => range.Min).Span * 1000 / settings.SpanRatio,
            TasOptimization.ParametersAt(settings, range => range.Max).Span * 1000 / settings.SpanRatio);
        LimitText.Text = $"limit L/{settings.SpanRatio:0.###} = " + (Math.Abs(high - low) < 0.05 ? $"{low:0.0} mm" : $"{low:0.0} … {high:0.0} mm") +
            (settings.DeflectionResult is null ? " · no result chosen: largest over every solved result" : "");
    }

    /// <summary>The system at the chosen corner, with its SLS point loads; the last good preview stays while a range is being typed.</summary>
    private void ShowPreview(TasOptimizationSettings settings, bool valid)
    {
        Func<VcfRange, double>? pick = PreviewCorner.SelectedIndex switch { 0 => range => range.Min, 2 => range => range.Max, _ => null };
        if (pick is not null && !valid) return;
        var p = pick is null ? current : TasOptimization.ParametersAt(settings, pick);
        try
        {
            var (model, supports, sections) = viewModel.PreviewTiedArch(p);
            var deck = model.Points.Values.Where(point => Math.Abs(point.Position.Z - p.DeckElevation) < 1e-6).OrderBy(point => point.Position.X).ToArray();
            Preview.Model = model;
            Preview.PinSupportPoints = supports;
            Preview.MemberSections = sections;
            Preview.AppliedPoints = deck.Select(point => new AppliedPoint { PointName = point.Name, X = point.Position.X, Y = point.Position.Y, Z = point.Position.Z }).ToArray();
            Preview.LoadValues = deck.ToDictionary(point => point.Name, point => point == deck[0] || point == deck[^1] ? -p.SlsPointLoad / 2 : -p.SlsPointLoad, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.InvalidDataException)
        {
            ProblemText.Text = (ProblemText.Text.Length > 0 ? ProblemText.Text + "\n" : "") + "Preview: " + ex.Message;
        }
    }

    /// <summary>Types into a range input (checks): sheet "geometry" or "load"; part "min", "max" or "step".</summary>
    internal void SetRange(string key, string part, string text, string sheet = "geometry")
    {
        var row = (sheet == "load" ? loads : geometry)[key];
        (part switch { "min" => row.Min, "max" => row.Max, _ => row.Step }).Text = text;
    }

    internal string SpaceText => SpaceSummary.Text;
    internal string LoadText => LoadSummary.Text;
    internal string LimitShown => LimitText.Text;

    private void Reset_Click(object sender, RoutedEventArgs e) => Load(null);
    private void Ok_Click(object sender, RoutedEventArgs e) => Confirm(run: false);
    private void Run_Click(object sender, RoutedEventArgs e) => Confirm(run: true);

    private void Confirm(bool run)
    {
        var settings = FormSettings();
        if (TasOptimization.Validate(settings).Count > 0) return;
        Result = settings;
        RunRequested = run;
        DialogResult = true;
    }
}
