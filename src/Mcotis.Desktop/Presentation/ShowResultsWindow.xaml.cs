using System.Windows;
using System.Windows.Controls;
using Mcotis.Desktop.Application;

namespace Mcotis.Desktop.Presentation;

public enum ShowResultPickerKind { All, Deflection, MemberForces, Reactions }

/// <summary>
/// Toolbar → Show Results: choose a static result, or a modal case and mode for the deformed view. Apply / OK draws
/// the output on the model view (as the Result menu does); Cable target fit opens the results table.
/// </summary>
public partial class ShowResultsWindow : Window
{
    private sealed record ShapeCase(string Name, bool Modal)
    {
        public override string ToString() => Modal ? $"{Name} (modal)" : Name;
    }

    private readonly MainViewModel viewModel;
    private readonly Action<bool> openTable;
    public ShowResultPickerKind PickerKind { get; private set; }

    /// <param name="openTable">Opens the results table; the argument selects the cable target fit.</param>
    public ShowResultsWindow(MainViewModel viewModel, Action<bool> openTable, ShowResultPickerKind kind = ShowResultPickerKind.All)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.openTable = openTable;
        DataContext = viewModel;
        if (viewModel.SelectedFemRun is null && viewModel.FemRuns.Count > 0) viewModel.SelectedFemRun = viewModel.FemRuns[0];
        Configure(kind);
    }

    public void Configure(ShowResultPickerKind kind)
    {
        PickerKind = kind;
        var any = viewModel.HasFemResults;
        NoResults.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        Outputs.IsEnabled = TableButton.IsEnabled = ApplyButton.IsEnabled = OkButton.IsEnabled = any;
        Title = kind switch
        {
            ShowResultPickerKind.Deflection => "Show Deformed Shape (F5)",
            ShowResultPickerKind.MemberForces => "Show Member Forces (F6)",
            ShowResultPickerKind.Reactions => "Show Joint Reactions (F7)",
            _ => "Show Results"
        };
        OutputGroup.Visibility = kind is ShowResultPickerKind.All or ShowResultPickerKind.MemberForces
            ? Visibility.Visible : Visibility.Collapsed;
        TableButton.Visibility = kind == ShowResultPickerKind.All ? Visibility.Visible : Visibility.Collapsed;
        var current = viewModel.FemCableFitView ? "CableFit" : viewModel.FemResultMode.ToString();
        foreach (var item in Outputs.Items.OfType<ListBoxItem>())
        {
            var tag = item.Tag as string;
            // the cable target fit is offered for a model with cables only
            item.Visibility = (tag != "CableFit" || viewModel.HasCableMembers) && (kind == ShowResultPickerKind.All ||
                kind == ShowResultPickerKind.Deflection && tag == "DeflectedShape" ||
                kind == ShowResultPickerKind.Reactions && tag == "Reactions" ||
                kind == ShowResultPickerKind.MemberForces && tag is "AxialForce" or "Shear" or "BendingMoment")
                ? Visibility.Visible : Visibility.Collapsed;
        }
        var preferred = kind switch
        {
            ShowResultPickerKind.Deflection => "DeflectedShape",
            ShowResultPickerKind.Reactions => "Reactions",
            ShowResultPickerKind.MemberForces when current is "AxialForce" or "Shear" or "BendingMoment" => current,
            ShowResultPickerKind.MemberForces => "AxialForce",
            _ => current
        };
        Outputs.SelectedItem = Outputs.Items.OfType<ListBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, preferred) && item.Visibility == Visibility.Visible)
            ?? Outputs.Items.OfType<ListBoxItem>().First(item => item.Visibility == Visibility.Visible);
        UpdateShapePicker();
    }

    private void Outputs_Changed(object sender, SelectionChangedEventArgs e) => UpdateShapePicker();

    private void UpdateShapePicker()
    {
        if (LoadCasePicker is null || ShapePicker is null || Outputs.SelectedItem is not ListBoxItem output) return;
        bool shape = Equals(output.Tag, nameof(FemResultMode.DeflectedShape));
        ShapePicker.Visibility = shape ? Visibility.Visible : Visibility.Collapsed;
        LoadCasePicker.Visibility = shape ? Visibility.Collapsed : Visibility.Visible;
        if (!shape) return;

        var previous = ShapeCasePicker.SelectedItem as ShapeCase;
        var choices = viewModel.FemResultNames.Select(name => new ShapeCase(name, false))
            .Concat(viewModel.ModalResults.CaseNames.Select(name => new ShapeCase(name, true))).ToArray();
        ShapeCasePicker.ItemsSource = choices;
        ShapeCasePicker.SelectedItem = choices.FirstOrDefault(item => item == previous)
            ?? choices.FirstOrDefault(item => item.Modal == viewModel.SharedViewerIsModeShape &&
                                             item.Name == (item.Modal ? viewModel.ModalResults.SelectedCase : viewModel.SelectedFemResultName))
            ?? choices.FirstOrDefault();
        UpdateModePicker();
    }

    private void ShapeCase_Changed(object sender, SelectionChangedEventArgs e) => UpdateModePicker();

    private void UpdateModePicker()
    {
        if (ModePicker is null || ShapeCasePicker.SelectedItem is not ShapeCase choice) return;
        ModePicker.Visibility = choice.Modal ? Visibility.Visible : Visibility.Collapsed;
        if (!choice.Modal) return;
        var modes = viewModel.ModalResults.ModeNumbersFor(choice.Name);
        ModeNumberPicker.ItemsSource = modes;
        ModeNumberPicker.SelectedItem = choice.Name == viewModel.ModalResults.SelectedCase &&
            modes.Contains(viewModel.ModalResults.SelectedMode) ? viewModel.ModalResults.SelectedMode : modes.FirstOrDefault();
    }

    /// <summary>Draws the selected output on the model view (cable target fit: the table).</summary>
    internal void ApplySelection()
    {
        if (!viewModel.HasFemResults || Outputs.SelectedItem is not ListBoxItem { Tag: string tag }) return;
        if (tag == nameof(FemResultMode.DeflectedShape) && ShapeCasePicker.SelectedItem is ShapeCase shape)
        {
            if (shape.Modal)
            {
                viewModel.ModalResults.SelectedCase = shape.Name;
                if (ModeNumberPicker.SelectedItem is int number) viewModel.ModalResults.SelectedMode = number;
                viewModel.ShowModeShape();
            }
            else
            {
                viewModel.SelectedFemResultName = shape.Name;
                viewModel.ShowFemResult(FemResultMode.DeflectedShape);
            }
            return;
        }
        var mode = Enum.TryParse<FemResultMode>(tag, out var value) ? value : (FemResultMode?)null;
        viewModel.ShowFemResult(mode);
        if (mode is null) openTable(true);
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => ApplySelection();
    private void Ok_Click(object sender, RoutedEventArgs e) { ApplySelection(); Close(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Outputs_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => ApplySelection();
    private void Table_Click(object sender, RoutedEventArgs e) => openTable(viewModel.FemCableFitView);
    private void Export_Click(object sender, RoutedEventArgs e) => new ExportResultsWindow(viewModel) { Owner = this }.ShowDialog();
}
