using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using COT_VCF.Application;
using COT_VCF.Domain;

namespace COT_VCF.Presentation;

/// <summary>
/// Run → Optimization VCF. The inputs of Edit → VCF Geometry as a search space: min / max / step for every geometry
/// parameter and the candidate sections per member role and per cable. OK keeps the settings on the view model
/// (<see cref="MainViewModel.VcfOptimizationSettings"/>); the working model is never changed here and the run loop is not
/// built yet.
/// </summary>
public partial class OptimizationVcfWindow : Window
{
    private readonly MainViewModel viewModel;
    private VcfGeometryAnalysis? analysis;
    private bool loading;

    private readonly Dictionary<string, (TextBox Min, TextBox Max, TextBox Step, TextBlock Count)> ranges = new(StringComparer.Ordinal);
    private readonly Dictionary<VcfMemberRole, CandidatePicker> roleSections = [];
    private readonly Dictionary<string, CandidatePicker> cableSections = new(StringComparer.Ordinal);

    public OptimizationVcfWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        Load(viewModel.VcfOptimizationSettings);
    }

    /// <summary>The settings confirmed with OK (null on Cancel or when the form has a problem).</summary>
    internal VcfOptimizationSettings? Result { get; private set; }

    // ---- read the model (and the kept settings) into the form ----
    private void Load(VcfOptimizationSettings? kept)
    {
        loading = true;
        try
        {
            GeometryGrid.Children.Clear(); GeometryGrid.RowDefinitions.Clear(); ranges.Clear();
            FrameGrid.Children.Clear(); FrameGrid.RowDefinitions.Clear(); roleSections.Clear();
            CableGrid.Children.Clear(); CableGrid.RowDefinitions.Clear(); cableSections.Clear();
            try { analysis = VcfGeometry.Analyze(viewModel.State.Geometry, viewModel.State.Truss); }
            catch (Exception ex)
            {
                analysis = null;
                ProblemText.Text = ex.Message;
                Preview.Model = viewModel.State.Geometry;
                OkButton.IsEnabled = false;
                return;
            }

            var current = VcfOptimization.Current(analysis);
            AddHeader();
            foreach (var (parameter, _) in VcfOptimization.Parameters(analysis))
            {
                var range = kept?.Geometry.TryGetValue(parameter.Key, out var saved) == true ? saved : current.Geometry[parameter.Key];
                AddRange(parameter, range);
            }

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
            if (frames.Count == 0 && cables.Count == 0)
                SectionHint.Text = "No sections yet — use File → Read FEM Properties from SAP or Define → Section Properties to add candidates.";
        }
        finally { loading = false; }
        Refresh();
    }

    private IReadOnlyList<string> Members(VcfMemberRole role) =>
        analysis!.Roles.Where(pair => pair.Value == role).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();

    private void AddHeader()
    {
        GeometryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(GeometryGrid, new TextBlock { Text = "Parameter", Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 0, 0, 2) }, 0, 0);
        foreach (var (text, column) in new[] { ("Min", 1), ("Max", 2), ("Step", 3), ("n", 5) })
            Place(GeometryGrid, new TextBlock { Text = text, Style = (Style)FindResource("ColumnHeader") }, 0, column);
    }

    private void AddRange(VcfOptimization.Parameter parameter, VcfRange range)
    {
        TextBox Box(double value, string part)
        {
            var box = new TextBox { Text = value.ToString("0.###", CultureInfo.CurrentCulture), TextAlignment = TextAlignment.Right,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), Margin = new Thickness(0, 2, 4, 2) };
            System.Windows.Automation.AutomationProperties.SetName(box, $"{parameter.Label} {part}");
            box.TextChanged += (_, _) => Refresh();
            return box;
        }
        var row = GeometryGrid.RowDefinitions.Count;
        GeometryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var (min, max, step) = (Box(range.Min, "min"), Box(range.Max, "max"), Box(range.Step, "step"));
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), FontSize = 11, Foreground = (System.Windows.Media.Brush)FindResource("Muted") };
        Place(GeometryGrid, new TextBlock { Text = parameter.Label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }, row, 0);
        Place(GeometryGrid, min, row, 1);
        Place(GeometryGrid, max, row, 2);
        Place(GeometryGrid, step, row, 3);
        Place(GeometryGrid, new TextBlock { Text = parameter.Unit, VerticalAlignment = VerticalAlignment.Center, Foreground = (System.Windows.Media.Brush)FindResource("Muted") }, row, 4);
        Place(GeometryGrid, count, row, 5);
        ranges[parameter.Key] = (min, max, step, count);
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

    /// <summary>The settings in the form (unchecked; see <see cref="VcfOptimization.Validate"/>).</summary>
    internal VcfOptimizationSettings FormSettings() => new(
        ranges.ToDictionary(pair => pair.Key, pair => new VcfRange(Number(pair.Value.Min), Number(pair.Value.Max), Number(pair.Value.Step)), StringComparer.Ordinal),
        roleSections.Where(pair => pair.Value.Selected.Count > 0).ToDictionary(pair => pair.Key, pair => pair.Value.Selected),
        cableSections.Where(pair => pair.Value.Selected.Count > 0).ToDictionary(pair => pair.Key, pair => pair.Value.Selected, StringComparer.Ordinal));

    private void Refresh()
    {
        if (loading || analysis is null) return;
        var settings = FormSettings();
        foreach (var (key, row) in ranges)
        {
            var range = settings.Geometry[key];
            row.Count.Text = range.Problem(key == VcfOptimization.Bays.Key) is null ? range.Count.ToString(CultureInfo.CurrentCulture) : "—";
        }
        var problems = VcfOptimization.Validate(analysis, settings);
        SpaceSummary.Text = problems.Count > 0 ? ""
            : $"{settings.GeometryCount:N0} geometry × {settings.SectionCount:N0} section variants = {settings.CombinationCount:N0} combinations";
        ProblemText.Text = string.Join("\n", problems);
        OkButton.IsEnabled = problems.Count == 0;
        ShowPreview(settings, problems.Count == 0);
    }

    private void ShowPreview(VcfOptimizationSettings settings, bool valid)
    {
        if (analysis is null) return;
        Func<VcfRange, double>? pick = PreviewCorner.SelectedIndex switch { 0 => range => range.Min, 2 => range => range.Max, _ => null };
        if (pick is not null && !valid) return;    // keep the last preview while the ranges are being typed
        try
        {
            var model = analysis;
            var form = analysis.Parameters;
            if (pick is not null)
            {
                var bays = (int)pick(settings.Geometry[VcfOptimization.Bays.Key]);
                model = bays == analysis.Bays ? analysis : VcfGeometry.Analyze(VcfGeometry.ChangeBays(analysis, bays));
                form = VcfOptimization.ParametersAt(analysis, settings, pick);
            }
            var geometry = VcfGeometry.Build(model, form);
            Preview.SpanPoints = model.TopPoints.Concat(model.MiddlePoints).Concat(model.BottomPoints).ToArray();
            Preview.Model = geometry;
            try { Preview.PinSupportPoints = FemStudyService.FindPinSupportPoints(geometry); } catch { Preview.PinSupportPoints = null; }
        }
        catch (InvalidOperationException ex) { ProblemText.Text = ex.Message; }
    }

    /// <summary>Types into a range input (checks): part is "min", "max" or "step".</summary>
    internal void SetRange(string key, string part, string text)
    {
        var row = ranges[key];
        (part switch { "min" => row.Min, "max" => row.Max, _ => row.Step }).Text = text;
    }

    // ---- actions ----
    private void PreviewCorner_Changed(object sender, SelectionChangedEventArgs e) => Refresh();
    private void Reset_Click(object sender, RoutedEventArgs e) => Load(null);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (analysis is null) return;
        var settings = FormSettings();
        if (VcfOptimization.Validate(analysis, settings).Count > 0) return;
        Result = settings;
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
