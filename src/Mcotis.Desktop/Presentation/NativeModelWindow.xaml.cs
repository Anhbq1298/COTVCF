using System.ComponentModel;
using System.Globalization;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using Mcotis.Application;
using Microsoft.Win32;

namespace Mcotis.Desktop.Presentation;

public partial class NativeModelWindow : Window
{
    private bool updatingView;
    public NativeModelViewModel Model { get; }
    public NativeModelWindow(DocumentSession session)
    {
        Model = new(session); InitializeComponent(); DataContext = Model;
        Loaded += (_, _) => { Viewer.ShowPlane("3D"); RefreshReferenceController(); Viewer.FitView(); };
        Closing += OnClosing;
        PreviewKeyDown += async (_, e) =>
        {
            if (!Model.Editable) return;
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Save(false); }
            else if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; await Model.RunAsync(); }
        };
    }
    private void ViewOrientation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || updatingView || ViewOrientation.SelectedIndex < 0) return;
        var plane = ViewOrientation.SelectedIndex switch { 1 => "XY", 2 => "XZ", 3 => "YZ", _ => "3D" };
        Model.SetViewPlane(plane);
        Viewer.ShowPlane(plane);
        RefreshReferenceController();
        Viewer.FitView();
    }

    private void RefreshReferenceController()
    {
        if (ViewCoordinate is null) return;
        updatingView = true;
        try
        {
            ViewCoordinate.ItemsSource = null;
            var coordinates = Model.ViewCoordinates;
            ViewCoordinate.ItemsSource = coordinates;
            ViewCoordinate.IsEnabled = Model.ViewPlane != "3D";
            FixedAxisLabel.Text = Model.ViewPlane == "3D" ? "Coordinate (m)" : NativeGridLevelController.FixedAxis(Model.ViewPlane) + " (m)";
            ReferenceKind.Text = Model.ViewPlane switch { "XY" => "Levels / Z planes", "XZ" => "Y grids / elevations", "YZ" => "X grids / elevations", _ => "Model coordinates" };
            if (Model.ViewPlane != "3D")
            {
                ViewCoordinate.SelectedItem = coordinates.FirstOrDefault(v => Math.Abs(v - Model.ViewCoordinate) <= NativeGridLevelController.Tolerance);
                ViewCoordinate.Text = Model.ViewCoordinate.ToString("G", CultureInfo.CurrentCulture);
            }
            else ViewCoordinate.Text = "";
        }
        finally { updatingView = false; }
    }

    private void ViewCoordinate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || updatingView || ViewCoordinate.SelectedItem is not double coordinate) return;
        Model.SetViewCoordinate(coordinate);
        Viewer.FitView();
    }

    private void ViewCoordinate_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!IsLoaded || updatingView || Model.ViewPlane == "3D") return;
        if (double.TryParse(ViewCoordinate.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value))
        {
            Model.SetViewCoordinate(value);
            Viewer.FitView();
        }
        else Model.Message = "Invalid plane coordinate. Enter a finite number in metres.";
    }

    private void StepLevel(int direction)
    {
        if (Model.ViewPlane == "3D") return;
        var levels = Model.ViewCoordinates;
        if (levels.Count == 0) return;
        var nearest = Enumerable.Range(0, levels.Count).MinBy(i => Math.Abs(levels[i] - Model.ViewCoordinate));
        Model.SetViewCoordinate(levels[Math.Clamp(nearest + direction, 0, levels.Count - 1)]);
        RefreshReferenceController();
        Viewer.FitView();
    }
    private void PreviousLevel_Click(object sender, RoutedEventArgs e) => StepLevel(-1);
    private void NextLevel_Click(object sender, RoutedEventArgs e) => StepLevel(1);

    internal static void CreateTemplate(Window owner)
    {
        var form = new TemplateBuildingWindow { Owner = owner };
        if (form.ShowDialog() == true && form.CreatedSession is { } session) new NativeModelWindow(session).Show();
    }
    private void Template_Click(object sender, RoutedEventArgs e) => CreateTemplate(this);
    private void Modal_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NativeModalSetupWindow(Model.ModalDefinition()) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Definition is not { } definition) return;
        try { Model.DefineModal(definition); }
        catch (Exception ex) { Model.Message = "Modal case was not changed: " + ex.Message; }
    }
    private void JointMass_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NativeJointMassWindow(Model.Session.Document) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Definition is not { } definition) return;
        try { Model.AssignJointMass(definition); }
        catch (Exception ex) { Model.Message = "Joint mass was not changed: " + ex.Message; }
    }
    private void Options_Click(object sender, RoutedEventArgs e) => new AnalysisOptionsWindow(Model.Session.Document.Settings,
        settings => Model.Session.Execute(DocumentCommands.SetSettings(settings)), cables: Model.Session.Document.Cables.Count > 0) { Owner = this }.ShowDialog();
    private async void Run_Click(object sender, RoutedEventArgs e) => await Model.RunAsync();
    private void Undo_Click(object sender, RoutedEventArgs e) { if (Model.Editable) Model.Session.Undo(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { if (Model.Editable) Model.Session.Redo(); }
    private bool Save(bool saveAs)
    {
        try
        {
            if (saveAs || Model.Session.FilePath is null)
            {
                var dialog = new SaveFileDialog { Filter = "Mcotis native model (*.mcotis)|*.mcotis", DefaultExt = ".mcotis", FileName = "TemplateBuilding.mcotis" };
                if (dialog.ShowDialog(this) != true) return false;
                Model.Files.SaveAs(dialog.FileName);
            }
            else Model.Files.Save();
            Model.Message = "Model saved. Results are held in this window; they are not stored in the model file.";
            return true;
        }
        catch (Exception ex) { Model.Message = "Save failed: " + ex.Message; return false; }
    }
    private void Save_Click(object sender, RoutedEventArgs e) => Save(false);
    private void SaveAs_Click(object sender, RoutedEventArgs e) => Save(true);
    private bool MayDiscard()
    {
        if (!Model.Session.IsDirty) return true;
        return MessageBox.Show(this, "Save changes to this native model?", "Mcotis", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        { MessageBoxResult.Yes => Save(false), MessageBoxResult.No => true, _ => false };
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Mcotis native model (*.mcotis)|*.mcotis" };
        if (dialog.ShowDialog(this) != true || !MayDiscard()) return;
        try { Model.Files.Open(dialog.FileName); Model.Message = "Native model opened. Run analysis to calculate results."; Viewer.FitView(); }
        catch (Exception ex) { Model.Message = "Open failed: " + ex.Message; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Model.Busy) { Model.Message = "Wait for analysis to finish before closing this window."; e.Cancel = true; }
        else if (!MayDiscard()) e.Cancel = true;
    }
}
