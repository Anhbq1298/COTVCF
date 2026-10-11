using COT_VCF.Application;
using COT_VCF.Application.Quantities;
using COT_VCF.Application.Results;
using COT_VCF.Configuration;
using COT_VCF.Domain;
using COT_VCF.Geometry;
using COT_VCF.Infrastructure;
using Microsoft.Win32;
using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;

namespace COT_VCF.Presentation;

public sealed partial class MainViewModel : Observable
{
    public ApplicationState State { get; }
    private readonly WorkflowService workflow;
    private readonly ISelectionService selection;
    private readonly ConfigService config;
    private readonly StudyExecutionCoordinator? studyExecution;
    private readonly StudyExecutionSnapshotFactory studySnapshotFactory;
    private readonly StudyResumeService studyResume = new();
    private readonly StudyPreflightService studyPreflight = new();
    private readonly IOutputSettingsDialogService? outputDialogs;
    private readonly OutputGroupPreparationService? outputGroupPreparation;
    private readonly ReportPrintSettingsStore? printStore;
    private readonly FemWorkingSessionStore femSessionStore;
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
    private int requestedWorkerCount = 2;
    private int studyCompletedCount, studyReusedCount, studyFailedCount, studyRunningCount, studyQueuedCount, studySkippedCount, studyCancelledCount;
    private int studyPeakConcurrent;
    private StudyBuildState studyBuildState = StudyBuildState.ReadyToBuild;
    private CancellationTokenSource? studyCancellation;
    private StudyResumeAssessment? resumeAssessment;
    private string resumeStatusText = "No compatible interrupted run inspected.";
    private StudyPreflightReport preflightReport = StudyPreflightReport.Empty;

    public bool IsBusy { get => busy; private set { Set(ref busy, value); Notify(nameof(IsIdle)); Notify(nameof(CanUseConfig)); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsIdle => !IsBusy;
    public bool HasSapConnection => State.IsSapConnected && State.Model is not null;
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
    public int SelectedTab
    {
        get => selectedTab;
        set
        {
            if (!Set(ref selectedTab, value)) return;
            NotifySharedViewer();
            NotifyPaneHeader();
            if (value == TabOptimization) RefreshOptimization();     // also picks up Tab 03 capacity edits made meanwhile
            if (value == TabQuantities) ShowQuantityTab();            // the running model again, and candidates built meanwhile
        }
    }
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
            Notify(nameof(SharedViewerCaption));
        }
    }

    public string ModelName => State.Model?.Name ?? "No model connected";
    public string ModelPath => State.Model is { Path.Length: > 0 } model ? model.Path
        : State.Model is not null ? "Unsaved MCOTiS configuration" : "Open or create a model.";
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

    // ---- Hoisted model preview: shared by tabs 01-04 and 07 FEM ----
    private const int TabLoading = 0, TabTruss = 1, TabPretension = 2, TabOutput = 3, TabIteration = 4, TabFem = 7;     // 5 = Post-Processing, 6 = 06A Optimization (MainViewModel.Optimization.cs), 8 = Quantity Take-off (MainViewModel.Quantities.cs)
    public bool ShowSharedViewer => selectedTab is >= TabLoading and <= TabOutput || selectedTab == TabFem;
    public bool ShowTrussLegend => selectedTab == TabTruss;
    public GeometryModel SharedViewerModel =>
        (selectedTab == TabTruss ? State.Preview : State.Geometry) ?? GeometryModel.Empty;
    public IEnumerable<AppliedPoint>? SharedViewerAppliedPoints =>
        selectedTab is TabLoading or TabFem ? State.AppliedPoints : null;
    public IEnumerable<string>? SharedViewerTopPoints => selectedTab == TabTruss ? State.Truss.TopPoints : null;
    public IEnumerable<string>? SharedViewerMiddlePoints => selectedTab == TabTruss ? State.Truss.MiddlePoints : null;
    public IEnumerable<string>? SharedViewerBottomPoints => selectedTab == TabTruss ? State.Truss.BottomPoints : null;
    /// <summary>All chord points (any tab): the overall span dimension runs between the mega columns they end at.</summary>
    public IEnumerable<string> SharedViewerSpanPoints => State.Truss.TopPoints.Concat(State.Truss.MiddlePoints).Concat(State.Truss.BottomPoints).ToArray();
    public IReadOnlyList<string> FemPinSupportPoints
    {
        get
        {
            try { return FemStudyService.FindPinSupportPoints(State.Baseline); }
            catch { return []; }
        }
    }
    public IEnumerable<string>? SharedViewerPinSupportPoints => selectedTab == TabFem ? FemPinSupportPoints : null;
    public bool ShowFemPinLegend => selectedTab == TabFem;
    public IEnumerable<CablePretensionConfig>? SharedViewerCables =>
        selectedTab is TabPretension or TabOutput ? State.Cables : null;
    public string SharedViewerCaption => selectedTab switch
    {
        TabTruss => HeightSummary,
        TabPretension => CablePreviewSummary,
        TabOutput => GeometrySummary,
        TabFem when SelectedFemPage == FemResultsPage => FemResultDiagramTitle,
        TabFem => $"FEM · {GeometrySummary}",
        _ => "",
    };
    public string SharedViewerHint => selectedTab switch
    {
        TabLoading => "Applied load locations are preview overlays; grid and viewer selection stay in sync.",
        TabTruss => "Preview height only — Top red · Middle blue · Bottom green.",
        TabPretension => "Configured cables are highlighted and labelled by SAP2000 cable name.",
        TabOutput => "Point and member labels for picking deflection points, foundation reactions, member-force groups and monitored cables.",
        TabFem when SelectedFemPage == FemResultsPage && FemResultMode == FemResultMode.DeflectedShape => "Deformed shape is visually magnified; displacement values in the Results table are true scale.",
        TabFem when SelectedFemPage == FemResultsPage && FemResultMode == FemResultMode.Reactions => "Green vectors show support reaction direction; labels and the Results table show exact magnitudes and components.",
        TabFem when SelectedFemPage == FemResultsPage => "Blue = positive (tension for axial force); red = negative (compression). Diagram width is auto-scaled; table values are exact.",
        TabFem => "Internal 2D XZ FEM · Tab 01 loading points are highlighted; automatically detected base pins restrain X/Z translation and leave rotation free.",
        _ => "",
    };
    private void NotifySharedViewer()
    {
        Notify(nameof(ShowSharedViewer)); Notify(nameof(ShowTrussLegend));
        Notify(nameof(SharedViewerModel)); Notify(nameof(SharedViewerAppliedPoints));
        Notify(nameof(SharedViewerTopPoints)); Notify(nameof(SharedViewerSpanPoints)); Notify(nameof(SharedViewerMiddlePoints)); Notify(nameof(SharedViewerBottomPoints));
        Notify(nameof(SharedViewerCables)); Notify(nameof(SharedViewerPinSupportPoints)); Notify(nameof(ShowFemPinLegend));
        Notify(nameof(SharedViewerFemResult)); Notify(nameof(SharedViewerFemResultMode));
        Notify(nameof(SharedViewerFrameMeshSegments)); Notify(nameof(SharedViewerMemberSizes));
        Notify(nameof(SharedViewerShowLoads)); Notify(nameof(SharedViewerLoadValues));
        Notify(nameof(SharedViewerCaption)); Notify(nameof(SharedViewerHint));
    }
    private void NotifyFemResultSelection()
    {
        Notify(nameof(FemResultRows)); Notify(nameof(FemResultSummary));
        Notify(nameof(FemRunCableFitRows));
        Notify(nameof(FemResultStartHeader)); Notify(nameof(FemResultEndHeader));
        Notify(nameof(FemResultNames)); Notify(nameof(FemResultDiagramTitle));
        Notify(nameof(FemResultIssue));
        NotifySharedViewer();
    }
    public bool CanViewTrussCandidates => TrussCandidatesReady(out _);
    public string TrussCandidatesHint { get { TrussCandidatesReady(out var reason); return reason; } }
    public string IterationSummary => $"MODEL\n{ModelName} - {State.Units}\n\nLOADING CRITERIA\n{State.AppliedPoints.Count} applied points - {State.LoadDefinitions.Count} load definitions\n\nTRUSS HEIGHT\n{State.Truss.MinimumHeight:F4} to {State.Truss.MaximumHeight:F4} {LengthUnit} - step {State.Truss.Increment:F4} {LengthUnit}\n\nCABLE PRETENSION\n{CableCount} cables ({VariableCableCount} variable) - {PretensionCandidateCount} pretension candidates";
    public int CableCount => State.Cables.Count;
    public int VariableCableCount => COT_VCF.Application.PretensionCandidates.VariableCount(State.Cables);
    /// <summary>Sync-column mode options.</summary>
    public IReadOnlyList<CableSyncMode> CableSyncModes { get; } =
        [CableSyncMode.Independent, CableSyncMode.SameValues, CableSyncMode.SameStep,
         CableSyncMode.SameRatio, CableSyncMode.SameOffset];
    /// <summary>Reference-cable options for the Sync column: a blank plus every other cable. Chains resolve
    /// transitively to their Independent root, so a follower may reference another follower.</summary>
    public IEnumerable<string> CableSyncReferenceOptions =>
        new[] { "" }.Concat(State.Cables.Select(c => c.CableName));
    /// <summary>"N group(s) · M follower(s)" over the current Sync graph.</summary>
    public string SyncGroupSummary
    {
        get
        {
            var (g, f) = COT_VCF.Application.PretensionCandidates.SyncGroupCounts(State.Cables);
            return f == 0 ? "no synced cables" : $"{g} sync group{(g == 1 ? "" : "s")} · {f} follower{(f == 1 ? "" : "s")}";
        }
    }
    /// <summary>Global cable material strength [MPa] (Grade 1860 strand by default). Editing it recomputes the
    /// Capacity column for every cable that has a retrieved section area.</summary>
    public double CableStrengthMpa
    {
        get => State.CableStrengthMpa;
        set { if (State.CableStrengthMpa == value) return; State.CableStrengthMpa = value; Notify(); RecomputeCableCapacities(); Update(); }
    }
    /// <summary>Global strength-reduction coefficient φ (default 0.5). Editing it recomputes the Capacity column.</summary>
    public double CableStrengthReductionFactor
    {
        get => State.CableStrengthReductionFactor;
        set { if (State.CableStrengthReductionFactor == value) return; State.CableStrengthReductionFactor = value; Notify(); RecomputeCableCapacities(); Update(); }
    }

    /// <summary>Refresh each cable's read-only <see cref="CablePretensionConfig.DesignCapacityKN"/> from its
    /// section area and the global strength / φ. An unresolved input clears the derived value so an old
    /// capacity can never survive an fpu / φ change.</summary>
    private void RecomputeCableCapacities()
    {
        foreach (var cable in State.Cables)
            cable.DesignCapacityKN = cable.ResolveCapacity(State.CableStrengthMpa, State.CableStrengthReductionFactor);
    }

    /// <summary>Resolve design capacities [kN] from the current area / fpu / φ at the instant a report is built.
    /// This deliberately does not trust a value persisted by an earlier run.</summary>
    private IReadOnlyDictionary<string, double> CurrentCableCapacities() => State.Cables
        .Select(c => new
        {
            c.CableName,
            Capacity = c.ResolveCapacity(State.CableStrengthMpa, State.CableStrengthReductionFactor)
        })
        .Where(c => !string.IsNullOrWhiteSpace(c.CableName) && c.Capacity is { } cap && double.IsFinite(cap) && cap > 0)
        .GroupBy(c => c.CableName, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First().Capacity!.Value, StringComparer.Ordinal);

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
    public IReadOnlyList<LoadingStudyCandidate> LoadingStudyCandidates => studyComposition.LoadingCandidates;
    public IReadOnlyList<StudyIteration> StudyIterations => studyComposition.Iterations;

    // ---- Tab 05: candidate definitions (master grid shows IDs only; these back the collapsible lists) ----
    public sealed record LoadingDefRow(string Id, IReadOnlyList<string> Values);
    public sealed record HeightDefRow(string Id, string Delta, string Height);
    public sealed record PretensionDefRow(string Cable, string Force);
    public sealed record PretensionDefGroup(string Id, string Summary, IReadOnlyList<PretensionDefRow> Cables);

    /// <summary>Distinct load-pattern names across every loading candidate — the column headers for the list.</summary>
    public IReadOnlyList<string> LoadingDefinitionColumns => LoadingStudyCandidates
        .SelectMany(c => c.Combination.Loads.Select(l => l.Name))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public IReadOnlyList<LoadingDefRow> LoadingDefinitionRows
    {
        get
        {
            var cols = LoadingDefinitionColumns;
            return LoadingStudyCandidates.Select(c =>
            {
                var byName = c.Combination.Loads
                    .GroupBy(l => l.Name, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().Qz, StringComparer.Ordinal);
                return new LoadingDefRow(c.Id, cols
                    .Select(n => byName.TryGetValue(n, out var q) ? $"{q:0.###} kPa" : "—")
                    .ToArray());
            }).ToArray();
        }
    }

    public IReadOnlyList<HeightDefRow> HeightDefinitionRows => HeightStudyCandidates
        .Select(c => new HeightDefRow(c.Id,
            $"{c.Candidate.DeltaH:+0.###;-0.###;0} {c.Unit}",
            $"{c.Candidate.Height:0.###} {c.Unit}"))
        .ToArray();

    public IReadOnlyList<PretensionDefGroup> PretensionDefinitionGroups => PretensionStudyCandidates
        .Select(c => new PretensionDefGroup(c.Id,
            c.Assignments.Count == 0 ? "no target-force assignments"
                : $"{c.Assignments.Count} cable target{(c.Assignments.Count == 1 ? "" : "s")}",
            c.Assignments.Select(a => new PretensionDefRow(a.CableName, $"{a.TargetForce:N0} kN")).ToArray()))
        .ToArray();

    public string LoadingCandidatesTotalText => $"{LoadingCandidateCount} in total";
    public string HeightCandidatesTotalText => $"{HeightCandidateCount} in total";
    public string PretensionCandidatesTotalText => $"{PretensionCandidateCount} in total";

    private StudyIteration? selectedStudyIteration;
    public StudyIteration? SelectedStudyIteration
    {
        get => selectedStudyIteration;
        set { if (Set(ref selectedStudyIteration, value)) Notify(nameof(SelectedCandidateBreadcrumb)); }
    }
    public string SelectedCandidateBreadcrumb => selectedStudyIteration is null
        ? "Select a study row to trace its L / H / P"
        : $"{selectedStudyIteration.LoadingCandidateId} · {selectedStudyIteration.HeightCandidateId} · {selectedStudyIteration.PretensionCandidateId}    {selectedStudyIteration.SapModelName}";

    // ---- Tab 05: worker monitor slots — always MaxWorkerCount cards; the ones past the requested
    //      instance count are shown hatched / "not in use", regardless of whether a study is running ----
    public sealed class WorkerMonitorSlot : Observable
    {
        private bool inUse;
        private SapWorkerSnapshot? snapshot;
        public required int Number { get; init; }
        public string InstanceName => $"Instance {Number}";
        public bool InUse
        {
            get => inUse;
            set { if (Set(ref inUse, value)) { Notify(nameof(ShowLive)); Notify(nameof(ShowStatus)); Notify(nameof(StatusLabel)); } }
        }
        public SapWorkerSnapshot? Snapshot
        {
            get => snapshot;
            set { if (Set(ref snapshot, value)) { Notify(nameof(ShowLive)); Notify(nameof(ShowStatus)); Notify(nameof(StatusLabel)); } }
        }
        public bool ShowLive => inUse && snapshot is not null;
        public bool ShowStatus => !ShowLive;
        public string StatusLabel => !inUse ? "Not in use" : snapshot is null ? "Idle — start a study run" : "";
    }

    public ObservableCollection<WorkerMonitorSlot> WorkerMonitorSlots { get; } =
        new(Enumerable.Range(1, 3).Select(n => new WorkerMonitorSlot { Number = n }));

    private void SyncWorkerMonitorSlots()
    {
        foreach (var slot in WorkerMonitorSlots)
        {
            slot.InUse = slot.Number <= RequestedWorkerCount;
            slot.Snapshot = StudyWorkers.FirstOrDefault(w => w.WorkerId == slot.Number);
        }
    }
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
            InvalidateStudyValidation();
            SyncWorkerMonitorSlots();
        }
    }
    public int MaxWorkerCount => 3;
    public ObservableCollection<SapWorkerSnapshot> StudyWorkers { get; } = [];
    public int StudyCompletedCount => studyCompletedCount;
    public int StudyReusedCount => studyReusedCount;
    public int StudyFailedCount => studyFailedCount;
    public int StudyRunningCount => studyRunningCount;
    public int StudyQueuedCount => studyQueuedCount;
    public int StudySkippedCount => studySkippedCount;
    public int StudyCancelledCount => studyCancelledCount;
    public int StudyFinishedCount => studyCompletedCount + studyFailedCount + studyCancelledCount;
    public double StudyOverallProgress => SelectedRunCount == 0 ? 0 : Math.Min(100, 100.0 * StudyFinishedCount / SelectedRunCount);
    public string StudyProgressText => $"{StudyFinishedCount:N0} / {SelectedRunCount:N0}";
    public string StudyCountsText => $"Ready {StudyCompletedCount:N0}" +
        (studyReusedCount > 0 ? $" (Reused {studyReusedCount:N0})" : "") +
        $"   Running {StudyRunningCount:N0}   Queued {StudyQueuedCount:N0}   Failed {StudyFailedCount:N0}   Skipped {StudySkippedCount:N0}   Workers {StudyWorkers.Count}   Peak {studyPeakConcurrent}";
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
                ? "No height candidates are available from Tab 2."
                : $"{candidate.Id}: baseline {State.Truss.BaselineHeight:F3} \u2192 {candidate.Candidate.Height:F3} {LengthUnit} (\u0394H {candidate.Candidate.DeltaH:+0.###;-0.###;0} {LengthUnit})";
        }
    }
    public string FixedPretensionValueText
    {
        get
        {
            var candidate = PretensionStudyCandidates.FirstOrDefault(c => c.Id == FixedPretensionCandidateId);
            if (candidate is null) return "No pretension candidates are available from Tab 3.";
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
            var factors = $"{LoadingCandidateCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} \\times {h} \\times {p}";
            var raw = studyComposition.UnfilteredRunCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            return State.Study.UseCandidateFilters ? $"{factors} = {raw} \\rightarrow {StudyTotalRuns}" : $"{factors} = {StudyTotalRuns}";
        }
    }
    /// <summary>Symbolic + numeric total, e.g. "N_L \times N_H \times N_P = 4 \times 5 \times 2 = 40".</summary>
    public string StudySpaceHeadlineFormula => $"N_L \\times N_H \\times N_P = {StudySpaceFormula}";
    public string StudyTotalRuns => studyComposition.ExpectedRunCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
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
    // ---- Tab 06 Post-Processing: Deflection · Uz ----
    public sealed record PostProcessRow(
        string IterationKey, string FileName, string Description,
        string ExtractionStatus, int Points, int Valid, int Warnings,
        string OutputPath, string DiagnosticRelPath)
    {
        public string StatusGlyph => ExtractionStatus switch
        {
            "ExtractedOk" => "● ok",
            "ExtractedWithWarning" => "▲ warning",
            "AnalysisFailed" => "× analysis",
            _ => "× failed",
        };
        public string PointsText => Valid == Points ? $"{Points}" : $"{Valid} / {Points}";
        public string StatusLine => Warnings > 0
            ? $"{StatusGlyph} · {Valid}/{Points} Uz · {Warnings} warn"
            : $"{StatusGlyph} · {Valid}/{Points} Uz";
    }

    public ObservableCollection<PostProcessRow> PostProcessRows { get; } = [];
    private string? rawJsonPath, rawCsvPath;
    public string? RawJsonPath
    {
        get => rawJsonPath;
        private set
        {
            if (!Set(ref rawJsonPath, value)) return;
            Notify(nameof(HasRawData));
            Notify(nameof(RawDataSummary));
            Notify(nameof(PostProcessStatus));
            CommandManager.InvalidateRequerySuggested();
            OnRawDataChangedForOptimization();
        }
    }
    public string? RawCsvPath
    {
        get => rawCsvPath;
        private set
        {
            if (!Set(ref rawCsvPath, value)) return;
            Notify(nameof(RawDataSummary));
        }
    }
    public bool HasRawData => !string.IsNullOrWhiteSpace(RawJsonPath) && File.Exists(RawJsonPath);
    public string RawDataSummary => HasRawData
        ? $"Source: {Path.GetFileName(RawJsonPath)} · CSV: {(File.Exists(RawCsvPath) ? Path.GetFileName(RawCsvPath) : "not available")}"
        : "No raw study data yet.";
    private string? deflectionReportPath;
    public string? DeflectionReportPath
    {
        get => deflectionReportPath;
        private set
        {
            if (!Set(ref deflectionReportPath, value)) return;
            Notify(nameof(HasDeflectionReport));
            Notify(nameof(HasNoDeflectionReport));
            Notify(nameof(DeflectionReportUri));
            Notify(nameof(DeflectionReportName));
            Notify(nameof(PostProcessStatus));
        }
    }
    public bool HasDeflectionReport => !string.IsNullOrEmpty(DeflectionReportPath) && File.Exists(DeflectionReportPath);
    public bool HasNoDeflectionReport => !HasDeflectionReport;
    public Uri? DeflectionReportUri => HasDeflectionReport ? new Uri(DeflectionReportPath!) : null;
    public string DeflectionReportName => HasDeflectionReport ? Path.GetFileName(DeflectionReportPath!) : "No report generated yet";

    private string? memberForceReportPath;
    public string? MemberForceReportPath
    {
        get => memberForceReportPath;
        private set
        {
            if (!Set(ref memberForceReportPath, value)) return;
            Notify(nameof(HasMemberForceReport));
            Notify(nameof(MemberForceReportName));
        }
    }
    public bool HasMemberForceReport => !string.IsNullOrEmpty(MemberForceReportPath) && File.Exists(MemberForceReportPath);
    public string MemberForceReportName => HasMemberForceReport ? Path.GetFileName(MemberForceReportPath!) : "No member-force report generated yet";

    private string? supportReactionReportPath;
    public string? SupportReactionReportPath
    {
        get => supportReactionReportPath;
        private set
        {
            if (!Set(ref supportReactionReportPath, value)) return;
            Notify(nameof(HasSupportReactionReport));
            Notify(nameof(SupportReactionReportName));
        }
    }
    public bool HasSupportReactionReport => !string.IsNullOrEmpty(SupportReactionReportPath) && File.Exists(SupportReactionReportPath);
    public string SupportReactionReportName => HasSupportReactionReport ? Path.GetFileName(SupportReactionReportPath!) : "No support-reaction report generated yet";

    private string? cableForceReportPath;
    public string? CableForceReportPath
    {
        get => cableForceReportPath;
        private set
        {
            if (!Set(ref cableForceReportPath, value)) return;
            Notify(nameof(HasCableForceReport));
            Notify(nameof(CableForceReportName));
        }
    }
    public bool HasCableForceReport => !string.IsNullOrEmpty(CableForceReportPath) && File.Exists(CableForceReportPath);
    public string CableForceReportName => HasCableForceReport ? Path.GetFileName(CableForceReportPath!) : "No cable-force report generated yet";

    /// <summary>Header / footer, logo and default sheet printed by the reports. A presentation input: it is
    /// embedded on the next Generate / Regenerate and never invalidates results.</summary>
    public ReportPrintSettings PrintSettings { get; }
    public IReadOnlyList<ReportPaperSize> PrintPaperSizes { get; } = Enum.GetValues<ReportPaperSize>();
    public IReadOnlyList<ReportPageOrientation> PrintOrientations { get; } = Enum.GetValues<ReportPageOrientation>();
    public string PrintLogoStatus => ReportPrintEngine.DescribeLogo(PrintSettings.LogoPath);

    /// <summary>Where the Print Setup on the card stands against the project file.</summary>
    public string PrintSetupProjectStatus
    {
        get
        {
            if (!config.Session.HasActiveProject)
                return "No project open — kept for this Windows user. Save All stores it in the project file.";
            var file = config.Session.FileName;
            if (config.Session.Project?.PrintSetup is not { } saved)
                return $"Not in {file} yet — Save Print Setup (or Save All) stores it with the project.";
            return new ConfigurationExtractor().SamePrintSetup(saved, PrintSettings)
                ? $"Saved in {file} · Load Config restores it"
                : $"Modified * — differs from {file}";
        }
    }
    private bool useCurrentRunArtifacts;
    private string? currentRunWorkbookPath;
    public string ResultsWorkbookPath => useCurrentRunArtifacts
        ? currentRunWorkbookPath ?? ""
        : discoveredWorkbookPath ?? "";
    public bool HasResultsWorkbook => !string.IsNullOrEmpty(ResultsWorkbookPath) && File.Exists(ResultsWorkbookPath);
    private string? currentRunId;
    private StudyRunState? currentRunState;
    private bool runArtifactsVerified;
    public string RunProvenanceText => currentRunId is null
        ? "No study run manifest yet."
        : $"Run {currentRunId[..Math.Min(8, currentRunId.Length)]} · {currentRunState} · " +
          (currentRunState == StudyRunState.Running ? "recording provenance" : runArtifactsVerified ? "artifacts verified" : "artifact check required");
    public string PostProcessStatus
    {
        get
        {
            var ok = PostProcessRows.Count(r => r.ExtractionStatus == "ExtractedOk");
            var total = PostProcessRows.Count;
            if (!HasRawData) return "○ No raw data — run the study on Tab 05.";
            if (!HasDeflectionReport) return "▲ Raw data ready — generate outputs on Tab 06.";
            return total == 0 ? "● Report ready (from a previous run)." : $"● Report ready · {ok}/{total} candidates clean.";
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
    public string RunStudyLabel => $"Build & Analyze {SelectedRunCount:N0} - {RequestedWorkerCount} Instance{(RequestedWorkerCount == 1 ? "" : "s")}";
    public string ToolbarStudySummary => SelectedRunCount == 0
        ? "Study · nothing selected"
        : $"Study · {SelectedRunCount:N0} run{(SelectedRunCount == 1 ? "" : "s")} selected";
    public bool CanRunStudy => !IsBusy && studyExecution is not null && State.IsSapConnected && SelectedRunCount > 0 && preflightReport.Ready;
    public string RunStudyToolTip => RequestedWorkerCount == 1
        ? "Uses the connected SAP2000 instance as worker 1 to build, analyze, save, and verify each child; no additional process is started."
        : $"Uses the connected SAP2000 instance as worker 1 and starts {RequestedWorkerCount - 1} additional processes. Up to {RequestedWorkerCount} child models build and analyze concurrently.";
    public int ResumableRunCount => resumeAssessment?.ResumeIds.Count ?? 0;
    public int RetryableRunCount => resumeAssessment?.RetryIds.Count ?? 0;
    public bool CanResumeStudy => CanRunStudy && ResumableRunCount > 0;
    public bool CanRetryStudy => CanRunStudy && RetryableRunCount > 0;
    public bool CanRebuildStudy => CanRunStudy;
    public string ResumeStatusText => resumeStatusText;
    public IReadOnlyList<StudyPreflightMetric> PreflightMetrics => preflightReport.Metrics;
    public string PreflightSummary => preflightReport.Summary;
    public bool PreflightReady => preflightReport.Ready;
    public string FilterAuditText => !State.Study.UseCandidateFilters
        ? "Filters off · all generated combinations retained."
        : $"{studyComposition.UnfilteredRunCount:N0} → {studyComposition.ExpectedRunCount:N0} rows · {studyComposition.FilterAudit.ExcludedCount:N0} filtered out.";
    public string SapStatus => State.IsSapConnected ? "Connected" : State.Connection;
    public string StatusLine => State.IsSapConnected
        ? $"{ModelName}  ·  {State.Units}  ·  {(State.Model?.IsLocked == true ? "Locked" : "Unlocked")}"
        : IsOfflineModel ? $"Offline model  ·  {OfflineModelName}  ·  {State.Units}  ·  SAP2000 not connected"
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
    public string OutputStatus => TabState(ConfigTabs.Output, State.OutputSettings.IsConfigured,
        new OutputSettingsValidator().Validate(State).Any(issue => issue.Severity == ValidationSeverity.Error));
    public string DeflectionOutputSummary => State.OutputSettings.DeflectionPoints.Count == 0
        ? "Not configured"
        : $"{State.OutputSettings.DeflectionPoints.Count} point{(State.OutputSettings.DeflectionPoints.Count == 1 ? "" : "s")} · U3 [mm]";
    public string MemberForceOutputSummary
    {
        get
        {
            var groups = State.OutputSettings.MemberForceGroups.Count;
            var members = State.OutputSettings.MemberForceGroups.Sum(group => group.Members.Count);
            return groups == 0 ? "Not configured" : $"{groups} group{(groups == 1 ? "" : "s")} · {members} frame{(members == 1 ? "" : "s")} · {State.OutputSettings.MemberForceResultLoadCase}";
        }
    }
    public string FoundationOutputSummary => $"Left {State.OutputSettings.FoundationLeftPoints.Count}/2 · Right {State.OutputSettings.FoundationRightPoints.Count}/2 · {(string.IsNullOrWhiteSpace(State.OutputSettings.FoundationReactionResultLoadCase) ? "No result case" : State.OutputSettings.FoundationReactionResultLoadCase)}";
    public string CableOutputSummary => State.OutputSettings.CableNames.Count == 0
        ? "Not configured"
        : $"{State.OutputSettings.CableNames.Count} cable{(State.OutputSettings.CableNames.Count == 1 ? "" : "s")} · {State.OutputSettings.CableResultLoadCase}";

    // ---- Tab 04 Output Settings: compact left-panel summaries + status ----
    private const string StatusReady = "● Ready", StatusIncomplete = "▲ Incomplete",
        StatusInvalid = "× Invalid", StatusNotConfigured = "○ Not configured";
    private OutputSettingsConfiguration Out => State.OutputSettings;
    public string MonitoredPointCaseSummary => string.IsNullOrWhiteSpace(Out.DeflectionOutputCaseName)
        ? "U3 · No output case"
        : $"U3 · {Out.DeflectionOutputCaseName}";
    public string MonitoredPointSummary =>
        $"{Out.DeflectionPoints.Count} point{(Out.DeflectionPoints.Count == 1 ? "" : "s")}";
    public string MonitoredPointStatus => Out.DeflectionLimitBand.Problem() is not null ? StatusInvalid
        : Out.DeflectionPoints.Count == 0 ? StatusNotConfigured
        : string.IsNullOrWhiteSpace(Out.DeflectionOutputCaseName) ? StatusIncomplete
        : StatusReady;
    /// <summary>One-line reading of the Tab 04 deflection limit band (peak downward U3).</summary>
    public string DeflectionLimitSummary
    {
        get
        {
            var band = Out.DeflectionLimitBand;
            if (band.Problem() is { } problem) return problem;
            return band switch
            {
                { MinMm: double lo, MaxMm: double hi } => $"Pass when {lo:0.###} ≤ peak ↓U3 ≤ {hi:0.###} mm",
                { MaxMm: double hi } => $"Pass when peak ↓U3 ≤ {hi:0.###} mm",
                { MinMm: double lo } => $"Pass when peak ↓U3 ≥ {lo:0.###} mm",
                _ => "No limit band · deflection is not a pass/fail criterion",
            };
        }
    }
    public string MemberForceSummary
    {
        get
        {
            var groups = Out.MemberForceGroups.Count;
            if (groups == 0) return "0 groups";
            var members = Out.MemberForceGroups.Sum(g => g.Members.Count);
            var empty = Out.MemberForceGroups.Count(g => g.Members.Count == 0 || string.IsNullOrWhiteSpace(g.Name));
            return $"{(string.IsNullOrWhiteSpace(Out.MemberForceResultLoadCase) ? "No output case" : Out.MemberForceResultLoadCase)} · {groups} group{(groups == 1 ? "" : "s")} · {members} member{(members == 1 ? "" : "s")}" +
                   (empty > 0 ? $" · {empty} empty" : "");
        }
    }
    public string MemberForceStatus => Out.MemberForceGroups.Count == 0 ? StatusNotConfigured
        : string.IsNullOrWhiteSpace(Out.MemberForceResultLoadCase) ||
          Out.MemberForceGroups.Any(g => g.Members.Count == 0 || string.IsNullOrWhiteSpace(g.Name)) ? StatusIncomplete
        : StatusReady;
    public string FoundationSummary => $"{(string.IsNullOrWhiteSpace(Out.FoundationReactionResultLoadCase) ? "No output case" : Out.FoundationReactionResultLoadCase)} · Left {Out.FoundationLeftPoints.Count} / 2 · Right {Out.FoundationRightPoints.Count} / 2";
    public string FoundationStatus => Out.FoundationLeftPoints.Count == 0 && Out.FoundationRightPoints.Count == 0 ? StatusNotConfigured
        : Out.FoundationLeftPoints.Count == 2 && Out.FoundationRightPoints.Count == 2 &&
          !string.IsNullOrWhiteSpace(Out.FoundationReactionResultLoadCase) ? StatusReady
        : StatusIncomplete;
    public string CableForceSummary => Out.CableNames.Count == 0
        ? "0 cables"
        : string.IsNullOrWhiteSpace(Out.CableResultLoadCase)
            ? $"No output case · {Out.CableNames.Count} cable{(Out.CableNames.Count == 1 ? "" : "s")}"
            : $"{Out.CableResultLoadCase} · {Out.CableNames.Count} cable{(Out.CableNames.Count == 1 ? "" : "s")}";
    public string CableForceStatus => Out.CableNames.Count == 0 ? StatusNotConfigured
        : string.IsNullOrWhiteSpace(Out.CableResultLoadCase) ? StatusIncomplete
        : StatusReady;
    private int ReadyOutputCount =>
        (MonitoredPointStatus == StatusReady ? 1 : 0) + (MemberForceStatus == StatusReady ? 1 : 0) +
        (FoundationStatus == StatusReady ? 1 : 0) + (CableForceStatus == StatusReady ? 1 : 0);
    public string OutputUnitsSummary => $"Force {OutputUnits.Force} · Moment {OutputUnits.Moment} · Deflection {OutputUnits.Displacement}";
    public string ConfiguredOutputCount
    {
        get
        {
            var n = ReadyOutputCount;
            var glyph = n == 4 ? "●" : n == 0 ? "○" : "▲";
            return $"{glyph} {n} / 4 configured";
        }
    }
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
    public Command RefreshCableSections { get; }
    public Command RemoveLoads { get; }
    public Command RemoveCables { get; }
    public Command ConfigureDeflectionOutput { get; }
    public Command ConfigureMemberForceOutput { get; }
    public Command ConfigureFoundationOutput { get; }
    public Command ConfigureCableOutput { get; }
    public Command ValidateOutputSettings { get; }
    public Command Validate { get; }
    public Command NavigateToIssue { get; }
    public Command SelectAllStudyRuns { get; }
    public Command ClearAllStudyRuns { get; }
    public Command BuildSelectedModels { get; }
    public Command InspectPreviousRun { get; }
    public Command ResumeInterruptedStudy { get; }
    public Command RetryFailedStudy { get; }
    public Command RebuildSelectedModels { get; }
    public Command RunStudyFromToolbar { get; }
    public Command CancelStudy { get; }
    public Command OpenStudyFolder { get; }
    public Command OpenIterationFolder { get; }
    public Command OpenDeflectionReport { get; }
    public Command GeneratePostProcessOutputs { get; }
    public Command OpenRawDataFolder { get; }
    public Command RegenerateDeflectionReport { get; }
    public Command OpenMemberForceReport { get; }
    public Command RegenerateMemberForceReport { get; }
    public Command OpenSupportReactionReport { get; }
    public Command RegenerateSupportReactionReport { get; }
    public Command OpenCableForceReport { get; }
    public Command RegenerateCableForceReport { get; }
    public Command OpenTopChordReport { get; }
    public Command OpenBottomChordReport { get; }
    public Command OpenMegaColumnReport { get; }
    public Command BrowsePrintLogo { get; }
    public Command ClearPrintLogo { get; }
    public Command SavePrintSetup { get; }
    public Command PrintAllReports { get; }
    public Command OpenResultsWorkbook { get; }
    public Command OpenCandidateDiagnostic { get; }
    public Command ViewIterationError { get; }
    public Command SaveTabConfig { get; }
    public Command SaveAllConfig { get; }
    public Command NewConfig { get; }
    public Command LoadConfig { get; }
    public Command ReadFemProperties { get; }
    public Command RunFemAnalysis { get; }
    public Command CancelFemAnalysis { get; }
    public Command UnlockFemModel { get; }

    private readonly FemStudyService femStudy = new();
    private CancellationTokenSource? femCancellation;
    private bool isFemRunning;
    private string femProgressText = "Open a model, then Run Analysis solves it as defined.";
    private FemStudyRun? selectedFemRun;
    private FemResultMode femResultMode;
    private int selectedFemPage;
    private double femTargetFitPercent = 10;
    /// <summary>Stop adjusting cable pretension once every cable's pretension-stage tension (before applied loads) is within this % of its Tab 03 target.</summary>
    public double FemTargetFitPercent
    {
        get => femTargetFitPercent;
        set
        {
            if (!double.IsFinite(value) || value <= 0 || value >= 100)
                throw new ArgumentOutOfRangeException(nameof(value), "Enter a fitting tolerance greater than 0% and less than 100%.");
            Set(ref femTargetFitPercent, value);
        }
    }
    private int femTargetFitIterations = PlanarFrameSolver.DefaultTargetFitIterations;
    /// <summary>Maximum pretension adjustments per FEM case while fitting cable tensions to their targets.</summary>
    public int FemTargetFitIterations
    {
        get => femTargetFitIterations;
        set
        {
            if (value < 1 || value > 10_000)
                throw new ArgumentOutOfRangeException(nameof(value), "Enter between 1 and 10,000 fitting iterations.");
            Set(ref femTargetFitIterations, value);
        }
    }
    private bool femCableSag = true;
    /// <summary>Analyse the sag of cables that a case loads along their length (self-weight, typically): the cable
    /// hangs in a shallow parabola and its tension and stiffness follow the sag. Off = straight tension-only bars.</summary>
    public bool FemCableSag { get => femCableSag; set => Set(ref femCableSag, value); }
    public ObservableCollection<FemSectionProperties> FemSectionProperties { get; } = [];
    /// <summary>The run of the model: empty before Run Analysis, and after a run that did not reach equilibrium.</summary>
    public ObservableCollection<FemStudyRun> FemRuns { get; } = [];
    /// <summary>One row per cable of the run: Tab 03 target vs final FEM tension — or the one Failed row of a run without results.</summary>
    public ObservableCollection<FemCableFitRow> FemCableFitRows { get; } = [];
    // ---- FEM run monitor (pop-up window opened by Run Analysis) ----
    public const string DefaultMonitorCable = "Cable1a-2";
    private const int MaximumFemLogLines = 5000;
    /// <summary>Raised when a FEM run starts so the view can open the run monitor window.</summary>
    public event EventHandler? FemAnalysisStarted;
    public ObservableCollection<string> FemLog { get; } = [];
    private bool femMonitorRunning;
    private string? femMonitorCable;
    private double femMonitorTolerancePercent;
    public bool FemMonitorRunning
    {
        get => femMonitorRunning;
        private set { if (Set(ref femMonitorRunning, value)) { Notify(nameof(FemMonitorProgressText)); Notify(nameof(FemMonitorProgress)); } }
    }
    /// <summary>Progress bar of the one run: full once it has results (the bar is indeterminate while it runs).</summary>
    public double FemMonitorProgress => !FemMonitorRunning && FemRuns.Count > 0 ? 1 : 0;
    public string FemMonitorProgressText => FemMonitorRunning ? "Running" : FemRuns.Count > 0 ? "Finished"
        : FemLog.Count > 0 ? "No result" : "No FEM run yet.";
    public double FemMonitorTolerancePercent { get => femMonitorTolerancePercent; private set => Set(ref femMonitorTolerancePercent, value); }
    public IReadOnlyList<string> FemMonitorCableNames => State.Cables.Select(cable => cable.CableName)
        .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToArray();
    public string? FemMonitorCable
    {
        get => femMonitorCable;
        set { if (Set(ref femMonitorCable, value)) NotifyFemMonitorChart(); }
    }
    private IReadOnlyList<string> femMonitorTargetStages = [];
    private string? femMonitorTargetStage;
    public IReadOnlyList<string> FemMonitorTargetStages => femMonitorTargetStages;
    public string? FemMonitorTargetStage
    {
        get => femMonitorTargetStage;
        set { if (Set(ref femMonitorTargetStage, value)) NotifyFemMonitorChart(); }
    }
    private FemAnalysisResult? FemMonitorStageResult => FemMonitorTargetStage is { } name &&
        SelectedFemRun?.Results?.TryGetValue(name, out var result) == true ? result : null;
    private FemCableResponse? FemMonitorCableResult => FemMonitorStageResult?.Cables
        .FirstOrDefault(cable => cable.MemberName.Equals(FemMonitorCable, StringComparison.Ordinal));
    public IReadOnlyList<double> FemMonitorRatios => FemMonitorCableResult?.FitRatios ?? [];
    public string FemMonitorChartSummary
    {
        get
        {
            if (SelectedFemRun is not { } run) return FemMonitorRunning ? "Solving the model…" : "No FEM result yet.";
            if (FemMonitorCableResult is not { } cable) return $"Cable '{FemMonitorCable}' is not in this FEM model.";
            if (cable.TargetForceKN <= 0 || cable.FitHistoryKN is null) return $"{cable.MemberName}: no target force to fit.";
            var ratio = cable.PretensionStageForceKN / cable.TargetForceKN;
            return $"{cable.MemberName} · P_preset {cable.TargetForceKN:0} kN → P_result {cable.PretensionStageForceKN:0} kN " +
                $"(ratio {ratio:0.000}) after {cable.FitHistoryKN.Count} run(s) · applied pretension {cable.AppliedPretensionKN:0} kN · " +
                (FemMonitorStageResult?.TargetFitConverged == true ? $"within ±{FemMonitorTolerancePercent:0.###}%" : "NOT fitted") +
                $" · target stage: {FemMonitorTargetStage}";
        }
    }

    private void NotifyFemMonitorChart()
    {
        Notify(nameof(FemMonitorRatios));
        Notify(nameof(FemMonitorChartSummary));
        Notify(nameof(FemMonitorProgress));
        Notify(nameof(FemMonitorProgressText));
    }

    private void StartFemMonitor()
    {
        FemLog.Clear();
        FemMonitorRunning = true;
        PrepareFemMonitor();
        FemAnalysisStarted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Refresh the Cable list from Tab 03 and select Cable1a-2 (or the first cable) if nothing valid is chosen.</summary>
    public void PrepareFemMonitor()
    {
        Notify(nameof(FemMonitorCableNames));
        var names = FemMonitorCableNames;
        if (FemMonitorCable is null || !names.Contains(FemMonitorCable, StringComparer.Ordinal))
            FemMonitorCable = names.FirstOrDefault(name => name.Equals(DefaultMonitorCable, StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault();
        NotifyFemMonitorChart();
    }

    private void FemLogLine(string line)
    {
        FemLog.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (FemLog.Count > MaximumFemLogLines) FemLog.RemoveAt(0);
    }

    private static string DescribeFemRun(FemStudyRun run)
    {
        var result = run.Result;
        var fitted = result.Cables.Where(cable => cable.TargetForceKN > 0 && cable.PretensionStageForceKN is not null).ToArray();
        var worst = fitted.Select(cable => Math.Abs(cable.PretensionStageForceKN!.Value / cable.TargetForceKN - 1) * 100).DefaultIfEmpty().Max();
        var fit = result.TargetFitIterations == 0 ? "No target fitting"
            : result.TargetFitConverged ? $"Pretension fitted in {result.TargetFitIterations} run(s), max deviation {worst:0.##}%"
            : $"Pretension NOT fitted after {result.TargetFitIterations} run(s), max deviation {worst:0.##}%";
        var slack = result.Cables.Count(cable => !cable.Active);
        return $"{fit} · loaded equilibrium in {result.NonlinearIterations} Newton iteration(s)" +
            (slack > 0 ? $" · {slack} slack cable(s)" : "") +
            $" · max |N| {result.MaximumAbsoluteAxialKN:0} kN · max deflection {result.MaximumDownwardDeflectionMm:0.#} mm";
    }

    public const int FemInputsPage = 0;
    public const int FemAnalysisPage = 1;
    public const int FemViewPage = 2;
    public const int FemResultsPage = FemAnalysisPage;

    private int femFrameMeshSegments = PlanarFrameSolver.DefaultFrameMeshSegments;
    /// <summary>Number of equal sub-elements each frame is divided into for the FEM (cables are not meshed).</summary>
    public int FemFrameMeshSegments
    {
        get => femFrameMeshSegments;
        set
        {
            if (value < 1 || value > PlanarFrameSolver.MaximumFrameMeshSegments)
                throw new ArgumentOutOfRangeException(nameof(value), $"Enter between 1 and {PlanarFrameSolver.MaximumFrameMeshSegments} segments per frame.");
            if (Set(ref femFrameMeshSegments, value)) Notify(nameof(SharedViewerFrameMeshSegments));
        }
    }

    private bool showFemFrameMesh;
    private bool showFemMemberSizes;
    private bool showFemLoads = true;
    public bool ShowFemFrameMesh { get => showFemFrameMesh; set { if (Set(ref showFemFrameMesh, value)) Notify(nameof(SharedViewerFrameMeshSegments)); } }
    public bool ShowFemMemberSizes { get => showFemMemberSizes; set { if (Set(ref showFemMemberSizes, value)) { Notify(nameof(SharedViewerMemberSizes)); Notify(nameof(FemMemberSizeHint)); } } }
    public bool ShowFemLoads { get => showFemLoads; set { if (Set(ref showFemLoads, value)) { Notify(nameof(SharedViewerShowLoads)); Notify(nameof(SharedViewerLoadValues)); } } }

    private bool showFemAxialValues = true;
    /// <summary>Label frame axial forces and cable tensions (whole kN) in the Axial force result view.</summary>
    public bool ShowFemAxialValues { get => showFemAxialValues; set => Set(ref showFemAxialValues, value); }

    /// <summary>SAP section data (shape, local-axis angle, area) per member once read, for the to-scale 3D view.</summary>
    public IReadOnlyDictionary<string, FemSectionProperties>? SharedViewerSections => FemSectionProperties.Count == 0 ? null
        : FemSectionProperties.GroupBy(property => property.MemberName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    public int SharedViewerFrameMeshSegments => selectedTab == TabFem && ShowFemFrameMesh ? FemFrameMeshSegments : 0;
    public IReadOnlyDictionary<string, string>? SharedViewerMemberSizes =>
        selectedTab == TabFem && ShowFemMemberSizes && FemSectionProperties.Count > 0
            ? FemSectionProperties.Where(property => !string.IsNullOrWhiteSpace(property.SectionName))
                .ToDictionary(property => property.MemberName, property => property.SectionName!, StringComparer.Ordinal)
            : null;
    public string FemMemberSizeHint => ShowFemMemberSizes && FemSectionProperties.Count == 0
        ? "No sections read yet — use Read Properties on the Inputs page."
        : "Section name labelled along each frame and cable.";
    public bool SharedViewerShowLoads => selectedTab != TabFem || ShowFemLoads;
    /// <summary>Vertical load per point of the selected FEM case, labelled at the load arrows.</summary>
    public IReadOnlyDictionary<string, double>? SharedViewerLoadValues =>
        selectedTab == TabFem && ShowFemLoads && SelectedFemRun?.Loads is { } loads
            ? loads.GroupBy(load => load.PointName, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Sum(load => load.FzKN), StringComparer.Ordinal)
            : null;
    public int SelectedFemPage
    {
        get => selectedFemPage;
        set
        {
            if (!Set(ref selectedFemPage, value)) return;
            SyncFemPane();
            NotifySharedViewer();
        }
    }
    public FemResultMode FemResultMode
    {
        get => femResultMode;
        set
        {
            if (!Set(ref femResultMode, value)) return;
            NotifyFemResultSelection();
            NotifyPaneHeader();
        }
    }
    public FemStudyRun? SelectedFemRun
    {
        get => selectedFemRun;
        set
        {
            if (!Set(ref selectedFemRun, value)) return;
            NotifyFemResultSelection();
            NotifyFemMonitorChart();
        }
    }
    /// <summary>The cable target fit of the run, with the tension under the load case / combination shown.</summary>
    public IEnumerable<FemCableFitRow> FemRunCableFitRows => SelectedFemRun is { } run
        ? FemCableFitRow.FromRun(run, SelectedFemResultName) : [];
    public IReadOnlyList<FemResultRow> FemResultRows => FemResultTable.Build(SelectedFemResult, FemResultMode);
    public FemAnalysisResult? SharedViewerFemResult =>
        !ShowUndeformed && selectedTab == TabFem && SelectedFemPage == FemResultsPage ? SelectedFemResult : null;
    public FemResultMode SharedViewerFemResultMode => FemResultMode;
    public string FemResultDiagramTitle
    {
        get
        {
            var name = FemResultMode switch
            {
                FemResultMode.AxialForce => "Axial Force Diagram",
                FemResultMode.BendingMoment => "Bending Moment Diagram",
                FemResultMode.Shear => "Shear Diagram",
                FemResultMode.Reactions => "Reaction Diagram",
                _ => "Deflected Shape"
            };
            var unit = FemResultMode switch
            {
                FemResultMode.BendingMoment => "kN·m",
                FemResultMode.DeflectedShape => "mm",
                _ => "kN"
            };
            return SelectedFemRun is null ? $"{name} [no results]"
                : $"{name}{(SelectedFemResultName is { } loadName ? $" ({loadName})" : "")} [{unit}]";
        }
    }
    public string FemResultSummary => SelectedFemRun is null ? "No FEM results yet — Run → Run FEM Analysis."
        : $"{SelectedFemResultName ?? "result"} · {FemResultMode} · {FemResultRows.Count:N0} results";
    public string FemResultStartHeader => FemResultMode switch
    {
        FemResultMode.Reactions => "Fx [kN]",
        FemResultMode.DeflectedShape => "UX [m]",
        _ => "I-End"
    };
    public string FemResultEndHeader => FemResultMode switch
    {
        FemResultMode.Reactions => "Fz [kN]",
        FemResultMode.DeflectedShape => "UZ [m]",
        _ => "J-End"
    };
    /// <summary>What Run Analysis solves: the model as it stands, once. A Tab 01 / Tab 03 range is an input of the parametric
    /// study; it enters this run with its first value, named here so the run never hides what it used.</summary>
    public string FemRunSummary
    {
        get
        {
            if (!femStudy.CanComposeModel(State)) return "Not ready — complete 01 Loading, the 02 Truss Height baseline and 03 Pretension.";
            var cables = State.Cables.ToArray();
            var ranges = State.LoadDefinitions.Where(load => load.CandidateCount > 1)
                .Select(load => $"{load.LoadName} qz {load.CandidateValues[0]:0.###} kPa")
                .Concat(cables.Where(cable => cable.CandidateCount > 1 && !PretensionCandidates.IsFollower(cable, cables))
                    .Select(cable => $"{cable.CableName} {cable.GetCandidateForces()[0]:0.###} kN")).ToArray();
            return $"1 FEM run · the model as defined · truss height {State.Truss.BaselineHeight:0.###} {LengthUnit}" +
                (ranges.Length == 0 ? "" : $" · ranges at their first value: {string.Join(", ", ranges.Take(3))}" +
                    (ranges.Length > 3 ? $" (+{ranges.Length - 3} more)" : ""));
        }
    }
    public string FemPinSupportSummary
    {
        get
        {
            try
            {
                var points = FemStudyService.FindPinSupportPoints(State.Baseline);
                return $"{points.Count} auto-detected pin supports at lowest frame elevation: {string.Join(", ", points)}";
            }
            catch (Exception ex) { return ex.Message; }
        }
    }
    public string FemProgressText { get => femProgressText; private set => Set(ref femProgressText, value); }
    public string FemStatus => isFemRunning ? "RUNNING" : FemRuns.Count > 0 ? "ANALYSIS COMPLETE" :
        HasWorkingModel && !HasModelGeometry ? "BLANK · DEFINE → EDIT" :
        HasWorkingModel && femStudy.CanComposeModel(State) ? (IsOfflineModel ? "OFFLINE · READY" : "READY TO ANALYZE") : "LOAD CONFIG / CONNECT SAP";
    public bool CanRunFemAnalysis => !IsBusy && HasWorkingModel && femStudy.CanComposeModel(State);
    public bool CanReadFemProperties => !IsBusy && State.IsSapConnected && State.Model is not null &&
        State.Geometry.Members.Any(member => member.Kind is MemberKind.Frame or MemberKind.Cable);
    public bool CanCancelFemAnalysis => isFemRunning;

    public bool CanUseConfig => !IsBusy && State.IsSapConnected && State.Model is not null;
    private bool applyingProject;

    public MainViewModel(
        ApplicationState state,
        WorkflowService workflow,
        ISelectionService selection,
        ConfigService config,
        StudyExecutionCoordinator? studyExecution = null,
        StudyExecutionSnapshotFactory? studySnapshotFactory = null,
        IOutputSettingsDialogService? outputDialogs = null,
        OutputGroupPreparationService? outputGroupPreparation = null,
        ReportPrintSettingsStore? printStore = null,
        QuantityTakeoffService? quantityTakeoff = null,
        FemWorkingSessionStore? femSessionStore = null)
    {
        State = state;
        // Without a service (checks) Tab 08 reads candidate text exports only; it can never start SAP2000.
        this.quantityTakeoff = quantityTakeoff ?? new();
        this.workflow = workflow;
        this.selection = selection;
        this.config = config;
        this.studyExecution = studyExecution;
        this.studySnapshotFactory = studySnapshotFactory ?? new();
        this.outputDialogs = outputDialogs;
        this.outputGroupPreparation = outputGroupPreparation;
        this.printStore = printStore;
        this.femSessionStore = femSessionStore ?? new FemWorkingSessionStore();
        // Print Setup is a per-user preference: loaded once, saved on every edit. Without a store (checks) it is
        // simply held in memory.
        PrintSettings = printStore?.Load() ?? new();
        PrintSettings.PropertyChanged += (_, e) =>
        {
            this.printStore?.TrySave(PrintSettings);
            if (e.PropertyName == nameof(ReportPrintSettings.LogoPath)) Notify(nameof(PrintLogoStatus));
            Notify(nameof(PrintSetupProjectStatus));
        };
        StudyWorkers.CollectionChanged += (_, _) =>
        {
            Notify(nameof(HasStudyWorkers));
            SyncWorkerMonitorSlots();
            NotifyStudyExecution();
        };
        SyncWorkerMonitorSlots();
        bool Ready() => !IsBusy && State.IsSapConnected;
        bool GeometryReady() => Ready() && State.Geometry.Points.Count > 0;

        Connect = new(_ => Run("Connecting to SAP2000...", ConnectAndRefreshAsync, "Connected. Geometry loaded."), () => !IsBusy);
        Refresh = new(_ => Run("Refreshing SAP2000 connection and geometry...", ConnectAndRefreshAsync, "Connection refreshed. Geometry updated."), () => !IsBusy);
        // The load locations come from the model's own geometry: a loaded FEM model has them too.
        AddPoints = new(_ => SelectLoadLocations(), () => !IsBusy && HasWorkingModel && State.Geometry.Points.Count > 0);
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
        RefreshCableSections = new(_ => RefreshCableSectionsFromSap(),
            () => Ready() && State.Model is not null && State.Cables.Count > 0);
        RemoveLoads = new(p => { if (p is IList list) foreach (var row in list.Cast<PointLoadConfiguration>().ToArray()) State.PointLoads.Remove(row); Update(); }, () => !IsBusy);
        RemoveCables = new(p =>
        {
            if (p is not IList list) return;
            var removed = list.Cast<CablePretensionConfig>().ToArray();
            foreach (var row in removed) State.Cables.Remove(row);
            if (SelectedCable is not null && removed.Contains(SelectedCable)) SelectedCable = null;
            Update();
        }, () => !IsBusy);
        ConfigureDeflectionOutput = new(_ => EditDeflectionOutput(), GeometryReady);
        ConfigureMemberForceOutput = new(_ => EditMemberForceOutput(), GeometryReady);
        ConfigureFoundationOutput = new(_ => EditFoundationOutput(), GeometryReady);
        ConfigureCableOutput = new(_ => EditCableOutput(), GeometryReady);
        ValidateOutputSettings = new(_ =>
        {
            var issues = new OutputSettingsValidator().Validate(State);
            Message = issues.Count == 0 ? "Output Settings are configured and ready for parent preparation at Run." :
                string.Join("  ·  ", issues.Take(3).Select(issue => issue.Message));
            if (issues.Count > 0) Warn("Output Settings", string.Join("\n", issues.Select(issue => "• " + issue.Message)));
            Update();
        }, GeometryReady);
        Validate = new(_ =>
        {
            if (CommitEditors?.Invoke() == false) return;
            RegenerateStudyPreview();
            if (ValidateStudy(showMessage: true)) InspectResumeState();
        }, () => !IsBusy);
        NavigateToIssue = new(p =>
        {
            if (p is not ValidationIssue issue) return;
            SelectedTab = issue.Section switch
            {
                "Loading Setup" => TabLoading,
                "Truss Height" => TabTruss,
                "Pretension Setup" => TabPretension,
                "Output Settings" or "Monitored Deflection" or "Member Forces" or "Foundation Reactions" or "Cable Forces" => TabOutput,
                _ => TabIteration
            };
            Message = $"Review {issue.Section}: {issue.Message}";
        });
        SelectAllStudyRuns = new(_ => SetAllStudyRuns(true), () => !IsBusy && StudyIterations.Count > 0);
        ClearAllStudyRuns = new(_ => SetAllStudyRuns(false), () => !IsBusy && StudyIterations.Count > 0);
        BuildSelectedModels = new(_ => RunStudy(), () => CanRunStudy);
        InspectPreviousRun = new(_ => InspectResumeState(), () => !IsBusy && State.IsSapConnected && StudyIterations.Count > 0);
        ResumeInterruptedStudy = new(_ => RunStudy(StudyRunIntent.ResumeInterrupted), () => CanResumeStudy);
        RetryFailedStudy = new(_ => RunStudy(StudyRunIntent.RetryFailed), () => CanRetryStudy);
        RebuildSelectedModels = new(_ => RunStudy(StudyRunIntent.RebuildSelected), () => CanRebuildStudy);
        // Run the study from anywhere: jump to the Iteration tab (worker monitor + validation live there) then build.
        RunStudyFromToolbar = new(_ => { SelectedTab = TabIteration; RunStudy(); }, () => CanRunStudy);
        CancelStudy = new(_ => CancelStudyExecution(), () => CanCancelStudy);
        OpenStudyFolder = new(_ => TryOpenFolder(StudyRoot), () => Directory.Exists(StudyRoot));
        OpenIterationFolder = new(p => { if (p is StudyIteration row) TryOpenFolder(row.IterationDirectory); });
        OpenRawDataFolder = new(_ =>
        {
            if (Path.GetDirectoryName(RawJsonPath) is { } folder) TryOpenFolder(folder);
        }, () => HasRawData);
        OpenDeflectionReport = new(_ => TryOpenFile(DeflectionReportPath), () => HasDeflectionReport);
        OpenMemberForceReport = new(_ => TryOpenFile(MemberForceReportPath), () => HasMemberForceReport);
        OpenSupportReactionReport = new(_ => TryOpenFile(SupportReactionReportPath), () => HasSupportReactionReport);
        OpenCableForceReport = new(_ => TryOpenFile(CableForceReportPath), () => HasCableForceReport);
        OpenResultsWorkbook = new(_ => TryOpenFile(ResultsWorkbookPath), () => HasResultsWorkbook);
        OpenCandidateDiagnostic = new(p =>
        {
            if (p is PostProcessRow row && Directory.Exists(StudyRoot))
                TryOpenFile(Path.Combine(StudyRoot, row.DiagnosticRelPath.Replace('/', Path.DirectorySeparatorChar)));
        });
        // Generate Outputs writes everything; a module's Regenerate rebuilds its own report only
        GeneratePostProcessOutputs = new(_ => GenerateStudyOutputs(), () => !IsBusy);
        RegenerateDeflectionReport = new(_ => GenerateStudyOutputs(StudyOutputs.DeflectionReport), () => !IsBusy);
        RegenerateMemberForceReport = new(_ => GenerateStudyOutputs(StudyOutputs.MemberForceReport), () => !IsBusy);
        RegenerateSupportReactionReport = new(_ => GenerateStudyOutputs(StudyOutputs.SupportReactionReport), () => !IsBusy);
        RegenerateCableForceReport = new(_ => GenerateStudyOutputs(StudyOutputs.CableForceReport), () => !IsBusy);
        // Modules 02 / 03 / 05 open the member-force report straight on their own Summary.
        OpenTopChordReport = new(_ => TryOpenFile(SummaryReportPath(MemberForceReportGenerator.TopChordFileName) ?? MemberForceReportPath),
            () => HasMemberForceReport);
        OpenBottomChordReport = new(_ => TryOpenFile(SummaryReportPath(MemberForceReportGenerator.BottomChordFileName) ?? MemberForceReportPath),
            () => HasMemberForceReport);
        OpenMegaColumnReport = new(_ => TryOpenFile(SummaryReportPath(MemberForceReportGenerator.MegaColumnFileName) ?? MemberForceReportPath),
            () => HasMemberForceReport);
        BrowsePrintLogo = new(_ =>
        {
            var dialog = new OpenFileDialog { Filter = ReportPrintEngine.LogoFileFilter, CheckFileExists = true, Title = "Company logo for report printouts" };
            if (dialog.ShowDialog() == true) PrintSettings.LogoPath = dialog.FileName;
        });
        ClearPrintLogo = new(_ => PrintSettings.LogoPath = "", () => PrintSettings.LogoPath.Length > 0);
        // the card's own save: the Print Setup alone into the active project (Save All writes it too)
        SavePrintSetup = new(_ => RunSave(ConfigTabs.None, printSetup: true), () => CanUseConfig);
        PrintAllReports = new(_ => PrintAll(StudyRoot, "Study Reports"),
            () => !IsBusy && (HasDeflectionReport || HasMemberForceReport || HasSupportReactionReport || HasCableForceReport));
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
            "Output" => ConfigTabs.Output,
            "Study" => ConfigTabs.Study,
            _ => ConfigTabs.All,
        }), () => CanUseConfig);
        SaveAllConfig = new(_ => SaveAll(), () => CanUseConfig);
        NewConfig = new(_ => InitializeBlankMcotisConfiguration(), () => !IsBusy && !IsFemModelLocked);
        LoadConfig = new(_ => OpenProject(), () => CanUseConfig);
        ReadFemProperties = new(_ => ReadFemModelProperties(), () => CanReadFemProperties);
        RunFemAnalysis = new(_ => RunFemAnalysisFromUi(), () => CanRunFemAnalysis);
        CancelFemAnalysis = new(_ => femCancellation?.Cancel(), () => CanCancelFemAnalysis);
        UnlockFemModel = new(_ => UnlockFemModelForEditing(), () => CanUnlockFemModel);

        void Dirty(ConfigTabs tab) { if (!applyingProject) config.Session.MarkDirty(tab); }

        State.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ApplicationState.TributaryWidthB) or nameof(ApplicationState.NumberOfStories)) Dirty(ConfigTabs.Loading);
            if (e.PropertyName is nameof(ApplicationState.Model) or nameof(ApplicationState.Connection) or
                nameof(ApplicationState.SessionBaselineModel) or nameof(ApplicationState.SessionBaseline) or
                nameof(ApplicationState.TributaryWidthB) or nameof(ApplicationState.NumberOfStories)) ScheduleStudyRegeneration();
            Update();
            if (e.PropertyName is nameof(ApplicationState.Model) or nameof(ApplicationState.Connection) or nameof(ApplicationState.SessionBaselineModel))
                OnSapSessionChangedForQuantities();
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
        void WireCable(CablePretensionConfig row)
        {
            row.Peers = () => State.Cables.ToArray();
            row.PropertyChanged += (_, ev) =>
            {
                Dirty(ConfigTabs.Cable); ScheduleStudyRegeneration(); Update();
                if (ev.PropertyName is nameof(CablePretensionConfig.SyncMode) or nameof(CablePretensionConfig.CableName)
                    or nameof(CablePretensionConfig.ReferenceCableName) or nameof(CablePretensionConfig.SyncFactor)
                    or nameof(CablePretensionConfig.MinForce) or nameof(CablePretensionConfig.MaxForce)
                    or nameof(CablePretensionConfig.StepForce) or nameof(CablePretensionConfig.ApplyRange))
                {
                    Notify(nameof(CableSyncReferenceOptions)); Notify(nameof(SyncGroupSummary));
                    foreach (var c in State.Cables) c.RaiseSyncRecompute();
                }
            };
        }
        foreach (var row in State.Cables) WireCable(row);
        State.Cables.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null) foreach (CablePretensionConfig row in e.NewItems) WireCable(row);
            if (SelectedCable is not null && !State.Cables.Contains(SelectedCable)) SelectedCable = null;
            Dirty(ConfigTabs.Cable);
            Notify(nameof(CableSyncReferenceOptions)); Notify(nameof(SyncGroupSummary));
            foreach (var c in State.Cables) c.RaiseSyncRecompute();
            ScheduleStudyRegeneration();
            Update();
        };
        State.OutputSettings.PropertyChanged += (_, _) =>
        {
            Dirty(ConfigTabs.Output); InvalidateStudyValidation(); Update();
            RequalifyOptimization();        // the deflection band and the Tab 06A criteria live here
        };
        State.OutputSettings.DeflectionPoints.CollectionChanged += (_, _) => { Dirty(ConfigTabs.Output); InvalidateStudyValidation(); Update(); };
        State.OutputSettings.FoundationLeftPoints.CollectionChanged += (_, _) => { Dirty(ConfigTabs.Output); InvalidateStudyValidation(); Update(); };
        State.OutputSettings.FoundationRightPoints.CollectionChanged += (_, _) => { Dirty(ConfigTabs.Output); InvalidateStudyValidation(); Update(); };
        State.OutputSettings.CableNames.CollectionChanged += (_, _) => { Dirty(ConfigTabs.Output); InvalidateStudyValidation(); Update(); };
        State.OutputSettings.MemberForceGroups.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (MemberForceOutputGroup group in e.NewItems)
                {
                    group.PropertyChanged += (_, _) => { Dirty(ConfigTabs.Output); InvalidateStudyValidation(); Update(); };
                    group.Members.CollectionChanged += (_, _) => { Dirty(ConfigTabs.Output); InvalidateStudyValidation(); Update(); };
                }
            Dirty(ConfigTabs.Output); InvalidateStudyValidation(); Update();
        };
        State.Study.PropertyChanged += (_, e) =>
        {
            if (normalizingStudyCandidateIds) return;
            Dirty(ConfigTabs.Study);
            if (e.PropertyName != nameof(IterationStudyConfig.ExcludedIterationKeys)) ScheduleStudyRegeneration();
            else InvalidateStudyValidation();
            Update();
        };
        // Apply one connection policy to every workflow command, including lazy commands
        // from the optimization/FEM partials. Reconnect and active-run cancellation stay usable.
        foreach (var property in GetType().GetProperties().Where(property => property.PropertyType == typeof(Command)))
        {
            if (property.GetValue(this) is not Command command ||
                command == Connect || command == Refresh || command == CancelStudy || command == CancelFemAnalysis ||
                command == LoadFemModel || command == LoadDefaultModel || command == NewConfig) continue;
            // A loaded FEM model (Load Model) runs FEM offline, and the 01 Loading add / remove commands only edit what the
            // app holds; every other command needs the live SAP2000 model.
            command.Availability = command == RunFemAnalysis || command == SaveFemModel || command == UnlockFemModel || command == AddPoints ||
                                   command == RemoveAppliedPoints || command == AddLoadDefinition || command == DeleteLoadDefinition
                ? () => HasWorkingModel
                : () => HasSapConnection;
        }
        PropertyChanged += ForwardPaneStatus;
        RegenerateStudyPreview();
    }

    private GeometryModel OutputBaselineGeometry() =>
        State.SessionBaseline.Points.Count > 0 ? State.SessionBaseline : State.Geometry;

    private async void EditDeflectionOutput()
    {
        if (IsBusy || outputDialogs is null) { if (outputDialogs is null) Message = "Output Settings editor is unavailable."; return; }
        IsBusy = true;
        try
        {
            var loadCases = await workflow.GetLoadCaseNamesAsync();
            if (!outputDialogs.EditDeflection(State.OutputSettings, OutputBaselineGeometry(), loadCases, selection)) return;
            config.Session.MarkDirty(ConfigTabs.Output);
            InvalidateStudyValidation();
            Message = $"{State.OutputSettings.DeflectionPoints.Count} deflection monitoring points · U3 case {State.OutputSettings.DeflectionOutputCaseName}. Groups will be applied to the parent when Tab 05 runs.";
        }
        catch (Exception ex) { Message = ex.Message; Warn("Deflection Output Settings", ex.Message); }
        finally { IsBusy = false; Update(); }
    }

    private async void EditMemberForceOutput()
    {
        if (IsBusy || outputDialogs is null) { if (outputDialogs is null) Message = "Output Settings editor is unavailable."; return; }
        IsBusy = true;
        try
        {
            var loadCases = await workflow.GetLoadCaseNamesAsync();
            if (!outputDialogs.EditMemberForces(State.OutputSettings, OutputBaselineGeometry(), loadCases, selection)) return;
            config.Session.MarkDirty(ConfigTabs.Output);
            InvalidateStudyValidation();
            Message = $"{State.OutputSettings.MemberForceGroups.Count} member-force output groups configured for result case {State.OutputSettings.MemberForceResultLoadCase}. Groups will be applied to the parent when Tab 05 runs.";
        }
        catch (Exception ex) { Message = ex.Message; Warn("Member Force Output Settings", ex.Message); }
        finally { IsBusy = false; Update(); }
    }

    private async void EditFoundationOutput()
    {
        if (IsBusy || outputDialogs is null) { if (outputDialogs is null) Message = "Output Settings editor is unavailable."; return; }
        IsBusy = true;
        try
        {
            var loadCases = await workflow.GetLoadCaseNamesAsync();
            if (!outputDialogs.EditFoundations(State.OutputSettings, OutputBaselineGeometry(), loadCases, selection)) return;
            config.Session.MarkDirty(ConfigTabs.Output);
            InvalidateStudyValidation();
            Message = $"Foundation reaction points configured for result case {State.OutputSettings.FoundationReactionResultLoadCase}. Groups will be applied to the parent when Tab 05 runs.";
        }
        catch (Exception ex) { Message = ex.Message; Warn("Foundation Reaction Output Settings", ex.Message); }
        finally { IsBusy = false; Update(); }
    }

    private async void EditCableOutput()
    {
        if (IsBusy || outputDialogs is null) return;
        IsBusy = true;
        try
        {
            var loadCases = await workflow.GetLoadCaseNamesAsync();
            if (!outputDialogs.EditCables(State.OutputSettings, OutputBaselineGeometry(), loadCases, selection)) return;
            config.Session.MarkDirty(ConfigTabs.Output);
            InvalidateStudyValidation();
            Message = $"{State.OutputSettings.CableNames.Count} cable monitoring objects configured for result case {State.OutputSettings.CableResultLoadCase}.";
        }
        catch (Exception ex) { Message = ex.Message; Warn("Cable Output Settings", ex.Message); }
        finally { IsBusy = false; Update(); }
    }

    private async Task ConnectAndRefreshAsync()
    {
        if (IsOfflineModel)
        {
            // Leaving a loaded FEM model: the live model becomes the session; Tabs 01–05 stay and are re-checked by Refresh.
            // The built-in default model is only a starting point, so its inputs are cleared for the connected model.
            var leavingDefault = isDefaultModel;
            State.Model = null;
            State.SessionBaselineModel = null;
            State.SessionBaseline = GeometryModel.Empty;
            if (leavingDefault) ClearDefaultFemModel();
        }
        State.Connection = "Connecting";
        try
        {
            await workflow.ConnectAsync();
            await workflow.RefreshAsync();
            offlineModelPath = null;
            femInputsOutdated = true;
            if (State.SessionBaselineModel is { Path.Length: > 0 } baseline)
            {
                var modelDirectory = System.IO.Path.GetDirectoryName(baseline.Path);
                if (!string.IsNullOrWhiteSpace(modelDirectory)) State.Study.OutputDirectory = System.IO.Path.Combine(modelDirectory, "Study");
            }
            RefreshDeflectionArtifacts();
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
        preflightReport = StudyPreflightReport.Empty;
        resumeAssessment = null;
        resumeStatusText = "Study definition or selection changed; inspect provenance again before Resume/Retry.";
        ApplyResumeAssessment(null);
    }

    private void NotifyStudyPresentation(bool heightCandidatesChanged = false, bool pretensionCandidatesChanged = false)
    {
        if (heightCandidatesChanged) Notify(nameof(HeightStudyCandidates));
        if (pretensionCandidatesChanged) Notify(nameof(PretensionStudyCandidates));
        foreach (var name in new[]
        {
            nameof(StudyIterations), nameof(LoadingStudyCandidates),
            nameof(LoadingDefinitionColumns), nameof(LoadingDefinitionRows), nameof(HeightDefinitionRows), nameof(PretensionDefinitionGroups),
            nameof(LoadingCandidatesTotalText), nameof(HeightCandidatesTotalText), nameof(PretensionCandidatesTotalText),
            nameof(SelectedCandidateBreadcrumb), nameof(StudySpaceHeadlineFormula),
            nameof(FixedHeightCandidateId), nameof(FixedPretensionCandidateId),
            nameof(FixedHeightValueText), nameof(FixedPretensionValueText), nameof(StudyLoadingCell),
            nameof(StudyHeightCell), nameof(StudyPretensionCell), nameof(StudySpaceFormula), nameof(StudyTotalRuns),
            nameof(BaselineModelName), nameof(BaselineModelPath), nameof(StudyRoot), nameof(GeneratedRunCount), nameof(SelectedRunCount),
            nameof(StudySelectionSummary), nameof(HasStudyIterations), nameof(ShowStudyEmptyState), nameof(StudyPreviewEmptyText),
            nameof(StudyValidationState), nameof(HasValidationIssues), nameof(ShowValidationStatus), nameof(ValidationStatusText),
            nameof(RunStudyLabel), nameof(CanRunStudy), nameof(RunStudyToolTip), nameof(ToolbarStudySummary),
            nameof(ResumableRunCount), nameof(RetryableRunCount), nameof(CanResumeStudy), nameof(CanRetryStudy),
            nameof(CanRebuildStudy), nameof(ResumeStatusText)
            , nameof(PreflightMetrics), nameof(PreflightSummary), nameof(PreflightReady), nameof(FilterAuditText)
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
        preflightReport = studyPreflight.Inspect(State, studyComposition, Issues.ToArray(), RequestedWorkerCount);
        if (showMessage)
            Message = studyValidationState is StudyValidationState.Valid or StudyValidationState.ValidWithWarnings
                ? $"Study is valid - {SelectedRunCount:N0} iterations are ready to build and analyze."
                : $"{Issues.Count} study validation issue{(Issues.Count == 1 ? "" : "s")} need review.";
        NotifyStudyPresentation();
        return preflightReport.Ready;
    }

    private async void ReadFemModelProperties()
    {
        if (IsBusy) return;
        IsBusy = true;
        FemSectionProperties.Clear();
        FemProgressText = "Reading sections, materials, modifiers, end releases, load patterns, cases and combinations from the connected model.";
        try
        {
            var properties = await ReadAndDisplayFemPropertiesAsync(resetLoadRows: true);
            FemProgressText = $"Read section/material properties for {properties.Count:N0} frame and cable members, {FemPatternRows.Count} load patterns, " +
                $"{FemCaseRows.Count} load cases and {FemCombinationRows.Count} combinations" +
                (femSapLoading.Warnings.Count > 0 ? $" ({femSapLoading.Warnings.Count} load warning(s) — see Loads)" : "") + ". No model changes made.";
            Message = FemProgressText;
        }
        catch (Exception ex)
        {
            FemProgressText = $"Property transfer failed: {ex.Message}";
            Message = ex.Message;
            Warn("FEM Model Properties", ex.Message);
        }
        finally { IsBusy = false; Update(); }
    }

    /// <summary>Reads section properties and the SAP loading snapshot. Edited load rows are kept unless
    /// <paramref name="resetLoadRows"/> (Read from SAP) or there are none yet.</summary>
    private async Task<IReadOnlyDictionary<string, FemSectionProperties>> ReadAndDisplayFemPropertiesAsync(bool resetLoadRows = false)
    {
        var properties = await workflow.ReadFemSectionPropertiesAsync();
        var requiredMembers = State.Geometry.Members
            .Where(member => member.Kind is MemberKind.Frame or MemberKind.Cable)
            .Select(member => member.Name).ToHashSet(StringComparer.Ordinal);
        var missingProperty = requiredMembers.FirstOrDefault(name => !properties.ContainsKey(name));
        if (missingProperty is not null)
            throw new InvalidOperationException($"The connected model did not provide section/material properties for '{missingProperty}'.");

        FemSectionProperties.Clear();
        foreach (var property in properties.Values.OrderBy(value => value.MemberName, StringComparer.Ordinal))
            FemSectionProperties.Add(property);
        Notify(nameof(SharedViewerMemberSizes));
        Notify(nameof(SharedViewerSections));
        Notify(nameof(FemMemberSizeHint));

        femSapLoading = await workflow.ReadFemLoadingAsync(properties);
        femInputsOutdated = false;
        if (resetLoadRows || FemPatternRows.Count == 0) ResetFemLoadRows();
        FemLoadWarnings.Clear();
        foreach (var warning in femSapLoading.Warnings) FemLoadWarnings.Add(warning);
        Notify(nameof(FemLoadSummary));
        return properties;
    }

    /// <summary>Connect / Refresh made SAP2000's model the working model, and its sections and loading are not in Mcotis
    /// until Read FEM Properties from SAP brings them in. What was held before belongs to another state of the model.</summary>
    private bool femInputsOutdated;

    /// <summary>The section and material properties the model holds — from Load Model, Read FEM Properties from SAP and the
    /// Define / Assign edits — checked against its geometry. Run Analysis and the optimization solve these; neither reads
    /// SAP2000.</summary>
    private IReadOnlyDictionary<string, FemSectionProperties> HeldFemProperties()
    {
        const string read = "File → Read FEM Properties from SAP brings them in (read-only); the analysis then solves the model held here.";
        if (State.IsSapConnected && femInputsOutdated)
            throw new InvalidOperationException("The sections, materials and loading of the connected SAP2000 model are not in Mcotis yet. " + read);
        var properties = FemSectionProperties.GroupBy(property => property.MemberName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var missing = State.Geometry.Members.FirstOrDefault(member =>
            member.Kind is MemberKind.Frame or MemberKind.Cable && !properties.ContainsKey(member.Name));
        if (missing is not null)
            throw new InvalidOperationException($"The model has no section/material properties for '{missing.Name}'." +
                (State.IsSapConnected ? " " + read : ""));
        return properties;
    }

    // ---- FEM loading (Inputs → Loads): read from SAP, editable here, never written back ----
    private SapFemLoading femSapLoading = SapFemLoading.Empty;
    public ObservableCollection<FemPatternRow> FemPatternRows { get; } = [];
    public ObservableCollection<FemCaseRow> FemCaseRows { get; } = [];
    public ObservableCollection<FemCombinationRow> FemCombinationRows { get; } = [];
    public ObservableCollection<string> FemLoadWarnings { get; } = [];
    public string FemLoadSummary => FemPatternRows.Count == 0
        ? "Not read yet — File → Read FEM Properties from SAP brings the load patterns, cases and combinations into the model."
        : $"{FemPatternRows.Count} patterns · {FemCaseRows.Count(row => row.Run)} of {FemCaseRows.Count} cases and " +
          $"{FemCombinationRows.Count(row => row.Run)} of {FemCombinationRows.Count} combinations ticked to run" +
          (FemLoadWarnings.Count > 0 ? $" · {FemLoadWarnings.Count} load assignment warning(s)" : "");
    private IReadOnlyList<string> Tab01Patterns => State.LoadDefinitions.Select(definition => definition.LoadName)
        .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private IReadOnlyList<string> PretensionPatterns => State.Cables.Select(cable => cable.LoadPattern)
        .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The load rows as SAP2000 has them (the snapshot the model holds), with the default cases for what SAP's
    /// own cases leave out.</summary>
    private void ResetFemLoadRows()
    {
        var (patterns, cases, combinations) = FemLoadPlan.RowsFromSap(femSapLoading, Tab01Patterns, PretensionPatterns);
        SetFemLoadRows(patterns, cases, combinations);
        // Default view: the first combination, else the last case (typically the loaded stage).
        var names = FemLoadSelectionNames;
        if (SelectedFemResultName is null || !names.Contains(SelectedFemResultName, StringComparer.OrdinalIgnoreCase))
            SelectedFemResultName = FemCombinationRows.FirstOrDefault(row => row.Run)?.Name ?? names.LastOrDefault();
    }

    private void LoadRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Notify(nameof(FemLoadSummary));
        Notify(nameof(FemLoadSelectionNames));
    }

    /// <summary>Load cases and combinations ticked to run on Inputs → Loads (cases first), for the Loadcase/Loadcomb pickers.</summary>
    public IReadOnlyList<string> FemLoadSelectionNames => FemCaseRows.Where(row => row.Run).Select(row => row.Name)
        .Concat(FemCombinationRows.Where(row => row.Run).Select(row => row.Name)).ToArray();

    // ---- Which load case / combination the Results page, viewer and cable table show ----
    private string? selectedFemResultName;
    public IReadOnlyList<string> FemResultNames => SelectedFemRun?.Results?.Keys.ToArray() is { Length: > 0 } keys
        ? femResultOrder.Where(keys.Contains).Concat(keys.Except(femResultOrder)).ToArray() : [];
    private IReadOnlyList<string> femResultOrder = [];
    public string? SelectedFemResultName
    {
        get => selectedFemResultName;
        set
        {
            if (!Set(ref selectedFemResultName, value)) return;
            NotifyFemResultSelection();
            RebuildFemCableFitRows();
            Notify(nameof(FemMonitorChartSummary));
        }
    }
    private FemAnalysisResult? SelectedFemResult => SelectedFemRun?.ResultFor(SelectedFemResultName);

    private void RebuildFemCableFitRows()
    {
        var failed = FemCableFitRows.Where(row => row.Failed).ToArray();
        FemCableFitRows.Clear();
        foreach (var run in FemRuns)
        foreach (var row in FemCableFitRow.FromRun(run, SelectedFemResultName)) FemCableFitRows.Add(row);
        foreach (var row in failed) FemCableFitRows.Add(row);
    }

    /// <summary>Takes the solver's report on the solver's own thread (Progress&lt;T&gt; would post it after the run returned).</summary>
    private sealed class FemFailureReport : IProgress<FemProgress>
    {
        public string? Reason { get; private set; }
        public void Report(FemProgress value) => Reason = value.Failure ?? Reason;
    }

    /// <summary>Run → Run FEM Analysis: solves the model as it stands, once — the geometry, sections, loading and settings
    /// Mcotis holds. It never reads or writes SAP2000, and it is not the parametric study: varying the Tab 01–03 ranges or
    /// the VCF system over many cases is Run → Optimization VCF.</summary>
    internal async Task AnalyzeFemAsync()
    {
        if (IsBusy || CommitEditors?.Invoke() == false) return;

        FemStudyCandidate model;
        IReadOnlyList<string> supports;
        IReadOnlyDictionary<string, FemSectionProperties> properties;
        try
        {
            model = femStudy.ComposeModel(State, IsOfflineModel);
            supports = FemStudyService.FindPinSupportPoints(State.Baseline);
            properties = HeldFemProperties();
        }
        catch (Exception ex)
        {
            FemProgressText = ex.Message;
            Message = ex.Message;
            Warn("FEM Inputs", ex.Message);
            Update();
            return;
        }

        try
        {
            if (IsFemModelLocked) DeleteRegisteredFemOutputs();
            LockFemModelForRun();
        }
        catch (Exception ex)
        {
            FemProgressText = $"Run could not lock the model: {ex.Message}";
            Message = FemProgressText;
            Warn("FEM Analysis", FemProgressText);
            Update();
            return;
        }

        IsBusy = true;
        isFemRunning = true;
        femCancellation = new CancellationTokenSource();
        var cancellationToken = femCancellation.Token;
        FemRuns.Clear();
        FemCableFitRows.Clear();
        SelectedFemRun = null;
        var modelName = IsOfflineModel ? OfflineModelName : State.Model?.Name ?? "the model";
        FemProgressText = $"Solving {modelName} as defined…";
        Notify(nameof(FemStatus));
        Notify(nameof(CanCancelFemAnalysis));
        CommandManager.InvalidateRequerySuggested();
        StartFemMonitor();
        try
        {
            FemLogLine($"Solving the model held in Mcotis ({modelName}) — SAP2000 is not read or written. {FemRunSummary}.");
            FemLogLine($"{properties.Count(pair => pair.Value.InPlaneInertiaM4 is not null):N0} frame and {properties.Count(pair => pair.Value.InPlaneInertiaM4 is null):N0} cable sections " +
                $"(self-weight from unit weight × gross area × weight modifier).");
            FemLogLine($"Loading: {femSapLoading.Patterns.Count} patterns, {femSapLoading.JointLoads.Count} joint, " +
                $"{femSapLoading.MemberLoads.Count + femSapLoading.MemberPointLoads.Count + femSapLoading.GravityLoads.Count} member load assignments, " +
                $"{femSapLoading.Cases.Count} cases, {femSapLoading.Combinations.Count} combinations" +
                (femSapLoading.Warnings.Count > 0 ? $" · {femSapLoading.Warnings.Count} warning(s), see Inputs → Loads" : "") + ".");
            var loadPlan = FemLoadPlan.Build(FemPatternRows.ToArray(), FemCaseRows.ToArray(), FemCombinationRows.ToArray(), femSapLoading,
                properties, State.Baseline, Tab01Patterns, PretensionPatterns, State.AppliedPoints.Select(point => point.PointName).ToArray());
            FemLogLine($"Load cases solved: {string.Join(", ", loadPlan.CaseNames)}" +
                (loadPlan.CombinationNames.Count > 0 ? $" · combinations: {string.Join(", ", loadPlan.CombinationNames)}" : "") +
                (loadPlan.FitCases.Count > 0 ? $" · cable target forces fitted in {string.Join(", ", loadPlan.FitCases.Select(name => $"'{name}'"))}" +
                    (loadPlan.FitCases.Count > 1 ? " (each case uses the stage it continues from)" : "") : "") + ".");
            foreach (var note in loadPlan.Notes) FemLogLine("Note: " + note);
            femResultOrder = loadPlan.ResultNames;
            femMonitorTargetStages = loadPlan.FitCases.ToArray();
            Notify(nameof(FemMonitorTargetStages));
            FemMonitorTargetStage = femMonitorTargetStages.FirstOrDefault();
            SelectedFemResultName = loadPlan.CombinationNames.FirstOrDefault() ?? loadPlan.CaseNames[^1];

            var baseline = State.Baseline;
            var targetFitPercent = FemTargetFitPercent;
            var targetFitIterations = FemTargetFitIterations;
            var frameMeshSegments = FemFrameMeshSegments;
            var cableSag = FemCableSag;
            FemMonitorTolerancePercent = targetFitPercent;
            FemLogLine($"Pin supports: {string.Join(", ", supports)}.");
            FemLogLine($"Settings: target-force fitting ±{targetFitPercent:0.###}% · max {targetFitIterations:N0} fitting runs · frame mesh {frameMeshSegments} segments (cables unmeshed) · " +
                (cableSag ? "cable sag ON (parabolic cable under the load each case puts on it)." : "cable sag OFF (straight tension-only cables)."));
            FemLogLine("Split frames at joints and mesh → fit cable pretension in its stage → apply the loads → tension-only equilibrium → recover member forces.");
            var failure = new FemFailureReport();
            // The model's own geometry: no Tab 02 height change, so the chord groups are not passed.
            var results = await Task.Run(() => femStudy.Analyze(baseline, [], [], [], supports, [model], properties, failure,
                cancellationToken, targetFitPercent, targetFitIterations, frameMeshSegments, loadPlan, cableSag), cancellationToken);
            if (results.Count == 0)
            {
                var reason = failure.Reason ?? "The model did not reach equilibrium.";
                FemCableFitRows.Add(FemCableFitRow.FailedCase(model.Id, reason));
                FemProgressText = $"Analysis failed · {reason}";
            }
            else
            {
                var run = results[0];
                FemRuns.Add(run);
                foreach (var row in FemCableFitRow.FromRun(run, SelectedFemResultName)) FemCableFitRows.Add(row);
                SelectedFemRun = run;
                FemLogLine(DescribeFemRun(run));
                var outputDatabase = WriteFemOutput(run);
                FemLogLine($"Output database: {outputDatabase}");
                FemProgressText = "Analysis complete" +
                    (run.Result.TargetFitConverged ? $" · pretension stage within {targetFitPercent:0.###}% of target"
                        : $" · pretension not fitted within {targetFitPercent:0.###}% (closest pretension used) · see Cable Target Fit") +
                    $" · {modelName}.";
            }
            Message = FemProgressText;
            FemLogLine(FemProgressText);
        }
        catch (OperationCanceledException)
        {
            FemProgressText = "Analysis cancelled.";
            Message = FemProgressText;
            FemLogLine(FemProgressText);
        }
        catch (Exception ex)
        {
            FemProgressText = $"FEM analysis failed: {ex.Message}";
            Message = ex.Message;
            FemLogLine(FemProgressText);
            Warn("FEM Analysis", ex.Message);
        }
        finally
        {
            FemMonitorRunning = false;
            isFemRunning = false;
            femCancellation?.Dispose();
            femCancellation = null;
            IsBusy = false;
            Update();
        }
    }

    private async void RunStudy(StudyRunIntent intent = StudyRunIntent.AllSelected)
    {
        if (IsBusy || studyExecution is null) return;
        if (CommitEditors?.Invoke() == false) return;
        RegenerateStudyPreview();
        StudyRunManifest? parentManifest = null;
        if (intent is StudyRunIntent.ResumeInterrupted or StudyRunIntent.RetryFailed)
        {
            try
            {
                var provisional = studySnapshotFactory.Create(State, studyComposition, RequestedWorkerCount);
                resumeAssessment = studyResume.Assess(provisional);
                resumeStatusText = resumeAssessment.Message;
                ApplyResumeAssessment(resumeAssessment);
                var selected = studyResume.Select(provisional, resumeAssessment, intent);
                ApplyExecutionSelection(selected.Jobs.Where(job => job.RunIterate).Select(job => job.IterationId));
                parentManifest = resumeAssessment.Manifest;
                RegenerateStudyPreview();
            }
            catch (Exception ex)
            {
                Message = ex.Message;
                Warn(intent == StudyRunIntent.ResumeInterrupted ? "Unable to Resume Study" : "Unable to Retry Study", ex.Message);
                NotifyStudyPresentation();
                return;
            }
        }
        if (intent == StudyRunIntent.RebuildSelected && MessageBox.Show(
                $"Rebuild {SelectedRunCount:N0} selected candidate folder{(SelectedRunCount == 1 ? "" : "s")}?\n\nExisting folders will be moved to Study\\RebuildArchive before new child models are created. Nothing is deleted.",
                "Archive and Rebuild Selected Candidates", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;
        if (!ValidateStudy(showMessage: true)) return;

        if (outputGroupPreparation is null)
        {
            Message = "Output-group parent preparation service is unavailable.";
            Warn("Unable to Build and Analyze Study Models", Message);
            return;
        }

        IsBusy = true;
        try
        {
            Message = "Preparing Tab 04 output groups on the parent baseline model...";
            await outputGroupPreparation.PrepareParentAsync(State);
            Message = "Parent output groups verified and saved. Freezing study execution snapshot...";
        }
        catch (Exception ex)
        {
            studyBuildState = StudyBuildState.Failed;
            Message = ex.Message;
            Warn("Output Group Preparation Failed", ex.Message + "\n\nNo child models were created.");
            if (ex is System.Runtime.InteropServices.COMException) State.Connection = "Connection Lost";
            NotifyStudyExecution();
            return;
        }
        finally { IsBusy = false; }

        StudyExecutionSnapshot snapshot;
        try { snapshot = studySnapshotFactory.Create(State, studyComposition, RequestedWorkerCount); }
        catch (Exception ex)
        {
            studyBuildState = StudyBuildState.Failed;
            Message = ex.Message;
            Warn("Unable to Build and Analyze Study Models", ex.Message);
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
        studyCompletedCount = studyReusedCount = studyFailedCount = studyRunningCount = studyCancelledCount = studyPeakConcurrent = 0;
        studyQueuedCount = snapshot.Jobs.Count(job => job.RunIterate);
        studySkippedCount = snapshot.Jobs.Count - studyQueuedCount;
        StudyWorkers.Clear();
        PostProcessRows.Clear();
        RawJsonPath = null;
        RawCsvPath = null;
        useCurrentRunArtifacts = true;
        currentRunWorkbookPath = null;
        DeflectionReportPath = null;
        MemberForceReportPath = null;
        SupportReactionReportPath = null;
        CableForceReportPath = null;
        SetRunReference(null, artifactsVerified: false);
        Notify(nameof(ResultsWorkbookPath));
        Notify(nameof(HasResultsWorkbook));
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
            var result = await studyExecution.BuildAsync(snapshot, progress, studyCancellation.Token, AddPostProcessRow,
                intent, parentManifest?.RunId);
            RawJsonPath = result.Artifacts.RawJsonPath;
            RawCsvPath = result.Artifacts.RawCsvPath;
            currentRunWorkbookPath = result.Artifacts.WorkbookPath;
            DeflectionReportPath = result.Artifacts.DeflectionReportPath;
            MemberForceReportPath = result.Artifacts.MemberForceReportPath;
            SupportReactionReportPath = result.Artifacts.SupportReactionReportPath;
            CableForceReportPath = result.Artifacts.CableForceReportPath;
            SetRunReference(result.Run, result.Artifacts.State == StudyArtifactState.Ready);
            Notify(nameof(ResultsWorkbookPath));
            Notify(nameof(HasResultsWorkbook));
            studyCompletedCount = result.Completed;
            studyReusedCount = result.Reused;
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
                    : result.Failed > 0 || result.Artifacts.State == StudyArtifactState.Failed
                        ? StudyBuildState.CompletedWithErrors : StudyBuildState.Completed;
            Message = studyBuildState switch
            {
                StudyBuildState.Completed => $"Build and analysis complete - {result.Completed:N0} verified child models and raw JSON/CSV are ready{(result.Reused > 0 ? $" ({result.Reused:N0} reused existing)" : "")}. Use Tab 06 to generate reports.",
                StudyBuildState.CompletedWithErrors => result.Artifacts.State == StudyArtifactState.Failed
                    ? $"Child analysis finished, but raw-data publication failed: {result.Artifacts.ErrorMessage}"
                    : $"Build and analysis completed with errors - {result.Completed:N0} ready{(result.Reused > 0 ? $" ({result.Reused:N0} reused)" : "")}, {result.Failed:N0} failed.",
                StudyBuildState.Cancelled => $"Build and analysis cancelled - {result.Completed:N0} ready, {result.Cancelled:N0} cancelled.",
                _ => $"Build and analysis failed - all SAP2000 workers faulted; {result.Completed:N0} models are ready and {result.Failed:N0} failed."
            };
        }
        catch (OperationCanceledException)
        {
            SetRunReference(studyExecution.LastRunReference, artifactsVerified: false);
            studyBuildState = StudyBuildState.Cancelled;
            MarkUnfinishedIterations(StudyIterationStatus.Cancelled, "Cancelled before worker assignment.");
            Message = "Study build and analysis cancelled at a safe SAP2000 stage boundary.";
        }
        catch (Exception ex)
        {
            SetRunReference(studyExecution.LastRunReference, artifactsVerified: false);
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
            InspectResumeState();
            NotifyStudyExecution();
            Update();
        }
    }

    private void ApplyExecutionSelection(IEnumerable<string> iterationIds)
    {
        var selected = iterationIds.ToHashSet(StringComparer.Ordinal);
        suppressStudySelectionEvents = true;
        try
        {
            foreach (var row in StudyIterations)
            {
                row.RunIterate = selected.Contains(row.IterationId);
                State.Study.SetRunSelected(row.IterationId, row.RunIterate);
            }
        }
        finally { suppressStudySelectionEvents = false; }
        config.Session.MarkDirty(ConfigTabs.Study);
    }

    private void InspectResumeState()
    {
        try
        {
            if (!State.IsSapConnected || StudyIterations.Count == 0)
                throw new InvalidOperationException("Connect and generate the study to inspect Resume/Retry eligibility.");
            var snapshot = studySnapshotFactory.Create(State, studyComposition, RequestedWorkerCount);
            resumeAssessment = studyResume.Assess(snapshot);
            resumeStatusText = resumeAssessment.Message;
            ApplyResumeAssessment(resumeAssessment);
        }
        catch (Exception ex)
        {
            resumeAssessment = null;
            resumeStatusText = ex.Message;
            ApplyResumeAssessment(null);
        }
        foreach (var name in new[] { nameof(ResumableRunCount), nameof(RetryableRunCount), nameof(CanResumeStudy),
                     nameof(CanRetryStudy), nameof(CanRebuildStudy), nameof(ResumeStatusText) }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }

    private void ApplyResumeAssessment(StudyResumeAssessment? assessment)
    {
        var byId = assessment?.Candidates.ToDictionary(item => item.IterationId, StringComparer.Ordinal);
        foreach (var row in StudyIterations)
        {
            if (byId is not null && byId.TryGetValue(row.IterationId, out var item))
            {
                row.ResumeDispositionText = item.Disposition.ToString();
                row.ResumeReason = item.Reason;
            }
            else
            {
                row.ResumeDispositionText = "Not inspected";
                row.ResumeReason = resumeStatusText;
            }
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
        if (!string.IsNullOrWhiteSpace(progress.RunId) && !string.IsNullOrWhiteSpace(progress.ManifestPath))
            SetRunReference(new StudyRunReference(progress.RunId, progress.ManifestPath, "", "", StudyRunState.Running), artifactsVerified: false);
        if (progress.Total > 0)
        {
            studyCompletedCount = progress.Completed;
            studyReusedCount = progress.Reused;
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
                     (StudyIterationStatus.Ready or StudyIterationStatus.Reused or StudyIterationStatus.Failed or StudyIterationStatus.Cancelled)))
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
            nameof(HasStudyWorkers), nameof(StudyCompletedCount), nameof(StudyReusedCount), nameof(StudyFailedCount),
            nameof(StudyRunningCount), nameof(StudyQueuedCount), nameof(StudySkippedCount),
            nameof(StudyCancelledCount), nameof(StudyFinishedCount), nameof(StudyOverallProgress),
            nameof(StudyProgressText), nameof(StudyCountsText), nameof(StudyBuildState),
            nameof(StudyBuildStateText), nameof(RunStudyLabel), nameof(RunStudyToolTip), nameof(ToolbarStudySummary)
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

    private void TryOpenFile(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new FileNotFoundException($"The file does not exist: {path}");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Message = ex.Message; }
    }

    private async void GenerateStudyOutputs(StudyOutputs outputs = StudyOutputs.All)
    {
        if (IsBusy) return;
        if (!HasRawData)
        {
            PostProcessRows.Clear();
            currentRunWorkbookPath = null;
            discoveredWorkbookPath = null;
            DeflectionReportPath = null;
            MemberForceReportPath = null;
            SupportReactionReportPath = null;
            CableForceReportPath = null;
            Message = $"No {StudyRawDataStore.JsonFileName} found under {StudyRawDataStore.DirectoryName}. Run the study on Tab 05 to extract raw data first.";
            return;
        }
        IsBusy = true;
        try
        {
            var root = StudyRoot;
            // Current Tab 03 capacity wins per cable; a Tab 03 cable that cannot resolve one is reported as
            // unavailable, and only a cable Tab 03 does not know falls back to the capacity stored with the results.
            var capacities = CurrentCableCapacities();
            var configuredCables = State.Cables.Select(cable => cable.CableName)
                .Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
            var band = State.OutputSettings.DeflectionLimitBand;
            var print = PrintSettings.Clone();
            // One report can be rebuilt alone only while the other outputs still belong to the same raw data;
            // after the raw package changed they are all stale, so everything is generated.
            var rawChanged = outputs != StudyOutputs.All && !await Task.Run(() => DerivedOutputsFollowRawData(root));
            if (rawChanged) outputs = StudyOutputs.All;
            if (outputs != StudyOutputs.All)
            {
                await RegenerateOneReport(root, capacities, band, configuredCables, print, outputs);
                return;
            }
            var artifacts = await Task.Run(() => new StudyPostProcessor().Generate(root, capacities, band, configuredCables, print));
            currentRunWorkbookPath = artifacts.WorkbookPath;
            useCurrentRunArtifacts = true;
            DeflectionReportPath = artifacts.DeflectionReportPath;
            MemberForceReportPath = artifacts.MemberForceReportPath;
            SupportReactionReportPath = artifacts.SupportReactionReportPath;
            CableForceReportPath = artifacts.CableForceReportPath;
            StudyRunJournal.ReplaceArtifacts(root, artifacts);
            RefreshDeflectionArtifacts();
            Message = (rawChanged ? "The raw data changed since the outputs were generated, so every Tab 06 output was regenerated" : "Tab 06 outputs generated") +
                      $" from {StudyRawDataStore.JsonFileName}" +
                      (capacities.Count > 0
                          ? $" · capacity from fpu {State.CableStrengthMpa:0.###} MPa · φ {State.CableStrengthReductionFactor:0.###}."
                          : ".") + " No SAP2000 run was performed.";
        }
        catch (Exception ex)
        {
            if (outputs == StudyOutputs.All)
            {
                currentRunWorkbookPath = null;
                discoveredWorkbookPath = null;
                DeflectionReportPath = null;
                MemberForceReportPath = null;
                SupportReactionReportPath = null;
                CableForceReportPath = null;
            }
            Message = ex.Message;
            Warn("Generate Post-Processing Outputs", ex.Message);
        }
        finally { IsBusy = false; }
    }

    /// <summary>A module's own Regenerate: that one report from the raw data, with the current capacities, limit
    /// band and Print Setup. The Excel workbook, the candidate diagnostics and the other reports are left exactly
    /// as they are — <c>Generate Outputs</c> rebuilds everything.</summary>
    private async Task RegenerateOneReport(string root, IReadOnlyDictionary<string, double> capacities, DeflectionLimitBand band,
        IReadOnlyCollection<string> configuredCables, ReportPrintSettings print, StudyOutputs output)
    {
        var clock = Stopwatch.StartNew();
        var (kind, path) = await Task.Run(() =>
        {
            var artifacts = new StudyPostProcessor().Generate(root, capacities, band, configuredCables, print, output);
            var written = output switch
            {
                StudyOutputs.DeflectionReport => ("DeflectionReport", artifacts.DeflectionReportPath),
                StudyOutputs.MemberForceReport => ("MemberForceReport", artifacts.MemberForceReportPath),
                StudyOutputs.SupportReactionReport => ("SupportReactionReport", artifacts.SupportReactionReportPath),
                StudyOutputs.CableForceReport => ("CableForceReport", artifacts.CableForceReportPath),
                _ => throw new ArgumentOutOfRangeException(nameof(output), output, "Not a single report."),
            };
            // the run journal records this report's new content; every other entry stays as it is
            if (written.Item2 is { } file) StudyRunJournal.RefreshArtifact(root, written.Item1, file);
            return written;
        });
        switch (output)
        {
            case StudyOutputs.DeflectionReport: DeflectionReportPath = path; break;
            case StudyOutputs.MemberForceReport: MemberForceReportPath = path; break;
            case StudyOutputs.SupportReactionReport: SupportReactionReportPath = path; break;
            case StudyOutputs.CableForceReport: CableForceReportPath = path; break;
        }
        CommandManager.InvalidateRequerySuggested();
        Message = path is null
            ? $"The raw data holds no results for this report ({kind}) — nothing was regenerated."
            : $"{Path.GetFileName(path)} regenerated in {clock.Elapsed.TotalSeconds:0.0} s from {StudyRawDataStore.JsonFileName} — this report only. " +
              "Generate Outputs rebuilds the Excel workbook and every other output. No SAP2000 run was performed.";
    }

    /// <summary>Whether the outputs on disk were generated from the raw package as it is now — the run journal
    /// recorded that file's content. Without a journal there is nothing to keep consistent.</summary>
    private static bool DerivedOutputsFollowRawData(string root)
    {
        var manifest = StudyRunJournal.TryRead(root);
        if (manifest is null) return true;
        var rawJson = Path.Combine(root, StudyRawDataStore.DirectoryName, StudyRawDataStore.JsonFileName);
        return SamePath(StudyRunJournal.ValidArtifactPath(manifest, "RawJson"), rawJson);
    }

    /// <summary>Asks where to save a PDF — suggested folder, suggested file name — and returns the chosen path, or
    /// null when the user cancels. A Windows Save dialog; the checks replace it.</summary>
    internal Func<string, string, string?> AskPdfPath { get; set; } = (folder, fileName) =>
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save all reports as one PDF", Filter = "PDF document (*.pdf)|*.pdf", DefaultExt = ".pdf", AddExtension = true,
            OverwritePrompt = true, FileName = fileName, InitialDirectory = Directory.Exists(folder) ? folder : "",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };

    /// <summary>Writes every report of a folder into one PDF: folder, PDF path, Print Setup, document title. The
    /// headless browser of <see cref="ReportPdfExporter"/>; the checks replace it.</summary>
    internal Func<string, string, ReportPrintSettings, string, ReportPdfResult> PrintPackage { get; set; } =
        (folder, pdfPath, settings, title) => ReportPdfExporter.ExportPackage(folder, pdfPath, settings, title);

    private string? lastPdfFolder;

    /// <summary>Print All Reports: every report generated in <paramref name="folder"/> (the study, or its Optimized
    /// set) as one PDF with the current Print Setup, numbered through. Asks only where to save — no print dialog —
    /// then opens the PDF. Runs a headless browser in the background; no SAP2000.</summary>
    private async void PrintAll(string folder, string what)
    {
        if (IsBusy) return;
        if (StudyReportPackage.Items(folder).Count == 0)
        {
            Message = "There is no report to print yet. Generate the outputs first.";
            return;
        }
        var title = $"{BaselineModelName} - {what}";
        var fileName = string.Concat(title.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0 ? c : '_')) + ".pdf";
        var pdfPath = AskPdfPath(lastPdfFolder is { } last && Directory.Exists(last) ? last : folder, fileName);
        if (string.IsNullOrWhiteSpace(pdfPath)) return;
        lastPdfFolder = Path.GetDirectoryName(pdfPath);

        IsBusy = true;
        Message = $"Printing all reports to {Path.GetFileName(pdfPath)}…";
        try
        {
            var settings = PrintSettings.Clone();
            var result = await Task.Run(() => PrintPackage(folder, pdfPath, settings, title));
            Message = $"{Path.GetFileName(result.PdfPath)} written" + (result.PageCount > 0 ? $" — {result.PageCount:N0} pages" : "") +
                      $" in {result.Elapsed.TotalSeconds:0.0} s with the current Print Setup. Charts are vector graphics.";
            if (File.Exists(result.PdfPath)) TryOpenFile(result.PdfPath);
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            Warn("Print All Reports", ex.Message, warning: true);
        }
        finally { IsBusy = false; }
    }

    /// <summary>The page that opens the member-force report on one group's Summary (top chord, bottom chord,
    /// mega column) — present only when this study has that group.</summary>
    private string? SummaryReportPath(string launcherFileName)
    {
        if (!HasMemberForceReport || Path.GetDirectoryName(MemberForceReportPath!) is not { } folder) return null;
        var path = Path.Combine(folder, launcherFileName);
        return File.Exists(path) ? path : null;
    }

    private void AddPostProcessRow(COT_VCF.Domain.Results.CandidateResultDto dto)
    {
        var points = dto.Groups.Sum(g => g.Points.Count);
        var valid = dto.Groups.Sum(g => g.Points.Count(p => p.Status == COT_VCF.Domain.Results.ResultValueStatus.Valid));
        var p = dto.Parameters;
        var fileName = string.IsNullOrEmpty(dto.CandidateModelPath) ? $"{dto.IterationKey}.sdb" : Path.GetFileName(dto.CandidateModelPath);
        var description = $"{p.LoadingLabel} · H {p.TrussHeightM:0.##} m · {p.PretensionLabel}";
        var row = new PostProcessRow(dto.IterationKey, fileName, description, dto.ExtractionStatus.ToString(),
            points, valid, dto.Warnings.Count, dto.CandidateModelPath, dto.DiagnosticReportRelativePath);
        void add() { PostProcessRows.Add(row); Notify(nameof(PostProcessStatus)); }
        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess()) d.Invoke(add); else add();
    }

    /// <summary>Picks up a Deflection report already sitting in the current baseline's Study folder.</summary>
    public void RefreshDeflectionArtifacts()
    {
        try
        {
            useCurrentRunArtifacts = false;
            currentRunWorkbookPath = null;
            PostProcessRows.Clear();
            RawJsonPath = null;
            RawCsvPath = null;
            var rawJson = Path.Combine(StudyRoot, StudyRawDataStore.DirectoryName, StudyRawDataStore.JsonFileName);
            var rawCsv = Path.Combine(StudyRoot, StudyRawDataStore.DirectoryName, StudyRawDataStore.CsvFileName);
            if (File.Exists(rawJson))
            {
                var raw = new StudyRawDataStore().Read(rawJson);
                if (SamePath(raw.BaselineModelPath, State.SessionBaselineModel?.Path ?? State.Model?.Path))
                {
                    RawJsonPath = rawJson;
                    RawCsvPath = File.Exists(rawCsv) ? rawCsv : null;
                    foreach (var result in raw.Results) AddPostProcessRow(result);
                }
            }
            var manifest = Directory.Exists(StudyRoot) ? StudyRunJournal.TryRead(StudyRoot) : null;
            if (manifest is null || !SamePath(manifest.BaselineModelPath, State.SessionBaselineModel?.Path ?? State.Model?.Path))
            {
                discoveredWorkbookPath = null;
                DeflectionReportPath = null;
                MemberForceReportPath = null;
                SupportReactionReportPath = null;
                CableForceReportPath = null;
                SetRunReference(null, artifactsVerified: false);
            }
            else
            {
                // If a user edits the raw JSON, derived outputs are intentionally considered stale until
                // Tab 06 is generated again. The raw file itself remains available as the flexible input.
                var recordedRaw = StudyRunJournal.ValidArtifactPath(manifest, "RawJson");
                var derivedAreCurrent = HasRawData && SamePath(recordedRaw, RawJsonPath);
                discoveredWorkbookPath = derivedAreCurrent ? StudyRunJournal.ValidArtifactPath(manifest, "Workbook") : null;
                DeflectionReportPath = derivedAreCurrent ? StudyRunJournal.ValidArtifactPath(manifest, "DeflectionReport") : null;
                MemberForceReportPath = derivedAreCurrent ? StudyRunJournal.ValidArtifactPath(manifest, "MemberForceReport") : null;
                SupportReactionReportPath = derivedAreCurrent ? StudyRunJournal.ValidArtifactPath(manifest, "SupportReactionReport") : null;
                CableForceReportPath = derivedAreCurrent ? StudyRunJournal.ValidArtifactPath(manifest, "CableForceReport") : null;
                SetRunReference(new StudyRunReference(manifest.RunId, Path.Combine(StudyRoot, StudyRunJournal.FileName),
                    manifest.BaselineSha256, manifest.SnapshotSha256, manifest.State), StudyRunJournal.ArtifactsAreIntact(manifest));
            }
        }
        catch
        {
            PostProcessRows.Clear();
            RawJsonPath = null;
            RawCsvPath = null;
            discoveredWorkbookPath = null;
            DeflectionReportPath = null;
            MemberForceReportPath = null;
            SupportReactionReportPath = null;
            CableForceReportPath = null;
            SetRunReference(null, false);
        }
        Notify(nameof(HasResultsWorkbook));
        Notify(nameof(ResultsWorkbookPath));
        Notify(nameof(PostProcessStatus));
        CommandManager.InvalidateRequerySuggested();
    }

    private string? discoveredWorkbookPath;

    private void SetRunReference(StudyRunReference? run, bool artifactsVerified)
    {
        currentRunId = run?.RunId;
        currentRunState = run?.State;
        runArtifactsVerified = artifactsVerified;
        Notify(nameof(RunProvenanceText));
        CommandManager.InvalidateRequerySuggested();
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
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
        State.LoadDefinitions.Count > 0 && State.LoadDefinitions.All(row => row.CandidateCount > 0 && row.CandidateCount <= LoadDefinition.MaterializeCeiling && State.LoadPatterns.Contains(row.LoadName, StringComparer.Ordinal)) &&
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

        // A loading combination is a Cartesian pick of one qz per load definition. Reject the product before
        // materializing per-axis values or the preview rows.
        var total = LoadingCandidates.TotalCount(State.LoadDefinitions);
        if (total > StudyCompositionService.MaxPreviewRows)
        {
            Message = $"Loading preview has {total:N0} combinations; reduce it to {StudyCompositionService.MaxPreviewRows:N0} or fewer.";
            return null;
        }
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

    private async void AddCablesFromSelection()
    {
        if (IsBusy) return;
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
        var added = new List<CablePretensionConfig>();
        foreach (var name in picked.Distinct(StringComparer.Ordinal))
            if (existing.Add(name))
            {
                var cable = new CablePretensionConfig { CableName = name };
                State.Cables.Add(cable);
                added.Add(cable);
            }

        if (added.Count == 0) { Message = "Cable selection unchanged."; Update(); return; }
        SelectedCable = added[0];

        // Pre-fill each new row from the connected SAP2000 model: its existing target force, and its section
        // area (→ resolved design capacity for the utilisation views in the cable-force report).
        int prefilled = 0, sized = 0;
        if (State.Model is not null)
        {
            IsBusy = true;
            try
            {
                var existingForces = await workflow.ReadCableTargetForcesAsync(added.Select(c => c.CableName));
                foreach (var cable in added)
                    if (existingForces.TryGetValue(cable.CableName, out var tf))
                    {
                        cable.LoadPattern = tf.LoadPattern;
                        cable.TargetForce = tf.TargetForce;
                        prefilled++;
                    }
            }
            catch (Exception ex) { Warn("Add Cable", $"Could not read existing target forces: {ex.Message}"); }

            try
            {
                var sections = await workflow.ReadCableSectionsAsync(added.Select(c => c.CableName));
                foreach (var cable in added)
                    if (sections.TryGetValue(cable.CableName, out var section) && section.AreaM2 > 0)
                    {
                        cable.SectionName = section.PropertyName;
                        cable.SectionAreaM2 = section.AreaM2;
                        if (cable.ResolveCapacity(State.CableStrengthMpa, State.CableStrengthReductionFactor) is { } capacity)
                            cable.DesignCapacityKN = capacity;
                        sized++;
                    }
            }
            catch (Exception ex) { Warn("Add Cable", $"Could not read cable sections: {ex.Message}"); }
            finally { IsBusy = false; }
        }

        Message = prefilled > 0 || sized > 0
            ? $"Added {added.Count} cable{(added.Count == 1 ? "" : "s")} · {prefilled} pre-filled, {sized} sized from SAP2000. Remove with − Remove."
            : $"Added {added.Count} cable{(added.Count == 1 ? "" : "s")}. Remove with − Remove.";
        Update();
    }

    private async void RefreshCableSectionsFromSap()
    {
        if (IsBusy || !State.IsSapConnected || State.Model is null || State.Cables.Count == 0) return;
        var cables = State.Cables.ToArray();
        IsBusy = true;
        Message = $"Refreshing section and area for {cables.Length} cable{(cables.Length == 1 ? "" : "s")} from SAP2000...";
        try
        {
            var sections = await workflow.ReadCableSectionsAsync(cables.Select(cable => cable.CableName));
            var problems = new List<string>();
            var refreshed = 0;
            foreach (var cable in cables)
            {
                if (!sections.TryGetValue(cable.CableName, out var section))
                {
                    cable.SectionName = null;
                    cable.SectionAreaM2 = null;
                    cable.DesignCapacityKN = null;
                    problems.Add($"{cable.CableName}: SAP2000 returned no section row.");
                    continue;
                }

                cable.SectionName = string.IsNullOrWhiteSpace(section.PropertyName) ? null : section.PropertyName;
                if (section.AreaM2 > 0 && double.IsFinite(section.AreaM2))
                {
                    cable.SectionAreaM2 = section.AreaM2;
                    cable.DesignCapacityKN = cable.ResolveCapacity(
                        State.CableStrengthMpa, State.CableStrengthReductionFactor);
                    refreshed++;
                }
                else
                {
                    cable.SectionAreaM2 = null;
                    cable.DesignCapacityKN = null;
                    problems.Add(section.Problem ?? $"{cable.CableName}: section area is unavailable.");
                }
            }

            Message = problems.Count == 0
                ? $"Refreshed section, area, and design capacity for {refreshed} cable{(refreshed == 1 ? "" : "s")}."
                : $"Refreshed {refreshed} of {cables.Length} cable sections; {problems.Count} could not be read.";
            if (problems.Count > 0)
                Warn("Refresh Cable Sections", string.Join("\n", problems.Select(problem => "• " + problem)), warning: true);
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            Warn("Refresh Cable Sections", ex.Message, warning: true);
        }
        finally
        {
            IsBusy = false;
            Update();
        }
    }

    private const string ConfigFilter = "Mcotis Configuration (*.json)|*.json|JSON Files (*.json)|*.json";

    private string? PickSavePath(ConfigTabs tabs)
    {
        var dialog = new SaveFileDialog { Filter = ConfigFilter, FileName = config.SuggestFileName(tabs), AddExtension = true, DefaultExt = ".json" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    // Per-tab Save: overwrites only this tab's section of the active project (other tabs come from the
    // in-memory project, NOT their unsaved UI); creates the project via Save-As the first time.
    private void SaveTab(ConfigTabs single) => RunSave(single);
    // Save All: extracts every tab from the UI — and the Print Setup — and rewrites the whole project atomically.
    private void SaveAll() => RunSave(ConfigTabs.All, printSetup: true);

    private void StartNewConfiguration()
    {
        if (!CanUseConfig) return;
        applyingProject = true;
        try { config.NewProject(); }
        finally { applyingProject = false; }
        SelectedAppliedPoint = null;
        SelectedCable = null;
        ClearFemResults();
        StartNewMcotisConfigurationFile();
        PreviewMessage = "New configuration — select loading points, truss groups and cables.";
        SelectedTab = TabLoading;
        RegenerateStudyPreview();
        Message = "New MCOTiS configuration started. Save Config to choose its file.";
        Update();
    }

    /// <param name="printSetup">Write the Print Setup card into the project too. With no tab at all
    /// (<see cref="ConfigTabs.None"/>) that is the only thing the save changes.</param>
    private async void RunSave(ConfigTabs uiTabs, bool printSetup = false)
    {
        if (IsBusy) return;
        if (CommitEditors?.Invoke() == false) return;
        if (!config.CanUse) { Warn("Cannot Save Configuration", "Connect to a SAP2000 model before saving a configuration."); return; }

        IsBusy = true;
        try
        {
            var outcome = await config.SaveAsync(uiTabs, () => PickSavePath(uiTabs), printSetup ? PrintSettings.Clone() : null);
            switch (outcome.Result)
            {
                case SaveResult.Saved:
                    Message = uiTabs == ConfigTabs.All
                        ? $"Project configuration saved · {config.Session.FileName}"
                        : uiTabs == ConfigTabs.None
                            ? $"Print Setup saved · {config.Session.FileName}"
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

    private static string? FindTestsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var tests = Path.Combine(directory.FullName, "tests");
            if (Directory.Exists(tests)) return tests;
        }
        return null;
    }

    // Load Config = open a project. Full-or-nothing: every present tab must validate against the
    // connected model, or the current project is left untouched.
    private void OpenProject()
    {
        if (IsBusy) return;
        if (CommitEditors?.Invoke() == false) return;
        if (!config.CanUse) { Warn("Cannot Load Configuration", "Connect to a SAP2000 model first."); return; }

        var dialog = new OpenFileDialog { Filter = ConfigFilter, CheckFileExists = true };
        if (FindTestsDirectory() is { } testsDirectory) dialog.InitialDirectory = testsDirectory;
        if (dialog.ShowDialog() == true) OpenProjectFile(dialog.FileName);
    }

    /// <summary>Opens the project at <paramref name="path"/> — what Load Config does once the file is chosen.</summary>
    internal async void OpenProjectFile(string path)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            CotVcfConfigDto project;
            try { project = config.Read(path); }
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
            try
            {
                config.Apply(project, check);
                // a project that carries a Print Setup brings it along; an older one leaves the card as it is
                if (project.PrintSetup is { } printSetup) ConfigurationApplicator.ApplyPrintSetup(printSetup, PrintSettings);
            }
            finally { applyingProject = false; }
            RegenerateStudyPreview();
            config.SetActiveProject(path, project);
            config.Session.ClearDirty();
            SelectedAppliedPoint = null;
            SelectedCable = null;
            Update();

            var present = new List<string>();
            if (check.Loading.Present) present.Add("Loading");
            if (check.Truss.Present) present.Add("Truss Height");
            if (check.Cable.Present) present.Add("Cable Force");
            if (check.Output.Present) present.Add("Output Settings");
            if (check.Study.Present) present.Add("Iteration Study");
            if (project.PrintSetup is not null) present.Add("Print Setup");

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
        Add("Tab 01 – Loading", r.Loading);
        Add("Tab 02 – Truss Height", r.Truss);
        Add("Tab 03 – Cable Force", r.Cable);
        Add("Tab 04 – Output Settings", r.Output);
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
        if (tabs.HasFlag(ConfigTabs.Output)) parts.Add("Output Settings");
        if (tabs.HasFlag(ConfigTabs.Study)) parts.Add("Iteration Study");
        return string.Join(" + ", parts);
    }

    private string CurrentModelFileName() => State.Model is { } m ? System.IO.Path.GetFileName(m.Path) : "(unknown)";

    /// <summary>Checks replace the message box with this.</summary>
    internal Action<string, string>? WarningSink { get; set; }

    private void Warn(string title, string body, bool warning = false)
    {
        if (WarningSink is { } sink) { sink(title, body); return; }
        MessageBox.Show(body, title, System.Windows.MessageBoxButton.OK,
            warning ? System.Windows.MessageBoxImage.Warning : System.Windows.MessageBoxImage.Information);
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
        RefreshFemPatternRows();
        Notify(nameof(CanDefineLoadPatterns));
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
            nameof(TrussStatus), nameof(CableStatus), nameof(OutputStatus), nameof(CableCount), nameof(VariableCableCount), nameof(PretensionCandidateCount),
            nameof(CableStrengthMpa), nameof(CableStrengthReductionFactor),
            nameof(CablePreviewSummary), nameof(DeflectionOutputSummary), nameof(MemberForceOutputSummary),
            nameof(FoundationOutputSummary), nameof(CableOutputSummary),
            nameof(MonitoredPointSummary), nameof(MonitoredPointCaseSummary), nameof(MonitoredPointStatus), nameof(DeflectionLimitSummary), nameof(MemberForceSummary), nameof(MemberForceStatus),
            nameof(FoundationSummary), nameof(FoundationStatus), nameof(CableForceSummary), nameof(CableForceStatus),
            nameof(ConfiguredOutputCount), nameof(OutputUnitsSummary),
            nameof(HasSapConnection), nameof(HasWorkingModel), nameof(IsOfflineModel), nameof(IsDefaultModel), nameof(OfflineModelSummary), nameof(CanSaveFemModel), nameof(CanUseConfig), nameof(HasProject), nameof(ProjectName), nameof(PrintSetupProjectStatus),
            nameof(FemRunSummary), nameof(FemPinSupportSummary), nameof(FemProgressText), nameof(FemStatus), nameof(IsFemModelLocked),
            nameof(CanDefineFemModel), nameof(HasModelGeometry), nameof(HasUsableModelGeometry), nameof(ShowBlankModelChoice), nameof(CanEditFemModel), nameof(CanEditVcfGeometry),
            nameof(CanUnlockFemModel), nameof(FemModelEditState), nameof(FemDatabaseSummary),
            nameof(FemOutputCaseChoices), nameof(FemDeflectionOutputCase), nameof(FemMemberForceOutputCase), nameof(FemFoundationOutputCase), nameof(FemCableOutputCase),
            nameof(FemDeflectionJointNames), nameof(FemFoundationLeftJointNames), nameof(FemFoundationRightJointNames), nameof(FemCableObjectNames), nameof(FemFrameObjectNames),
            nameof(FemDeflectionLimitMinMm), nameof(FemDeflectionLimitMaxMm), nameof(FemMaximumCableUtilization), nameof(FemOutputSettingsStatus),
            nameof(CanRunFemAnalysis), nameof(CanCancelFemAnalysis), nameof(PaneStatus), nameof(HasFemResults),
            nameof(LoadingCandidateCount), nameof(HeightCandidateCount),
            nameof(StudyModeLabel), nameof(StudyNeedsFixedHeight), nameof(StudyNeedsFixedPretension),
            nameof(StudyRoot), nameof(RequestedWorkerCount), nameof(MaxWorkerCount),
            nameof(HasRawData), nameof(RawDataSummary), nameof(RawJsonPath), nameof(RawCsvPath),
            nameof(HasDeflectionReport), nameof(HasNoDeflectionReport), nameof(DeflectionReportUri), nameof(DeflectionReportName),
            nameof(HasMemberForceReport), nameof(MemberForceReportName),
            nameof(HasSupportReactionReport), nameof(SupportReactionReportName),
            nameof(HasCableForceReport), nameof(CableForceReportName),
            nameof(HasResultsWorkbook), nameof(ResultsWorkbookPath), nameof(PostProcessStatus)
        }) Notify(name);
        NotifySharedViewer();
        NotifyStudyPresentation();
        ScheduleFemWorkingSave();
        CommandManager.InvalidateRequerySuggested();
    }
}
