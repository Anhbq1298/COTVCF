using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mcotis.Desktop.Application.Design;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Presentation.Controls;
using static Mcotis.Desktop.Presentation.PropertyForm;

namespace Mcotis.Desktop.Presentation;

/// <summary>
/// The section form of a concrete filled tube (CFT) or steel encased composite frame section, laid out like the other
/// SAP2000-style section forms. It holds what the Design menu's CFT / Steel Encased design needs — the tube or the concrete
/// outline, encased I section and reinforcement (mm), the concrete, steel and reinforcement materials (fck, fy, fyk, Ea, Es
/// from Define → Materials), the creep ratio and permanent load share — and shows, as you type, the section drawing, the
/// composite properties (areas, transformed A / I for the FEM, Npl,Rk, δ, EIeff, EN 1994-1-1 section limits) and the material
/// quantities and cost per metre from the materials' unit costs. OK returns the edited section in <see cref="Result"/>.
/// </summary>
public sealed class CompositeSectionWindow : Window
{
    private static readonly string[] ModifierLabels = ["Cross-section (axial) Area", "Shear Area in 2 direction", "Shear Area in 3 direction",
        "Torsional Constant", "Moment of Inertia about 2 axis", "Moment of Inertia about 3 axis", "Mass", "Weight"];

    private readonly MainViewModel viewModel;
    private readonly IReadOnlySet<string> taken;
    private FrameSectionProperty section;
    private readonly bool tube, mega;
    private readonly ComboBox profileCount = new() { Width = 100, ItemsSource = new[] { "4", "6" } };
    private readonly TextBox profileName = new() { Width = 100 };
    private readonly TextBlock profileData = new() { Foreground = Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Dictionary<string, TextBox> fields = [];
    private readonly TextBox nameBox;
    private readonly ComboBox tubeShape = new() { Width = 100, ItemsSource = new[] { "Rectangular", "Circular" } };
    private readonly ComboBox concreteBox = new() { Width = 150 }, steelBox = new() { Width = 150 }, rebarBox = new() { Width = 150 };
    private readonly SectionSketch sketch = new() { Width = 250, Height = 210 };
    private readonly StackPanel propertyRows = new(), costRows = new(), checkRows = new();
    private readonly List<TextBox> sectionPropertyValues = [];
    private readonly TextBlock materialData = new() { Foreground = Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock problem = new() { Foreground = Brushes.Firebrick, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly InteractionChart chart = new() { Width = 318, Height = 330 };
    private readonly StackPanel diagramRows = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock diagramBasis = new() { Foreground = Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly ColumnDesignType designType;
    private readonly ColumnDesignBasis designBasis;
    /// <summary>The N–M interaction of the section in the form (null while the input is not valid).</summary>
    internal InteractionDiagram? Interaction { get; private set; }

    public FrameSectionProperty? Result { get; private set; }
    /// <summary>The composite properties and cost of the section in the form (null while the input is not valid).</summary>
    internal CompositeAnalysis? Analysis { get; private set; }

    public CompositeSectionWindow(MainViewModel viewModel, FrameSectionProperty section, IReadOnlySet<string> taken)
    {
        this.viewModel = viewModel;
        this.section = section;
        this.taken = taken;
        tube = section.Shape == FrameShape.FilledTube;
        mega = section.Shape == FrameShape.MegaComposite;
        var c = section.Composite ?? new ColumnSectionInput();
        Title = $"{FrameSectionProperty.Title(section.Shape)} Section";
        Icon = AppIcon.Window;
        Width = Math.Min(1360, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(900, SystemParameters.WorkArea.Height - 40);
        MinWidth = 760; MinHeight = 480;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = (Brush)FindResource("Surface");

        var root = new Grid { Margin = new Thickness(10) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var head = new StackPanel();
        var header = Group(head, "");
        nameBox = new TextBox { Text = section.Name, Width = 260 };
        System.Windows.Automation.AutomationProperties.SetName(nameBox, "Section Name");
        Row(header, "Section Name", new StackPanel { Orientation = Orientation.Horizontal, Children = { nameBox, new TextBlock { Text = "Display Color", Margin = new Thickness(24, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center },
            Swatch(section.Color, color => this.section = this.section with { Color = color }) } });
        var notes = new Button { Content = "Modify/Show Notes...", Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        notes.Click += (_, _) => { if (EditNotes(this, "Section Notes", this.section.Notes) is { } text) this.section = this.section with { Notes = text }; };
        Row(header, "Section Notes", new StackPanel { Orientation = Orientation.Horizontal, Children = { notes } });
        root.Children.Add(head);

        var columns = new Grid();
        foreach (var width in new[] { new GridLength(1, GridUnitType.Star), new GridLength(10), new GridLength(1, GridUnitType.Star), new GridLength(10), new GridLength(290), new GridLength(10), new GridLength(340) })
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var left = new StackPanel(); var middle = new StackPanel(); var right = new StackPanel(); var interaction = new StackPanel();
        Grid.SetColumn(middle, 2); Grid.SetColumn(right, 4); Grid.SetColumn(interaction, 6);
        columns.Children.Add(left); columns.Children.Add(middle); columns.Children.Add(right); columns.Children.Add(interaction);
        var tabs = new TabControl { Margin = new Thickness(0, 6, 0, 0) };
        tabs.Items.Add(new TabItem { Header = "Section Data", Content = columns, Width = double.NaN, Height = 28 });
        var propertySheet = new StackPanel { Margin = new Thickness(10), Width = 470, HorizontalAlignment = HorizontalAlignment.Left };
        var propertyGroup = Group(propertySheet, "Properties");
        foreach (var (label, unit) in new[] {
            ("Cross-section (axial) area", "m²"), ("Moment of Inertia about 3 axis", "m⁴"),
            ("Moment of Inertia about 2 axis", "m⁴"), ("Torsional constant", "m⁴"),
            ("Shear area in 2 direction", "m²"), ("Shear area in 3 direction", "m²") })
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var value = new TextBox { IsReadOnly = true, TextAlignment = TextAlignment.Right, BorderThickness = new Thickness(0),
                Background = (Brush)FindResource("Background"), Foreground = (Brush)FindResource("Muted"),
                Padding = new Thickness(5, 2, 5, 2), MinHeight = 24 };
            System.Windows.Automation.AutomationProperties.SetName(value, label);
            Grid.SetColumn(value, 1); row.Children.Add(value); sectionPropertyValues.Add(value);
            var suffix = new TextBlock { Text = unit, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(suffix, 2); row.Children.Add(suffix);
            ((StackPanel)propertyGroup.Content).Children.Add(row);
        }
        propertySheet.Children.Add(Hint("Composite A and I are transformed to concrete Ecm; values are before modifiers."));
        tabs.Items.Add(new TabItem { Header = "Show Section Properties", Content = propertySheet, Width = double.NaN, Height = 28 });
        var form = new ScrollViewer
        {
            Content = tabs,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(form, 1);
        root.Children.Add(form);

        // ---- N–M interaction from the Design menu's algorithm, with its partial factors ----
        designType = tube ? ColumnDesignType.ConcreteFilledTube : mega ? ColumnDesignType.MegaComposite : ColumnDesignType.SteelEncased;
        designBasis = viewModel.DesignBasis(designType);
        var diagramGroup = Group(interaction, "N–M Interaction (Design algorithm)");
        ((StackPanel)diagramGroup.Content).Children.Add(chart);
        ((StackPanel)diagramGroup.Content).Children.Add(diagramRows);
        ((StackPanel)diagramGroup.Content).Children.Add(diagramBasis);

        // ---- dimensions (mm, as the Design menu takes them) ----
        TextBox Mm(GroupBox group, string key, string label, double value, string unit = "mm")
        {
            var box = Field(group, fields, key, label, value, unit);
            box.TextChanged += (_, _) => Recalculate();
            return box;
        }
        if (tube)
        {
            var steelTube = Group(left, "Steel Tube");
            tubeShape.SelectedIndex = c.Tube == TubeShape.Circular ? 1 : 0;
            System.Windows.Automation.AutomationProperties.SetName(tubeShape, "Tube shape");
            Row(steelTube, "Tube shape", tubeShape);
            Mm(steelTube, "TubeDepth", "Outside depth / diameter  ( H, D )", c.TubeDepth);
            var widthBox = Mm(steelTube, "TubeWidth", "Outside width  ( B )", c.TubeWidth);
            Mm(steelTube, "TubeThickness", "Wall thickness  ( t )", c.TubeThickness);
            void ShapeChanged() { widthBox.IsEnabled = tubeShape.SelectedIndex == 0; Recalculate(); }
            tubeShape.SelectionChanged += (_, _) => ShapeChanged();
            widthBox.IsEnabled = tubeShape.SelectedIndex == 0;
            ((StackPanel)steelTube.Content).Children.Add(Hint("H (D) lies in the plane of the 2D model: the in-plane moment bends the section about its major axis."));
        }
        else
        {
            var outline = Group(left, "Concrete Section");
            Mm(outline, "Depth", "Depth, in-plane  ( h )", c.Depth);
            Mm(outline, "Width", "Width  ( b )", c.Width);
            Mm(outline, "Cover", "Cover to the links  ( c )", c.Cover);
            var steelSection = Group(left, mega ? "Encased Steel Profiles (each; strong axis in-plane)" : "Encased Steel I Section (strong axis in-plane)");
            if (mega)
            {
                profileCount.SelectedIndex = c.ProfileCount == 6 ? 1 : 0;
                System.Windows.Automation.AutomationProperties.SetName(profileCount, "Number of profiles");
                profileCount.SelectionChanged += (_, _) => Recalculate();
                Row(steelSection, "Number of profiles", profileCount);
                profileName.Text = c.ProfileName;
                System.Windows.Automation.AutomationProperties.SetName(profileName, "Profile designation");
                profileName.TextChanged += (_, _) => Recalculate();
                Row(steelSection, "Designation (e.g. HD 400x551)", profileName);
            }
            Mm(steelSection, "SteelDepth", "Depth  ( ha )", c.SteelDepth);
            Mm(steelSection, "SteelWidth", "Flange width  ( ba )", c.SteelWidth);
            Mm(steelSection, "FlangeThickness", "Flange thickness  ( tf )", c.FlangeThickness);
            Mm(steelSection, "WebThickness", "Web thickness  ( tw )", c.WebThickness);
            if (mega)
            {
                Mm(steelSection, "ProfileOffsetY", "Offset in-plane  ( dsy )", c.ProfileOffsetY);
                Mm(steelSection, "ProfileOffsetZ", "Offset across  ( dsx )", c.ProfileOffsetZ);
                ((StackPanel)steelSection.Content).Children.Add(profileData);
                ((StackPanel)steelSection.Content).Children.Add(Hint("Profiles at (±dsx, ±dsy) from the centroid; 6 profiles add two at (±dsx, 0). Root fillets are not counted."));
            }
            var bars = Group(left, "Reinforcement");
            Mm(bars, "BarDiameter", "Bar diameter  ( Ø )", c.BarDiameter);
            Mm(bars, "BarsAlongWidth", "Bars on each b face (corners incl.)", c.BarsAlongWidth, "no.");
            Mm(bars, "BarsAlongDepth", "Bars on each h face (corners excl.)", c.BarsAlongDepth, "no.");
            if (mega) Mm(bars, "BarLayers", "Bar layers on each b face", c.BarLayers, "no.");
            Mm(bars, "LinkDiameter", "Link diameter", c.LinkDiameter);
            Mm(bars, "LinkSpacing", "Link spacing", c.LinkSpacing);
        }

        // ---- materials and design data ----
        var materials = Group(middle, "Materials");
        MaterialRow(materials, "Concrete", concreteBox);
        MaterialRow(materials, tube ? "Steel tube" : "Structural steel", steelBox);
        if (!tube) MaterialRow(materials, "Reinforcement", rebarBox);
        ((StackPanel)materials.Content).Children.Add(materialData);
        LoadMaterials(section.Material, section.SteelMaterial, section.RebarMaterial);
        foreach (var box in new[] { concreteBox, steelBox, rebarBox }) box.SelectionChanged += (_, _) => Recalculate();

        var design = Group(middle, "Design Data (EN 1994-1-1 §6.7.3.3)");
        Mm(design, "CreepRatio", "Creep coefficient  ( φt )", c.CreepRatio, "");
        Mm(design, "PermanentLoadRatio", "Permanent share  ( NG,Ed / NEd )", c.PermanentLoadRatio, "");
        ((StackPanel)design.Content).Children.Add(Hint("Partial factors, code and method are set in Design → " + (tube ? "CFT Design…" : mega ? "Mega Composite Column Design…" : "Steel Encased Design…")));
        var checks = Group(middle, "Section Limits");
        ((StackPanel)checks.Content).Children.Add(checkRows);

        var modifiers = Group(middle, "Property Modifiers");
        var setModifiers = new Button { Content = "Set Modifiers...", Width = 150, HorizontalAlignment = HorizontalAlignment.Left };
        setModifiers.Click += (_, _) =>
        {
            if (EditModifiers(this, "Frame Property/Stiffness Modification Factors", ModifierLabels, this.section.Modifiers) is { } values)
            {
                this.section = this.section with { Modifiers = values };
                Recalculate();
            }
        };
        ((StackPanel)modifiers.Content).Children.Add(setModifiers);

        // ---- drawing, properties and cost ----
        var drawing = Group(right, "Section");
        ((StackPanel)drawing.Content).Children.Add(sketch);
        var properties = Group(right, "Composite Properties");
        ((StackPanel)properties.Content).Children.Add(propertyRows);
        var cost = Group(right, "Material Quantity and Cost (per m)");
        ((StackPanel)cost.Content).Children.Add(costRows);

        var footer = new StackPanel();
        footer.Children.Add(problem);
        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true, Margin = new Thickness(0, 6, 30, 0) };
        ok.Click += (_, _) => { if (Read(out var message) is { } result) { Result = result; DialogResult = true; } else problem.Text = message; };
        footer.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
            Children = { ok, new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, Margin = new Thickness(0, 6, 0, 0) } } });
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
        Recalculate();
    }

    private static TextBlock Hint(string text) =>
        new() { Text = text, Foreground = Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    private void MaterialRow(GroupBox group, string label, ComboBox box)
    {
        System.Windows.Automation.AutomationProperties.SetName(box, label + " material");
        var add = new Button { Content = new TextBlock { Text = "+", FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(0, -3, 0, 0) },
            Width = 26, Height = 26, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Define Materials (add or edit materials)" };
        add.Click += (_, _) =>
        {
            new DefineMaterialsWindow(viewModel) { Owner = this }.ShowDialog();
            LoadMaterials(concreteBox.SelectedItem as string ?? "", steelBox.SelectedItem as string ?? "", rebarBox.SelectedItem as string ?? "");
            Recalculate();
        };
        Row(group, label, new StackPanel { Orientation = Orientation.Horizontal, Children = { add, box } });
    }

    private void LoadMaterials(string concrete, string steel, string rebar)
    {
        viewModel.EnsureMaterials();
        void Load(ComboBox box, string selected, Func<MaterialProperty, bool> suits)
        {
            var names = viewModel.Materials.Where(suits).Select(item => item.Name).ToList();
            if (box == rebarBox && names.Count == 0) names.Add(MainViewModel.DefaultRebar.Name);
            if (selected.Length > 0 && !names.Contains(selected) && viewModel.Materials.Any(item => item.Name == selected && suits(item))) names.Add(selected);
            box.ItemsSource = names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            box.SelectedItem = names.Contains(selected) ? selected : names.FirstOrDefault();
        }
        Load(concreteBox, concrete, item => item.Kind == MaterialKind.Concrete);
        Load(steelBox, steel, item => item.Kind is MaterialKind.Steel or MaterialKind.ColdFormed);
        Load(rebarBox, rebar, item => item.Kind == MaterialKind.Rebar);
    }

    private double? Value(string key) => fields.TryGetValue(key, out var box) ? Parse(box) : null;

    /// <summary>The section in the form, or null with <paramref name="message"/>.</summary>
    internal FrameSectionProperty? Read(out string message)
    {
        message = "";
        var name = nameBox.Text.Trim();
        if (name.Length == 0) { message = "Enter a section name."; return null; }
        if (taken.Contains(name)) { message = $"A section named '{name}' already exists."; return null; }
        if (fields.Values.FirstOrDefault(box => Parse(box) is not (>= 0)) is { } bad)
        { message = $"{System.Windows.Automation.AutomationProperties.GetName(bad)}: enter a number of zero or more."; return null; }
        if (concreteBox.SelectedItem is not string concrete || steelBox.SelectedItem is not string steel) { message = "Choose the concrete and the steel material."; return null; }
        var rebar = tube ? section.RebarMaterial : rebarBox.SelectedItem as string ?? "";
        if (!tube && rebar.Length == 0) { message = "Choose the reinforcement material."; return null; }

        var c = section.Composite ?? new ColumnSectionInput();
        double V(string key, double current) => Value(key) ?? current;
        int N(string key, int current) => Value(key) is { } v ? (int)Math.Round(v) : current;
        c = c with { CreepRatio = V("CreepRatio", c.CreepRatio), PermanentLoadRatio = V("PermanentLoadRatio", c.PermanentLoadRatio) };
        if (tube)
        {
            c = c with { Tube = tubeShape.SelectedIndex == 1 ? TubeShape.Circular : TubeShape.Rectangular, TubeDepth = V("TubeDepth", c.TubeDepth), TubeWidth = V("TubeWidth", c.TubeWidth), TubeThickness = V("TubeThickness", c.TubeThickness) };
            var outside = c.Tube == TubeShape.Circular ? c.TubeDepth : Math.Min(c.TubeDepth, c.TubeWidth);
            if (!(c.TubeDepth > 0) || c.Tube == TubeShape.Rectangular && !(c.TubeWidth > 0)) { message = "The tube needs an outside depth and width greater than zero."; return null; }
            if (!(c.TubeThickness > 0) || 2 * c.TubeThickness >= outside) { message = "The wall thickness must be greater than zero and fit inside the tube."; return null; }
        }
        else
        {
            c = c with
            {
                Depth = V("Depth", c.Depth), Width = V("Width", c.Width), Cover = V("Cover", c.Cover),
                SteelDepth = V("SteelDepth", c.SteelDepth), SteelWidth = V("SteelWidth", c.SteelWidth), FlangeThickness = V("FlangeThickness", c.FlangeThickness), WebThickness = V("WebThickness", c.WebThickness),
                BarDiameter = V("BarDiameter", c.BarDiameter), BarsAlongWidth = N("BarsAlongWidth", c.BarsAlongWidth), BarsAlongDepth = N("BarsAlongDepth", c.BarsAlongDepth),
                LinkDiameter = V("LinkDiameter", c.LinkDiameter), LinkSpacing = V("LinkSpacing", c.LinkSpacing),
            };
            if (!(c.Depth > 0) || !(c.Width > 0)) { message = "The concrete section needs a depth and a width greater than zero."; return null; }
            if (mega)
                c = c with
                {
                    ProfileCount = profileCount.SelectedIndex == 1 ? 6 : 4, ProfileName = profileName.Text.Trim(), BarLayers = N("BarLayers", c.BarLayers),
                    ProfileOffsetY = V("ProfileOffsetY", c.ProfileOffsetY), ProfileOffsetZ = V("ProfileOffsetZ", c.ProfileOffsetZ),
                };
            if (!(c.SteelDepth > 0) || !(c.SteelWidth > 0) || !(c.FlangeThickness > 0) || !(c.WebThickness > 0) || 2 * c.FlangeThickness >= c.SteelDepth || c.WebThickness >= c.SteelWidth)
            { message = "The encased I section needs positive dimensions with the plates fitting inside it."; return null; }
            var inside = 2 * (c.Cover + c.LinkDiameter + c.BarDiameter);
            if (mega)
            {
                if (c.ProfileOffsetY + c.SteelDepth / 2 > c.Depth / 2 - inside / 2 - (Math.Max(1, c.BarLayers) - 1) * 2 * c.BarDiameter || c.ProfileOffsetZ + c.SteelWidth / 2 > c.Width / 2 - inside / 2)
                { message = "The profiles must lie inside the reinforcement cage: dsy + ha/2 and dsx + ba/2 inside the bars."; return null; }
                if (2 * c.ProfileOffsetZ <= c.SteelWidth || (c.ProfileCount == 6 ? c.ProfileOffsetY : 2 * c.ProfileOffsetY) <= c.SteelDepth)
                { message = "The profiles overlap — increase dsx / dsy."; return null; }
                if (c.BarsAlongDepth < 2 || c.BarLayers < 1) { message = "Use at least 2 bars on each h face and 1 bar layer on each b face."; return null; }
            }
            else if (c.SteelDepth > c.Depth - inside || c.SteelWidth > c.Width - inside) { message = "The I section must fit inside the reinforcement cage (cover + link + bar on each side)."; return null; }
            if (c.BarsAlongWidth < 2 || !(c.BarDiameter > 0)) { message = "Use at least 2 bars on each b face, with a diameter greater than zero."; return null; }
        }
        // the design strengths and moduli follow the chosen materials (MPa)
        var materialsByName = viewModel.Materials.ToDictionary(item => item.Name, StringComparer.Ordinal);
        if (materialsByName.GetValueOrDefault(concrete)?.StrengthMpa is > 0 and var fck) c = c with { Fck = fck };
        if (materialsByName.GetValueOrDefault(steel) is { } steelMaterial)
            c = c with { Fy = steelMaterial.StrengthMpa is > 0 and var fy ? fy : c.Fy, Ea = steelMaterial.ElasticModulus > 0 ? steelMaterial.ElasticModulus / 1000 : c.Ea };
        if (!tube && (materialsByName.GetValueOrDefault(rebar) is { Kind: MaterialKind.Rebar } bars ? bars : MainViewModel.DefaultRebar) is var rebarMaterial)
            c = c with { Fyk = rebarMaterial.StrengthMpa is > 0 and var fyk ? fyk : c.Fyk, Es = rebarMaterial.ElasticModulus > 0 ? rebarMaterial.ElasticModulus / 1000 : c.Es };

        var s = (section with
        {
            Name = name, Material = concrete, SteelMaterial = steel, RebarMaterial = rebar, Composite = c,
            SapType = tube ? "FilledTube" : mega ? "MegaComposite" : "Encased",
            Source = section.Source is "App" or "" ? "App" : section.Source.EndsWith("(edited)", StringComparison.Ordinal) ? section.Source : section.Source + " (edited)",
        }).WithCompositeOutline();
        return s with { Given = viewModel.AnalyseComposite(s)?.Values };
    }

    /// <summary>Sets a field as the user would (checks).</summary>
    internal void SetField(string key, string text) => fields[key].Text = text;
    internal void SetTubeShape(TubeShape shape) => tubeShape.SelectedIndex = shape == TubeShape.Circular ? 1 : 0;

    private void Recalculate()
    {
        if (!IsInitializedForm) return;
        if (Read(out var message) is not { } current) { problem.Text = message; Analysis = null; foreach (var box in sectionPropertyValues) box.Text = ""; return; }
        problem.Text = "";
        sketch.Section = current;
        var a = viewModel.AnalyseComposite(current)!;
        Analysis = a;
        var c = current.Composite!;
        var p = a.Parts;
        var values = new[] { a.Values.Area, a.Values.I33, a.Values.I22, a.Values.Torsion, a.Values.ShearArea2, a.Values.ShearArea3 };
        for (var index = 0; index < values.Length; index++)
            sectionPropertyValues[index].Text = values[index].ToString("0.########", CultureInfo.CurrentCulture);

        static string F(double value, string format) => value.ToString(format, CultureInfo.CurrentCulture);
        // EN 1994-1-1 §6.7.3.2: Npl,Rk = Aa fy + μc η Ac fck + As fsk (μc = 1.0 for a filled section, 0.85 encased; η of EC2 Table 3.1 above C50/60, BC4)
        var concreteFactor = (tube ? 1.0 : 0.85) * Mcotis.Desktop.Application.Design.ColumnDesignEngine.BlockFactors(c.Fck).Eta;
        var npl = (p.SteelArea * c.Fy + concreteFactor * p.ConcreteArea * c.Fck + p.RebarArea * c.Fyk) * 1000;           // kN
        var nplRd = (p.SteelArea * c.Fy / 1.0 + concreteFactor * p.ConcreteArea * c.Fck / 1.5 + p.RebarArea * c.Fyk / 1.15) * 1000;
        var delta = nplRd > 0 ? p.SteelArea * c.Fy * 1000 / nplRd : 0;
        // §6.7.3.3: (EI)eff = Ea Ia + Es Is + Ke Ecm,eff Ic, Ke = 0.6, Ecm,eff = Ecm / (1 + (NG,Ed/NEd) φt)
        var ecmEff = a.Ecm / (1 + c.PermanentLoadRatio * c.CreepRatio);
        var eiEff = a.Ea * p.SteelI33 + a.Es * p.RebarI33 + 0.6 * ecmEff * p.ConcreteI33;                              // kNm²
        var ratio = p.ConcreteArea > 0 ? p.RebarArea / p.ConcreteArea : 0;

        propertyRows.Children.Clear();
        void Line(StackPanel host, string label, string value, bool bold = false) =>
            host.Children.Add(new DockPanel
            {
                Margin = new Thickness(0, 1, 0, 1),
                Children =
                {
                    Docked(new TextBlock { Text = value, Foreground = Brushes.Black, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal }, Dock.Right),
                    new TextBlock { Text = label, Foreground = Brushes.Black, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal },
                },
            });
        Line(propertyRows, "Concrete area  Ac", F(p.ConcreteArea, "0.0000") + " m²");
        Line(propertyRows, tube ? "Tube area  Aa" : mega ? $"{c.ProfileCount} profiles  Aa" : "Steel I area  Aa", F(p.SteelArea, "0.0000") + " m²");
        if (!tube) Line(propertyRows, $"Bars  As  ({p.Bars.Count} Ø{F(c.BarDiameter, "0")}, ρ = {F(100 * ratio, "0.00")}%)", F(p.RebarArea, "0.0000") + " m²");
        Line(propertyRows, "Transformed area  A (to Ecm)", F(a.Values.Area, "0.0000") + " m²");
        Line(propertyRows, "Transformed  I33 (in-plane)", F(a.Values.I33, "0.00000") + " m⁴");
        Line(propertyRows, "Transformed  I22", F(a.Values.I22, "0.00000") + " m⁴");
        Line(propertyRows, "Modulus  E = Ecm", F(a.Ecm / 1e6, "0.0") + " GPa");
        Line(propertyRows, "Self-weight", F(a.WeightPerMetre, "0.00") + " kN/m");
        Line(propertyRows, "Npl,Rk  /  Npl,Rd", $"{F(npl, "#,0")} / {F(nplRd, "#,0")} kN");
        Line(propertyRows, "Steel contribution  δ", F(delta, "0.000"));
        Line(propertyRows, "(EI)eff,33", F(eiEff, "#,0") + " kNm²");
        if (mega && MegaColumnEngine.Calculate(new MegaDesigner(new ColumnDesignBasis { Type = ColumnDesignType.MegaComposite, DesignCode = "", Method = "", Section = c })
                .EngineInput(1, 0, 1, c.Fy)) is { } method)
            Line(propertyRows, "Mpl,y,Rd / Mmax,Rd", $"{F(method.MplRd, "#,0")} / {F(method.MmaxRd, "#,0")} kNm");
        if (mega)
        {
            var one = CompositeSection.Profile(c);
            var nominal = System.Text.RegularExpressions.Regex.Match(c.ProfileName, @"x\s*(\d+(?:\.\d+)?)\s*$");
            profileData.Text = $"One profile: A = {F(one.Area, "#,0")} mm², {F(one.KgPerM, "0")} kg/m, Ix = {F(one.IStrong / 1e6, "#,0")}×10⁶ mm⁴" +
                (nominal.Success && double.TryParse(nominal.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var kg) && kg > 0
                    ? $" · designation {F(kg, "0")} kg/m ({F(100 * (one.KgPerM / kg - 1), "+0.0;-0.0")}%)" : "");
        }

        checkRows.Children.Clear();
        void Check(string label, bool pass) => checkRows.Children.Add(new TextBlock
        {
            Text = (pass ? "✓  " : "✗  ") + label, Foreground = pass ? Brushes.DarkGreen : Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1),
        });
        Check($"0.2 ≤ δ ≤ 0.9 (δ = {F(delta, "0.00")}) — §6.7.1(4)", delta is >= 0.2 and <= 0.9);
        var epsilon = Math.Sqrt(235 / Math.Max(1, c.Fy));
        if (tube && c.Tube == TubeShape.Circular)
            Check($"d/t = {F(c.TubeDepth / c.TubeThickness, "0.0")} ≤ 90ε² = {F(90 * epsilon * epsilon, "0.0")} — Table 6.3", c.TubeDepth / c.TubeThickness <= 90 * epsilon * epsilon);
        else if (tube)
            Check($"h/t = {F(Math.Max(c.TubeDepth, c.TubeWidth) / c.TubeThickness, "0.0")} ≤ 52ε = {F(52 * epsilon, "0.0")} — Table 6.3", Math.Max(c.TubeDepth, c.TubeWidth) / c.TubeThickness <= 52 * epsilon);
        else
        {
            var coverY = mega ? c.Depth / 2 - (c.ProfileOffsetY + c.SteelDepth / 2) : (c.Depth - c.SteelDepth) / 2;
            var coverZ = mega ? c.Width / 2 - (c.ProfileOffsetZ + c.SteelWidth / 2) : (c.Width - c.SteelWidth) / 2;
            Check($"Concrete cover to the {(mega ? "profiles" : "flanges")} ≥ max(40 mm, bf/6) — §6.7.5.1(2)", Math.Min(coverY, coverZ) >= Math.Max(40, c.SteelWidth / 6));
            Check($"0.3% ≤ ρs = {F(100 * ratio, "0.00")}% (≤ 6% counted) — §6.7.3.1(3)", ratio >= 0.003);
            Check($"0.2 ≤ h/b = {F(c.Depth / c.Width, "0.00")} ≤ 5.0 — §6.7.3.1(4)", c.Depth / c.Width is >= 0.2 and <= 5.0);
            if (mega)
                Check($"web bars n = (ny − 1)/2 = {F((c.BarsAlongDepth - 1) / 2.0, "0.0")} ≥ 5 — CTBUH §4.2.3", c.BarsAlongDepth >= 11);
        }
        var fckLimit = c.Fck <= 90;
        Check($"fck = {F(c.Fck, "0")} MPa ≤ C90/105 (above C50/60 per BC4) · fy = {F(c.Fy, "0")} MPa ≤ 550 (BC4)", fckLimit && c.Fy <= 550);

        materialData.Text = $"fck = {F(c.Fck, "0.#")} MPa · Ecm = {F(a.Ecm / 1e6, "0.0")} GPa\nfy = {F(c.Fy, "0.#")} MPa · Ea = {F(a.Ea / 1e6, "0")} GPa" +
                            (tube ? "" : $"\nfyk = {F(c.Fyk, "0.#")} MPa · Es = {F(a.Es / 1e6, "0")} GPa");

        costRows.Children.Clear();
        string Rate(double? rate, string unit) => rate is { } r ? $"{F(r, "#,0")} SGD/{unit}" : "no rate";
        var cost = a.Cost;
        Line(costRows, $"Concrete {F(cost.ConcreteM3PerM, "0.000")} m³ × {Rate(a.Concrete?.UnitCostPerCubicMetre, "m³")}",
            a.Concrete?.UnitCostPerCubicMetre is { } rc ? F(cost.ConcreteM3PerM * rc, "#,0") : "—");
        Line(costRows, $"Steel {F(cost.SteelTPerM, "0.000")} t × {Rate(a.Steel?.UnitCost, "t")}",
            a.Steel?.UnitCost is { } rs ? F(cost.SteelTPerM * rs, "#,0") : "—");
        if (!tube)
            Line(costRows, $"Rebar + links {F(cost.RebarTPerM, "0.000")} t × {Rate(a.Rebar?.UnitCost, "t")}",
                a.Rebar?.UnitCost is { } rr ? F(cost.RebarTPerM * rr, "#,0") : "—");
        Line(costRows, "Cost per metre", cost.CostPerM is { } total ? F(total, "#,0") + " SGD/m" : "—", bold: true);
        if (cost.Missing.Count > 0)
            costRows.Children.Add(Hint($"Set the unit cost of the {string.Join(" and ", cost.Missing)} in Define → Materials."));

        // N–M interaction: the Design menu's algorithm for this column type, with its partial factors and grade limits
        diagramRows.Children.Clear();
        try { Interaction = ColumnDesignEngine.Interaction(designBasis with { Section = c }); }
        catch (Exception ex) { Interaction = null; diagramBasis.Text = "The interaction could not be computed: " + ex.Message; }
        chart.Diagram = Interaction;
        if (Interaction is { } d)
        {
            Line(diagramRows, "Npl,Rd  (squash)", F(d.NplRdKN, "#,0") + " kN");
            Line(diagramRows, "Nt,Rd  (tension)", F(d.NtRdKN, "#,0") + " kN");
            Line(diagramRows, "Mpl,y,Rd  (in-plane)", F(d.MplYRdKNm, "#,0") + " kNm");
            Line(diagramRows, "Mpl,z,Rd", F(d.MplZRdKNm, "#,0") + " kNm");
            var maxY = d.Series[0].Points.MaxBy(point => point.MKNm);
            Line(diagramRows, "Mmax,y,Rd  at N", $"{F(maxY.MKNm, "#,0")} kNm at {F(maxY.NKN, "#,0")} kN");
            diagramBasis.Text = $"{d.Basis}; γC = {F(designBasis.GammaC, "0.0#")}, γa = {F(designBasis.GammaA, "0.0#")}, γS = {F(designBasis.GammaS, "0.0#")} " +
                                "(Design → " + ColumnDesignCodes.Title(designType) + "). Cross-section resistance: buckling, second order and αM apply in the member design.";
        }
    }

    private bool IsInitializedForm => Content is not null;

    private static UIElement Docked(UIElement element, Dock dock) { DockPanel.SetDock(element, dock); return element; }
}
