using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Mcotis.Core;

namespace Mcotis.Desktop.Presentation;

/// <summary>Assigns lumped translational mass and rotational mass moment of inertia to one native-model joint.</summary>
public sealed class NativeJointMassWindow : Window
{
    private sealed record PointChoice(ObjectId Id, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly ModelDocument document;
    private readonly ComboBox point = new();
    private readonly TextBox ux = new(), uy = new(), uz = new(), rx = new(), ry = new(), rz = new();
    private readonly TextBlock problem = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };

    public NativeJointMassDefinition? Definition { get; private set; }

    public NativeJointMassWindow(ModelDocument document)
    {
        this.document = document;
        Title = "Joint Mass"; Width = 430; Height = 480; MinWidth = 390; MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.CanResizeWithGrip;

        point.ItemsSource = document.Points.Values.OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => new PointChoice(item.Id, item.Name)).ToArray();
        point.SelectionChanged += (_, _) => LoadSelection();

        var root = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var clear = new Button { Content = "Clear mass", Width = 92, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Cancel", Width = 80, Margin = new Thickness(8, 12, 0, 0), IsCancel = true };
        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(8, 12, 0, 0), IsDefault = true };
        clear.Click += (_, _) => { foreach (var box in Fields) box.Text = "0"; };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(clear); buttons.Children.Add(cancel); buttons.Children.Add(ok);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);

        var form = new StackPanel();
        form.Children.Add(new TextBlock { Text = "JOINT MASS", Style = TryFindResource("Subheading") as Style });
        Add(form, "Joint", point);
        Add(form, "UX mass [t]", ux); Add(form, "UY mass [t]", uy); Add(form, "UZ mass [t]", uz);
        Add(form, "RX inertia [t·m²]", rx); Add(form, "RY inertia [t·m²]", ry); Add(form, "RZ inertia [t·m²]", rz);
        form.Children.Add(new TextBlock { Margin = new Thickness(0, 8, 0, 2), TextWrapping = TextWrapping.Wrap,
            Text = "Values are lumped directly at this joint. Zero in all six fields removes the assignment." });
        form.Children.Add(problem); root.Children.Add(form); Content = root;
        if (point.Items.Count > 0) point.SelectedIndex = 0;
    }

    private TextBox[] Fields => [ux, uy, uz, rx, ry, rz];

    private void LoadSelection()
    {
        if (point.SelectedItem is not PointChoice selected) return;
        var mass = document.JointMassAssignments.GetValueOrDefault(selected.Id);
        var values = mass?.Values ?? [0, 0, 0, 0, 0, 0];
        for (var i = 0; i < Fields.Length; i++) Fields[i].Text = values[i].ToString("G12", CultureInfo.InvariantCulture);
        problem.Text = "";
    }

    private static void Add(Panel panel, string label, Control input)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 2) });
        panel.Children.Add(input);
    }

    private void Accept()
    {
        if (point.SelectedItem is not PointChoice selected) { problem.Text = "The model has no joint to assign."; return; }
        var values = new double[6];
        for (var i = 0; i < Fields.Length; i++)
        {
            if (!double.TryParse(Fields[i].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) ||
                !double.IsFinite(values[i]) || values[i] < 0)
            { problem.Text = "Every mass or inertia must be a finite nonnegative number, using '.' as the decimal separator."; return; }
        }
        Definition = new(selected.Id, values[0], values[1], values[2], values[3], values[4], values[5]);
        DialogResult = true;
    }
}
