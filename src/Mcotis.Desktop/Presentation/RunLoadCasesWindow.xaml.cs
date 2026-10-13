using System.Windows;
using Mcotis.Analysis.Cases;
using Mcotis.Core;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Presentation;

/// <summary>
/// Run → Run FEM Analysis opens this first (as SAP2000's Set Load Cases to Run): the load cases and combinations of
/// Define → Load Cases with their action, Run or Do Not Run, and the run plan those actions make
/// (<see cref="CaseExecutionCoordinator"/>): which cases the run solves, what each one needs first, and which of them
/// is solved only because another needs it. Run Now keeps the choice and starts the analysis of the model as it is
/// now; OK keeps the choice only; Cancel discards it.
/// <para>A case of a type that is not solved yet can be set to Run: the run then stops at it with the reason. It is
/// never left out without a word, and never solved as a simpler case.</para>
/// </summary>
public partial class RunLoadCasesWindow : Window
{
    /// <summary>One load case or combination with its staged action, and its place in the plan of the run.</summary>
    internal sealed class RunItem(FemDefinitionRow row, string status) : Observable
    {
        private bool run = row.Run;
        private string role = "", dependsOn = "", availability = "";
        public FemDefinitionRow Row { get; } = row;
        public string Name => Row.Name;
        public string Type => Row is FemCaseRow loadCase ? loadCase.CaseType : Row.Type;
        /// <summary>The case a nonlinear case continues from, or the case whose stiffness a linear or modal case uses.</summary>
        public string From => Row is not FemCaseRow loadCase ? "" : loadCase.Options.ProducesState ? loadCase.InitialCase : loadCase.Options.StiffnessSourceCase ?? "";
        public string Status { get; } = status;
        public bool Supported => Row.Supported;
        public bool Run
        {
            get => run;
            set { if (Set(ref run, value && Supported)) Notify(nameof(Action)); }
        }
        public string Action => !Supported ? "Not available" : Run ? "Run" : "Do Not Run";
        /// <summary>In the plan of the run: asked for, needed by another (a prerequisite), or a result added up from others.</summary>
        public string Role { get => role; set => Set(ref role, value); }
        public string DependsOn { get => dependsOn; set => Set(ref dependsOn, value); }
        public string Availability { get => availability; set => Set(ref availability, value); }
    }

    private readonly MainViewModel viewModel;
    private FemCaseExecutionPlan plan = new([], [], []);
    internal IReadOnlyList<RunItem> Items { get; }

    public RunLoadCasesWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        var solved = viewModel.SelectedFemRun?.Results;
        Items = viewModel.FemCaseRows.Cast<FemDefinitionRow>().Concat(viewModel.FemCombinationRows)
            .Select(row => new RunItem(row, !row.Supported ? "" : solved?.ContainsKey(row.Name) == true ? "Finished" : "Not Run")).ToArray();
        CaseGrid.ItemsSource = Items;
        CaseGrid.SelectedItem = Items.FirstOrDefault(item => item.Supported);
        ShowPlan();
    }

    /// <summary>The plan of the run with the actions as they are staged now.</summary>
    private void ShowPlan()
    {
        plan = CaseExecutionCoordinator.Plan(viewModel.FemCaseRows.ToArray(), viewModel.FemCombinationRows.ToArray(),
            viewModel.FemMassSourceReferences, Items.Where(item => item.Run).Select(item => item.Row.Id));
        foreach (var item in Items)
        {
            (item.Role, item.DependsOn) = ("", "");
            item.Availability = !item.Supported ? "Not supported"
                : item.Row is FemCaseRow loadCase && loadCase.Options.NotSolvedYet(loadCase.Name) is not null ? FemCaseOptions.Unavailable : "Available";
        }
        foreach (var planned in plan.Cases)
        {
            var item = Items.Single(entry => ReferenceEquals(entry.Row, planned.Row));
            item.Role = planned.Role == CaseRunRole.Requested ? "Requested" : "Prerequisite";
            item.DependsOn = string.Join("; ", planned.Dependencies.Select(dependency => dependency.Kind switch
            {
                CaseDependencyKind.ContinueState => $"continues {dependency.Source}",
                CaseDependencyKind.FrozenStiffness => $"stiffness of {dependency.Source}",
                _ => $"mass {dependency.Source}",
            }));
        }
        foreach (var combination in plan.Combinations)
            Items.Single(item => ReferenceEquals(item.Row, combination)).Role = "Result";
        ShowHint();
    }

    private void ShowHint()
    {
        var run = Items.Count(item => item.Run);
        int prerequisites = plan.Cases.Count(item => item.Role == CaseRunRole.Prerequisite);
        var pending = plan.Cases.Where(item => item.UnavailableReason is not null).Select(item => item.Row.Name).ToArray();
        Hint.Text = Items.Count == 0 ? "No load case is defined — Define → Load Cases → Default Setup."
            : run == 0 ? "Set at least one load case to Run."
            : !plan.IsValid ? plan.Issues[0]
            : $"{run} of {Items.Count(item => item.Supported)} set to Run" +
              (prerequisites > 0 ? $" · {prerequisites} more solved first, as {(prerequisites == 1 ? "a prerequisite" : "prerequisites")}" : "") +
              (pending.Length > 0 ? $" · '{pending[0]}' is not solved yet: the run stops there with the reason" : "") + ".";
        RunNowButton.IsEnabled = run > 0 && plan.IsValid;
    }

    internal void Toggle(RunItem? item)
    {
        if (item is null || !item.Supported) return;
        item.Run = !item.Run;
        ShowPlan();
    }

    /// <summary>Sets every case to Run, or — when all already are — every case to Do Not Run.</summary>
    internal void ToggleAll()
    {
        var available = Items.Where(item => item.Supported).ToArray();
        var run = available.Any(item => !item.Run);
        foreach (var item in available) item.Run = run;
        ShowPlan();
    }

    /// <summary>Writes the staged actions to the load case table.</summary>
    internal void Commit()
    {
        foreach (var item in Items) item.Row.Run = item.Run;
    }

    /// <summary>Keeps the choice and reports whether the analysis may start: at least one case is set to Run, and
    /// the cases make a plan — none depends on itself.</summary>
    internal bool RunNow()
    {
        if (!RunNowButton.IsEnabled) { ShowHint(); return false; }
        Commit();
        return true;
    }

    private void ToggleCase_Click(object sender, RoutedEventArgs e) => Toggle(CaseGrid.SelectedItem as RunItem);
    private void ToggleAll_Click(object sender, RoutedEventArgs e) => ToggleAll();
    private void CaseGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Toggle(CaseGrid.SelectedItem as RunItem);
    private void RunNow_Click(object sender, RoutedEventArgs e) { if (RunNow()) DialogResult = true; }
    private void Ok_Click(object sender, RoutedEventArgs e) { Commit(); DialogResult = false; }
}
