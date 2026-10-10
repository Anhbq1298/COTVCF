using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using COT_VCF.Domain;

namespace COT_VCF.Presentation;

/// <summary>
/// Design → RC Column / CFT / Steel Encased Composite: the design code, method, effective lengths, partial factors and
/// references (Design Basis), the section and materials (Section), and the members and load cases to design (Members &amp;
/// Loads). Run Design returns the confirmed <see cref="Result"/>; Cancel leaves it null.
/// </summary>
public partial class DesignReferenceWindow : Window
{
    /// <summary>A ticked / unticked row of the member or load-case list.</summary>
    public sealed class CheckItem(string name, string text, bool isChecked, bool suggested = false) : Observable
    {
        private bool isChecked = isChecked;
        public string Name { get; } = name;
        public string Text { get; } = text;
        public bool Suggested { get; } = suggested;
        public bool IsChecked { get => isChecked; set => Set(ref isChecked, value); }
    }

    /// <summary>One numeric section input: label, unit, the designs it applies to and how it reads / writes the input.</summary>
    private sealed record SectionField(string Group, string Label, string Unit, ColumnDesignType[] Types,
        Func<ColumnSectionInput, double> Get, Func<ColumnSectionInput, double, ColumnSectionInput> Set, bool Integer = false, bool Positive = true);

    private const ColumnDesignType Rc = ColumnDesignType.ReinforcedConcrete, Cft = ColumnDesignType.ConcreteFilledTube, Src = ColumnDesignType.SteelEncased;
    private static readonly SectionField[] Fields =
    [
        new("Concrete outline", "b  width (minor)", "mm", [Rc, Src], s => s.Width, (s, v) => s with { Width = v }),
        new("Concrete outline", "h  depth (in-plane)", "mm", [Rc, Src], s => s.Depth, (s, v) => s with { Depth = v }),
        new("Concrete outline", "Cover to links", "mm", [Rc, Src], s => s.Cover, (s, v) => s with { Cover = v }),
        new("Steel tube", "H  depth / D diameter", "mm", [Cft], s => s.TubeDepth, (s, v) => s with { TubeDepth = v }),
        new("Steel tube", "B  width (rectangular)", "mm", [Cft], s => s.TubeWidth, (s, v) => s with { TubeWidth = v }),
        new("Steel tube", "t  wall thickness", "mm", [Cft], s => s.TubeThickness, (s, v) => s with { TubeThickness = v }),
        new("Encased steel I section", "h  steel depth", "mm", [Src], s => s.SteelDepth, (s, v) => s with { SteelDepth = v }),
        new("Encased steel I section", "b  flange width", "mm", [Src], s => s.SteelWidth, (s, v) => s with { SteelWidth = v }),
        new("Encased steel I section", "tf  flange", "mm", [Src], s => s.FlangeThickness, (s, v) => s with { FlangeThickness = v }),
        new("Encased steel I section", "tw  web", "mm", [Src], s => s.WebThickness, (s, v) => s with { WebThickness = v }),
        new("Reinforcement", "Bar Ø", "mm", [Rc, Src], s => s.BarDiameter, (s, v) => s with { BarDiameter = v }),
        new("Reinforcement", "Bars per b face (incl. corners)", "no.", [Rc, Src], s => s.BarsAlongWidth, (s, v) => s with { BarsAlongWidth = (int)v }, Integer: true),
        new("Reinforcement", "Bars per h face (excl. corners)", "no.", [Rc, Src], s => s.BarsAlongDepth, (s, v) => s with { BarsAlongDepth = (int)v }, Integer: true, Positive: false),
        new("Reinforcement", "Link Ø", "mm", [Rc, Src], s => s.LinkDiameter, (s, v) => s with { LinkDiameter = v }),
        new("Reinforcement", "Link spacing", "mm", [Rc, Src], s => s.LinkSpacing, (s, v) => s with { LinkSpacing = v }),
        new("Materials", "fck  concrete (cylinder)", "MPa", [Rc, Cft, Src], s => s.Fck, (s, v) => s with { Fck = v }),
        new("Materials", "fy  structural steel", "MPa", [Cft, Src], s => s.Fy, (s, v) => s with { Fy = v }),
        new("Materials", "fyk  reinforcement", "MPa", [Rc, Src], s => s.Fyk, (s, v) => s with { Fyk = v }),
        new("Materials", "Ea  structural steel", "MPa", [Cft, Src], s => s.Ea, (s, v) => s with { Ea = v }),
        new("Materials", "Es  reinforcement", "MPa", [Rc, Src], s => s.Es, (s, v) => s with { Es = v }),
        new("Materials", "αcc  (EC2 3.1.6, NA)", "", [Rc], s => s.AlphaCc, (s, v) => s with { AlphaCc = v }),
        new("Long term", "φef  effective creep (5.8.4)", "", [Rc], s => s.CreepRatio, (s, v) => s with { CreepRatio = v }, Positive: false),
        new("Long term", "φt  creep coefficient (6.7.3.3)", "", [Cft, Src], s => s.CreepRatio, (s, v) => s with { CreepRatio = v }, Positive: false),
        new("Long term", "NG,Ed / NEd  permanent share", "", [Cft, Src], s => s.PermanentLoadRatio, (s, v) => s with { PermanentLoadRatio = v }, Positive: false),
    ];

    private readonly ColumnDesignType type;
    private readonly Func<string, ColumnSectionInput> sectionFromMember;
    private readonly Dictionary<SectionField, TextBox> fieldBoxes = [];
    private readonly ComboBox tubeBox = new() { ItemsSource = Enum.GetValues<TubeShape>(), Width = 140, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
    private readonly List<CheckItem> members;
    private readonly List<CheckItem> cases;
    private readonly IReadOnlyList<string> defaultCases;

    public ColumnDesignBasis? Result { get; private set; }

    public DesignReferenceWindow(ColumnDesignBasis basis, IReadOnlyList<DesignMemberOption> memberOptions, IReadOnlyList<string> caseNames,
        IReadOnlyList<string> defaultCases, string? runId, Func<string, ColumnSectionInput> sectionFromMember)
    {
        InitializeComponent();
        type = basis.Type;
        this.sectionFromMember = sectionFromMember;
        this.defaultCases = defaultCases;
        Title = "Design Reference — " + ColumnDesignCodes.Title(type);
        DesignTitle.Text = ColumnDesignCodes.Title(type);
        CodeBox.ItemsSource = ColumnDesignCodes.DesignCodes(type);
        MethodBox.ItemsSource = ColumnDesignCodes.Methods(type);
        ReferenceList.ItemsSource = ColumnDesignCodes.References(type);
        ScopeText.Text = ColumnDesignCodes.MaterialScope(type);
        if (type == Rc)
        {
            GammaALabel.Visibility = GammaABox.Visibility = Visibility.Collapsed;
            SteelGradeLabel.Text = "Reinforcement";
        }
        BuildSectionFields();
        var chosen = basis.Members.ToHashSet(StringComparer.Ordinal);
        members = memberOptions.Select(option => new CheckItem(option.Member,
            $"   {option.Section}{(option.Material.Length > 0 ? " · " + option.Material : "")}{(option.LengthM > 0 ? $" · L = {option.LengthM:0.###} m" : "")}",
            chosen.Contains(option.Member), option.Suggested)).ToList();
        MemberList.ItemsSource = members;
        var ticked = (basis.LoadCases.Count > 0 ? basis.LoadCases : defaultCases).ToHashSet(StringComparer.Ordinal);
        cases = caseNames.Select(name => new CheckItem(name, "", ticked.Contains(name))).ToList();
        CaseList.ItemsSource = cases;
        RunText.Text = runId is null ? "No FEM results — run the FEM analysis first." : $"Forces from FEM candidate {runId} (Result → Result Candidate).";
        Show(basis);
    }

    private void BuildSectionFields()
    {
        foreach (var group in Fields.Where(field => field.Types.Contains(type)).GroupBy(field => field.Group))
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            if (group.Key == "Steel tube") AddRow(grid, "Shape", tubeBox, "");
            foreach (var field in group)
            {
                var box = new TextBox { Margin = new Thickness(0, 0, 0, 6) };
                AutomationProperties.SetName(box, field.Label);
                fieldBoxes[field] = box;
                AddRow(grid, field.Label, box, field.Unit);
            }
            SectionPanel.Children.Add(new GroupBox { Header = group.Key, Content = grid });
        }
    }

    private static void AddRow(Grid grid, string label, FrameworkElement input, string unit)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition());
        var caption = new TextBlock { Text = label, Style = (Style)System.Windows.Application.Current.FindResource("FieldLabel"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) };
        Grid.SetRow(caption, row);
        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        grid.Children.Add(caption);
        grid.Children.Add(input);
        if (unit.Length == 0) return;
        var suffix = new TextBlock { Text = unit, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 6) };
        Grid.SetRow(suffix, row);
        Grid.SetColumn(suffix, 2);
        grid.Children.Add(suffix);
    }

    private void Show(ColumnDesignBasis basis)
    {
        CodeBox.SelectedItem = basis.DesignCode;
        if (CodeBox.SelectedIndex < 0) CodeBox.SelectedIndex = 0;
        MethodBox.SelectedItem = basis.Method;
        if (MethodBox.SelectedIndex < 0) MethodBox.SelectedIndex = 0;
        GammaCBox.Text = Text(basis.GammaC);
        GammaSBox.Text = Text(basis.GammaS);
        GammaABox.Text = Text(basis.GammaA);
        ConcreteGradeBox.Text = basis.MaxConcreteGrade;
        SteelGradeBox.Text = basis.MaxSteelGrade;
        LengthYBox.Text = basis.EffectiveLengthY is { } y ? Text(y) : "";
        LengthZBox.Text = basis.EffectiveLengthZ is { } z ? Text(z) : "";
        ShowSection(basis.Section);
        StatusText.Text = "";
    }

    private void ShowSection(ColumnSectionInput section)
    {
        foreach (var (field, box) in fieldBoxes) box.Text = Text(field.Get(section));
        tubeBox.SelectedItem = section.Tube;
    }

    private static string Text(double value) => value.ToString(CultureInfo.InvariantCulture);

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var first = members.FirstOrDefault(member => member.Suggested) ?? members.FirstOrDefault();
        Show(ColumnDesignCodes.Default(type) with { Section = first is null ? new ColumnSectionInput() : sectionFromMember(first.Name) });
        foreach (var member in members) member.IsChecked = member.Suggested;
        foreach (var item in cases) item.IsChecked = defaultCases.Contains(item.Name);
    }

    private void FromMember_Click(object sender, RoutedEventArgs e)
    {
        var first = members.FirstOrDefault(member => member.IsChecked);
        if (first is null) { StatusText.Text = "Tick a member on Members & Loads first."; return; }
        ShowSection(sectionFromMember(first.Name));
        StatusText.Text = "";
    }

    private void MemberPick_Click(object sender, RoutedEventArgs e)
    {
        var mode = (string)((FrameworkElement)sender).Tag;
        foreach (var member in members) member.IsChecked = mode == "all" || mode == "suggested" && member.Suggested;
    }

    private void CasePick_Click(object sender, RoutedEventArgs e)
    {
        var all = (string)((FrameworkElement)sender).Tag == "all";
        foreach (var item in cases) item.IsChecked = all;
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        void Fail(string message, int tab) { StatusText.Text = message; Tabs.SelectedIndex = tab; }
        if (!TryNumber(GammaCBox.Text, 1.0, out var gammaC) || !TryNumber(GammaSBox.Text, 1.0, out var gammaS) || !TryNumber(GammaABox.Text, 1.0, out var gammaA))
        { Fail("Partial factors must be numbers of 1.0 or more.", 0); return; }
        if (!TryLength(LengthYBox.Text, out var lengthY) || !TryLength(LengthZBox.Text, out var lengthZ))
        { Fail("Key in both effective lengths in m (greater than 0).", 0); return; }
        var section = new ColumnSectionInput { Tube = tubeBox.SelectedItem is TubeShape tube ? tube : TubeShape.Rectangular };
        foreach (var (field, box) in fieldBoxes)
        {
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0 || field.Positive && value <= 0 ||
                field.Integer && value != Math.Floor(value))
            { Fail($"{field.Label}: enter a {(field.Integer ? "whole " : "")}number{(field.Positive ? " greater than 0" : "")}.", 1); return; }
            section = field.Set(section, value);
        }
        if (SectionProblem(section) is { } problem) { Fail(problem, 1); return; }
        var chosenMembers = members.Where(member => member.IsChecked).Select(member => member.Name).ToArray();
        var chosenCases = cases.Where(item => item.IsChecked).Select(item => item.Name).ToArray();
        if (chosenMembers.Length == 0) { Fail("Tick at least one member to design.", 2); return; }
        if (cases.Count > 0 && chosenCases.Length == 0) { Fail("Tick at least one load case or combination.", 2); return; }
        Result = new ColumnDesignBasis
        {
            Type = type,
            DesignCode = (string)CodeBox.SelectedItem,
            Method = (string)MethodBox.SelectedItem,
            GammaC = gammaC,
            GammaS = gammaS,
            GammaA = gammaA,
            MaxConcreteGrade = ConcreteGradeBox.Text.Trim(),
            MaxSteelGrade = SteelGradeBox.Text.Trim(),
            EffectiveLengthY = lengthY,
            EffectiveLengthZ = lengthZ,
            Section = section,
            Members = chosenMembers,
            LoadCases = chosenCases,
        };
        DialogResult = true;
    }

    /// <summary>Geometry that cannot be built: walls thicker than half the tube, steel larger than the concrete, bars that do not fit.</summary>
    private string? SectionProblem(ColumnSectionInput s)
    {
        if (type == Cft)
            return 2 * s.TubeThickness >= Math.Min(s.TubeDepth, s.Tube == TubeShape.Circular ? s.TubeDepth : s.TubeWidth) ? "The tube wall is too thick for its size." : null;
        var inset = s.Cover + s.LinkDiameter + s.BarDiameter / 2;
        if (2 * inset >= Math.Min(s.Width, s.Depth)) return "Cover, link and bar do not fit in the concrete outline.";
        if (s.BarsAlongWidth < 2) return "Use at least 2 bars per b face (the corner bars).";
        if (type == Src && (s.SteelDepth >= s.Depth || s.SteelWidth >= s.Width || 2 * s.FlangeThickness >= s.SteelDepth || s.WebThickness >= s.SteelWidth))
            return "The encased I section must fit inside the concrete and have a web between its flanges.";
        return null;
    }

    private static bool TryNumber(string text, double minimum, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= minimum;

    private static bool TryLength(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0;
}
