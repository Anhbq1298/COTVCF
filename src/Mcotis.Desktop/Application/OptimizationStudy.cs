using System.Globalization;

namespace Mcotis.Desktop.Application;

public enum StudyChartKind { Line, Bars, Scatter }

/// <summary>One curve of a figure. <see cref="Low"/> / <see cref="High"/> (same length as <see cref="Points"/>) give the
/// min … max band around a mean curve; <see cref="Colour"/> is a palette index.</summary>
public sealed record StudySeries(string Name, IReadOnlyList<(double X, double Y)> Points, int Colour,
    IReadOnlyList<double>? Low = null, IReadOnlyList<double>? High = null, bool Markers = true, bool Line = true);

/// <summary>A figure of the study: numbered, titled, with axis titles, its curves, an optional limit line (horizontal at
/// <see cref="LimitY"/> or vertical at <see cref="LimitX"/>), category labels for a categorical x axis (x = 0, 1, …) and
/// the caption read below it.</summary>
public sealed record StudyChart(int Number, string Title, string XTitle, string YTitle, StudyChartKind Kind, IReadOnlyList<StudySeries> Series,
    string Caption, double? LimitY = null, string? LimitLabel = null, IReadOnlyList<string>? Categories = null, double? LimitX = null);

/// <summary>How strongly a response follows a parameter: the swing of its main effect (max − min of the mean curve) as a
/// share of the response's overall mean, the elasticity (% change of the response per % change of the parameter, from a
/// least-squares line through the mean curve; null for a categorical parameter), the direction and the linearity (R²).
/// <see cref="TurningPointX"/> is where a numeric mean curve has an interior minimum (<see cref="TurningIsMinimum"/>) or
/// maximum — the trend reverses there.</summary>
public sealed record StudySensitivity(string Metric, string Parameter, double SwingPercent, double? Elasticity, int Direction, double? RSquared,
    double? TurningPointX = null, bool TurningIsMinimum = false)
{
    /// <summary>A trend that a straight line does not describe (R² below 0.6) — its elasticity says little.</summary>
    public bool NonMonotonic => RSquared is < 0.6 && TurningPointX is not null;
}

/// <summary>The Chart page of an optimization run: the figures, the sensitivities and the summary (paragraphs, then the
/// conclusions as a list).</summary>
public sealed record StudyReport(IReadOnlyList<StudyChart> Charts, IReadOnlyList<StudySensitivity> Sensitivities,
    IReadOnlyList<(string Heading, string Text)> Summary, IReadOnlyList<string> Conclusions)
{
    public static readonly StudyReport Empty = new([], [], [], []);
}

/// <summary>
/// The post-processing of an optimization run, written like the results section of a parametric study: main-effect curves
/// of every response against every varying parameter (the mean over the other parameters, with the min … max band), a
/// sensitivity ranking, the cost / utilisation trade-off, an interaction plot of the two governing parameters, and a summary
/// of the findings. Uses only the analysed cases; a parameter or response that does not vary is left out.
/// </summary>
public static class OptimizationStudy
{
    public const int MaxCharts = 20;

    /// <summary>A main effect whose swing is below this share of the mean [%] is flat: not charted, named in the summary.</summary>
    public const double FlatSwingPercent = 0.5;

    /// <summary>A response of a case: its name, unit and value (null when the case gives none).</summary>
    public sealed record Metric(string Key, string Name, string Unit, Func<VcfOptimizationResult, double?> Value, bool Key3 = false);

    public static IReadOnlyList<Metric> Metrics(string spanTitle) =>
    [
        new("defl", "Max deflection δ", "mm", result => result.MaxDeflectionMm, Key3: true),
        new("util", $"Utilisation δ / ({spanTitle})", "–", result => result.MaxDeflectionMm is { } d && result.LimitMm is > 0 and var l ? d / l : null),
        new("h", "Base horizontal force H (one end)", "kN", result => result.Response?.BaseHorizontalKN, Key3: true),
        new("cost", "Total cost of the system", "SGD", result => result.CostSgd, Key3: true),
        new("costspan", "Cost per metre of span", "SGD/m", result => result.CostSgd is { } c && result.SpanM is > 0 and var s ? c / s : null),
        new("mass", "Mass of the system", "t", result => result.Response?.MassT is > 0 and var m ? m : null),
        new("v", "Base vertical force V (one end)", "kN", result => result.Response?.BaseVerticalKN),
        new("cable", "Max cable tension", "kN", result => result.Response?.MaxCableKN is > 0 and var t ? t : null),
        new("axial", "Max frame axial force", "kN", result => result.Response?.MaxFrameAxialKN),
        new("moment", "Max frame bending moment", "kNm", result => result.Response?.MaxFrameMomentKNm),
        new("uplift", "Max uplift", "mm", result => result.MaxUpliftMm),
    ];

    /// <summary>The x value of a case on an axis: a number, a cable step's force, or the index of a section name.</summary>
    private static double X(VcfOptimizationAxis axis, object value) => value switch
    {
        double number => number,
        VcfCableStep step => step.ForceKN,
        _ => axis.Values.ToList().FindIndex(item => Equals(item, value)),
    };

    private static bool Categorical(VcfOptimizationAxis axis) => axis.Values.Count > 0 && axis.Values[0] is not (double or VcfCableStep);

    /// <summary>Mean, min and max of the metric per value of the axis (values in axis order; values without a case left out).</summary>
    private static IReadOnlyList<(double X, double Mean, double Min, double Max, int Count)> MainEffect(
        VcfOptimizationAxis axis, int axisIndex, IReadOnlyList<VcfOptimizationResult> results, Metric metric)
    {
        var effect = new List<(double, double, double, double, int)>();
        foreach (var value in axis.Values)
        {
            var ys = results.Where(result => Equals(result.Case.Values[axisIndex], value)).Select(metric.Value).OfType<double>().Where(double.IsFinite).ToArray();
            if (ys.Length > 0) effect.Add((X(axis, value), ys.Average(), ys.Min(), ys.Max(), ys.Length));
        }
        return effect;
    }

    private static StudySensitivity Sensitivity(Metric metric, VcfOptimizationAxis axis, IReadOnlyList<(double X, double Mean, double Min, double Max, int Count)> effect, double overallMean)
    {
        var means = effect.Select(entry => entry.Mean).ToArray();
        var swing = overallMean == 0 ? 0 : (means.Max() - means.Min()) / Math.Abs(overallMean) * 100;
        double? elasticity = null, r2 = null;
        var direction = Math.Sign(means[^1] - means[0]);
        if (!Categorical(axis) && effect.Count >= 2)
        {
            double xBar = effect.Average(entry => entry.X), yBar = means.Average();
            double sxx = effect.Sum(entry => (entry.X - xBar) * (entry.X - xBar)), sxy = effect.Sum(entry => (entry.X - xBar) * (entry.Mean - yBar));
            if (sxx > 0)
            {
                var slope = sxy / sxx;
                direction = Math.Sign(slope);
                if (yBar != 0 && xBar != 0) elasticity = slope * xBar / yBar;
                var ssTot = means.Sum(y => (y - yBar) * (y - yBar));
                var ssRes = effect.Sum(entry => Math.Pow(entry.Mean - (yBar + slope * (entry.X - xBar)), 2));
                r2 = ssTot > 0 ? 1 - ssRes / ssTot : 1;
            }
        }
        // an interior minimum or maximum of the mean curve: the trend reverses there
        double? turning = null;
        var turningIsMinimum = false;
        if (!Categorical(axis) && effect.Count >= 3)
        {
            var (low, high) = (Array.IndexOf(means, means.Min()), Array.IndexOf(means, means.Max()));
            if (low > 0 && low < means.Length - 1) { turning = effect[low].X; turningIsMinimum = true; }
            else if (high > 0 && high < means.Length - 1) turning = effect[high].X;
        }
        return new(metric.Name, axis.Label, swing, elasticity, direction, r2, turning, turningIsMinimum);
    }

    /// <summary>The report of a run: <paramref name="axes"/> as the runner gave them (all, in case order) and every result.</summary>
    public static StudyReport Build(IReadOnlyList<VcfOptimizationAxis> axes, IReadOnlyList<VcfOptimizationResult> all,
        string resultName, string criterion, string spanTitle = "span / 250")
    {
        var results = all.Where(result => result.MaxDeflectionMm is not null).ToArray();
        var varying = axes.Select((axis, index) => (Axis: axis, Index: index)).Where(entry => entry.Axis.Varies).ToArray();
        if (results.Length < 2 || varying.Length == 0)
            return StudyReport.Empty with
            {
                Summary = [("Study", results.Length < 2
                    ? "Fewer than two cases were analysed — the charts need at least two."
                    : "No parameter varies in this run, so there is no trend to chart. Give at least one parameter a range.")],
            };

        // responses that vary over the analysed cases
        var metrics = Metrics(spanTitle).Where(metric =>
        {
            var values = results.Select(metric.Value).OfType<double>().Where(double.IsFinite).ToArray();
            return values.Length >= 2 && values.Max() - values.Min() > 1e-9 * Math.Max(1, Math.Abs(values.Max()));
        }).ToArray();

        var sensitivities = new List<StudySensitivity>();
        var effects = new Dictionary<(string Metric, int Axis), IReadOnlyList<(double X, double Mean, double Min, double Max, int Count)>>();
        foreach (var metric in metrics)
        {
            var mean = results.Select(metric.Value).OfType<double>().Average();
            foreach (var (axis, index) in varying)
            {
                var effect = MainEffect(axis, index, results, metric);
                if (effect.Count < 2) continue;
                effects[(metric.Key, index)] = effect;
                sensitivities.Add(Sensitivity(metric, axis, effect, mean));
            }
        }

        // figures: main effects first (responses in order of importance, parameters most sensitive first), then the
        // sensitivity ranking, the trade-off and the interaction plot — at most MaxCharts in all
        var charts = new List<StudyChart>();
        var reserved = 1 + 1 + (varying.Length >= 2 ? 1 : 0);
        var budget = Math.Max(0, MaxCharts - reserved);
        int Next() => charts.Count + 1;
        foreach (var metric in metrics)
        {
            var ranked = varying.Where(entry => effects.ContainsKey((metric.Key, entry.Index)))
                .OrderByDescending(entry => sensitivities.First(s => s.Metric == metric.Name && s.Parameter == entry.Axis.Label).SwingPercent).ToArray();
            foreach (var (axis, index) in ranked)
            {
                if (charts.Count >= budget) break;
                var effect = effects[(metric.Key, index)];
                var s = sensitivities.First(item => item.Metric == metric.Name && item.Parameter == axis.Label);
                if (s.SwingPercent < FlatSwingPercent) continue;     // flat: the summary says it does not depend on the parameter
                var others = varying.Length > 1 ? "mean over the other parameters, band = min … max" : "one case per value";
                charts.Add(new(Next(), $"{metric.Name} vs {axis.Label}", axis.Label, $"{metric.Name} [{metric.Unit}]", StudyChartKind.Line,
                    [new StudySeries(metric.Name, effect.Select(entry => (entry.X, entry.Mean)).ToArray(), 0,
                        varying.Length > 1 ? effect.Select(entry => entry.Min).ToArray() : null,
                        varying.Length > 1 ? effect.Select(entry => entry.Max).ToArray() : null)],
                    $"{metric.Name} against {axis.Label} ({others}). Swing {s.SwingPercent:0.#}% of the mean" +
                    (s.NonMonotonic ? $"; not monotonic — {(s.TurningIsMinimum ? "lowest" : "highest")} at {axis.Label} ≈ {s.TurningPointX:0.###}"
                        : (s.Elasticity is { } e ? $"; elasticity {e:0.00}" : "") + (s.RSquared is { } r ? $"; linear fit R² {r:0.00}" : "")) + ".",
                    metric.Key == "util" ? 1 : null, metric.Key == "util" ? "limit" : null,
                    Categorical(axis) ? axis.Values.Select(VcfOptimizationRunner.Text).ToArray() : null));
            }
        }

        // sensitivity ranking of the key responses
        var keyMetrics = metrics.Where(metric => metric.Key3).ToArray();
        var parameters = varying.Select(entry => entry.Axis.Label).ToArray();
        if (keyMetrics.Length > 0)
            charts.Add(new(Next(), "Sensitivity ranking", "Parameter", "Swing [% of mean]", StudyChartKind.Bars,
                keyMetrics.Select((metric, colour) => new StudySeries(metric.Name,
                    parameters.Select((parameter, i) => ((double)i, sensitivities.FirstOrDefault(s => s.Metric == metric.Name && s.Parameter == parameter)?.SwingPercent ?? 0)).ToArray(),
                    colour)).ToArray(),
                "How much each parameter moves each key response over its range: the swing of the main-effect curve (max − min of the " +
                "mean) as a share of the response's mean. The taller the bar, the more sensitive the response to that parameter.",
                Categories: parameters));

        // trade-off: cost against utilisation, passing and failing cases
        var cost = metrics.FirstOrDefault(metric => metric.Key == "cost") ?? Metrics(spanTitle).First(metric => metric.Key == "cost");
        var util = Metrics(spanTitle).First(metric => metric.Key == "util");
        var tradeoff = results.Where(result => cost.Value(result) is not null && util.Value(result) is not null).ToArray();
        if (tradeoff.Length >= 2)
            charts.Add(new(Next(), "Cost against utilisation", util.Name, $"{cost.Name} [{cost.Unit}]", StudyChartKind.Scatter,
                [new StudySeries("Passes", tradeoff.Where(result => result.Passes == true).Select(result => (util.Value(result)!.Value, cost.Value(result)!.Value)).ToArray(), 2, Line: false),
                 new StudySeries("Fails", tradeoff.Where(result => result.Passes != true).Select(result => (util.Value(result)!.Value, cost.Value(result)!.Value)).ToArray(), 5, Line: false)],
                "Every analysed case: its total cost against δ / limit. The lower-left edge of the cloud left of 1.0 is the economical frontier — " +
                "the cheapest systems that still satisfy the deflection limit.", LimitLabel: "δ = limit", LimitX: 1));

        // interaction of the two parameters that move the deflection most
        var defl = metrics.FirstOrDefault(metric => metric.Key == "defl");
        if (defl is not null && varying.Length >= 2)
        {
            var top = varying.OrderByDescending(entry => sensitivities.FirstOrDefault(s => s.Metric == defl.Name && s.Parameter == entry.Axis.Label)?.SwingPercent ?? 0).Take(2).ToArray();
            var (first, second) = (top[0], top[1]);
            var lines = second.Axis.Values.Take(6).Select((value, colour) => new StudySeries($"{second.Axis.Label} = {VcfOptimizationRunner.Text(value)}",
                first.Axis.Values.Select(x => (X(first.Axis, x), results.Where(result => Equals(result.Case.Values[first.Index], x) && Equals(result.Case.Values[second.Index], value))
                    .Select(defl.Value).OfType<double>().DefaultIfEmpty(double.NaN).Average())).Where(point => double.IsFinite(point.Item2)).ToArray(), colour))
                .Where(series => series.Points.Count > 0).ToArray();
            if (lines.Length > 0)
                charts.Add(new(Next(), $"Interaction: δ vs {first.Axis.Label} for each {second.Axis.Label}", first.Axis.Label, $"{defl.Name} [{defl.Unit}]",
                    StudyChartKind.Line, lines,
                    $"The two parameters that move the deflection most. Parallel curves mean the effects add up; curves that fan out mean the " +
                    $"effect of {first.Axis.Label} depends on {second.Axis.Label}.",
                    Categories: Categorical(first.Axis) ? first.Axis.Values.Select(VcfOptimizationRunner.Text).ToArray() : null));
        }

        var (summary, conclusions) = Summarise(axes, varying.Select(entry => entry.Axis).ToArray(), all, results, metrics, sensitivities, resultName, criterion);
        return new(charts, sensitivities, summary, conclusions);
    }

    private static string N(double value, string unit) => unit switch
    {
        "SGD" => value.ToString("N0", CultureInfo.CurrentCulture),
        "–" => value.ToString("0.00", CultureInfo.CurrentCulture),
        _ => Math.Abs(value) >= 100 ? value.ToString("N0", CultureInfo.CurrentCulture) : value.ToString("0.#", CultureInfo.CurrentCulture),
    };

    private static (IReadOnlyList<(string, string)>, IReadOnlyList<string>) Summarise(IReadOnlyList<VcfOptimizationAxis> axes, IReadOnlyList<VcfOptimizationAxis> varying,
        IReadOnlyList<VcfOptimizationResult> all, IReadOnlyList<VcfOptimizationResult> results, IReadOnlyList<Metric> metrics,
        IReadOnlyList<StudySensitivity> sensitivities, string resultName, string criterion)
    {
        var summary = new List<(string, string)>();
        var conclusions = new List<string>();
        var failed = all.Count - results.Count;
        string Range(VcfOptimizationAxis axis) => axis.Values.Count <= 6
            ? string.Join(", ", axis.Values.Select(VcfOptimizationRunner.Text))
            : $"{VcfOptimizationRunner.Text(axis.Values[0])} … {VcfOptimizationRunner.Text(axis.Values[^1])} ({axis.Values.Count} values)";
        summary.Add(("Study",
            $"{all.Count:N0} cases were generated by varying {string.Join("; ", varying.Select(axis => $"{axis.Label} ({Range(axis)})"))}, every other input held at " +
            $"the model's value. {results.Count:N0} were analysed with the internal FEM" + (failed > 0 ? $" ({failed:N0} could not be analysed and are left out)" : "") +
            $". Responses are read in '{resultName}'; the horizontal and vertical base forces are the sums over the supports of one end."));

        var passing = results.Where(result => result.Passes == true).ToArray();
        summary.Add(("Compliance", $"{passing.Length:N0} of {results.Count:N0} cases ({100.0 * passing.Length / results.Count:0}%) satisfy {criterion}."));

        foreach (var metric in metrics.Where(metric => metric.Key3))
        {
            var values = results.Select(metric.Value).OfType<double>().ToArray();
            var byParameter = sensitivities.Where(s => s.Metric == metric.Name).OrderByDescending(s => s.SwingPercent).ToArray();
            if (byParameter.Length == 0) continue;
            var ranked = byParameter.Where(s => s.SwingPercent >= FlatSwingPercent).ToArray();
            var flat = byParameter.Where(s => s.SwingPercent < FlatSwingPercent).Select(s => s.Parameter).ToArray();
            string Describe(StudySensitivity s) =>
                $"{s.Parameter} (swing {s.SwingPercent:0.#}%" +
                (s.NonMonotonic ? $", not monotonic — {(s.TurningIsMinimum ? "lowest" : "highest")} at {s.Parameter} ≈ {s.TurningPointX:0.###}"
                    : s.Elasticity is { } e ? $", elasticity {e:0.00} — +1% of {s.Parameter} gives {(e >= 0 ? "+" : "")}{e:0.00}% of the response" : "") + ")";
            var independent = flat.Length == 0 ? "" : $" It does not depend on {string.Join(" or ", flat)} (swing below {FlatSwingPercent}%).";
            if (ranked.Length == 0)
            {
                summary.Add((metric.Name, $"Ranges {N(values.Min(), metric.Unit)} … {N(values.Max(), metric.Unit)} {metric.Unit}; no parameter moves it noticeably." + independent));
                conclusions.Add($"{metric.Name} hardly changes over the ranges studied.");
                continue;
            }
            var lead = ranked[0];
            var trend = lead.Direction switch { > 0 => "rises", < 0 => "falls", _ => "does not change" };
            var shape = lead.NonMonotonic ? "" : lead.RSquared is { } r ? r >= 0.98 ? "almost linearly " : r >= 0.85 ? "nearly linearly " : "non-linearly " : "";
            var leadTrend = lead.NonMonotonic
                ? $"it is not monotonic in {lead.Parameter}: {(lead.TurningIsMinimum ? "lowest" : "highest")} near {lead.Parameter} ≈ {lead.TurningPointX:0.###}"
                : $"it {trend} {shape}as {lead.Parameter} increases";
            summary.Add((metric.Name,
                $"Ranges {N(values.Min(), metric.Unit)} … {N(values.Max(), metric.Unit)} {metric.Unit}. Most sensitive to {Describe(lead)}; {leadTrend}." +
                (ranked.Length > 1 ? $" Next: {string.Join("; ", ranked.Skip(1).Select(Describe))}." : "") + independent));
            var comparison = ranked.Length > 1 && ranked[1].SwingPercent > 0
                ? lead.SwingPercent / ranked[1].SwingPercent < 1.15 ? $", about as much as {ranked[1].Parameter}."
                    : $", {lead.SwingPercent / ranked[1].SwingPercent:0.#}× the effect of {ranked[1].Parameter}."
                : ".";
            conclusions.Add(lead.NonMonotonic
                ? $"{metric.Name} is governed by {lead.Parameter} ({lead.SwingPercent:0}% swing), with an optimum near {lead.Parameter} ≈ {lead.TurningPointX:0.###}{comparison}"
                : $"{metric.Name} is governed by {lead.Parameter}: it {trend} by {lead.SwingPercent:0}% of its mean over the range{comparison}");
            foreach (var turning in ranked.Skip(1).Where(s => s.NonMonotonic))
                conclusions.Add($"{metric.Name} is not monotonic in {turning.Parameter}: it is {(turning.TurningIsMinimum ? "lowest" : "highest")} near {turning.Parameter} ≈ {turning.TurningPointX:0.###}.");
        }

        // parameters that move cost without much deflection benefit, and the other way round
        var deflName = metrics.FirstOrDefault(metric => metric.Key == "defl")?.Name;
        var costName = metrics.FirstOrDefault(metric => metric.Key == "cost")?.Name;
        if (deflName is not null && costName is not null)
            foreach (var axis in varying)
            {
                var d = sensitivities.FirstOrDefault(s => s.Metric == deflName && s.Parameter == axis.Label);
                var c = sensitivities.FirstOrDefault(s => s.Metric == costName && s.Parameter == axis.Label);
                if (d is null || c is null) continue;
                if (d.Direction < 0 && c.Direction > 0 && d.SwingPercent > 2 * c.SwingPercent)
                    conclusions.Add($"Increasing {axis.Label} is an efficient way to stiffen the system: it reduces δ by {d.SwingPercent:0}% for {c.SwingPercent:0}% more cost.");
                else if (d.Direction < 0 && c.Direction > 0 && c.SwingPercent > 2 * d.SwingPercent)
                    conclusions.Add($"Increasing {axis.Label} costs {c.SwingPercent:0}% more for only {d.SwingPercent:0}% less deflection — an expensive way to stiffen.");
            }

        var cheapest = passing.Where(result => result.CostSgd is not null).MinBy(result => result.CostSgd);
        if (cheapest is not null)
        {
            string Inputs(VcfOptimizationResult result) => string.Join(", ", varying.Select(axis => $"{axis.Label} {VcfOptimizationRunner.Text(result.Case.Values[axes.ToList().IndexOf(axis)])}"));
            var text = $"The cheapest passing case is case {cheapest.Case.Index + 1} ({Inputs(cheapest)}): SGD {cheapest.CostSgd:N0}, δ {cheapest.MaxDeflectionMm:0.0} mm " +
                $"against {cheapest.LimitMm:0.0} mm (δ / limit {cheapest.MaxDeflectionMm / cheapest.LimitMm:0.00})" +
                (cheapest.Response is { } response ? $", H {response.BaseHorizontalKN:N0} kN at one end" : "") + ".";
            var spare = 1 - cheapest.MaxDeflectionMm!.Value / cheapest.LimitMm!.Value;
            text += spare > 0.25 ? " It keeps more than a quarter of the deflection limit in reserve, so a lighter system outside the ranges studied may still pass."
                : spare < 0.05 ? " It sits at the deflection limit: deflection governs the optimum." : "";
            summary.Add(("Optimum", text));
            conclusions.Add($"Recommended starting point: case {cheapest.Case.Index + 1} ({Inputs(cheapest)}), SGD {cheapest.CostSgd:N0}.");
        }
        else summary.Add(("Optimum", "No analysed case satisfies the criteria. Widen the ranges towards a stiffer system (the sensitivity ranking shows which parameter helps most)."));

        summary.Add(("Limitations",
            "Trends are main effects: each curve averages over the other parameters, so a strong interaction (see the interaction plot) can hide " +
            "behind a flat mean. Results hold within the ranges studied; every case is analysed with the app's internal FEM and the Analysis Options of the model."));
        return (summary, conclusions);
    }
}
