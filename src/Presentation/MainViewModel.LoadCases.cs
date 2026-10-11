using System.Collections.ObjectModel;
using COT_VCF.Application;
using COT_VCF.Domain;

namespace COT_VCF.Presentation;

// ---- Define → Load Cases: the load cases are defined in the app ----
// The default setup is SAP2000's for a cable structure (FemLoadPlan.DefaultSetup): Pretension — the cable target forces
// with the self-weight — first, then SLS and ULS continuing from it with the Tab 01 loads. The table can also show the
// cases saved from SAP2000, and either set is edited here: add, remove, name, definition, the case a case continues from.
// Nothing is written to SAP2000; the rows are saved with the model (Save Model).
public sealed partial class MainViewModel
{
    private FemCaseRow? selectedFemCaseRow;

    /// <summary>The case selected in the Load Cases table (Remove acts on it).</summary>
    public FemCaseRow? SelectedFemCaseRow { get => selectedFemCaseRow; set => Set(ref selectedFemCaseRow, value); }

    /// <summary>What a case can continue from: nothing ("") or another case. Changed in place, so a From cell never loses
    /// the name it shows.</summary>
    public ObservableCollection<string> FemInitialCaseChoices { get; } = [""];

    /// <summary>The model holds load cases read from SAP2000, so the table can show SAP2000's own setup again.</summary>
    public bool HasSapLoadCases => femSapLoading.Cases.Count > 0;

    /// <summary>Replaces the three load tables, keeping the rows wired to the table's rules.</summary>
    private void SetFemLoadRows(IReadOnlyList<FemPatternRow> patterns, IReadOnlyList<FemCaseRow> cases, IReadOnlyList<FemCombinationRow> combinations)
    {
        foreach (var row in FemPatternRows) row.PropertyChanged -= PatternRowChanged;
        foreach (var row in FemCaseRows) Detach(row);
        foreach (var row in FemCombinationRows) row.PropertyChanged -= LoadRowChanged;
        SelectedFemCaseRow = null;
        FemPatternRows.Clear(); FemCaseRows.Clear(); FemCombinationRows.Clear();
        while (FemInitialCaseChoices.Count > 1) FemInitialCaseChoices.RemoveAt(1);
        foreach (var row in patterns) { row.PropertyChanged += PatternRowChanged; FemPatternRows.Add(row); }
        foreach (var row in cases) AddCaseRow(row);
        foreach (var row in combinations) { row.PropertyChanged += LoadRowChanged; FemCombinationRows.Add(row); }
        RefreshFemPatternRows();
        Notify(nameof(FemLoadSummary));
        Notify(nameof(FemLoadSelectionNames));
        Notify(nameof(HasSapLoadCases));
    }

    // ---- Define → Load Patterns: the patterns are the model's own ----

    private void PatternRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FemPatternRow.SelfWeightMultiplier)) RefreshFemPatternRows();
    }

    /// <summary>What each load pattern is used for and carries now — it follows 01 Loading, 03 Pretension and the
    /// self-weight multiplier, not the text saved with the row.</summary>
    private void RefreshFemPatternRows()
    {
        if (FemPatternRows.Count == 0) return;
        var (tab01, pretension) = (Tab01Patterns, PretensionPatterns);
        foreach (var row in FemPatternRows)
            (row.Role, row.Loads) = FemLoadPlan.Describe(femSapLoading, row.Name, row.SelfWeightMultiplier, tab01, pretension);
    }

    /// <summary>Load patterns can be defined here once the model is the app's own; a connected SAP2000 model has SAP2000's.</summary>
    public bool CanDefineLoadPatterns => !IsBusy && HasWorkingModel && !State.IsSapConnected;

    /// <summary>Adds a load pattern to the model. It carries nothing until a load is assigned to it: a storey load
    /// (Edit → VCF System → Storey Force), the cable target forces, or a self-weight multiplier.</summary>
    internal FemPatternRow? AddFemLoadPattern(string? name, string? type)
    {
        if (IsBusy || CommitEditors?.Invoke() == false) return null;
        name = name?.Trim() ?? "";
        var problem = State.IsSapConnected ? "The model is the connected SAP2000 model, so its load patterns are SAP2000's. " +
                "Define load patterns on an offline model (File → Default Model or Load Model, or after Edit → VCF System → Apply)."
            : FemLoadPlan.NameProblem(name)
            ?? (FemPatternRows.Select(row => row.Name).Concat(State.LoadPatterns).Contains(name, StringComparer.OrdinalIgnoreCase)
                ? $"There is already a load pattern '{name}'." : null);
        if (problem is not null)
        {
            Warn("Load Patterns", problem);
            return null;
        }
        var row = new FemPatternRow(name, string.IsNullOrWhiteSpace(type) ? "Other" : type.Trim(), 0, "", "none");
        row.PropertyChanged += PatternRowChanged;
        FemPatternRows.Add(row);
        State.LoadPatterns.Add(name);
        Message = $"Load pattern '{name}' added. Give it a load on Edit → VCF System → Storey Force, then use it in a load case.";
        Notify(nameof(FemLoadSummary));
        Update();
        return row;
    }

    /// <summary>Removes a load pattern nothing uses: no load case, no row of 01 Loading, no cable of 03 Pretension.</summary>
    internal bool RemoveFemLoadPattern(FemPatternRow? row)
    {
        if (IsBusy || row is null || !FemPatternRows.Contains(row) || CommitEditors?.Invoke() == false) return false;
        bool Same(string? other) => row.Name.Equals(other, StringComparison.OrdinalIgnoreCase);
        var users = FemCaseRows.Where(item => Terms(item).Any(term => Same(term.Name))).Select(item => $"load case '{item.Name}'")
            .Concat(State.LoadDefinitions.Any(definition => Same(definition.LoadName)) ? ["01 Loading"] : [])
            .Concat(State.Cables.Any(cable => Same(cable.LoadPattern)) ? ["03 Pretension"] : []).ToArray();
        var problem = State.IsSapConnected ? "The model is the connected SAP2000 model, so its load patterns are SAP2000's."
            : users.Length > 0 ? $"'{row.Name}' is used by {string.Join(", ", users)}. Change those first." : null;
        if (problem is not null)
        {
            Warn("Load Patterns", problem);
            return false;
        }
        row.PropertyChanged -= PatternRowChanged;
        FemPatternRows.Remove(row);
        foreach (var listed in State.LoadPatterns.Where(Same).ToArray()) State.LoadPatterns.Remove(listed);
        Message = $"Load pattern '{row.Name}' removed.";
        Notify(nameof(FemLoadSummary));
        Update();
        return true;
    }

    // ---- Run: which load cases to analyse ----

    /// <summary>Set by the view: shows Set Load Cases to Run and returns true when the user chose Run Now. Without a
    /// view (checks) Run Analysis solves the ticked cases directly.</summary>
    public Func<bool>? ChooseFemLoadCases { get; set; }

    /// <summary>Run → Run FEM Analysis from the UI: choose the load cases, then solve the model with them.</summary>
    private void RunFemAnalysisFromUi()
    {
        if (IsBusy || CommitEditors?.Invoke() == false) return;
        if (ChooseFemLoadCases?.Invoke() == false) return;
        _ = AnalyzeFemAsync();
    }

    // ---- Edit → VCF System → Cable Force / Storey Force: the loads of the model ----
    // The cable forces and the storey loads are part of the parametric set-up of the model: the same data as 03 Pretension
    // and 01 Loading, entered there as one fixed value each. A range is set for an optimization run, not for the model.

    /// <summary>Sets the target force of cables that have a value of their own. The model holds one value per cable, so a
    /// Tab 03 range becomes that fixed value; a cable that follows another's value keeps following it.</summary>
    internal void SetCableTargetForces(IReadOnlyDictionary<string, double> forcesKN)
    {
        var changed = 0;
        foreach (var (name, force) in forcesKN)
        {
            if (!double.IsFinite(force) || force < 0) continue;
            if (State.Cables.FirstOrDefault(item => item.CableName == name) is not { } cable) continue;
            if (cable.IsSameStepFollower) cable.MinForce = cable.MaxForce = force;     // its own end points, both at the value
            else if (cable.IsFollower) continue;
            else { cable.ApplyRange = false; cable.TargetForce = force; }
            changed++;
        }
        if (changed == 0) return;
        ClearFemResults();
        Update();
    }

    /// <summary>Sets the storey loads of the model: the tributary width, the number of storeys, one qz [kPa] per load
    /// pattern and — when given — the loaded points. A load whose value is the one the model already uses is left as it
    /// is; another value makes it fixed (a Tab 01 range ends there).</summary>
    internal void SetStoreyLoads(double tributaryWidthB, double numberOfStories, IReadOnlyList<(string Pattern, double Qz)> loads,
        IReadOnlyList<string>? loadedPoints = null)
    {
        string Signature() => $"{State.TributaryWidthB:R}|{State.NumberOfStories:R}|" +
            string.Join(";", State.LoadDefinitions.Select(item => $"{item.LoadName}={(item.CandidateCount > 0 ? item.CandidateValues[0] : double.NaN):R}/{item.CandidateCount}")) + "|" +
            string.Join(";", State.AppliedPoints.Select(point => point.PointName));
        var before = Signature();
        State.TributaryWidthB = tributaryWidthB;
        State.NumberOfStories = numberOfStories;

        var kept = new HashSet<LoadDefinition>();
        foreach (var (pattern, qz) in loads)
        {
            var definition = State.LoadDefinitions.FirstOrDefault(item => !kept.Contains(item) && item.LoadName.Equals(pattern, StringComparison.OrdinalIgnoreCase));
            if (definition is null) State.LoadDefinitions.Add(definition = new LoadDefinition { LoadName = pattern });
            kept.Add(definition);
            if (definition.CandidateCount > 0 && definition.CandidateValues[0] == qz) continue;
            definition.ApplyIncrementStep = false;
            definition.Qz = qz;
        }
        foreach (var removed in State.LoadDefinitions.Where(item => !kept.Contains(item)).ToArray()) State.LoadDefinitions.Remove(removed);

        if (loadedPoints is not null)
        {
            var wanted = loadedPoints.ToHashSet(StringComparer.Ordinal);
            foreach (var point in State.AppliedPoints.Where(point => !wanted.Contains(point.PointName)).ToArray())
            {
                State.AppliedPoints.Remove(point);
                if (ReferenceEquals(SelectedAppliedPoint, point)) SelectedAppliedPoint = null;
            }
            workflow.AddAppliedPoints(loadedPoints);
            workflow.SortAppliedPoints();
        }
        if (Signature() == before) return;
        ClearFemResults();
        Update();
    }

    /// <summary>The point picker with the model's points, for the loaded points of the Storey Force sheet; null when the
    /// user backs out.</summary>
    internal IReadOnlyList<string>? ChooseLoadPoints(IReadOnlyList<string> current)
    {
        var available = State.Geometry.Points.Values.OrderBy(point => point.Position.X).ThenBy(point => point.Name, StringComparer.Ordinal)
            .Select(point => point.Name).ToArray();
        return selection.Select("Select Loaded Points", available, current);
    }

    private void AddCaseRow(FemCaseRow row)
    {
        row.PropertyChanged += LoadRowChanged;
        row.NameProblem = CaseNameProblem;
        row.Renamed = CaseRenamed;
        FemInitialCaseChoices.Add(row.Name);
        FemCaseRows.Add(row);
    }

    private void Detach(FemCaseRow row)
    {
        row.PropertyChanged -= LoadRowChanged;
        row.NameProblem = null;
        row.Renamed = null;
    }

    private string? CaseNameProblem(FemDefinitionRow row, string name) =>
        FemCaseRows.Any(other => !ReferenceEquals(other, row) && other.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ? $"There is already a load case '{name}'."
        : FemCombinationRows.Any(other => other.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ? $"'{name}' is the name of a load combination."
        : null;

    /// <summary>A renamed case keeps its followers: the cases that continue from it and the combinations that use it.</summary>
    private void CaseRenamed(FemDefinitionRow row, string previous)
    {
        // The new name is listed before any From cell takes it, and the old one leaves only when none shows it.
        var listed = FemInitialCaseChoices.IndexOf(previous);
        FemInitialCaseChoices.Insert(listed < 0 ? FemInitialCaseChoices.Count : listed, row.Name);
        foreach (var other in FemCaseRows.Where(other => other.InitialCase.Equals(previous, StringComparison.OrdinalIgnoreCase)).ToArray())
            other.InitialCase = row.Name;
        FemInitialCaseChoices.Remove(previous);
        foreach (var combination in FemCombinationRows)
        {
            IReadOnlyList<(string Name, double Factor)> terms;
            try { terms = FemLoadDefinition.Parse(combination.Definition); }
            catch (ArgumentException) { continue; }
            if (terms.Any(term => term.Name.Equals(previous, StringComparison.OrdinalIgnoreCase)))
                combination.Definition = FemLoadDefinition.Format(terms.Select(term =>
                    term.Name.Equals(previous, StringComparison.OrdinalIgnoreCase) ? (row.Name, term.Factor) : term));
        }
    }

    /// <summary>The app's default load cases only — the rows; see <see cref="ApplyDefaultFemLoadCases"/>.</summary>
    private void UseDefaultFemLoadCases()
    {
        var (patterns, cases) = FemLoadPlan.DefaultSetup(FemPatternRows.ToArray(), Tab01Patterns, PretensionPatterns);
        SetFemLoadRows(patterns, cases, []);
        SelectedFemResultName = FemLoadSelectionNames.LastOrDefault();
    }

    /// <summary>Define → Load Cases → Default Setup: the staged cases SAP2000 needs for a cable structure — Pretension
    /// (target forces + self-weight), then SLS and ULS continuing from it with the Tab 01 loads — replace the table.</summary>
    internal void ApplyDefaultFemLoadCases()
    {
        if (IsBusy || CommitEditors?.Invoke() == false) return;
        UseDefaultFemLoadCases();
        ClearFemResults();
        FemProgressText = FemCaseRows.Count == 0
            ? "No default load case could be defined — set the Tab 01 loads and the Tab 03 cable target forces first."
            : $"Default load cases defined: {string.Join(" → ", FemCaseRows.Select(row => row.Name))}. " +
              "The cable target forces are met in the first case; the others continue from it.";
        Message = FemProgressText;
        Update();
    }

    /// <summary>Define → Load Cases → SAP Cases: the load cases and combinations as SAP2000 has them (the snapshot the
    /// model holds) replace the table.</summary>
    internal void ApplySapFemLoadCases()
    {
        if (IsBusy || !HasSapLoadCases || CommitEditors?.Invoke() == false) return;
        ResetFemLoadRows();
        ClearFemResults();
        FemProgressText = $"Load cases as saved from SAP2000: {FemCaseRows.Count(row => row.Run)} of {FemCaseRows.Count} cases and " +
            $"{FemCombinationRows.Count(row => row.Run)} of {FemCombinationRows.Count} combinations ticked to run.";
        Message = FemProgressText;
        Update();
    }

    /// <summary>Adds a nonlinear static case with the Tab 01 loads, continuing from the pretension case when there is one.</summary>
    internal FemCaseRow? AddFemLoadCase()
    {
        if (IsBusy || CommitEditors?.Invoke() == false) return null;
        var name = "Case 1";
        for (var number = 2; CaseNameProblem(null!, name) is not null; number++) name = $"Case {number}";
        var pretension = PretensionPatterns;
        var stage = FemCaseRows.FirstOrDefault(row => row.Supported && Terms(row).Any(term => pretension.Contains(term.Name, StringComparer.OrdinalIgnoreCase)));
        var patterns = FemPatternRows.Select(row => row.Name).ToArray();
        var loads = Tab01Patterns.Where(pattern => patterns.Contains(pattern, StringComparer.OrdinalIgnoreCase)).ToArray();
        var terms = (loads.Length > 0 ? loads : patterns.Take(1)).Select(pattern => (pattern, 1.0)).ToArray();
        if (terms.Length == 0)
        {
            Warn("Load Cases", "There is no load pattern to build a case from. Use Default Setup, or File → Read FEM Properties from SAP.");
            return null;
        }
        var row = new FemCaseRow(name, FemLoadPlan.StagedCaseType, FemLoadDefinition.Format(terms), stage?.Name, true, true, "App", "");
        AddCaseRow(row);
        SelectedFemCaseRow = row;
        LoadRowChanged(row, new(nameof(FemCaseRow.Run)));
        return row;
    }

    private static IReadOnlyList<(string Name, double Factor)> Terms(FemDefinitionRow row)
    {
        try { return FemLoadDefinition.Parse(row.Definition); }
        catch (ArgumentException) { return []; }
    }

    /// <summary>Removes a case nothing else depends on; a case others continue from, or a combination uses, stays.</summary>
    internal bool RemoveFemLoadCase(FemCaseRow? row)
    {
        if (IsBusy || row is null || !FemCaseRows.Contains(row) || CommitEditors?.Invoke() == false) return false;
        var followers = FemCaseRows.Where(other => other.InitialCase.Equals(row.Name, StringComparison.OrdinalIgnoreCase)).Select(other => other.Name)
            .Concat(FemCombinationRows.Where(combination => Terms(combination).Any(term => term.Name.Equals(row.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(combination => combination.Name)).ToArray();
        if (followers.Length > 0)
        {
            Warn("Load Cases", $"'{row.Name}' is used by {string.Join(", ", followers.Select(name => $"'{name}'"))}. Change or remove those first.");
            return false;
        }
        Detach(row);
        FemCaseRows.Remove(row);
        FemInitialCaseChoices.Remove(row.Name);
        if (ReferenceEquals(SelectedFemCaseRow, row)) SelectedFemCaseRow = null;
        LoadRowChanged(row, new(nameof(FemCaseRow.Run)));
        return true;
    }
}
