using System.Windows;
using Mcotis.Desktop.Presentation.Controls;

namespace Mcotis.Desktop.Presentation;

/// <summary>
/// Display → View Options, laid out like SAP2000's Display Options: the form is edited, then Apply / OK writes it to the
/// shared model view; Close discards unapplied changes. "Reset Form to Default Values" fills the form with the app defaults,
/// "Reset Form to Current Window Settings" with what the view shows now. Presentation only.
/// </summary>
public partial class ViewOptionsWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly Structural2DViewer viewer;

    public ViewOptionsWindow(MainViewModel viewModel, Structural2DViewer viewer)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.viewer = viewer;
        DataContext = viewModel;
        ResetToCurrent();
    }

    /// <summary>The display settings the form edits.</summary>
    private sealed record Settings(bool PointLabels, bool FrameLabels, bool CableLabels, bool MemberSizes,
        bool Dimensions, bool Loading, bool FrameMesh, bool FrameLocalAxes, string Plane, bool Extruded3D,
        bool AxialValues, bool Undeformed, bool DeflectionContour);

    private static readonly Settings Defaults = new(PointLabels: true, FrameLabels: false, CableLabels: false, MemberSizes: false,
        Dimensions: true, Loading: true, FrameMesh: false, FrameLocalAxes: false, Plane: "XZ", Extruded3D: true,
        AxialValues: true, Undeformed: true, DeflectionContour: true);

    private Settings Current() => new(viewer.ShowPointLabels, viewer.ShowFrameLabels, viewer.ShowCableLabels, viewModel.ShowFemMemberSizes,
        viewModel.ShowDimensions, viewModel.ShowFemLoads, viewModel.ShowFemFrameMesh, viewModel.ShowFrameLocalAxes, viewer.CurrentPlane,
        viewModel.ShowExtruded3D, viewModel.ShowFemAxialValues, viewer.ShowUndeformed, viewer.ShowDeflectionContour);

    private Settings Form() => new(PointLabels.IsChecked == true, FrameLabels.IsChecked == true, CableLabels.IsChecked == true, MemberSizes.IsChecked == true,
        Dimensions.IsChecked == true, Loading.IsChecked == true, FrameMesh.IsChecked == true, FrameLocalAxes.IsChecked == true,
        PlaneXY.IsChecked == true ? "XY" : PlaneYZ.IsChecked == true ? "YZ" : Plane3D.IsChecked == true ? "3D" : "XZ",
        Extruded3D.IsChecked == true, AxialValues.IsChecked == true, Undeformed.IsChecked == true, DeflectionContour.IsChecked == true);

    private void Show(Settings settings)
    {
        PointLabels.IsChecked = settings.PointLabels;
        FrameLabels.IsChecked = settings.FrameLabels;
        CableLabels.IsChecked = settings.CableLabels;
        MemberSizes.IsChecked = settings.MemberSizes;
        Dimensions.IsChecked = settings.Dimensions;
        Loading.IsChecked = settings.Loading;
        FrameMesh.IsChecked = settings.FrameMesh;
        FrameLocalAxes.IsChecked = settings.FrameLocalAxes;
        PlaneXY.IsChecked = settings.Plane == "XY";
        PlaneXZ.IsChecked = settings.Plane == "XZ";
        PlaneYZ.IsChecked = settings.Plane == "YZ";
        Plane3D.IsChecked = settings.Plane == "3D";
        Extruded3D.IsChecked = settings.Extruded3D;
        AxialValues.IsChecked = settings.AxialValues;
        Undeformed.IsChecked = settings.Undeformed;
        DeflectionContour.IsChecked = settings.DeflectionContour;
    }

    internal void ResetToDefaults() { Show(Defaults); ShowObjects(ObjectDefaults); }
    internal void ResetToCurrent() { Show(Current()); ShowObjects(CurrentObjects()); }

    /// <summary>What of the model and its references is drawn (geometry editing plan, Ctrl+W): a hidden kind is neither
    /// drawn nor picked, and stays in the model and its analysis.</summary>
    private sealed record ObjectSettings(bool Joints, bool Frames, bool Cables, bool GridLines, bool Levels, bool ReferenceLabels);
    private static readonly ObjectSettings ObjectDefaults = new(true, true, true, true, true, true);
    private ObjectSettings CurrentObjects() => new(viewModel.ShowJoints, viewModel.ShowFrames, viewModel.ShowCables,
        viewModel.ShowGridLines, viewModel.ShowLevels, viewModel.ShowReferenceLabels);
    private ObjectSettings FormObjects() => new(ShowJoints.IsChecked == true, ShowFrames.IsChecked == true, ShowCables.IsChecked == true,
        ShowGridLines.IsChecked == true, ShowLevels.IsChecked == true, ShowReferenceLabels.IsChecked == true);
    private void ShowObjects(ObjectSettings settings) =>
        (ShowJoints.IsChecked, ShowFrames.IsChecked, ShowCables.IsChecked, ShowGridLines.IsChecked, ShowLevels.IsChecked, ShowReferenceLabels.IsChecked) =
        (settings.Joints, settings.Frames, settings.Cables, settings.GridLines, settings.Levels, settings.ReferenceLabels);
    private void ApplyObjects()
    {
        var objects = FormObjects();
        (viewModel.ShowJoints, viewModel.ShowFrames, viewModel.ShowCables) = (objects.Joints, objects.Frames, objects.Cables);
        (viewModel.ShowGridLines, viewModel.ShowLevels, viewModel.ShowReferenceLabels) = (objects.GridLines, objects.Levels, objects.ReferenceLabels);
    }

    /// <summary>Writes the form to the model view.</summary>
    internal void ApplyForm()
    {
        ApplyObjects();
        var settings = Form();
        viewer.ShowPointLabels = settings.PointLabels;
        viewer.ShowFrameLabels = settings.FrameLabels;
        viewer.ShowCableLabels = settings.CableLabels;
        viewer.ShowUndeformed = settings.Undeformed;
        viewer.ShowDeflectionContour = settings.DeflectionContour;
        viewModel.ShowFemMemberSizes = settings.MemberSizes;
        viewModel.ShowDimensions = settings.Dimensions;
        viewModel.ShowFemLoads = settings.Loading;
        viewModel.ShowFemFrameMesh = settings.FrameMesh;
        viewModel.ShowFrameLocalAxes = settings.FrameLocalAxes;
        viewModel.ShowExtruded3D = settings.Extruded3D;
        viewModel.ShowFemAxialValues = settings.AxialValues;
        if (viewer.CurrentPlane != settings.Plane) viewer.ShowPlane(settings.Plane);
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e) => ResetToDefaults();
    private void ResetCurrent_Click(object sender, RoutedEventArgs e) => ResetToCurrent();
    private void Apply_Click(object sender, RoutedEventArgs e) => ApplyForm();
    private void Ok_Click(object sender, RoutedEventArgs e) { ApplyForm(); Close(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
