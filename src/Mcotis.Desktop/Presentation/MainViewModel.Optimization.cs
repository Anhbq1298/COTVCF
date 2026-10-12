using System.Globalization;
using System.IO;
using System.Windows.Input;
using COT_VCF.Application.Results;

namespace COT_VCF.Presentation;

/// <summary>One row of the Tab 06A candidate table.</summary>
public sealed record OptimizationRow(
    string IterationKey,
    string LoadId,
    string Height,
    string PretensionId,
    double? PeakDownMm,
    double? UtilisationPercent,
    string GoverningCable,
    QualificationStatus Kind,
    string Status,
    string Reason);

// Tab 06A — Optimization: which candidates of the stored study qualify under the acceptance criteria, and the
// report set for those. Works on the raw data only; never SAP2000, never part of a run.
public sealed partial class MainViewModel
{
    private const int TabOptimization = 6;
    private const string FilterAll = "All", FilterQualified = "Qualified", FilterNotQualified = "Not qualified", FilterNotAssessed = "Not assessed";
    public IReadOnlyList<string> OptimizationFilters { get; } = [FilterAll, FilterQualified, FilterNotQualified, FilterNotAssessed];

    private StudyMetricsIndex? optimizationIndex;
    private string? optimizationIndexRoot;
    private StudyQualification? optimization;
    private IReadOnlyDictionary<string, double> optimizationCapacities = new Dictionary<string, double>();
    private IReadOnlyList<OptimizationRow> optimizationRows = [];
    private bool optimizationLoading;
    private string? optimizationError;
    private string optimizationFilter = FilterAll;

    public bool OptimizationLoading => optimizationLoading;
    public IReadOnlyList<OptimizationRow> OptimizationRows => optimizationRows;

    public string OptimizationFilter
    {
        get => optimizationFilter;
        set { if (Set(ref optimizationFilter, value ?? FilterAll)) { BuildOptimizationRows(); NotifyOptimization(); } }
    }

    /// <summary>Second line of the tab header.</summary>
    public string OptimizationTabStatus =>
        !HasRawData ? "Needs raw data"
        : optimizationLoading ? "Reading raw data…"
        : OptimizationProblem.Length > 0 ? "⚠ Check criteria"
        : optimization is { } q ? $"Qualified {q.Summary.Qualified:N0} / {q.Summary.Total:N0}"
        : "Qualify candidates";

    public string OptimizationRawSummary => optimizationIndex is { } index
        ? $"{index.Candidates.Count:N0} candidates · raw data of {index.RawGeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {Path.GetFileName(RawJsonPath)}"
        : optimizationLoading ? "Reading the raw data once to derive each candidate's deflection and cable forces…"
        : RawDataSummary;

    /// <summary>What stops the criteria from being applied; empty when they can be.</summary>
    public string OptimizationProblem =>
        QualificationCriteria.From(State.OutputSettings).Problem() ?? optimizationError ?? "";
    public bool HasOptimizationProblem => OptimizationProblem.Length > 0;

    public string OptimizationHeadline => optimization is { } q
        ? $"Qualified {q.Summary.Qualified:N0} of {q.Summary.Total:N0}" +
          (q.Summary.Total > 0 ? $"  ·  {(100.0 * q.Summary.Qualified / q.Summary.Total).ToString("0.#", CultureInfo.InvariantCulture)} %" : "")
        : optimizationLoading ? "Reading raw data…" : "—";

    public string OptimizationBreakdown => optimization is { } q
        ? $"Not qualified {q.Summary.NotQualified:N0}  ({q.Summary.Breakdown})"
        : "";

    public string OptimizationCriteriaNote
    {
        get
        {
            var criteria = QualificationCriteria.From(State.OutputSettings);
            if (!criteria.AnyActive)
                return criteria.DeflectionEnabled
                    ? "No criterion is active — enter a deflection Min and/or Max, or switch the cable criterion on. Every assessable candidate qualifies."
                    : "No criterion is active. Every assessable candidate qualifies.";
            return criteria.DeflectionEnabled && !criteria.DeflectionActive
                ? $"Active: {criteria.CableLabel}. Enter a deflection Min and/or Max to apply the deflection criterion."
                : $"Active: {criteria.Label}";
        }
    }

    public string DeflectionStudyRange => optimization?.Summary is { MinPeakDownMm: { } lo, MaxPeakDownMm: { } hi }
        ? $"δ in this study: {lo.ToString("0.#", CultureInfo.InvariantCulture)}–{hi.ToString("0.#", CultureInfo.InvariantCulture)} mm"
        : "δ in this study: unavailable";

    public string CableStudyRange
    {
        get
        {
            if (optimization?.Summary is not { } s) return "governing utilisation in this study: unavailable";
            var range = s is { MinUtilisation: { } lo, MaxUtilisation: { } hi }
                ? $"governing utilisation in this study: {lo.ToString("0.00", CultureInfo.InvariantCulture)}–{hi.ToString("0.00", CultureInfo.InvariantCulture)}"
                : "governing utilisation in this study: unavailable";
            return s.CablesWithoutCapacity.Count == 0
                ? $"{range} · {s.CablesWithCapacity.Count} cable(s) with a design capacity"
                : $"{range} · ⚠ no design capacity for {string.Join(", ", s.CablesWithoutCapacity.Take(4))}{(s.CablesWithoutCapacity.Count > 4 ? " …" : "")} — candidates with these cables cannot be assessed";
        }
    }

    public string OptimizationTableSummary => optimization is null ? ""
        : $"{optimizationRows.Count:N0} shown of {optimization.Summary.Total:N0}";

    // ---------- the optimized report set on disk ----------

    private string OptimizedFolder => StudyOptimizationProcessor.FolderOf(StudyRoot);
    private string? OptimizedFile(string fileName)
    {
        try { var path = Path.Combine(OptimizedFolder, fileName); return File.Exists(path) ? path : null; }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    public bool OptimizedReportsStale { get; private set; }

    public string OptimizedReportsStatus
    {
        get
        {
            OptimizedReportsStale = false;
            if (!HasRawData || StudyOptimizationProcessor.ReadRecord(StudyRoot) is not { } record)
                return "No optimized reports generated yet.";
            var when = record.GeneratedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var set = $"{record.QualifiedIterationKeys.Count:N0} of {record.TotalCandidates:N0} candidates · generated {when}";
            if (optimizationIndex is null || optimization is null) return $"Optimized reports: {set}.";
            if (StudyOptimizationProcessor.IsCurrent(record, optimizationIndex, optimization.Criteria, optimizationCapacities))
                return $"Optimized reports are up to date · {set}.";
            OptimizedReportsStale = true;
            return $"Optimized reports are out of date — the criteria, capacities or raw data changed since they were generated ({set}). Regenerate.";
        }
    }

    private Command? generateOptimizedReports, openOptimizationSummary, openOptimizedDeflection, openOptimizedTopChord,
        openOptimizedBottomChord, openOptimizedMegaColumn, openOptimizedMemberForces, openOptimizedSupportReactions,
        openOptimizedCableForces, openOptimizedFolder, printAllOptimizedReports;

    public Command GenerateOptimizedReports => generateOptimizedReports ??= new(_ => GenerateOptimized(),
        () => !IsBusy && HasRawData && optimization is not null && !HasOptimizationProblem);
    private Command OpenOptimized(ref Command? field, string fileName) =>
        field ??= new(_ => TryOpenFile(OptimizedFile(fileName)), () => OptimizedFile(fileName) is not null);
    public Command OpenOptimizationSummary => OpenOptimized(ref openOptimizationSummary, OptimizationSummaryReport.FileName);
    public Command OpenOptimizedDeflection => OpenOptimized(ref openOptimizedDeflection, DeflectionReportGenerator.ReportFileName);
    public Command OpenOptimizedTopChord => OpenOptimized(ref openOptimizedTopChord, MemberForceReportGenerator.TopChordFileName);
    public Command OpenOptimizedBottomChord => OpenOptimized(ref openOptimizedBottomChord, MemberForceReportGenerator.BottomChordFileName);
    public Command OpenOptimizedMegaColumn => OpenOptimized(ref openOptimizedMegaColumn, MemberForceReportGenerator.MegaColumnFileName);
    public Command OpenOptimizedMemberForces => OpenOptimized(ref openOptimizedMemberForces, MemberForceReportGenerator.ReportFileName);
    public Command OpenOptimizedSupportReactions => OpenOptimized(ref openOptimizedSupportReactions, SupportReactionReportGenerator.ReportFileName);
    public Command OpenOptimizedCableForces => OpenOptimized(ref openOptimizedCableForces, CableForceReportGenerator.ReportFileName);
    /// <summary>The optimized set as one PDF — the summary first, then the reports of the qualified candidates.</summary>
    public Command PrintAllOptimizedReports => printAllOptimizedReports ??= new(_ => PrintAll(OptimizedFolder, "Optimized Reports"),
        () => !IsBusy && OptimizedFile(OptimizationSummaryReport.FileName) is not null);
    public Command OpenOptimizedFolder => openOptimizedFolder ??= new(_ => TryOpenFolder(OptimizedFolder), () => Directory.Exists(OptimizedFolder));

    // ---------- metrics once, criteria as often as they change ----------

    /// <summary>Brings the tab up to date: derives the candidate metrics when the raw data is new or has changed,
    /// then applies the current criteria. Called when the tab is shown and when the raw data changes.</summary>
    private async void RefreshOptimization()
    {
        if (!HasRawData)
        {
            optimizationIndex = null; optimization = null; optimizationRows = []; optimizationError = null;
            NotifyOptimization();
            return;
        }
        var root = StudyRoot;
        if (optimizationLoading) return;
        if (optimizationIndex is null || !SamePath(optimizationIndexRoot, root) || RawDataChangedSinceMetrics())
        {
            optimizationLoading = true; optimizationError = null;
            NotifyOptimization();
            try
            {
                var index = await Task.Run(() => new StudyMetricsStore().Load(root));
                optimizationIndex = index; optimizationIndexRoot = root;
            }
            catch (Exception ex)
            {
                optimizationIndex = null; optimization = null; optimizationRows = [];
                optimizationError = $"The raw data could not be read: {ex.Message}";
            }
            finally { optimizationLoading = false; }
        }
        RequalifyOptimization();
    }

    private bool RawDataChangedSinceMetrics()
    {
        try
        {
            var raw = new FileInfo(RawJsonPath!);
            return optimizationIndex is not { } index || raw.Length != index.RawLength || raw.LastWriteTimeUtc != index.RawLastWriteUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return true; }
    }

    /// <summary>Applies the current criteria and Tab 03 capacities to the kept metrics — no file is read.</summary>
    private void RequalifyOptimization()
    {
        if (optimizationIndex is not { } index)
        {
            optimization = null; optimizationRows = [];
            NotifyOptimization();
            return;
        }
        var criteria = QualificationCriteria.From(State.OutputSettings);
        if (criteria.Problem() is null)
        {
            var configured = State.Cables.Select(cable => cable.CableName).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
            optimizationCapacities = CableCapacityResolver.Resolve(CurrentCableCapacities(), configured, index.StoredCableCapacities).Capacities;
            optimization = StudyQualifier.Evaluate(index.Candidates, criteria, optimizationCapacities);
        }
        else optimization = null;          // the problem is shown; nothing is qualified against unusable limits
        BuildOptimizationRows();
        NotifyOptimization();
    }

    private void BuildOptimizationRows()
    {
        if (optimization is not { } q) { optimizationRows = []; return; }
        bool Shown(CandidateQualification c) => optimizationFilter switch
        {
            FilterQualified => c.Status == QualificationStatus.Qualified,
            FilterNotQualified => c.Status == QualificationStatus.NotQualified,
            FilterNotAssessed => c.Status == QualificationStatus.NotAssessed,
            _ => true,
        };
        optimizationRows = q.Candidates.Where(Shown).Select(c => new OptimizationRow(
            c.Metrics.IterationKey, c.Metrics.LoadId, CableForceMath.HeightLabel(c.Metrics.TrussHeightM), c.Metrics.PretensionId,
            c.Metrics.PeakDownMm, c.GoverningUtilisation * 100, c.GoverningCable ?? "—", c.Status, c.StatusText, c.Reason)).ToArray();
    }

    private async void GenerateOptimized()
    {
        if (IsBusy || !HasRawData) return;
        IsBusy = true;
        Message = "Generating the optimized reports from the raw data…";
        try
        {
            var root = StudyRoot;
            var criteria = QualificationCriteria.From(State.OutputSettings);
            var capacities = CurrentCableCapacities();
            var configured = State.Cables.Select(cable => cable.CableName).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
            var print = PrintSettings.Clone();
            var artifacts = await Task.Run(() => new StudyOptimizationProcessor().Generate(root, criteria, capacities, configured, print));
            var s = artifacts.Qualification.Summary;
            Message = s.Qualified > 0
                ? $"Optimized set written to {StudyOptimizationProcessor.FolderName}: {s.Qualified:N0} of {s.Total:N0} candidates qualify. No SAP2000 run was performed."
                : $"No candidate qualifies under these criteria — only {OptimizationSummaryReport.FileName} was written. No SAP2000 run was performed.";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            Warn("Generate Optimized Reports", ex.Message);
        }
        finally
        {
            IsBusy = false;
            RefreshOptimization();
        }
    }

    /// <summary>The raw data appeared, changed or went away. (A rediscovery of the study's files clears the
    /// path and sets it again, so the tab is left where it is and simply shows its empty state in between.)</summary>
    private void OnRawDataChangedForOptimization()
    {
        optimizationIndex = null; optimization = null; optimizationRows = []; optimizationError = null;
        if (SelectedTab == TabOptimization && HasRawData) RefreshOptimization();
        NotifyOptimization();
    }

    private void NotifyOptimization()
    {
        foreach (var name in new[]
        {
            nameof(OptimizationLoading), nameof(OptimizationRows), nameof(OptimizationTabStatus), nameof(OptimizationRawSummary),
            nameof(OptimizationProblem), nameof(HasOptimizationProblem), nameof(OptimizationHeadline), nameof(OptimizationBreakdown),
            nameof(OptimizationCriteriaNote), nameof(DeflectionStudyRange), nameof(CableStudyRange), nameof(OptimizationTableSummary),
            nameof(OptimizedReportsStatus), nameof(OptimizedReportsStale)
        }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }
}
