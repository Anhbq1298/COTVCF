using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Presentation.Controls;
using static Mcotis.Desktop.Presentation.PropertyForm;

namespace Mcotis.Desktop.Presentation;

/// <summary>
/// A frame section form, laid out like SAP2000's (e.g. "Box/Tube Section"): Section Name, Section Notes and Display Color;
/// Dimensions for the shape; the Section drawing (redrawn as you type); Material (with + to open Define Materials);
/// Property Modifiers → Set Modifiers…; Properties → Section Properties…. A General / Section Designer section shows its
/// properties instead of dimensions. OK returns the edited section in <see cref="Result"/>.
/// </summary>
public sealed class FrameSectionWindow : Window
{
    private static readonly string[] ModifierLabels = ["Cross-section (axial) Area", "Shear Area in 2 direction", "Shear Area in 3 direction",
        "Torsional Constant", "Moment of Inertia about 2 axis", "Moment of Inertia about 3 axis", "Mass", "Weight"];

    private readonly MainViewModel viewModel;
    private readonly IReadOnlySet<string> taken;
    private FrameSectionProperty section;
    private readonly Dictionary<string, TextBox> fields = [];
    private readonly TextBox nameBox;
    private readonly ComboBox materialBox;
    private readonly SectionSketch sketch = new() { Width = 200, Height = 170 };
    private readonly TextBlock problem = new() { Foreground = Brushes.Firebrick, FontSize = 11, TextWrapping = TextWrapping.Wrap };

    public FrameSectionProperty? Result { get; private set; }

    public FrameSectionWindow(MainViewModel viewModel, FrameSectionProperty section, IReadOnlySet<string> taken)
    {
        this.viewModel = viewModel;
        this.section = section;
        this.taken = taken;
        Title = $"{FrameSectionProperty.Title(section.Shape)} Section";
        Icon = AppIcon.Window;
        Width = 780; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = (Brush)FindResource("Surface");

        var root = new StackPanel { Margin = new Thickness(10) };
        // name, notes, colour
        var head = new StackPanel();
        var header = Group(head, "");
        nameBox = new TextBox { Text = section.Name, Width = 260 };
        Row(header, "Section Name", new StackPanel { Orientation = Orientation.Horizontal, Children = { nameBox, new TextBlock { Text = "Display Color", Margin = new Thickness(24, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center },
            Swatch(section.Color, color => this.section = this.section with { Color = color }) } });
        var notes = new Button { Content = "Modify/Show Notes...", Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        notes.Click += (_, _) => { if (EditNotes(this, "Section Notes", this.section.Notes) is { } text) this.section = this.section with { Notes = text }; };
        Row(header, "Section Notes", new StackPanel { Orientation = Orientation.Horizontal, Children = { notes } });
        root.Children.Add(head);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        var left = new StackPanel();
        var right = new StackPanel();
        Grid.SetColumn(right, 2);
        columns.Children.Add(left); columns.Children.Add(right);
        root.Children.Add(columns);

        // dimensions (or properties for a general section)
        var dimensions = Group(left, "Dimensions");
        if (section.Shape == FrameShape.General)
        {
            var v = section.Values();
            Display(dimensions, "Cross-section (axial) area", Number(v.Area), "m²");
            Display(dimensions, "Moment of Inertia about 3 axis", Number(v.I33), "m⁴");
            Display(dimensions, "Moment of Inertia about 2 axis", Number(v.I22), "m⁴");
            Display(dimensions, "Torsional constant", Number(v.Torsion), "m⁴");
            ((StackPanel)dimensions.Content).Children.Add(new TextBlock { Text = $"{section.SapType} section — its shape is defined in SAP2000; the properties are as SAP2000 reports them.",
                Foreground = Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        }
        else
            foreach (var (key, label, value) in DimensionFields(section))
                Field(dimensions, fields, key, label, value, "m").TextChanged += (_, _) => Redraw();

        // material and modifiers
        var lower = new Grid();
        lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var materialHost = new StackPanel();
        var modifierHost = new StackPanel();
        Grid.SetColumn(modifierHost, 2);
        lower.Children.Add(materialHost); lower.Children.Add(modifierHost);
        left.Children.Add(lower);
        var material = Group(materialHost, "Material");
        materialBox = new ComboBox { Width = 170, IsEditable = false };
        LoadMaterials(section.Material);
        var add = new Button { Content = new TextBlock { Text = "+", FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(0, -3, 0, 0) },
            Width = 26, Height = 26, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Define Materials (add or edit materials)" };
        add.Click += (_, _) =>
        {
            new DefineMaterialsWindow(viewModel) { Owner = this }.ShowDialog();
            LoadMaterials(materialBox.SelectedItem as string ?? this.section.Material);
        };
        ((StackPanel)material.Content).Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { add, materialBox } });
        var modifiers = Group(modifierHost, "Property Modifiers");
        var setModifiers = new Button { Content = "Set Modifiers...", Width = 150 };
        setModifiers.Click += (_, _) =>
        {
            if (EditModifiers(this, "Frame Property/Stiffness Modification Factors", ModifierLabels, this.section.Modifiers) is { } values)
                this.section = this.section with { Modifiers = values };
        };
        ((StackPanel)modifiers.Content).Children.Add(setModifiers);

        // drawing and properties
        var drawing = Group(right, "Section");
        ((StackPanel)drawing.Content).Children.Add(sketch);
        var properties = Group(right, "Properties");
        var showProperties = new Button { Content = "Section Properties...", Width = 210, Margin = new Thickness(0, 0, 0, 6) };
        showProperties.Click += (_, _) =>
        {
            if (Read(out _) is not { } current) return;
            var v = current.Values();
            ShowTable(this, "Property Data", [("Cross-section (axial) area", Number(v.Area), "m²"), ("Moment of Inertia about 3 axis", Number(v.I33), "m⁴"),
                ("Moment of Inertia about 2 axis", Number(v.I22), "m⁴"), ("Torsional constant", Number(v.Torsion), "m⁴"),
                ("Shear area in 2 direction", Number(v.ShearArea2), "m²"), ("Shear area in 3 direction", Number(v.ShearArea3), "m²")]);
        };
        var timeDependent = new Button { Content = "Time Dependent Properties...", Width = 210, IsEnabled = false, ToolTip = "Not used by the internal FEM" };
        ToolTipService.SetShowOnDisabled(timeDependent, true);
        ((StackPanel)properties.Content).Children.Add(showProperties);
        ((StackPanel)properties.Content).Children.Add(timeDependent);

        root.Children.Add(problem);
        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true, Margin = new Thickness(0, 6, 30, 0) };
        ok.Click += (_, _) => { if (Read(out var message) is { } result) { Result = result; DialogResult = true; } else problem.Text = message; };
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
            Children = { ok, new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, Margin = new Thickness(0, 6, 0, 0) } } });
        Content = root;
        Redraw();
    }

    private void LoadMaterials(string selected)
    {
        viewModel.EnsureMaterials();
        var names = viewModel.Materials.Where(item => !item.IsUniaxial).Select(item => item.Name)
            .Append(selected).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        materialBox.ItemsSource = names;
        materialBox.SelectedItem = names.Contains(selected) ? selected : names.FirstOrDefault();
    }

    private static IEnumerable<(string Key, string Label, double Value)> DimensionFields(FrameSectionProperty s) => s.Shape switch
    {
        FrameShape.Rectangle => [("Depth", "Depth  ( t3 )", s.Depth), ("Width", "Width  ( t2 )", s.Width)],
        FrameShape.Box => [("Depth", "Outside depth  ( t3 )", s.Depth), ("Width", "Outside width  ( t2 )", s.Width), ("Tf", "Flange thickness  ( tf )", s.FlangeThickness),
            ("Tw", "Web thickness  ( tw )", s.WebThickness), ("Radius", "Corner Radius", s.CornerRadius)],
        FrameShape.I => [("Depth", "Outside height  ( t3 )", s.Depth), ("Width", "Top flange width  ( t2 )", s.Width), ("Tf", "Top flange thickness  ( tf )", s.FlangeThickness),
            ("Tw", "Web thickness  ( tw )", s.WebThickness), ("T2b", "Bottom flange width  ( t2b )", s.BottomWidth), ("Tfb", "Bottom flange thickness  ( tfb )", s.BottomFlangeThickness)],
        FrameShape.Pipe => [("Depth", "Outside diameter  ( t3 )", s.Depth), ("Tw", "Wall thickness  ( tw )", s.WebThickness)],
        FrameShape.Circle => [("Depth", "Diameter  ( t3 )", s.Depth)],
        _ => [("Depth", "Outside depth  ( t3 )", s.Depth), ("Width", "Outside width  ( t2 )", s.Width), ("Tf", "Flange thickness  ( tf )", s.FlangeThickness),
            ("Tw", "Web thickness  ( tw )", s.WebThickness)],
    };

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
        var s = section with
        {
            Name = name, Material = materialBox.SelectedItem as string ?? section.Material,
            Depth = Value("Depth") ?? section.Depth, Width = Value("Width") ?? section.Width, FlangeThickness = Value("Tf") ?? section.FlangeThickness,
            WebThickness = Value("Tw") ?? section.WebThickness, BottomWidth = Value("T2b") ?? section.BottomWidth,
            BottomFlangeThickness = Value("Tfb") ?? section.BottomFlangeThickness, CornerRadius = Value("Radius") ?? section.CornerRadius,
            Source = section.Source is "App" or "" ? "App" : section.Source.EndsWith("(edited)", StringComparison.Ordinal) ? section.Source : section.Source + " (edited)",
        };
        if (s.Shape != FrameShape.General)
        {
            var thin = s.Shape switch
            {
                FrameShape.Box => 2 * s.FlangeThickness >= s.Depth || 2 * s.WebThickness >= s.Width,
                FrameShape.Pipe => 2 * s.WebThickness > s.Depth,
                FrameShape.I => s.FlangeThickness + s.BottomFlangeThickness >= s.Depth || s.WebThickness > Math.Min(s.Width, s.BottomWidth),
                FrameShape.Channel or FrameShape.Tee or FrameShape.Angle => s.FlangeThickness >= s.Depth || s.WebThickness >= s.Width,
                _ => false,
            };
            if (!(s.Depth > 0) || s.Shape is not (FrameShape.Pipe or FrameShape.Circle) && !(s.Width > 0)) { message = "The section needs a depth and a width greater than zero."; return null; }
            if (thin) { message = "The plate thicknesses do not fit inside the section."; return null; }
        }
        return s;
    }

    /// <summary>Sets a dimension as the user would (checks).</summary>
    internal void SetField(string key, string text) => fields[key].Text = text;

    private void Redraw()
    {
        if (Read(out var message) is { } current) { sketch.Section = current; problem.Text = ""; }
        else problem.Text = message;
    }
}
