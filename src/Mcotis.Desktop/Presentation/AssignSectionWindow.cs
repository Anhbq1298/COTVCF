using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using COT_VCF.Domain;

namespace COT_VCF.Presentation;

/// <summary>
/// Assign → Frame → Frame Sections / Cable → Cable Sections, laid out like SAP2000's "Assign Frame Sections": Find this
/// property and the section list (the one the selection shares is preselected), Define Sections… (the Define → Section
/// Properties list, to add or modify a section), and OK / Close / Apply. Apply and OK assign the chosen section to the members
/// selected on the model view.
/// </summary>
public sealed class AssignSectionWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly IReadOnlyList<string> members;
    private readonly bool cable;
    private readonly ListBox list = new() { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
    private readonly TextBox find = new() { Margin = new Thickness(0, 2, 0, 4) };
    private readonly TextBlock status = new() { Foreground = Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    /// <summary>Members changed by Apply / OK so far.</summary>
    internal int Assigned { get; private set; }

    public AssignSectionWindow(MainViewModel viewModel, IReadOnlyList<string> members, bool cable)
    {
        this.viewModel = viewModel;
        this.members = members;
        this.cable = cable;
        viewModel.EnsureMaterials();
        viewModel.EnsureSections();
        Title = cable ? "Assign Cable Sections" : "Assign Frame Sections";
        Icon = AppIcon.Window;
        Width = 560; Height = 440; MinWidth = 480; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = (Brush)FindResource("Surface");
        System.Windows.Automation.AutomationProperties.SetName(list, "Sections");
        System.Windows.Automation.AutomationProperties.SetName(find, "Find this property");
        find.TextChanged += (_, _) => Load(find.Text.Trim());
        list.MouseDoubleClick += (_, _) => { if (Apply()) Close(); };

        Button Action(string text, RoutedEventHandler click)
        {
            var button = new Button { Content = text, Width = 190, Margin = new Thickness(0, 0, 0, 8) };
            button.Click += click;
            return button;
        }
        var define = Action(cable ? "Define Cable Sections..." : "Define Frame Sections...", (_, _) =>
        {
            var selected = list.SelectedItem as string;
            new SectionListWindow(viewModel, cable ? SectionListKind.Cable : SectionListKind.Frame) { Owner = this }.ShowDialog();
            Load(find.Text.Trim(), selected);
        });
        define.ToolTip = "Add a new section or modify one (Define → Section Properties); the list here follows";

        var properties = new GroupBox { Header = "Properties", Padding = new Thickness(8), BorderBrush = Brushes.Gray, Foreground = (Brush)FindResource("Accent"),
            Content = new DockPanel { Children = { Docked(new StackPanel { Children = { new TextBlock { Text = "Find this property:", Foreground = Brushes.Black }, find } }, Dock.Top), list } } };
        var selection = new TextBlock { Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap,
            Text = $"{members.Count} {(cable ? "cable" : "frame")}{(members.Count == 1 ? "" : "s")} selected:\n" + string.Join(", ", members.Take(12)) + (members.Count > 12 ? $", … (+{members.Count - 12})" : "") };
        var actions = new GroupBox { Header = "Click to:", Padding = new Thickness(10), BorderBrush = Brushes.Gray, Foreground = (Brush)FindResource("Accent"),
            Content = new StackPanel { Children = { define, new Separator { Margin = new Thickness(0, 4, 0, 8) }, selection, status } } };

        var grid = new Grid { Margin = new Thickness(10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(actions, 2);
        grid.Children.Add(properties); grid.Children.Add(actions);
        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, Margin = new Thickness(0, 10, 8, 0) };
        ok.Click += (_, _) => { if (Apply()) Close(); };
        var close = new Button { Content = "Close", MinWidth = 80, IsCancel = true, Margin = new Thickness(0, 10, 8, 0) };
        close.Click += (_, _) => Close();
        var apply = new Button { Content = "Apply", MinWidth = 80, Margin = new Thickness(0, 10, 0, 0) };
        apply.Click += (_, _) => Apply();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { ok, close, apply } };
        Grid.SetRow(buttons, 1); Grid.SetColumnSpan(buttons, 3);
        grid.Children.Add(buttons);
        Content = grid;

        // preselect the section the selection shares (or the first one's)
        var current = viewModel.FemSectionProperties.Where(property => members.Contains(property.MemberName)).Select(property => property.SectionName).FirstOrDefault();
        Load("", current);
    }

    private static UIElement Docked(UIElement element, Dock dock) { DockPanel.SetDock(element, dock); return element; }

    private IReadOnlyList<string> Names() => cable
        ? viewModel.CableSections.Select(section => section.Name).ToArray()
        : viewModel.FrameSections.Select(section => section.Name).ToArray();

    private void Load(string filter, string? select = null)
    {
        var keep = select ?? list.SelectedItem as string;
        var names = Names().Where(name => filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        list.ItemsSource = names;
        list.SelectedItem = names.Contains(keep) ? keep : names.FirstOrDefault(name => name.Equals(filter, StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault();
    }

    /// <summary>Chooses <paramref name="section"/> as a click would (checks).</summary>
    internal void Choose(string section) => list.SelectedItem = section;

    /// <summary>Assigns the chosen section to the selected members; false (with a message) when nothing is chosen.</summary>
    internal bool Apply()
    {
        if (list.SelectedItem is not string section) { status.Text = "Choose a section in the list."; return false; }
        try
        {
            var changed = viewModel.AssignSection(members, section, cable);
            Assigned += changed;
            status.Text = changed > 0 ? $"{section} assigned to {changed} member{(changed == 1 ? "" : "s")}." : $"The selection already has {section}.";
            return true;
        }
        catch (Exception ex) { status.Text = ex.Message; return false; }
    }
}
