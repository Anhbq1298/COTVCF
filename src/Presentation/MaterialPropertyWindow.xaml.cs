using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using COT_VCF.Domain;

namespace COT_VCF.Presentation;

/// <summary>
/// Material Property Data (as SAP2000): General Data (name and colour, type, grade, notes), Weight and Mass, Isotropic or
/// Uniaxial Property Data, Other Properties for the type, and an advanced display. The type can be chosen for a new
/// material only. OK returns the edited material in <see cref="Result"/>.
/// </summary>
public partial class MaterialPropertyWindow : Window
{
    private MaterialProperty material;
    private readonly bool isNew;
    private readonly IReadOnlySet<string> taken;
    private readonly Dictionary<string, TextBox> fields = [];
    private TextBox nameBox = null!, gradeBox = null!;
    private TextBlock massText = null!, shearText = null!;
    private CheckBox? lightweight;
    private Border swatch = null!;
    private string notes;
    private int color;
    private string costNote;
    private bool settingCost;

    public MaterialProperty? Result { get; private set; }

    public MaterialPropertyWindow(MaterialProperty material, bool isNew, bool advanced, IReadOnlySet<string> taken)
    {
        InitializeComponent();
        this.material = material;
        this.isNew = isNew;
        this.taken = taken;
        notes = material.Notes;
        color = material.Color;
        costNote = material.CostNote;
        AdvancedCheck.IsChecked = advanced;
        Build();
    }

    private static string Number(double? value) => value is { } v ? v.ToString("0.##########", CultureInfo.CurrentCulture) : "";

    // ---- layout ----
    private void Build()
    {
        Groups.Children.Clear();
        fields.Clear();
        lightweight = null;

        // General Data
        var general = Group("General Data");
        nameBox = new TextBox { Text = material.Name, Width = 190 };
        swatch = new Border { Width = 22, Height = 22, Margin = new Thickness(4, 0, 0, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Rgb(color)), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Display colour — click to change" };
        swatch.MouseLeftButtonUp += (_, _) => PickColor();
        Row(general, "Material Name and Display Color", Horizontal(nameBox, swatch));
        var type = new ComboBox { Width = 216, IsEnabled = isNew, ItemsSource = Enum.GetValues<MaterialKind>().Where(kind => kind != MaterialKind.Other).ToArray(),
            SelectedItem = material.Kind == MaterialKind.Other ? MaterialKind.NoDesign : material.Kind };
        type.SelectionChanged += (_, _) =>
        {
            if (type.SelectedItem is not MaterialKind kind || kind == material.Kind) return;
            material = MaterialProperty.Default(kind, nameBox.Text);
            color = material.Color; notes = material.Notes;
            Dispatcher.BeginInvoke(Build);
        };
        Row(general, "Material Type", type);
        gradeBox = new TextBox { Text = material.Grade, Width = 216 };
        Row(general, "Material Grade", gradeBox);
        var notesButton = new Button { Content = "Modify/Show Notes...", Width = 216 };
        notesButton.Click += (_, _) => EditNotes();
        Row(general, "Material Notes", notesButton);

        // Weight and Mass
        var weight = Group("Weight and Mass");
        var weightBox = Field(weight, "Weight", "Weight per Unit Volume", material.WeightPerVolume, "kN/m³");
        massText = Display(weight, "Mass per Unit Volume", Number(material.MassPerVolume), "kN-s²/m⁴");
        weightBox.TextChanged += (_, _) => massText.Text = Parse(weightBox) is { } w ? Number(w / MaterialProperty.Gravity) : "";

        // Isotropic / Uniaxial Property Data
        var elastic = Group(material.IsUniaxial ? "Uniaxial Property Data" : "Isotropic Property Data");
        var eBox = Field(elastic, "E", "Modulus Of Elasticity,  E", material.ElasticModulus, "kN/m²");
        var uBox = Field(elastic, "U", "Poisson,  U", material.IsUniaxial ? 0 : material.PoissonRatio, "");
        uBox.IsEnabled = !material.IsUniaxial;
        Field(elastic, "A", "Coefficient Of Thermal Expansion,  A", material.ThermalCoefficient, "1/C");
        shearText = Display(elastic, "Shear Modulus,  G", material.ShearModulus is { } g ? Number(g) : "", "kN/m²");
        void Shear() => shearText.Text = !material.IsUniaxial && Parse(eBox) is { } e && Parse(uBox) is { } u ? Number(e / (2 * (1 + u))) : "";
        eBox.TextChanged += (_, _) => Shear();
        uBox.TextChanged += (_, _) => Shear();

        // Other properties for the type
        var other = Group($"Other Properties For {MaterialProperty.Title(material.Kind)} Materials");
        switch (material.Kind)
        {
            case MaterialKind.Concrete:
                Field(other, "Fc", "Specified Concrete Compressive Strength, f'c", material.Fc, "kN/m²");
                Field(other, "ExpectedFc", "Expected Concrete Compressive Strength", material.ExpectedFc, "kN/m²");
                lightweight = new CheckBox { Content = "Lightweight Concrete", IsChecked = material.IsLightweight, Margin = new Thickness(0, 4, 0, 4) };
                ((StackPanel)other.Content).Children.Add(lightweight);
                var factor = Field(other, "Fcs", "Shear Strength Reduction Factor", material.ShearStrengthReductionFactor, "");
                factor.IsEnabled = material.IsLightweight;
                lightweight.Click += (_, _) => factor.IsEnabled = lightweight.IsChecked == true;
                break;
            case MaterialKind.Steel:
            case MaterialKind.Rebar:
                Field(other, "Fy", "Minimum Yield Stress, Fy", material.Fy, "kN/m²");
                Field(other, "Fu", "Minimum Tensile Stress, Fu", material.Fu, "kN/m²");
                Field(other, "ExpectedFy", "Expected Yield Stress, Fye", material.ExpectedFy, "kN/m²");
                Field(other, "ExpectedFu", "Expected Tensile Stress, Fue", material.ExpectedFu, "kN/m²");
                break;
            case MaterialKind.Tendon:
            case MaterialKind.ColdFormed:
                Field(other, "Fy", "Minimum Yield Stress, Fy", material.Fy, "kN/m²");
                Field(other, "Fu", "Minimum Tensile Stress, Fu", material.Fu, "kN/m²");
                break;
            default:
                ((StackPanel)other.Content).Children.Add(new TextBlock { Text = "No design properties for this material type.", Foreground = Brushes.Gray });
                break;
        }

        // Cost
        var cost = Group("Cost");
        static string Money(double? value) => value is { } v ? v.ToString("0.##", CultureInfo.CurrentCulture) : "";
        var costBox = Field(cost, "Cost", "Unit Cost", material.UnitCost, "SGD/t");
        costBox.Text = Money(material.UnitCost);
        var perCubicMetre = material.Kind == MaterialKind.Concrete ? Display(cost, "Unit Cost per Volume", Money(material.UnitCostPerCubicMetre), "SGD/m³") : null;
        var costText = new TextBlock { Text = costNote, Foreground = Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
        ((StackPanel)cost.Content).Children.Add(costText);
        var suggest = new Button { Content = "Suggest Rate", Width = 120, HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "Fill in the default rate for this material type and grade (project rates, BCA averages, estimates)" };
        ((StackPanel)cost.Content).Children.Add(suggest);
        void PerVolume() { if (perCubicMetre is not null) perCubicMetre.Text = Parse(costBox) is { } c && Parse(fields["Weight"]) is { } w ? Money(c * w / MaterialProperty.Gravity) : ""; }
        costBox.TextChanged += (_, _) => { if (!settingCost) { costNote = costBox.Text.Trim().Length == 0 ? "" : "Entered rate"; costText.Text = costNote; } PerVolume(); };
        fields["Weight"].TextChanged += (_, _) => PerVolume();
        suggest.Click += (_, _) =>
        {
            var (rate, note) = MaterialCosts.Suggest(ReadForm() ?? material);
            settingCost = true;
            costBox.Text = Money(rate);
            settingCost = false;
            costNote = note;
            costText.Text = note;
            PerVolume();
        };

        // Advanced display
        if (AdvancedCheck.IsChecked == true)
        {
            var advanced = Group("Advanced");
            Display(advanced, "Data source", material.Source, "");
            Display(advanced, "Used by the FEM", "E, unit weight and unit mass of the members with this material", "");
        }
    }

    private GroupBox Group(string title)
    {
        var box = new GroupBox { Header = title, Content = new StackPanel() };
        Groups.Children.Add(box);
        return box;
    }

    private static StackPanel Horizontal(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    private static void Row(GroupBox group, string label, UIElement editor, string? unit = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        if (unit is not null)
        {
            var text = new TextBlock { Text = unit, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Black };
            Grid.SetColumn(text, 2);
            grid.Children.Add(text);
        }
        ((StackPanel)group.Content).Children.Add(grid);
    }

    private TextBox Field(GroupBox group, string key, string label, double? value, string unit)
    {
        var box = new TextBox { Text = Number(value), Width = 110, TextAlignment = TextAlignment.Right };
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        fields[key] = box;
        Row(group, label, box, unit);
        return box;
    }

    private static TextBlock Display(GroupBox group, string label, string value, string unit)
    {
        var text = new TextBlock { Text = value, Width = 110, TextAlignment = TextAlignment.Right, Padding = new Thickness(4, 3, 4, 3),
            Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xEF, 0xF1)), Foreground = Brushes.DimGray, TextTrimming = TextTrimming.CharacterEllipsis };
        Row(group, label, text, unit);
        return text;
    }

    private static Color Rgb(int value) => Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);

    private void PickColor()
    {
        var palette = new[] { 0xFF0000, 0xFFA500, 0xFFFF00, 0x00FF00, 0x008000, 0x00FFFF, 0x0000FF, 0x800080, 0xFF00FF, 0x808080, 0xC0C0C0, 0x000000 };
        var grid = new UniformGrid { Columns = 6, Margin = new Thickness(4) };
        var popup = new Popup { PlacementTarget = swatch, Placement = PlacementMode.Bottom, StaysOpen = false,
            Child = new Border { Background = Brushes.White, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = grid } };
        foreach (var value in palette)
        {
            var button = new Button { Width = 22, Height = 22, Margin = new Thickness(2), Background = new SolidColorBrush(Rgb(value)), MinHeight = 0 };
            button.Click += (_, _) => { color = value; swatch.Background = new SolidColorBrush(Rgb(value)); popup.IsOpen = false; };
            grid.Children.Add(button);
        }
        popup.IsOpen = true;
    }

    private void EditNotes()
    {
        var box = new TextBox { Text = notes, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, Margin = new Thickness(0, 8, 8, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true, Margin = new Thickness(0, 8, 0, 0) };
        var dialog = new Window { Title = "Material Notes", Owner = this, Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Icon = AppIcon.Window,
            Content = new StackPanel { Margin = new Thickness(10), Children = { box, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } } } } };
        ok.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() == true) notes = box.Text;
    }

    // ---- result ----
    private static double? Parse(TextBox box) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value) ? value : null;
    private double? Value(string key) => fields.TryGetValue(key, out var box) ? Parse(box) : null;

    /// <summary>The material in the form, or null with the problem shown.</summary>
    internal MaterialProperty? ReadForm()
    {
        var problems = new List<string>();
        var name = nameBox.Text.Trim();
        if (name.Length == 0) problems.Add("Enter a material name.");
        else if (taken.Contains(name)) problems.Add($"A material named '{name}' already exists.");
        foreach (var (key, box) in fields)
            if (box.IsEnabled && box.Text.Trim().Length > 0 && Parse(box) is null) problems.Add($"{System.Windows.Automation.AutomationProperties.GetName(box)}: enter a number.");
        if (Value("E") is not (> 0)) problems.Add("Modulus of elasticity must be greater than zero.");
        if (Value("Cost") is < 0) problems.Add("Unit cost must be zero or more.");
        if (Value("Weight") is not (>= 0)) problems.Add("Weight per unit volume must be zero or more.");
        if (!material.IsUniaxial && Value("U") is not (>= 0 and < 0.5)) problems.Add("Poisson's ratio must be from 0 to less than 0.5.");
        ProblemText.Text = string.Join("\n", problems);
        if (problems.Count > 0) return null;
        var light = lightweight?.IsChecked == true;
        return material with
        {
            Name = name, Grade = gradeBox.Text.Trim(), Notes = notes, Color = color,
            WeightPerVolume = Value("Weight")!.Value, ElasticModulus = Value("E")!.Value,
            PoissonRatio = material.IsUniaxial ? 0 : Value("U")!.Value, ThermalCoefficient = Value("A") ?? 0,
            Fc = Value("Fc"), ExpectedFc = Value("ExpectedFc"), IsLightweight = light, ShearStrengthReductionFactor = light ? Value("Fcs") : null,
            Fy = Value("Fy"), Fu = Value("Fu"), ExpectedFy = Value("ExpectedFy"), ExpectedFu = Value("ExpectedFu"),
            UnitCost = Value("Cost"), CostNote = costNote,
            Source = isNew || material.Source == "App" ? "App" : material.Source + " (edited)",
        };
    }

    /// <summary>Sets a field as the user would (checks).</summary>
    internal void SetField(string key, string text) => fields[key].Text = text;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ReadForm() is not { } result) return;
        Result = result;
        DialogResult = true;
    }

    private void Advanced_Click(object sender, RoutedEventArgs e)
    {
        if (ReadForm() is { } current) material = current with { Source = material.Source };
        Build();
    }
}
