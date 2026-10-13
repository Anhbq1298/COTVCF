using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Mcotis.Core;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Presentation;

/// <summary>Edits a private form. Only Apply/OK calls the supplied commit; Cancel discards uncommitted edits.</summary>
public partial class AnalysisOptionsWindow : Window
{
    private readonly Action<AnalysisSettings> commit;
    private readonly bool planar;
    private AnalysisSettings current;

    public AnalysisOptionsWindow(AnalysisSettings settings, Action<AnalysisSettings> commit, bool planar = false, bool editable = true)
    {
        this.commit = commit; this.planar = planar; current = settings;
        InitializeComponent();
        foreach (var box in Boxes) box.Click += (_, _) => Preset.SelectedIndex = 4;
        SetMask(planar ? DofMask.XZ : settings.ActiveDofs);
        Preset.SelectedIndex = settings.ActiveDofs switch { DofMask.Full3D => 0, DofMask.XZ => 1, DofMask.YZ => 2, DofMask.XY => 3, _ => 4 };
        Preset.IsEnabled = DofEditors.IsEnabled = CableDofEditors.IsEnabled = !planar && editable;
        // the main window's model is planar: its cable joints move in the plane
        var cableMask = planar ? settings.CableDofs & DofMask.XZ : settings.CableDofs;
        for (int i = 0; i < 6; i++) CableBoxes[i].IsChecked = (cableMask & (DofMask)(1 << i)) != 0;
        Tolerance.Text = settings.CableFitTolerancePercent.ToString(CultureInfo.CurrentCulture);
        Iterations.Text = settings.CableFitMaxIterations.ToString(CultureInfo.CurrentCulture);
        Mesh.Text = settings.FrameMeshSegments.ToString(CultureInfo.CurrentCulture);
        Sag.IsChecked = settings.CableSag;
        Tolerance.IsEnabled = Iterations.IsEnabled = Mesh.IsEnabled = Sag.IsEnabled = editable;
        ScopeText.Text = "Static analysis of frames and cables. Shell, P-Delta and modal analysis are not available.";
        CableScope.Text = "These settings are used by Run Analysis: the fitting when a load case fits cable target forces, cable sag for every case with cables. The mesh is the number of stations each frame is read at; it changes no joint result." +
            (planar ? " This window's model is analysed in its XZ plane; the run monitor shows the degrees of freedom of each run." : "");
        if (planar)
        {
            // The degrees of freedom are not a choice in the main window — its model is planar — so the tab would only
            // show boxes that cannot be changed (user, 2026-10-12: "tab này hơi thừa, nhất là cho cable").
            DofTab.Visibility = Visibility.Collapsed;
            Tabs.SelectedItem = FittingTab;
            MinHeight = 0;
            SizeToContent = SizeToContent.Height;
        }
        if (!editable) { ErrorText.Text = "Model locked or analysis running. Close this form and unlock / finish the run before editing."; }
        CanCommit = editable;
        OkButton.IsEnabled = ApplyButton.IsEnabled = editable;
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
            if (!planar)
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
