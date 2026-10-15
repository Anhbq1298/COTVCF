using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Mcotis.Application;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Presentation.Controls;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Presentation;

/// <summary>
/// Define → Grid Systems… / Levels… (geometry editing plan, P1; GRID_LEVEL_CONTROLLER.md): the grids and levels of the
/// model, edited on a copy — tables of lines and levels, Quick Grid and Generate Levels, the grid drawn over the model in
/// plan as it is typed — and written by Apply or OK as one step of the model's history; Cancel leaves the model as it was.
/// A grid line or a level is a reference: no point is made or moved, and the results stay current.
/// </summary>
public sealed class ReferenceGeometryWindow : Window
{
    /// <summary>One grid line as the table edits it.</summary>
    public sealed class LineRow : Observable
    {
        private string label = "";
        private double coordinate;
        private bool visible = true;
        private double? spacing;
        public string Label { get => label; set => Set(ref label, value); }
        public double Coordinate { get => coordinate; set => Set(ref coordinate, value); }
        public bool Visible { get => visible; set => Set(ref visible, value); }
        /// <summary>From the line before it in the table, read-only.</summary>
        public double? Spacing { get => spacing; internal set => Set(ref spacing, value); }
    }

    /// <summary>One level as the table edits it.</summary>
    public sealed class LevelRow : Observable
    {
        private string name = "";
        private double elevation;
        private bool visible = true;
        private double? height;
        public string Name { get => name; set => Set(ref name, value); }
        public double Elevation { get => elevation; set => Set(ref elevation, value); }
        public bool Visible { get => visible; set => Set(ref visible, value); }
        /// <summary>To the next level above, read-only.</summary>
        public double? HeightToAbove { get => height; internal set => Set(ref height, value); }
    }

    /// <summary>One grid system as the form edits it.</summary>
    public sealed class SystemRow : Observable
    {
        private string name = "GLOBAL";
        private double originX, originY, originZ, rotation;
        private bool visible = true;
        public string Name { get => name; set => Set(ref name, value); }
        public double OriginX { get => originX; set => Set(ref originX, value); }
        public double OriginY { get => originY; set => Set(ref originY, value); }
        public double OriginZ { get => originZ; set => Set(ref originZ, value); }
        public double RotationDeg { get => rotation; set => Set(ref rotation, value); }
        public bool Visible { get => visible; set => Set(ref visible, value); }
        public ObservableCollection<LineRow> XLines { get; } = [];
        public ObservableCollection<LineRow> YLines { get; } = [];
        public override string ToString() => Name;
    }

    private readonly MainViewModel viewModel;
    internal ObservableCollection<SystemRow> Systems { get; } = [];
    internal ObservableCollection<LevelRow> Levels { get; } = [];
    private readonly ComboBox systemBox = new() { Width = 180, DisplayMemberPath = nameof(SystemRow.Name) };
    private readonly StackPanel systemEditor = new();
    private readonly TextBlock problem = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), MaxWidth = 900 };
    private readonly Button ok = new() { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button apply = new() { Content = "Apply", Width = 80, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Structural2DViewer preview = new() { Height = 280, MinWidth = 380, ShowPointLabels = false, ShowDimensions = false, KeepViewOnEdit = false };
    internal TabControl Tabs { get; } = new();
    private bool loading;

    internal ReferenceGeometryWindow(MainViewModel viewModel, bool levels = false)
    {
        this.viewModel = viewModel;
        Title = "Grids and Levels";
        Icon = AppIcon.Window;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface");

        Tabs.Items.Add(new TabItem { Header = "Grid Systems", Content = GridSystemsPage() });
        Tabs.Items.Add(new TabItem { Header = "Levels", Content = LevelsPage() });
        Tabs.SelectedIndex = levels ? 1 : 0;

        var suggest = new Button { Content = "Suggest from Model", Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 0, 10, 0),
            ToolTip = "Fill the form with a grid line at every distinct X and Y of the model's points and a level at every distinct Z (nothing is written until Apply)" };
        suggest.Click += (_, _) => Load(GeometryEditService.SuggestFromPoints(viewModel.NativeDocumentSession.Document));
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        ok.Click += (_, _) => { if (ApplyForm()) Close(); };
        apply.Click += (_, _) => ApplyForm();
        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        DockPanel.SetDock(suggest, Dock.Left);
        buttons.Children.Add(suggest);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(ok); right.Children.Add(apply); right.Children.Add(cancel);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);

        var note = new TextBlock
        {
            Text = "Grid lines and levels are references to draw and navigate on: no joint is made or moved, and the results stay current. " +
                   "Draw → Generate Points at Grid Intersections makes joints at the crossings, only when asked.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 900, Margin = new Thickness(0, 6, 0, 0),
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        var root = new StackPanel { Margin = new Thickness(12) };
        foreach (var child in new UIElement[] { Tabs, note, problem, buttons }) root.Children.Add(child);
        Content = root;

        preview.ShowPlane("XY");
        Load(viewModel.NativeDocumentSession.Document.ReferenceGeometry);
    }

    // ---- the pages ----

    private UIElement GridSystemsPage()
    {
        var page = new StackPanel { Margin = new Thickness(8) };
        var header = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(Label("Grid system"));
        systemBox.ItemsSource = Systems;
        systemBox.SelectionChanged += (_, _) => ShowSystem();
        header.Children.Add(systemBox);
        foreach (var (text, action) in new (string, Action)[]
                 {
                     ("New", () => AddSystem(new SystemRow { Name = FreeSystemName("G") })),
                     ("Copy", () => { if (Selected is { } s) AddSystem(Copy(s, FreeSystemName(s.Name + " copy"))); }),
                     ("Delete", () => { if (Selected is { } s) { Systems.Remove(s); systemBox.SelectedItem = Systems.FirstOrDefault(); Changed(); } }),
                 })
        {
            var button = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 0, 10, 0) };
            button.Click += (_, _) => action();
            header.Children.Add(button);
        }
        page.Children.Add(header);
        page.Children.Add(systemEditor);
        return page;
    }

    private SystemRow? Selected => systemBox.SelectedItem as SystemRow;

    private void AddSystem(SystemRow system)
    {
        Watch(system);
        Systems.Add(system);
        systemBox.SelectedItem = system;
        Changed();
    }

    private string FreeSystemName(string stem)
    {
        var name = stem;
        for (var n = 2; Systems.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)); n++) name = $"{stem} {n}";
        return name;
    }

    private static SystemRow Copy(SystemRow source, string name)
    {
        var copy = new SystemRow { Name = name, OriginX = source.OriginX, OriginY = source.OriginY, OriginZ = source.OriginZ, RotationDeg = source.RotationDeg, Visible = source.Visible };
        foreach (var line in source.XLines) copy.XLines.Add(new() { Label = line.Label, Coordinate = line.Coordinate, Visible = line.Visible });
        foreach (var line in source.YLines) copy.YLines.Add(new() { Label = line.Label, Coordinate = line.Coordinate, Visible = line.Visible });
        return copy;
    }

    /// <summary>The editor of the system chosen: its name, origin and rotation, the two tables, Quick Grid, the preview.</summary>
    private void ShowSystem()
    {
        if (preview.Parent is ContentControl holder) holder.Content = null;   // the one preview moves to the new editor
        systemEditor.Children.Clear();
        if (Selected is not { } system)
        {
            systemEditor.Children.Add(new TextBlock { Text = "No grid system. New makes one; Suggest from Model fills one from the model's points.", Margin = new Thickness(0, 8, 0, 8) });
            return;
        }
        var origin = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        origin.Children.Add(Label("Name"));
        origin.Children.Add(Box(system, nameof(SystemRow.Name), 120, text: true));
        origin.Children.Add(Label("Origin X", 14));
        origin.Children.Add(Box(system, nameof(SystemRow.OriginX)));
        origin.Children.Add(Label("Y", 8));
        origin.Children.Add(Box(system, nameof(SystemRow.OriginY)));
        origin.Children.Add(Label("Z", 8));
        origin.Children.Add(Box(system, nameof(SystemRow.OriginZ)));
        origin.Children.Add(Label("m   Rotation", 4));
        origin.Children.Add(Box(system, nameof(SystemRow.RotationDeg)));
        origin.Children.Add(Label("°", 4));
        var visible = new CheckBox { Content = "Visible", Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        visible.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(SystemRow.Visible)) { Source = system, Mode = BindingMode.TwoWay });
        origin.Children.Add(visible);
        systemEditor.Children.Add(origin);

        var tables = new Grid();
        for (var i = 0; i < 3; i++) tables.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var xTable = LinesTable(system, system.XLines, true);
        var yTable = LinesTable(system, system.YLines, false);
        Grid.SetColumn(yTable, 1);
        tables.Children.Add(xTable);
        tables.Children.Add(yTable);
        var previewBox = new GroupBox { Header = "Preview — plan (XY)", Content = preview, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(2) };
        Grid.SetColumn(previewBox, 2);
        tables.Children.Add(previewBox);
        systemEditor.Children.Add(tables);
        systemEditor.Children.Add(QuickGrid(system));
    }

    private UIElement LinesTable(SystemRow system, ObservableCollection<LineRow> lines, bool xAxis)
    {
        var axis = xAxis ? "X" : "Y";
        var grid = new DataGrid
        {
            ItemsSource = lines, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column, Height = 220, Width = 300, SelectionMode = DataGridSelectionMode.Extended,
        };
        System.Windows.Automation.AutomationProperties.SetName(grid, $"{axis} grid lines");
        grid.Columns.Add(new DataGridTextColumn { Header = "Grid ID", Binding = new Binding(nameof(LineRow.Label)) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = 64 });
        grid.Columns.Add(new DataGridTextColumn { Header = $"{axis} (m)", Binding = Number(nameof(LineRow.Coordinate)), Width = 80 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Spacing (m)", Binding = new Binding(nameof(LineRow.Spacing)) { StringFormat = "0.###", Mode = BindingMode.OneWay }, IsReadOnly = true, Width = 80 });
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Visible", Binding = new Binding(nameof(LineRow.Visible)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 50 });
        var add = new Button { Content = "+ Add", Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 0, 8, 0),
            ToolTip = $"A line after the last, one spacing on, with the next label" };
        add.Click += (_, _) =>
        {
            var last = lines.LastOrDefault();
            var spacing = last?.Spacing ?? 6;
            var line = new LineRow { Label = NextLabel(system, xAxis), Coordinate = (last?.Coordinate ?? 0) + (last is null ? 0 : spacing) };
            Watch(line);
            lines.Add(line);
            Changed();
        };
        var remove = new Button { Content = "- Remove", Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 0, 8, 0) };
        remove.Click += (_, _) =>
        {
            foreach (var row in grid.SelectedItems.OfType<LineRow>().ToArray()) lines.Remove(row);
            Changed();
        };
        var sort = new Button { Content = "Sort", Padding = new Thickness(8, 0, 8, 0), ToolTip = "Order the lines by coordinate (OK does this too)" };
        sort.Click += (_, _) =>
        {
            var ordered = lines.OrderBy(line => line.Coordinate).ToArray();
            lines.Clear();
            foreach (var line in ordered) lines.Add(line);
            Changed();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(add); buttons.Children.Add(remove); buttons.Children.Add(sort);
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        panel.Children.Add(new TextBlock { Text = $"{axis} GRID LINES (lines of constant {axis.ToLowerInvariant()}, labels {(xAxis ? "1, 2, 3 …" : "A, B, C …")})" }.Subheading());
        panel.Children.Add(grid);
        panel.Children.Add(buttons);
        return panel;
    }

    private string NextLabel(SystemRow system, bool xAxis)
    {
        var used = system.XLines.Concat(system.YLines).Select(line => line.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; ; index++)
        {
            var label = GeometryEditService.GridLabel(xAxis, index);
            if (!used.Contains(label)) return label;
        }
    }

    /// <summary>Quick Grid: the lines of an axis from the first line, a number of equal bays or a list of spacings.</summary>
    private UIElement QuickGrid(SystemRow system)
    {
        var panel = new StackPanel();
        TextBox Field(string text, double width = 60) => new() { Text = text, Width = width, Margin = new Thickness(0, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center };
        var (xStart, xBays, xSpacing, xList) = (Field("0"), Field("3", 40), Field("6"), Field("", 150));
        var (yStart, yBays, ySpacing, yList) = (Field("0"), Field("2", 40), Field("6"), Field("", 150));
        var result = new TextBlock { Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 860 };
        WrapPanel Row(string axis, TextBox start, TextBox bays, TextBox spacing, TextBox list)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(Label($"{axis}: first line at"));
            row.Children.Add(start);
            row.Children.Add(Label("m,", 0));
            row.Children.Add(bays);
            row.Children.Add(Label("bays of", 0));
            row.Children.Add(spacing);
            row.Children.Add(Label("m — or spacings", 0));
            row.Children.Add(list);
            row.Children.Add(Label("m (e.g. 6, 8, 6)", 0));
            return row;
        }
        var generate = new Button { Content = "Generate Lines", Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "Replace the lines of this system with the ones described (the tables above can be edited afterwards)" };
        generate.Click += (_, _) =>
        {
            try
            {
                var xs = Lines(true, xStart.Text, xBays.Text, xSpacing.Text, xList.Text);
                var ys = Lines(false, yStart.Text, yBays.Text, ySpacing.Text, yList.Text);
                system.XLines.Clear(); system.YLines.Clear();
                foreach (var line in xs) { var row = new LineRow { Label = line.Label, Coordinate = line.Coordinate }; Watch(row); system.XLines.Add(row); }
                foreach (var line in ys) { var row = new LineRow { Label = line.Label, Coordinate = line.Coordinate }; Watch(row); system.YLines.Add(row); }
                result.Text = $"{xs.Length} X lines ({string.Join(", ", xs.Select(line => line.Coordinate.ToString("0.###", CultureInfo.CurrentCulture)))} m) and {ys.Length} Y lines ({string.Join(", ", ys.Select(line => line.Coordinate.ToString("0.###", CultureInfo.CurrentCulture)))} m).";
                result.SetResourceReference(TextBlock.ForegroundProperty, "Text");
                Changed();
            }
            catch (Exception ex) when (ex is FormatException or Native.DocumentException)
            {
                result.Text = ex.Message;
                result.SetResourceReference(TextBlock.ForegroundProperty, "Error");
            }
        };
        panel.Children.Add(Row("X", xStart, xBays, xSpacing, xList));
        panel.Children.Add(Row("Y", yStart, yBays, ySpacing, yList));
        panel.Children.Add(generate);
        panel.Children.Add(result);
        return new GroupBox { Header = "Quick Grid", Content = panel, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
    }

    private static System.Collections.Immutable.ImmutableArray<Native.GridLine> Lines(bool xAxis, string start, string bays, string spacing, string list)
    {
        var first = Parse(start, "the first line");
        if (!string.IsNullOrWhiteSpace(list))
            return GeometryEditService.LinesFromSpacings(xAxis, first, list.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(text => Parse(text, "a spacing")));
        if (!int.TryParse(bays, NumberStyles.Integer, CultureInfo.CurrentCulture, out var count)) throw new FormatException($"'{bays}' is not a whole number of bays.");
        return GeometryEditService.UniformLines(xAxis, first, count, Parse(spacing, "the spacing"));
    }

    private UIElement LevelsPage()
    {
        var page = new StackPanel { Margin = new Thickness(8) };
        var grid = new DataGrid
        {
            ItemsSource = Levels, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column, Height = 240, Width = 420, SelectionMode = DataGridSelectionMode.Extended, HorizontalAlignment = HorizontalAlignment.Left,
        };
        System.Windows.Automation.AutomationProperties.SetName(grid, "Levels");
        grid.Columns.Add(new DataGridTextColumn { Header = "Level", Binding = new Binding(nameof(LevelRow.Name)) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Elevation Z (m)", Binding = Number(nameof(LevelRow.Elevation)), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Height to above (m)", Binding = new Binding(nameof(LevelRow.HeightToAbove)) { StringFormat = "0.###", Mode = BindingMode.OneWay }, IsReadOnly = true, Width = 120 });
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Visible", Binding = new Binding(nameof(LevelRow.Visible)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 60 });
        var add = new Button { Content = "+ Add Above", Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 0, 8, 0), ToolTip = "A level above the highest, one storey height on" };
        add.Click += (_, _) =>
        {
            var ordered = Levels.OrderBy(level => level.Elevation).ToArray();
            var height = ordered.Length >= 2 ? ordered[^1].Elevation - ordered[^2].Elevation : 4;
            var name = "L1";
            for (var n = 1; Levels.Any(level => string.Equals(level.Name, name, StringComparison.OrdinalIgnoreCase)); n++) name = $"L{n}";
            var row = new LevelRow { Name = Levels.Count == 0 ? "Base" : name, Elevation = ordered.Length == 0 ? 0 : ordered[^1].Elevation + height };
            Watch(row);
            Levels.Add(row);
            Changed();
        };
        var remove = new Button { Content = "- Remove", Padding = new Thickness(8, 0, 8, 0) };
        remove.Click += (_, _) => { foreach (var row in grid.SelectedItems.OfType<LevelRow>().ToArray()) Levels.Remove(row); Changed(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(add); buttons.Children.Add(remove);
        page.Children.Add(new TextBlock { Text = "LEVELS (STOREYS), FROM THE LOWEST UP ON OK" }.Subheading());
        page.Children.Add(grid);
        page.Children.Add(buttons);

        TextBox Field(string text, double width = 60) => new() { Text = text, Width = width, Margin = new Thickness(0, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center };
        var (baseZ, storeys, height, list) = (Field("0"), Field("3", 40), Field("4"), Field("", 160));
        var result = new TextBlock { Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 620 };
        var row = new WrapPanel();
        row.Children.Add(Label("Base at Z ="));
        row.Children.Add(baseZ);
        row.Children.Add(Label("m,", 0));
        row.Children.Add(storeys);
        row.Children.Add(Label("storeys of", 0));
        row.Children.Add(height);
        row.Children.Add(Label("m — or heights", 0));
        row.Children.Add(list);
        row.Children.Add(Label("m (e.g. 4.5, 4.8)", 0));
        var generate = new Button { Content = "Generate Levels", Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "Replace the levels with Base, L1, L2 … Roof at the running elevations (the table can be edited afterwards)" };
        generate.Click += (_, _) =>
        {
            try
            {
                var first = Parse(baseZ.Text, "the base");
                double[] heights = !string.IsNullOrWhiteSpace(list.Text)
                    ? list.Text.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(text => Parse(text, "a storey height")).ToArray()
                    : int.TryParse(storeys.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var count) && count is >= 0 and <= 500
                        ? Enumerable.Repeat(Parse(height.Text, "the storey height"), count).ToArray()
                        : throw new FormatException($"'{storeys.Text}' is not a number of storeys from 0 to 500.");
                var levels = GeometryEditService.LevelsFromHeights(first, heights);
                Levels.Clear();
                foreach (var level in levels) { var r = new LevelRow { Name = level.Name, Elevation = level.Elevation }; Watch(r); Levels.Add(r); }
                result.Text = $"{levels.Length} levels: {string.Join(", ", levels.Select(level => $"{level.Name} {level.Elevation.ToString("0.###", CultureInfo.CurrentCulture)}"))} m.";
                result.SetResourceReference(TextBlock.ForegroundProperty, "Text");
                Changed();
            }
            catch (Exception ex) when (ex is FormatException or Native.DocumentException)
            {
                result.Text = ex.Message;
                result.SetResourceReference(TextBlock.ForegroundProperty, "Error");
            }
        };
        var generator = new StackPanel();
        generator.Children.Add(row);
        generator.Children.Add(generate);
        generator.Children.Add(result);
        page.Children.Add(new GroupBox { Header = "Generate Levels", Content = generator, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 4, 8, 4) });
        return page;
    }

    // ---- staged data ↔ the model ----

    /// <summary>Fills the form with <paramref name="reference"/> (the model's, or a suggestion).</summary>
    internal void Load(Native.ReferenceGeometry reference)
    {
        loading = true;
        try
        {
            Systems.Clear();
            foreach (var system in reference.GridSystems)
            {
                var row = new SystemRow { Name = system.Name, OriginX = system.Origin.X, OriginY = system.Origin.Y, OriginZ = system.Origin.Z, RotationDeg = system.RotationDeg, Visible = system.Visible };
                foreach (var line in system.XLines) row.XLines.Add(new() { Label = line.Label, Coordinate = line.Coordinate, Visible = line.Visible });
                foreach (var line in system.YLines) row.YLines.Add(new() { Label = line.Label, Coordinate = line.Coordinate, Visible = line.Visible });
                Watch(row);
                Systems.Add(row);
            }
            Levels.Clear();
            foreach (var level in reference.Levels)
            {
                var row = new LevelRow { Name = level.Name, Elevation = level.Elevation, Visible = level.Visible };
                Watch(row);
                Levels.Add(row);
            }
            systemBox.SelectedItem = Systems.FirstOrDefault();
            ShowSystem();
        }
        finally { loading = false; }
        Changed();
    }

    /// <summary>What the form says, as the model would hold it (not yet checked).</summary>
    internal Native.ReferenceGeometry FormReference() => new(
        [.. Systems.Select(system => new Native.GridSystem(system.Name, new(system.OriginX, system.OriginY, system.OriginZ), system.RotationDeg,
            [.. system.XLines.Select(line => new Native.GridLine(line.Label, line.Coordinate, line.Visible))],
            [.. system.YLines.Select(line => new Native.GridLine(line.Label, line.Coordinate, line.Visible))], system.Visible))],
        [.. Levels.Select(level => new Native.Level(level.Name, level.Elevation, level.Visible))]);

    private void Watch(SystemRow system)
    {
        system.PropertyChanged += (_, _) => Changed();
        foreach (var line in system.XLines.Concat(system.YLines)) Watch(line);
        system.XLines.CollectionChanged += (_, _) => Changed();
        system.YLines.CollectionChanged += (_, _) => Changed();
    }
    private void Watch(Observable row) => row.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName is nameof(LineRow.Spacing) or nameof(LevelRow.HeightToAbove)) return;
        Changed();
    };

    /// <summary>After any change: spacings and storey heights read again, the preview redrawn, the form checked — what is
    /// not valid is said under the tabs and holds OK and Apply back.</summary>
    private void Changed()
    {
        if (loading) return;
        foreach (var system in Systems)
            foreach (var lines in new[] { system.XLines, system.YLines })
                for (var i = 0; i < lines.Count; i++) lines[i].Spacing = i == 0 ? null : lines[i].Coordinate - lines[i - 1].Coordinate;
        var byElevation = Levels.OrderBy(level => level.Elevation).ToArray();
        for (var i = 0; i < byElevation.Length; i++) byElevation[i].HeightToAbove = i + 1 < byElevation.Length ? byElevation[i + 1].Elevation - byElevation[i].Elevation : null;

        var form = FormReference();
        try
        {
            var normalized = form.Normalized();
            problem.Text = $"{normalized.GridSystems.Length} grid system{(normalized.GridSystems.Length == 1 ? "" : "s")}, " +
                           $"{normalized.GridSystems.Sum(system => system.XLines.Length + system.YLines.Length)} grid lines, {normalized.Levels.Length} level{(normalized.Levels.Length == 1 ? "" : "s")}.";
            problem.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            ok.IsEnabled = apply.IsEnabled = true;
            preview.ReferenceGeometry = normalized;
        }
        catch (Native.DocumentException ex)
        {
            problem.Text = ex.Message;
            problem.SetResourceReference(TextBlock.ForegroundProperty, "Error");
            ok.IsEnabled = apply.IsEnabled = false;
        }
        preview.Model = viewModel.SharedViewerModel;
    }

    /// <summary>Writes the form to the model as one step; false (and the reason shown) when it is refused.</summary>
    internal bool ApplyForm()
    {
        try
        {
            viewModel.ApplyReferenceGeometry(FormReference(), Tabs.SelectedIndex == 1 ? "Edit Levels" : "Edit Grid Systems");
            problem.Text = viewModel.Message;
            problem.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var selected = Selected?.Name;
            Load(viewModel.NativeDocumentSession.Document.ReferenceGeometry);
            systemBox.SelectedItem = Systems.FirstOrDefault(system => system.Name == selected) ?? Systems.FirstOrDefault();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            problem.Text = ex.Message;
            problem.SetResourceReference(TextBlock.ForegroundProperty, "Error");
            return false;
        }
    }

    // ---- small helpers ----

    private static TextBlock Label(string text, double left = 0) =>
        new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 6, 0) };

    private static Binding Number(string path) => new(path)
    {
        StringFormat = "0.###", UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, ValidatesOnExceptions = true, ConverterCulture = CultureInfo.CurrentCulture,
    };

    private static TextBox Box(object source, string path, double width = 64, bool text = false)
    {
        var box = new TextBox { Width = width, Margin = new Thickness(0, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center };
        box.SetBinding(TextBox.TextProperty, text ? new Binding(path) { Source = source, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }
            : new Binding(path) { Source = source, StringFormat = "0.###", UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, ValidatesOnExceptions = true, ConverterCulture = CultureInfo.CurrentCulture });
        System.Windows.Automation.AutomationProperties.SetName(box, path);
        return box;
    }

    internal static double Parse(string text, string what) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value) ? value
        : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) ? value
        : throw new FormatException($"'{text}' is not a number for {what}.");
}

internal static class TextBlockStyles
{
    /// <summary>The theme's small uppercase section label.</summary>
    public static TextBlock Subheading(this TextBlock text)
    {
        text.SetResourceReference(FrameworkElement.StyleProperty, "Subheading");
        text.Margin = new Thickness(0, 0, 0, 4);
        return text;
    }
}
