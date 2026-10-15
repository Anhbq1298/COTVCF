using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Presentation.Controls;

namespace Mcotis.Desktop.Presentation;

public enum ShowResultPickerKind { All, Deflection, MemberForces, Reactions }

/// <summary>
/// Toolbar → Show Results: choose a static result, or a modal case and mode for the deformed view. Apply / OK draws
/// the output on the model view (as the Result menu does); Cable target fit opens the results table.
/// </summary>
public partial class ShowResultsWindow : Window
{
    private sealed record ShapeCase(string Name, bool Modal, bool Combination = false)
    {
        public override string ToString() => Modal ? $"{Name} (modal)" : Name;
    }

    private readonly MainViewModel viewModel;
    private readonly Action<bool> openTable;
    private readonly Structural2DViewer? viewer;
    private bool lastModalUnits;
    private bool refreshingShapeCases;
    public ShowResultPickerKind PickerKind { get; private set; }

    /// <param name="openTable">Opens the results table; the argument selects the cable target fit.</param>
    public ShowResultsWindow(MainViewModel viewModel, Action<bool> openTable, ShowResultPickerKind kind = ShowResultPickerKind.All,
        Structural2DViewer? viewer = null)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.openTable = openTable;
        this.viewer = viewer;
        DataContext = viewModel;
        ReadDisplaySettings();
        DrawContours.Checked += DrawContours_Changed;
        DrawContours.Unchecked += DrawContours_Changed;
        if (viewModel.SelectedFemRun is null && viewModel.FemRuns.Count > 0) viewModel.SelectedFemRun = viewModel.FemRuns[0];
        Configure(kind);
    }

    public void Configure(ShowResultPickerKind kind)
    {
        PickerKind = kind;
        Width = kind == ShowResultPickerKind.Deflection ? 480 : 650;
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
        if (kind == ShowResultPickerKind.Deflection) ReadDisplaySettings();
        UpdateShapePicker();
    }

    private void Outputs_Changed(object sender, SelectionChangedEventArgs e) => UpdateShapePicker();

    private void UpdateShapePicker()
    {
        if (LoadCasePicker is null || ShapePicker is null || Outputs.SelectedItem is not ListBoxItem output) return;
        bool shape = Equals(output.Tag, nameof(FemResultMode.DeflectedShape));
        ShapePicker.Visibility = shape ? Visibility.Visible : Visibility.Collapsed;
        DeformationOptions.Visibility = shape ? Visibility.Visible : Visibility.Collapsed;
        LoadCasePicker.Visibility = shape ? Visibility.Collapsed : Visibility.Visible;
        if (!shape) return;
        var combinations = viewModel.FemCombinationRows.Select(row => row.Name).ToHashSet(StringComparer.Ordinal);
        CaseRadio.IsEnabled = viewModel.FemResultNames.Any(name => !combinations.Contains(name));
        ComboRadio.IsEnabled = viewModel.FemResultNames.Any(combinations.Contains);
        ModeRadio.IsEnabled = viewModel.ModalResults.CaseNames.Count > 0;
        if (viewModel.SharedViewerIsModeShape && ModeRadio.IsEnabled || !CaseRadio.IsEnabled && !ComboRadio.IsEnabled && ModeRadio.IsEnabled) ModeRadio.IsChecked = true;
        else if (ComboRadio.IsEnabled && (combinations.Contains(viewModel.SelectedFemResultName ?? "") || !CaseRadio.IsEnabled)) ComboRadio.IsChecked = true;
        else CaseRadio.IsChecked = true;
        RefreshShapeCases();
    }

    private void ShapeCategory_Click(object sender, RoutedEventArgs e) => RefreshShapeCases();

    private void RefreshShapeCases()
    {
        if (ShapeCasePicker is null) return;
        var previous = ShapeCasePicker.SelectedItem as ShapeCase;
        var combinations = viewModel.FemCombinationRows.Select(row => row.Name).ToHashSet(StringComparer.Ordinal);
        var choices = ModeRadio.IsChecked == true
            ? viewModel.ModalResults.CaseNames.Select(name => new ShapeCase(name, true)).ToArray()
            : viewModel.FemResultNames.Where(name => combinations.Contains(name) == (ComboRadio.IsChecked == true))
                .Select(name => new ShapeCase(name, false, combinations.Contains(name))).ToArray();
        refreshingShapeCases = true;
        try
        {
            ShapeCasePicker.ItemsSource = choices;
            ShapeCasePicker.SelectedItem = choices.FirstOrDefault(item => item == previous)
                ?? choices.FirstOrDefault(item => item.Name == (item.Modal ? viewModel.ModalResults.SelectedCase : viewModel.SelectedFemResultName))
                ?? choices.FirstOrDefault();
        }
        finally { refreshingShapeCases = false; }
        ModePicker.Visibility = ModeRadio.IsChecked == true && choices.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateModePicker();
    }

    private void ShapeCase_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!refreshingShapeCases) UpdateModePicker();
    }

    private void UpdateModePicker()
    {
        if (ModePicker is null) return;
        var modal = ShapeCasePicker.SelectedItem is ShapeCase { Modal: true };
        if (lastModalUnits != modal)
        {
            if (TryNumber(MinimumRange.Text, out var minimum) && TryNumber(MaximumRange.Text, out var maximum))
            {
                MinimumRange.Text = (modal ? minimum / 1000 : minimum * 1000).ToString("G9", CultureInfo.InvariantCulture);
                MaximumRange.Text = (modal ? maximum / 1000 : maximum * 1000).ToString("G9", CultureInfo.InvariantCulture);
            }
            lastModalUnits = modal;
        }
        MinimumUnit.Text = MaximumUnit.Text = modal ? "relative" : "mm";
        if (ShapeCasePicker.SelectedItem is not ShapeCase choice) { ModePicker.Visibility = Visibility.Collapsed; return; }
        ModePicker.Visibility = choice.Modal ? Visibility.Visible : Visibility.Collapsed;
        if (!choice.Modal) return;
        var modes = viewModel.ModalResults.ModeNumbersFor(choice.Name);
        ModeNumberPicker.ItemsSource = modes;
        ModeNumberPicker.SelectedItem = choice.Name == viewModel.ModalResults.SelectedCase &&
            modes.Contains(viewModel.ModalResults.SelectedMode) ? viewModel.ModalResults.SelectedMode : modes.FirstOrDefault();
    }

    /// <summary>Draws the selected output on the model view (cable target fit: the table).</summary>
    internal bool ApplySelection()
    {
        if (!viewModel.HasFemResults || Outputs.SelectedItem is not ListBoxItem { Tag: string tag }) return false;
        if (tag == nameof(FemResultMode.DeflectedShape))
        {
            if (ShapeCasePicker.SelectedItem is not ShapeCase shape)
            {
                OptionError.Text = "Choose a solved case, combination or modal case.";
                ShapeCasePicker.Focus();
                return false;
            }
            if (shape.Modal && ModeNumberPicker.SelectedItem is not int)
            {
                OptionError.Text = "Choose a solved mode number.";
                ModeNumberPicker.Focus();
                return false;
            }
            if (!ApplyDisplaySettings()) return false;
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
            return true;
        }
        var mode = Enum.TryParse<FemResultMode>(tag, out var value) ? value : (FemResultMode?)null;
        viewModel.ShowFemResult(mode);
        if (mode is null) openTable(true);
        return true;
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => ApplySelection();
    private void Ok_Click(object sender, RoutedEventArgs e) { if (ApplySelection()) Close(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Outputs_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => ApplySelection();
    private void Table_Click(object sender, RoutedEventArgs e) => openTable(viewModel.FemCableFitView);
    private void Export_Click(object sender, RoutedEventArgs e) => new ExportResultsWindow(viewModel) { Owner = this }.ShowDialog();

    private void ReadDisplaySettings()
    {
        AutomaticScale.IsChecked = viewer is null || viewer.DeflectionScaleFactor <= 0;
        UserScale.IsChecked = !AutomaticScale.IsChecked;
        ScaleFactor.Text = (viewer is { DeflectionScaleFactor: > 0 } ? viewer.DeflectionScaleFactor : 1).ToString("G9", CultureInfo.InvariantCulture);
        DrawContours.IsChecked = viewer?.ShowDeflectionContour ?? true;
        ContourComponent.SelectedItem = ContourComponent.Items.OfType<ComboBoxItem>()
            .First(item => (string)item.Tag == (viewer?.DeflectionContourComponent ?? DisplacementContourComponent.Magnitude).ToString());
        MinimumRange.Text = (viewer?.DeflectionRangeMinMm ?? 0).ToString("G9", CultureInfo.InvariantCulture);
        MaximumRange.Text = (viewer?.DeflectionRangeMaxMm ?? 0).ToString("G9", CultureInfo.InvariantCulture);
        WireShadow.IsChecked = viewer?.ShowUndeformed ?? true;
        CubicCurve.IsChecked = viewer?.DeflectionCubicCurve ?? true;
        lastModalUnits = false;
        ScaleChoice_Click(this, new RoutedEventArgs());
        DrawContours_Changed(this, new RoutedEventArgs());
        OptionError.Text = "";
    }

    private void ScaleChoice_Click(object sender, RoutedEventArgs e) => ScaleFactor.IsEnabled = UserScale.IsChecked == true;
    private void DrawContours_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = DrawContours.IsChecked == true;
        ContourComponent.IsEnabled = MinimumRange.IsEnabled = MaximumRange.IsEnabled = enabled;
    }

    private static bool TryNumber(string text, out double value)
    {
        var normalized = text.Trim();
        if (normalized.Contains('.') && normalized.Contains(',')) { value = 0; return false; }
        return double.TryParse(normalized.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private bool ApplyDisplaySettings()
    {
        OptionError.Text = "";
        if (viewer is null) return true;
        var scale = 0.0;
        if (UserScale.IsChecked == true && (!TryNumber(ScaleFactor.Text, out scale) || scale <= 0))
        {
            OptionError.Text = "Scale factor must be a positive finite number.";
            ScaleFactor.Focus();
            return false;
        }
        double minimum = lastModalUnits ? viewer.DeflectionRangeMinMm / 1000 : viewer.DeflectionRangeMinMm;
        double maximum = lastModalUnits ? viewer.DeflectionRangeMaxMm / 1000 : viewer.DeflectionRangeMaxMm;
        if (DrawContours.IsChecked == true)
        {
            if (!TryNumber(MinimumRange.Text, out minimum) || !TryNumber(MaximumRange.Text, out maximum) ||
                (minimum != 0 || maximum != 0) && maximum <= minimum)
            {
                OptionError.Text = "Contour range must have finite numbers with maximum greater than minimum, or both zero for automatic range.";
                MinimumRange.Focus();
                return false;
            }
        }
        var component = Enum.Parse<DisplacementContourComponent>((string)((ComboBoxItem)ContourComponent.SelectedItem).Tag);
        viewer.DeflectionScaleFactor = scale;
        viewer.ShowDeflectionContour = DrawContours.IsChecked == true;
        viewer.DeflectionContourComponent = component;
        viewer.DeflectionRangeMinMm = lastModalUnits ? minimum * 1000 : minimum;
        viewer.DeflectionRangeMaxMm = lastModalUnits ? maximum * 1000 : maximum;
        viewer.ShowUndeformed = WireShadow.IsChecked == true;
        viewer.DeflectionCubicCurve = CubicCurve.IsChecked == true;
        return true;
    }
}
