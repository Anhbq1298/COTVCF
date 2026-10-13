using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Etabs;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Presentation;

/// <summary>One analysed case in the Optimization VCF results: the values of the varying inputs, then the outcome — the
/// deflection, the span and limit span / ratio, the utilisation δ / limit, the uplift of the span (with the chord point
/// that rises most) and the check of both.</summary>
public sealed record VcfOptimizationRow(long Case, IReadOnlyList<string> Values, double? MaxDeflectionMm, string Deflection,
    string Governing, string Point, string Status, string Span = "", string Limit = "", double? Utilisation = null, string Ratio = "",
    string Check = "", double? CostSgd = null, string Cost = "", double? MaxUpliftMm = null, string Uplift = "", string UpliftPoint = "");

// ---- Run → Optimization VCF: the search space of the VCF system and its run ----
// The settings are kept in memory. Run walks every combination with the internal FEM (VcfOptimizationRunner) and lists the
// largest downward deflection of each case; the working model is never changed.
public sealed partial class MainViewModel
{
    /// <summary>The ranges confirmed in Run → Optimization VCF, or null before the first OK.</summary>
    public VcfOptimizationSettings? VcfOptimizationSettings { get; private set; }

    /// <summary>The load cases and combinations a deflection can be read from (those set to run on Define → Load Cases /
    /// Load Combinations).</summary>
    internal (IReadOnlyList<string> Cases, IReadOnlyList<string> Combinations) VcfDeflectionResults() =>
        (FemCaseRows.Where(row => row.Run && row.Supported).Select(row => row.Name).ToArray(),
         FemCombinationRows.Where(row => row.Run && row.Supported).Select(row => row.Name).ToArray());

    internal void SetVcfOptimization(VcfOptimizationSettings settings)
    {
        VcfOptimizationSettings = settings;
        Notify(nameof(VcfOptimizationSettings));
        Message = $"Optimization VCF: {settings.CombinationCount:N0} combinations kept " +
            $"({settings.GeometryCount:N0} geometry × {settings.ForceCount:N0} force × {settings.SectionCount:N0} section variants).";
    }

    public ObservableCollection<VcfOptimizationRow> VcfOptimizationRows { get; } = [];

    private StudyReport optimizationReport = StudyReport.Empty;
    /// <summary>The Chart page of the last run (<see cref="OptimizationStudy"/>): figures and summary, built when the run ends.</summary>
    public StudyReport OptimizationReport { get => optimizationReport; private set => Set(ref optimizationReport, value); }

    /// <summary>The axes of the last run that vary (one results column each).</summary>
    public IReadOnlyList<VcfOptimizationAxis> VcfOptimizationColumns { get; private set; } = [];

    private string vcfOptimizationProgress = "Not run yet.";
    public string VcfOptimizationProgress { get => vcfOptimizationProgress; private set => Set(ref vcfOptimizationProgress, value); }

    private string vcfOptimizationBest = "";
    public string VcfOptimizationBest { get => vcfOptimizationBest; private set => Set(ref vcfOptimizationBest, value); }

    private double vcfOptimizationPercent;
    public double VcfOptimizationPercent { get => vcfOptimizationPercent; private set => Set(ref vcfOptimizationPercent, value); }

    // ---- Optimization menu: each optimization varies its own system, so only the one of the open model is offered ----

    /// <summary>The open model is a VCF system — the one Optimization VCF varies. Another model (the tied arch system,
    /// a building, an imported model) has no VCF parameters to vary, so the command is greyed.</summary>
    public bool CanOptimizeVcf => HasModelGeometry && VcfGeometry.CanAnalyze(State.Geometry, State.Truss);

    /// <summary>The open model is the tied arch system (File → Load TAS… or Edit → Tied Arch System…) — the one
    /// Optimization TAS varies.</summary>
    public bool CanOptimizeTas => TiedArchParameters is not null;

    private bool isVcfOptimizationRunning;
    public bool IsVcfOptimizationRunning
    {
        get => isVcfOptimizationRunning;
        private set { if (Set(ref isVcfOptimizationRunning, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    private static string Best(VcfOptimizationRow row, VcfOptimizationRow? cheapest, int passing, int analysed, string criterion, bool final) =>
        $"{passing:N0} of {analysed:N0} cases pass {criterion}{(final ? "" : " (so far)")}" +
        (cheapest is null ? "" : $" · cheapest passing: case {cheapest.Case} · SGD {cheapest.Cost} (δ {cheapest.Deflection} mm, limit {cheapest.Limit} mm)") +
        $" · least deflection: case {row.Case} · {row.Deflection} mm at {row.Point}, limit {row.Limit} mm ({row.Check})";

    /// <summary>A member's cost per metre [SGD/m] for the optimization: a composite section (CFT, steel encased) from its
    /// concrete volume and steel / reinforcement mass per metre; any other member from its gross area × its material's unit
    /// cost per m³ (= SGD/t × t/m³). Works on a snapshot, so it may be used off the UI thread.</summary>
    internal Func<FemSectionProperties, (double? SgdPerM, string? Missing)> CostPerMetre()
    {
        EnsureMaterials();
        EnsureSections();
        var materials = Materials.ToArray();
        var composites = FrameSections.Where(section => section.IsComposite)
            .ToDictionary(section => section.Name, section => AnalyseComposite(section)?.Cost, StringComparer.Ordinal);
        return property =>
        {
            if (property.SectionName is { } name && composites.TryGetValue(name, out var composite) && composite is not null)
                return composite.CostPerM is { } perMetre ? (perMetre, null) : (null, $"{name} ({string.Join(", ", composite.Missing)})");
            var material = materials.FirstOrDefault(item => item.Name == property.MaterialName);
            if (material?.UnitCostPerCubicMetre is not { } rate) return (null, property.MaterialName ?? property.SectionName ?? property.MemberName);
            return ((property.GrossAreaM2 > 0 ? property.GrossAreaM2 : property.AreaM2) * rate, null);
        };
    }

    private CancellationTokenSource? vcfOptimizationCancellation;
    internal void CancelVcfOptimization() => vcfOptimizationCancellation?.Cancel();

    /// <summary>Runs every combination of <paramref name="settings"/> and fills <see cref="VcfOptimizationRows"/>.</summary>
    internal async Task RunVcfOptimizationAsync(VcfOptimizationSettings settings)
    {
        if (IsBusy || IsVcfOptimizationRunning) return;
        SetVcfOptimization(settings);
        var fitPercent = FemTargetFitPercent;
        await RunOptimizationAsync("Optimization VCF", settings.CombinationCount, settings.SpanRatio, settings.UpliftLimitMm,
            $"δ in {settings.DeflectionResult ?? "every solved result"} ≤ span / {settings.SpanRatio:0.###} and uplift ≤ {settings.UpliftLimitMm:0.###} mm",
            VcfResultsNote, $"pretension not fitted within {fitPercent:0.###}%", () =>
            {
                // The cases vary the model Mcotis holds — the same one Run FEM Analysis solves; SAP2000 is not read.
                var inputs = new VcfOptimizationInputs(
                    VcfGeometry.Analyze(State.Geometry, State.Truss), HeldFemProperties(), SectionSwapper(), femSapLoading,
                    FemPatternRows.ToArray(), FemCaseRows.ToArray(), FemCombinationRows.ToArray(), Tab01Patterns, PretensionPatterns,
                    State.AppliedPoints.Select(point => point.PointName).ToArray(), State.Cables.ToArray(),
                    FemTargetFitPercent, FemTargetFitIterations, FemFrameMeshSegments, FemCableSag, CostPerMetre())
                    { ActiveDofs = FemActiveDofs, CableDofs = FemCableDofs };
                return new VcfOptimizationRunner(inputs, settings);
            });
    }

    private const string VcfResultsNote = "Max defl. and Uplift = largest downward and largest upward displacement of a bottom chord point (BTrPt…) in the result chosen under Deflection Check (Result column); Limit = span between the mega columns / ratio; Check OK when δ ≤ limit and uplift ≤ its limit. Total cost = Σ member cost per metre × length (gross area × material unit cost, as on Define → Materials; composite sections from their concrete, steel and reinforcement). Run → Analysis Options and Define → Load Cases apply. Each case is built in memory; the working model and SAP2000 are not changed.";

    // ---- what the results window shows: the run that filled it ----
    private string optimizationTitle = "Optimization VCF";
    /// <summary>The study the results belong to: "Optimization VCF" or "Optimization TAS".</summary>
    public string OptimizationTitle { get => optimizationTitle; private set => Set(ref optimizationTitle, value); }
    private string optimizationResultsNote = VcfResultsNote;
    public string OptimizationResultsNote { get => optimizationResultsNote; private set => Set(ref optimizationResultsNote, value); }
    /// <summary>The span ratio and uplift limit of the run shown (the results' column headers).</summary>
    public double OptimizationSpanRatio { get; private set; } = VcfOptimization.DefaultSpanRatio;
    public double OptimizationUpliftLimitMm { get; private set; } = VcfOptimization.DefaultUpliftLimitMm;

    /// <summary>The run loop of the Optimization menu: prepares the runner on the UI thread, analyses every case off it and
    /// lists each as it completes, with the passing count, the cheapest passing case and the least deflection. Cancel
    /// Optimization stops it. Shared by Optimization VCF and Optimization TAS.</summary>
    private async Task RunOptimizationAsync(string title, long combinations, double spanRatio, double upliftLimitMm, string criterion,
        string resultsNote, string unfittedNote, Func<IOptimizationRunner> prepare)
    {
        if (IsBusy || IsVcfOptimizationRunning) return;
        VcfOptimizationRows.Clear();
        OptimizationReport = StudyReport.Empty;
        var collected = new List<VcfOptimizationResult>();
        IReadOnlyList<VcfOptimizationAxis> runAxes = [];
        void Report()
        {
            var resultName = collected.Select(result => result.GoverningResult).FirstOrDefault(name => !string.IsNullOrEmpty(name)) ?? "every solved result";
            OptimizationReport = OptimizationStudy.Build(runAxes, collected, resultName, criterion,
                (title == "Optimization TAS" ? "L / " : "span / ") + spanRatio.ToString("0.###", CultureInfo.CurrentCulture));
        }
        VcfOptimizationBest = "";
        VcfOptimizationPercent = 0;
        OptimizationTitle = title;
        OptimizationResultsNote = resultsNote;
        OptimizationSpanRatio = spanRatio;
        OptimizationUpliftLimitMm = upliftLimitMm;
        if (combinations > VcfOptimizationRunner.MaxCases)
        {
            VcfOptimizationProgress = $"{combinations:N0} combinations — the limit for one run is {VcfOptimizationRunner.MaxCases:N0}. Narrow the ranges or widen the steps.";
            Warn(title, VcfOptimizationProgress);
            return;
        }
        if (State.Model is null || !(State.IsSapConnected || IsOfflineModel))
        {
            VcfOptimizationProgress = "Open a model before running the optimization: File → Load VCF… or Load TAS…, Load MCOTiS Config, or Connect SAP.";
            Warn(title, VcfOptimizationProgress);
            return;
        }

        IsBusy = true;
        IsVcfOptimizationRunning = true;
        vcfOptimizationCancellation = new CancellationTokenSource();
        var token = vcfOptimizationCancellation.Token;
        var analysed = 0;
        try
        {
            VcfOptimizationProgress = "Preparing the cases from the model…";
            var runner = prepare();
            runAxes = runner.Axes;
            var columns = runner.Axes.Select((axis, index) => (axis, index)).Where(entry => entry.axis.Varies).ToArray();
            VcfOptimizationColumns = columns.Select(entry => entry.axis).ToArray();
            Notify(nameof(VcfOptimizationColumns));

            var total = runner.Count;
            VcfOptimizationRow? best = null;
            var passing = 0;
            VcfOptimizationRow? cheapest = null;
            var progress = new Progress<VcfOptimizationResult>(result =>
            {
                analysed++;
                collected.Add(result);
                var values = columns.Select(entry => VcfOptimizationRunner.Text(result.Case.Values[entry.index])).ToArray();
                var utilisation = result.MaxDeflectionMm is { } d && result.LimitMm is { } l && l > 0 ? d / l : (double?)null;
                var row = new VcfOptimizationRow(result.Case.Index + 1, values, result.MaxDeflectionMm,
                    result.MaxDeflectionMm is { } mm ? mm.ToString("0.0", CultureInfo.CurrentCulture) : "—",
                    result.GoverningResult ?? "", result.Point ?? "",
                    result.Failure ?? (result.PretensionFitted ? "Analysed" : "Analysed · " + unfittedNote),
                    result.SpanM is { } span ? span.ToString("0.###", CultureInfo.CurrentCulture) : "",
                    result.LimitMm is { } limit ? limit.ToString("0.0", CultureInfo.CurrentCulture) : "",
                    utilisation, utilisation is { } u ? u.ToString("0.00", CultureInfo.CurrentCulture) : "",
                    result.Passes switch { true => "OK", false => "NG", null => "—" },
                    result.CostSgd, result.CostSgd is { } sgd ? sgd.ToString("N0", CultureInfo.CurrentCulture) : "—",
                    result.MaxUpliftMm, result.MaxUpliftMm is { } up ? up.ToString("0.0", CultureInfo.CurrentCulture) : "—", result.UpliftPoint ?? "");
                if (result.UpliftPasses == false)
                    row = row with { Status = row.Status + $" · uplift {row.Uplift} mm at {row.UpliftPoint} is above the {upliftLimitMm:0.###} mm limit" };
                if (result.CostNote is { } costNote) row = row with { Status = row.Status + " · " + costNote };
                VcfOptimizationRows.Add(row);
                if (row.MaxDeflectionMm is { } value && (best?.MaxDeflectionMm is not { } least || value < least)) best = row;
                if (row.Check == "OK") passing++;
                if (row.Check == "OK" && row.CostSgd is { } price && (cheapest?.CostSgd is not { } lowest || price < lowest)) cheapest = row;
                VcfOptimizationPercent = 100.0 * analysed / total;
                VcfOptimizationProgress = $"Analysing {analysed:N0} / {total:N0}";
                if (best is not null) VcfOptimizationBest = Best(best, cheapest, passing, analysed, criterion, final: false);
            });
            IProgress<VcfOptimizationResult> report = progress;
            await Task.Run(() =>
            {
                foreach (var item in runner.Cases())
                {
                    token.ThrowIfCancellationRequested();
                    report.Report(runner.Run(item, token));
                }
            }, token);
            // the progress reports are posted at normal priority: let the last ones reach the table first
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            var failed = VcfOptimizationRows.Count(row => row.MaxDeflectionMm is null);
            VcfOptimizationProgress = $"Completed {VcfOptimizationRows.Count:N0} of {total:N0} cases" + (failed > 0 ? $" · {failed:N0} could not be analysed" : "") +
                " · working model and SAP2000 unchanged.";
            Message = VcfOptimizationProgress;
            if (best is not null) VcfOptimizationBest = Best(best, cheapest, passing, analysed, criterion, final: true);
            Report();
        }
        catch (OperationCanceledException)
        {
            VcfOptimizationProgress = $"Cancelled after {VcfOptimizationRows.Count:N0} cases.";
            Message = VcfOptimizationProgress;
            Report();     // the cases analysed so far
        }
        catch (Exception ex)
        {
            VcfOptimizationProgress = $"Optimization failed: {ex.Message}";
            Message = ex.Message;
            Warn(title, ex.Message);
        }
        finally
        {
            vcfOptimizationCancellation?.Dispose();
            vcfOptimizationCancellation = null;
            IsVcfOptimizationRunning = false;
            IsBusy = false;
            Update();
        }
    }

    // ---- Optimization → Optimization TAS: the search space of the tied arch system and its run ----

    /// <summary>The ranges confirmed in Optimization TAS, or null before the first OK.</summary>
    public TasOptimizationSettings? TasOptimizationSettings { get; private set; }

    internal void SetTasOptimization(TasOptimizationSettings settings)
    {
        TasOptimizationSettings = settings;
        Notify(nameof(TasOptimizationSettings));
        Message = $"Optimization TAS: {settings.CombinationCount:N0} combinations kept " +
            $"({settings.GeometryCount:N0} geometry × {settings.ForceCount:N0} load × {settings.SectionCount:N0} section variants).";
    }

    /// <summary>The cable sections Optimization TAS can try for the tie: the model's cable library (the VCF cable section first).</summary>
    internal IReadOnlyList<string> TasCableSectionNames() =>
        CableSections.Select(section => section.Name).Prepend(TiedArchSystem.CableSection).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>The frame sections Optimization TAS can try: the section library of the open model.</summary>
    internal IReadOnlyList<string> TasSectionNames()
    {
        EnsureSections();
        return FrameSections.Select(section => section.Name).ToArray();
    }

    /// <summary>Runs every combination of the tied arch system's <paramref name="settings"/> and fills <see cref="VcfOptimizationRows"/>
    /// (the same results table as Optimization VCF). Needs the tied arch system open (File → Load TAS… or Edit → Tied Arch System).</summary>
    internal async Task RunTasOptimizationAsync(TasOptimizationSettings settings)
    {
        if (IsBusy || IsVcfOptimizationRunning) return;
        SetTasOptimization(settings);
        if (TiedArchParameters is null)
        {
            VcfOptimizationProgress = "Open the tied arch system first: File → Load TAS… or Edit → Tied Arch System…";
            Warn("Optimization TAS", VcfOptimizationProgress);
            return;
        }
        const string note = "Max defl. and Uplift = largest downward and largest upward displacement of a top chord (deck) joint in the result chosen under Deflection Check (Result column); Limit = span L (bays × spacing) / ratio; Check OK when δ ≤ limit and uplift ≤ its limit. Each case is the tied arch system of its H1, H2, bays and spacing — the parabolic arch, the TAS layout — with the deck loads (SW + SDL) and LL × spacing × B × n (half at the end joints) and the sections tried per role. Total cost = Σ member cost per metre × length, as for Optimization VCF. Run → Analysis Options and Define → Load Cases apply. Each case is built in memory; the open model is not changed.";
        await RunOptimizationAsync("Optimization TAS", settings.CombinationCount, settings.SpanRatio, settings.UpliftLimitMm,
            $"δ in {settings.DeflectionResult ?? "every solved result"} ≤ L / {settings.SpanRatio:0.###} and uplift ≤ {settings.UpliftLimitMm:0.###} mm",
            note, $"cable not fitted within {FemTargetFitPercent:0.###}%", () => new TasOptimizationRunner(TasOptimizationInputs(), settings));
    }

    /// <summary>What an Optimization TAS case takes from the open model: the TAS template, each member's properties with a
    /// library section (as Import From ETABS works them out, Section Designer sections with their true self-weight), the
    /// load case rows, the analysis options and the cost per metre.</summary>
    internal TasOptimizationInputs TasOptimizationInputs()
    {
        var template = ReadTasTemplate().Model;
        EnsureSections();
        var library = FrameSections.ToDictionary(section => section.Name, StringComparer.Ordinal);
        var weights = Application.Etabs.EtabsImport.Convert(template).SectionWeights;
        // the tie's strand sections: the model's cable library, with the VCF cable section it is built with
        // (and their materials) — a snapshot, so the run never changes the open model's libraries
        var strands = CableSections.ToDictionary(section => section.Name, StringComparer.Ordinal);
        var strandMaterials = Materials.ToDictionary(material => material.Name, StringComparer.Ordinal);
        if (!strands.ContainsKey(TiedArchSystem.CableSection))
        {
            var (vcfCable, strandMaterial) = VcfCableSection();
            strands[vcfCable.Name] = vcfCable;
            strandMaterials.TryAdd(strandMaterial.Name, strandMaterial);
        }
        FemSectionProperties Properties(FemSectionProperties member, string sectionName)
        {
            if (member.IsCable)
            {
                if (!strands.TryGetValue(sectionName, out var strand))
                    throw new InvalidOperationException($"Cable section '{sectionName}' is not in the cable section library (Define → Section Properties → Cable Sections).");
                var material = strandMaterials.GetValueOrDefault(strand.Material)
                    ?? throw new InvalidOperationException($"Cable section '{sectionName}' has no material {strand.Material} in the library.");
                return member with
                {
                    SectionName = strand.Name, MaterialName = strand.Material, AreaM2 = strand.Area, GrossAreaM2 = strand.Area,
                    ElasticModulusKNPerM2 = material.ElasticModulus, UnitWeightKNPerM3 = material.WeightPerVolume, UnitMassTPerM3 = material.MassPerVolume,
                };
            }
            if (!library.TryGetValue(sectionName, out var section))
                throw new InvalidOperationException($"Section '{sectionName}' is not in the frame section library (Define → Section Properties → Frame Sections).");
            var property = PropertiesFor(member with { SectionName = sectionName }, section);
            if (weights.TryGetValue(sectionName, out var weight) && property.GrossAreaM2 > 0)
                property = property with
                {
                    UnitWeightKNPerM3 = weight.WeightPerLengthKNPerM / property.GrossAreaM2,
                    UnitMassTPerM3 = weight.MassPerLengthTPerM / property.GrossAreaM2,
                };
            return property;
        }
        return new TasOptimizationInputs(template, Properties, FemPatternRows.ToArray(), FemCaseRows.ToArray(), FemCombinationRows.ToArray(),
            PretensionPatterns, FemTargetFitPercent, FemTargetFitIterations, FemFrameMeshSegments, FemCableSag, CostPerMetre())
            { ActiveDofs = FemActiveDofs, CableDofs = FemCableDofs };
    }
}
