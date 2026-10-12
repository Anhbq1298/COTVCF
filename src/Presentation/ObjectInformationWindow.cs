using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using COT_VCF.Domain;

namespace COT_VCF.Presentation;

/// <summary>
/// Frame / Cable / Point Information, as ETABS shows it for a right-clicked object: tabs Geometry, Assignments and Loads,
/// each a grid of name / value lines under section bands (<see cref="MainViewModel.MemberInformation"/>,
/// <see cref="MainViewModel.PointInformation"/>). Read-only — the values can be selected and copied; for a member, Modify
/// Assignments opens <see cref="MemberAssignmentWindow"/> and the form shows the result.
/// </summary>
public sealed class ObjectInformationWindow : Window
{
    private readonly Func<IReadOnlyList<InformationTab>> information;
    private readonly TabControl tabs = new();

    /// <param name="shown">The geometry the member was picked in (the view's model); the model's own when omitted.</param>
    public ObjectInformationWindow(MainViewModel viewModel, StructuralMember member, GeometryModel? shown = null)
        : this(viewModel, $"{(member.Kind == MemberKind.Frame ? "Frame" : member.Kind.ToString())} Information - {member.Name}",
            () => viewModel.MemberInformation(member, shown), member) { }

    /// <param name="shown">The geometry the point was picked in (the view's model); the model's own when omitted.</param>
    public ObjectInformationWindow(MainViewModel viewModel, StructuralPoint point, GeometryModel? shown = null)
        : this(viewModel, $"Point Information - {point.Name}", () => viewModel.PointInformation(point, shown), null) { }

    private ObjectInformationWindow(MainViewModel viewModel, string title, Func<IReadOnlyList<InformationTab>> information, StructuralMember? member)
    {
        this.information = information;
        Title = title;
        Icon = AppIcon.Window;
        Width = 470; Height = 640; MinWidth = 400; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)FindResource("Surface");

        // compact tab headers, as on the other property forms (the theme's are sized for the main workflow)
        var header = new Style(typeof(TabItem), (Style)FindResource(typeof(TabItem)));
        header.Setters.Add(new Setter(HeightProperty, 26.0));
        header.Setters.Add(new Setter(WidthProperty, double.NaN));
        header.Setters.Add(new Setter(MinWidthProperty, 0.0));
        header.Setters.Add(new Setter(PaddingProperty, new Thickness(12, 0, 12, 0)));
        tabs.Resources.Add(typeof(TabItem), header);
        tabs.Background = Background;
        System.Windows.Automation.AutomationProperties.SetName(tabs, "Object information");
        Fill();

        var ok = new Button { Content = "OK", MinWidth = 84, IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => Close();
        var buttons = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        if (member is not null)   // a point has no assignment form of its own: its support is set under Assign → Joint
        {
            var modify = new Button { Content = "Modify Assignments…", MinWidth = 150, HorizontalAlignment = HorizontalAlignment.Left,
                IsEnabled = viewModel.FemSectionProperties.Any(item => item.MemberName == member.Name),
                ToolTip = "Change the section, local axis, end releases or cable target force of this member" };
            modify.Click += (_, _) =>
            {
                new MemberAssignmentWindow(viewModel, member) { Owner = this }.ShowDialog();
                Fill();
            };
            buttons.Children.Add(modify);
        }
        buttons.Children.Add(ok);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(tabs);
        Content = root;
    }

    /// <summary>The tabs as shown (checks).</summary>
    internal IReadOnlyList<InformationTab> Shown { get; private set; } = [];

    /// <summary>Builds the tabs from the model as it is now, keeping the tab that was open.</summary>
    private void Fill()
    {
        var open = Math.Max(0, tabs.SelectedIndex);
        Shown = information();
        tabs.Items.Clear();
        foreach (var tab in Shown) tabs.Items.Add(new TabItem { Header = tab.Title, Content = Sheet(tab) });
        tabs.SelectedIndex = Math.Min(open, tabs.Items.Count - 1);
    }

    private ScrollViewer Sheet(InformationTab tab)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.46, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.54, GridUnitType.Star) });
        var line = (Brush)FindResource("Border");
        var band = (Brush)FindResource("Background");
        var muted = (Brush)FindResource("Muted");
        void Place(UIElement element, int column, int span = 1)
        {
            Grid.SetRow(element, grid.RowDefinitions.Count - 1);
            Grid.SetColumn(element, column);
            Grid.SetColumnSpan(element, span);
            grid.Children.Add(element);
        }
        foreach (var section in tab.Sections)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Place(new Border { Background = band, BorderBrush = line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(6, 4, 6, 4),
                Child = new TextBlock { Text = section.Title, FontWeight = FontWeights.SemiBold } }, 0, 2);
            foreach (var row in section.Rows)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = 24 });
                Place(new Border { BorderBrush = line, BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(14, 3, 6, 3),
                    Child = new TextBlock { Text = row.Name, Foreground = muted, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap } }, 0);
                // a read-only box, not a label: the value can be selected and copied
                var value = new TextBox { Text = row.Value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, MinHeight = 0,
                    Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, IsTabStop = false };
                System.Windows.Automation.AutomationProperties.SetName(value, $"{section.Title} {row.Name}");
                Place(new Border { BorderBrush = line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(6, 3, 6, 3), Child = value }, 1);
            }
        }
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new Border { BorderBrush = line, BorderThickness = new Thickness(1, 1, 1, 0),
            Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Top, Child = grid } };
    }
}
