using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mcotis.Desktop.Presentation.Controls;
using Mcotis.Desktop.Presentation.GeometryEditing;

namespace Mcotis.Desktop.Presentation;

// ---- Geometry editing in the main window (docs/geometry/GEOMETRY_EDITING_PLAN.md) ----
// The window wires the model view's tools to the view model's edits: a click of a tool becomes AddPoint / DrawMember /
// MoveObjects, each one Undo step; a refusal is said in the status bar, never in a dialog. Forms by coordinates, the grids
// and levels, the plane navigator and the cursor read-out live here too.
public partial class MainWindow
{
    private void InitializeGeometryEditing()
    {
        SharedViewer.PointPlaced += (_, e) =>
        {
            if (DataContext is not MainViewModel vm) return;
            if (e.Joint is not null)
            {
                vm.ReportEditing($"Point {e.Joint} is already there: a second point would not be connected to it.");
                return;
            }
            e.Accepted = Edit(vm, () => vm.AddPoint(e.Position));
        };
        SharedViewer.MemberDrawn += (_, e) =>
        {
            if (DataContext is not MainViewModel vm) return;
            (string Member, string EndJoint) drawn = default;
            e.Accepted = Edit(vm, () => drawn = vm.DrawMember(e.Start, e.StartJoint, e.End, e.EndJoint, e.Cable));
            if (e.Accepted) e.EndJoint = drawn.EndJoint;
        };
        SharedViewer.MoveRequested += (_, e) =>
        {
            if (DataContext is not MainViewModel vm) return;
            e.Accepted = Edit(vm, () => vm.MoveObjects(SharedViewer.SelectedJoints, SharedViewer.SelectedMembers, e.Offset));
        };
        SharedViewer.CursorMoved += (_, _) => UpdateCursorStatus();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(MainViewModel.ViewPlaneTitle) or nameof(MainViewModel.DrawingToolText)) UpdateCursorStatus();
                };
        };
    }

    /// <summary>An edit from a tool on the model view: true when made; a refusal goes to the status bar.</summary>
    private static bool Edit(MainViewModel vm, Action edit)
    {
        try
        {
            edit();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Mcotis.Core.DocumentException)
        {
            vm.ReportEditing("Not drawn: " + ex.Message);
            System.Media.SystemSounds.Beep.Play();
            return false;
        }
    }

    /// <summary>The status bar: the plane, the cursor in model coordinates (snapped while a tool is on) and the tool.</summary>
    private void UpdateCursorStatus()
    {
        if (DataContext is not MainViewModel vm) return;
        var parts = new List<string> { vm.ViewPlaneTitle };
        if (SharedViewer.CursorWorld is { } at)
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"X {at.X:0.000}  Y {at.Y:0.000}  Z {at.Z:0.000} m"));
        if (SharedViewer.CursorSnap is { Kind: not SnapKind.None } snap) parts.Add(SnapResolver.Describe(snap.Kind) + (snap.Joint is { } joint ? $" {joint}" : ""));
        parts.Add(vm.DrawingToolText);
        CursorStatus.Text = string.Join("  ·  ", parts);
    }

    // ---- menus and tools ----

    private void DrawTool_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<DrawingTool>(tag, out var tool)) return;
        SharedViewer.PanMode = false;
        vm.DrawingTool = tool;
        SharedViewer.Focus();
    }

    private void MoveTool_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        SharedViewer.PanMode = false;
        vm.DrawingTool = DrawingTool.Move;
    }

    private void MoveSelection_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanEditFemModel) return;
        if (SharedViewer.SelectedJoints.Count + SharedViewer.SelectedMembers.Count == 0)
        {
            MessageBox.Show(this, "Select the points, frames or cables to move on the model view first (click them, or drag a window), then choose Edit → Move.",
                "Move", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new MoveSelectionWindow(vm, SharedViewer.SelectedJoints, SharedViewer.SelectedMembers) { Owner = this }.ShowDialog();
    }

    private void PointCoordinates_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) new PointCoordinatesWindow(vm, SharedViewer.SelectedJoints) { Owner = this }.ShowDialog();
    }

    private void MemberCoordinates_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) new MemberCoordinatesWindow(vm, SharedViewer.SelectedJoints) { Owner = this }.ShowDialog();
    }

    private void GenerateGridPoints_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) new GenerateGridPointsWindow(vm) { Owner = this }.ShowDialog();
    }

    private void GridSystems_Click(object sender, RoutedEventArgs e) => OpenReferenceGeometry(levels: false);
    private void Levels_Click(object sender, RoutedEventArgs e) => OpenReferenceGeometry(levels: true);

    private void OpenReferenceGeometry(bool levels)
    {
        if (DataContext is not MainViewModel vm) return;
        new ReferenceGeometryWindow(vm, levels) { Owner = this }.ShowDialog();
    }

    // ---- the plane navigator ----

    private void PreviousPlane_Click(object sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.StepViewPlane(-1);
    private void NextPlane_Click(object sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.StepViewPlane(1);

    private void PlaneChoice_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitTypedPlane();
        e.Handled = true;
    }

    private void PlaneChoice_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitTypedPlane();

    /// <summary>A coordinate typed in the navigator: that plane, named or not ("8", "Z = 8" or a choice's own text).</summary>
    private void CommitTypedPlane()
    {
        if (DataContext is not MainViewModel vm || !vm.ViewPlaneIsPlane) return;
        var text = PlaneChoiceBox.Text?.Trim() ?? "";
        if (vm.ViewPlaneChoices.FirstOrDefault(choice => choice.Label == text) is { } choice) { vm.SelectedViewPlaneChoice = choice; return; }
        var number = text.Contains('=') ? text[(text.LastIndexOf('=') + 1)..].Replace("m", "").Trim() : text;
        if (double.TryParse(number, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
            double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            if (double.IsFinite(value)) vm.ViewCoordinate = value;
        }
        else if (text.Length > 0) vm.ReportEditing($"'{text}' is not a plane: choose a level or grid line, or type a coordinate in metres.");
    }
}
