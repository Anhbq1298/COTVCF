using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Presentation;

/// <summary>Assign → Frame → Frame Load: SAP2000's Frame Distributed Loads form, on the frames selected in the model view
/// (docs/loads/FRAME_DISTRIBUTED_LOAD_PLAN.md, FL-04). Load pattern; forces or moments; GLOBAL or Local and a direction
/// (projected directions per unit projected length); four trapezoid points, relative or absolute from End-I; a uniform
/// load; Add / Replace / Delete existing loads of the pattern. The form refuses only what it cannot read: whether the
/// active degrees of freedom can carry the load is the run's to say.</summary>
public sealed class AssignFrameLoadWindow : Window
{
    private const string Global = "GLOBAL", Local = "Local";

    private static readonly (string Label, Native.LoadDirection Direction, bool Projected)[] GlobalDirections =
    [
        ("X", Native.LoadDirection.GlobalX, false), ("Y", Native.LoadDirection.GlobalY, false), ("Z", Native.LoadDirection.GlobalZ, false),
        ("X Projected", Native.LoadDirection.GlobalX, true), ("Y Projected", Native.LoadDirection.GlobalY, true),
        ("Z Projected", Native.LoadDirection.GlobalZ, true),
        ("Gravity", Native.LoadDirection.Gravity, false), ("Gravity Projected", Native.LoadDirection.Gravity, true),
    ];
    private static readonly (string Label, Native.LoadDirection Direction, bool Projected)[] LocalDirections =
    [
        ("1", Native.LoadDirection.Local1, false), ("2", Native.LoadDirection.Local2, false), ("3", Native.LoadDirection.Local3, false),
    ];

    private readonly MainViewModel viewModel;
    private readonly IReadOnlyList<string> frames;
    private readonly ComboBox patternBox = new() { MinWidth = 180 };
    private readonly RadioButton forces = new() { Content = "Forces", IsChecked = true, GroupName = "LoadType", Margin = new Thickness(0, 0, 16, 0) };
    private readonly RadioButton moments = new() { Content = "Moments", GroupName = "LoadType" };
    private readonly ComboBox coordinateBox = new() { MinWidth = 120 };
    private readonly ComboBox directionBox = new() { MinWidth = 150 };
    private readonly TextBox[] distanceBoxes = Enumerable.Range(0, 4).Select(_ => Number()).ToArray();
    private readonly TextBox[] loadBoxes = Enumerable.Range(0, 4).Select(_ => Number()).ToArray();
    private readonly RadioButton relative = new() { Content = "Relative Distance from End-I", IsChecked = true, GroupName = "Distance", Margin = new Thickness(0, 0, 16, 0) };
    private readonly RadioButton absolute = new() { Content = "Absolute Distance from End-I", GroupName = "Distance" };
    private readonly TextBox uniformBox = Number();
    private readonly RadioButton add = new() { Content = "Add to Existing Loads", GroupName = "Options" };
    private readonly RadioButton replace = new() { Content = "Replace Existing Loads", IsChecked = true, GroupName = "Options" };
    private readonly RadioButton delete = new() { Content = "Delete Existing Loads", GroupName = "Options" };
    private readonly TextBlock distanceUnit = Unit(""), uniformUnit = Unit("kN/m");
    private readonly TextBlock loadUnit = Unit("kN/m");
    private readonly TextBlock status = new() { Foreground = Brushes.Firebrick, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly List<FrameworkElement> valueControls = [];

    public AssignFrameLoadWindow(MainViewModel viewModel, IReadOnlyList<string> frames)
    {
        this.viewModel = viewModel;
        this.frames = frames;
        Title = "Frame Load Assignment - Distributed";
        Icon = AppIcon.Window;
        Width = 640; Height = 560; MinWidth = 560; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)FindResource("Surface");

        foreach (var name in viewModel.FrameLoadPatternNames) patternBox.Items.Add(name);
        patternBox.SelectedIndex = patternBox.Items.Count > 0 ? 0 : -1;
        coordinateBox.Items.Add(Global);
        coordinateBox.Items.Add(Local);
        coordinateBox.SelectionChanged += (_, _) => FillDirections();
        coordinateBox.SelectedIndex = 0;
        directionBox.SelectedIndex = Array.FindIndex(GlobalDirections, item => item.Label == "Gravity");
        double[] distances = [0, .25, .75, 1];
        for (int i = 0; i < 4; i++)
        {
            distanceBoxes[i].Text = distances[i].ToString("0.###", CultureInfo.CurrentCulture);
            loadBoxes[i].Text = "0";
        }
        uniformBox.Text = "0";
        forces.Checked += (_, _) => UpdateUnits();
        moments.Checked += (_, _) => UpdateUnits();
        relative.Checked += (_, _) => UpdateUnits();
        absolute.Checked += (_, _) => UpdateUnits();
        foreach (var option in new[] { add, replace, delete }) option.Checked += (_, _) => UpdateEnabled();
        System.Windows.Automation.AutomationProperties.SetName(patternBox, "Load pattern");
        System.Windows.Automation.AutomationProperties.SetName(coordinateBox, "Coordinate system");
        System.Windows.Automation.AutomationProperties.SetName(directionBox, "Direction of load application");
        System.Windows.Automation.AutomationProperties.SetName(uniformBox, "Uniform load");

        var content = new StackPanel { Margin = new Thickness(14) };
        content.Children.Add(new TextBlock
        {
            Text = $"{frames.Count} frame{(frames.Count == 1 ? "" : "s")} selected: {string.Join(", ", frames.Take(8))}{(frames.Count > 8 ? ", …" : "")}",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });
        content.Children.Add(Row("Load Pattern Name", patternBox));

        var typeAndDirection = new StackPanel();
        var typeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        typeRow.Children.Add(forces);
        typeRow.Children.Add(moments);
        typeAndDirection.Children.Add(typeRow);
        typeAndDirection.Children.Add(Row("Coordinate System", coordinateBox));
        typeAndDirection.Children.Add(Row("Direction", directionBox));
        var options = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        foreach (var option in new[] { add, replace, delete }) { option.Margin = new Thickness(0, 0, 0, 4); options.Children.Add(option); }
        var top = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        var typeGroup = new GroupBox { Header = "Load Type and Direction", Content = typeAndDirection, Padding = new Thickness(8) };
        var optionGroup = new GroupBox { Header = "Options", Content = options, Padding = new Thickness(8), Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(optionGroup, 1);
        top.Children.Add(typeGroup);
        top.Children.Add(optionGroup);
        content.Children.Add(top);

        var trapezoid = new Grid();
        trapezoid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        for (int i = 0; i < 4; i++) trapezoid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        trapezoid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        for (int r = 0; r < 3; r++) trapezoid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(trapezoid, new TextBlock { Text = "Distance", VerticalAlignment = VerticalAlignment.Center }, 1, 0);
        Place(trapezoid, new TextBlock { Text = "Load", VerticalAlignment = VerticalAlignment.Center }, 2, 0);
        for (int i = 0; i < 4; i++)
        {
            Place(trapezoid, new TextBlock { Text = $"{i + 1}.", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) }, 0, i + 1);
            Place(trapezoid, distanceBoxes[i], 1, i + 1);
            Place(trapezoid, loadBoxes[i], 2, i + 1);
            System.Windows.Automation.AutomationProperties.SetName(distanceBoxes[i], $"Trapezoid point {i + 1} distance");
            System.Windows.Automation.AutomationProperties.SetName(loadBoxes[i], $"Trapezoid point {i + 1} load");
            valueControls.Add(distanceBoxes[i]);
            valueControls.Add(loadBoxes[i]);
        }
        Place(trapezoid, distanceUnit, 1, 5);
        Place(trapezoid, loadUnit, 2, 5);
        var distanceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        distanceRow.Children.Add(relative);
        distanceRow.Children.Add(absolute);
        var trapezoidPanel = new StackPanel();
        trapezoidPanel.Children.Add(trapezoid);
        trapezoidPanel.Children.Add(distanceRow);
        content.Children.Add(new GroupBox { Header = "Trapezoidal Loads", Content = trapezoidPanel, Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0) });

        var uniformRow = new StackPanel { Orientation = Orientation.Horizontal };
        uniformRow.Children.Add(new TextBlock { Text = "Load", Width = 70, VerticalAlignment = VerticalAlignment.Center });
        uniformBox.Width = 110;
        uniformRow.Children.Add(uniformBox);
        uniformRow.Children.Add(uniformUnit);
        content.Children.Add(new GroupBox { Header = "Uniform Load", Content = uniformRow, Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0) });
        valueControls.AddRange([forces, moments, coordinateBox, directionBox, relative, absolute, uniformBox]);
        content.Children.Add(new TextBlock
        {
            Text = "Distances from End-I; the load is linear between points and zero outside them. Projected loads are per unit length " +
                   "projected on the plane normal to the load; Gravity acts along −Z, positive down.",
            Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0),
        });
        content.Children.Add(status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => { if (Apply()) Close(); };
        var close = new Button { Content = "Close", MinWidth = 80, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "Apply", MinWidth = 80 };
        apply.Click += (_, _) => Apply();
        buttons.Children.Add(ok); buttons.Children.Add(close); buttons.Children.Add(apply);

        var root = new Grid { Margin = new Thickness(10) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);
        Content = root;
        UpdateUnits();
        UpdateEnabled();
    }

    private (string Label, Native.LoadDirection Direction, bool Projected)[] Directions =>
        Equals(coordinateBox.SelectedItem, Local) ? LocalDirections : GlobalDirections;

    private void FillDirections()
    {
        var previous = directionBox.SelectedItem as string;
        directionBox.Items.Clear();
        foreach (var item in Directions) directionBox.Items.Add(item.Label);
        var index = Array.FindIndex(Directions, item => item.Label == previous);
        directionBox.SelectedIndex = index >= 0 ? index : Equals(coordinateBox.SelectedItem, Local) ? 1 : Array.FindIndex(GlobalDirections, item => item.Label == "Gravity");
    }

    private void UpdateUnits()
    {
        var per = moments.IsChecked == true ? "kN·m/m" : "kN/m";
        loadUnit.Text = per;
        uniformUnit.Text = per;
        distanceUnit.Text = absolute.IsChecked == true ? "m" : "";
        status.Text = "";
    }

    private void UpdateEnabled()
    {
        // Delete removes the pattern's loads: nothing else of the form is read
        foreach (var control in valueControls) control.IsEnabled = delete.IsChecked != true;
        status.Text = "";
    }

    private bool Apply()
    {
        if (patternBox.SelectedItem is not string pattern)
        {
            status.Text = "Define a load pattern first (Define → Load Patterns).";
            return false;
        }
        var mode = add.IsChecked == true ? Native.LoadAssignMode.Add : delete.IsChecked == true ? Native.LoadAssignMode.Delete : Native.LoadAssignMode.Replace;
        var directions = Directions;
        var chosen = directionBox.SelectedIndex >= 0 && directionBox.SelectedIndex < directions.Length ? directions[directionBox.SelectedIndex] : directions[0];
        var points = new List<Native.FrameLoadPoint>();
        double uniform = 0;
        if (mode != Native.LoadAssignMode.Delete)
        {
            for (int i = 0; i < 4; i++)
            {
                if (!Read(distanceBoxes[i], $"the distance of point {i + 1}", out var distance) || !Read(loadBoxes[i], $"the load of point {i + 1}", out var value)) return false;
                points.Add(new(distance, value));
            }
            if (!Read(uniformBox, "the uniform load", out uniform)) return false;
        }
        try
        {
            viewModel.AssignFrameDistributedLoad(frames, pattern, moments.IsChecked == true ? Native.FrameLoadType.Moment : Native.FrameLoadType.Force,
                chosen.Direction, chosen.Projected, absolute.IsChecked == true ? Native.LoadDistanceType.Absolute : Native.LoadDistanceType.Relative,
                points, uniform, mode);
            status.Foreground = (Brush)FindResource("Muted");
            status.Text = viewModel.Message;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Native.DocumentException)
        {
            status.Foreground = Brushes.Firebrick;
            status.Text = ex.Message;
            return false;
        }
    }

    private bool Read(TextBox box, string what, out double value)
    {
        if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value)) return true;
        status.Foreground = Brushes.Firebrick;
        status.Text = $"Enter a number for {what}.";
        box.Focus();
        box.SelectAll();
        return false;
    }

    private static TextBox Number() => new() { TextAlignment = TextAlignment.Right, Margin = new Thickness(2), MinWidth = 70 };

    private static TextBlock Unit(string text) => new() { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    private static StackPanel Row(string label, FrameworkElement control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(new TextBlock { Text = label, Width = 130, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(control);
        return row;
    }

    private static void Place(Grid grid, UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}
