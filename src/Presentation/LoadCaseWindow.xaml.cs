using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using COT_VCF.Application;
using COT_VCF.Domain;

namespace COT_VCF.Presentation;

/// <summary>
/// Define → Load Cases → + Add / Modify… (as SAP2000's Load Case Data): the name of a load case, the case it continues
/// from and its Loads Applied — a load pattern of the model with a scale factor per row, added and removed here. The
/// form is staged: OK writes the case to the Load Cases table (a new case is added there), Cancel leaves it as it was.
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

    private const string NoCase = "(none)";
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

        NameBox.Text = row.Name;
        TypeText.Text = row.Type;
        PurposeBox.SelectedIndex = row.CableTargetForces ? 1 : 0;
        var cases = viewModel.InitialCaseChoicesFor(existing).Skip(1).ToArray();
        FromBox.ItemsSource = new[] { NoCase }.Concat(cases).ToArray();
        FromBox.SelectedIndex = Array.FindIndex(cases, name => comparer.Equals(name, row.InitialCase)) + 1;
        foreach (var (pattern, factor) in own)
            loads.Add(NewRow(patterns.First(name => comparer.Equals(name, pattern)), factor.ToString("0.###", CultureInfo.InvariantCulture)));
        LoadGrid.ItemsSource = loads;
        ready = true;
        Refresh();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    /// <summary>The case this one continues from; "" when it starts unstressed.</summary>
    internal string From
    {
        get => FromBox.SelectedIndex > 0 ? (string)FromBox.SelectedItem : "";
        set => FromBox.SelectedIndex = Math.Max(0, FromBox.Items.IndexOf(value));
    }

    // 1.35 is read as typed in the Definition column, and as the user's own regional format writes it
    private static double? Number(string text) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
         double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && double.IsFinite(value) ? value : null;

    private LoadRow NewRow(string pattern, string factor) => new(pattern, factor, patterns, Carries, Refresh);

    private string Carries(string pattern) =>
        pattern.Length == 0 ? ""
        : viewModel.FemPatternRows.FirstOrDefault(item => item.Name.Equals(pattern, StringComparison.OrdinalIgnoreCase)) is { } row ? row.Loads
        : "not a load pattern of the model";

    /// <summary>The form as it stands: what keeps it from being written, and everything on the structure at the end of the case.</summary>
    private void Refresh()
    {
        if (!ready) return;
        var comparer = StringComparer.OrdinalIgnoreCase;
        Problem = viewModel.FemLoadCaseProblem(existing, NameBox.Text, From, loads.Select(row => (row.Pattern, row.Value ?? double.NaN)).ToArray());
        ProblemText.Text = Problem ?? "";
        OkButton.IsEnabled = Problem is null;
        FromHint.Text = From.Length == 0 ? "starts unstressed" : "its loads stay on the structure";
        var total = viewModel.FemLoadCaseTotal(From, loads.Where(row => row.Pattern.Length > 0 && row.Value is not null).Select(row => (row.Pattern, row.Value!.Value)).ToArray());
        TotalText.Text = total.Count == 0 ? "" : "At the end of this case: " + FemLoadDefinition.Format(total);
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
        Saved = viewModel.SaveFemLoadCase(existing, NameBox.Text, From, loads.Select(row => (row.Pattern, row.Value!.Value)).ToArray(), PurposeBox.SelectedIndex == 1);
        return Saved is not null;
    }

    private void Input_Changed(object sender, RoutedEventArgs e) => Refresh();
    private void AddLoad_Click(object sender, RoutedEventArgs e) => AddLoad();
    private void RemoveLoad_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LoadRow row }) RemoveLoad(row);
    }
    private void Ok_Click(object sender, RoutedEventArgs e) { if (Accept()) DialogResult = true; }
}
