using COT_VCF.Domain;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace COT_VCF.Presentation;

public interface IOutputSettingsDialogService
{
    bool EditDeflection(OutputSettingsConfiguration settings, GeometryModel baseline, ISelectionService selection);
    bool EditMemberForces(OutputSettingsConfiguration settings, GeometryModel baseline, ISelectionService selection);
    bool EditFoundations(OutputSettingsConfiguration settings, GeometryModel baseline, ISelectionService selection);
    bool EditCables(OutputSettingsConfiguration settings, GeometryModel baseline, IReadOnlyList<string> loadCases, ISelectionService selection);
}

public sealed class OutputSettingsDialogService : IOutputSettingsDialogService
{
    public bool EditDeflection(OutputSettingsConfiguration settings, GeometryModel baseline, ISelectionService selection)
    {
        var available = baseline.Points.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var picked = selection.Select("Deflection Monitoring Points — U3 [mm]", available, settings.DeflectionPoints.ToArray());
        if (picked is null) return false;
        Replace(settings.DeflectionPoints, picked);
        return true;
    }

    public bool EditFoundations(OutputSettingsConfiguration settings, GeometryModel baseline, ISelectionService selection)
    {
        var available = baseline.Points.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var left = selection.Select("Foundation Left — select exactly 2 points", available, settings.FoundationLeftPoints.ToArray());
        if (left is null) return false;
        if (left.Count != 2) { Info("Foundation Left requires exactly 2 points."); return false; }

        var right = selection.Select("Foundation Right — select exactly 2 points", available, settings.FoundationRightPoints.ToArray());
        if (right is null) return false;
        if (right.Count != 2) { Info("Foundation Right requires exactly 2 points."); return false; }
        if (left.Intersect(right, StringComparer.Ordinal).Any())
        {
            Info("A foundation point cannot belong to both Left and Right groups.");
            return false;
        }

        Replace(settings.FoundationLeftPoints, left);
        Replace(settings.FoundationRightPoints, right);
        return true;
    }

    public bool EditCables(OutputSettingsConfiguration settings, GeometryModel baseline, IReadOnlyList<string> loadCases, ISelectionService selection)
    {
        var cables = baseline.Members.Where(m => m.Kind == MemberKind.Cable).Select(m => m.Name)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var selected = selection.Select("Cable Force Monitoring", cables, settings.CableNames.ToArray());
        if (selected is null) return false;
        if (selected.Count == 0) { Info("Select at least one cable."); return false; }

        var owner = System.Windows.Application.Current?.MainWindow;
        var dialog = BasicDialog("Cable Result Load Case", 480, 210);
        if (owner is not null) dialog.Owner = owner;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(Label("Result load case", "Future cable tension output is extracted under this SAP analysis load case."));
        var combo = new ComboBox { ItemsSource = loadCases.OrderBy(x => x, StringComparer.Ordinal).ToArray(), IsEditable = false };
        combo.SelectedItem = loadCases.FirstOrDefault(c => c.Equals(settings.CableResultLoadCase, StringComparison.OrdinalIgnoreCase))
            ?? loadCases.FirstOrDefault(c => c.Equals("PRETENSION", StringComparison.OrdinalIgnoreCase))
            ?? loadCases.FirstOrDefault();
        panel.Children.Add(combo);
        var footer = Footer(dialog, "Save");
        panel.Children.Add(footer);
        dialog.Content = panel;
        if (dialog.ShowDialog() != true) return false;
        if (combo.SelectedItem is not string resultCase || string.IsNullOrWhiteSpace(resultCase))
        {
            Info("Select a result load case.");
            return false;
        }

        Replace(settings.CableNames, selected);
        settings.CableResultLoadCase = resultCase;
        return true;
    }

    public bool EditMemberForces(OutputSettingsConfiguration settings, GeometryModel baseline, ISelectionService selection)
    {
        var rows = new ObservableCollection<MemberGroupRow>(settings.MemberForceGroups.Select(Clone));
        var owner = System.Windows.Application.Current?.MainWindow;
        var dialog = BasicDialog("Member Force Output Groups", 780, 560);
        if (owner is not null) dialog.Owner = owner;

        var root = new DockPanel { Margin = new Thickness(16) };
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        heading.Children.Add(new TextBlock { Text = "MEMBER FORCE OUTPUT GROUPS", FontWeight = FontWeights.SemiBold, FontSize = 14 });
        heading.Children.Add(new TextBlock { Text = "P / V2 / V3 [kN]   ·   T / M2 / M3 [kNm]", Foreground = System.Windows.Media.Brushes.DimGray });
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var save = new Button { Content = "Save", IsDefault = true };
        save.Click += (_, _) =>
        {
            var problem = ValidateMemberRows(rows, null);
            if (problem is not null) { Info(problem); return; }
            dialog.DialogResult = true;
        };
        footer.Children.Add(cancel); footer.Children.Add(save); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var add = new Button { Content = "+ Add Group" };
        var edit = new Button { Content = "Edit" };
        var remove = new Button { Content = "Remove" };
        toolbar.Children.Add(add); toolbar.Children.Add(edit); toolbar.Children.Add(remove);
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);

        var grid = new DataGrid { ItemsSource = rows, IsReadOnly = true, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single };
        grid.Columns.Add(new DataGridTextColumn { Header = "SAP Group", Binding = new Binding(nameof(MemberGroupRow.SapGroupName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 240 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Members", Binding = new Binding(nameof(MemberGroupRow.MemberCount)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Frames", Binding = new Binding(nameof(MemberGroupRow.MemberSummary)), Width = new DataGridLength(1.5, DataGridLengthUnitType.Star), MinWidth = 280 });

        var availableFrames = baseline.Members.Where(m => m.Kind == MemberKind.Frame).Select(m => m.Name)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();

        add.Click += (_, _) =>
        {
            if (EditMemberGroup(null, rows, availableFrames, selection) is { } created) rows.Add(created);
        };
        edit.Click += (_, _) =>
        {
            if (grid.SelectedItem is not MemberGroupRow selected) return;
            if (EditMemberGroup(selected, rows, availableFrames, selection) is not { } edited) return;
            var index = rows.IndexOf(selected);
            rows[index] = edited;
            grid.SelectedItem = edited;
        };
        remove.Click += (_, _) =>
        {
            if (grid.SelectedItem is MemberGroupRow selected) rows.Remove(selected);
        };

        root.Children.Add(grid);
        dialog.Content = root;
        if (dialog.ShowDialog() != true) return false;

        settings.MemberForceGroups.Clear();
        foreach (var row in rows)
        {
            var group = new MemberForceOutputGroup { Id = row.Id, Name = row.Name };
            foreach (var member in row.Members) group.Members.Add(member);
            settings.MemberForceGroups.Add(group);
        }
        return true;
    }

    private static MemberGroupRow? EditMemberGroup(MemberGroupRow? existing, ObservableCollection<MemberGroupRow> all,
        IReadOnlyList<string> availableFrames, ISelectionService selection)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        var dialog = BasicDialog(existing is null ? "Add Member Force Group" : "Edit Member Force Group", 600, 300);
        if (owner is not null) dialog.Owner = owner;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(Label("Group suffix", "The SAP group will be created as [O]-{suffix} when Tab 06 starts."));
        var name = new TextBox { Text = existing?.Name ?? "" };
        panel.Children.Add(name);

        var members = new ObservableCollection<string>(existing?.Members ?? []);
        var summary = new TextBlock { Margin = new Thickness(0, 8, 0, 8) };
        void RefreshSummary() => summary.Text = $"{members.Count} frame{(members.Count == 1 ? "" : "s")} selected";
        RefreshSummary();
        panel.Children.Add(summary);

        var choose = new Button { Content = "Select Frames", HorizontalAlignment = HorizontalAlignment.Left };
        choose.Click += (_, _) =>
        {
            var picked = selection.Select("Select Frame Objects", availableFrames, members.ToArray());
            if (picked is null) return;
            Replace(members, picked);
            RefreshSummary();
        };
        panel.Children.Add(choose);

        var footer = Footer(dialog, existing is null ? "Add Group" : "Save Changes", () =>
        {
            var candidate = new MemberGroupRow(existing?.Id ?? Guid.NewGuid().ToString("N"), name.Text.Trim(), members.ToArray());
            var problem = ValidateMemberRows(all.Where(row => !ReferenceEquals(row, existing)).Append(candidate), candidate);
            if (problem is not null) { Info(problem); return false; }
            dialog.Tag = candidate;
            return true;
        });
        panel.Children.Add(footer);
        dialog.Content = panel;
        return dialog.ShowDialog() == true ? dialog.Tag as MemberGroupRow : null;
    }

    private static string? ValidateMemberRows(IEnumerable<MemberGroupRow> rows, MemberGroupRow? candidate)
    {
        var array = rows.ToArray();
        foreach (var row in array)
        {
            if (string.IsNullOrWhiteSpace(row.Name)) return "Enter a member-force group name.";
            if (OutputGroupNaming.ReservedNames.Contains(row.SapGroupName)) return $"{row.SapGroupName} is reserved.";
            if (row.Members.Count == 0) return $"{row.SapGroupName} must contain at least one frame.";
        }
        if (array.GroupBy(row => row.SapGroupName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            return "Member-force group names must be unique.";
        var duplicates = array.SelectMany(row => row.Members.Select(member => (member, row.SapGroupName)))
            .GroupBy(x => x.member, StringComparer.Ordinal).FirstOrDefault(group => group.Select(x => x.SapGroupName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
        if (duplicates is not null)
            return $"Frame {duplicates.Key} is already assigned to another member-force output group.";
        return null;
    }

    private static MemberGroupRow Clone(MemberForceOutputGroup group) => new(group.Id, group.Name, group.Members.ToArray());

    private static Window BasicDialog(string title, double width, double height) => new()
    {
        Title = title, Width = width, Height = height, MinWidth = Math.Min(width, 420), MinHeight = Math.Min(height, 180),
        WindowStartupLocation = WindowStartupLocation.CenterOwner
    };

    private static TextBlock Label(string text, string toolTip) => new()
    {
        Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5), ToolTip = toolTip
    };

    private static StackPanel Footer(Window dialog, string okText, Func<bool>? beforeClose = null)
    {
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var ok = new Button { Content = okText, IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (beforeClose is not null && !beforeClose()) return;
            dialog.DialogResult = true;
        };
        footer.Children.Add(cancel); footer.Children.Add(ok);
        return footer;
    }

    private static void Replace(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (var value in values.Distinct(StringComparer.Ordinal)) target.Add(value);
    }

    private static void Info(string message) =>
        MessageBox.Show(message, "Output Settings", MessageBoxButton.OK, MessageBoxImage.Information);

    private sealed record MemberGroupRow(string Id, string Name, IReadOnlyList<string> Members)
    {
        public string SapGroupName => OutputGroupNaming.MemberGroupName(Name);
        public int MemberCount => Members.Count;
        public string MemberSummary => Members.Count <= 6 ? string.Join(", ", Members) : string.Join(", ", Members.Take(6)) + $" … (+{Members.Count - 6})";
    }
}
