using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Mcotis.Desktop.Application;
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

    /// <summary>The axes of the last run that vary (one results column each).</summary>
    public IReadOnlyList<VcfOptimizationAxis> VcfOptimizationColumns { get; private set; } = [];

    private string vcfOptimizationProgress = "Not run yet.";
    public string VcfOptimizationProgress { get => vcfOptimizationProgress; private set => Set(ref vcfOptimizationProgress, value); }

    private string vcfOptimizationBest = "";
    public string VcfOptimizationBest { get => vcfOptimizationBest; private set => Set(ref vcfOptimizationBest, value); }

    private double vcfOptimizationPercent;
    public double VcfOptimizationPercent { get => vcfOptimizationPercent; private set => Set(ref vcfOptimizationPercent, value); }

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
        VcfOptimizationRows.Clear();
        VcfOptimizationBest = "";
        VcfOptimizationPercent = 0;
        if (settings.CombinationCount > VcfOptimizationRunner.MaxCases)
        {
            VcfOptimizationProgress = $"{settings.CombinationCount:N0} combinations — the limit for one run is {VcfOptimizationRunner.MaxCases:N0}. Narrow the ranges or widen the steps.";
            Warn("Optimization VCF", VcfOptimizationProgress);
            return;
        }
        if (State.Model is null || !(State.IsSapConnected || IsOfflineModel))
        {
            VcfOptimizationProgress = "Open a model before running the optimization: File → Default Model or Load MCOTiS Config, or Connect SAP.";
            Warn("Optimization VCF", VcfOptimizationProgress);
            return;
        }

        IsBusy = true;
        IsVcfOptimizationRunning = true;
        vcfOptimizationCancellation = new CancellationTokenSource();
        var token = vcfOptimizationCancellation.Token;
        var analysed = 0;
        try
        {
            // The cases vary the model Mcotis holds — the same one Run FEM Analysis solves; SAP2000 is not read.
            VcfOptimizationProgress = "Preparing the cases from the model…";
            var properties = HeldFemProperties();
            var inputs = new VcfOptimizationInputs(
                VcfGeometry.Analyze(State.Geometry, State.Truss), properties, SectionSwapper(), femSapLoading,
                FemPatternRows.ToArray(), FemCaseRows.ToArray(), FemCombinationRows.ToArray(), Tab01Patterns, PretensionPatterns,
                State.AppliedPoints.Select(point => point.PointName).ToArray(), State.Cables.ToArray(),
                FemTargetFitPercent, FemTargetFitIterations, FemFrameMeshSegments, FemCableSag, CostPerMetre());
            var runner = new VcfOptimizationRunner(inputs, settings);
            var columns = runner.Axes.Select((axis, index) => (axis, index)).Where(entry => entry.axis.Varies).ToArray();
            VcfOptimizationColumns = columns.Select(entry => entry.axis).ToArray();
            Notify(nameof(VcfOptimizationColumns));

            var total = runner.Count;
            VcfOptimizationRow? best = null;
            var passing = 0;
            VcfOptimizationRow? cheapest = null;
            var criterion = $"δ in {settings.DeflectionResult ?? "every solved result"} ≤ span / {settings.SpanRatio:0.###} and uplift ≤ {settings.UpliftLimitMm:0.###} mm";
            var progress = new Progress<VcfOptimizationResult>(result =>
            {
                analysed++;
                var values = columns.Select(entry => VcfOptimizationRunner.Text(result.Case.Values[entry.index])).ToArray();
                var utilisation = result.MaxDeflectionMm is { } d && result.LimitMm is { } l && l > 0 ? d / l : (double?)null;
                var row = new VcfOptimizationRow(result.Case.Index + 1, values, result.MaxDeflectionMm,
                    result.MaxDeflectionMm is { } mm ? mm.ToString("0.0", CultureInfo.CurrentCulture) : "—",
                    result.GoverningResult ?? "", result.Point ?? "",
                    result.Failure ?? (result.PretensionFitted ? "Analysed" : $"Analysed · pretension not fitted within {inputs.TargetFitPercent:0.###}%"),
                    result.SpanM is { } span ? span.ToString("0.###", CultureInfo.CurrentCulture) : "",
                    result.LimitMm is { } limit ? limit.ToString("0.0", CultureInfo.CurrentCulture) : "",
                    utilisation, utilisation is { } u ? u.ToString("0.00", CultureInfo.CurrentCulture) : "",
                    result.Passes switch { true => "OK", false => "NG", null => "—" },
                    result.CostSgd, result.CostSgd is { } sgd ? sgd.ToString("N0", CultureInfo.CurrentCulture) : "—",
                    result.MaxUpliftMm, result.MaxUpliftMm is { } up ? up.ToString("0.0", CultureInfo.CurrentCulture) : "—", result.UpliftPoint ?? "");
                if (result.UpliftPasses == false)
                    row = row with { Status = row.Status + $" · uplift {row.Uplift} mm at {row.UpliftPoint} is above the {settings.UpliftLimitMm:0.###} mm limit" };
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
        }
        catch (OperationCanceledException)
        {
            VcfOptimizationProgress = $"Cancelled after {VcfOptimizationRows.Count:N0} cases.";
            Message = VcfOptimizationProgress;
        }
        catch (Exception ex)
        {
            VcfOptimizationProgress = $"Optimization failed: {ex.Message}";
            Message = ex.Message;
            Warn("Optimization VCF", ex.Message);
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
}
