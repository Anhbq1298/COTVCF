using Mcotis.Desktop.Application;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;

/// <summary>
/// The Chart sheet of an optimization run (<see cref="OptimizationStudy"/>), on a synthetic run with known answers: main
/// effects, the sensitivity ranking (swing, elasticity, R²), the figure set (10–20), the trade-off and interaction figures and
/// the summary; and the one-end base force of <see cref="OptimizationResponses"/>. Part of the default suite and
/// <c>--optimization-study</c>.
/// </summary>
internal static class OptimizationStudyChecks
{
    public static void Run()
    {
        // 7 bay counts × 3 live loads: δ = 10 × bays + qz [mm], span = 10 m × bays, limit span / 250, cost = 1,000,000 × bays,
        // H = 500 × qz — every response is linear in its parameter(s)
        var bays = new VcfOptimizationAxis("geometry:Bays", "Bays", Enumerable.Range(4, 7).Select(value => (object)(double)value).ToArray());
        var live = new VcfOptimizationAxis("storey:LL", "qz LL [kPa]", new object[] { 5.0, 7.5, 10.0 });
        var dead = new VcfOptimizationAxis("storey:DL", "qz DL [kPa]", new object[] { 10.0 });
        var axes = new[] { bays, dead, live };
        var results = new List<VcfOptimizationResult>();
        long index = 0;
        foreach (double b in bays.Values)
            foreach (double q in live.Values)
            {
                var span = 10 * b;
                results.Add(new VcfOptimizationResult(new VcfOptimizationCase(index++, [b, 10.0, q]), 10 * b + q, "SLS", "BTrPt003", true, null,
                    span, span * 1000 / 250, 1_000_000 * b, null, 0, null, 0,
                    new OptimizationResponse(500 * q, 1000 * b, 2000 * b, 300 * b, 0, 20 * b)));
            }
        results.Add(VcfOptimizationRunnerFailed(index));     // a case that could not be analysed is left out

        var report = OptimizationStudy.Build(axes, results, "SLS", "δ ≤ span / 250");
        StudySensitivity S(string metric, string parameter) => report.Sensitivities.Single(s => s.Metric.StartsWith(metric, StringComparison.Ordinal) && s.Parameter == parameter);
        var deflBays = S("Max deflection", "Bays");
        var deflLive = S("Max deflection", "qz LL [kPa]");
        // means over qz: 10 b + 7.5 → swing 60 of the mean 77.5 = 77.4%; slope 10 × x̄ 7 / ȳ 77.5 → elasticity 0.903
        Check(Math.Abs(deflBays.SwingPercent - 60 / 77.5 * 100) < 1e-9 && Math.Abs(deflBays.Elasticity!.Value - 70 / 77.5) < 1e-9 &&
              Math.Abs(deflBays.RSquared!.Value - 1) < 1e-12 && deflBays.Direction > 0 &&
              Math.Abs(deflLive.SwingPercent - 5 / 77.5 * 100) < 1e-9 && deflBays.SwingPercent > deflLive.SwingPercent,
            $"Main effects: δ follows the bays (swing {deflBays.SwingPercent:0.0}%, elasticity {deflBays.Elasticity:0.000}, R² 1) far more than the live load (swing {deflLive.SwingPercent:0.0}%)");
        Check(S("Total cost", "qz LL [kPa]").SwingPercent == 0 && S("Base horizontal", "Bays").SwingPercent == 0 && S("Base horizontal", "qz LL [kPa]").SwingPercent > 0 &&
              report.Sensitivities.All(s => s.Parameter != "qz DL [kPa]"),
            "A response that does not depend on a parameter has no swing to it (cost vs load, H vs bays); a fixed parameter is not charted");

        var lines = report.Charts.Where(chart => chart.Kind == StudyChartKind.Line).ToArray();
        var first = report.Charts[0];
        Check(report.Charts.Count is >= 10 and <= OptimizationStudy.MaxCharts &&
              report.Charts.Select(chart => chart.Number).SequenceEqual(Enumerable.Range(1, report.Charts.Count)) &&
              first.Title == "Max deflection δ vs Bays" && first.Series[0].Points.Count == 7 && first.Series[0].Low is { Count: 7 } &&
              first.Series[0].Points[0] == (4, 47.5) && first.Series[0].Low![0] == 45 && first.Series[0].High![0] == 50 &&
              report.Charts.Count(chart => chart.Kind == StudyChartKind.Bars) == 1 && report.Charts.Count(chart => chart.Kind == StudyChartKind.Scatter) == 1 &&
              lines.Any(chart => chart.Title.StartsWith("Interaction", StringComparison.Ordinal) && chart.Series.Count == 3) &&
              lines.Single(chart => chart.Title.StartsWith("Utilisation δ", StringComparison.Ordinal) && chart.XTitle == "Bays").LimitY == 1,
            $"{report.Charts.Count} numbered figures: main effects (mean with min … max band; δ vs bays first, 47.5 mm at 4 bays, band 45 … 50), " +
            "a sensitivity bar chart, the cost / utilisation scatter and the interaction plot; the utilisation figure carries the limit line at 1.0");

        var summary = string.Join(" ", report.Summary.Select(entry => entry.Heading + ". " + entry.Text));
        Check(report.Summary.Select(entry => entry.Heading).Take(2).SequenceEqual(["Study", "Compliance"]) &&
              summary.Contains("22 cases were generated", StringComparison.Ordinal) && summary.Contains("21 were analysed", StringComparison.Ordinal) && summary.Contains("1 could not be analysed", StringComparison.Ordinal) &&
              summary.Contains("21 of 21 cases (100%) satisfy δ ≤ span / 250", StringComparison.Ordinal) &&
              report.Summary.Any(entry => entry.Heading == "Optimum" && entry.Text.Contains("case 1 (Bays 4, qz LL [kPa] 5)", StringComparison.Ordinal)) &&
              report.Conclusions.Any(text => text.StartsWith("Max deflection δ is governed by Bays", StringComparison.Ordinal)) &&
              report.Conclusions.Any(text => text.StartsWith("Base horizontal force H (one end) is governed by qz LL [kPa]", StringComparison.Ordinal)),
            "The summary reads like a paper: the study, the compliance, each key response with its governing parameter, the optimum (cheapest passing case) and numbered conclusions");

        Check(report.Charts.All(chart => !chart.Title.StartsWith("Total cost of the system vs qz", StringComparison.Ordinal)) &&
              report.Charts.All(chart => chart.Title != "Base horizontal force H (one end) vs Bays") &&
              report.Summary.Single(entry => entry.Heading == "Total cost of the system").Text.Contains("It does not depend on qz LL [kPa]", StringComparison.Ordinal),
            "A flat main effect (cost vs the live load, H vs the bays) is not charted; the summary says the response does not depend on it");

        // a U-shaped trend: δ = (depth − 11)² + 10 over depth 5 … 20 — lowest at 11 m, which a straight line cannot describe
        var depthAxis = new VcfOptimizationAxis("geometry:Depth", "Depth [m]", Enumerable.Range(5, 16).Select(value => (object)(double)value).ToArray());
        var bowl = Enumerable.Range(5, 16).Select((d, i) => new VcfOptimizationResult(new VcfOptimizationCase(i, [(double)d]), (d - 11.0) * (d - 11.0) + 10, "SLS", "BTrPt003", true, null,
            100, 400, 1_000_000 + 10_000 * d, null, 0, null, 0, new OptimizationResponse(1000, 2000, 0, 0, 0, 100 + d))).ToArray();
        var curved = OptimizationStudy.Build([depthAxis], bowl, "SLS", "δ ≤ span / 250");
        var turn = curved.Sensitivities.Single(s => s.Metric == "Max deflection δ");
        Check(turn.NonMonotonic && turn.TurningPointX == 11 && turn.TurningIsMinimum &&
              curved.Summary.Single(entry => entry.Heading == "Max deflection δ").Text.Contains("lowest near Depth [m] ≈ 11", StringComparison.Ordinal) &&
              curved.Conclusions.Any(text => text.Contains("optimum near Depth [m] ≈ 11", StringComparison.Ordinal)) &&
              curved.Charts[0].Caption.Contains("not monotonic — lowest at Depth [m] ≈ 11", StringComparison.Ordinal),
            "A U-shaped trend is reported by its turning point (δ lowest at depth ≈ 11 m), not by an elasticity a straight line would give");

        var empty = OptimizationStudy.Build([bays], results.Take(1).ToArray(), "SLS", "δ ≤ span / 250");
        Check(empty.Charts.Count == 0 && empty.Summary.Single().Text.Contains("Fewer than two cases", StringComparison.Ordinal),
            "With fewer than two analysed cases there are no charts, and the page says why");

        // one-end base force: four column bases, the thrust of one end is the sum of its two (over all four it is zero)
        var points = new Dictionary<string, StructuralPoint>(StringComparer.Ordinal)
        {
            ["A"] = new("A", new(0, 0, 0)), ["B"] = new("B", new(10, 0, 0)), ["C"] = new("C", new(90, 0, 0)), ["D"] = new("D", new(100, 0, 0)),
        };
        var geometry = new GeometryModel(points, [new StructuralMember("F1", "A", "B", MemberKind.Frame)]);
        var reactions = new FemAnalysisResult([], [new("A", 300, 1000), new("B", 200, 1500), new("C", -260, 1400), new("D", -240, 1100)], [], [], 0);
        var response = OptimizationResponses.Of(geometry, new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal), reactions);
        Check(response.BaseHorizontalKN == 500 && response.BaseVerticalKN == 2500,
            "The base horizontal force is the sum over the two column bases of one end (300 + 200 = 500 kN; all four sum to 0), V likewise (2,500 kN)");
    }

    private static VcfOptimizationResult VcfOptimizationRunnerFailed(long index) =>
        new(new VcfOptimizationCase(index, [4.0, 10.0, 5.0]), null, null, null, false, "did not converge");

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
