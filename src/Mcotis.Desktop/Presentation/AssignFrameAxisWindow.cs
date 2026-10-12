using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace COT_VCF.Presentation;

/// <summary>Assigns one local 2/3-axis orientation to the frames selected in the model view.</summary>
public sealed class AssignFrameAxisWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly IReadOnlyList<string> members;
    private readonly TextBox angleBox = new() { Width = 100, TextAlignment = TextAlignment.Right };
    private readonly TextBlock status = new() { Foreground = Brushes.Firebrick, FontSize = 11, TextWrapping = TextWrapping.Wrap };

    public AssignFrameAxisWindow(MainViewModel viewModel, IReadOnlyList<string> members)
    {
        this.viewModel = viewModel;
        this.members = members;
        Title = "Assign Frame Local Axes";
        Icon = AppIcon.Window;
        Width = 460; Height = 260; MinWidth = 400; MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)FindResource("Surface");

        var current = viewModel.FemSectionProperties.Where(property => members.Contains(property.MemberName)).Select(property => property.LocalAngleDeg).Distinct().ToArray();
        if (current.Length == 1) angleBox.Text = current[0].ToString("0.###", CultureInfo.CurrentCulture);
        angleBox.ToolTip = "Absolute rotation about the frame's local 1-axis, in degrees. 90° swaps local axes 2 and 3.";
        System.Windows.Automation.AutomationProperties.SetName(angleBox, "Frame local-axis rotation in degrees");
        angleBox.TextChanged += (_, _) => status.Text = "";

        var content = new StackPanel { Margin = new Thickness(14) };
        content.Children.Add(new TextBlock
        {
            Text = $"{members.Count} frame{(members.Count == 1 ? "" : "s")} selected: {string.Join(", ", members.Take(8))}{(members.Count > 8 ? ", …" : "")}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });
        var angleRow = new StackPanel { Orientation = Orientation.Horizontal };
        angleRow.Children.Add(new TextBlock { Text = "Local-axis angle", Width = 140, VerticalAlignment = VerticalAlignment.Center });
        angleRow.Children.Add(angleBox);
        angleRow.Children.Add(new TextBlock { Text = "degrees", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(angleRow);
        content.Children.Add(new TextBlock
        {
            Text = "0° uses I33 for in-plane bending; 90° uses I22. The section and FEM update together.",
            Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 8),
        });
        content.Children.Add(status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => { if (Apply()) Close(); };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "Apply", MinWidth = 80 };
        apply.Click += (_, _) => Apply();
        buttons.Children.Add(ok); buttons.Children.Add(cancel); buttons.Children.Add(apply);

        var root = new Grid { Margin = new Thickness(10) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(content);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);
        Content = root;
    }

    private bool Apply()
    {
        if (!double.TryParse(angleBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var angle) || !double.IsFinite(angle))
        {
            status.Text = "Enter a finite angle in degrees, such as 0 or 90.";
            angleBox.Focus();
            return false;
        }
        try
        {
            viewModel.AssignFrameLocalAxis(members, angle);
            status.Text = viewModel.Message;
            return true;
        }
        catch (Exception ex)
        {
            status.Text = ex.Message;
            return false;
        }
    }
}
