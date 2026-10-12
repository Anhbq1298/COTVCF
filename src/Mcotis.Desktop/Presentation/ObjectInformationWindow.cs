using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using COT_VCF.Domain;

namespace COT_VCF.Presentation;

/// <summary>
/// Frame / Cable / Point Information, as ETABS shows it for a right-clicked object: tabs Geometry, Assignments and Loads,
/// each a grid of name / value lines under section bands (<see cref="MainViewModel.MemberInformation"/>,
/// <see cref="MainViewModel.PointInformation"/>). The values can be selected and copied.
/// <para>It is also where a member's assignments are changed — one form, not an information form and an assignment form
/// (asked for by the user, 2026-10-12). While the model can be edited, the lines a member's information marks with an
/// <see cref="InformationEdit"/> are editors in place: section, local-axis angle and end moment releases of a frame, target
/// pattern and force of a cable. OK / Apply write them; a locked model shows the same lines read-only.</para>
/// </summary>
public sealed class ObjectInformationWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly StructuralMember? member;
    private readonly Func<IReadOnlyList<InformationTab>> information;
    private readonly TabControl tabs = new();
    private readonly TextBlock status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    // the editors of the tabs as they stand; made anew each time the tabs are filled
    private ComboBox? section, pattern;
    private TextBox? angle, force;
    private CheckBox? releaseI, releaseJ;
    // what the editors showed when they were made: only an editor the user changed is written to the model
    private string? shownSection;
    private string shownAngle = "", shownPattern = "", shownForce = "";
    private bool shownReleaseI, shownReleaseJ;

    /// <param name="shown">The geometry the member was picked in (the view's model); the model's own when omitted.</param>
    /// <param name="openAssignments">Open on the Assignments tab (a double-click), not on Geometry (a right-click).</param>
    public ObjectInformationWindow(MainViewModel viewModel, StructuralMember member, GeometryModel? shown = null, bool openAssignments = false)
        : this(viewModel, $"{(member.Kind == MemberKind.Frame ? "Frame" : member.Kind.ToString())} Information - {member.Name}",
            () => viewModel.MemberInformation(member, shown), member)
    {
        if (openAssignments) tabs.SelectedIndex = Math.Min(1, tabs.Items.Count - 1);
    }

    /// <param name="shown">The geometry the point was picked in (the view's model); the model's own when omitted.</param>
    public ObjectInformationWindow(MainViewModel viewModel, StructuralPoint point, GeometryModel? shown = null)
        : this(viewModel, $"Point Information - {point.Name}", () => viewModel.PointInformation(point, shown), null) { }

    private ObjectInformationWindow(MainViewModel viewModel, string title, Func<IReadOnlyList<InformationTab>> information, StructuralMember? member)
    {
        this.viewModel = viewModel;
        this.member = member;
        this.information = information;
        Title = title;
        Icon = AppIcon.Window;
        Width = 540; Height = 640; MinWidth = 420; MinHeight = 360;   // wide enough for a section name in its editor
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)FindResource("Surface");

        // compact tab headers, as on the other property forms (the theme's are sized for the main workflow)
        var header = new Style(typeof(TabItem), (Style)FindResource(typeof(TabItem)));
        header.Setters.Add(new Setter(HeightProperty, 26.0));
        header.Setters.Add(new Setter(WidthProperty, double.NaN));
        header.Setters.Add(new Setter(MinWidthProperty, 0.0));
        header.Setters.Add(new Setter(PaddingProperty, new Thickness(12, 0, 12, 0)));
        tabs.Resources.Add(typeof(TabItem), header);
        tabs.Background = Background;
        System.Windows.Automation.AutomationProperties.SetName(tabs, "Object information");
        Fill();

        // a member with assignments: OK / Cancel / Apply while the model can be edited, else Close under the reason
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (Editable)
        {
            var ok = new Button { Content = "OK", MinWidth = 76, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (_, _) => { if (Apply()) Close(); };
            var cancel = new Button { Content = "Cancel", MinWidth = 76, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
            cancel.Click += (_, _) => Close();
            var apply = new Button { Content = "Apply", MinWidth = 76, ToolTip = "Write the changed assignments to the model and keep this form open" };
            apply.Click += (_, _) => Apply();
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            buttons.Children.Add(apply);
            if (Follower) Say("This cable follows another cable: its force is set through its sync on 03 Pretension.", error: false);
        }
        else
        {
            var close = new Button { Content = HasAssignments ? "Close" : "OK", MinWidth = 84, IsDefault = true, IsCancel = true };
            close.Click += (_, _) => Close();
            buttons.Children.Add(close);
            if (HasAssignments) Say("Model locked. Unlock it to change member assignments.", error: true);
        }

        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        status.Margin = new Thickness(8, 0, 12, 0);
        footer.Children.Add(status);
        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(tabs);
        Content = root;
    }

    /// <summary>The tabs as shown (checks).</summary>
    internal IReadOnlyList<InformationTab> Shown { get; private set; } = [];
    internal string OpenTab => (tabs.SelectedItem as TabItem)?.Header as string ?? "";
    internal string Status => status.Text;
    internal ComboBox? SectionEditor => section;
    internal TextBox? AngleEditor => angle;
    internal CheckBox? ReleaseStartEditor => releaseI;
    internal CheckBox? ReleaseEndEditor => releaseJ;
    internal ComboBox? TargetPatternEditor => pattern;
    internal TextBox? TargetForceEditor => force;

    /// <summary>The object is a member that carries FEM assignments.</summary>
    private bool HasAssignments => member is not null && viewModel.FemSectionProperties.Any(item => item.MemberName == member.Name);
    /// <summary>Its assignments can be changed here: the model is unlocked.</summary>
    internal bool Editable => HasAssignments && viewModel.CanEditFemModel;
    private CablePretensionConfig? Target => member is { Kind: not MemberKind.Frame } ? viewModel.State.Cables.FirstOrDefault(item => item.CableName == member.Name) : null;
    private bool Follower => Target is { IsFollower: true };

    private void Say(string text, bool error)
    {
        status.Text = text;
        status.Foreground = error ? Brushes.Firebrick : (Brush)FindResource("Muted");
    }

    /// <summary>Builds the tabs from the model as it is now, keeping the tab that was open.</summary>
    private void Fill()
    {
        var open = Math.Max(0, tabs.SelectedIndex);
        Shown = information();
        section = pattern = null;
        angle = force = null;
        releaseI = releaseJ = null;
        tabs.Items.Clear();
        foreach (var tab in Shown) tabs.Items.Add(new TabItem { Header = tab.Title, Content = Sheet(tab) });
        tabs.SelectedIndex = Math.Min(open, tabs.Items.Count - 1);
    }

    /// <summary>The editor of an assignment line, with the value the model holds — or null where the line is only read:
    /// the model is locked, the object is not a member, or the cable takes its force from another cable.</summary>
    private FrameworkElement? Editor(InformationEdit edit)
    {
        if (edit == InformationEdit.None || member is null || !Editable) return null;
        var property = viewModel.FemSectionProperties.First(item => item.MemberName == member.Name);
        var cable = member.Kind != MemberKind.Frame;
        static StackPanel WithUnit(TextBox box, string unit) => new()
        {
            Orientation = Orientation.Horizontal,
            Children = { box, new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) } },
        };
        switch (edit)
        {
            case InformationEdit.Section:
                viewModel.EnsureSections();
                section = new ComboBox
                {
                    ItemsSource = (cable ? viewModel.CableSections.Select(item => item.Name) : viewModel.FrameSections.Select(item => item.Name)).ToArray(),
                    SelectedItem = property.SectionName,
                };
                shownSection = section.SelectedItem as string;
                section.SetBinding(ToolTipProperty, new System.Windows.Data.Binding(nameof(ComboBox.SelectedItem)) { Source = section });
                System.Windows.Automation.AutomationProperties.SetName(section, "Assigned section");
                return section;
            case InformationEdit.LocalAxisAngle when !cable:
                shownAngle = property.LocalAngleDeg.ToString("0.###", CultureInfo.CurrentCulture);
                angle = new TextBox { Width = 76, TextAlignment = TextAlignment.Right, Text = shownAngle };
                System.Windows.Automation.AutomationProperties.SetName(angle, "Frame local-axis angle in degrees");
                return WithUnit(angle, "° about local 1");
            case InformationEdit.ReleaseStart when !cable:
                shownReleaseI = property.ReleaseStartRotation;
                releaseI = new CheckBox { Content = "Released", IsChecked = shownReleaseI, VerticalAlignment = VerticalAlignment.Center };
                System.Windows.Automation.AutomationProperties.SetName(releaseI, "Release moment at End I");
                return releaseI;
            case InformationEdit.ReleaseEnd when !cable:
                shownReleaseJ = property.ReleaseEndRotation;
                releaseJ = new CheckBox { Content = "Released", IsChecked = shownReleaseJ, VerticalAlignment = VerticalAlignment.Center };
                System.Windows.Automation.AutomationProperties.SetName(releaseJ, "Release moment at End J");
                return releaseJ;
            case InformationEdit.TargetPattern when Target is { IsFollower: false } target:
                pattern = new ComboBox { ItemsSource = viewModel.State.LoadPatterns.ToArray(), SelectedItem = target.LoadPattern };
                shownPattern = target.LoadPattern;
                System.Windows.Automation.AutomationProperties.SetName(pattern, "Cable target load pattern");
                return pattern;
            case InformationEdit.TargetForce when Target is { IsFollower: false } target:
                // a range of 03 Pretension shows the value the model is analysed with, its first; typing another fixes the force
                shownForce = ((target.ApplyRange ? target.MinForce : target.TargetForce) ?? target.TargetForce)?.ToString("0.###", CultureInfo.CurrentCulture) ?? "";
                force = new TextBox { Width = 96, TextAlignment = TextAlignment.Right, Text = shownForce,
                    ToolTip = target.ApplyRange ? "03 Pretension holds a range for this cable; this is its first value. Another value here makes the force fixed." : null };
                System.Windows.Automation.AutomationProperties.SetName(force, "Cable target force in kilonewtons");
                return WithUnit(force, "kN");
            default:
                return null;
        }
    }

    /// <summary>Writes the editors the user changed to the model, as the assignment commands do (FEM results are cleared
    /// by a change), and shows the model as it then is. An editor left as it was is not written, so OK on a form that was
    /// only read changes nothing — re-assigning a section, for one, would take its properties from the library again.
    /// False when an entry is refused; the reason is under the tabs.</summary>
    internal bool Apply()
    {
        if (member is null || !HasAssignments) return true;
        if (!viewModel.CanEditFemModel) { Say("Unlock the model before changing assignments.", error: true); return false; }
        var cable = member.Kind != MemberKind.Frame;
        var sectionName = section?.SelectedItem as string;
        var sectionEdited = section is not null && sectionName != shownSection;
        if (sectionEdited && sectionName is null) { Say("Choose a defined section.", error: true); return false; }
        double angleValue = 0, forceValue = 0;
        var angleEdited = angle is not null && angle.Text.Trim() != shownAngle;
        if (angleEdited && (!double.TryParse(angle!.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out angleValue) || !double.IsFinite(angleValue)))
        {
            Say("Enter a finite local-axis angle in degrees.", error: true);
            angle.Focus();
            return false;
        }
        var releasesEdited = releaseI is not null && releaseJ is not null &&
            (releaseI.IsChecked == true != shownReleaseI || releaseJ.IsChecked == true != shownReleaseJ);
        // an untouched cable target stays what it is on 03 Pretension, a range included
        var patternName = pattern?.SelectedItem as string;
        var targetEdited = force is not null && pattern is not null && (force.Text.Trim() != shownForce || patternName != shownPattern);
        if (targetEdited && patternName is null) { Say("Choose a defined target load pattern.", error: true); pattern!.Focus(); return false; }
        if (targetEdited && (!double.TryParse(force!.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out forceValue) || !double.IsFinite(forceValue) || forceValue < 0))
        {
            Say("Enter a nonnegative target force in kN.", error: true);
            force.Focus();
            return false;
        }
        if (!sectionEdited && !angleEdited && !releasesEdited && !targetEdited) { Say("Assignments unchanged.", error: false); return true; }

        try
        {
            var changed = sectionEdited && viewModel.AssignSection([member.Name], sectionName!, cable) > 0;
            if (angleEdited) changed |= viewModel.AssignFrameLocalAxis([member.Name], angleValue) > 0;
            if (releasesEdited) changed |= viewModel.AssignFrameEndReleases(member.Name, releaseI!.IsChecked == true, releaseJ!.IsChecked == true);
            if (targetEdited) changed |= viewModel.AssignCableTarget(member.Name, patternName!, forceValue);
            Fill();
            Say(changed ? "Assignments updated. FEM results were cleared; rerun the analysis for new results." : "Assignments unchanged.", error: false);
            return true;
        }
        catch (Exception ex)
        {
            Say(ex.Message, error: true);
            return false;
        }
    }

    private ScrollViewer Sheet(InformationTab tab)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.40, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.60, GridUnitType.Star) });
        var line = (Brush)FindResource("Border");
        var band = (Brush)FindResource("Background");
        var muted = (Brush)FindResource("Muted");
        void Place(UIElement element, int column, int span = 1)
        {
            Grid.SetRow(element, grid.RowDefinitions.Count - 1);
            Grid.SetColumn(element, column);
            Grid.SetColumnSpan(element, span);
            grid.Children.Add(element);
        }
        foreach (var section in tab.Sections)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Place(new Border { Background = band, BorderBrush = line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(6, 4, 6, 4),
                Child = new TextBlock { Text = section.Title, FontWeight = FontWeights.SemiBold } }, 0, 2);
            foreach (var row in section.Rows)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = 24 });
                Place(new Border { BorderBrush = line, BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(14, 3, 6, 3),
                    Child = new TextBlock { Text = row.Name, Foreground = muted, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap } }, 0);
                if (Editor(row.Edit) is { } editor)
                {
                    Place(new Border { BorderBrush = line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(6, 2, 6, 2), Child = editor }, 1);
                    continue;
                }
                // a read-only box, not a label: the value can be selected and copied
                var value = new TextBox { Text = row.Value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, MinHeight = 0,
                    Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, IsTabStop = false };
                System.Windows.Automation.AutomationProperties.SetName(value, $"{section.Title} {row.Name}");
                Place(new Border { BorderBrush = line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(6, 3, 6, 3), Child = value }, 1);
            }
        }
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new Border { BorderBrush = line, BorderThickness = new Thickness(1, 1, 1, 0),
            Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Top, Child = grid } };
    }
}
