using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Mcotis.Desktop.Presentation;

/// <summary>Builders for the SAP2000-style property forms (Define → Section Properties): labelled rows with a value and a
/// unit, read-only values, a colour swatch, and the small Notes / Modifiers / Properties dialogs.</summary>
internal static class PropertyForm
{
    public static string Number(double? value) => value is { } v ? v.ToString("0.##########", CultureInfo.CurrentCulture) : "";

    public static double? Parse(TextBox box) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value) ? value : null;

    public static GroupBox Group(Panel host, string title)
    {
        var box = new GroupBox { Header = title, Content = new StackPanel(), Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(8, 6, 8, 6),
            BorderBrush = (Brush)System.Windows.Application.Current.FindResource("Border"), Foreground = (Brush)System.Windows.Application.Current.FindResource("Accent"),
            Background = (Brush)System.Windows.Application.Current.FindResource("Raised") };
        host.Children.Add(box);
        return box;
    }

    public static void Row(GroupBox group, string label, UIElement editor, string? unit = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(unit is null ? 0 : 36) });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) });
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

    public static TextBox Field(GroupBox group, IDictionary<string, TextBox> fields, string key, string label, double? value, string unit)
    {
        var box = new TextBox { Text = Number(value), Width = 100, TextAlignment = TextAlignment.Right };
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        fields[key] = box;
        Row(group, label, box, unit);
        return box;
    }

    public static TextBlock Display(GroupBox group, string label, string value, string unit = "")
    {
        var text = new TextBlock { Text = value, Width = 100, TextAlignment = TextAlignment.Right, Padding = new Thickness(4, 3, 4, 3),
            Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xEF, 0xF1)), Foreground = Brushes.DimGray, TextTrimming = TextTrimming.CharacterEllipsis };
        Row(group, label, text, unit);
        return text;
    }

    public static Color Rgb(int value) => Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);

    /// <summary>A display-colour swatch; clicking it opens a small palette.</summary>
    public static Border Swatch(int color, Action<int> changed)
    {
        var swatch = new Border { Width = 22, Height = 22, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Background = new SolidColorBrush(Rgb(color)),
            Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Display colour — click to change", VerticalAlignment = VerticalAlignment.Center };
        swatch.MouseLeftButtonUp += (_, _) =>
        {
            var palette = new[] { 0xFF0000, 0xFFA500, 0xFFFF00, 0x00FF00, 0x008000, 0x00FFFF, 0x0000FF, 0x800080, 0xFF00FF, 0x808080, 0xC0C0C0, 0x000000 };
            var grid = new UniformGrid { Columns = 6, Margin = new Thickness(4) };
            var popup = new Popup { PlacementTarget = swatch, Placement = PlacementMode.Bottom, StaysOpen = false,
                Child = new Border { Background = Brushes.White, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = grid } };
            foreach (var value in palette)
            {
                var button = new Button { Width = 22, Height = 22, Margin = new Thickness(2), Background = new SolidColorBrush(Rgb(value)), MinHeight = 0 };
                button.Click += (_, _) => { swatch.Background = new SolidColorBrush(Rgb(value)); changed(value); popup.IsOpen = false; };
                grid.Children.Add(button);
            }
            popup.IsOpen = true;
        };
        return swatch;
    }

    /// <summary>A small OK / Cancel dialog; OK closes it when <paramref name="accept"/> (if any) says the input is good.</summary>
    private static Window Dialog(Window owner, string title, UIElement body, out Button ok, Func<Window, bool>? accept = null)
    {
        ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, Margin = new Thickness(0, 10, 8, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true, Margin = new Thickness(0, 10, 0, 0) };
        var dialog = new Window { Title = title, Owner = owner, Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Icon = AppIcon.Window,
            Background = (Brush)System.Windows.Application.Current.FindResource("Surface"),
            Content = new StackPanel { Margin = new Thickness(10), Children = { body, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } } } } };
        var self = dialog;
        ok.Click += (_, _) => { if (accept?.Invoke(self) ?? true) self.DialogResult = true; };
        return dialog;
    }

    public static string? EditNotes(Window owner, string title, string notes)
    {
        var box = new TextBox { Text = notes, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return Dialog(owner, title, box, out _).ShowDialog() == true ? box.Text : null;
    }

    /// <summary>Property / stiffness modifiers dialog; returns the edited values or null.</summary>
    public static double[]? EditModifiers(Window owner, string title, IReadOnlyList<string> labels, IReadOnlyList<double> values)
    {
        var panel = new StackPanel();
        var group = Group(panel, "Property/Stiffness Modifiers for Analysis");
        var fields = new Dictionary<string, TextBox>();
        for (var index = 0; index < labels.Count; index++) Field(group, fields, index.ToString(CultureInfo.InvariantCulture), labels[index], index < values.Count ? values[index] : 1, "");
        double[]? result = null;
        var dialog = Dialog(owner, title, panel, out _, self =>
        {
            var parsed = fields.OrderBy(pair => int.Parse(pair.Key, CultureInfo.InvariantCulture)).Select(pair => Parse(pair.Value)).ToArray();
            if (parsed.Any(value => value is not (>= 0))) { MessageBox.Show(self, "Each modifier must be a number of zero or more.", title); return false; }
            result = parsed.Select(value => value!.Value).ToArray();
            return true;
        });
        return dialog.ShowDialog() == true ? result : null;
    }

    public static void ShowTable(Window owner, string title, IReadOnlyList<(string Label, string Value, string Unit)> rows)
    {
        var panel = new StackPanel();
        var group = Group(panel, "Properties");
        foreach (var (label, value, unit) in rows) Display(group, label, value, unit);
        Dialog(owner, title, panel, out _).ShowDialog();
    }

    /// <summary>Picks one of <paramref name="items"/> from a list (e.g. the shape for Add New Property).</summary>
    public static T? Pick<T>(Window owner, string title, string prompt, IReadOnlyList<T> items, Func<T, string> label) where T : struct
    {
        var list = new ListBox { Height = 200, ItemsSource = items.Select(label).ToArray(), SelectedIndex = 0 };
        var panel = new StackPanel { Children = { new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) }, list } };
        var dialog = Dialog(owner, title, panel, out _);
        list.MouseDoubleClick += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true && list.SelectedIndex >= 0 ? items[list.SelectedIndex] : null;
    }
}
