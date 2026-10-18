using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Presentation;

/// <summary>
/// Design → Override Design Procedure: select members, assign each the design procedure of one of the Design menu's engines
/// (RC column, CFT, steel encased, mega composite) whatever its section suggests, then design them. A member is designed on the
/// forces of the FEM run and the size of its own section; the code, method and factors are those of the engine's Design window.
/// </summary>
public sealed class DesignProcedureWindow : Window
{
    /// <summary>A member of the grid and the procedure assigned to it.</summary>
    public sealed class MemberRow(string member, string section, string material, double length, string suggested) : Observable
    {
        private string procedure = "";
        public string Member { get; } = member;
        public string Section { get; } = section;
        public string Material { get; } = material;
        public string Length { get; } = length > 0 ? length.ToString("0.###", CultureInfo.InvariantCulture) : "";
        public string Suggested { get; } = suggested;
        public string Procedure { get => procedure; set => Set(ref procedure, value); }
    }

    private readonly MainViewModel viewModel;
    private readonly List<MemberRow> rows;
    private readonly DataGrid grid = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended, HeadersVisibility = DataGridHeadersVisibility.Column, CanUserAddRows = false };
    private readonly ComboBox procedureBox = new() { MinWidth = 300, DisplayMemberPath = "Title" };
    private readonly TextBox factorBox = new() { Text = "10", Width = 60 };
    private readonly List<DesignReferenceWindow.CheckItem> cases;
    private readonly TabControl tabs = new();
    private readonly TabItem designTab = new() { Header = "Design" };
    private readonly DesignResultsPanel results = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 520, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
    private readonly Button closeButton = new() { Content = "Cancel", MinWidth = 80, IsCancel = true };

    private sealed record ProcedureItem(ColumnDesignType? Type, string Title);

    public DesignProcedureWindow(MainViewModel viewModel, IReadOnlyList<string> viewerSelection)
    {
        this.viewModel = viewModel;
        Title = "Override Design Procedure";
        Icon = AppIcon.Window;
        Width = 1000; Height = 720; MinWidth = 700; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Surface");

        var options = viewModel.DesignMemberOptions(ColumnDesignType.ReinforcedConcrete);
        var selected = viewerSelection.ToHashSet(StringComparer.Ordinal);
        rows = options.OrderBy(option => selected.Contains(option.Member) ? 0 : 1)
            .Select(option => new MemberRow(option.Member, option.Section, option.Material, option.LengthM,
                viewModel.SuggestedDesignProcedure(option.Member) is { } type ? ColumnDesignCodes.Title(type) : "—")
            { Procedure = viewModel.DesignProcedureOf(option.Member) is { } assigned ? ColumnDesignCodes.Title(assigned) : "" }).ToList();
        cases = viewModel.DesignCaseNames().Select(name => new DesignReferenceWindow.CheckItem(name, "", viewModel.DesignDefaultCases().Contains(name))).ToList();

        procedureBox.ItemsSource = Enum.GetValues<ColumnDesignType>().Select(type => new ProcedureItem(type, ColumnDesignCodes.Title(type))).ToList();
        procedureBox.SelectedIndex = 0;
        System.Windows.Automation.AutomationProperties.SetName(procedureBox, "Design procedure");
        System.Windows.Automation.AutomationProperties.SetName(grid, "Members");
        System.Windows.Automation.AutomationProperties.SetName(factorBox, "Effective length");

        var root = new DockPanel { Margin = new Thickness(10) };
        var banner = new TextBlock { Text = "Override Design Procedure" };
        banner.SetResourceReference(StyleProperty, "BannerTitle");
        banner.Margin = new Thickness(2, 0, 0, 8);
        DockPanel.SetDock(banner, Dock.Top);
        root.Children.Add(banner);

        var runButton = new Button { Content = "Run Design", MinWidth = 90, Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
        runButton.Click += (_, _) => Run();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { runButton, closeButton } };
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Error");
        bottom.Children.Add(status);
        bottom.Children.Add(buttons);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        tabs.Items.Add(new TabItem { Header = "Members & Procedure", Content = BuildAssignTab() });
        designTab.Content = results;
        tabs.Items.Add(designTab);
        root.Children.Add(tabs);
        Content = root;

        // frames selected in the viewer are selected in the grid
        foreach (var row in rows.Where(row => selected.Contains(row.Member))) grid.SelectedItems.Add(row);
        Message(viewModel.DesignRunId is null ? "There are no FEM results to design with. Run the FEM analysis (Run → Run FEM Analysis) first." : "");
    }

    private UIElement BuildAssignTab()
    {
        var layout = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });

        var assign = new Button { Content = "Assign to selected", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2),
            ToolTip = "Assign the procedure to the members selected in the table" };
        assign.Click += (_, _) => Assign((procedureBox.SelectedItem as ProcedureItem)?.Type);
        var clear = new Button { Content = "Clear selected", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2),
            ToolTip = "Remove the assignment from the members selected in the table" };
        clear.Click += (_, _) => Assign(null);
        var suggested = new Button { Content = "Use suggested", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2),
            ToolTip = "Assign each selected member the procedure its section suggests" };
        suggested.Click += (_, _) => UseSuggested();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(new TextBlock { Text = "Design procedure", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        bar.Children.Add(procedureBox);
        bar.Children.Add(assign);
        bar.Children.Add(clear);
        bar.Children.Add(suggested);

        grid.ItemsSource = rows;
        foreach (var (header, path, width) in new[] { ("Member", nameof(MemberRow.Member), 80.0), ("Section", nameof(MemberRow.Section), 150.0), ("Material", nameof(MemberRow.Material), 110.0),
                     ("L [m]", nameof(MemberRow.Length), 60.0), ("Suggested by section", nameof(MemberRow.Suggested), 200.0), ("Assigned procedure", nameof(MemberRow.Procedure), 240.0) })
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(path), Width = new DataGridLength(width, DataGridLengthUnitType.Pixel) });
        var left = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        left.Children.Add(bar);
        var hint = new TextBlock { Text = "Select members (Ctrl / Shift-click, or select frames in the viewer first), choose a procedure and assign it. Only assigned members are designed; " +
                                          "the size comes from each member's own section and the forces from the FEM analysis.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        hint.SetResourceReference(StyleProperty, "Hint");
        DockPanel.SetDock(hint, Dock.Bottom);
        left.Children.Add(hint);
        left.Children.Add(grid);
        layout.Children.Add(left);

        var side = new StackPanel();
        Grid.SetColumn(side, 2);
        var factorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        factorRow.Children.Add(new TextBlock { Text = "Effective length l0 [m]", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        factorRow.Children.Add(factorBox);
        var factorHint = new TextBlock { Text = "10 m by default, used about both axes for every member.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        factorHint.SetResourceReference(StyleProperty, "Hint");
        var caseList = new ListBox { Height = 360, ItemsSource = cases };
        var template = new FrameworkElementFactory(typeof(CheckBox));
        template.SetBinding(CheckBox.IsCheckedProperty, new System.Windows.Data.Binding(nameof(DesignReferenceWindow.CheckItem.IsChecked)));
        template.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding(nameof(DesignReferenceWindow.CheckItem.Name)));
        caseList.ItemTemplate = new DataTemplate { VisualTree = template };
        System.Windows.Automation.AutomationProperties.SetName(caseList, "Load cases");
        side.Children.Add(factorRow);
        side.Children.Add(factorHint);
        side.Children.Add(new GroupBox { Header = "Load cases / combinations", Content = caseList });
        layout.Children.Add(side);
        return layout;
    }

    private void Message(string text) => status.Text = text;

    private void Assign(ColumnDesignType? type)
    {
        var picked = grid.SelectedItems.OfType<MemberRow>().ToList();
        if (picked.Count == 0) { Message("Select at least one member in the table."); return; }
        var refused = viewModel.AssignDesignProcedure(picked.Select(row => row.Member), type);
        Refresh();
        Message(refused.Count == 0 ? "" : "Not assigned — " + string.Join(" ", refused));
    }

    private void UseSuggested()
    {
        var picked = grid.SelectedItems.OfType<MemberRow>().ToList();
        if (picked.Count == 0) { Message("Select at least one member in the table."); return; }
        var refused = new List<string>();
        foreach (var row in picked)
        {
            if (viewModel.SuggestedDesignProcedure(row.Member) is { } type) refused.AddRange(viewModel.AssignDesignProcedure([row.Member], type));
            else refused.Add($"{row.Member}: its section suggests no design procedure.");
        }
        Refresh();
        Message(refused.Count == 0 ? "" : "Not assigned — " + string.Join(" ", refused));
    }

    private void Refresh()
    {
        foreach (var row in rows) row.Procedure = viewModel.DesignProcedureOf(row.Member) is { } type ? ColumnDesignCodes.Title(type) : "";
    }

    private void Run()
    {
        if (viewModel.DesignRunId is null) { Message("There are no FEM results to design with. Run the FEM analysis (Run → Run FEM Analysis) first."); return; }
        if (!double.TryParse(factorBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor) || factor <= 0) { Message("The effective length must be a number greater than 0."); return; }
        var members = rows.Where(row => row.Procedure.Length > 0).Select(row => row.Member).ToArray();
        if (members.Length == 0) { Message("Assign a design procedure to at least one member first."); return; }
        var chosen = cases.Where(item => item.IsChecked).Select(item => item.Name).ToArray();
        if (cases.Count > 0 && chosen.Length == 0) { Message("Tick at least one load case or combination."); return; }
        try
        {
            var designed = viewModel.RunOverriddenDesign(members, chosen, factor);
            var basis = viewModel.DesignBasis(ColumnDesignType.ReinforcedConcrete) with { Members = members, LoadCases = chosen };
            results.Show(basis, designed, viewModel.DesignRunId, "Override Design Procedure",
                $"{designed.Select(result => result.Procedure).Distinct().Count()} procedure(s) · l0 = {factor:0.###} m · FEM analysis of the model · {chosen.Length} load case(s)");
        }
        catch (Exception ex) { Message(ex is InvalidOperationException ? ex.Message : "The design could not be completed: " + ex.Message); return; }
        Message("");
        closeButton.Content = "Close";
        tabs.SelectedItem = designTab;
    }
}
