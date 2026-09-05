using COT_VCF.Application;
using COT_VCF.Configuration;
using COT_VCF.Domain;
using Microsoft.Win32;
using System.Collections;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;

namespace COT_VCF.Presentation;

public sealed class MainViewModel : Observable
{
    public ApplicationState State { get; }
    private readonly WorkflowService workflow;
    private readonly ISelectionService selection;
    private readonly ConfigService config;
    private bool busy;
    private int selectedTab;
    private AppliedPoint? selectedAppliedPoint;
    private string message = "Connect to the running SAP2000 model to begin.";
    private string previewMessage = "Select point groups and capture the baseline.";

    public bool IsBusy { get => busy; private set { Set(ref busy, value); Notify(nameof(IsIdle)); Notify(nameof(CanUseConfig)); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsIdle => !IsBusy;
    public int SelectedTab { get => selectedTab; set => Set(ref selectedTab, value); }
    public string Message { get => message; private set => Set(ref message, value); }
    public Func<bool>? CommitEditors { get; set; }
    public string PreviewMessage { get => previewMessage; private set => Set(ref previewMessage, value); }
    public AppliedPoint? SelectedAppliedPoint
    {
        get => selectedAppliedPoint;
        set
        {
            if (ReferenceEquals(selectedAppliedPoint, value)) return;
            if (selectedAppliedPoint is not null) selectedAppliedPoint.IsSelected = false;
            selectedAppliedPoint = value;
            if (selectedAppliedPoint is not null) selectedAppliedPoint.IsSelected = true;
            Notify();
        }
    }

    public string ModelName => State.Model?.Name ?? "No model connected";
    public string ModelPath => State.Model?.Path ?? "Open a model in SAP2000, then connect.";
    public string ModelVersion => State.Model?.Version ?? "-";
    public string LockState => State.Model is null ? "-" : State.Model.IsLocked ? "Locked - unlock in SAP before applying" : "Unlocked";
    public string ForceUnit => State.Model?.ForceUnit ?? "-";
    public string LengthUnit => State.Model?.LengthUnit ?? "-";
    public string FxHeader => $"Fx [{ForceUnit}]";
    public string FyHeader => $"Fy [{ForceUnit}]";
    public string FzHeader => $"Fz [{ForceUnit}]";
    public string GeometrySummary => $"{State.Geometry.Points.Count} points  -  {State.Geometry.Members.Count(m => m.Kind == MemberKind.Frame)} frames  -  {State.Geometry.Members.Count(m => m.Kind != MemberKind.Frame)} cables / tendons";
    public string TopSummary => $"{State.Truss.TopPoints.Count} points selected";
    public string MiddleSummary => $"{State.Truss.MiddlePoints.Count} points selected";
    public string BottomSummary => $"{State.Truss.BottomPoints.Count} points selected";
    public string GroupWarning => State.Truss.TopPoints.Count > 0 && State.Truss.MiddlePoints.Count > 0 && State.Truss.BottomPoints.Count > 0 && (State.Truss.TopPoints.Count != State.Truss.MiddlePoints.Count || State.Truss.TopPoints.Count != State.Truss.BottomPoints.Count) ? "Review: group counts differ. Each selected group moves uniformly; point-to-point correspondence is not inferred." : "";
    public string HeightSummary => State.BaselineCaptured
        ? $"SAP {State.CommittedHeight:F4}   ·   Preview {State.Truss.CurrentHeight:F4}   ·   ΔH {State.Truss.CurrentHeight - State.Truss.BaselineHeight:+0.0000;-0.0000;0.0000} {LengthUnit}   ·   Ref {State.Truss.Reference}"
        : "Baseline not captured — select chord groups, then Capture Baseline.";
    public string BaselineStatus => State.BaselineCaptured
        ? $"✓ Baseline captured · H₀ {State.Truss.BaselineHeight:F4} {LengthUnit}"
        : "Baseline not captured";
    public string CandidateCountText => State.BaselineCaptured && State.Truss.CandidateCount > 0 ? State.Truss.CandidateCount.ToString() : "—";
    public string CandidateWarning
    {
        get
        {
            if (!State.BaselineCaptured) return "";
            var t = State.Truss;
            if (!(t.MaximumHeight >= t.MinimumHeight)) return "Maximum height must be at least the minimum height.";
            if (!(t.Increment > 0)) return "Increment step must be greater than zero.";
            if (t.CandidateCount <= 0) return "The height range produces no candidates.";
            return t.LandsOnMaximum ? "" : $"Last step lands at {t.MinimumHeight + (t.CandidateCount - 1) * t.Increment:F4} {LengthUnit}, not the maximum {t.MaximumHeight:F4} {LengthUnit}.";
        }
    }
    public string PreviewDelta => State.BaselineCaptured
        ? $"ΔH {State.Truss.CurrentHeight - State.Truss.BaselineHeight:+0.0000;-0.0000;0.0000} {LengthUnit}  ·  Ref {State.Truss.Reference}"
        : "";
    public IReadOnlyList<string> TrussTopPoints => State.Truss.TopPoints;
    public IReadOnlyList<string> TrussMiddlePoints => State.Truss.MiddlePoints;
    public IReadOnlyList<string> TrussBottomPoints => State.Truss.BottomPoints;
    public bool CanViewTrussCandidates => TrussCandidatesReady(out _);
    public string TrussCandidatesHint { get { TrussCandidatesReady(out var reason); return reason; } }
    public string IterationSummary => $"MODEL\n{ModelName} - {State.Units}\n\nLOADING CRITERIA\n{State.AppliedPoints.Count} applied points - {State.LoadDefinitions.Count} load definitions\n\nTRUSS HEIGHT\n{State.Truss.MinimumHeight:F4} to {State.Truss.MaximumHeight:F4} {LengthUnit} - step {State.Truss.Increment:F4} {LengthUnit}\n\nCABLE PRETENSION\n{CableCount} cables ({VariableCableCount} variable) - {PretensionCandidateCount} pretension candidates";
    public int CableCount => State.Cables.Count;
    public int VariableCableCount => COT_VCF.Application.PretensionCandidates.VariableCount(State.Cables);
    public string PretensionCandidateCount => State.Cables.Count == 0 || State.Cables.Any(c => c.CandidateCount <= 0)
        ? "0"
        : PretensionRuns.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    // ---- Tab 05 iteration study ----
    private BigInteger PretensionRuns => State.Cables.Count == 0 || State.Cables.Any(c => c.CandidateCount <= 0)
        ? BigInteger.Zero
        : PretensionCandidates.TotalCount(State.Cables);
    public int LoadingCandidateCount => State.LoadDefinitions.Sum(d => d.CandidateValues.Count);
    public int HeightCandidateCount => State.BaselineCaptured ? State.Truss.CandidateCount : 0;
    public IReadOnlyList<double> HeightCandidates => State.BaselineCaptured ? State.Truss.CandidateHeights : [];

    public string StudyModeLabel => State.Study.Mode switch
    {
        IterationStudyMode.FixPretension => "Fix Pretension",
        IterationStudyMode.FixHeight => "Fix Height",
        _ => "Combined",
    };
    public bool StudyNeedsFixedHeight => State.Study.Mode == IterationStudyMode.FixHeight;
    public bool StudyNeedsFixedPretension => State.Study.Mode == IterationStudyMode.FixPretension;

    public int FixedHeightIndex
    {
        get { var n = HeightCandidateCount; return n <= 0 ? -1 : Math.Clamp(State.Study.FixedHeightIndex, 0, n - 1); }
        set { if (value >= 0) State.Study.FixedHeightIndex = value; }
    }
    // Pretension combinations are enumerated 1-based in the UI; cap the picker at int range.
    private int PretensionRunCount
    {
        get { var n = PretensionRuns; return n <= 0 ? 0 : n > int.MaxValue ? int.MaxValue : (int)n; }
    }
    public double PretensionMaxOrdinal => Math.Max(1, PretensionRunCount);
    public double FixedPretensionOrdinal
    {
        get { var n = PretensionRunCount; return n <= 0 ? 1 : Math.Clamp(State.Study.FixedPretensionIndex + 1, 1, n); }
        set => State.Study.FixedPretensionIndex = Math.Max(0, (int)Math.Round(value) - 1);
    }

    public string FixedHeightValueText
    {
        get
        {
            var hs = HeightCandidates; var i = FixedHeightIndex;
            return i >= 0 && i < hs.Count ? $"H{i + 1} = {hs[i]:F4} {LengthUnit}" : "no height candidates yet";
        }
    }
    public string FixedPretensionValueText
    {
        get
        {
            var n = PretensionRunCount;
            if (n <= 0) return "no pretension candidates yet";
            int idx = Math.Clamp(State.Study.FixedPretensionIndex, 0, n - 1);
            var combo = PretensionCandidates.Enumerate(State.Cables.ToArray()).Skip(idx).FirstOrDefault();
            return combo is null
                ? $"P{idx + 1}"
                : $"P{idx + 1}:  " + string.Join(",  ", combo.CableForces.Select(kv => $"{kv.Key}={kv.Value:0.###}"));
        }
    }

    public string StudyLoadingCell => LoadingCandidateCount.ToString();
    public string StudyHeightCell => StudyNeedsFixedHeight ? $"FIXED · H{FixedHeightIndex + 1}" : HeightCandidateCount.ToString();
    public string StudyPretensionCell => StudyNeedsFixedPretension ? $"FIXED · P{State.Study.FixedPretensionIndex + 1}" : PretensionCandidateCount;
    public string StudyTotalRuns => StudyPlan.TotalRuns(State.Study.Mode, LoadingCandidateCount, HeightCandidateCount, PretensionRuns)
        .ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    public string SapStatus => State.IsSapConnected ? "Connected" : State.Connection;
    public string StatusLine => State.IsSapConnected
        ? $"{ModelName}  ·  {State.Units}  ·  {(State.Model?.IsLocked == true ? "Locked" : "Unlocked")}"
        : $"SAP2000 — {SapStatus}";
    public string AppliedPointsTitle => $"APPLIED POINTS ({State.AppliedPoints.Count})";
    public bool HasProject => config.Session.HasActiveProject;
    public string ProjectName => config.Session.HasActiveProject ? $"Project: {config.Session.FileName}" : "No project — save to create one";

    // A tab header state reflects the active project: Saved / Modified * / Not saved / Not configured.
    private string TabState(ConfigTabs tab, bool configured, bool invalid)
    {
        if (!configured) return "Not configured";
        if (invalid) return "Review inputs";
        if (!config.Session.ProjectHas(tab)) return "Not saved";
        return config.Session.IsDirty(tab) ? "Modified *" : "Saved";
    }
    private bool AnyTrussGroup => State.Truss.TopPoints.Count > 0 || State.Truss.MiddlePoints.Count > 0 || State.Truss.BottomPoints.Count > 0;
    public string LoadStatus => TabState(ConfigTabs.Loading, State.AppliedPoints.Count > 0, State.AppliedPoints.Count > 0 && !LoadingCriteriaValid());
    public string TrussStatus => TabState(ConfigTabs.Truss, State.BaselineCaptured || AnyTrussGroup,
        !State.BaselineCaptured || new COT_VCF.Geometry.TrussGeometryService().Validate(State.Baseline, State.Truss).Count > 0);
    public string CableStatus => TabState(ConfigTabs.Cable, State.Cables.Count > 0, State.Cables.Any(c => c.Status != "Configured"));
    public ObservableCollection<ValidationIssue> Issues { get; } = [];

    public Command Connect { get; }
    public Command Refresh { get; }
    public Command AddPoints { get; }
    public Command RemoveAppliedPoints { get; }
    public Command AddLoadDefinition { get; }
    public Command DeleteLoadDefinition { get; }
    public Command SelectGroup { get; }
    public Command CaptureBaseline { get; }
    public Command ResetPreview { get; }
    public Command ApplyGeometry { get; }
    public Command ApplyLoads { get; }
    public Command AddCables { get; }
    public Command RemoveLoads { get; }
    public Command RemoveCables { get; }
    public Command Validate { get; }
    public Command SaveTabConfig { get; }
    public Command SaveAllConfig { get; }
    public Command LoadConfig { get; }

    public bool CanUseConfig => !IsBusy && State.IsSapConnected && State.Model is not null;
    private bool applyingProject;

    public MainViewModel(ApplicationState state, WorkflowService workflow, ISelectionService selection, ConfigService config)
    {
        State = state;
        this.workflow = workflow;
        this.selection = selection;
        this.config = config;
        bool Ready() => !IsBusy && State.IsSapConnected;
        bool GeometryReady() => Ready() && State.Geometry.Points.Count > 0;

        Connect = new(_ => Run("Connecting to SAP2000...", ConnectAndRefreshAsync, "Connected. Geometry loaded."), () => !IsBusy);
        Refresh = new(_ => Run("Refreshing SAP2000 connection and geometry...", ConnectAndRefreshAsync, "Connection refreshed. Geometry updated."), () => !IsBusy);
        AddPoints = new(_ => SelectLoadLocations(), GeometryReady);
        RemoveAppliedPoints = new(p =>
        {
            if (p is not IList list) return;
            var removed = list.Cast<AppliedPoint>().ToArray();
            foreach (var point in removed) State.AppliedPoints.Remove(point);
            if (SelectedAppliedPoint is not null && removed.Contains(SelectedAppliedPoint)) SelectedAppliedPoint = null;
            workflow.SortAppliedPoints();
            Update();
        }, () => !IsBusy);
        AddLoadDefinition = new(_ => { State.LoadDefinitions.Add(new()); Update(); }, () => !IsBusy);
        DeleteLoadDefinition = new(p => { if (p is LoadDefinition row) State.LoadDefinitions.Remove(row); Update(); }, () => !IsBusy);
        SelectGroup = new(p => ChooseGroup(p?.ToString() ?? "Top"), GeometryReady);
        CaptureBaseline = new(_ => Local(() => { workflow.CaptureBaseline(); PreviewMessage = "Baseline captured. Slider changes only the preview."; }), GeometryReady);
        ResetPreview = new(_ => Local(() => { State.Truss.CurrentHeight = State.CommittedHeight; workflow.UpdatePreview(); }), () => GeometryReady() && State.BaselineCaptured);
        ApplyGeometry = new(_ => Run("Applying geometry to SAP2000...", workflow.ApplyGeometryAsync, "Geometry applied. The original baseline is retained."), () => GeometryReady() && State.BaselineCaptured);
        ApplyLoads = new(_ => Run("Applying point loads to SAP2000...", workflow.ApplyLoadsAsync, "Point loads applied. Existing moments preserved."), () => GeometryReady() && State.PointLoads.Count > 0);
        AddCables = new(_ => AddCablesFromSelection(), GeometryReady);
        RemoveLoads = new(p => { if (p is IList list) foreach (var row in list.Cast<PointLoadConfiguration>().ToArray()) State.PointLoads.Remove(row); Update(); }, () => !IsBusy);
        RemoveCables = new(p => { if (p is IList list) foreach (var row in list.Cast<CablePretensionConfig>().ToArray()) State.Cables.Remove(row); Update(); }, () => !IsBusy);
        Validate = new(_ =>
        {
            if (CommitEditors?.Invoke() == false) return;
            Issues.Clear();
            foreach (var issue in new ConfigurationValidator().Validate(State)) Issues.Add(issue);
            Message = Issues.Count == 0 ? "Configuration is valid. Iteration is planned for the next phase." : $"{Issues.Count} configuration issues need review.";
        }, () => !IsBusy);
        SaveTabConfig = new(p => SaveTab(p switch
        {
            "Loading" => ConfigTabs.Loading,
            "Truss" => ConfigTabs.Truss,
            "Cable" => ConfigTabs.Cable,
            _ => ConfigTabs.All,
        }), () => CanUseConfig);
        SaveAllConfig = new(_ => SaveAll(), () => CanUseConfig);
        LoadConfig = new(_ => OpenProject(), () => CanUseConfig);

        void Dirty(ConfigTabs tab) { if (!applyingProject) config.Session.MarkDirty(tab); }

        State.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ApplicationState.TributaryWidthB) or nameof(ApplicationState.NumberOfStories)) Dirty(ConfigTabs.Loading);
            Update();
        };
        State.Truss.PropertyChanged += (_, _) =>
        {
            Dirty(ConfigTabs.Truss);
            if (State.BaselineCaptured)
            {
                try { workflow.UpdatePreview(); PreviewMessage = "Preview only - Apply geometry commits these coordinates to SAP2000."; }
                catch (Exception ex) { PreviewMessage = ex.Message; }
            }
            Update();
        };
        State.PointLoads.CollectionChanged += (_, e) => { if (e.NewItems is not null) foreach (PointLoadConfiguration row in e.NewItems) row.PropertyChanged += (_, _) => Update(); Update(); };
        State.AppliedPoints.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null) foreach (AppliedPoint point in e.NewItems) point.PropertyChanged += (_, _) => Update();
            Dirty(ConfigTabs.Loading);
            Update();
        };
        foreach (var row in State.LoadDefinitions) Subscribe(row);
        State.LoadDefinitions.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null) foreach (LoadDefinition row in e.NewItems) Subscribe(row);
            Dirty(ConfigTabs.Loading);
            Update();
        };
        State.Cables.CollectionChanged += (_, e) => { if (e.NewItems is not null) foreach (CablePretensionConfig row in e.NewItems) row.PropertyChanged += (_, _) => { Dirty(ConfigTabs.Cable); Update(); }; Dirty(ConfigTabs.Cable); Update(); };
        State.Study.PropertyChanged += (_, _) => Update();
    }

    private async Task ConnectAndRefreshAsync()
    {
        State.Connection = "Connecting";
        try
        {
            await workflow.ConnectAsync();
            await workflow.RefreshAsync();
        }
        catch
        {
            State.Connection = "Error";
            throw;
        }
    }

    private void Subscribe(LoadDefinition row) => row.PropertyChanged += (_, _) => { if (!applyingProject) config.Session.MarkDirty(ConfigTabs.Loading); Update(); };

    private void SelectLoadLocations()
    {
        var available = State.Geometry.Points.Values
            .OrderBy(point => point.Position.X)
            .ThenBy(point => point.Name, StringComparer.Ordinal)
            .Select(point => point.Name)
            .ToArray();
        var current = State.AppliedPoints.Select(point => point.PointName).ToArray();
        var selected = selection.Select("Select Load Locations", available, current);
        if (selected is null) return;
        var added = workflow.AddAppliedPoints(selected);
        Message = added == 0 ? "Load locations are unchanged." : $"Added {added} load location{(added == 1 ? "" : "s")}.";
        Update();
    }

    private bool LoadingCriteriaValid() => State.TributaryWidthB > 0 && double.IsFinite(State.TributaryWidthB) &&
        State.NumberOfStories >= 1 && State.NumberOfStories <= int.MaxValue && State.NumberOfStories == Math.Truncate(State.NumberOfStories) &&
        State.AppliedPoints.All(point => State.Geometry.Points.ContainsKey(point.PointName)) &&
        State.LoadDefinitions.Count > 0 && State.LoadDefinitions.All(row => row.CandidateValues.Count > 0);

    public LoadingCandidatePreview? CreateLoadingCandidatePreview()
    {
        var issues = new ConfigurationValidator().ValidateLoadingCriteria(State);
        if (issues.Count > 0)
        {
            Message = issues[0].Message;
            return null;
        }

        var points = State.AppliedPoints.OrderBy(point => point.X).ThenBy(point => point.PointName, StringComparer.Ordinal).ToArray();
        var tributaryAreas = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int i = 0; i < points.Length; i++)
        {
            double tributaryLength = points.Length switch
            {
                1 => 0,
                _ when i == 0 => Math.Max(0, points[1].X - points[0].X) / 2,
                _ when i == points.Length - 1 => Math.Max(0, points[^1].X - points[^2].X) / 2,
                _ => Math.Max(0, points[i + 1].X - points[i - 1].X) / 2
            };
            tributaryAreas[points[i].PointName] = tributaryLength * State.TributaryWidthB;
        }

        var cases = new List<(string Name, double Qz)>();
        for (int definitionIndex = 0; definitionIndex < State.LoadDefinitions.Count; definitionIndex++)
        {
            var definition = State.LoadDefinitions[definitionIndex];
            var values = definition.CandidateValues;
            var baseName = string.IsNullOrWhiteSpace(definition.LoadName) ? $"Load_{definitionIndex + 1}" : definition.LoadName.Trim();
            for (int candidateIndex = 0; candidateIndex < values.Count; candidateIndex++)
            {
                var caseName = values.Count == 1 ? baseName : $"{baseName}_{candidateIndex + 1}";
                cases.Add((caseName, values[candidateIndex]));
            }
        }

        var numberOfStories = (int)State.NumberOfStories;
        var rows = new List<LoadingCandidate>(points.Length * cases.Count);
        foreach (var point in points)
        {
            var area = tributaryAreas[point.PointName];
            foreach (var candidate in cases)
                rows.Add(new(point.PointName, candidate.Name, candidate.Qz, area, numberOfStories, -candidate.Qz * area * numberOfStories));
        }
        Message = $"Generated {cases.Count} loading cases and {rows.Count} candidate point loads.";
        return new(cases.Count, rows);
    }

    private bool TrussCandidatesReady(out string reason)
    {
        var t = State.Truss;
        if (t.TopPoints.Count == 0 || t.MiddlePoints.Count == 0 || t.BottomPoints.Count == 0)
        { reason = "Select Top, Middle and Bottom chord point groups."; return false; }
        if (!State.BaselineCaptured)
        { reason = "Capture baseline geometry before generating candidates."; return false; }
        if (!double.IsFinite(t.MinimumHeight) || t.MinimumHeight <= 0)
        { reason = "Enter a positive minimum height."; return false; }
        if (!double.IsFinite(t.MaximumHeight) || t.MaximumHeight < t.MinimumHeight)
        { reason = "Maximum height must be at least the minimum height."; return false; }
        if (!double.IsFinite(t.Increment) || t.Increment <= 0)
        { reason = "Increment step must be greater than zero."; return false; }
        if (t.CandidateCount <= 0)
        { reason = "The height range produces no candidates."; return false; }
        reason = $"{t.CandidateCount} candidate geometr{(t.CandidateCount == 1 ? "y" : "ies")} from the captured baseline · reference {t.Reference} chord fixed.";
        return true;
    }

    public IReadOnlyList<TrussCandidate>? CreateTrussCandidates()
    {
        if (!TrussCandidatesReady(out var reason)) { Message = reason; return null; }
        var t = State.Truss;
        var rows = new List<TrussCandidate>(t.CandidateCount);
        int index = 1;
        foreach (var height in t.CandidateHeights)
        {
            double deltaH = height - t.BaselineHeight;
            var (top, mid, bot) = COT_VCF.Geometry.TrussGeometryService.ChordDeltas(t.Reference, deltaH);
            rows.Add(new(index++, height, deltaH, t.Reference.ToString(), top, mid, bot));
        }
        Message = $"{rows.Count} truss-height candidates generated from baseline H₀ {t.BaselineHeight:F4} {LengthUnit}.";
        return rows;
    }

    private void ChooseGroup(string group)
    {
        var truss = State.Truss;
        var old = group == "Top" ? truss.TopPoints : group == "Middle" ? truss.MiddlePoints : truss.BottomPoints;
        var names = selection.Select($"Select {group} Truss Points", State.Geometry.Points.Keys.Order().ToArray(), old);
        if (names is null) return;
        if (group == "Top") truss.TopPoints = names;
        else if (group == "Middle") truss.MiddlePoints = names;
        else truss.BottomPoints = names;
        State.BaselineCaptured = false;
        State.Preview = State.Geometry;
        PreviewMessage = "Groups changed. Capture a baseline before adjusting height.";
        Update();
    }

    private void AddCablesFromSelection()
    {
        var available = State.Geometry.Members
            .Where(m => m.Kind == MemberKind.Cable)
            .Select(m => m.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (available.Length == 0) { Message = "The loaded SAP2000 model has no cable objects."; return; }

        var current = State.Cables.Select(c => c.CableName).ToArray();
        var picked = selection.Select("Add Cables for PRETENSION", available, current);
        if (picked is null) return;

        var existing = State.Cables.Select(c => c.CableName).ToHashSet(StringComparer.Ordinal);
        int added = 0;
        foreach (var name in picked.Distinct(StringComparer.Ordinal))
            if (existing.Add(name)) { State.Cables.Add(new CablePretensionConfig { CableName = name }); added++; }

        Message = added == 0 ? "Cable selection unchanged." : $"Added {added} cable{(added == 1 ? "" : "s")}. Remove with − Remove.";
        Update();
    }

    private const string ConfigFilter = "COT VCF Configuration (*.json)|*.json|JSON Files (*.json)|*.json";

    private string? PickSavePath(ConfigTabs tabs)
    {
        var dialog = new SaveFileDialog { Filter = ConfigFilter, FileName = config.SuggestFileName(tabs), AddExtension = true, DefaultExt = ".json" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    // Per-tab Save: overwrites only this tab's section of the active project (other tabs come from the
    // in-memory project, NOT their unsaved UI); creates the project via Save-As the first time.
    private void SaveTab(ConfigTabs single) => RunSave(single);
    // Save All: extracts every tab from the UI and rewrites the whole project atomically.
    private void SaveAll() => RunSave(ConfigTabs.All);

    private async void RunSave(ConfigTabs uiTabs)
    {
        if (IsBusy) return;
        if (CommitEditors?.Invoke() == false) return;
        if (!config.CanUse) { Warn("Cannot Save Configuration", "Connect to a SAP2000 model before saving a configuration."); return; }

        IsBusy = true;
        try
        {
            var outcome = await config.SaveAsync(uiTabs, () => PickSavePath(uiTabs));
            switch (outcome.Result)
            {
                case SaveResult.Saved:
                    Message = uiTabs == ConfigTabs.All
                        ? $"Project configuration saved · {config.Session.FileName}"
                        : $"{TabsLabel(uiTabs)} configuration saved · {config.Session.FileName}";
                    break;
                case SaveResult.Cancelled:
                    if (!string.IsNullOrEmpty(outcome.Message)) Message = outcome.Message;
                    break;
                case SaveResult.Blocked:
                    Warn("Unable to Save Configuration", outcome.Message);
                    Message = "Save blocked — the project file is unchanged.";
                    break;
            }
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            Warn("Unable to Save Configuration", ex.Message);
            if (ex is System.Runtime.InteropServices.COMException) State.Connection = "Connection Lost";
        }
        finally { IsBusy = false; Update(); }
    }

    // Load Config = open a project. Full-or-nothing: every present tab must validate against the
    // connected model, or the current project is left untouched.
    private async void OpenProject()
    {
        if (IsBusy) return;
        if (CommitEditors?.Invoke() == false) return;
        if (!config.CanUse) { Warn("Cannot Load Configuration", "Connect to a SAP2000 model first."); return; }

        var dialog = new OpenFileDialog { Filter = ConfigFilter, CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        try
        {
            CotVcfConfigDto project;
            try { project = config.Read(dialog.FileName); }
            catch (Exception ex)
            {
                Warn("Unable to Load Configuration", ex.Message);
                Message = "Load cancelled — the current project is unchanged.";
                return;
            }

            var check = await config.ValidateAsync(project);
            if (!check.FileValid || !check.AllPresentOk)
            {
                Warn("Configuration Is Not Compatible With Current Model", OpenBlockReport(project, check));
                Message = "Project is not compatible with the current model — nothing was applied.";
                return;
            }

            applyingProject = true;
            try { config.Apply(project, check); }
            finally { applyingProject = false; }
            config.SetActiveProject(dialog.FileName, project);
            config.Session.ClearDirty();
            SelectedAppliedPoint = null;
            Update();

            var present = new List<string>();
            if (check.Loading.Present) present.Add("Loading");
            if (check.Truss.Present) present.Add("Truss Height");
            if (check.Cable.Present) present.Add("Cable Force");

            if (check.ModelAppearsRenamed)
            {
                Warn("Project Loaded",
                    $"All referenced objects and geometry were validated, but the connected model has a different file name.\n\n" +
                    $"Original model:\n{project.Model?.OriginalFileName}\n\nCurrent model:\n{CurrentModelFileName()}\n\nThe model appears to have been renamed.",
                    warning: true);
            }
            Message = $"Project {config.Session.FileName} opened · restored {string.Join(", ", present)}";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            Warn("Unable to Load Configuration", ex.Message);
            if (ex is System.Runtime.InteropServices.COMException) State.Connection = "Connection Lost";
        }
        finally { IsBusy = false; Update(); }
    }

    private string OpenBlockReport(CotVcfConfigDto project, ConfigValidationResult r)
    {
        var lines = new List<string>
        {
            $"Saved model:\n{project.Model?.FileName}",
            $"Current model:\n{CurrentModelFileName()}",
        };
        if (r.FileErrors.Count > 0) lines.Add(string.Join("\n", r.FileErrors.Select(e => "• " + e)));
        void Add(string name, TabCheck tc) { if (tc.Present && !tc.Ok) lines.Add($"{name}:\n" + string.Join("\n", tc.Problems.Select(p => "  • " + p))); }
        Add("Tab 02 – Loading", r.Loading);
        Add("Tab 03 – Truss Height", r.Truss);
        Add("Tab 04 – Cable Force", r.Cable);
        lines.Add("No configuration has been applied.");
        return string.Join("\n\n", lines);
    }

    private static string TabsLabel(ConfigTabs tabs)
    {
        if (tabs == ConfigTabs.All) return "Full configuration";
        if (tabs == ConfigTabs.None) return "Nothing";
        var parts = new List<string>();
        if (tabs.HasFlag(ConfigTabs.Loading)) parts.Add("Loading");
        if (tabs.HasFlag(ConfigTabs.Truss)) parts.Add("Truss Height");
        if (tabs.HasFlag(ConfigTabs.Cable)) parts.Add("Cable Force");
        return string.Join(" + ", parts);
    }

    private string CurrentModelFileName() => State.Model is { } m ? System.IO.Path.GetFileName(m.Path) : "(unknown)";

    private void Warn(string title, string body, bool warning = false) =>
        MessageBox.Show(body, title, System.Windows.MessageBoxButton.OK,
            warning ? System.Windows.MessageBoxImage.Warning : System.Windows.MessageBoxImage.Information);

    private void Local(Action action)
    {
        if (CommitEditors?.Invoke() == false) return;
        try { action(); }
        catch (Exception ex) { Message = ex.Message; }
        Update();
    }

    private async void Run(string pending, Func<Task> action, string done, bool commitEditors = true)
    {
        if (IsBusy) return;
        if (commitEditors && CommitEditors?.Invoke() == false) return;
        IsBusy = true;
        Message = pending;
        try { await action(); Message = done; }
        catch (Exception ex)
        {
            Message = ex.Message;
            if (ex is System.Runtime.InteropServices.COMException) State.Connection = "Connection Lost";
        }
        finally { IsBusy = false; Update(); }
    }

    public void Update()
    {
        Notify(nameof(GroupWarning));
        foreach (var name in new[]
        {
            nameof(ModelName), nameof(ModelPath), nameof(ModelVersion), nameof(LockState), nameof(GeometrySummary),
            nameof(ForceUnit), nameof(LengthUnit), nameof(FxHeader), nameof(FyHeader), nameof(FzHeader),
            nameof(TopSummary), nameof(MiddleSummary), nameof(BottomSummary), nameof(HeightSummary),
            nameof(BaselineStatus), nameof(CandidateCountText), nameof(CandidateWarning), nameof(PreviewDelta),
            nameof(TrussTopPoints), nameof(TrussMiddlePoints), nameof(TrussBottomPoints),
            nameof(CanViewTrussCandidates), nameof(TrussCandidatesHint),
            nameof(IterationSummary), nameof(SapStatus), nameof(StatusLine), nameof(AppliedPointsTitle), nameof(LoadStatus),
            nameof(TrussStatus), nameof(CableStatus), nameof(CableCount), nameof(VariableCableCount), nameof(PretensionCandidateCount),
            nameof(CanUseConfig), nameof(HasProject), nameof(ProjectName),
            nameof(LoadingCandidateCount), nameof(HeightCandidateCount), nameof(HeightCandidates),
            nameof(StudyModeLabel), nameof(StudyNeedsFixedHeight), nameof(StudyNeedsFixedPretension),
            nameof(FixedHeightIndex), nameof(FixedPretensionOrdinal), nameof(PretensionMaxOrdinal),
            nameof(FixedHeightValueText), nameof(FixedPretensionValueText),
            nameof(StudyLoadingCell), nameof(StudyHeightCell), nameof(StudyPretensionCell), nameof(StudyTotalRuns)
        }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }
}
