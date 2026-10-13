using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Mcotis.Core;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Presentation;

/// <summary>
/// Define → Load Cases → + Add / Modify… (as SAP2000's Load Case Data): the name of a load case, its type, and what
/// belongs to the type. A nonlinear case names the case it continues from; a linear or modal case names the case whose
/// end stiffness it uses — two references, shown in the same place under their own label and never mixed. A static
/// case has its Loads Applied, a load pattern of the model with a scale factor per row; a modal case has a mass source
/// and a number of modes instead. The form is staged: OK writes the case to the Load Cases table (a new case is added
/// there), Cancel leaves it as it was.
/// <para>Every type can be defined and saved. One that is not solved yet says so in the form, with the reason Run
/// gives when it refuses the case (docs/analysis/LOAD_CASE_STATE_REFACTOR_PLAN.md).</para>
/// </summary>
public partial class LoadCaseWindow : Window
{
    /// <summary>One load of the case: a load pattern of the model with its scale factor.</summary>
    internal sealed class LoadRow(string pattern, string factor, IReadOnlyList<string> choices, Func<string, string> carries, Action changed) : Observable
    {
        private string pattern = pattern, factor = factor;
        public IReadOnlyList<string> Choices { get; } = choices;
        public string Pattern
        {
            get => pattern;
            set { if (Set(ref pattern, value ?? "")) { Notify(nameof(Carries)); changed(); } }
        }
        /// <summary>The scale factor as typed; <see cref="Value"/> is null while it does not read as a number.</summary>
        public string Factor { get => factor; set { if (Set(ref factor, value ?? "")) changed(); } }
        public double? Value => Number(factor);
        /// <summary>What the pattern carries now (the Assigned loads of Define → Load Patterns).</summary>
        public string Carries => carries(pattern);
    }

    private const int DefaultModes = 12;
    /// <summary>What the boxes of a case without iteration controls of its own show, greyed: where ticking the box starts from.</summary>
    private static readonly StaticSolveControls DefaultControls = new(1, 50, 1e-6, 1e-6);

    private readonly MainViewModel viewModel;
    private readonly FemCaseRow? existing;      // the case being modified; null for a new one
    private readonly ObservableCollection<LoadRow> loads = [];
    private readonly IReadOnlyList<string> patterns;
    private readonly bool ready;

    internal IReadOnlyList<LoadRow> Loads => loads;
    /// <summary>What keeps OK from writing the case, or null.</summary>
    internal string? Problem { get; private set; }
    /// <summary>The case as OK wrote it.</summary>
    internal FemCaseRow? Saved { get; private set; }

    /// <param name="row">A case of the Load Cases table to modify, or a proposed new one (<see cref="MainViewModel.NewFemLoadCase"/>).</param>
    public LoadCaseWindow(MainViewModel viewModel, FemCaseRow row)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        existing = viewModel.FemCaseRows.Contains(row) ? row : null;
        if (existing is null) Title += " — New";
        var comparer = StringComparer.OrdinalIgnoreCase;
        var own = viewModel.FemLoadCaseLoads(row);
        var defined = viewModel.FemPatternRows.Select(item => item.Name).ToArray();
        // a pattern the definition names but the model does not have stays listed, so its row shows it (and says so)
        patterns = [.. defined, .. own.Select(load => load.Pattern).Where(name => !defined.Contains(name, comparer)).Distinct(comparer)];

        var options = row.Options;
        NameBox.Text = row.Name;
        TypeBox.ItemsSource = FemCaseOptions.Kinds.Select(FemCaseOptions.KindName).ToArray();
        TypeBox.SelectedIndex = Math.Max(0, FemCaseOptions.Kinds.ToList().IndexOf(options.Kind));
        PurposeBox.SelectedIndex = row.CableTargetForces ? 1 : 0;
        GeometricBox.SelectedIndex = options.GeometricMode == GeometricNonlinearity.PDelta ? 1 : 0;
        var masses = viewModel.FemMassSourceChoices;
        MassSourceBox.ItemsSource = masses;
        MassSourceBox.SelectedItem = masses.FirstOrDefault(name => comparer.Equals(name, options.MassSource));
        ModesBox.Text = (options.RequestedModes ?? DefaultModes).ToString(CultureInfo.CurrentCulture);
        var controls = options.Controls ?? DefaultControls;
        ControlsBox.IsChecked = options.Controls is not null;
        StepsBox.Text = controls.MaxLoadSteps.ToString(CultureInfo.CurrentCulture);
        IterationsBox.Text = controls.MaxIterationsPerStep.ToString(CultureInfo.CurrentCulture);
        ResidualBox.Text = controls.RelativeResidualTolerance.ToString("0.###E+0", CultureInfo.CurrentCulture);
        CorrectionBox.Text = controls.RelativeDisplacementTolerance.ToString("0.###E+0", CultureInfo.CurrentCulture);
        foreach (var (pattern, factor) in own)
            loads.Add(NewRow(patterns.First(name => comparer.Equals(name, pattern)), factor.ToString("0.###", CultureInfo.InvariantCulture)));
        LoadGrid.ItemsSource = loads;
        ShowType(options.ProducesState ? row.InitialCase : options.StiffnessSourceCase);
        ready = true;
        Refresh();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    /// <summary>The type of the case.</summary>
    internal CaseKind Kind
    {
        get => FemCaseOptions.Kinds[Math.Max(0, TypeBox.SelectedIndex)];
        set => TypeBox.SelectedIndex = FemCaseOptions.Kinds.ToList().IndexOf(value);
    }

    private bool Nonlinear => Kind is CaseKind.NonlinearStatic or CaseKind.InitialPDelta;

    /// <summary>The case named under "Continue from" (a nonlinear case) or "Stiffness from" (a linear or modal case);
    /// "" for none — the case starts from zero, or uses the unstressed stiffness.</summary>
    internal string Reference
    {
        get => ReferenceBox.SelectedIndex > 0 ? (string)ReferenceBox.SelectedItem : "";
        set => ReferenceBox.SelectedIndex = Math.Max(0, ReferenceBox.Items.IndexOf(value));
    }

    /// <summary>The mass source of a modal case.</summary>
    internal string? MassSource { get => MassSourceBox.SelectedItem as string; set => MassSourceBox.SelectedItem = value; }

    // 1.35 is read as typed in the Definition column, and as the user's own regional format writes it
    private static double? Number(string text) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
         double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && double.IsFinite(value) ? value : null;

    private static int? Whole(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) ||
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : null;

    private LoadRow NewRow(string pattern, string factor) => new(pattern, factor, patterns, Carries, Refresh);

    private string Carries(string pattern) =>
        pattern.Length == 0 ? ""
        : viewModel.FemPatternRows.FirstOrDefault(item => item.Name.Equals(pattern, StringComparison.OrdinalIgnoreCase)) is { } row ? row.Loads
        : "not a load pattern of the model";

    /// <summary>Shows what belongs to the type that is chosen, and nothing else: a field that is not shown has no part
    /// in the case (<see cref="Options"/>), whatever it held under another type.</summary>
    private void ShowType(string? reference)
    {
        var kind = Kind;
        bool nonlinear = Nonlinear, modal = kind == CaseKind.ModalEigen;
        // one list for both references: only a nonlinear case ends in a state, or in a stiffness, that another can take
        var producers = viewModel.StateProducerChoicesFor(existing).Skip(1).ToArray();
        ReferenceLabel.Text = nonlinear ? "Continue from" : "Stiffness from";
        ReferenceBox.ItemsSource = new[] { nonlinear ? "(zero)" : "(unstressed)" }.Concat(producers).ToArray();
        ReferenceBox.SelectedIndex = Array.FindIndex(producers, name => name.Equals(reference, StringComparison.OrdinalIgnoreCase)) + 1;
        ReferenceBox.ToolTip = nonlinear
            ? "The nonlinear case this one continues from: it starts from that case's end state, and its own loads are added to the loads already there. (zero) = it starts unstressed."
            : "The nonlinear case whose end stiffness this case uses. Only the stiffness is taken: the loads of that case are not applied again. (unstressed) = the stiffness of the unloaded structure.";
        GeometricLabel.Visibility = GeometricBox.Visibility = nonlinear ? Visibility.Visible : Visibility.Collapsed;
        GeometricBox.IsEnabled = kind == CaseKind.NonlinearStatic;   // an initial P-Delta case is P-Delta
        if (kind == CaseKind.InitialPDelta) GeometricBox.SelectedIndex = 1;
        PurposeLabel.Visibility = PurposeBox.Visibility = kind == CaseKind.NonlinearStatic ? Visibility.Visible : Visibility.Collapsed;
        ControlsPanel.Visibility = nonlinear ? Visibility.Visible : Visibility.Collapsed;
        ModalPanel.Visibility = modal ? Visibility.Visible : Visibility.Collapsed;
        // the one mass source there is needs no choosing
        if (modal && MassSourceBox.SelectedItem is null && MassSourceBox.Items.Count == 1) MassSourceBox.SelectedIndex = 0;
        LoadsPanel.Visibility = modal ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The cable target forces are fitted in this case — a nonlinear static case only.</summary>
    private bool FitsCableTargets => Kind == CaseKind.NonlinearStatic && PurposeBox.SelectedIndex == 1;

    /// <summary>What the form holds for the type that is chosen.</summary>
    private FemCaseOptions Options() => new(Kind,
        Kind == CaseKind.InitialPDelta || Kind == CaseKind.NonlinearStatic && GeometricBox.SelectedIndex == 1 ? GeometricNonlinearity.PDelta : GeometricNonlinearity.None,
        Nonlinear || Reference.Length == 0 ? null : Reference,
        Nonlinear && ControlsBox.IsChecked == true
            ? new(Whole(StepsBox.Text) ?? 0, Whole(IterationsBox.Text) ?? 0, Number(ResidualBox.Text) ?? double.NaN, Number(CorrectionBox.Text) ?? double.NaN) : null,
        Kind == CaseKind.ModalEigen ? MassSource : null,
        Kind == CaseKind.ModalEigen ? Whole(ModesBox.Text) ?? 0 : null);

    private IReadOnlyList<(string Pattern, double Factor)> EnteredLoads() => Kind == CaseKind.ModalEigen ? []
        : loads.Select(row => (row.Pattern, row.Value ?? double.NaN)).ToArray();

    /// <summary>The form as it stands: what keeps it from being written, whether Run solves such a case, and
    /// everything on the structure at the end of the case.</summary>
    private void Refresh()
    {
        if (!ready) return;
        var comparer = StringComparer.OrdinalIgnoreCase;
        var options = Options();
        Problem = viewModel.FemLoadCaseProblem(existing, NameBox.Text, Nonlinear ? Reference : "", EnteredLoads(), options, FitsCableTargets);
        ProblemText.Text = Problem ?? "";
        OkButton.IsEnabled = Problem is null;
        ReferenceHint.Text = Nonlinear ? Reference.Length == 0 ? "starts unstressed" : "its loads stay on the structure"
            : Reference.Length == 0 ? "the stiffness of the unloaded structure" : "only its end stiffness is used; its loads are not applied again";
        // a case that can be defined but is not solved yet: the reason Run gives, said before Run
        string? pending = options.NotSolvedYet(string.IsNullOrWhiteSpace(NameBox.Text) ? "(new case)" : NameBox.Text.Trim());
        AvailabilityPanel.Visibility = pending is null ? Visibility.Collapsed : Visibility.Visible;
        AvailabilityText.Text = pending is null ? "" : "The case can be defined and saved; Run does not solve it yet. " + pending;

        var own = loads.Where(row => row.Pattern.Length > 0 && row.Value is not null).Select(row => (row.Pattern, row.Value!.Value)).ToArray();
        // a nonlinear case adds its loads to those of the cases it continues from; a linear case has its own alone
        var total = viewModel.FemLoadCaseTotal(Nonlinear ? Reference : "", own);
        TotalText.Text = total.Count == 0 ? "" : (Nonlinear ? "At the end of this case: " : "The loads of this case: ") + FemLoadDefinition.Format(total);
        TotalText.ToolTip = Nonlinear ? "Everything on the structure at the end of this case: the loads of the cases it continues from, then its own."
            : "A linear case answers to its own loads alone, on the stiffness it uses.";
        var free = patterns.Any(pattern => !loads.Any(row => comparer.Equals(row.Pattern, pattern)));
        AddLoadButton.IsEnabled = free;
        AddLoadButton.ToolTip = free ? "Add a load pattern to this case, then choose it and its scale factor in the row"
            : "Every load pattern of the model is in the case. Define another on Define → Load Patterns.";
    }

    /// <summary>Adds a load to the case: the first load pattern it does not hold yet, with a scale factor of 1.</summary>
    internal LoadRow AddLoad()
    {
        var used = loads.Select(row => row.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var row = NewRow(patterns.FirstOrDefault(pattern => !used.Contains(pattern)) ?? "", "1");
        loads.Add(row);
        LoadGrid.SelectedItem = row;
        LoadGrid.ScrollIntoView(row);
        Refresh();
        return row;
    }

    internal void RemoveLoad(LoadRow row)
    {
        if (loads.Remove(row)) Refresh();
    }

    /// <summary>Writes the case to the Load Cases table; false while the form holds a problem.</summary>
    internal bool Accept()
    {
        Refresh();
        if (Problem is not null) return false;
        Saved = viewModel.SaveFemLoadCase(existing, NameBox.Text, Nonlinear ? Reference : "", EnteredLoads(), Options(), FitsCableTargets);
        return Saved is not null;
    }

    private void Type_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        // the case that was named stays named where the new type can name it: both references are to a nonlinear case
        ShowType(Reference);
        Refresh();
    }
    private void Input_Changed(object sender, RoutedEventArgs e) => Refresh();
    private void AddLoad_Click(object sender, RoutedEventArgs e) => AddLoad();
    private void RemoveLoad_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LoadRow row }) RemoveLoad(row);
    }
    private void Ok_Click(object sender, RoutedEventArgs e) { if (Accept()) DialogResult = true; }
}
