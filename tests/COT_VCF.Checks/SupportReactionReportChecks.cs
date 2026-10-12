using System.IO;
using System.Text.RegularExpressions;
using COT_VCF.Application.Results;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;

internal static class SupportReactionReportChecks
{
    public static void Run()
    {
        var results = new List<CandidateResultDto>();
        for (var index = 1; index <= 60; index++)
        {
            var load = $"L{((index - 1) % 4) + 1:00}";
            var height = $"H{((index - 1) % 3) + 1:00}";
            var pretension = $"P{((index - 1) % 5) + 1:00}";
            var key = $"{load}_{height}_{pretension}_{index:000}";
            SupportReactionPointResultDto Point(string name, int sequence, double x, double fz) =>
                new(name, sequence, x, 0, 0, "Step", 1,
                    index * .1, -index * .05, fz, index * .01, index * .02, -index * .015,
                    ResultValueStatus.Valid);
            var left = new SupportReactionGroupResultDto("Left", "[O]-FoundationLeft", "ULS",
                [Point("L-S1", 1, -50, 1000 + index), Point("L-S2", 2, -45, 900 + index)]);
            var right = new SupportReactionGroupResultDto("Right", "[O]-FoundationRight", "ULS",
                [Point("R-S1", 1, 45, 950 + index), Point("R-S2", 2, 50, 1050 + index)]);
            var parameters = new CandidateParametersDto(index, key, load, height, pretension,
                2.5, $"{load} · 2.5 kPa", 20 + index % 3, 0, 500 + index, $"{pretension} · {500 + index} kN");
            results.Add(new CandidateResultDto(key, $@"C:\Study\{key}.sdb", @"C:\base.sdb", "COT.sdb",
                IterationStudyMode.Combined, parameters, DateTimeOffset.Now, DateTimeOffset.Now,
                DateTimeOffset.Now, CandidateExtractionStatus.ExtractedOk, [], [], [], [], [left, right]));
        }

        var generator = new SupportReactionReportGenerator();
        var dto = generator.Build(results, "COT.sdb", IterationStudyMode.Combined,
            ["deflection", "support"]);
        Check(dto.Groups.Count == 2 && dto.Groups.All(group => group.Series.Count == 60) &&
              dto.Groups.Single(group => group.FoundationSide == "Left").Series[0].Rows.Count == 2,
            "support-reaction report retains all candidates and both selected points per foundation");
        Check(dto.Groups.SelectMany(group => group.Series).SelectMany(candidate => candidate.Rows)
                  .All(row => row.Fz > 0 && row.Fy < 0),
            "support-reaction report preserves signed global force components");

        var html = generator.BuildHtml(dto);
        Check(html.Contains("Individual supports") && html.Contains("Foundation totals") &&
              html.Contains("Candidate matrix") && html.Contains("min/max reaction envelopes"),
            "support-reaction HTML provides individual, foundation-total, matrix, and large-study envelope views");
        Check(html.Contains("className='ms'") && html.Contains("Loading") && html.Contains("Pretension") &&
              html.Contains("Reset filters"),
            "support-reaction HTML uses the same compact L/H/P filtering pattern as the other reports");
        Check(html.Contains("Point:") && html.Contains("Global:") && html.Contains("Points are discrete supports") &&
              !html.Contains("http://") && !html.Contains("https://") && !html.Contains("cdn."),
            "support-reaction HTML has exact hover provenance, explains discrete supports, and is self-contained");
        Check(html.Contains("id=\"band\"") && html.Contains("Band by") && html.Contains("range among candidates") &&
              html.Contains("middle half (P25–P75)") && html.Contains("agg('Range'"),
            "support-reaction HTML draws a min–max / P25–P75 / median range band per column, optionally per L/H/P candidate, and matrix range rows");

        var folder = Path.Combine(Path.GetTempPath(), "cotvcf_sr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var writer = new ConsolidatedResultWriter();
            foreach (var result in results.Take(2)) writer.Add(result);
            var workbook = writer.Write(folder, [], "", "COT.sdb", @"C:\base.sdb",
                IterationStudyMode.Combined,
                foundationLeftPoints: ["L-S1", "L-S2"],
                foundationRightPoints: ["R-S1", "R-S2"],
                foundationReactionCase: "ULS");
            var roundTrip = ConsolidatedResultWriter.Read(workbook);
            Check(roundTrip.Count == 2 && roundTrip.All(result => result.SupportReactionGroups is { Count: 2 }) &&
                  roundTrip[0].SupportReactionGroups!.SelectMany(group => group.Points).Sum(point => point.Fz ?? 0) > 0,
                "support-reaction workbook round-trips signed point rows for both foundations");
            var regenerated = generator.RegenerateFromWorkbook(folder);
            Check(regenerated.EndsWith(SupportReactionReportGenerator.ReportFileName) && File.Exists(regenerated),
                "support-reaction HTML regenerates from the source workbook without SAP2000");
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }

        SummaryData();
        SummaryViews();
    }

    // ---------- Summary: inner | outer | Σ net per foundation side, and the By Candidate drill-down ----------

    /// <summary>P01… × truss heights under one or more loads; the last height is missing at P08. Left: Pt 200
    /// (outer, x = −60) and Pt 198 (inner, x = −50); Right: Pt 199 (inner, x = 50) and Pt 205 (outer, x = 60).
    /// "Min" is the default result step; "Max" reads 10 MN more inner Fz. The Right inner support carries 0.4 MN
    /// more than the Left, so the two sides are not interchangeable.</summary>
    private static List<CandidateResultDto> ReactionStudy(int loads = 1, int heights = 3, int pretensions = 9, bool located = true)
    {
        (string Id, double M)[] hs = heights == 3 ? [("H01", 20), ("H02", 25), ("H03", 30)]
            : Enumerable.Range(1, heights).Select(n => ($"H{n:00}", 20 + 2.5 * (n - 1))).ToArray();
        var results = new List<CandidateResultDto>();
        long run = 0;
        for (var l = 1; l <= loads; l++)
        for (var h = 1; h <= heights; h++)
        for (var p = 1; p <= pretensions; p++)
        {
            run++;
            if (h == heights && p == 8) continue;                     // this iteration never produced a result
            var key = $"L{l:00}_H{h:00}_P{p:00}";
            double fzInner = 161900 + 2000 * (h - 1) + 300 * (p - 1) + 20000 * (l - 1), fzOuter = -(3500 + 1500 * (h - 1) + 2000 * (p - 1));
            double fxInner = 24300 + 500 * (h - 1) + 100 * (p - 1), fxOuter = -14900 - 200 * (h - 1);
            IEnumerable<SupportReactionPointResultDto> Point(string name, int sequence, double x, double fx, double fz, bool inner) =>
                new[] { "Max", "Min" }.Select(step => new SupportReactionPointResultDto(name, sequence, located ? x : sequence * 10, 0, -20, step, 0,
                    fx, 0, fz + (inner && step == "Max" ? 10000 : 0), 0, 0, 0, ResultValueStatus.Valid));
            var left = new SupportReactionGroupResultDto("Left", "[O]-FoundationLeft", "NL CASE-7",
                [.. Point("200", 1, -60, fxOuter, fzOuter, false), .. Point("198", 2, -50, fxInner, fzInner, true)]);
            var right = new SupportReactionGroupResultDto("Right", "[O]-FoundationRight", "NL CASE-7",
                [.. Point("199", 1, 50, -fxInner, fzInner + 400, true), .. Point("205", 2, 60, -fxOuter, fzOuter, false)]);
            results.Add(new CandidateResultDto(key, $@"C:\Study\{key}.sdb", @"C:\base.sdb", "COT.sdb", IterationStudyMode.Combined,
                new CandidateParametersDto(run, key, $"L{l:00}", hs[h - 1].Id, $"P{p:00}", 5, $"DL 10 + LL {5 * l} kPa", hs[h - 1].M, 0, null, $"P{p:00} · mixed"),
                DateTimeOffset.Now, DateTimeOffset.Now, DateTimeOffset.Now, CandidateExtractionStatus.ExtractedOk, [], [], [], [], [left, right]));
        }
        return results;
    }

    private static void SummaryData()
    {
        var generator = new SupportReactionReportGenerator();
        var dto = generator.Build(ReactionStudy(), "COT.sdb", IterationStudyMode.Combined);
        SupportReactionGroupReportDto left = dto.Groups[0], right = dto.Groups[1];
        Check(left is { FoundationSide: "Left", Summary: { InnerPoint: "198", OuterPoint: "200" } } &&
              right is { FoundationSide: "Right", Summary: { InnerPoint: "199", OuterPoint: "205" } },
            "each foundation's inner support is the one towards the other foundation, read from the stored coordinates — Left 198 / 200, Right 199 / 205");
        Check(left.Summary!.Candidates.Count == 26 && left.Summary.Candidates.Select(c => c.IterationKey).SequenceEqual(left.Series.Select(s => s.IterationKey)) &&
              !left.Series.Any(s => s.IterationKey == "L01_H03_P08"),
            "every available iteration has an inner / outer / net reading; the missing iteration is absent, not zero");

        var step = left.Summary.Candidates.Single(c => c.IterationKey == "L01_H01_P01").Steps.Single(s => s.StepType == "Min");
        Check(step is { StepNumber: 0, Fz: { Inner: 161900, Outer: -3500, Net: 158400 }, Fx: { Inner: 24300, Outer: -14900, Net: 9400 } },
            "the net is the algebraic sum of the two joints — Fx +24.3 + (−14.9) = +9.4 MN, not 39.2 — with the stored signs kept");
        Check(dto.Groups.All(g => g.Summary!.Candidates.Zip(g.Series).All(pair => pair.First.Steps.All(s =>
              {
                  double? Raw(string point, Func<SupportReactionRowDto, double?> value) =>
                      value(pair.Second.Rows.Single(r => r.PointName == point && r.StepType == s.StepType && r.StepNumber == s.StepNumber));
                  return s.Fz!.Inner == Raw(g.Summary.InnerPoint, r => r.Fz) && s.Fz.Outer == Raw(g.Summary.OuterPoint, r => r.Fz) &&
                         s.Fx!.Inner == Raw(g.Summary.InnerPoint, r => r.Fx) && s.Fx.Outer == Raw(g.Summary.OuterPoint, r => r.Fx) &&
                         s.Fz.Net == s.Fz.Inner + s.Fz.Outer && s.Fx.Net == s.Fx.Inner + s.Fx.Outer;
              }))),
            "every summary value is the stored row of that point, iteration and result step; only Σ = inner + outer is derived");
        var max = left.Summary.Candidates[0].Steps.Single(s => s.StepType == "Max");
        var rightStep = right.Summary!.Candidates[0].Steps.Single(s => s.StepType == "Min");
        Check(max.Fz is { Inner: 171900, Net: 168400 } && rightStep is { Fz.Inner: 162300, Fx: { Inner: -24300, Outer: 14900, Net: -9400 } },
            "result steps and foundation sides are kept apart — the Right side is read from its own rows, not mirrored from the Left");

        SupportReactionRowDto Row(string point, string status, double? fx, double? fz) => new(point, 1, 0, 0, 0, "Max", 0, fx, 0, fz, 0, 0, 0, status);
        var lost = SupportReactionSummary.Summarise("K", [Row("198", "Valid", 10, 100), Row("200", "NoResult", -5, -20)], "198", "200");
        var partial = SupportReactionSummary.Summarise("K", [Row("198", "Valid", null, 100), Row("200", "Valid", -5, -20)], "198", "200");
        Check(lost.Steps.Count == 0 && partial.Steps.Single() is { Fx: null, Fz: { Inner: 100, Outer: -20, Net: 80 } } &&
              SupportReactionSummary.Pair(1, double.NaN) is null,
            "a pair with a missing or invalid support has no value — the net is never a sum over one joint, and never 0");

        var unlocated = generator.Build(ReactionStudy(located: false), "COT.sdb", IterationStudyMode.Combined);
        var oneSide = generator.Build(ReactionStudy().Select(r => r with { SupportReactionGroups = [r.SupportReactionGroups![0]] }).ToArray(), "COT.sdb", IterationStudyMode.Combined);
        Check(unlocated.Groups.All(g => g.Summary is null) && oneSide.Groups.Single().Summary is null,
            "supports that cannot be told apart from their coordinates get no inner / outer role — never a guessed one");
    }

    private static void SummaryViews()
    {
        var generator = new SupportReactionReportGenerator();
        var html = generator.BuildHtml(generator.Build(ReactionStudy(), "COT.sdb", IterationStudyMode.Combined));
        Check(html.Contains("<option value=\"summary\">Summary</option><option value=\"supports\">Individual supports</option>") &&
              !Regex.IsMatch(html, "<option[^>]*>\\s*By Candidate") && html.Contains("View by Candidate") && html.Contains("← Summary") &&
              html.Contains("Pretension Candidate") && html.Contains("Truss Height") && html.Contains("Foundation Side <select id=\"group\">") &&
              !html.Contains("http://") && !html.Contains("https://"),
            "Summary is the default view; By Candidate is a drill-down, not a tab beside it");

        // the report's own script, on the default page
        var page = new ReportPage(html);
        var panel = page.Html("panels");
        Check(page.RequestedIds.All(page.StaticIds.Contains) && page.Value("view") == "summary" && page.Value("group") == "0" &&
              panel.Contains("<span class=\"cname\">Support Reactions</span><span class=\"cview\">Summary</span>") &&
              panel.Contains("data-go=\"candidate\">View by Candidate</button>") &&
              page.Eval("__els['component'].parentElement.hidden") == "true" && page.Eval("__els['band'].parentElement.hidden") == "true" &&
              page.Eval("__els['step'].parentElement.hidden") == "false" && page.Value("step") == "Min · 0",
            "the report opens on the Support Reactions summary of the Left foundation, with the generic component / band controls out of the way");
        int kpis = panel.IndexOf("class=\"kpis\"", StringComparison.Ordinal), fzCard = panel.IndexOf("data-card=\"fz|Left|L01\"", StringComparison.Ordinal),
            fxCard = panel.IndexOf("data-card=\"fx|Left|L01\"", StringComparison.Ordinal);
        Check(kpis > 0 && kpis < fzCard && fzCard < fxCard && !panel.Contains("|Right|") && Count(panel, "data-card=") == 2 &&
              panel.Contains("Vertical Reaction Fz – Left Foundation") && panel.Contains("Horizontal Reaction Fx – Left Foundation") &&
              panel.Contains("Left Foundation: Inner Pt 198, Outer Pt 200 · NL CASE-7 · Min · 0"),
            "summary order: governing KPI cards, the vertical reaction chart, then the horizontal reaction chart — one foundation side at a time");

        // KPI cards: governing value, where it occurs, over both foundation sides
        string Kpi(string source, int index) => Regex.Match(source, $"<div class=\"kpi\" data-kpi=\"{index}\".*?</div></div>", RegexOptions.Singleline).Value;
        Check(Kpi(panel, 0).Contains("Max Inner Compression") && Kpi(panel, 0).Contains("<div class=\"kv\">168.7 <span>MN</span></div><div class=\"kw\">P09 · H30 · Right</div><div class=\"kd\">Inner · Pt 199 · Fz</div>") &&
              Kpi(panel, 0).Contains("data-cand=\"L01_H03_P09\"") && Kpi(panel, 0).Contains("Right: Fz = +168,700 kN"),
            "KPI Max Inner Compression = max Fz of an inner support, with its P, H and foundation side — here the Right side alone");
        Check(Kpi(panel, 1).Contains("Max Outer Uplift") && Kpi(panel, 1).Contains("<div class=\"kv\">22.5 <span>MN</span></div><div class=\"kw\">P09 · H30 · Left/Right</div><div class=\"kd\">Outer · Pt 200 / Pt 205 · Fz</div>") &&
              Kpi(panel, 1).Contains("Left: Fz = −22,500 kN · Right: Fz = −22,500 kN"),
            "KPI Max Outer Uplift = max |Fz| of an outer support in uplift; both sides are named when they read the same");
        Check(Kpi(panel, 2).Contains("Max Individual Horizontal Reaction") && Kpi(panel, 2).Contains("<div class=\"kv\">26.1 <span>MN</span></div><div class=\"kw\">P09 · H30 · Left/Right</div><div class=\"kd\">Inner · Pt 198 / Pt 199 · Fx</div>") &&
              Kpi(panel, 3).Contains("Max Net Horizontal Reaction per Side") && Kpi(panel, 3).Contains("<div class=\"kv\">10.8 <span>MN</span></div><div class=\"kw\">P09 · H30 · Left/Right</div><div class=\"kd\">Σ Net · 198+200 / 199+205 · Fx</div>"),
            "KPIs Max Individual Horizontal Reaction = max(|Fx inner|, |Fx outer|) and Max Net Horizontal Reaction per Side = max |Fx inner + Fx outer|");

        // vertical reaction chart
        string Chart(string source, string comp) => Regex.Match(source, $"<svg class=\"cchart\" data-chart=\"{comp}\".*?</svg>", RegexOptions.Singleline).Value;
        Match Bar(string chart, string key) => Regex.Match(chart, $"<rect data-bar=\"{Regex.Escape(key)}\" x=\"([\\d.]+)\" y=\"([\\d.]+)\" width=\"([\\d.]+)\" height=\"([\\d.]+)\" ([^/]*)/>");
        var fz = Chart(panel, "fz");
        var zero = Number(Regex.Match(fz, "<line class=\"czero\" x1=\"[\\d.]+\" x2=\"[\\d.]+\" y1=\"([\\d.]+)\""), 1);
        Match inner = Bar(fz, "fz|P01|H01|inner"), outer = Bar(fz, "fz|P01|H01|outer"), net = Bar(fz, "fz|P01|H01|net"), next = Bar(fz, "fz|P01|H02|inner");
        Check(Count(panel, "<svg class=\"cchart\" data-chart=\"fz\"") == 1 && Count(fz, "<rect data-bar=\"fz|") == 26 * 3 &&
              Count(fz, "class=\"chh\"") == 27 && new[] { ">H20<", ">H25<", ">H30<" }.All(fz.Contains) &&
              Regex.Matches(fz, "<text class=\"cpl\"[^>]*>(P\\d+)<").Select(m => m.Groups[1].Value).SequenceEqual(Enumerable.Range(1, 9).Select(p => $"P{p:00}")),
            "the chart runs P01 → P09 and, within each, one slot per truss height holding three bars");
        double pitch = Number(outer, 1) - Number(inner, 1), triple = Number(next, 1) - Number(inner, 1);
        Check(pitch > 0 && Math.Abs(Number(net, 1) - Number(outer, 1) - pitch) < 0.11 && triple > 3 * pitch + 1 &&
              Number(Bar(fz, "fz|P02|H01|inner"), 1) - Number(Bar(fz, "fz|P01|H03|net"), 1) > triple - 2 * pitch + 1,
            "the three bars sit side by side — Inner | Outer | Σ Net — with air after each height and a wider gap before the next pretension candidate");
        Check(zero > 0 && Math.Abs(Number(inner, 2) + Number(inner, 4) - zero) < 0.11 && Number(inner, 2) < zero &&
              Math.Abs(Number(outer, 2) - zero) < 0.11 && Number(outer, 4) > 0 &&
              Math.Abs(Number(net, 2) + Number(net, 4) - zero) < 0.11 && Number(net, 4) < Number(inner, 4),
            "about a visible zero line: inner compression up, outer uplift down, and the net as its own bar from zero — not stacked on the others");
        Check(inner.Groups[5].Value.Contains("fill=\"#1f4788\"") && outer.Groups[5].Value.Contains("fill=\"#ff7f0e\"") &&
              Regex.IsMatch(net.Groups[5].Value, "fill=\"url\\(#nh\\d+\\)\" stroke=\"#475467\"") && fz.Contains("<pattern id=\"nh") &&
              panel.Contains("Inner Support</span>") && panel.Contains("Outer Support</span>") && panel.Contains("Σ Net = Inner + Outer</span>"),
            "inner and outer are solid series; the derived Σ Net is hatched and outlined; the legend names the three categories");
        Check(Count(fz, "<tspan class=\"rr\">Inner</tspan><tspan class=\"rj\"> Pt 198</tspan>") == 26 &&
              Count(fz, "<tspan class=\"rr\">Outer</tspan><tspan class=\"rj\"> Pt 200</tspan>") == 26 &&
              Count(fz, "<tspan class=\"rr\">Σ Net</tspan><tspan class=\"rj\"> 198+200</tspan>") == 26,
            "every bar carries its role and its SAP joint: Inner Pt 198, Outer Pt 200, Σ Net 198+200");
        Check(Regex.IsMatch(fz, "<text class=\"rv\"[^>]*text-anchor=\"start\">\\+161\\.9</text>") && Regex.IsMatch(fz, "<text class=\"rv\"[^>]*text-anchor=\"end\">−3\\.5</text>") &&
              Regex.IsMatch(fz, "<text class=\"rv rvn\"[^>]*>\\+158\\.4</text>"),
            "values are signed MN to one decimal — above a positive bar, below a negative one — with the net the prominent one");
        Check(!fz.Contains("data-bar=\"fz|P08|H03") && Count(fz, "class=\"cna\"") == 1 && fz.Contains("data-missing=\"fz|P08|H03\"") &&
              fz.Contains("data-tip=\"P08 · H30 · Left Foundation\nResult unavailable\"") && !fz.Contains(">+0.0<") && !fz.Contains(">0.0<"),
            "P08-H30 stays blank in the chart: no bars, no labels, no zero");
        Check(fz.Contains("data-tip=\"P01 · H20 · Left Foundation\nInner · Pt 198\nFz: +161.9 MN\nBehaviour: Compression\nSource: +161,900 kN · Min · 0\nIteration L01_H01_P01 · DL 10 + LL 5 kPa\nNL CASE-7\"") &&
              fz.Contains("Outer · Pt 200\nFz: −3.5 MN\nBehaviour: Uplift\nSource: −3,500 kN") &&
              fz.Contains("Σ Net · Pt 198 + Pt 200\nFz net: +158.4 MN\nΣ Fz = Fz198 + Fz200 = +161.9 + (−3.5)\nSource: +158,400 kN"),
            "tooltips trace each bar: candidate, height, side, role and joint, signed value, behaviour, source kN, step, iteration and case");

        // horizontal reaction chart: the same structure, signs kept
        var fx = Chart(panel, "fx");
        Check(Count(fx, "<rect data-bar=\"fx|") == 26 * 3 && fx.Contains(">+24.3<") && fx.Contains(">−14.9<") && Regex.IsMatch(fx, "<text class=\"rv rvn\"[^>]*>\\+9\\.4</text>") &&
              !fx.Contains(">+39.2<") && !fx.Contains("39.2 MN") && fx.Contains("Fx net: +9.4 MN\nΣ Fx = Fx198 + Fx200 = +24.3 + (−14.9)") && !fx.Contains("Behaviour:") &&
              fx.Contains("data-missing=\"fx|P08|H03\"") && Count(fx, "<tspan class=\"rr\">Inner</tspan><tspan class=\"rj\"> Pt 198</tspan>") == 26,
            "Horizontal Reaction Fx uses the same chart: opposing inner and outer reactions with their much smaller algebraic net");
        Match fxInner = Bar(fx, "fx|P01|H01|inner"), fxOuter = Bar(fx, "fx|P01|H01|outer"), fxNet = Bar(fx, "fx|P01|H01|net");
        var fxZero = Number(Regex.Match(fx, "<line class=\"czero\" x1=\"[\\d.]+\" x2=\"[\\d.]+\" y1=\"([\\d.]+)\""), 1);
        Check(Number(fxInner, 2) < fxZero && Math.Abs(Number(fxOuter, 2) - fxZero) < 0.11 && Number(fxNet, 4) < Number(fxOuter, 4) && Number(fxNet, 4) < Number(fxInner, 4),
            "Fx keeps its sign about the zero line; the net bar is shorter than either individual reaction");
        Check(panel.Contains("Reaction values — the source of every bar") && Count(panel, "<tr><td>Left</td><td class=\"n\">198</td><td class=\"n\">200</td>") == 27 &&
              panel.Contains("<td>L01_H01_P01</td><td class=\"n\">+24,300</td><td class=\"n\">−14,900</td><td class=\"n\">+9,400</td><td class=\"n\">+161,900</td><td class=\"n\">−3,500</td><td class=\"n\">+158,400</td></tr>") &&
              Count(panel, "colspan=\"6\">Result unavailable</td>") == 1,
            "a values table lists the source of every bar in kN, including the unavailable iteration");

        // the other foundation side, both sides, another result step
        page.Run("__els['group'].value = '1'; __els['group'].onchange();");
        var right = page.Html("panels");
        Check(right.Contains("data-card=\"fz|Right|L01\"") && !right.Contains("|Left|") && right.Contains("Vertical Reaction Fz – Right Foundation") &&
              Count(Chart(right, "fz"), "<tspan class=\"rr\">Inner</tspan><tspan class=\"rj\"> Pt 199</tspan>") == 26 && Chart(right, "fz").Contains("> Pt 205<") &&
              Chart(right, "fz").Contains("> 199+205<") && !Chart(right, "fz").Contains("Pt 198") && Chart(right, "fz").Contains(">+162.3<") &&
              Chart(right, "fx").Contains(">−24.3<") && Chart(right, "fx").Contains(">+14.9<") && Regex.IsMatch(Chart(right, "fx"), "<text class=\"rv rvn\"[^>]*>−9\\.4</text>") &&
              Kpi(right, 0) == Kpi(panel, 0),
            "Foundation Side switches to the Right foundation with its own joints, values and signs; the KPI cards keep covering both sides");
        page.Run("__els['group'].value = 'all'; __els['group'].onchange();");
        var both = page.Html("panels");
        Check(Regex.Matches(both, "data-card=\"([^\"]+)\"").Select(m => m.Groups[1].Value).SequenceEqual(["fz|Left|L01", "fz|Right|L01", "fx|Left|L01", "fx|Right|L01"]) &&
              !both.Contains("198+199") && !both.Contains("200+205"),
            "Left + Right shows the two sides as separate charts — joints of different sides are never combined into one bar");
        page.Run("__els['group'].value = '0'; __els['group'].onchange(); __els['step'].value = 'Max · 0'; __els['step'].onchange();");
        Check(Chart(page.Html("panels"), "fz").Contains(">+171.9<") && Chart(page.Html("panels"), "fz").Contains(">+168.4<") && Kpi(page.Html("panels"), 0).Contains("178.7"),
            "the summary reads the selected result step");
        page.Run("__els['step'].value = 'Min · 0'; __els['step'].onchange();");

        // drill-down
        page.Run("window.reactionReport.summary('candidate')");
        var candidate = page.Html("panels");
        Check(candidate.Contains("data-sum-view=\"candidate\"") && candidate.Contains("<span class=\"cview\">By Candidate</span>") &&
              candidate.Contains("data-go=\"summary\">← Summary</button>") && !candidate.Contains("Loading Candidate") &&
              candidate.Contains("Pretension Candidate <select data-pick=\"pretensionId\"><option value=\"P01\" selected>P01</option>") &&
              candidate.Contains("Truss Height <select data-pick=\"heightId\"><option value=\"H01\" selected>H20</option><option value=\"H02\">H25</option>") &&
              candidate.Contains("Support Reactions – P01 · H20<span>L01 · DL 10 + LL 5 kPa · Iteration L01_H01_P01</span>") && !candidate.Contains("data-bar=") &&
              page.Eval("__els['group'].parentElement.hidden") == "true",
            "View by Candidate opens the drill-down with ← Summary, a Pretension Candidate selector and a Truss Height selector");
        var joints = Regex.Matches(candidate, "<text class=\"sjoint\" x=\"([\\d.]+)\"[^>]*>(Pt \\d+)<").Select(m => (X: Number(m, 1), Point: m.Groups[2].Value)).ToArray();
        Check(candidate.Contains("data-chart=\"schematic\"") && joints.Select(j => j.Point).SequenceEqual(["Pt 200", "Pt 198", "Pt 199", "Pt 205"]) &&
              joints.Zip(joints.Skip(1)).All(pair => pair.First.X < pair.Second.X) &&
              candidate.Contains(">Left Foundation<") && candidate.Contains(">Right Foundation<"),
            "the schematic shows both sides left to right: Left outer Pt 200, inner Pt 198 — Right inner Pt 199, outer Pt 205");
        Check(candidate.Contains("data-arrow=\"fz|Left|outer|down\"") && candidate.Contains(">Fz = −3.5 MN</text><text class=\"sbeh up\"") && candidate.Contains(">Uplift</text>") &&
              candidate.Contains("data-arrow=\"fz|Left|inner|up\"") && candidate.Contains(">Fz = +161.9 MN</text><text class=\"sbeh\"") &&
              Regex.IsMatch(candidate, "data-arrow=\"fx\\|Left\\|inner\\|right\"[^>]*>Fx = \\+24\\.3 MN<") && Regex.IsMatch(candidate, "data-arrow=\"fx\\|Left\\|outer\\|left\"[^>]*>Fx = −14\\.9 MN<") &&
              Regex.IsMatch(candidate, "data-arrow=\"fx\\|Right\\|inner\\|left\"[^>]*>Fx = −24\\.3 MN<") && Regex.IsMatch(candidate, "data-arrow=\"fx\\|Right\\|outer\\|right\"[^>]*>Fx = \\+14\\.9 MN<") &&
              Regex.IsMatch(candidate, "data-arrow=\"fz\\|Right\\|inner\\|up\"[^>]*>Fz = \\+162\\.3 MN<"),
            "each support shows a vertical Fz and a horizontal Fx arrow in its actual direction with the signed value — the Right side from its own results");
        Check(candidate.Contains(">Net Left · Pt 198 + Pt 200<") && Regex.IsMatch(candidate, "data-net=\"Left\"[^>]*>↑ ΣFz = \\+158\\.4 MN  ·  → ΣFx = \\+9\\.4 MN<") &&
              candidate.Contains(">Net Right · Pt 199 + Pt 205<") && Regex.IsMatch(candidate, "data-net=\"Right\"[^>]*>↑ ΣFz = \\+158\\.8 MN  ·  ← ΣFx = −9\\.4 MN<"),
            "below each side: its net ΣFz and ΣFx");
        var tableRows = Regex.Matches(Regex.Match(candidate, "<table class=\"ctable\" data-table=\"candidate\">.*?</table>", RegexOptions.Singleline).Value, "<tr[^>]*>(.*?)</tr>")
            .Select(m => Regex.Replace(m.Groups[1].Value, "<[^>]+>", "|").Replace("||", "|").Trim('|')).ToArray();
        Check(tableRows.SequenceEqual([
                  "Foundation|Role|Point|Fx [MN]|Fz [MN]|Vertical behaviour",
                  "Left|Outer|200|−14.9|−3.5|Uplift", "Left|Inner|198|+24.3|+161.9|Compression",
                  "Right|Inner|199|−24.3|+162.3|Compression", "Right|Outer|205|+14.9|−3.5|Uplift",
                  "Left|Σ Net|198 + 200|+9.4|+158.4|Compression", "Right|Σ Net|199 + 205|−9.4|+158.8|Compression"]) &&
              candidate.Contains("<td class=\"n\" title=\"−14,900 kN\">−14.9</td>"),
            "the reaction table lists Foundation, Role, Point, Fx and Fz for the four joints, then the Left and Right resultants");
        page.Run("window.reactionReport.summary('candidate', { pretensionId: 'P03', heightId: 'H02' })");
        candidate = page.Html("panels");
        Check(candidate.Contains("Support Reactions – P03 · H25<span>L01 · DL 10 + LL 5 kPa · Iteration L01_H02_P03</span>") &&
              candidate.Contains("<option value=\"P03\" selected>") && candidate.Contains("<option value=\"H02\" selected>") && candidate.Contains(">Fz = +164.5 MN<"),
            "the Pretension Candidate and Truss Height selectors pick the iteration shown");
        page.Run("window.reactionReport.summary('candidate', { pretensionId: 'P08', heightId: 'H03' })");
        Check(page.Html("panels").Contains("No result for L01 · P08 · H30") && !page.Html("panels").Contains("data-chart=\"schematic\"") && !page.Html("panels").Contains("Fz = "),
            "by candidate for P08-H30 says the iteration has no result — no schematic, no values");
        page.Run("window.reactionReport.summary('summary')");
        Check(page.Html("panels").Contains("data-sum-view=\"summary\"") && Count(Chart(page.Html("panels"), "fz"), "<rect data-bar=") == 78 &&
              page.Eval("__els['group'].parentElement.hidden") == "false", "← Summary returns to the summary and its Foundation Side selector");

        // paper: the charts are laid out again for the sheet, and back
        page.Fire("beforeprint");
        var paper = Regex.Match(page.Html("panels"), "data-chart=\"fz\" viewBox=\"0 0 (\\d+) ").Groups[1].Value;
        page.Fire("afterprint");
        Check(paper == "1022" && page.Html("panels").Contains("data-chart=\"fz\" viewBox=\"0 0 1100 "),
            "for printing the summary charts are laid out at the sheet width (A4 landscape), then restored");

        // the existing views stay available
        page.Run("__els['view'].value = 'supports'; __els['view'].onchange();");
        Check(page.Html("panels").Contains("Left foundation · Fz") && !page.Html("panels").Contains("data-sum-view") &&
              page.Eval("__els['component'].parentElement.hidden") == "false" && page.Eval("__els['band'].parentElement.hidden") == "false",
            "Individual supports / Foundation totals / Candidate matrix remain available under View, with their component and band controls");

        // deep links
        var linked = new ReportPage(html, "#group=Right");
        Check(linked.Value("group") == "1" && linked.Value("view") == "summary" && linked.Html("panels").Contains("data-card=\"fz|Right|L01\""),
            "#group=Right opens the summary of the Right foundation");
        var drilled = new ReportPage(html, "#detail=candidate&pretension=P03&height=H02");
        Check(drilled.Html("panels").Contains("data-sum-view=\"candidate\"") && drilled.Html("panels").Contains("Support Reactions – P03 · H25"),
            "#detail=candidate&pretension=P03&height=H02 opens the drill-down on that iteration");
        Check(new ReportPage(html, "#view=totals&group=all").Html("panels").Contains("Right foundation · Fz") && new ReportPage(html, "#group=all").Value("group") == "all",
            "the export URL still selects the generic views and Left + Right");

        // more loads: one card per loading candidate, never mixed
        var two = new ReportPage(generator.BuildHtml(generator.Build(ReactionStudy(loads: 2), "COT.sdb", IterationStudyMode.Combined)));
        Check(Regex.Matches(two.Html("panels"), "data-card=\"([^\"]+)\"").Select(m => m.Groups[1].Value).SequenceEqual(["fz|Left|L01", "fz|Left|L02", "fx|Left|L01", "fx|Left|L02"]) &&
              Kpi(two.Html("panels"), 0).Contains("<div class=\"kw\">L02 · P09 · H30 · Right</div>") && two.Html("panels").Contains("L02 · DL 10 + LL 10 kPa · 26 iteration(s)"),
            "with several loading candidates each gets its own chart, and a KPI names its load");
        two.Run("window.reactionReport.summary('candidate', { loadId: 'L02' })");
        Check(two.Html("panels").Contains("Loading Candidate <select data-pick=\"loadId\">") && two.Html("panels").Contains("<option value=\"L02\" selected>L02 · DL 10 + LL 10 kPa</option>") &&
              two.Html("panels").Contains("Iteration L02_H01_P01") && two.Html("panels").Contains(">Fz = +181.9 MN<"),
            "by candidate adds a Loading Candidate selector when the study has more than one load");

        // more heights than one row can label: the chart wraps, each row captioned
        var five = new ReportPage(generator.BuildHtml(generator.Build(ReactionStudy(heights: 5), "COT.sdb", IterationStudyMode.Combined))).Html("panels");
        Check(Count(five, "<svg class=\"cchart\" data-chart=\"fz\"") == 2 && five.Contains("class=\"ccard multi\" data-card=\"fz|Left|L01\"") &&
              five.Contains(">Fz · L01 · Left Foundation · P01 – P05<") && five.Contains(">Fz · L01 · Left Foundation · P06 – P09<") &&
              Count(Chart(five, "fz"), "<rect data-bar=") == 5 * 5 * 3 && five.Contains(">H22.5<") && five.Contains("data-missing=\"fz|P08|H05\""),
            "five truss heights: the chart wraps to a second row instead of shrinking its bars and labels");

        // few candidates: room for the two-line label
        var few = new ReportPage(generator.BuildHtml(generator.Build(ReactionStudy(heights: 2, pretensions: 2), "COT.sdb", IterationStudyMode.Combined))).Html("panels");
        Check(Regex.IsMatch(few, "<text class=\"rr\" x=\"([\\d.]+)\" y=\"[\\d.]+\" text-anchor=\"middle\">Inner</text><text class=\"rj\" x=\"\\1\" y=\"[\\d.]+\" text-anchor=\"middle\">Pt 198</text>") &&
              Regex.IsMatch(few, "<text class=\"rj\"[^>]*>198\\+200</text>") && !Chart(few, "fz").Contains("rotate(-90)\" text-anchor=\"end\"><tspan"),
            "with room to spare the label is two-level under each bar: the role, then the joint");

        // supports without a resolvable inner / outer role: the report is as before
        var plain = new ReportPage(generator.BuildHtml(generator.Build(ReactionStudy(located: false), "COT.sdb", IterationStudyMode.Combined)));
        Check(plain.Value("view") == "supports" && plain.Eval("__els['view'].options[0].value") == "supports" && plain.Value("group") == "all" &&
              plain.Html("panels").Contains("Left foundation · Fz") && plain.Html("panels").Contains("Right foundation · Fz"),
            "without inner / outer roles the report opens on Individual supports for both foundations, as before");
    }

    private static double Number(Match match, int group) => double.Parse(match.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
