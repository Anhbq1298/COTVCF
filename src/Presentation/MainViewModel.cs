using COT_VCF.Application;
using COT_VCF.Configuration;
using COT_VCF.Domain;
using Microsoft.Win32;
using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
    private readonly StudyExecutionCoordinator? studyExecution;
    private readonly StudyExecutionSnapshotFactory studySnapshotFactory;
    private bool busy;
    private int selectedTab;
    private AppliedPoint? selectedAppliedPoint;
    private CablePretensionConfig? selectedCable;
    private string message = "Connect to the running SAP2000 model to begin.";
    private string previewMessage = "Select point groups and capture the baseline.";
    private readonly StudyCompositionService studyComposer = new();
    private StudyCompositionResult studyComposition = new([], [], [], [], BigInteger.Zero, false, "", "");
    private StudyValidationState studyValidationState = StudyValidationState.NotValidated;
    private bool suppressStudySelectionEvents;
    private bool normalizingStudyCandidateIds;
    private bool studyRefreshPending;
    private bool isStudyRunning;
    private int requestedWorkerCount = 1;
    private int studyCompletedCount, studyFailedCount, studyRunningCount, studyQueuedCount, studySkippedCount, studyCancelledCount;
    private int studyPeakConcurrent;
    private StudyBuildState studyBuildState = StudyBuildState.ReadyToBuild;
    private CancellationTokenSource? studyCancellation;

    public bool IsBusy { get => busy; private set { Set(ref busy, value); Notify(nameof(IsIdle)); Notify(nameof(CanUseConfig)); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsIdle => !IsBusy;
    public bool IsStudyRunning
    {
        get => isStudyRunning;
        private set
        {
            if (!Set(ref isStudyRunning, value)) return;
            Notify(nameof(CanCancelStudy));
            Notify(nameof(CanRunStudy));
        }
    }
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
    public CablePretensionConfig? SelectedCable
    {
        get => selectedCable;
        set
        {
            if (!Set(ref selectedCable, value)) return;
            Notify(nameof(CablePreviewSummary));
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
    public string CablePreviewSummary => SelectedCable is null
        ? $"{CableCount} cable{(CableCount == 1 ? "" : "s")} configured — select a row or cable in the view"
        : $"Selected  ·  {SelectedCable.CableName}  ·  {SelectedCable.Status}";
    public string PretensionCandidateCount => State.Cables.Count == 0 || State.Cables.Any(c => c.CandidateCount <= 0)
        ? "0"
        : PretensionRuns.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    // ---- Tab 05 study composer ----
    private BigInteger PretensionRuns => State.Cables.Count == 0 || State.Cables.Any(c => c.CandidateCount <= 0)
        ? BigInteger.Zero
        : PretensionCandidates.TotalCount(State.Cables);
    private BigInteger LoadingRuns => LoadingCandidates.TotalCount(State.LoadDefinitions);
    public int LoadingCandidateCount => (int)BigInteger.Min(LoadingRuns, int.MaxValue);
    public int HeightCandidateCount => State.BaselineCaptured ? State.Truss.CandidateCount : 0;
    public IReadOnlyList<StudyHeightCandidate> HeightStudyCandidates => studyComposition.HeightCandidates;
    public IReadOnlyList<StudyPretensionCandidate> PretensionStudyCandidates => studyComposition.PretensionCandidates;
    public IReadOnlyList<StudyIteration> StudyIterations => studyComposition.Iterations;
    public IReadOnlyList<int> WorkerCountOptions { get; } = [1, 2, 3];
    public int RequestedWorkerCount
    {
        get => requestedWorkerCount;
        set
        {
            var next = Math.Clamp(value, 1, MaxWorkerCount);
            if (!Set(ref requestedWorkerCount, next)) return;
            Notify(nameof(RunStudyLabel));
            Notify(nameof(RunStudyToolTip));
        }
    }
    public int MaxWorkerCount => 3;
    public ObservableCollection<SapWorkerSnapshot> StudyWorkers { get; } = [];
    public int StudyCompletedCount => studyCompletedCount;
    public int StudyFailedCount => studyFailedCount;
    public int StudyRunningCount => studyRunningCount;
    public int StudyQueuedCount => studyQueuedCount;
    public int StudySkippedCount => studySkippedCount;
    public int StudyCancelledCount => studyCancelledCount;
    public int StudyFinishedCount => studyCompletedCount + studyFailedCount + studyCancelledCount;
    public double StudyOverallProgress => SelectedRunCount == 0 ? 0 : Math.Min(100, 100.0 * StudyFinishedCount / SelectedRunCount);
    public string StudyProgressText => $"{StudyFinishedCount:N0} / {SelectedRunCount:N0}";
    public string StudyCountsText => $"Ready {StudyCompletedCount:N0}   Running {StudyRunningCount:N0}   Queued {StudyQueuedCount:N0}   Failed {StudyFailedCount:N0}   Skipped {StudySkippedCount:N0}   Workers {StudyWorkers.Count}   Peak {studyPeakConcurrent}";
    public bool HasStudyWorkers => StudyWorkers.Count > 0;
    public bool CanCancelStudy => IsStudyRunning && studyCancellation is { IsCancellationRequested: false };
    public StudyBuildState StudyBuildState => studyBuildState;
    public string StudyBuildStateText => studyBuildState switch
    {
        StudyBuildState.ReadyToBuild => "READY TO BUILD",
        StudyBuildState.Completed => "BUILD COMPLETE",
        StudyBuildState.CompletedWithErrors => "BUILD COMPLETED WITH ERRORS",
        StudyBuildState.Cancelled => "BUILD CANCELLED",
        StudyBuildState.Failed => "BUILD FAILED",
        _ => $"BUILDING - {RequestedWorkerCount} INSTANCE{(RequestedWorkerCount == 1 ? "" : "S")}"
    };

    public string StudyModeLabel => State.Study.Mode switch
    {
        IterationStudyMode.FixPretension => "Fix Pretension",
        IterationStudyMode.FixHeight => "Fix Height",
        _ => "Combined",
    };
    public bool StudyNeedsFixedHeight => State.Study.Mode == IterationStudyMode.FixHeight;
    public bool StudyNeedsFixedPretension => State.Study.Mode == IterationStudyMode.FixPretension;

    public string FixedHeightCandidateId
    {
        get => State.Study.FixedHeightCandidateId;
        set
        {
            var next = value ?? "";
            if (string.IsNullOrWhiteSpace(next) && HeightStudyCandidates.Any(candidate => candidate.Id == State.Study.FixedHeightCandidateId)) return;
            State.Study.FixedHeightCandidateId = next;
        }
    }
    public string FixedPretensionCandidateId
    {
        get => State.Study.FixedPretensionCandidateId;
        set
        {
            var next = value ?? "";
            if (string.IsNullOrWhiteSpace(next) && PretensionStudyCandidates.Any(candidate => candidate.Id == State.Study.FixedPretensionCandidateId)) return;
            State.Study.FixedPretensionCandidateId = next;
        }
    }
    public string FixedHeightValueText
    {
        get
        {
            var candidate = HeightStudyCandidates.FirstOrDefault(c => c.Id == FixedHeightCandidateId);
            return candidate is null
                ? "No height candidates are available from Tab 3."
                : $"{candidate.Id}: baseline {State.Truss.BaselineHeight:F3} \u2192 {candidate.Candidate.Height:F3} {LengthUnit} (\u0394H {candidate.Candidate.DeltaH:+0.###;-0.###;0} {LengthUnit})";
        }
    }
    public string FixedPretensionValueText
    {
        get
        {
            var candidate = PretensionStudyCandidates.FirstOrDefault(c => c.Id == FixedPretensionCandidateId);
            if (candidate is null) return "No pretension candidates are available from Tab 4.";
            var forces = candidate.Candidate.CableForces;
            return forces.Count <= 3
                ? $"{candidate.Id}: " + string.Join(",  ", forces.Select(kv => $"{kv.Key}={kv.Value:0.###} kN"))
                : $"{candidate.Id}: {forces.Count} cables, target range {forces.Values.Min():0.###}-{forces.Values.Max():0.###} kN";
        }
    }

    public string StudyLoadingCell => $"{LoadingCandidateCount:N0} used";
    public string StudyHeightCell => StudyNeedsFixedHeight ? $"1 ({FixedHeightCandidateId} fixed)" : $"{HeightCandidateCount:N0} used";
    public string StudyPretensionCell => StudyNeedsFixedPretension ? $"1 ({FixedPretensionCandidateId} fixed)" : $"{PretensionRuns:N0} used";
    public string StudySpaceFormula
    {
        get
        {
            var h = StudyNeedsFixedHeight ? "1" : HeightCandidateCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var p = StudyNeedsFixedPretension ? "1" : PretensionRuns.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            return $"{LoadingCandidateCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} \\times {h} \\times {p} = {StudyTotalRuns}";
        }
    }
    public string StudyTotalRuns => StudyPlan.TotalRuns(State.Study.Mode, LoadingCandidateCount, HeightCandidateCount, PretensionRuns)
        .ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    public string BaselineModelName => State.SessionBaselineModel?.Name ?? "Baseline unavailable";
    public string BaselineModelPath => State.SessionBaselineModel?.Path ?? "";
    public string StudyRoot
    {
        get
        {
            try { return new StudyPathService().GetStudyRoot(BaselineModelPath); }
            catch { return "Study root unavailable"; }
        }
    }
    public int GeneratedRunCount => StudyIterations.Count;
    public int SelectedRunCount => StudyIterations.Count(i => i.RunIterate);
    public string StudySelectionSummary => $"{SelectedRunCount:N0} / {GeneratedRunCount:N0} selected";
    public bool HasStudyIterations => StudyIterations.Count > 0;
    public bool ShowStudyEmptyState => !HasStudyIterations;
    public string StudyPreviewEmptyText => studyComposition.PreviewLimitExceeded
        ? $"Study space is too large to preview. Reduce it below {StudyCompositionService.MaxPreviewRows:N0} iterations."
        : "No study iterations generated.";
    public StudyValidationState StudyValidationState => studyValidationState;
    public bool HasValidationIssues => Issues.Count > 0;
    public bool ShowValidationStatus => Issues.Count == 0;
    public string ValidationStatusText => studyValidationState switch
    {
        StudyValidationState.Valid => "No validation issues.",
        StudyValidationState.ValidWithWarnings => "Study is valid with warnings.",
        StudyValidationState.Invalid => "Study validation failed.",
        _ => "Not validated yet."
    };
    public string RunStudyLabel => $"Build {SelectedRunCount:N0} Models - {RequestedWorkerCount} Instance{(RequestedWorkerCount == 1 ? "" : "s")}";
    public bool CanRunStudy => !IsBusy && studyExecution is not null && State.IsSapConnected && SelectedRunCount > 0;
    public string RunStudyToolTip => RequestedWorkerCount == 1
        ? "Uses the connected SAP2000 instance as worker 1; no additional SAP2000 process is started."
        : $"Uses the connected SAP2000 instance as worker 1 and starts {RequestedWorkerCount - 1} additional processes. Up to {RequestedWorkerCount} models build concurrently.";
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
    public Command SelectAllStudyRuns { get; }
    public Command ClearAllStudyRuns { get; }
    public Command BuildSelectedModels { get; }
    public Command CancelStudy { get; }
    public Command OpenStudyFolder { get; }
    public Command OpenIterationFolder { get; }
    public Command ViewIterationError { get; }
    public Command SaveTabConfig { get; }
    public Command SaveAllConfig { get; }
    public Command LoadConfig { get; }

    public bool CanUseConfig => !IsBusy && State.IsSapConnected && State.Model is not null;
    private bool applyingProject;

    public MainViewModel(
        ApplicationState state,
        WorkflowService workflow,
        ISelectionService selection,
        ConfigService config,
        StudyExecutionCoordinator? studyExecution = null,
        StudyExecutionSnapshotFactory? studySnapshotFactory = null)
    {
        State = state;
        this.workflow = workflow;
        this.selection = selection;
        this.config = config;
        this.studyExecution = studyExecution;
        this.studySnapshotFactory = studySnapshotFactory ?? new();
        StudyWorkers.CollectionChanged += (_, _) =>
        {
            Notify(nameof(HasStudyWorkers));
            NotifyStudyExecution();
        };
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
        RemoveCables = new(p =>
        {
            if (p is not IList list) return;
            var removed = list.Cast<CablePretensionConfig>().ToArray();
            foreach (var row in removed) State.Cables.Remove(row);
            if (SelectedCable is not null && removed.Contains(SelectedCable)) SelectedCable = null;
            Update();
        }, () => !IsBusy);
        Validate = new(_ =>
        {
            if (CommitEditors?.Invoke() == false) return;
            RegenerateStudyPreview();
            ValidateStudy(showMessage: true);
        }, () => !IsBusy);
        SelectAllStudyRuns = new(_ => SetAllStudyRuns(true), () => !IsBusy && StudyIterations.Count > 0);
        ClearAllStudyRuns = new(_ => SetAllStudyRuns(false), () => !IsBusy && StudyIterations.Count > 0);
        BuildSelectedModels = new(_ => RunStudy(), () => CanRunStudy);
        CancelStudy = new(_ => CancelStudyExecution(), () => CanCancelStudy);
        OpenStudyFolder = new(_ => TryOpenFolder(StudyRoot), () => Directory.Exists(StudyRoot));
        OpenIterationFolder = new(p => { if (p is StudyIteration row) TryOpenFolder(row.IterationDirectory); });
        ViewIterationError = new(p =>
        {
            if (p is StudyIteration { ErrorMessage.Length: > 0 } row)
                Warn($"{row.IterationId} - Build Error", row.ErrorMessage);
        });
        SaveTabConfig = new(p => SaveTab(p switch
        {
            "Loading" => ConfigTabs.Loading,
            "Truss" => ConfigTabs.Truss,
            "Cable" => ConfigTabs.Cable,
            "Study" => ConfigTabs.Study,
            _ => ConfigTabs.All,
        }), () => CanUseConfig);
        SaveAllConfig = new(_ => SaveAll(), () => CanUseConfig);
        LoadConfig = new(_ => OpenProject(), () => CanUseConfig);

        void Dirty(ConfigTabs tab) { if (!applyingProject) config.Session.MarkDirty(tab); }

        State.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ApplicationState.TributaryWidthB) or nameof(ApplicationState.NumberOfStories)) Dirty(ConfigTabs.Loading);
            if (e.PropertyName is nameof(ApplicationState.Model) or nameof(ApplicationState.Connection) or
                nameof(ApplicationState.SessionBaselineModel) or nameof(ApplicationState.SessionBaseline) or
                nameof(ApplicationState.TributaryWidthB) or nameof(ApplicationState.NumberOfStories)) ScheduleStudyRegeneration();
            Update();
        };
        State.Truss.PropertyChanged += (_, e) =>
        {
            Dirty(ConfigTabs.Truss);
            if (State.BaselineCaptured)
            {
                try { workflow.UpdatePreview(); PreviewMessage = "Preview only - Apply geometry commits these coordinates to SAP2000."; }
                catch (Exception ex) { PreviewMessage = ex.Message; }
            }
            if (e.PropertyName != nameof(TrussHeightConfiguration.CurrentHeight)) ScheduleStudyRegeneration();
            Update();
        };
        State.PointLoads.CollectionChanged += (_, e) => { if (e.NewItems is not null) foreach (PointLoadConfiguration row in e.NewItems) row.PropertyChanged += (_, _) => Update(); Update(); };
        State.AppliedPoints.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null) foreach (AppliedPoint point in e.NewItems) point.PropertyChanged += (_, _) => Update();
            Dirty(ConfigTabs.Loading);
            ScheduleStudyRegeneration();
            Update();
        };
        foreach (var row in State.LoadDefinitions) Subscribe(row);
        State.LoadDefinitions.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null) foreach (LoadDefinition row in e.NewItems) Subscribe(row);
            Dirty(ConfigTabs.Loading);
            ScheduleStudyRegeneration();
            Update();
        };
        State.Cables.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (CablePretensionConfig row in e.NewItems)
                    row.PropertyChanged += (_, _) => { Dirty(ConfigTabs.Cable); ScheduleStudyRegeneration(); Update(); };
            if (SelectedCable is not null && !State.Cables.Contains(SelectedCable)) SelectedCable = null;
            Dirty(ConfigTabs.Cable);
            ScheduleStudyRegeneration();
            Update();
        };
        State.Study.PropertyChanged += (_, e) =>
        {
            if (normalizingStudyCandidateIds) return;
            Dirty(ConfigTabs.Study);
            if (e.PropertyName != nameof(IterationStudyConfig.ExcludedIterationKeys)) ScheduleStudyRegeneration();
            else InvalidateStudyValidation();
            Update();
        };
        RegenerateStudyPreview();
    }

    private async Task ConnectAndRefreshAsync()
    {
        State.Connection = "Connecting";
        try
        {
            await workflow.ConnectAsync();
            await workflow.RefreshAsync();
            if (State.SessionBaselineModel is { Path.Length: > 0 } baseline)
            {
                var modelDirectory = System.IO.Path.GetDirectoryName(baseline.Path);
                if (!string.IsNullOrWhiteSpace(modelDirectory)) State.Study.OutputDirectory = System.IO.Path.Combine(modelDirectory, "Study");
            }
        }
        catch
        {
            State.Connection = "Error";
            throw;
        }
    }

    private void Subscribe(LoadDefinition row) => row.PropertyChanged += (_, _) =>
    {
        if (!applyingProject) config.Session.MarkDirty(ConfigTabs.Loading);
        ScheduleStudyRegeneration();
        Update();
    };

    private void ScheduleStudyRegeneration()
    {
        if (IsStudyRunning) return;
        if (studyRefreshPending) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            RegenerateStudyPreview();
            return;
        }
        studyRefreshPending = true;
        dispatcher.BeginInvoke(() =>
        {
            studyRefreshPending = false;
            RegenerateStudyPreview();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void RegenerateStudyPreview()
    {
        if (IsStudyRunning) return;
        studyRefreshPending = false;
        var nextComposition = studyComposer.Compose(State);
        var heightCandidatesChanged = !EquivalentHeightCandidates(
            studyComposition.HeightCandidates, nextComposition.HeightCandidates);
        var pretensionCandidatesChanged = !EquivalentPretensionCandidates(
            studyComposition.PretensionCandidates, nextComposition.PretensionCandidates);
        if (!heightCandidatesChanged)
            nextComposition = nextComposition with { HeightCandidates = studyComposition.HeightCandidates };
        if (!pretensionCandidatesChanged)
            nextComposition = nextComposition with { PretensionCandidates = studyComposition.PretensionCandidates };
        studyComposition = nextComposition;
        bool normalized = false;
        normalizingStudyCandidateIds = true;
        try
        {
            if (studyComposition.EffectiveFixedHeightId.Length > 0 &&
                State.Study.FixedHeightCandidateId != studyComposition.EffectiveFixedHeightId)
            {
                State.Study.FixedHeightCandidateId = studyComposition.EffectiveFixedHeightId;
                normalized = true;
            }
            if (studyComposition.EffectiveFixedPretensionId.Length > 0 &&
                State.Study.FixedPretensionCandidateId != studyComposition.EffectiveFixedPretensionId)
            {
                State.Study.FixedPretensionCandidateId = studyComposition.EffectiveFixedPretensionId;
                normalized = true;
            }
        }
        finally { normalizingStudyCandidateIds = false; }
        if (normalized && !applyingProject) config.Session.MarkDirty(ConfigTabs.Study);
        foreach (var row in StudyIterations) row.PropertyChanged += StudyIterationPropertyChanged;
        InvalidateStudyValidation();
        NotifyStudyPresentation(heightCandidatesChanged, pretensionCandidatesChanged);
    }

    private static bool EquivalentHeightCandidates(
        IReadOnlyList<StudyHeightCandidate> current,
        IReadOnlyList<StudyHeightCandidate> next) => current.SequenceEqual(next);

    private static bool EquivalentPretensionCandidates(
        IReadOnlyList<StudyPretensionCandidate> current,
        IReadOnlyList<StudyPretensionCandidate> next)
    {
        if (current.Count != next.Count) return false;
        for (int i = 0; i < current.Count; i++)
        {
            var left = current[i];
            var right = next[i];
            if (left.Id != right.Id || left.Candidate.CombinationIndex != right.Candidate.CombinationIndex ||
                left.Candidate.CableForces.Count != right.Candidate.CableForces.Count)
                return false;
            foreach (var force in left.Candidate.CableForces)
                if (!right.Candidate.CableForces.TryGetValue(force.Key, out var value) || value != force.Value)
                    return false;
        }
        return true;
    }

    private void StudyIterationPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StudyIteration.RunIterate) || sender is not StudyIteration row || suppressStudySelectionEvents) return;
        State.Study.SetRunSelected(row.IterationKey, row.RunIterate);
        config.Session.MarkDirty(ConfigTabs.Study);
        InvalidateStudyValidation();
        NotifyStudyPresentation();
    }

    private void SetAllStudyRuns(bool selected)
    {
        suppressStudySelectionEvents = true;
        try
        {
            foreach (var row in StudyIterations)
            {
                row.RunIterate = selected;
                State.Study.SetRunSelected(row.IterationKey, selected);
            }
        }
        finally { suppressStudySelectionEvents = false; }
        config.Session.MarkDirty(ConfigTabs.Study);
        InvalidateStudyValidation();
        NotifyStudyPresentation();
    }

    private void InvalidateStudyValidation()
    {
        studyValidationState = StudyValidationState.NotValidated;
        Issues.Clear();
    }

    private void NotifyStudyPresentation(bool heightCandidatesChanged = false, bool pretensionCandidatesChanged = false)
    {
        if (heightCandidatesChanged) Notify(nameof(HeightStudyCandidates));
        if (pretensionCandidatesChanged) Notify(nameof(PretensionStudyCandidates));
        foreach (var name in new[]
        {
            nameof(StudyIterations),
            nameof(FixedHeightCandidateId), nameof(FixedPretensionCandidateId),
            nameof(FixedHeightValueText), nameof(FixedPretensionValueText), nameof(StudyLoadingCell),
            nameof(StudyHeightCell), nameof(StudyPretensionCell), nameof(StudySpaceFormula), nameof(StudyTotalRuns),
            nameof(BaselineModelName), nameof(BaselineModelPath), nameof(StudyRoot), nameof(GeneratedRunCount), nameof(SelectedRunCount),
            nameof(StudySelectionSummary), nameof(HasStudyIterations), nameof(ShowStudyEmptyState), nameof(StudyPreviewEmptyText),
            nameof(StudyValidationState), nameof(HasValidationIssues), nameof(ShowValidationStatus), nameof(ValidationStatusText),
            nameof(RunStudyLabel), nameof(CanRunStudy), nameof(RunStudyToolTip)
        }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }

    private bool ValidateStudy(bool showMessage)
    {
        Issues.Clear();
        foreach (var issue in new StudyValidationService().Validate(State, studyComposition)) Issues.Add(issue);
        studyValidationState = Issues.Any(issue => issue.Severity == ValidationSeverity.Error)
            ? StudyValidationState.Invalid
            : Issues.Any(issue => issue.Severity == ValidationSeverity.Warning)
                ? StudyValidationState.ValidWithWarnings
                : StudyValidationState.Valid;
        if (showMessage)
            Message = studyValidationState is StudyValidationState.Valid or StudyValidationState.ValidWithWarnings
                ? $"Study is valid - {SelectedRunCount:N0} iterations are ready to build."
                : $"{Issues.Count} study validation issue{(Issues.Count == 1 ? "" : "s")} need review.";
        NotifyStudyPresentation();
        return studyValidationState is StudyValidationState.Valid or StudyValidationState.ValidWithWarnings;
    }

    private async void RunStudy()
    {
        if (IsBusy || studyExecution is null) return;
        if (CommitEditors?.Invoke() == false) return;
        RegenerateStudyPreview();
        if (!ValidateStudy(showMessage: true)) return;

        StudyExecutionSnapshot snapshot;
        try { snapshot = studySnapshotFactory.Create(State, studyComposition, RequestedWorkerCount); }
        catch (Exception ex)
        {
            studyBuildState = StudyBuildState.Failed;
            Message = ex.Message;
            Warn("Unable to Build Study Models", ex.Message);
            NotifyStudyExecution();
            return;
        }

        foreach (var row in StudyIterations)
        {
            row.AssignedWorkerId = null;
            row.CurrentStageText = null;
            row.ErrorMessage = null;
            row.Status = row.RunIterate ? StudyIterationStatus.Queued : StudyIterationStatus.Skipped;
        }
        studyCompletedCount = studyFailedCount = studyRunningCount = studyCancelledCount = studyPeakConcurrent = 0;
        studyQueuedCount = snapshot.Jobs.Count(job => job.RunIterate);
        studySkippedCount = snapshot.Jobs.Count - studyQueuedCount;
        StudyWorkers.Clear();
        for (int id = 1; id <= RequestedWorkerCount; id++)
            StudyWorkers.Add(new SapWorkerSnapshot { WorkerId = id, State = SapWorkerState.Offline });

        studyCancellation = new CancellationTokenSource();
        studyBuildState = StudyBuildState.Running;
        IsStudyRunning = true;
        IsBusy = true;
        Message = RequestedWorkerCount == 1
            ? "Using the connected SAP2000 instance as study worker 1; no additional process will be started."
            : $"Using the connected SAP2000 instance as worker 1 and starting {RequestedWorkerCount - 1} additional SAP2000 process{(RequestedWorkerCount == 2 ? "" : "es")}...";
        NotifyStudyExecution();

        var progress = new Progress<StudyExecutionProgress>(ApplyStudyProgress);
        try
        {
            var result = await studyExecution.BuildAsync(snapshot, progress, studyCancellation.Token);
            studyCompletedCount = result.Completed;
            studyFailedCount = result.Failed;
            studyCancelledCount = result.Cancelled;
            studyRunningCount = 0;
            studyQueuedCount = 0;
            studySkippedCount = result.Skipped;
            studyPeakConcurrent = result.PeakConcurrent;
            studyBuildState = result.AllWorkersFaulted
                ? StudyBuildState.Failed
                : result.Cancelled > 0 && studyCancellation.IsCancellationRequested
                    ? StudyBuildState.Cancelled
                    : result.Failed > 0 ? StudyBuildState.CompletedWithErrors : StudyBuildState.Completed;
            Message = studyBuildState switch
            {
                StudyBuildState.Completed => $"Build complete - {result.Completed:N0} child models are ready.",
                StudyBuildState.CompletedWithErrors => $"Build completed with errors - {result.Completed:N0} ready, {result.Failed:N0} failed.",
                StudyBuildState.Cancelled => $"Build cancelled - {result.Completed:N0} ready, {result.Cancelled:N0} cancelled.",
                _ => $"Build failed - all SAP2000 workers faulted; {result.Completed:N0} models are ready and {result.Failed:N0} failed."
            };
        }
        catch (OperationCanceledException)
        {
            studyBuildState = StudyBuildState.Cancelled;
            MarkUnfinishedIterations(StudyIterationStatus.Cancelled, "Cancelled before worker assignment.");
            Message = "Study build cancelled at a safe SAP2000 stage boundary.";
        }
        catch (Exception ex)
        {
            studyBuildState = StudyBuildState.Failed;
            MarkUnfinishedIterations(StudyIterationStatus.Failed, ex.Message, leaveUnassignedPlanned: true);
            Message = ex.Message;
            Warn("Study Build Failed", ex.Message);
            if (ex is System.Runtime.InteropServices.COMException) State.Connection = "Connection Lost";
        }
        finally
        {
            studyCancellation.Dispose();
            studyCancellation = null;
            IsStudyRunning = false;
            IsBusy = false;
            NotifyStudyExecution();
            Update();
        }
    }

    private void CancelStudyExecution()
    {
        if (studyCancellation is null || studyCancellation.IsCancellationRequested) return;
        studyCancellation.Cancel();
        Message = $"Cancellation requested. {StudyRunningCount:N0} running candidate{(StudyRunningCount == 1 ? "" : "s")} will stop at the next safe API boundary.";
        NotifyStudyExecution();
    }

    private void ApplyStudyProgress(StudyExecutionProgress progress)
    {
        if (progress.Total > 0)
        {
            studyCompletedCount = progress.Completed;
            studyFailedCount = progress.Failed;
            studyCancelledCount = progress.Cancelled;
            studyRunningCount = progress.Running;
            studyQueuedCount = progress.Queued;
            studySkippedCount = progress.Skipped;
            studyPeakConcurrent = Math.Max(studyPeakConcurrent, progress.Running);
        }

        if (progress.WorkerId > 0)
        {
            var worker = StudyWorkers.FirstOrDefault(item => item.WorkerId == progress.WorkerId);
            if (worker is null)
            {
                worker = new SapWorkerSnapshot { WorkerId = progress.WorkerId };
                StudyWorkers.Add(worker);
            }
            worker.State = progress.WorkerState;
            worker.CurrentStage = progress.Stage;
            worker.CurrentIterationId = progress.WorkerState is SapWorkerState.Ready or SapWorkerState.Offline
                ? null : progress.IterationId;
            worker.CurrentModelPath = progress.CurrentModelPath;
            worker.CompletedCount = progress.WorkerCompletedCount;
            worker.LastMessage = progress.Message;
        }

        if (progress.IterationId is { Length: > 0 } iterationId &&
            StudyIterations.FirstOrDefault(row => row.IterationId == iterationId) is { } row)
        {
            if (progress.WorkerId > 0) row.AssignedWorkerId = progress.WorkerId;
            if (progress.IterationStatus is { } status) row.Status = status;
            row.CurrentStageText = progress.Message;
            if (progress.IterationStatus is StudyIterationStatus.Failed or StudyIterationStatus.Cancelled)
                row.ErrorMessage = progress.Message;
        }
        if (IsStudyRunning && progress.Message.Length > 0)
            Message = progress.IterationId is { Length: > 0 }
                ? $"{progress.IterationId} - {progress.Message}"
                : progress.Message;
        NotifyStudyExecution();
    }

    private void MarkUnfinishedIterations(
        StudyIterationStatus terminalStatus,
        string message,
        bool leaveUnassignedPlanned = false)
    {
        foreach (var row in StudyIterations.Where(row => row.RunIterate && row.Status is not
                     (StudyIterationStatus.Ready or StudyIterationStatus.Failed or StudyIterationStatus.Cancelled)))
        {
            if (leaveUnassignedPlanned && row.AssignedWorkerId is null)
            {
                row.Status = StudyIterationStatus.Planned;
                continue;
            }
            row.Status = terminalStatus;
            row.ErrorMessage = message;
        }
    }

    private void NotifyStudyExecution()
    {
        foreach (var name in new[]
        {
            nameof(IsStudyRunning), nameof(CanCancelStudy), nameof(CanRunStudy), nameof(StudyWorkers),
            nameof(HasStudyWorkers), nameof(StudyCompletedCount), nameof(StudyFailedCount),
            nameof(StudyRunningCount), nameof(StudyQueuedCount), nameof(StudySkippedCount),
            nameof(StudyCancelledCount), nameof(StudyFinishedCount), nameof(StudyOverallProgress),
            nameof(StudyProgressText), nameof(StudyCountsText), nameof(StudyBuildState),
            nameof(StudyBuildStateText), nameof(RunStudyLabel), nameof(RunStudyToolTip)
        }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }

    private static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"The study folder does not exist: {path}");
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void TryOpenFolder(string path)
    {
        try { OpenFolder(path); }
        catch (Exception ex) { Message = ex.Message; }
    }

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
        State.LoadDefinitions.Count > 0 && State.LoadDefinitions.All(row => row.CandidateValues.Count > 0 && State.LoadPatterns.Contains(row.LoadName, StringComparer.Ordinal)) &&
        !State.LoadDefinitions.GroupBy(row => row.LoadName, StringComparer.Ordinal).Any(group => group.Count() > 1);

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

        // A loading combination is a Cartesian pick of one qz per load definition.
        var combinations = LoadingCandidates.Enumerate(State.LoadDefinitions.ToArray()).ToArray();
        if (combinations.Length == 0)
        {
            Message = "No loading combinations — every load definition needs at least one candidate value.";
            return null;
        }

        var numberOfStories = (int)State.NumberOfStories;
        var rows = new List<LoadingCandidate>(points.Length * combinations.Length * State.LoadDefinitions.Count);
        foreach (var point in points)
        {
            var area = tributaryAreas[point.PointName];
            foreach (var combo in combinations)
                foreach (var (name, qz) in combo.Loads)
                    rows.Add(new(point.PointName, $"C{combo.Index + 1} · {name}", qz, area, numberOfStories, -qz * area * numberOfStories));
        }
        Message = $"Generated {combinations.Length} loading combination{(combinations.Length == 1 ? "" : "s")} and {rows.Count} candidate point loads.";
        return new(combinations.Length, rows);
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
        CablePretensionConfig? firstAdded = null;
        foreach (var name in picked.Distinct(StringComparer.Ordinal))
            if (existing.Add(name))
            {
                var cable = new CablePretensionConfig { CableName = name };
                State.Cables.Add(cable);
                firstAdded ??= cable;
                added++;
            }

        if (firstAdded is not null) SelectedCable = firstAdded;
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
            RegenerateStudyPreview();
            config.SetActiveProject(dialog.FileName, project);
            config.Session.ClearDirty();
            SelectedAppliedPoint = null;
            SelectedCable = null;
            Update();

            var present = new List<string>();
            if (check.Loading.Present) present.Add("Loading");
            if (check.Truss.Present) present.Add("Truss Height");
            if (check.Cable.Present) present.Add("Cable Force");
            if (check.Study.Present) present.Add("Iteration Study");

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
        Add("Tab 05 - Iteration Study", r.Study);
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
        if (tabs.HasFlag(ConfigTabs.Study)) parts.Add("Iteration Study");
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
            nameof(CablePreviewSummary),
            nameof(CanUseConfig), nameof(HasProject), nameof(ProjectName),
            nameof(LoadingCandidateCount), nameof(HeightCandidateCount),
            nameof(StudyModeLabel), nameof(StudyNeedsFixedHeight), nameof(StudyNeedsFixedPretension),
            nameof(StudyRoot), nameof(RequestedWorkerCount), nameof(MaxWorkerCount)
        }) Notify(name);
        NotifyStudyPresentation();
    }
}
