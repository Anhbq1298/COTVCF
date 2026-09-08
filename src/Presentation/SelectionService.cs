using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace COT_VCF.Presentation;

public sealed class SelectionService : ISelectionService
{
    public IReadOnlyList<string>? Select(string title, IReadOnlyList<string> available, IReadOnlyList<string> selected)
    {
        var rows = new ObservableCollection<SelectionRow>(available.Select(n => new SelectionRow(n)));
        var view = CollectionViewSource.GetDefaultView(rows);
        var owner = System.Windows.Application.Current.MainWindow;
        var dialog = new Window { Title = title, Width = 520, Height = 600, MinWidth = 420, MinHeight = 400, WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner };
        if (owner is not null && owner != dialog) dialog.Owner = owner;
        var panel = new DockPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = title, Style = (Style)dialog.FindResource("Heading"), Margin = new Thickness(0, 0, 0, 16) }; DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 8), ToolTip = "Search object names" }; System.Windows.Automation.AutomationProperties.SetName(search, "Search object names");
        search.TextChanged += (_, _) =>
        {
            view.Filter = o => ((SelectionRow)o).Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase);
        };
        DockPanel.SetDock(search, Dock.Top); panel.Children.Add(search);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        var grid = new DataGrid
        {
            ItemsSource = view,
            CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            IsReadOnly = true
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "SAP object", Binding = new Binding(nameof(SelectionRow.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var all = new Button { Content = "Select all shown" }; all.Click += (_, _) => grid.SelectAll();
        var clear = new Button { Content = "Clear selection" }; clear.Click += (_, _) => grid.UnselectAll();
        toolbar.Children.Add(all); toolbar.Children.Add(clear); DockPanel.SetDock(toolbar, Dock.Top); panel.Children.Add(toolbar);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; var add = new Button { Content = "Add selected", IsDefault = true, IsEnabled = false, Style = (Style)dialog.FindResource("Primary") };
        add.Click += (_, _) => dialog.DialogResult = true; footer.Children.Add(cancel); footer.Children.Add(add); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        grid.SelectionChanged += (_, _) => add.IsEnabled = grid.SelectedItems.Count > 0;
        grid.MouseDoubleClick += (_, _) => { if (grid.SelectedItems.Count > 0) dialog.DialogResult = true; };
        grid.Loaded += (_, _) =>
        {
            foreach (var row in rows.Where(r => selected.Contains(r.Name))) grid.SelectedItems.Add(row);
            search.Focus();
        };
        panel.Children.Add(grid); dialog.Content = panel;
        return dialog.ShowDialog() == true ? grid.SelectedItems.Cast<SelectionRow>().Select(r => r.Name).ToArray() : null;
    }
    private sealed record SelectionRow(string Name);
}
