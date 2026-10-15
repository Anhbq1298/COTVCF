using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Mcotis.Application;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Presentation.GeometryEditing;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Presentation;

/// <summary>The shared layout of the geometry forms: a compact form, a line under it with the result (muted) or the
/// reason of a refusal (red) — never a message box — and the buttons at the bottom right.</summary>
public abstract class GeometryForm : Window
{
    protected readonly MainViewModel ViewModel;
    protected readonly TextBlock Note = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 8, 0, 0) };

    protected GeometryForm(MainViewModel viewModel, string title)
    {
        ViewModel = viewModel;
        Title = title;
        Icon = AppIcon.Window;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Surface");
    }

    protected void Say(string text, bool error = false)
    {
        Note.Text = text;
        Note.SetResourceReference(TextBlock.ForegroundProperty, error ? "Error" : "Muted");
    }

    protected static TextBox Field(string name, string text = "", double width = 84)
    {
        var box = new TextBox { Text = text, Width = width, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(box, name);
        return box;
    }

    protected static TextBlock Caption(string text, double width = 0) =>
        new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), MinWidth = width };

    protected static double Number(TextBox box, string what) => ReferenceGeometryWindow.Parse(box.Text.Trim(), what);

    protected static string Metres(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    protected static WrapPanel Row(params UIElement[] children)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 3, 0, 3) };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    protected StackPanel Buttons(params (string Text, bool IsDefault, bool IsCancel, Action Click)[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (text, isDefault, isCancel, click) in buttons)
        {
            var button = new Button { Content = text, MinWidth = 84, Padding = new Thickness(8, 0, 8, 0), IsDefault = isDefault, IsCancel = isCancel, Margin = new Thickness(6, 0, 0, 0) };
            button.Click += (_, _) => click();
            panel.Children.Add(button);
        }
        return panel;
    }

    /// <summary>Runs an edit of the model; the refusal, with its reason, goes under the form and the form stays open.</summary>
    protected bool Try(Action edit)
    {
        try
        {
            edit();
            Say(ViewModel.Message);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or Native.DocumentException)
        {
            Say(ex.Message, error: true);
            return false;
        }
    }
}

/// <summary>
/// Draw → Point by Coordinates… (geometry editing plan, P2): a point at X, Y, Z — absolute, relative to a point of the
/// model, or on the plane shown with its fixed coordinate held — named automatically (P1, P2 …) or by the user; Add Next
/// keeps the form open. The second tab adds a pasted table of X Y Z rows as one step. A position that has a point within
/// the model's tolerance is refused, naming it: no second, unconnected joint is made there.
/// </summary>
public sealed class PointCoordinatesWindow : GeometryForm
{
    private readonly TextBox name = Field("Point name", width: 120);
    private readonly TextBox x = Field("X"), y = Field("Y"), z = Field("Z");
    private readonly ComboBox mode = new() { Width = 200, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ComboBox reference = new() { Width = 120, IsEditable = true };
    private readonly TextBox table = new() { AcceptsReturn = true, AcceptsTab = true, Height = 150, Width = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new("Consolas") };
    internal TabControl Tabs { get; } = new();

    internal PointCoordinatesWindow(MainViewModel viewModel, IReadOnlyList<string> selectedJoints) : base(viewModel, "Point by Coordinates")
    {
        mode.Items.Add("Absolute X, Y, Z");
        mode.Items.Add("Relative to point (DX, DY, DZ)");
        if (viewModel.SharedViewerPlaneCoordinate is not null) mode.Items.Add($"On the plane shown ({viewModel.ViewPlaneAxisLabel} {Metres(viewModel.SharedViewerPlaneCoordinate.Value)} m)");
        mode.SelectedIndex = 0;
        System.Windows.Automation.AutomationProperties.SetName(mode, "Coordinates");
        foreach (var joint in viewModel.SharedViewerModel.Points.Keys.Order(StringComparer.Ordinal)) reference.Items.Add(joint);
        reference.Text = selectedJoints.FirstOrDefault() ?? "";
        System.Windows.Automation.AutomationProperties.SetName(reference, "Relative to point");
        mode.SelectionChanged += (_, _) => ShowMode();
        var fromSelection = new Button { Content = "From Selection", Padding = new Thickness(8, 0, 8, 0), IsEnabled = selectedJoints.Count > 0,
            ToolTip = "Take X, Y, Z of the first point selected on the model view" };
        fromSelection.Click += (_, _) =>
        {
            if (selectedJoints.Count == 0 || !viewModel.SharedViewerModel.Points.TryGetValue(selectedJoints[0], out var point)) return;
            mode.SelectedIndex = 0;
            (x.Text, y.Text, z.Text) = (Metres(point.Position.X), Metres(point.Position.Y), Metres(point.Position.Z));
        };

        var single = new StackPanel { Margin = new Thickness(8) };
        single.Children.Add(Row(Caption("Name", 70), name, Caption("(empty: P1, P2 … in sequence)")));
        single.Children.Add(Row(Caption("Coordinates", 70), mode, reference));
        single.Children.Add(Row(Caption("X (m)", 70), x, Caption("Y (m)"), y, Caption("Z (m)"), z));
        single.Children.Add(Row(fromSelection));

        var batch = new StackPanel { Margin = new Thickness(8) };
        batch.Children.Add(new TextBlock { Text = "One point per line: X Y Z in metres, separated by tabs, spaces, commas or semicolons — paste from a spreadsheet. A position that has a point already, or is repeated, is left out.", TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 0, 0, 6) });
        batch.Children.Add(table);
        System.Windows.Automation.AutomationProperties.SetName(table, "Points table");

        Tabs.Items.Add(new TabItem { Header = "Point", Content = single });
        Tabs.Items.Add(new TabItem { Header = "Table", Content = batch });
        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(Tabs);
        root.Children.Add(Note);
        root.Children.Add(Buttons(("Add Point", true, false, () => { if (Add()) Close(); }), ("Add Next", false, false, () => Add()), ("Close", false, true, Close)));
        Content = root;
        ShowMode();
        Say("The point is one Undo step. A position within 1 mm of a point of the model is that point: it is refused, not doubled.");
    }

    private void ShowMode()
    {
        reference.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        var onPlane = mode.SelectedIndex == 2 && ViewModel.SharedViewerPlaneCoordinate is { } c;
        // on the plane shown its fixed coordinate is held: read-only, at the plane's value
        foreach (var (box, axis) in new[] { (x, "X"), (y, "Y"), (z, "Z") })
        {
            var held = onPlane && ViewPlanes.FixedAxis(ViewModel.ViewPlane) == axis;
            box.IsReadOnly = held;
            if (held) box.Text = Metres(ViewModel.SharedViewerPlaneCoordinate!.Value);
        }
    }

    /// <summary>The position the form says, in the model's coordinates.</summary>
    internal Position FormPosition()
    {
        var (dx, dy, dz) = (Number(x, "X"), Number(y, "Y"), Number(z, "Z"));
        if (mode.SelectedIndex != 1) return new(dx, dy, dz);
        if (!ViewModel.SharedViewerModel.Points.TryGetValue(reference.Text.Trim(), out var origin))
            throw new FormatException($"There is no point '{reference.Text.Trim()}' to measure from.");
        return new(origin.Position.X + dx, origin.Position.Y + dy, origin.Position.Z + dz);
    }

    /// <summary>The rows of the table, or the reason one cannot be read (its line number).</summary>
    internal IReadOnlyList<Position> TableRows()
    {
        var rows = new List<Position>();
        var lines = table.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < lines.Length; index++)
        {
            var cells = lines[index].Split(['\t', ' ', ';', ','], StringSplitOptions.RemoveEmptyEntries);
            if (cells.Length == 0) continue;
            if (cells.Length != 3) throw new FormatException($"Line {index + 1} has {cells.Length} values; a point needs X, Y and Z.");
            try { rows.Add(new(ReferenceGeometryWindow.Parse(cells[0], "X"), ReferenceGeometryWindow.Parse(cells[1], "Y"), ReferenceGeometryWindow.Parse(cells[2], "Z"))); }
            catch (FormatException ex) { throw new FormatException($"Line {index + 1}: {ex.Message}"); }
        }
        if (rows.Count == 0) throw new FormatException("The table has no point.");
        return rows;
    }

    internal bool Add() => Tabs.SelectedIndex == 1
        ? Try(() => ViewModel.GeneratePoints(TableRows(), "Add Points"))
        : Try(() =>
        {
            ViewModel.AddPoint(FormPosition(), string.IsNullOrWhiteSpace(name.Text) ? null : name.Text);
            name.Text = "";
        });

    internal void SetForm(string px, string py, string pz, string? pointName = null) => (x.Text, y.Text, z.Text, name.Text) = (px, py, pz, pointName ?? "");
    internal void SetTable(string text) { Tabs.SelectedIndex = 1; table.Text = text; }
}

/// <summary>
/// Draw → Frame / Cable by Coordinates… (geometry editing plan, P3): a member from end I to end J, each typed or taken
/// from the points selected on the model view; an end on a point (within the model's tolerance) uses it, otherwise a point
/// is made. The member takes the section chosen here. Refusals — no length, doubling a member, lying along one — are said
/// under the form.
/// </summary>
public sealed class MemberCoordinatesWindow : GeometryForm
{
    private readonly TextBox xi = Field("XI"), yi = Field("YI"), zi = Field("ZI"), xj = Field("XJ"), yj = Field("YJ"), zj = Field("ZJ");
    private readonly RadioButton frame = new() { Content = "Frame", IsChecked = true, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton cable = new() { Content = "Cable", VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox section = new() { Width = 200 };
    private string? startJoint, endJoint;

    internal MemberCoordinatesWindow(MainViewModel viewModel, IReadOnlyList<string> selectedJoints) : base(viewModel, "Frame / Cable by Coordinates")
    {
        void Sections()
        {
            var cableKind = cable.IsChecked == true;
            section.ItemsSource = cableKind ? viewModel.DrawCableSectionChoices : viewModel.DrawFrameSectionChoices;
            section.SelectedItem = cableKind ? viewModel.DrawCableSection : viewModel.DrawFrameSection;
        }
        frame.Checked += (_, _) => Sections();
        cable.Checked += (_, _) => Sections();
        System.Windows.Automation.AutomationProperties.SetName(section, "Section");
        Sections();
        void Take(int index, TextBox bx, TextBox by, TextBox bz, Action<string> remember)
        {
            if (index >= selectedJoints.Count || !viewModel.SharedViewerModel.Points.TryGetValue(selectedJoints[index], out var point)) return;
            (bx.Text, by.Text, bz.Text) = (Metres(point.Position.X), Metres(point.Position.Y), Metres(point.Position.Z));
            remember(point.Name);
        }
        Take(0, xi, yi, zi, joint => startJoint = joint);
        Take(1, xj, yj, zj, joint => endJoint = joint);
        // an end typed over is a position, no longer the point it was taken from
        foreach (var box in new[] { xi, yi, zi }) box.TextChanged += (_, _) => startJoint = null;
        foreach (var box in new[] { xj, yj, zj }) box.TextChanged += (_, _) => endJoint = null;

        var form = new StackPanel();
        form.Children.Add(Row(Caption("Member", 70), frame, cable, Caption("   Section"), section));
        form.Children.Add(Row(Caption("End I (m)", 70), Caption("X"), xi, Caption("Y"), yi, Caption("Z"), zi));
        form.Children.Add(Row(Caption("End J (m)", 70), Caption("X"), xj, Caption("Y"), yj, Caption("Z"), zj));
        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(form);
        root.Children.Add(Note);
        root.Children.Add(Buttons(("Add", true, false, () => { if (Add()) Close(); }), ("Add Next", false, false, () => Add()), ("Close", false, true, Close)));
        Content = root;
        Say(selectedJoints.Count >= 2 ? $"Ends from the selection: {selectedJoints[0]} → {selectedJoints[1]}." : "Type both ends, or select two points on the model view first.");
    }

    internal bool Add() => Try(() =>
    {
        var start = new Position(Number(xi, "XI"), Number(yi, "YI"), Number(zi, "ZI"));
        var end = new Position(Number(xj, "XJ"), Number(yj, "YJ"), Number(zj, "ZJ"));
        ViewModel.DrawMember(start, startJoint, end, endJoint, cable.IsChecked == true, section.SelectedItem as string);
    });

    internal void SetEnds(Position start, Position end, bool asCable = false)
    {
        (xi.Text, yi.Text, zi.Text) = (Metres(start.X), Metres(start.Y), Metres(start.Z));
        (xj.Text, yj.Text, zj.Text) = (Metres(end.X), Metres(end.Y), Metres(end.Z));
        (cable.IsChecked, frame.IsChecked) = (asCable, !asCable);
    }
}

/// <summary>Edit → Move… (geometry editing plan, P3): the selection by DX, DY, DZ — each point once, the members on the
/// points following. A move that puts a point on another or collapses a member is refused, with the reason.</summary>
public sealed class MoveSelectionWindow : GeometryForm
{
    private readonly TextBox dx = Field("DX", "0"), dy = Field("DY", "0"), dz = Field("DZ", "0");
    private readonly IReadOnlyCollection<string> joints, members;

    internal MoveSelectionWindow(MainViewModel viewModel, IReadOnlyCollection<string> joints, IReadOnlyCollection<string> members) : base(viewModel, "Move")
    {
        (this.joints, this.members) = (joints, members);
        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(new TextBlock { Text = $"{joints.Count} point{(joints.Count == 1 ? "" : "s")} and {members.Count} member{(members.Count == 1 ? "" : "s")} selected: their points move once; the members on them follow.", TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        root.Children.Add(Row(Caption("DX (m)"), dx, Caption("DY (m)"), dy, Caption("DZ (m)"), dz));
        root.Children.Add(Note);
        root.Children.Add(Buttons(("OK", true, false, () => { if (Apply()) Close(); }), ("Cancel", false, true, Close)));
        Content = root;
    }

    internal bool Apply() => Try(() => ViewModel.MoveObjects(joints, members, new(Number(dx, "DX"), Number(dy, "DY"), Number(dz, "DZ"))));

    internal void SetOffset(double x, double y, double z) => (dx.Text, dy.Text, dz.Text) = (Metres(x), Metres(y), Metres(z));
}

/// <summary>Draw → Generate Points at Grid Intersections… (geometry editing plan, P2): a point at every crossing of the
/// visible lines of a grid system, at the levels ticked — positions that have a point already are left out, and the form
/// says how many are new before anything is made. One Undo step. Defining a grid alone never makes a point.</summary>
public sealed class GenerateGridPointsWindow : GeometryForm
{
    private readonly ComboBox system = new() { Width = 200, DisplayMemberPath = nameof(Native.GridSystem.Name) };
    private readonly StackPanel levels = new();
    private readonly TextBlock count = new() { Margin = new Thickness(0, 6, 0, 0) };

    internal GenerateGridPointsWindow(MainViewModel viewModel) : base(viewModel, "Generate Points at Grid Intersections")
    {
        var reference = viewModel.SharedViewerReference;
        system.ItemsSource = reference.GridSystems;
        system.SelectedIndex = reference.GridSystems.IsEmpty ? -1 : 0;
        system.SelectionChanged += (_, _) => Count();
        System.Windows.Automation.AutomationProperties.SetName(system, "Grid system");
        foreach (var level in reference.LevelsByElevation)
        {
            var box = new CheckBox { Content = $"{level.Name}  (Z = {ViewPlanes.Metres(level.Elevation)} m)", Tag = level.Elevation, IsChecked = false, Margin = new Thickness(0, 2, 0, 2) };
            box.Checked += (_, _) => Count();
            box.Unchecked += (_, _) => Count();
            levels.Children.Add(box);
        }
        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(Row(Caption("Grid system"), system));
        root.Children.Add(new TextBlock { Text = levels.Children.Count > 0 ? "At the levels" : "No level is defined: the points are made at the grid system's own elevation (its origin Z).", Margin = new Thickness(0, 6, 0, 2), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        root.Children.Add(new ScrollViewer { Content = levels, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        root.Children.Add(count);
        root.Children.Add(Note);
        root.Children.Add(Buttons(("Generate", true, false, () => { if (Generate()) Close(); }), ("Cancel", false, true, Close)));
        Content = root;
        if (reference.GridSystems.IsEmpty) Say("Define a grid system first (Define → Grid Systems…).", error: true);
        Count();
    }

    internal void Tick(params string[] levelNames)
    {
        foreach (var box in levels.Children.OfType<CheckBox>())
            box.IsChecked = levelNames.Any(name => (box.Content as string)?.StartsWith(name + "  ", StringComparison.Ordinal) == true);
    }

    private IReadOnlyList<Position> Positions()
    {
        if (system.SelectedItem is not Native.GridSystem grid) return [];
        var elevations = levels.Children.OfType<CheckBox>().Where(box => box.IsChecked == true).Select(box => (double)box.Tag).ToArray();
        if (levels.Children.Count > 0 && elevations.Length == 0) return [];
        return GeometryEditService.GridIntersections(grid, levels.Children.Count == 0 ? null : elevations).Select(p => new Position(p.X, p.Y, p.Z)).ToArray();
    }

    private void Count()
    {
        var positions = Positions();
        var (_, made) = GeometryEditService.AddPoints(ViewModel.NativeDocumentSession.Document, positions.Select(p => new Native.Xyz(p.X, p.Y, p.Z)));
        count.Text = positions.Count == 0 ? "Tick the levels to make points at." : $"{positions.Count} crossings · {made.Length} new point{(made.Length == 1 ? "" : "s")} · {positions.Count - made.Length} already have one.";
    }

    internal bool Generate() => Try(() =>
    {
        var positions = Positions();
        if (positions.Count == 0) throw new InvalidOperationException("Choose a grid system and tick at least one level.");
        ViewModel.GeneratePoints(positions);
    });
}
