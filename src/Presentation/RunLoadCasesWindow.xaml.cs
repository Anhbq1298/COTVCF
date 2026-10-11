using System.Windows;
using COT_VCF.Application;
using COT_VCF.Domain;

namespace COT_VCF.Presentation;

/// <summary>
/// Run → Run FEM Analysis opens this first (as SAP2000's Set Load Cases to Run): the load cases and combinations of
/// Define → Load Cases with their action, Run or Do Not Run. Run Now keeps the choice and starts the analysis of the
/// model as it is now; OK keeps the choice only; Cancel discards it.
/// </summary>
public partial class RunLoadCasesWindow : Window
{
    /// <summary>One load case or combination with its staged action.</summary>
    internal sealed class RunItem(FemDefinitionRow row, string status) : Observable
    {
        private bool run = row.Run;
        public FemDefinitionRow Row { get; } = row;
        public string Name => Row.Name;
        public string Type => Row.Type;
        public string From => (Row as FemCaseRow)?.InitialCase ?? "";
        public string Status { get; } = status;
        public bool Supported => Row.Supported;
        public bool Run
        {
            get => run;
            set { if (Set(ref run, value && Supported)) Notify(nameof(Action)); }
        }
        public string Action => !Supported ? "Not available" : Run ? "Run" : "Do Not Run";
    }

    private readonly MainViewModel viewModel;
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
        ShowHint();
    }

    private void ShowHint()
    {
        var run = Items.Count(item => item.Run);
        Hint.Text = Items.Count == 0 ? "No load case is defined — Define → Load Cases → Default Setup."
            : run == 0 ? "Set at least one load case to Run."
            : $"{run} of {Items.Count(item => item.Supported)} set to Run · a case that another continues from is part of it even when it is not run itself.";
        RunNowButton.IsEnabled = run > 0;
    }

    internal void Toggle(RunItem? item)
    {
        if (item is null || !item.Supported) return;
        item.Run = !item.Run;
        ShowHint();
    }

    /// <summary>Sets every case to Run, or — when all already are — every case to Do Not Run.</summary>
    internal void ToggleAll()
    {
        var available = Items.Where(item => item.Supported).ToArray();
        var run = available.Any(item => !item.Run);
        foreach (var item in available) item.Run = run;
        ShowHint();
    }

    /// <summary>Writes the staged actions to the load case table.</summary>
    internal void Commit()
    {
        foreach (var item in Items) item.Row.Run = item.Run;
    }

    /// <summary>Keeps the choice and reports whether the analysis may start (at least one case is set to Run).</summary>
    internal bool RunNow()
    {
        if (!Items.Any(item => item.Run)) { ShowHint(); return false; }
        Commit();
        return true;
    }

    private void ToggleCase_Click(object sender, RoutedEventArgs e) => Toggle(CaseGrid.SelectedItem as RunItem);
    private void ToggleAll_Click(object sender, RoutedEventArgs e) => ToggleAll();
    private void CaseGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Toggle(CaseGrid.SelectedItem as RunItem);
    private void RunNow_Click(object sender, RoutedEventArgs e) { if (RunNow()) DialogResult = true; }
    private void Ok_Click(object sender, RoutedEventArgs e) { Commit(); DialogResult = false; }
}
