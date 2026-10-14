using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Mcotis.Desktop.Presentation;

/// <summary>Compact native editor for the supported first modal milestone: an Eigen case, a named lumped mass
/// source, and the requested count. Joint masses remain model assignments; this form decides whether the source
/// includes them.</summary>
public sealed class NativeModalSetupWindow : Window
{
    private readonly TextBox caseName = new(), sourceName = new(), modes = new();
    private readonly CheckBox selfMass = new() { Content = "Element self mass (ρAL × mass modifier)" };
    private readonly CheckBox additionalMass = new() { Content = "Assigned joint masses / rotational inertias" };
    private readonly TextBlock problem = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };

    public NativeModalDefinition? Definition { get; private set; }

    public NativeModalSetupWindow(NativeModalDefinition initial)
    {
        Title = "Modal Eigen Case"; Width = 470; Height = 410; MinWidth = 420; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.CanResizeWithGrip;
        caseName.Text = initial.CaseName; sourceName.Text = initial.MassSourceName;
        modes.Text = initial.RequestedModes.ToString(CultureInfo.InvariantCulture);
        selfMass.IsChecked = initial.IncludeSelfMass; additionalMass.IsChecked = initial.IncludeAdditionalMass;

        var root = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Width = 80, Margin = new Thickness(8, 12, 0, 0), IsCancel = true };
        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(8, 12, 0, 0), IsDefault = true };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(cancel); buttons.Children.Add(ok); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);

        var form = new StackPanel();
        form.Children.Add(new TextBlock { Text = "MODAL EIGEN CASE", Style = TryFindResource("Subheading") as Style });
        Add(form, "Case name", caseName); Add(form, "Mass source", sourceName);
        form.Children.Add(selfMass); form.Children.Add(additionalMass); Add(form, "Number of modes", modes);
        form.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 4), TextWrapping = TextWrapping.Wrap,
            Text = "Lumped mass only. The solver uses elastic frame stiffness. End-of-nonlinear/P-Delta stiffness and cable modes stay unavailable until a verified frozen tangent state exists."
        });
        form.Children.Add(problem); root.Children.Add(form); Content = root;
    }

    private static void Add(Panel panel, string label, Control input)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 3) });
        panel.Children.Add(input);
    }

    private void Accept()
    {
        string caseText = caseName.Text.Trim(), sourceText = sourceName.Text.Trim();
        if (caseText.Length == 0 || sourceText.Length == 0)
        { problem.Text = "Enter both a case name and a mass-source name."; return; }
        if (!int.TryParse(modes.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 1 || count > 400)
        { problem.Text = "Number of modes must be a whole number from 1 to 400."; return; }
        if (selfMass.IsChecked != true && additionalMass.IsChecked != true)
        { problem.Text = "Select at least one physical mass component. A model with no positive free mass is refused at Run."; return; }
        Definition = new(caseText, sourceText, selfMass.IsChecked == true, additionalMass.IsChecked == true, count);
        DialogResult = true;
    }
}
