using COT_VCF.Application;
using COT_VCF.Domain;
using System.Collections;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace COT_VCF.Presentation;

public sealed class MainViewModel : Observable
{
    public ApplicationState State { get; }
    private readonly WorkflowService workflow;
    private readonly ISelectionService selection;
    private bool busy;
    private int selectedTab;
    private AppliedPoint? selectedAppliedPoint;
    private string message = "Connect to the running SAP2000 model to begin.";
    private string previewMessage = "Select point groups and capture the baseline.";

    public bool IsBusy { get => busy; private set { Set(ref busy, value); Notify(nameof(IsIdle)); CommandManager.InvalidateRequerySuggested(); } }
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
    public string HeightSummary => State.BaselineCaptured ? $"SAP: {State.CommittedHeight:F4} {LengthUnit}   |   Preview: {State.Truss.CurrentHeight:F4} {LengthUnit}" : "Baseline not captured";
    public string IterationSummary => $"MODEL\n{ModelName} - {State.Units}\n\nLOADING CRITERIA\n{State.AppliedPoints.Count} applied points - {State.LoadDefinitions.Count} load definitions\n\nTRUSS HEIGHT\n{State.Truss.MinimumHeight:F4} to {State.Truss.MaximumHeight:F4} {LengthUnit} - step {State.Truss.Increment:F4} {LengthUnit}\n\nCABLE FORCE\n{State.Cables.Count} force sources configured";
    public string SapStatus => State.IsSapConnected ? "Connected" : State.Connection;
    public string AppliedPointsTitle => $"APPLIED POINTS ({State.AppliedPoints.Count})";
    public string LoadStatus => State.AppliedPoints.Count == 0 ? "Not configured" : LoadingCriteriaValid() ? "Configured" : "Review criteria";
    public string TrussStatus => !State.BaselineCaptured ? "Not configured" : new COT_VCF.Geometry.TrussGeometryService().Validate(State.Baseline, State.Truss).Count > 0 ? "Review height" : "Preview ready";
    public string CableStatus => State.Cables.Count == 0 ? "Not configured" : new ConfigurationValidator().Validate(State).Any(i => i.Section == "Cable force") ? "Review ranges" : "Configured";
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

    public MainViewModel(ApplicationState state, WorkflowService workflow, ISelectionService selection)
    {
        State = state;
        this.workflow = workflow;
        this.selection = selection;
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
        AddCables = new(_ => SelectCables(), GeometryReady);
        RemoveLoads = new(p => { if (p is IList list) foreach (var row in list.Cast<PointLoadConfiguration>().ToArray()) State.PointLoads.Remove(row); Update(); }, () => !IsBusy);
        RemoveCables = new(p => { if (p is IList list) foreach (var row in list.Cast<CableForceConfiguration>().ToArray()) State.Cables.Remove(row); Update(); }, () => !IsBusy);
        Validate = new(_ =>
        {
            if (CommitEditors?.Invoke() == false) return;
            Issues.Clear();
            foreach (var issue in new ConfigurationValidator().Validate(State)) Issues.Add(issue);
            Message = Issues.Count == 0 ? "Configuration is valid. Iteration is planned for the next phase." : $"{Issues.Count} configuration issues need review.";
        }, () => !IsBusy);

        State.PropertyChanged += (_, _) => Update();
        State.Truss.PropertyChanged += (_, _) =>
        {
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
            Update();
        };
        foreach (var row in State.LoadDefinitions) Subscribe(row);
        State.LoadDefinitions.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null) foreach (LoadDefinition row in e.NewItems) Subscribe(row);
            Update();
        };
        State.Cables.CollectionChanged += (_, e) => { if (e.NewItems is not null) foreach (CableForceConfiguration row in e.NewItems) row.PropertyChanged += (_, _) => Update(); Update(); };
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

    private void Subscribe(LoadDefinition row) => row.PropertyChanged += (_, _) => Update();

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

    private void SelectCables()
    {
        var members = State.Geometry.Members.Where(m => m.Kind != MemberKind.Frame).ToDictionary(m => $"{m.Kind}: {m.Name}");
        var names = selection.Select("Select SAP2000 Cables / Tendons", members.Keys.Order().ToArray(), []);
        if (names is null) return;
        Run("Reading cable / tendon force sources...", () => workflow.AddCablesAsync(names.Select(n => members[n]).ToArray()), "Force sources loaded. Review ranges in the current force units.");
    }

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
            nameof(IterationSummary), nameof(SapStatus), nameof(AppliedPointsTitle), nameof(LoadStatus),
            nameof(TrussStatus), nameof(CableStatus)
        }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }
}
