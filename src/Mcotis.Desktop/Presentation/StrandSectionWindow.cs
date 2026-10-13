using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mcotis.Desktop.Domain;
using static Mcotis.Desktop.Presentation.PropertyForm;

namespace Mcotis.Desktop.Presentation;

/// <summary>A cable or tendon section form (SAP2000's Cable / Tendon Section Data): name, colour, notes; material; diameter
/// and area (linked); for a cable the area / mass / weight modifiers. Strand Bundle works the section out from its
/// strands instead — number × steel area of one strand — and names it by the convention (<see cref="StrandBundle"/>).
/// OK returns the edited section in <see cref="Result"/>.</summary>
public sealed class StrandSectionWindow : Window
{
    private StrandSectionProperty section;
    private readonly IReadOnlySet<string> taken;
    private readonly TextBox nameBox, diameterBox, areaBox, strandCountBox, strandAreaBox;
    private readonly ComboBox materialBox, strandDiameterBox;
    private readonly CheckBox fromStrands = new() { Content = "Calculate the section from its strands", Margin = new Thickness(0, 0, 0, 4), Foreground = Brushes.Black };
    private readonly TextBlock strandSummary = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock problem = new() { Foreground = Brushes.Firebrick, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private bool linking, applyingStrands, nameTyped;

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
        nameBox.TextChanged += (_, _) => { if (!applyingStrands) nameTyped = true; };   // a name typed by hand is kept
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

        // Strand Bundle: the equivalent section of n parallel strands. A name written by the convention (192T15.7) fills the
        // inputs, but nothing is changed until the option is ticked.
        var bundle = Group(root, "Strand Bundle");
        var culture = CultureInfo.CurrentCulture;
        var stated = StrandBundle.Parse(section.Name);
        fromStrands.ToolTip = "Area = number of strands × steel area of one strand; the section is named by the convention, e.g. 192T15.7 = 192 strands of 15.7 mm";
        ((StackPanel)bundle.Content).Children.Add(fromStrands);
        strandCountBox = new TextBox { Text = stated?.Count.ToString(culture) ?? "", Width = 100, TextAlignment = TextAlignment.Right };
        System.Windows.Automation.AutomationProperties.SetName(strandCountBox, "Number of strands");
        Row(bundle, "Number of strands", strandCountBox, "");
        strandDiameterBox = new ComboBox
        {
            IsEditable = true, Width = 100, ItemsSource = StrandBundle.StandardStrands.Select(strand => strand.DiameterMm.ToString("0.###", culture)).ToArray(),
            Text = stated?.DiameterMm.ToString("0.###", culture) ?? "",
            ToolTip = "Nominal diameter of one 7-wire strand. The listed sizes carry their nominal steel area (EN 10138-3); type any other diameter and enter its area.",
        };
        System.Windows.Automation.AutomationProperties.SetName(strandDiameterBox, "Strand diameter");
        Row(bundle, "Strand diameter", strandDiameterBox, "mm");
        strandAreaBox = new TextBox
        {
            // a listed strand's nominal area; for another strand the section's own area shared over its strands
            Text = stated is { } named ? Number(StrandBundle.StandardArea(named.DiameterMm) ?? (section.Area > 0 ? section.Area * 1e6 / named.Count : null)) : "",
            Width = 100, TextAlignment = TextAlignment.Right,
            ToolTip = "Steel area of one strand — the seven wires, less than the circle of its diameter. Filled for a listed diameter; enter it for another strand.",
        };
        System.Windows.Automation.AutomationProperties.SetName(strandAreaBox, "Steel area of one strand");
        Row(bundle, "Steel area of one strand", strandAreaBox, "mm²");
        ((StackPanel)bundle.Content).Children.Add(strandSummary);
        fromStrands.Checked += (_, _) => { nameTyped = false; ApplyStrands(); };
        fromStrands.Unchecked += (_, _) => ApplyStrands();
        strandCountBox.TextChanged += (_, _) => ApplyStrands();
        strandAreaBox.TextChanged += (_, _) => ApplyStrands();
        // typed or picked from the list: either way the combo's Text changes
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ComboBox.TextProperty, typeof(ComboBox))
            .AddValueChanged(strandDiameterBox, (_, _) => StrandDiameterChanged());
        ApplyStrands();

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

    // ---- Strand Bundle ----
    /// <summary>A listed diameter brings its nominal steel area; any other clears it — it is the user's to enter.</summary>
    private void StrandDiameterChanged()
    {
        if (applyingStrands) return;
        var diameter = double.TryParse(strandDiameterBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ? value : double.NaN;
        strandAreaBox.Text = Number(StrandBundle.StandardArea(diameter));   // fires ApplyStrands
        ApplyStrands();
    }

    /// <summary>The strands typed, or what is missing.</summary>
    private (int Count, double DiameterMm, double AreaMm2)? Strands(out string message)
    {
        message = "";
        if (!int.TryParse(strandCountBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var count) || count < 1)
        { message = "Enter the number of strands: a whole number of at least 1."; return null; }
        if (!double.TryParse(strandDiameterBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var diameter) || !double.IsFinite(diameter) || diameter <= 0)
        { message = "Enter the diameter of one strand in mm."; return null; }
        if (Parse(strandAreaBox) is not { } area || area <= 0)
        {
            message = StrandBundle.StandardArea(diameter) is null
                ? $"A {diameter.ToString("0.###", CultureInfo.CurrentCulture)} mm strand is not a listed size: enter the steel area of one strand."
                : "Enter the steel area of one strand in mm².";
            return null;
        }
        return (count, diameter, area);
    }

    /// <summary>With the option ticked, the area is number × strand area (the diameter follows as the solid round of that
    /// area) and the name follows the convention — unless a name has been typed by hand since. Unticked, the section is as
    /// entered above.</summary>
    private void ApplyStrands()
    {
        if (applyingStrands) return;
        var on = fromStrands.IsChecked == true;
        diameterBox.IsEnabled = areaBox.IsEnabled = !on;
        strandSummary.Foreground = (Brush)FindResource("Muted");
        if (!on) { strandSummary.Text = "Tick to work the area out as number of strands × steel area of one strand and name the section by the convention (192T15.7)."; return; }
        if (Strands(out var message) is not { } strands)
        {
            strandSummary.Foreground = Brushes.Firebrick;
            strandSummary.Text = message;
            return;
        }
        var area = StrandBundle.Area(strands.Count, strands.AreaMm2);
        applyingStrands = true;
        try
        {
            areaBox.Text = Number(area);   // the diameter box follows through Link
            if (!nameTyped) nameBox.Text = StrandBundle.Name(strands.Count, strands.DiameterMm);
        }
        finally { applyingStrands = false; }
        var culture = CultureInfo.CurrentCulture;
        strandSummary.Text = $"{strands.Count.ToString("N0", culture)} × {strands.AreaMm2.ToString("0.###", culture)} mm² = {(area * 1e6).ToString("N0", culture)} mm² · " +
            $"equivalent solid round Ø {(Math.Sqrt(4 * area / Math.PI) * 1000).ToString("0.#", culture)} mm" +
            (nameTyped ? "" : $" · named {nameBox.Text}");
    }

    internal void SetArea(string text) => areaBox.Text = text;

    /// <summary>Ticks Strand Bundle and types its inputs (checks); a null area keeps what the diameter brought.</summary>
    internal void SetStrands(string count, string diameterMm, string? strandAreaMm2 = null)
    {
        fromStrands.IsChecked = true;
        strandCountBox.Text = count;
        strandDiameterBox.Text = diameterMm;
        if (strandAreaMm2 is not null) strandAreaBox.Text = strandAreaMm2;
    }

    /// <summary>The Strand Bundle inputs and read-out as shown (checks).</summary>
    internal (bool On, string Count, string DiameterMm, string StrandAreaMm2, string Summary) StrandBundleShown =>
        (fromStrands.IsChecked == true, strandCountBox.Text, strandDiameterBox.Text, strandAreaBox.Text, strandSummary.Text);

    internal StrandSectionProperty? Read(out string message)
    {
        message = "";
        var name = nameBox.Text.Trim();
        if (name.Length == 0) { message = "Enter a section name."; return null; }
        if (taken.Contains(name)) { message = $"A section named '{name}' already exists."; return null; }
        if (fromStrands.IsChecked == true && Strands(out message) is null) return null;   // the area shown is not the strands'
        if (Parse(areaBox) is not (> 0) || areaBox.Text.Length == 0) { message = "The area must be greater than zero."; return null; }
        return section with
        {
            Name = name, Material = materialBox.SelectedItem as string ?? section.Material, Area = Parse(areaBox)!.Value,
            Source = section.Source is "App" or "" ? "App" : section.Source.EndsWith("(edited)", StringComparison.Ordinal) ? section.Source : section.Source + " (edited)",
        };
    }
}
