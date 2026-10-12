using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Mcotis.Desktop.Presentation;

/// <summary>A compact multi-select (Optimization VCF and Optimization TAS candidate sections): a drop-down button naming the ticked sections, with a check list in its popup.</summary>
internal sealed class CandidatePicker : Grid
{
    private readonly List<CheckBox> boxes = [];
    private readonly ToggleButton button;
    public ToggleButton Button => button;

    public CandidatePicker(IReadOnlyList<string> names, IReadOnlyList<string> selected, Action changed)
    {
        Margin = new Thickness(0, 2, 6, 2);
        IsEnabled = names.Count > 0;
        button = new ToggleButton { HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 0, 6, 0), MinHeight = 24 };
        var list = new StackPanel { Margin = new Thickness(6) };
        foreach (var name in names.Concat(selected.Except(names, StringComparer.Ordinal)))
        {
            var box = new CheckBox { Content = new TextBlock { Text = name }, Tag = name, IsChecked = selected.Contains(name, StringComparer.Ordinal), Margin = new Thickness(0, 2, 0, 2) };
            box.Checked += (_, _) => { Caption(); changed(); };
            box.Unchecked += (_, _) => { Caption(); changed(); };
            boxes.Add(box);
            list.Children.Add(box);
        }
        var popup = new Popup
        {
            PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = false,
            Child = new Border
            {
                Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("Raised"),
                BorderBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("Border"),
                BorderThickness = new Thickness(1),
                Child = new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            },
        };
        popup.SetBinding(Popup.IsOpenProperty, new System.Windows.Data.Binding(nameof(ToggleButton.IsChecked)) { Source = button, Mode = System.Windows.Data.BindingMode.TwoWay });
        popup.Opened += (_, _) => popup.MinWidth = button.ActualWidth;
        Children.Add(button);
        Children.Add(popup);
        Caption();
    }

    public IReadOnlyList<string> Selected => boxes.Where(box => box.IsChecked == true).Select(box => (string)box.Tag).ToArray();

    private void Caption()
    {
        var selected = Selected;
        button.Content = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, Text = selected.Count switch
        {
            0 => "(keep as assigned)",
            <= 3 => $"({selected.Count}) " + string.Join(", ", selected),
            _ => $"({selected.Count}) {string.Join(", ", selected.Take(2))}, …",
        } };
    }
}
