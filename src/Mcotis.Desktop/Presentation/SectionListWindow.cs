using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Presentation;

public enum SectionListKind { Frame, Cable, Tendon }

/// <summary>
/// Define → Section Properties → Frame / Cable / Tendon Sections, laid out like SAP2000's "Frame Properties": Find this
/// property, the list, and Get From SAP2000, Import New Property…, Add New Property…, Add Copy of Property…, Modify/Show
/// Property…, Delete Property, OK / Cancel. The list is edited as a copy; OK hands it to the view model.
/// </summary>
public sealed class SectionListWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly SectionListKind kind;
    private readonly ObservableCollection<object> working;
    private readonly Dictionary<string, string> renamed = new(StringComparer.Ordinal);
    private readonly ListBox list = new() { DisplayMemberPath = "Name", BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
    private readonly TextBox find = new() { Margin = new Thickness(0, 2, 0, 4) };
    private readonly TextBlock status = new() { Foreground = Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button getButton, importButton, addButton, copyButton, modifyButton, deleteButton;

    /// <summary>Opens a section in its form; returns the edited section or null (checks replace it).</summary>
    internal Func<object, IReadOnlySet<string>, object?> EditSection { get; set; }
    /// <summary>Chooses the shape for Add New Property (checks replace it).</summary>
    internal Func<FrameShape?> PickShape { get; set; }

    public SectionListWindow(MainViewModel viewModel, SectionListKind kind)
    {
        this.viewModel = viewModel;
        this.kind = kind;
        viewModel.EnsureMaterials();
        viewModel.EnsureSections();
        working = new(kind switch
        {
            SectionListKind.Frame => viewModel.FrameSections.Cast<object>(),
            SectionListKind.Cable => viewModel.CableSections.Cast<object>(),
            _ => viewModel.TendonSections.Cast<object>(),
        });
        Title = kind switch { SectionListKind.Frame => "Frame Properties", SectionListKind.Cable => "Cable Section Properties", _ => "Tendon Section Properties" };
        Icon = AppIcon.Window;
        Width = 700; Height = 470; MinWidth = 600; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = (Brush)FindResource("Surface");

        var view = CollectionViewSource.GetDefaultView(working);
        view.Filter = item => find.Text.Trim().Length == 0 || SectionName(item).Contains(find.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        list.ItemsSource = view;
        System.Windows.Automation.AutomationProperties.SetName(list, "Sections");
        System.Windows.Automation.AutomationProperties.SetName(find, "Find this property");
        find.TextChanged += (_, _) =>
        {
            view.Refresh();
            var exact = working.FirstOrDefault(item => SectionName(item).Equals(find.Text.Trim(), StringComparison.OrdinalIgnoreCase));
            list.SelectedItem = exact ?? list.Items.Cast<object>().FirstOrDefault();
        };
        list.SelectionChanged += (_, _) => UpdateState();
        list.MouseDoubleClick += (_, _) => Modify();

        Button Action(string text, RoutedEventHandler click)
        {
            var button = new Button { Content = text, Width = 230, Margin = new Thickness(0, 0, 0, 8) };
            button.Click += click;
            ToolTipService.SetShowOnDisabled(button, true);
            return button;
        }
        getButton = Action("Get From SAP2000", async (_, _) => await GetFromSapAsync());
        getButton.ToolTip = "Read every section of the model open in the running SAP2000 (read-only; works without Connect SAP)";
        importButton = Action("Import New Property...", (_, _) => { });
        importButton.IsEnabled = false;
        importButton.ToolTip = "SAP2000 section database files are not available here — use Get From SAP2000 for the model's sections";
        addButton = Action("Add New Property...", (_, _) => AddNew());
        copyButton = Action("Add Copy of Property...", (_, _) => AddCopy());
        modifyButton = Action("Modify/Show Property...", (_, _) => Modify());
        deleteButton = Action("Delete Property", (_, _) => Delete());
        deleteButton.Margin = new Thickness(0);

        EditSection = (section, taken) => section switch
        {
            FrameSectionProperty { Shape: FrameShape.FilledTube or FrameShape.Encased or FrameShape.MegaComposite } composite =>
                new CompositeSectionWindow(viewModel, composite, taken) { Owner = this } is var form && form.ShowDialog() == true ? form.Result : null,
            FrameSectionProperty frame => new FrameSectionWindow(viewModel, frame, taken) { Owner = this } is var form && form.ShowDialog() == true ? form.Result : null,
            StrandSectionProperty strand => new StrandSectionWindow(viewModel, strand, taken) { Owner = this } is var form && form.ShowDialog() == true ? form.Result : null,
            _ => null,
        };
        PickShape = () => PropertyForm.Pick(this, "Add Frame Section Property", "Select the section shape:", ShapeChoices, FrameSectionProperty.Title);

        var properties = new GroupBox { Header = "Properties", Padding = new Thickness(8), BorderBrush = Brushes.Gray, Foreground = (Brush)FindResource("Accent"),
            Content = new DockPanel { Children = { Docked(new StackPanel { Children = { new TextBlock { Text = "Find this property:", Foreground = Brushes.Black }, find } }, Dock.Top), list } } };
        var actions = new GroupBox { Header = "Click to:", Padding = new Thickness(10), BorderBrush = Brushes.Gray, Foreground = (Brush)FindResource("Accent"),
            Content = new StackPanel { Children = { getButton, importButton, addButton, copyButton, modifyButton, deleteButton, status } } };
        var grid = new Grid { Margin = new Thickness(10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(actions, 2);
        grid.Children.Add(properties); grid.Children.Add(actions);
        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true, Margin = new Thickness(0, 10, 30, 0) };
        ok.Click += (_, _) => Commit();
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, Margin = new Thickness(0, 10, 0, 0) };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { ok, cancel } };
        Grid.SetRow(buttons, 1); Grid.SetColumnSpan(buttons, 3);
        grid.Children.Add(buttons);
        Content = grid;
        if (working.Count > 0) list.SelectedIndex = 0;
        UpdateState();
    }

    private static UIElement Docked(UIElement element, Dock dock) { DockPanel.SetDock(element, dock); return element; }
    private static string SectionName(object item) => item switch { FrameSectionProperty frame => frame.Name, StrandSectionProperty strand => strand.Name, _ => "" };
    private static object Renamed(object item, string name, string source) => item switch
    {
        FrameSectionProperty frame => frame with { Name = name, Source = source },
        StrandSectionProperty strand => strand with { Name = name, Source = source },
        _ => item,
    };

    internal IReadOnlyList<object> Working => working;
    /// <summary>The shapes Add New Property offers: the steel shapes, then CFT and steel encased composite.</summary>
    internal static IReadOnlyList<FrameShape> ShapeChoices { get; } = Enum.GetValues<FrameShape>().Where(shape => shape != FrameShape.General).ToArray();
    private object? Selected => list.SelectedItem;
    private IReadOnlySet<string> Names(string? except = null) =>
        working.Select(SectionName).Where(name => name != except).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private string OriginalName(string current) => renamed.FirstOrDefault(pair => pair.Value == current).Key ?? current;

    private void UpdateState()
    {
        var selected = Selected;
        copyButton.IsEnabled = modifyButton.IsEnabled = selected is not null;
        var usage = selected is null ? 0 : viewModel.SectionUsage(OriginalName(SectionName(selected)));
        deleteButton.IsEnabled = selected is not null && usage == 0;
        deleteButton.ToolTip = usage > 0 ? $"Assigned to {usage} member{(usage == 1 ? "" : "s")} — cannot be deleted" : "Delete the selected section";
        if (status.Tag is "keep") { status.Tag = null; return; }
        status.Text = selected switch
        {
            FrameSectionProperty frame => $"{FrameSectionProperty.Title(frame.Shape)} · {string.Join(" + ", new[] { frame.Material, frame.SteelMaterial, frame.RebarMaterial }.Where(name => name.Length > 0))} · source: {frame.Source}" + (usage > 0 ? $" · {usage} member{(usage == 1 ? "" : "s")}" : ""),
            StrandSectionProperty strand => $"{strand.Material} · A = {PropertyForm.Number(strand.Area)} m² · source: {strand.Source}" + (usage > 0 ? $" · {usage} member{(usage == 1 ? "" : "s")}" : ""),
            _ => "",
        };
    }

    private string UniqueName(string stem)
    {
        var taken = Names();
        if (!taken.Contains(stem)) return stem;
        for (var index = 1; ; index++) if (!taken.Contains($"{stem} {index}")) return $"{stem} {index}";
    }

    internal async Task GetFromSapAsync()
    {
        try
        {
            var library = await viewModel.ReadSectionsFromSapAsync();
            IEnumerable<object> read = kind switch { SectionListKind.Frame => library.Frames, SectionListKind.Cable => library.Cables, _ => library.Tendons };
            var items = read.ToArray();
            var selected = Selected is { } current ? SectionName(current) : null;
            foreach (var item in items)
            {
                var index = working.ToList().FindIndex(existing => SectionName(existing).Equals(SectionName(item), StringComparison.Ordinal));
                if (index >= 0) working[index] = item; else working.Add(item);
            }
            var sorted = working.OrderBy(SectionName, StringComparer.CurrentCultureIgnoreCase).ToArray();
            working.Clear();
            foreach (var item in sorted) working.Add(item);
            list.SelectedItem = working.FirstOrDefault(item => SectionName(item) == selected) ?? working.FirstOrDefault();
            status.Text = $"Read {items.Length} {(kind == SectionListKind.Frame ? "frame" : kind == SectionListKind.Cable ? "cable" : "tendon")} section{(items.Length == 1 ? "" : "s")} from SAP2000 (read-only). OK keeps them.";
        }
        catch (Exception ex) { status.Text = "Get From SAP2000 failed: " + ex.Message; }
        status.Tag = "keep";
        UpdateState();
    }

    internal void AddNew()
    {
        object? fresh = kind switch
        {
            SectionListKind.Frame => PickShape() is { } shape ? NewFrameSection(shape) : null,
            _ => new StrandSectionProperty { Name = UniqueName(kind == SectionListKind.Cable ? $"CAB{working.Count + 1}" : $"TEN{working.Count + 1}"), IsTendon = kind == SectionListKind.Tendon,
                Area = 0.001, Material = viewModel.Materials.FirstOrDefault(item => item.Kind == MaterialKind.Tendon)?.Name ?? "", Modifiers = kind == SectionListKind.Cable ? [1, 1, 1] : [] },
        };
        if (fresh is not null && EditSection(fresh, Names()) is { } created) { working.Add(created); list.SelectedItem = created; }
    }

    /// <summary>A new frame section of <paramref name="shape"/>: steel for the steel shapes; a CFT / steel encased section
    /// takes the strongest concrete, the first steel and the first reinforcement of the library.</summary>
    /// <summary>The materials a new concrete filled tube starts with.</summary>
    internal const string CftConcrete = "C100/115", CftSteel = "S690";

    internal FrameSectionProperty NewFrameSection(FrameShape shape)
    {
        string First(MaterialKind kind) => viewModel.Materials.FirstOrDefault(item => item.Kind == kind)?.Name ?? "";
        if (shape is not (FrameShape.FilledTube or FrameShape.Encased or FrameShape.MegaComposite))
            return FrameSectionProperty.Default(shape, UniqueName($"FSEC{working.Count + 1}"), First(MaterialKind.Steel));
        // the concrete the model uses most (the library also offers every standard grade)
        var concrete = viewModel.Materials.Where(item => item.Kind == MaterialKind.Concrete)
            .OrderByDescending(item => viewModel.MaterialUsage(item.Name)).FirstOrDefault()?.Name ?? "";
        var stem = shape switch { FrameShape.FilledTube => "CFT", FrameShape.MegaComposite => "MEGA", _ => "SRC" };
        var section = FrameSectionProperty.Default(shape, UniqueName($"{stem}{working.Count + 1}"), concrete) with
        {
            SteelMaterial = First(MaterialKind.Steel), RebarMaterial = shape == FrameShape.FilledTube ? "" : First(MaterialKind.Rebar),
        };
        if (shape != FrameShape.FilledTube) return section;
        // a new CFT starts in C100/115 and S690 (fck 100 MPa, fy 690 MPa) when the library has them
        bool Has(string name, MaterialKind kind) => viewModel.Materials.Any(item => item.Name == name && item.Kind == kind);
        return section with
        {
            Material = Has(CftConcrete, MaterialKind.Concrete) ? CftConcrete : section.Material,
            SteelMaterial = Has(CftSteel, MaterialKind.Steel) ? CftSteel : section.SteelMaterial,
            Composite = section.Composite! with { Fck = 100, Fy = 690 },
        };
    }

    internal void AddCopy()
    {
        if (Selected is not { } source) return;
        var copy = Renamed(source, UniqueName(SectionName(source)), "App");
        if (EditSection(copy, Names()) is { } created) { working.Add(created); list.SelectedItem = created; }
    }

    internal void Modify()
    {
        if (Selected is not { } source) return;
        if (EditSection(source, Names(except: SectionName(source))) is not { } edited) return;
        working[working.IndexOf(source)] = edited;
        if (SectionName(edited) != SectionName(source)) renamed[OriginalName(SectionName(source))] = SectionName(edited);
        list.SelectedItem = edited;
    }

    internal void Delete()
    {
        if (Selected is not { } source || viewModel.SectionUsage(OriginalName(SectionName(source))) > 0) return;
        working.Remove(source);
        if (working.Count > 0) list.SelectedIndex = 0;
    }

    internal void Commit()
    {
        switch (kind)
        {
            case SectionListKind.Frame: viewModel.CommitFrameSections(working.Cast<FrameSectionProperty>().ToArray(), renamed); break;
            case SectionListKind.Cable: viewModel.CommitStrandSections(working.Cast<StrandSectionProperty>().ToArray(), renamed, tendons: false); break;
            default: viewModel.CommitStrandSections(working.Cast<StrandSectionProperty>().ToArray(), renamed, tendons: true); break;
        }
        Close();
    }

    /// <summary>Types into Find this property (checks).</summary>
    internal void Find(string text) => find.Text = text;
    internal object? SelectedSection => Selected;
}
