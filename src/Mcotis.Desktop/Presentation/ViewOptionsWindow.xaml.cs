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
        bool Dimensions, bool Loading, bool FrameMesh, bool FrameLocalAxes, string Plane, bool AxialValues, bool Undeformed, bool DeflectionContour);

    private static readonly Settings Defaults = new(PointLabels: true, FrameLabels: false, CableLabels: false, MemberSizes: false,
        Dimensions: true, Loading: true, FrameMesh: false, FrameLocalAxes: false, Plane: "XZ", AxialValues: true, Undeformed: true, DeflectionContour: true);

    private Settings Current() => new(viewer.ShowPointLabels, viewer.ShowFrameLabels, viewer.ShowCableLabels, viewModel.ShowFemMemberSizes,
        viewModel.ShowDimensions, viewModel.ShowFemLoads, viewModel.ShowFemFrameMesh, viewModel.ShowFrameLocalAxes, viewer.CurrentPlane,
        viewModel.ShowFemAxialValues, viewer.ShowUndeformed, viewer.ShowDeflectionContour);

    private Settings Form() => new(PointLabels.IsChecked == true, FrameLabels.IsChecked == true, CableLabels.IsChecked == true, MemberSizes.IsChecked == true,
        Dimensions.IsChecked == true, Loading.IsChecked == true, FrameMesh.IsChecked == true, FrameLocalAxes.IsChecked == true,
        PlaneXY.IsChecked == true ? "XY" : PlaneYZ.IsChecked == true ? "YZ" : Plane3D.IsChecked == true ? "3D" : "XZ",
        AxialValues.IsChecked == true, Undeformed.IsChecked == true, DeflectionContour.IsChecked == true);

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
        AxialValues.IsChecked = settings.AxialValues;
        Undeformed.IsChecked = settings.Undeformed;
        DeflectionContour.IsChecked = settings.DeflectionContour;
    }

    internal void ResetToDefaults() => Show(Defaults);
    internal void ResetToCurrent() => Show(Current());

    /// <summary>Writes the form to the model view.</summary>
    internal void ApplyForm()
    {
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
        viewModel.ShowFemAxialValues = settings.AxialValues;
        if (viewer.CurrentPlane != settings.Plane) viewer.ShowPlane(settings.Plane);
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e) => ResetToDefaults();
    private void ResetCurrent_Click(object sender, RoutedEventArgs e) => ResetToCurrent();
    private void Apply_Click(object sender, RoutedEventArgs e) => ApplyForm();
    private void Ok_Click(object sender, RoutedEventArgs e) { ApplyForm(); Close(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
