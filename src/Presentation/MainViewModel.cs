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
    private readonly IOutputSettingsDialogService? outputDialogs;
    private readonly OutputGroupPreparationService? outputGroupPreparation;
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
    public int SelectedTab
    {
        get => selectedTab;
        set { if (Set(ref selectedTab, value)) NotifySharedViewer(); }
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

    // ---- Hoisted model preview: one Structural2DViewer shared by tabs 01-04 (index 0-3) ----
    // Workflow tabs after the SAP Model tab was removed: 0 Loading, 1 Truss, 2 Pretension, 3 Output, 4 Iteration.
    private const int TabLoading = 0, TabTruss = 1, TabPretension = 2, TabOutput = 3, TabIteration = 4;
    public bool ShowSharedViewer => selectedTab is >= TabLoading and <= TabOutput;
    public bool ShowTrussLegend => selectedTab == TabTruss;
    public GeometryModel SharedViewerModel =>
        (selectedTab == TabTruss ? State.Preview : State.Geometry) ?? GeometryModel.Empty;
    public IEnumerable<AppliedPoint>? SharedViewerAppliedPoints =>
        selectedTab == TabLoading ? State.AppliedPoints : null;
    public IEnumerable<string>? SharedViewerTopPoints => selectedTab == TabTruss ? State.Truss.TopPoints : null;
    public IEnumerable<string>? SharedViewerMiddlePoints => selectedTab == TabTruss ? State.Truss.MiddlePoints : null;
    public IEnumerable<string>? SharedViewerBottomPoints => selectedTab == TabTruss ? State.Truss.BottomPoints : null;
    public IEnumerable<CablePretensionConfig>? SharedViewerCables =>
        selectedTab is TabPretension or TabOutput ? State.Cables : null;
    public string SharedViewerCaption => selectedTab switch
    {
        TabTruss => HeightSummary,
        TabPretension => CablePreviewSummary,
        TabOutput => GeometrySummary,
        _ => "",
    };
    public string SharedViewerHint => selectedTab switch
    {
        TabLoading => "Applied load locations are preview overlays; grid and viewer selection stay in sync.",
        TabTruss => "Preview height only — Top red · Middle blue · Bottom green.",
        TabPretension => "Configured cables are highlighted and labelled by SAP2000 cable name.",
        TabOutput => "Point and member labels for picking deflection points, foundation reactions, member-force groups and monitored cables.",
        _ => "",
    };
    private void NotifySharedViewer()
    {
        Notify(nameof(ShowSharedViewer)); Notify(nameof(ShowTrussLegend));
        Notify(nameof(SharedViewerModel)); Notify(nameof(SharedViewerAppliedPoints));
        Notify(nameof(SharedViewerTopPoints)); Notify(nameof(SharedViewerMiddlePoints)); Notify(nameof(SharedViewerBottomPoints));
        Notify(nameof(SharedViewerCables)); Notify(nameof(SharedViewerCaption)); Notify(nameof(SharedViewerHint));
    }
    public bool CanViewTrussCandidates => TrussCandidatesReady(out _);
    public string TrussCandidatesHint { get { TrussCandidatesReady(out var reason); return reason; } }
    public string IterationSummary => $"MODEL\n{ModelName} - {State.Units}\n\nLOADING CRITERIA\n{State.AppliedPoints.Count} applied points - {State.LoadDefinitions.Count} load definitions\n\nTRUSS HEIGHT\n{State.Truss.MinimumHeight:F4} to {State.Truss.MaximumHeight:F4} {LengthUnit} - step {State.Truss.Increment:F4} {LengthUnit}\n\nCABLE PRETENSION\n{CableCount} cables ({VariableCableCount} variable) - {PretensionCandidateCount} pretension candidates";
    public int CableCount => State.Cables.Count;
    public int VariableCableCount => COT_VCF.Application.PretensionCandidates.VariableCount(State.Cables);
    /// <summary>"Same As" column options: a blank (independent) plus every current cable name.</summary>
    public IEnumerable<string> CableReferenceOptions => new[] { "" }.Concat(State.Cables.Select(c => c.CableName));
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
            return $"{LoadingCandidateCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} \\times {h} \\times {p} = {StudyTotalRuns}";
        }
    }
    /// <summary>Symbolic + numeric total, e.g. "N_L \times N_H \times N_P = 4 \times 5 \times 2 = 40".</summary>
    public string StudySpaceHeadlineFormula => $"N_L \\times N_H \\times N_P = {StudySpaceFormula}";
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
    public string ResultsWorkbookPath => Directory.Exists(StudyRoot)
        ? Path.Combine(StudyRoot, COT_VCF.Application.Results.ConsolidatedResultWriter.WorkbookFileName) : "";
    public bool HasResultsWorkbook => !string.IsNullOrEmpty(ResultsWorkbookPath) && File.Exists(ResultsWorkbookPath);
    public string PostProcessStatus
    {
        get
        {
            var ok = PostProcessRows.Count(r => r.ExtractionStatus == "ExtractedOk");
            var total = PostProcessRows.Count;
            if (!HasDeflectionReport && !HasResultsWorkbook) return "○ No deflection results — run the study on Tab 05.";
            if (!HasDeflectionReport) return "▲ Results present but no report — press Regenerate.";
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
    public bool CanRunStudy => !IsBusy && studyExecution is not null && State.IsSapConnected && SelectedRunCount > 0;
    public string RunStudyToolTip => RequestedWorkerCount == 1
        ? "Uses the connected SAP2000 instance as worker 1 to build, analyze, save, and verify each child; no additional process is started."
        : $"Uses the connected SAP2000 instance as worker 1 and starts {RequestedWorkerCount - 1} additional processes. Up to {RequestedWorkerCount} child models build and analyze concurrently.";
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
            return groups == 0 ? "Not configured" : $"{groups} group{(groups == 1 ? "" : "s")} · {members} frame{(members == 1 ? "" : "s")}";
        }
    }
    public string FoundationOutputSummary => $"Left {State.OutputSettings.FoundationLeftPoints.Count}/2 · Right {State.OutputSettings.FoundationRightPoints.Count}/2";
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
    public string MonitoredPointStatus => Out.DeflectionPoints.Count == 0 ? StatusNotConfigured
        : string.IsNullOrWhiteSpace(Out.DeflectionOutputCaseName) ? StatusIncomplete
        : StatusReady;
    public string MemberForceSummary
    {
        get
        {
            var groups = Out.MemberForceGroups.Count;
            if (groups == 0) return "0 groups";
            var members = Out.MemberForceGroups.Sum(g => g.Members.Count);
            var empty = Out.MemberForceGroups.Count(g => g.Members.Count == 0 || string.IsNullOrWhiteSpace(g.Name));
            return $"{groups} group{(groups == 1 ? "" : "s")} · {members} member{(members == 1 ? "" : "s")}" +
                   (empty > 0 ? $" · {empty} empty" : "");
        }
    }
    public string MemberForceStatus => Out.MemberForceGroups.Count == 0 ? StatusNotConfigured
        : Out.MemberForceGroups.Any(g => g.Members.Count == 0 || string.IsNullOrWhiteSpace(g.Name)) ? StatusIncomplete
        : StatusReady;
    public string FoundationSummary => $"Left {Out.FoundationLeftPoints.Count} / 2 · Right {Out.FoundationRightPoints.Count} / 2";
    public string FoundationStatus => Out.FoundationLeftPoints.Count == 0 && Out.FoundationRightPoints.Count == 0 ? StatusNotConfigured
        : Out.FoundationLeftPoints.Count == 2 && Out.FoundationRightPoints.Count == 2 ? StatusReady
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
    public Command RemoveLoads { get; }
    public Command RemoveCables { get; }
    public Command ConfigureDeflectionOutput { get; }
    public Command ConfigureMemberForceOutput { get; }
    public Command ConfigureFoundationOutput { get; }
    public Command ConfigureCableOutput { get; }
    public Command ValidateOutputSettings { get; }
    public Command Validate { get; }
    public Command SelectAllStudyRuns { get; }
    public Command ClearAllStudyRuns { get; }
    public Command BuildSelectedModels { get; }
    public Command RunStudyFromToolbar { get; }
    public Command CancelStudy { get; }
    public Command OpenStudyFolder { get; }
    public Command OpenIterationFolder { get; }
    public Command OpenDeflectionReport { get; }
    public Command RegenerateDeflectionReport { get; }
    public Command OpenResultsWorkbook { get; }
    public Command OpenCandidateDiagnostic { get; }
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
        StudyExecutionSnapshotFactory? studySnapshotFactory = null,
        IOutputSettingsDialogService? outputDialogs = null,
        OutputGroupPreparationService? outputGroupPreparation = null)
    {
        State = state;
        this.workflow = workflow;
        this.selection = selection;
        this.config = config;
        this.studyExecution = studyExecution;
        this.studySnapshotFactory = studySnapshotFactory ?? new();
        this.outputDialogs = outputDialogs;
        this.outputGroupPreparation = outputGroupPreparation;
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
            ValidateStudy(showMessage: true);
        }, () => !IsBusy);
        SelectAllStudyRuns = new(_ => SetAllStudyRuns(true), () => !IsBusy && StudyIterations.Count > 0);
        ClearAllStudyRuns = new(_ => SetAllStudyRuns(false), () => !IsBusy && StudyIterations.Count > 0);
        BuildSelectedModels = new(_ => RunStudy(), () => CanRunStudy);
        // Run the study from anywhere: jump to the Iteration tab (worker monitor + validation live there) then build.
        RunStudyFromToolbar = new(_ => { SelectedTab = TabIteration; RunStudy(); },
            () => !IsBusy && studyExecution is not null && State.IsSapConnected && SelectedRunCount > 0);
        CancelStudy = new(_ => CancelStudyExecution(), () => CanCancelStudy);
        OpenStudyFolder = new(_ => TryOpenFolder(StudyRoot), () => Directory.Exists(StudyRoot));
        OpenIterationFolder = new(p => { if (p is StudyIteration row) TryOpenFolder(row.IterationDirectory); });
        OpenDeflectionReport = new(_ => TryOpenFile(DeflectionReportPath), () => HasDeflectionReport);
        OpenResultsWorkbook = new(_ => TryOpenFile(ResultsWorkbookPath), () => HasResultsWorkbook);
        OpenCandidateDiagnostic = new(p =>
        {
            if (p is PostProcessRow row && Directory.Exists(StudyRoot))
                TryOpenFile(Path.Combine(StudyRoot, row.DiagnosticRelPath.Replace('/', Path.DirectorySeparatorChar)));
        });
        RegenerateDeflectionReport = new(_ => RegenerateReport(), () => !IsBusy);
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
            Notify(nameof(CableReferenceOptions));
            ScheduleStudyRegeneration();
            Update();
        };
        State.OutputSettings.PropertyChanged += (_, _) => { Dirty(ConfigTabs.Output); InvalidateStudyValidation(); Update(); };
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

    private void EditMemberForceOutput()
    {
        if (outputDialogs is null) { Message = "Output Settings editor is unavailable."; return; }
        if (!outputDialogs.EditMemberForces(State.OutputSettings, OutputBaselineGeometry(), selection)) return;
        config.Session.MarkDirty(ConfigTabs.Output);
        InvalidateStudyValidation();
        Message = $"{State.OutputSettings.MemberForceGroups.Count} member-force output groups configured. Groups will be applied to the parent when Tab 05 runs.";
        Update();
    }

    private void EditFoundationOutput()
    {
        if (outputDialogs is null) { Message = "Output Settings editor is unavailable."; return; }
        if (!outputDialogs.EditFoundations(State.OutputSettings, OutputBaselineGeometry(), selection)) return;
        config.Session.MarkDirty(ConfigTabs.Output);
        InvalidateStudyValidation();
        Message = "Foundation reaction points configured. Groups will be applied to the parent when Tab 05 runs.";
        Update();
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
            nameof(RunStudyLabel), nameof(CanRunStudy), nameof(RunStudyToolTip), nameof(ToolbarStudySummary)
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
                ? $"Study is valid - {SelectedRunCount:N0} iterations are ready to build and analyze."
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
            var result = await studyExecution.BuildAsync(snapshot, progress, studyCancellation.Token, AddPostProcessRow);
            DeflectionReportPath = studyExecution.LastDeflectionReportPath;
            RefreshDeflectionArtifacts();
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
                    : result.Failed > 0 ? StudyBuildState.CompletedWithErrors : StudyBuildState.Completed;
            Message = studyBuildState switch
            {
                StudyBuildState.Completed => $"Build and analysis complete - {result.Completed:N0} verified child models are ready{(result.Reused > 0 ? $" ({result.Reused:N0} reused existing)" : "")}.",
                StudyBuildState.CompletedWithErrors => $"Build and analysis completed with errors - {result.Completed:N0} ready{(result.Reused > 0 ? $" ({result.Reused:N0} reused)" : "")}, {result.Failed:N0} failed.",
                StudyBuildState.Cancelled => $"Build and analysis cancelled - {result.Completed:N0} ready, {result.Cancelled:N0} cancelled.",
                _ => $"Build and analysis failed - all SAP2000 workers faulted; {result.Completed:N0} models are ready and {result.Failed:N0} failed."
            };
        }
        catch (OperationCanceledException)
        {
            studyBuildState = StudyBuildState.Cancelled;
            MarkUnfinishedIterations(StudyIterationStatus.Cancelled, "Cancelled before worker assignment.");
            Message = "Study build and analysis cancelled at a safe SAP2000 stage boundary.";
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

    private async void RegenerateReport()
    {
        if (IsBusy) return;
        if (!HasResultsWorkbook)
        {
            // Nothing to regenerate from: clear the stale report + status and white out the preview.
            PostProcessRows.Clear();
            DeflectionReportPath = null;
            Message = $"No {COT_VCF.Application.Results.ConsolidatedResultWriter.WorkbookFileName} found in {StudyRoot}. Run the study on Tab 05 to produce deflection results.";
            RefreshDeflectionArtifacts();
            return;
        }
        IsBusy = true;
        try
        {
            var path = await Task.Run(() => new COT_VCF.Application.Results.DeflectionReportGenerator().RegenerateFromWorkbook(StudyRoot));
            DeflectionReportPath = path;
            Message = $"Regenerated {Path.GetFileName(path)} from {COT_VCF.Application.Results.ConsolidatedResultWriter.WorkbookFileName}.";
        }
        catch (Exception ex) { Message = ex.Message; Warn("Regenerate Deflection Report", ex.Message); }
        finally { IsBusy = false; }
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
            var report = Directory.Exists(StudyRoot)
                ? Path.Combine(StudyRoot, COT_VCF.Application.Results.DeflectionReportGenerator.ReportFileName) : null;
            DeflectionReportPath = report is not null && File.Exists(report) ? report : null;
        }
        catch { DeflectionReportPath = null; }
        Notify(nameof(HasResultsWorkbook));
        Notify(nameof(ResultsWorkbookPath));
        Notify(nameof(PostProcessStatus));
        CommandManager.InvalidateRequerySuggested();
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

        // Pre-fill each new row from the target force already assigned in the connected SAP2000 model.
        int prefilled = 0;
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
            finally { IsBusy = false; }
        }

        Message = prefilled > 0
            ? $"Added {added.Count} cable{(added.Count == 1 ? "" : "s")} · {prefilled} pre-filled from SAP2000. Remove with − Remove."
            : $"Added {added.Count} cable{(added.Count == 1 ? "" : "s")}. Remove with − Remove.";
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
            if (check.Output.Present) present.Add("Output Settings");
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
            nameof(TrussStatus), nameof(CableStatus), nameof(OutputStatus), nameof(CableCount), nameof(VariableCableCount), nameof(PretensionCandidateCount),
            nameof(CablePreviewSummary), nameof(DeflectionOutputSummary), nameof(MemberForceOutputSummary),
            nameof(FoundationOutputSummary), nameof(CableOutputSummary),
            nameof(MonitoredPointSummary), nameof(MonitoredPointCaseSummary), nameof(MonitoredPointStatus), nameof(MemberForceSummary), nameof(MemberForceStatus),
            nameof(FoundationSummary), nameof(FoundationStatus), nameof(CableForceSummary), nameof(CableForceStatus),
            nameof(ConfiguredOutputCount), nameof(OutputUnitsSummary),
            nameof(CanUseConfig), nameof(HasProject), nameof(ProjectName),
            nameof(LoadingCandidateCount), nameof(HeightCandidateCount),
            nameof(StudyModeLabel), nameof(StudyNeedsFixedHeight), nameof(StudyNeedsFixedPretension),
            nameof(StudyRoot), nameof(RequestedWorkerCount), nameof(MaxWorkerCount),
            nameof(HasDeflectionReport), nameof(HasNoDeflectionReport), nameof(DeflectionReportUri), nameof(DeflectionReportName),
            nameof(HasResultsWorkbook), nameof(ResultsWorkbookPath), nameof(PostProcessStatus)
        }) Notify(name);
        NotifySharedViewer();
        NotifyStudyPresentation();
    }
}
