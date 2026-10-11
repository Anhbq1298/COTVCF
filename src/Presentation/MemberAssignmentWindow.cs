using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using COT_VCF.Domain;
using COT_VCF.Geometry;

namespace COT_VCF.Presentation;

/// <summary>Single-member assignment inspector opened from either model viewport.</summary>
public sealed class MemberAssignmentWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly StructuralMember member;
    private readonly ComboBox section = new() { MinWidth = 220 };
    private readonly ComboBox pattern = new() { MinWidth = 160 };
    private readonly TextBox angle = new() { Width = 90, TextAlignment = TextAlignment.Right };
    private readonly TextBox force = new() { Width = 110, TextAlignment = TextAlignment.Right };
    private readonly CheckBox releaseI = new() { Content = "Release moment at I" };
    private readonly CheckBox releaseJ = new() { Content = "Release moment at J" };
    private readonly TextBlock status = new() { Foreground = Brushes.Firebrick, FontSize = 11, TextWrapping = TextWrapping.Wrap };

    public MemberAssignmentWindow(MainViewModel viewModel, StructuralMember member)
    {
        this.viewModel = viewModel;
        this.member = member;
        var property = viewModel.FemSectionProperties.FirstOrDefault(item => item.MemberName == member.Name)
            ?? throw new InvalidOperationException($"Member '{member.Name}' has no FEM assignment.");
        var cable = member.Kind != MemberKind.Frame;
        var target = cable ? viewModel.State.Cables.FirstOrDefault(item => item.CableName == member.Name) : null;
        var editable = viewModel.CanEditFemModel;

        Title = $"Member Assignments - {member.Name}";
        Icon = AppIcon.Window;
        Width = 510; Height = cable ? 400 : 410; MinWidth = 460; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)FindResource("Surface");

        viewModel.EnsureSections();
        section.ItemsSource = cable
            ? viewModel.CableSections.Select(item => item.Name).ToArray()
            : viewModel.FrameSections.Select(item => item.Name).ToArray();
        section.SelectedItem = property.SectionName;
        section.IsEnabled = editable;
        System.Windows.Automation.AutomationProperties.SetName(section, "Assigned section");

        var form = new Grid { Margin = new Thickness(14) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(string label, UIElement value)
        {
            var index = form.RowDefinitions.Count;
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var caption = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 9) };
            Grid.SetRow(caption, index); Grid.SetRow(value, index); Grid.SetColumn(value, 1);
            if (value is FrameworkElement element) element.Margin = new Thickness(0, 0, 0, 9);
            form.Children.Add(caption); form.Children.Add(value);
        }
        static TextBlock ReadOnly(string value) => new() { Text = value, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Row("Member", ReadOnly($"{member.Name}  ·  {(cable ? "Cable" : "Frame")}"));
        Row("Joints I → J", ReadOnly($"{member.Start} → {member.End}"));
        Row("Section", section);
        Row("Material", ReadOnly(property.MaterialName ?? "—"));
        Row("Area", ReadOnly($"{property.AreaM2:0.######} m²"));

        if (!cable)
        {
            angle.Text = property.LocalAngleDeg.ToString("0.###", CultureInfo.CurrentCulture);
            angle.IsEnabled = editable;
            System.Windows.Automation.AutomationProperties.SetName(angle, "Frame local-axis angle in degrees");
            Row("Local-axis rotation", new StackPanel { Orientation = Orientation.Horizontal, Children = { angle,
                new TextBlock { Text = "deg about local 1", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0) } } });
            releaseI.IsChecked = property.ReleaseStartRotation;
            releaseJ.IsChecked = property.ReleaseEndRotation;
            releaseI.IsEnabled = releaseJ.IsEnabled = editable;
            Row("End moment releases", new StackPanel { Orientation = Orientation.Horizontal, Children = { releaseI, releaseJ } });
            Row("In-plane inertia", ReadOnly($"{property.InPlaneInertiaM4:0.########} m⁴"));
        }
        else if (target is not null)
        {
            pattern.ItemsSource = viewModel.State.LoadPatterns.ToArray();
            pattern.SelectedItem = target.LoadPattern;
            force.Text = target.TargetForce?.ToString("0.###", CultureInfo.CurrentCulture) ?? "";
            var canEditTarget = editable && !target.IsFollower;
            pattern.IsEnabled = force.IsEnabled = canEditTarget;
            System.Windows.Automation.AutomationProperties.SetName(pattern, "Cable target load pattern");
            System.Windows.Automation.AutomationProperties.SetName(force, "Cable target force in kilonewtons");
            Row("Target pattern", pattern);
            Row("Target force", new StackPanel { Orientation = Orientation.Horizontal, Children = { force,
                new TextBlock { Text = "kN", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0) } } });
            Row("Force sync", ReadOnly(target.SyncSummary));
        }
        else Row("Target force", ReadOnly("No cable target-force assignment. Configure this cable in 03 Pretension."));

        if (!editable) status.Text = "Model locked. Unlock it to change member assignments.";
        else if (target?.IsFollower == true) status.Text = "This cable follows another cable. Change its force sync in 03 Pretension.";

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0), IsEnabled = editable };
        ok.Click += (_, _) => { if (Apply()) Close(); };
        var cancel = new Button { Content = editable ? "Cancel" : "Close", MinWidth = 80, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        var apply = new Button { Content = "Apply", MinWidth = 80, IsEnabled = editable };
        apply.Click += (_, _) => Apply();
        buttons.Children.Add(ok); buttons.Children.Add(cancel); buttons.Children.Add(apply);

        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(form);
        status.Margin = new Thickness(14, 0, 14, 8);
        Grid.SetRow(status, 1); root.Children.Add(status);
        buttons.Margin = new Thickness(0, 0, 8, 4);
        Grid.SetRow(buttons, 2); root.Children.Add(buttons);
        Content = root;
    }

    internal bool Apply()
    {
        if (!viewModel.CanEditFemModel) { status.Text = "Unlock the model before changing assignments."; return false; }
        if (section.SelectedItem is not string name) { status.Text = "Choose a defined section."; return false; }
        var cable = member.Kind != MemberKind.Frame;
        double angleValue = 0, forceValue = 0;
        var target = cable ? viewModel.State.Cables.FirstOrDefault(item => item.CableName == member.Name) : null;
        if (!cable && (!double.TryParse(angle.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out angleValue) || !double.IsFinite(angleValue)))
        { status.Text = "Enter a finite local-axis angle in degrees."; angle.Focus(); return false; }
        if (target is { IsFollower: false } &&
            (!double.TryParse(force.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out forceValue) || !double.IsFinite(forceValue) || forceValue < 0))
        { status.Text = "Enter a nonnegative target force in kN."; force.Focus(); return false; }
        if (target is { IsFollower: false } && pattern.SelectedItem is not string)
        { status.Text = "Choose a defined target load pattern."; pattern.Focus(); return false; }

        try
        {
            var changed = viewModel.AssignSection([member.Name], name, cable) > 0;
            if (!cable)
            {
                changed |= viewModel.AssignFrameLocalAxis([member.Name], angleValue) > 0;
                changed |= viewModel.AssignFrameEndReleases(member.Name, releaseI.IsChecked == true, releaseJ.IsChecked == true);
            }
            else if (target is { IsFollower: false })
                changed |= viewModel.AssignCableTarget(member.Name, (string)pattern.SelectedItem, forceValue);
            status.Text = changed ? "Assignments updated. FEM results were cleared; rerun analysis for new results." : "Assignments unchanged.";
            status.Foreground = (Brush)FindResource("Muted");
            return true;
        }
        catch (Exception ex) { status.Text = ex.Message; status.Foreground = Brushes.Firebrick; return false; }
    }
}
