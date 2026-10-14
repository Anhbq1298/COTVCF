using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Mcotis.Core;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Presentation;

/// <summary>Edits a private form. Only Apply/OK calls the supplied commit; Cancel discards uncommitted edits.
/// <para><c>planar</c> is the main window, whose model lies in one XZ plane. Its Degrees of Freedom tab is the same
/// as the native workspace's — the presets, the six boxes, those of cable joints, every one a box that can be changed
/// (user, 2026-10-12, of a tab of boxes that were all disabled: "tab này hơi thừa"; 2026-10-13: set them freely
/// rather than fixed, "if not proper then report in the log", and "make the planar use 3d as 3d is default"). So the
/// form refuses no set the model might not be analysable with: the run does, in the run monitor. Only the notes
/// differ: they say what full 3D means for a model in a plane.</para>
/// <para><c>cables</c>: the model has cables. The settings of the cable force fitting — tolerance, iterations, cable
/// sag — are shown only then (user, 2026-10-13); without cables the second tab is "Frame Mesh" and holds the mesh
/// alone. The settings that are not shown keep the values they have.</para></summary>
public partial class AnalysisOptionsWindow : Window
{
    private readonly Action<AnalysisSettings> commit;
    private readonly bool planar;
    private AnalysisSettings current;

    public AnalysisOptionsWindow(AnalysisSettings settings, Action<AnalysisSettings> commit, bool planar = false, bool editable = true, bool cables = true)
    {
        this.commit = commit; this.planar = planar; current = settings;
        InitializeComponent();
        foreach (var box in Boxes) box.Click += (_, _) => Preset.SelectedIndex = 4;
        SetMask(settings.ActiveDofs);
        Preset.SelectedIndex = settings.ActiveDofs switch { DofMask.Full3D => 0, DofMask.XZ => 1, DofMask.YZ => 2, DofMask.XY => 3, _ => 4 };
        Preset.IsEnabled = DofEditors.IsEnabled = CableDofEditors.IsEnabled = editable;
        for (int i = 0; i < 6; i++) CableBoxes[i].IsChecked = (settings.CableDofs & (DofMask)(1 << i)) != 0;
        Tolerance.Text = settings.CableFitTolerancePercent.ToString(CultureInfo.CurrentCulture);
        Iterations.Text = settings.CableFitMaxIterations.ToString(CultureInfo.CurrentCulture);
        Mesh.Text = settings.FrameMeshSegments.ToString(CultureInfo.CurrentCulture);
        Sag.IsChecked = settings.CableSag;
        Tolerance.IsEnabled = Iterations.IsEnabled = Mesh.IsEnabled = Sag.IsEnabled = editable;
        ScopeText.Text = "Static analysis of frames and cables. Modal Eigen is available for native frame models; shell and P-Delta analysis are not available.";
        CableScope.Text = (cables
                ? "These settings are used by Run Analysis: the fitting when a load case fits cable target forces, cable sag for every case with cables. The mesh is the number of stations each frame is read at; it changes no joint result."
                : "The mesh is the number of stations each frame is read at by Run Analysis; it changes no joint result. The model has no cables, so the settings of the cable force fitting are not shown.") +
            (planar ? " The Run Monitor shows the degrees of freedom of each run." : "");
        if (!cables) HideCableSettings();
        if (planar)
        {
            // The same choices as the native workspace. What differs is what they mean for a model that lies in a plane.
            DofNote.Text = "A degree of freedom that is off is held at every joint, as by a support — not released — and a load on it is refused, not dropped. " +
                "This model lies in its XZ plane on pin supports that are all on one line: in Full 3D nothing holds it out of that plane. " +
                "Its loads act in the plane and do no work on that movement, so the run holds it, solves, and lists it in the Run Monitor as a warning; " +
                "the results are those of the XZ plane. A set the model cannot be analysed with is not refused here: the run reports it, with the reason.";
            CableDofNote.Text = "A joint that only cables reach has these, among those that are on. A cable gives stiffness along its own line only: " +
                "a movement left on that nothing else holds makes the model unstable, and the Run Monitor names the joint. " +
                "Switching it off here holds it at those joints; the log of the run says where a cable pulls against that hold.";
            MinHeight = 0;
            SizeToContent = SizeToContent.Height;
        }
        if (!editable) { ErrorText.Text = "Model locked or analysis running. Close this form and unlock / finish the run before editing."; }
        CanCommit = editable;
        OkButton.IsEnabled = ApplyButton.IsEnabled = editable;
    }

    /// <summary>A model without cables: the tab is the frame mesh alone. The boxes that go keep their values, so
    /// Apply writes the settings back as they were.</summary>
    private void HideCableSettings()
    {
        FittingTab.Header = "Frame Mesh";
        ToleranceRow.Height = IterationsRow.Height = SagRow.Height = new GridLength(0);
        ToleranceLabel.Visibility = Tolerance.Visibility = IterationsLabel.Visibility = Iterations.Visibility = Sag.Visibility = CableTargetNote.Visibility = Visibility.Collapsed;
    }

    private bool CanCommit { get; }
    private CheckBox[] Boxes => [UX, UY, UZ, RX, RY, RZ];
    private CheckBox[] CableBoxes => [CableUX, CableUY, CableUZ, CableRX, CableRY, CableRZ];
    private void SetMask(DofMask mask)
    {
        for (int i = 0; i < 6; i++) Boxes[i].IsChecked = (mask & (DofMask)(1 << i)) != 0;
    }
    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (UX is null || Preset.SelectedItem is not ComboBoxItem { Tag: string tag } || tag == "None") return;
        SetMask(Enum.Parse<DofMask>(tag));
    }
    internal bool ApplyForm()
    {
        if (!CanCommit) return false;
        try
        {
            DofMask mask = DofMask.None;
            for (int i = 0; i < 6; i++) if (Boxes[i].IsChecked == true) mask |= (DofMask)(1 << i);
            if ((mask & DofMask.Translations) == 0) throw new ArgumentException("Select at least one translational DOF.");
            var next = current with { ActiveDofs = mask };
            {
                DofMask cable = DofMask.None;
                for (int i = 0; i < 6; i++) if (CableBoxes[i].IsChecked == true) cable |= (DofMask)(1 << i);
                if ((cable & DofMask.Translations) == 0) throw new ArgumentException("Select at least one translational DOF for cable joints.");
                next = next with { CableDofs = cable };
            }
            {
                if (!double.TryParse(Tolerance.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var tolerance) ||
                    !double.IsFinite(tolerance) || tolerance <= 0 || tolerance >= 100)
                    throw new ArgumentException("Cable fitting tolerance must be greater than 0% and less than 100%.");
                if (!int.TryParse(Iterations.Text, out var iterations) || iterations < 1 || iterations > 10000)
                    throw new ArgumentException("Enter between 1 and 10,000 fitting iterations.");
                if (!int.TryParse(Mesh.Text, out var mesh) || mesh < 1 || mesh > Application.FemSolver.MaximumFrameMeshSegments)
                    throw new ArgumentException($"Enter between 1 and {Application.FemSolver.MaximumFrameMeshSegments} mesh segments per frame.");
                next = next with { CableFitTolerancePercent = tolerance, CableFitMaxIterations = iterations, FrameMeshSegments = mesh, CableSag = Sag.IsChecked == true };
            }
            if (next != current) commit(next);
            current = next;
            ErrorText.Text = "Applied.";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DocumentException)
        { ErrorText.Text = ex.Message; return false; }
    }
    private void Apply_Click(object sender, RoutedEventArgs e) => ApplyForm();
    private void Ok_Click(object sender, RoutedEventArgs e) { if (ApplyForm()) DialogResult = true; }
}
