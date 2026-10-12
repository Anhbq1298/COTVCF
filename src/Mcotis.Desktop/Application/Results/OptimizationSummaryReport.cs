using System.Globalization;
using System.IO;
using System.Text;
using COT_VCF.Domain;
using COT_VCF.Infrastructure;

namespace COT_VCF.Application.Results;

/// <summary>Tab 06A front page of the optimized set: the criteria, how many candidates qualify and why the others
/// do not, an acceptance map of every candidate against the two limits, and the candidate tables. Self-contained
/// HTML rendered here — the page needs no script of its own.</summary>
public sealed class OptimizationSummaryReport
{
    public const string FileName = "Optimization_Summary.html";
    /// <summary>Report name printed in the page header.</summary>
    public const string ReportTitle = "Optimization Summary";

    public string Generate(string folder, StudyQualification qualification, string baselineName, IterationStudyMode studyMode,
        IReadOnlyList<string> siblingReports, ReportPrintSettings? print = null)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        AtomicFile.WriteAllText(path, BuildHtml(qualification, baselineName, studyMode.ToString(), siblingReports,
            ReportPrintEngine.Describe(print, ReportTitle, baselineName, DateTimeOffset.Now)), new UTF8Encoding(false));
        return path;
    }

    public string BuildHtml(StudyQualification q, string baselineName, string studyMode, IReadOnlyList<string> siblingReports,
        ReportPrintDto? print = null)
    {
        print ??= ReportPrintEngine.Describe(null, ReportTitle, baselineName, DateTimeOffset.Now);
        var s = q.Summary;
        var library = ReportNav.Library("optimization", new HashSet<string>(siblingReports, StringComparer.Ordinal));
        var qualified = q.Qualified.OrderBy(c => c.Metrics.RunIndex).ToArray();
        var b = new StringBuilder();
        b.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Optimization Summary — ").Append(E(baselineName)).Append("</title>")
            .Append("<style>").Append(ReportPrintEngine.Css).Append(Style).Append(ReportSummaryTheme.Css).Append(ReportScope.Css).Append("</style>")
            .Append(ReportPrintEngine.PageStyle).Append("</head><body><header><div data-pr=\"info\"><h1>Optimization Summary</h1>")
            .Append($"<div>{E(baselineName)} · {s.Total} candidate(s) · {E(studyMode)}</div>")
            .Append(ReportScope.Banner(StudyOptimizationProcessor.SetLabel, StudyOptimizationProcessor.Note(q)))
            .Append("<p data-pr=\"note\">A candidate is <b>qualified</b> when it passes every active criterion. δ is the peak downward deflection −min(U<sub>z</sub>) over the monitored points; ")
            .Append("cable utilisation is the final analysed force of a cable over its design capacity (current Tab 03 Pretension), the largest over all cables. ")
            .Append("A candidate whose result or capacity is missing is <b>not assessed</b> — never counted as qualified. The other reports of this folder show the qualified candidates only.</p>")
            .Append("</div>").Append(library).Append("</header>").Append(ReportPrintEngine.Bar)
            .Append("<main><section class=\"panel sumpanel\">")
            .Append("<div class=\"chead\" data-pr=\"summary\" data-pr-keep><span class=\"cname\">Qualified Candidates</span><span class=\"cview\">Summary</span>")
            .Append($"<span class=\"sub\">{E(q.Criteria.Label)}</span></div>");

        // ---- KPI cards
        string Card(string name, string value, string unit, string line, string detail, string colour) =>
            $"<div class=\"kpi\" style=\"border-left-color:{colour}\"><div class=\"kl\">{E(name)}</div><div class=\"kv\">{E(value)} <span>{E(unit)}</span></div>" +
            $"<div class=\"kw\">{E(line)}</div><div class=\"kd\">{E(detail)}</div></div>";
        var share = s.Total > 0 ? (100.0 * s.Qualified / s.Total).ToString("0.#", Inv) + " % of the study" : "";
        b.Append("<div class=\"kpis\" data-pr=\"summary\">")
            .Append(Card("Qualified", N(s.Qualified), $"of {N(s.Total)}", share, "pass every active criterion", Accent))
            .Append(Card("Not qualified", N(s.NotQualified), "", $"deflection only {N(s.FailedDeflectionOnly)} · cable only {N(s.FailedCableOnly)} · both {N(s.FailedBoth)}", "fail at least one criterion", Fail))
            .Append(Card("Not assessed", N(s.NotAssessed), "", s.NotAssessed == 0 ? "every candidate could be assessed" : "a result or a capacity is missing", "never counted as qualified", Muted))
            .Append(Card("Study range", s.MinPeakDownMm is { } lo && s.MaxPeakDownMm is { } hi ? $"{Mm(lo)}–{Mm(hi)}" : "—", "mm δ",
                s.MinUtilisation is { } ulo && s.MaxUtilisation is { } uhi ? $"cable utilisation {Pct(ulo)}–{Pct(uhi)}" : "cable utilisation unavailable",
                s.CablesWithoutCapacity.Count == 0 ? $"{s.CablesWithCapacity.Count} cable(s) with a design capacity"
                    : $"no capacity: {string.Join(", ", s.CablesWithoutCapacity.Take(6))}{(s.CablesWithoutCapacity.Count > 6 ? " …" : "")}", "#8e98a3"))
            .Append("</div>");

        // ---- criteria
        b.Append("<div class=\"ccard\" data-pr=\"summary\"><div class=\"ctitle\">Criteria</div><table class=\"ctable\" data-table=\"criteria\"><thead><tr><th>Criterion</th><th>Limit</th><th>Reads</th><th class=\"n\">Pass</th><th class=\"n\">Fail</th><th class=\"n\">Not assessed</th></tr></thead><tbody>");
        void Criterion(string name, bool active, string limit, string reads, Func<CandidateQualification, CriterionVerdict> verdict) =>
            b.Append($"<tr><td>{E(name)}</td><td>{E(active ? limit : "not active")}</td><td>{E(reads)}</td>")
                .Append(active
                    ? $"<td class=\"n\">{N(q.Candidates.Count(c => verdict(c) == CriterionVerdict.Pass))}</td><td class=\"n\">{N(q.Candidates.Count(c => verdict(c) == CriterionVerdict.Fail))}</td><td class=\"n\">{N(q.Candidates.Count(c => verdict(c) == CriterionVerdict.NotAssessed))}</td></tr>"
                    : "<td class=\"n\">—</td><td class=\"n\">—</td><td class=\"n\">—</td></tr>");
        Criterion("Deflection within range", q.Criteria.DeflectionActive, $"δ {q.Criteria.DeflectionBand.Label}", "peak downward deflection δ = −min(Uz) [mm]", c => c.Deflection);
        Criterion("Cable forces below the limit", q.Criteria.CableActive, $"utilisation ≤ {QualificationCriteria.Ratio(q.Criteria.MaxCableUtilization)} ({Pct(q.Criteria.MaxCableUtilization)} of capacity)",
            "final analysed force / design capacity, every cable", c => c.Cable);
        b.Append("</tbody></table></div>");

        // ---- acceptance map
        var plotted = q.Candidates.Where(c => c.Metrics.PeakDownMm.HasValue && c.GoverningUtilisation.HasValue).ToArray();
        b.Append("<div class=\"ccard\" data-pr=\"chart\" data-pr-type=\"acceptanceMap\" data-pr-id=\"chart_acceptance_map\"><div class=\"ctitle\">Acceptance Map<span>every candidate by its δ and its governing cable utilisation · the shaded window is where both criteria pass</span></div>")
            .Append("<div class=\"clegend\" data-pr=\"legend\">")
            .Append($"<span><svg width=\"12\" height=\"12\"><circle cx=\"6\" cy=\"6\" r=\"4\" fill=\"{Accent}\"/></svg>Qualified ({N(plotted.Count(c => c.Status == QualificationStatus.Qualified))})</span>")
            .Append($"<span><svg width=\"12\" height=\"12\"><circle cx=\"6\" cy=\"6\" r=\"3.4\" fill=\"#fff\" stroke=\"{Fail}\" stroke-width=\"1.3\"/></svg>Not qualified ({N(plotted.Count(c => c.Status == QualificationStatus.NotQualified))})</span>")
            .Append($"<span><svg width=\"12\" height=\"12\"><path d=\"M2 2 L10 10 M10 2 L2 10\" stroke=\"{Muted}\" stroke-width=\"1.4\"/></svg>Not assessed ({N(plotted.Count(c => c.Status == QualificationStatus.NotAssessed))})</span>")
            .Append($"<span class=\"note\">{(plotted.Length == q.Candidates.Count ? "all candidates plotted" : $"{N(q.Candidates.Count - plotted.Length)} candidate(s) have no δ or no utilisation and cannot be plotted")} · hover a point for its iteration</span></div>")
            .Append(plotted.Length > 0 ? Map(q, plotted) : "<p class=\"empty\">No candidate has both a deflection and a cable utilisation to plot.</p>")
            .Append("</div>");

        // ---- qualified candidates
        const string head = "<thead><tr><th>Iteration</th><th>Load</th><th>Height</th><th>Pretension</th><th class=\"n\">δ [mm]</th><th class=\"n\">Cable utilisation [%]</th><th>Governing cable</th><th class=\"n\">Force [kN]</th><th class=\"n\">Capacity [kN]</th>";
        string Row(CandidateQualification c, bool withStatus)
        {
            var m = c.Metrics;
            return $"<tr data-status=\"{c.Status}\"><td>{E(m.IterationKey)}</td><td>{E(m.LoadId)}</td><td>{E(CableForceMath.HeightLabel(m.TrussHeightM))}</td><td>{E(m.PretensionId)}</td>" +
                   $"<td class=\"n\">{(m.PeakDownMm is { } d ? Mm(d) : "—")}</td><td class=\"n\">{(c.GoverningUtilisation is { } u ? (u * 100).ToString("0.0", Inv) : "—")}</td>" +
                   $"<td>{E(c.GoverningCable ?? "—")}</td><td class=\"n\">{(c.GoverningForceKN is { } f ? f.ToString("#,0", Inv) : "—")}</td><td class=\"n\">{(c.GoverningCapacityKN is { } cap ? cap.ToString("#,0", Inv) : "—")}</td>" +
                   (withStatus ? $"<td class=\"st {c.Status.ToString().ToLowerInvariant()}\">{E(c.StatusText)}</td><td>{E(c.Reason)}</td>" : "") + "</tr>";
        }
        b.Append($"<div class=\"ccard\" data-pr=\"chart\" data-pr-type=\"qualifiedTable\" data-pr-id=\"table_qualified\"><div class=\"ctitle\">Qualified Candidates<span>{N(qualified.Length)} of {N(s.Total)}</span></div>");
        if (qualified.Length == 0)
            b.Append("<p class=\"empty\" data-empty=\"qualified\">No candidate qualifies under these criteria — no optimized charts were generated.</p>");
        else
            b.Append("<div class=\"matrix-wrap\" data-print-fit><table class=\"ctable\" data-table=\"qualified\">").Append(head).Append("</tr></thead><tbody>")
                .Append(string.Concat(qualified.Select(c => Row(c, false)))).Append("</tbody></table></div>");
        b.Append("</div>");

        // ---- every candidate (optional on paper)
        b.Append("<details class=\"cdata\" data-print=\"source\" data-pr=\"source\"><summary>All candidates — status and governing values</summary><div class=\"matrix-wrap\" data-print-fit><table class=\"ctable\" data-table=\"all\">")
            .Append(head).Append("<th>Status</th><th>Reason</th></tr></thead><tbody>")
            .Append(string.Concat(q.Candidates.OrderBy(c => c.Status).ThenBy(c => c.Metrics.RunIndex).Select(c => Row(c, true))))
            .Append("</tbody></table></div></details>");

        b.Append("</section></main>").Append(ReportPrintEngine.Scripts(print)).Append("</body></html>");
        return b.ToString();
    }

    // δ on x, governing cable utilisation on y; the acceptance window shaded; one point per candidate.
    private static string Map(StudyQualification q, IReadOnlyList<CandidateQualification> plotted)
    {
        const double W = 1100, H = 430, ml = 62, mr = 22, mt = 16, mb = 46;
        double pw = W - ml - mr, ph = H - mt - mb;
        var band = q.Criteria.DeflectionActive ? q.Criteria.DeflectionBand : null;
        double? limit = q.Criteria.CableActive ? q.Criteria.MaxCableUtilization * 100 : null;
        var xs = plotted.Select(c => c.Metrics.PeakDownMm!.Value).Concat(new[] { band?.MinMm, band?.MaxMm }.Where(v => v.HasValue).Select(v => v!.Value)).ToArray();
        var ys = plotted.Select(c => c.GoverningUtilisation!.Value * 100).Concat(limit is { } l ? [l] : []).ToArray();
        var (x0, x1, xStep) = Axis(xs.Min(), xs.Max());
        var (y0, y1, yStep) = Axis(ys.Min(), ys.Max());
        double X(double v) => ml + (v - x0) / (x1 - x0) * pw;
        double Y(double v) => mt + (y1 - v) / (y1 - y0) * ph;
        string F(double v) => v.ToString("0.#", Inv);

        var svg = new StringBuilder($"<svg class=\"cchart\" data-chart=\"acceptance\" viewBox=\"0 0 {F(W)} {F(H)}\" role=\"img\" aria-label=\"Acceptance map: peak downward deflection against governing cable utilisation for every candidate\">");
        // the window where every active criterion passes
        double wx0 = band?.MinMm is { } bmin ? X(bmin) : ml, wx1 = band?.MaxMm is { } bmax ? X(bmax) : ml + pw, wy0 = limit is { } lim ? Y(lim) : mt;
        svg.Append($"<rect data-window=\"accept\" x=\"{F(wx0)}\" y=\"{F(wy0)}\" width=\"{F(Math.Max(0, wx1 - wx0))}\" height=\"{F(Math.Max(0, mt + ph - wy0))}\" fill=\"{Accent}\" fill-opacity=\".08\"/>");
        for (var v = y0; v <= y1 + 1e-9; v += yStep)
            svg.Append($"<line class=\"cgrid\" x1=\"{F(ml)}\" x2=\"{F(W - mr)}\" y1=\"{F(Y(v))}\" y2=\"{F(Y(v))}\"/><text class=\"ctick\" x=\"{F(ml - 6)}\" y=\"{F(Y(v) + 3)}\" text-anchor=\"end\">{Tick(v, yStep)}</text>");
        for (var v = x0; v <= x1 + 1e-9; v += xStep)
            svg.Append($"<line class=\"cgrid\" x1=\"{F(X(v))}\" x2=\"{F(X(v))}\" y1=\"{F(mt)}\" y2=\"{F(mt + ph)}\"/><text class=\"ctick\" x=\"{F(X(v))}\" y=\"{F(mt + ph + 14)}\" text-anchor=\"middle\">{Tick(v, xStep)}</text>");
        svg.Append($"<line class=\"caxis\" x1=\"{F(ml)}\" x2=\"{F(ml)}\" y1=\"{F(mt)}\" y2=\"{F(mt + ph)}\"/><line class=\"caxis\" x1=\"{F(ml)}\" x2=\"{F(W - mr)}\" y1=\"{F(mt + ph)}\" y2=\"{F(mt + ph)}\"/>");
        // the limits, labelled on the lines
        void Vertical(double value, string label) =>
            svg.Append($"<line class=\"alim\" x1=\"{F(X(value))}\" x2=\"{F(X(value))}\" y1=\"{F(mt)}\" y2=\"{F(mt + ph)}\"/><text class=\"alab\" x=\"{F(X(value) + 4)}\" y=\"{F(mt + 11)}\">{E(label)}</text>");
        if (band?.MinMm is { } minMm) Vertical(minMm, $"δ min {Mm(minMm)} mm");
        if (band?.MaxMm is { } maxMm) Vertical(maxMm, $"δ max {Mm(maxMm)} mm");
        if (limit is { } limitPct)
            svg.Append($"<line class=\"alim\" x1=\"{F(ml)}\" x2=\"{F(W - mr)}\" y1=\"{F(Y(limitPct))}\" y2=\"{F(Y(limitPct))}\"/><text class=\"alab\" x=\"{F(W - mr - 4)}\" y=\"{F(Y(limitPct) - 5)}\" text-anchor=\"end\">utilisation limit {F(limitPct)} %</text>");

        // not qualified first, so the qualified points sit on top
        foreach (var c in plotted.OrderBy(c => c.Status == QualificationStatus.Qualified ? 1 : 0).ThenBy(c => c.Metrics.RunIndex))
        {
            var m = c.Metrics;
            double cx = X(m.PeakDownMm!.Value), cy = Y(c.GoverningUtilisation!.Value * 100);
            var tip = $"{m.IterationKey} — δ {Mm(m.PeakDownMm.Value)} mm · utilisation {Pct(c.GoverningUtilisation.Value)} ({c.GoverningCable}) · {c.StatusText}" +
                      (c.Status == QualificationStatus.Qualified ? "" : $": {c.Reason}");
            svg.Append(c.Status switch
            {
                QualificationStatus.Qualified => $"<circle data-point=\"{E(m.IterationKey)}\" data-status=\"Qualified\" cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"3.4\" fill=\"{Accent}\" stroke=\"#fff\" stroke-width=\".8\">",
                QualificationStatus.NotQualified => $"<circle data-point=\"{E(m.IterationKey)}\" data-status=\"NotQualified\" cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"3\" fill=\"#fff\" fill-opacity=\".6\" stroke=\"{Fail}\" stroke-width=\"1.1\">",
                _ => $"<path data-point=\"{E(m.IterationKey)}\" data-status=\"NotAssessed\" d=\"M{F(cx - 3)} {F(cy - 3)} l6 6 m0 -6 l-6 6\" stroke=\"{Muted}\" stroke-width=\"1.3\" fill=\"none\">",
            }).Append($"<title>{E(tip)}</title>").Append(c.Status == QualificationStatus.NotAssessed ? "</path>" : "</circle>");
        }
        svg.Append($"<text class=\"cax\" x=\"{F(ml + pw / 2)}\" y=\"{F(H - 8)}\" text-anchor=\"middle\">Peak downward deflection δ (mm)</text>")
            .Append($"<text class=\"cax\" transform=\"translate(15 {F(mt + ph / 2)}) rotate(-90)\" text-anchor=\"middle\">Governing cable utilisation (%)</text></svg>");
        return svg.ToString();
    }

    // a padded axis on round steps
    private static (double Lo, double Hi, double Step) Axis(double lo, double hi)
    {
        if (hi - lo < 1e-9) { lo -= 1; hi += 1; }
        double raw = (hi - lo) / 6, p = Math.Pow(10, Math.Floor(Math.Log10(raw))), n = raw / p;
        var step = (n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10) * p;
        return (Math.Floor(lo / step + 1e-9) * step, Math.Ceiling(hi / step - 1e-9) * step, step);
    }

    private static string Tick(double v, double step) => step % 1 != 0 ? v.ToString("0.0", Inv) : Math.Round(v).ToString("0", Inv);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string Accent = "#1f4788", Fail = "#b42318", Muted = "#667085";
    private static string E(string? value) => ReportScope.Text(value);
    private static string N(int value) => value.ToString("N0", Inv);
    private static string Mm(double value) => value.ToString("0.#", Inv);
    private static string Pct(double ratio) => (ratio * 100).ToString("0.0", Inv) + " %";

    private const string Style = """
*{box-sizing:border-box}body{margin:0;font:13px/1.4 Segoe UI,Arial;color:#1a1a1a}header{display:flex;justify-content:space-between;align-items:flex-start;gap:16px;padding:10px 14px;border-bottom:2px solid #1f4788}h1{font-size:16px;margin:0}header>div>div,header p{margin:3px 0;color:#666;font-size:11.5px;max-width:980px}.lib{font-size:12px}.lib summary{cursor:pointer;color:#1f4788;font-weight:600}.lib ul{list-style:none;margin:6px 0 0;padding:0;border:1px solid #c7ccd2;border-radius:3px;min-width:190px}.lib li{padding:4px 8px;border-bottom:1px solid #f3f4f6;color:#666}.lib li.on{color:#1a1a1a}.lib li span{float:right;font-size:10px;text-transform:uppercase;letter-spacing:.04em;color:#9aa0a6}.lib li.on span{color:#2e7d32}
.prbar{padding:6px 14px;border-bottom:1px solid #c7ccd2}.panel{padding:8px 10px}.sub{color:#666;font-size:11px;margin-left:8px}.empty{padding:14px 4px;color:#666}.matrix-wrap{overflow:auto;max-height:520px}
.ccard>.ctable{margin:4px 0 6px}.ctable td.st{font-weight:600}.ctable td.st.qualified{color:#1f4788}.ctable td.st.notqualified{color:#b42318}.ctable td.st.notassessed{color:#667085}
.alim{stroke:#344054;stroke-width:1.1;stroke-dasharray:5 3}.alab{fill:#344054;font-size:10px;font-weight:600;paint-order:stroke;stroke:#fff;stroke-width:3px;stroke-linejoin:round}
[hidden]{display:none!important}
.pr-sheet .panel{padding:0}.pr-sheet .matrix-wrap{max-height:none;overflow:visible}
""";
}
