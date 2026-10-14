using System.Windows;
using System.Windows.Controls;
using Mcotis.Core;

namespace Mcotis.Desktop.Presentation;

/// <summary>
/// Assign → Joint → Restraints (as SAP2000's Assign Joint Restraints): the six components held at the joints selected on
/// the model view — UX, UY, UZ, RX, RY, RZ — and the fast restraints Fixed, Pinned, Roller and Free. OK / Apply write
/// them to the model (<see cref="MainViewModel.AssignJointRestraints"/>); Close discards what was not applied. The
/// restraints are the model's own: nothing adds a support behind the user's back, and a joint made free is free.
/// </summary>
public sealed class JointRestraintWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly IReadOnlyList<string> joints;
    private readonly CheckBox[] boxes;
    private readonly TextBlock note = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    internal JointRestraintWindow(MainViewModel viewModel, IReadOnlyList<string> joints)
    {
        this.viewModel = viewModel;
        this.joints = joints;
        Title = "Assign Joint Restraints";
        Icon = AppIcon.Window;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface");

        string[] labels = ["Translation 1 (UX)", "Translation 2 (UY)", "Translation 3 (UZ)", "Rotation about 1 (RX)", "Rotation about 2 (RY)", "Rotation about 3 (RZ)"];
        boxes = labels.Select(label => new CheckBox { Content = label, Margin = new Thickness(0, 2, 18, 2) }).ToArray();
        foreach (var (box, index) in boxes.Select((box, index) => (box, index)))
            System.Windows.Automation.AutomationProperties.SetName(box, ((DofComponent)index).ToString());

        // what the joints hold now: shown when they all hold the same, else every box clear and the form says so
        var held = joints.Select(joint => viewModel.FemJointRestraints.GetValueOrDefault(joint, Restraint.Free)).Distinct().ToArray();
        if (held.Length == 1) Show(held[0]);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int row = 0; row < 3; row++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int index = 0; index < 6; index++)
        {
            Grid.SetRow(boxes[index], index % 3);
            Grid.SetColumn(boxes[index], index / 3);
            grid.Children.Add(boxes[index]);
        }
        var restraints = new GroupBox { Header = "Restraints in global directions", Content = grid, Padding = new Thickness(8) };

        var fast = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (text, restraint, tip) in new (string, Restraint, string)[]
                 {
                     ("Fixed", Restraint.Fixed, "All six components held"),
                     ("Pinned", Restraint.Pinned, "Translations held, rotations free"),
                     ("Roller", new Restraint(false, false, true, false, false, false), "UZ held only"),
                     ("Free", Restraint.Free, "Nothing held: the restraint is taken away"),
                 })
        {
            var button = new Button { Content = text, MinWidth = 70, Margin = new Thickness(0, 0, 6, 0), ToolTip = tip };
            button.Click += (_, _) => Show(restraint);
            fast.Children.Add(button);
        }
        var fastGroup = new GroupBox { Header = "Fast restraints", Content = fast, Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0) };

        var selection = new TextBlock
        {
            Text = $"{joints.Count} joint{(joints.Count == 1 ? "" : "s")}: {string.Join(", ", joints.Take(8))}{(joints.Count > 8 ? $" and {joints.Count - 8} more" : "")}" +
                   (held.Length > 1 ? " — they do not all hold the same restraint now" : ""),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 0, 0, 8),
        };
        note.MaxWidth = 420;
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        note.Text = "The model is analysed, drawn and exported on these restraints as they are. A joint held only out of the XZ plane (UY, RX, RZ) is restrained in the analysis but not drawn as a support.";

        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var apply = new Button { Content = "Apply", Width = 80, Margin = new Thickness(0, 0, 6, 0) };
        var close = new Button { Content = "Close", Width = 80, IsCancel = true };
        ok.Click += (_, _) => { if (ApplyForm()) Close(); };
        apply.Click += (_, _) => ApplyForm();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(apply); buttons.Children.Add(close);

        var root = new StackPanel { Margin = new Thickness(12) };
        foreach (var child in new UIElement[] { selection, restraints, fastGroup, note, buttons }) root.Children.Add(child);
        Content = root;
    }

    /// <summary>The restraint the boxes say.</summary>
    internal Restraint FormRestraint => new(boxes[0].IsChecked == true, boxes[1].IsChecked == true, boxes[2].IsChecked == true,
        boxes[3].IsChecked == true, boxes[4].IsChecked == true, boxes[5].IsChecked == true);

    internal void Show(Restraint restraint)
    {
        bool[] values = [restraint.Ux, restraint.Uy, restraint.Uz, restraint.Rx, restraint.Ry, restraint.Rz];
        for (int index = 0; index < 6; index++) boxes[index].IsChecked = values[index];
    }

    /// <summary>Writes the form to the model; false (and the reason shown) when the model refuses it.</summary>
    internal bool ApplyForm()
    {
        try
        {
            viewModel.AssignJointRestraints(joints, FormRestraint);
            note.Text = viewModel.Message;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            note.Text = ex.Message;
            note.SetResourceReference(TextBlock.ForegroundProperty, "Error");
            return false;
        }
    }
}
