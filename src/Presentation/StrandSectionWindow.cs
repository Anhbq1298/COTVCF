using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using COT_VCF.Domain;
using static COT_VCF.Presentation.PropertyForm;

namespace COT_VCF.Presentation;

/// <summary>A cable or tendon section form (SAP2000's Cable / Tendon Section Data): name, colour, notes; material; diameter
/// and area (linked); for a cable the area / mass / weight modifiers. OK returns the edited section in <see cref="Result"/>.</summary>
public sealed class StrandSectionWindow : Window
{
    private StrandSectionProperty section;
    private readonly IReadOnlySet<string> taken;
    private readonly TextBox nameBox, diameterBox, areaBox;
    private readonly ComboBox materialBox;
    private readonly TextBlock problem = new() { Foreground = Brushes.Firebrick, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private bool linking;

    public StrandSectionProperty? Result { get; private set; }

    public StrandSectionWindow(MainViewModel viewModel, StrandSectionProperty section, IReadOnlySet<string> taken)
    {
        this.section = section;
        this.taken = taken;
        Title = section.IsTendon ? "Tendon Section Data" : "Cable Section Data";
        Icon = AppIcon.Window;
        Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = (Brush)FindResource("Surface");

        var root = new StackPanel { Margin = new Thickness(10) };
        var general = Group(root, "General Data");
        nameBox = new TextBox { Text = section.Name, Width = 190 };
        Row(general, "Section Name and Display Color", new StackPanel { Orientation = Orientation.Horizontal,
            Children = { nameBox, new Border { Width = 6 }, Swatch(section.Color, color => this.section = this.section with { Color = color }) } });
        var notes = new Button { Content = "Modify/Show Notes...", Width = 218 };
        notes.Click += (_, _) => { if (EditNotes(this, "Section Notes", this.section.Notes) is { } text) this.section = this.section with { Notes = text }; };
        Row(general, "Section Notes", notes);
        viewModel.EnsureMaterials();
        var materials = viewModel.Materials.Where(item => item.Kind is MaterialKind.Tendon or MaterialKind.Rebar or MaterialKind.Steel).Select(item => item.Name)
            .Append(section.Material).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        materialBox = new ComboBox { Width = 218, ItemsSource = materials, SelectedItem = section.Material.Length > 0 ? section.Material : materials.FirstOrDefault() };
        Row(general, "Material", materialBox);

        var geometry = Group(root, "Section Properties");
        var fields = new Dictionary<string, TextBox>();
        diameterBox = Field(geometry, fields, "Diameter", "Diameter (solid round of the same area)", section.Diameter, "m");
        areaBox = Field(geometry, fields, "Area", "Area", section.Area, "m²");
        diameterBox.TextChanged += (_, _) => Link(fromDiameter: true);
        areaBox.TextChanged += (_, _) => Link(fromDiameter: false);

        if (!section.IsTendon)
        {
            var modifiers = Group(root, "Property Modifiers");
            var set = new Button { Content = "Set Modifiers...", Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
            set.Click += (_, _) =>
            {
                if (EditModifiers(this, "Cable Property Modification Factors", ["Cross-section (axial) Area", "Mass", "Weight"], this.section.Modifiers) is { } values)
                    this.section = this.section with { Modifiers = values };
            };
            ((StackPanel)modifiers.Content).Children.Add(set);
        }

        root.Children.Add(problem);
        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true, Margin = new Thickness(0, 6, 30, 0) };
        ok.Click += (_, _) => { if (Read(out var message) is { } result) { Result = result; DialogResult = true; } else problem.Text = message; };
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
            Children = { ok, new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, Margin = new Thickness(0, 6, 0, 0) } } });
        Content = root;
    }

    private void Link(bool fromDiameter)
    {
        if (linking) return;
        linking = true;
        if (fromDiameter && Parse(diameterBox) is { } d) areaBox.Text = Number(Math.PI * d * d / 4);
        else if (!fromDiameter && Parse(areaBox) is { } a && a >= 0) diameterBox.Text = Number(Math.Sqrt(4 * a / Math.PI));
        linking = false;
    }

    internal void SetArea(string text) => areaBox.Text = text;

    internal StrandSectionProperty? Read(out string message)
    {
        message = "";
        var name = nameBox.Text.Trim();
        if (name.Length == 0) { message = "Enter a section name."; return null; }
        if (taken.Contains(name)) { message = $"A section named '{name}' already exists."; return null; }
        if (Parse(areaBox) is not (> 0) || areaBox.Text.Length == 0) { message = "The area must be greater than zero."; return null; }
        return section with
        {
            Name = name, Material = materialBox.SelectedItem as string ?? section.Material, Area = Parse(areaBox)!.Value,
            Source = section.Source is "App" or "" ? "App" : section.Source.EndsWith("(edited)", StringComparison.Ordinal) ? section.Source : section.Source + " (edited)",
        };
    }
}
