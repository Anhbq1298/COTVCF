using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Mcotis.Desktop.Application.Design;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Presentation;

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
        private string text = text;
        public string Text { get => text; set => Set(ref text, value); }
        public bool Suggested { get; } = suggested;
        public bool IsChecked { get => isChecked; set => Set(ref isChecked, value); }
    }

    /// <summary>One numeric section input: label, unit, the designs it applies to and how it reads / writes the input.</summary>
    private sealed record SectionField(string Group, string Label, string Unit, ColumnDesignType[] Types,
        Func<ColumnSectionInput, double> Get, Func<ColumnSectionInput, double, ColumnSectionInput> Set, bool Integer = false, bool Positive = true);

    private const ColumnDesignType Rc = ColumnDesignType.ReinforcedConcrete, Cft = ColumnDesignType.ConcreteFilledTube, Src = ColumnDesignType.SteelEncased, Mega = ColumnDesignType.MegaComposite, St = ColumnDesignType.Steel;
    private const string SteelGroup = "Steel section", HollowGroup = "Hollow section (RHS / CHS)";
    private static readonly SectionField[] Fields =
    [
        new(SteelGroup, "h  I section depth", "mm", [St], s => s.SteelDepth, (s, v) => s with { SteelDepth = v }),
        new(SteelGroup, "b  I section flange width", "mm", [St], s => s.SteelWidth, (s, v) => s with { SteelWidth = v }),
        new(SteelGroup, "tf  flange", "mm", [St], s => s.FlangeThickness, (s, v) => s with { FlangeThickness = v }),
        new(SteelGroup, "tw  web", "mm", [St], s => s.WebThickness, (s, v) => s with { WebThickness = v }),
        new(SteelGroup, "r  root radius (0 = not counted)", "mm", [St], s => s.RootRadius, (s, v) => s with { RootRadius = v }, Positive: false),
        new(HollowGroup, "H  depth / D  diameter", "mm", [St], s => s.TubeDepth, (s, v) => s with { TubeDepth = v }),
        new(HollowGroup, "B  width (RHS)", "mm", [St], s => s.TubeWidth, (s, v) => s with { TubeWidth = v }),
        new(HollowGroup, "t  wall thickness", "mm", [St], s => s.TubeThickness, (s, v) => s with { TubeThickness = v }),
        new("Lateral-torsional buckling", "LLT  between lateral restraints (0 = member length)", "mm", [St], s => s.LateralLength, (s, v) => s with { LateralLength = v }, Positive: false),
        new("Concrete outline", "b  width (minor)", "mm", [Rc, Src, Mega], s => s.Width, (s, v) => s with { Width = v }),
        new("Concrete outline", "h  depth (in-plane)", "mm", [Rc, Src, Mega], s => s.Depth, (s, v) => s with { Depth = v }),
        new("Concrete outline", "Cover to links", "mm", [Rc, Src, Mega], s => s.Cover, (s, v) => s with { Cover = v }),
        new("Steel tube", "H  depth / D diameter", "mm", [Cft], s => s.TubeDepth, (s, v) => s with { TubeDepth = v }),
        new("Steel tube", "B  width (rectangular)", "mm", [Cft], s => s.TubeWidth, (s, v) => s with { TubeWidth = v }),
        new("Steel tube", "t  wall thickness", "mm", [Cft], s => s.TubeThickness, (s, v) => s with { TubeThickness = v }),
        new("Bars inside the tube", "Bars per B face / on the circle (0 = none)", "no.", [Cft], s => s.TubeReinforced ? s.BarsAlongWidth : 0,
            (s, v) => s with { TubeReinforced = v >= 2, BarsAlongWidth = v >= 2 ? (int)v : s.BarsAlongWidth }, Integer: true, Positive: false),
        new("Bars inside the tube", "Bars per H face (excl. corners)", "no.", [Cft], s => s.BarsAlongDepth, (s, v) => s with { BarsAlongDepth = (int)v }, Integer: true, Positive: false),
        new("Bars inside the tube", "Bar Ø", "mm", [Cft], s => s.BarDiameter, (s, v) => s with { BarDiameter = v }),
        new("Bars inside the tube", "Cover to the tube's inside face", "mm", [Cft], s => s.Cover, (s, v) => s with { Cover = v }),
        new("Encased steel I section", "h  steel depth", "mm", [Src, Mega], s => s.SteelDepth, (s, v) => s with { SteelDepth = v }),
        new("Encased steel I section", "b  flange width", "mm", [Src, Mega], s => s.SteelWidth, (s, v) => s with { SteelWidth = v }),
        new("Encased steel I section", "tf  flange", "mm", [Src, Mega], s => s.FlangeThickness, (s, v) => s with { FlangeThickness = v }),
        new("Encased steel I section", "tw  web", "mm", [Src, Mega], s => s.WebThickness, (s, v) => s with { WebThickness = v }),
        new("Encased steel I section", "Number of profiles (4 or 6)", "no.", [Mega], s => s.ProfileCount, (s, v) => s with { ProfileCount = (int)v }, Integer: true),
        new("Encased steel I section", "dsy  offset in-plane", "mm", [Mega], s => s.ProfileOffsetY, (s, v) => s with { ProfileOffsetY = v }),
        new("Encased steel I section", "dsx  offset across", "mm", [Mega], s => s.ProfileOffsetZ, (s, v) => s with { ProfileOffsetZ = v }),
        new("Reinforcement", "Bar Ø", "mm", [Rc, Src, Mega], s => s.BarDiameter, (s, v) => s with { BarDiameter = v }),
        new("Reinforcement", "Bars per b face (incl. corners)", "no.", [Rc, Src, Mega], s => s.BarsAlongWidth, (s, v) => s with { BarsAlongWidth = (int)v }, Integer: true),
        new("Reinforcement", "Bar layers per b face", "no.", [Mega], s => s.BarLayers, (s, v) => s with { BarLayers = (int)v }, Integer: true),
        new("Reinforcement", "Bars per h face (excl. corners)", "no.", [Rc, Src, Mega], s => s.BarsAlongDepth, (s, v) => s with { BarsAlongDepth = (int)v }, Integer: true, Positive: false),
        new("Reinforcement", "Link Ø", "mm", [Rc, Src, Mega], s => s.LinkDiameter, (s, v) => s with { LinkDiameter = v }),
        new("Reinforcement", "Link spacing", "mm", [Rc, Src, Mega], s => s.LinkSpacing, (s, v) => s with { LinkSpacing = v }),
        new("Materials", "fck  concrete (cylinder)", "MPa", [Rc, Cft, Src, Mega], s => s.Fck, (s, v) => s with { Fck = v }),
        new("Materials", "fy  structural steel (nominal)", "MPa", [Cft, Src, Mega, St], s => s.Fy, (s, v) => s with { Fy = v }),
        new("Materials", "fyk  reinforcement", "MPa", [Rc, Src, Mega], s => s.Fyk, (s, v) => s with { Fyk = v }),
        new("Materials", "Ea  structural steel", "MPa", [Cft, Src, Mega, St], s => s.Ea, (s, v) => s with { Ea = v }),
        new("Materials", "Es  reinforcement", "MPa", [Rc, Src, Mega], s => s.Es, (s, v) => s with { Es = v }),
        new("Materials", "αcc  (EC2 3.1.6, NA)", "", [Rc], s => s.AlphaCc, (s, v) => s with { AlphaCc = v }),
        new("Long term", "φef  effective creep (5.8.4)", "", [Rc], s => s.CreepRatio, (s, v) => s with { CreepRatio = v }, Positive: false),
        new("Long term", "φt  creep coefficient (6.7.3.3)", "", [Cft, Src, Mega], s => s.CreepRatio, (s, v) => s with { CreepRatio = v }, Positive: false),
        new("Long term", "NG,Ed / NEd  permanent share", "", [Cft, Src, Mega], s => s.PermanentLoadRatio, (s, v) => s with { PermanentLoadRatio = v }, Positive: false),
    ];

    private readonly ColumnDesignType type;
    private readonly Func<string, ColumnSectionInput> sectionFromMember;
    private readonly Func<ColumnDesignBasis, IReadOnlyList<ColumnMemberDesign>>? runDesign;
    private readonly string? runId;
    private readonly IReadOnlyList<DesignMemberOption> memberOptions;
    private readonly Dictionary<SectionField, TextBox> fieldBoxes = [];
    private readonly ComboBox tubeBox = new() { ItemsSource = Enum.GetValues<TubeShape>(), Width = 140, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
    private readonly ComboBox steelKindBox = new() { ItemsSource = Enum.GetValues<SteelShape>(), Width = 140, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6),
        ToolTip = "I: h, b, tf, tw, r · RHS: H, B, t · CHS: D, t (the hollow section's own group)" };
    private readonly ComboBox fabricationBox = new() { ItemsSource = Enum.GetValues<SteelFabrication>(), Width = 140, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6),
        ToolTip = "Picks the buckling curves (Tables 6.2, 6.4, 6.5): Rolled / Welded for an I section, HotFinished / ColdFormed for a hollow one" };
    private readonly List<CheckItem> members;
    private readonly HashSet<string> selection;
    private readonly bool viewerSelectionActive;
    private readonly List<CheckItem> cases;
    private readonly IReadOnlyList<string> defaultCases;
    /// <summary>The section last shown: what the fields do not edit (e.g. the profile name) is kept from it.</summary>
    private ColumnSectionInput shownSection = new();
    /// <summary>The member whose section the Section tab shows: the first ticked one.</summary>
    private string? sectionMember;

    public ColumnDesignBasis? Result { get; private set; }

    public DesignReferenceWindow(ColumnDesignBasis basis, IReadOnlyList<DesignMemberOption> memberOptions, IReadOnlyList<string> caseNames,
        IReadOnlyList<string> defaultCases, string? runId, Func<string, ColumnSectionInput> sectionFromMember,
        IReadOnlyList<string>? viewerSelection = null,
        Func<IReadOnlyList<string>, IReadOnlyList<string>, IReadOnlyList<DesignMemberInput>>? memberForces = null,
        IReadOnlyList<MaterialProperty>? materials = null, string? resultState = null, bool resultsStale = false,
        Func<ColumnDesignBasis, IReadOnlyList<ColumnMemberDesign>>? runDesign = null)
    {
        InitializeComponent();
        type = basis.Type;
        this.sectionFromMember = sectionFromMember;
        this.runDesign = runDesign;
        this.runId = runId;
        this.memberOptions = memberOptions;
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
        if (type == St)
        {
            // γC and γS carry γM1 and γM2 of EN 1993-1-1; there is no concrete
            GammaCLabel.Text = "γM1  stability";
            GammaSLabel.Text = "γM2  net section";
            GammaALabel.Text = "γM0  cross-section";
            ConcreteGradeLabel.Visibility = ConcreteGradeBox.Visibility = Visibility.Collapsed;
            SteelGradeLabel.Text = "Max. steel grade";
            Tabs.Items.Remove(OptimizationTab);
        }
        BuildSectionFields();
        // Only viewer-selected frames whose assigned section suits this module are checked. A mixed selection must
        // not make a CFT run include a Mega column (or the other way around).
        var selectedNames = (viewerSelection ?? []).ToHashSet(StringComparer.Ordinal);
        viewerSelectionActive = selectedNames.Count > 0;
        selection = memberOptions.Where(option => selectedNames.Contains(option.Member) && option.Suggested)
            .Select(option => option.Member).ToHashSet(StringComparer.Ordinal);
        var chosen = viewerSelectionActive ? selection : basis.Members.ToHashSet(StringComparer.Ordinal);
        members = memberOptions.OrderBy(option => selection.Contains(option.Member) ? 0 : 1).Select(option => new CheckItem(option.Member,
            MemberText(option),
            chosen.Contains(option.Member), option.Suggested)).ToList();
        MemberList.ItemsSource = members;
        SelectionButton.Visibility = viewerSelectionActive ? Visibility.Visible : Visibility.Collapsed;
        SelectionText.Text = viewerSelectionActive
            ? $"{selection.Count} selected member(s) match this design; other selected members are skipped."
            : "";
        SelectionText.Visibility = viewerSelectionActive ? Visibility.Visible : Visibility.Collapsed;
        var ticked = (basis.LoadCases.Count > 0 ? basis.LoadCases : defaultCases).ToHashSet(StringComparer.Ordinal);
        cases = caseNames.Select(name => new CheckItem(name, "", ticked.Contains(name))).ToList();
        CaseList.ItemsSource = cases;
        // the forces are those of the run on hand; results of a model that has changed since are said to be stale, never used silently
        RunText.Text = runId is null ? "No FEM results — run the FEM analysis first."
            : "Forces from the FEM analysis of the model (Run → Run FEM Analysis)" + (resultState is null ? "." : $" · {resultState}.") +
              (resultsStale ? " Run the analysis again before relying on this design." : "");
        if (resultsStale) RunText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Warning");
        // the Section tab always shows the first ticked member's section, never a section kept from an earlier design
        sectionMember = members.FirstOrDefault(member => member.IsChecked)?.Name;
        Show(sectionMember is null ? basis : basis with { Section = sectionFromMember(sectionMember) });
        foreach (var member in members) member.PropertyChanged += (_, _) => FollowMemberSection();
        // the length shown for a member is the effective length of the Design Basis, so it follows what is keyed in there
        LengthYBox.TextChanged += (_, _) => RefreshMemberTexts();
        LengthZBox.TextChanged += (_, _) => RefreshMemberTexts();
        RefreshMemberTexts();
        if (type != St) InitOptimization(basis, memberForces, materials);
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
            if (group.Key == SteelGroup) { AddRow(grid, "Section shape", steelKindBox, ""); AddRow(grid, "Manufacture", fabricationBox, ""); }
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
        shownSection = section;
        foreach (var (field, box) in fieldBoxes) box.Text = Text(field.Get(section));
        tubeBox.SelectedItem = section.Tube;
        steelKindBox.SelectedItem = section.SteelKind;
        fabricationBox.SelectedItem = section.Fabrication;
    }

    private static string Text(double value) => value.ToString(CultureInfo.InvariantCulture);

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        foreach (var member in members) member.IsChecked = selection.Count > 0 ? selection.Contains(member.Name) : member.Suggested;
        foreach (var item in cases) item.IsChecked = defaultCases.Contains(item.Name);
        sectionMember = members.FirstOrDefault(member => member.IsChecked)?.Name;
        var basis = ColumnDesignCodes.Default(type);
        Show(sectionMember is null ? basis : basis with { Section = sectionFromMember(sectionMember) });
    }

    /// <summary>A member's line in the list: its section, material and the effective length(s) l0 of the Design Basis.</summary>
    private string MemberText(DesignMemberOption option)
    {
        var y = LengthYBox.Text.Trim();
        var z = LengthZBox.Text.Trim();
        var length = y.Length == 0 && z.Length == 0 ? "" : y == z || z.Length == 0 ? $" · l0 = {y} m" : y.Length == 0 ? $" · l0,z = {z} m" : $" · l0,y = {y} m, l0,z = {z} m";
        return $"   {option.Section}{(option.Material.Length > 0 ? " · " + option.Material : "")}{length}";
    }

    private void RefreshMemberTexts()
    {
        foreach (var member in members)
            if (memberOptions.FirstOrDefault(option => option.Member == member.Name) is { } option) member.Text = MemberText(option);
    }

    /// <summary>Shows the section of the first ticked member when that member changes.</summary>
    private void FollowMemberSection()
    {
        var first = members.FirstOrDefault(member => member.IsChecked)?.Name;
        if (first is null || first == sectionMember) return;
        sectionMember = first;
        ShowSection(sectionFromMember(first));
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
        foreach (var member in members)
            member.IsChecked = mode == "all" || mode == "suggested" && member.Suggested || mode == "selection" && viewerSelectionActive && selection.Contains(member.Name);
    }

    private void CasePick_Click(object sender, RoutedEventArgs e)
    {
        var all = (string)((FrameworkElement)sender).Tag == "all";
        foreach (var item in cases) item.IsChecked = all;
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if (ReadBasis() is not { } basis) return;
        Result = basis;
        if (runDesign is null) { DialogResult = true; return; }
        // the design runs here and its results go in the Design tab; the dialog stays open
        try { ResultsPanel.Show(basis, runDesign(basis), runId); }
        catch (Exception ex) { StatusText.Text = ex is InvalidOperationException ? ex.Message : "The design could not be completed: " + ex.Message; return; }
        StatusText.Text = "";
        CancelButton.Content = "Close";
        Tabs.SelectedItem = DesignTab;
    }

    /// <summary>The design basis as the form holds it, checked; null (with the problem shown and its tab selected) when
    /// something is missing or invalid.</summary>
    private ColumnDesignBasis? ReadBasis(bool requireMembers = true)
    {
        ColumnDesignBasis? Fail(string message, int tab) { StatusText.Text = message; Tabs.SelectedIndex = tab; return null; }
        if (!TryNumber(GammaCBox.Text, 1.0, out var gammaC) || !TryNumber(GammaSBox.Text, 1.0, out var gammaS) || !TryNumber(GammaABox.Text, 1.0, out var gammaA))
        { return Fail("Partial factors must be numbers of 1.0 or more.", 0);  }
        if (!TryLength(LengthYBox.Text, out var lengthY) || !TryLength(LengthZBox.Text, out var lengthZ))
        { return Fail("Key in both effective lengths in m (greater than 0).", 0);  }
        var section = shownSection with
        {
            Tube = tubeBox.SelectedItem is TubeShape tube ? tube : TubeShape.Rectangular,
            SteelKind = steelKindBox.SelectedItem is SteelShape kind ? kind : SteelShape.I,
            Fabrication = fabricationBox.SelectedItem is SteelFabrication made ? made : SteelFabrication.Rolled,
        };
        foreach (var (field, box) in fieldBoxes)
        {
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0 || field.Positive && value <= 0 ||
                field.Integer && value != Math.Floor(value))
            { return Fail($"{field.Label}: enter a {(field.Integer ? "whole " : "")}number{(field.Positive ? " greater than 0" : "")}.", 1);  }
            section = field.Set(section, value);
        }
        if (SectionProblem(section) is { } problem) { return Fail(problem, 1);  }
        var chosenMembers = members.Where(member => member.IsChecked).Select(member => member.Name).ToArray();
        var chosenCases = cases.Where(item => item.IsChecked).Select(item => item.Name).ToArray();
        if (requireMembers && chosenMembers.Length == 0) return Fail("Tick at least one member to design.", 2);
        if (cases.Count > 0 && chosenCases.Length == 0) return Fail("Tick at least one load case or combination.", 2);
        StatusText.Text = "";
        return new ColumnDesignBasis
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
    }

    /// <summary>Geometry that cannot be built: walls thicker than half the tube, steel larger than the concrete, bars that do not fit.</summary>
    private string? SectionProblem(ColumnSectionInput s)
    {
        if (type == St)
        {
            var hollowFabrication = s.Fabrication is SteelFabrication.HotFinished or SteelFabrication.ColdFormed;
            return s.SteelKind switch
            {
                SteelShape.I when hollowFabrication => "An I section is Rolled or Welded; HotFinished and ColdFormed are for hollow sections.",
                SteelShape.I when 2 * s.FlangeThickness >= s.SteelDepth || s.WebThickness >= s.SteelWidth => "The I section needs a web between its flanges and flanges wider than the web.",
                SteelShape.I when s.Fabrication == SteelFabrication.Rolled && s.RootRadius > 0 && (2 * s.RootRadius + s.WebThickness >= s.SteelWidth || 2 * (s.FlangeThickness + s.RootRadius) >= s.SteelDepth) =>
                    "The root radius does not fit between the web and the flange tips.",
                SteelShape.I => null,
                _ when !hollowFabrication => "A hollow section is HotFinished or ColdFormed.",
                SteelShape.Rhs when 2 * s.TubeThickness >= Math.Min(s.TubeDepth, s.TubeWidth) => "The tube wall is too thick for its size.",
                SteelShape.Chs when 2 * s.TubeThickness >= s.TubeDepth => "The tube wall is too thick for its diameter.",
                _ => null,
            };
        }
        if (type == Cft)
            return 2 * s.TubeThickness >= Math.Min(s.TubeDepth, s.Tube == TubeShape.Circular ? s.TubeDepth : s.TubeWidth) ? "The tube wall is too thick for its size." : null;
        var inset = s.Cover + s.LinkDiameter + s.BarDiameter / 2;
        if (2 * inset >= Math.Min(s.Width, s.Depth)) return "Cover, link and bar do not fit in the concrete outline.";
        if (s.BarsAlongWidth < 2) return "Use at least 2 bars per b face (the corner bars).";
        if (type == Mega)
        {
            if (s.ProfileCount is not (4 or 6)) return "A mega composite column has 4 or 6 profiles.";
            if (2 * s.FlangeThickness >= s.SteelDepth || s.WebThickness >= s.SteelWidth) return "The profile needs a web between its flanges.";
            if (s.ProfileOffsetY + s.SteelDepth / 2 >= s.Depth / 2 || s.ProfileOffsetZ + s.SteelWidth / 2 >= s.Width / 2) return "The profiles must lie inside the concrete outline (dsy + h/2 < h/2 of the column, likewise across).";
            if (s.BarsAlongDepth < 2) return "Use at least 2 bars per h face for the CTBUH web plates.";
            if (s.BarLayers < 1) return "Use at least 1 bar layer per b face.";
        }
        if (type == Src && (s.SteelDepth >= s.Depth || s.SteelWidth >= s.Width || 2 * s.FlangeThickness >= s.SteelDepth || s.WebThickness >= s.SteelWidth))
            return "The encased I section must fit inside the concrete and have a web between its flanges.";
        return null;
    }

    private static bool TryNumber(string text, double minimum, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= minimum;

    private static bool TryLength(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0;
}
