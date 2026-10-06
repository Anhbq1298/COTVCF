using COT_VCF.Application.Results;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;
using System.IO;
using System.Text.RegularExpressions;

internal static class MemberForceReportChecks
{
    public static void Run()
    {
        var results = new List<CandidateResultDto>();
        for (var i = 1; i <= 60; i++)
        {
            var key = $"L{((i - 1) % 4) + 1:00}_H01_P{((i - 1) % 15) + 1:00}";
            MemberForceStationResultDto Station(string name, int sequence, string element, double station, double p) => new(
                name, sequence, element, station, station, sequence - 1 + station, "Step", 1,
                p, p * .08, p * -.04, p * .01, p * .2, p * -.3, ResultValueStatus.Valid);
            var top = new MemberForceGroupResultDto("TopChords", "[O]-TopChords", "ULS",
                [Station("TC-01", 1, "TC-01-1", 0, -800 - i), Station("TC-01", 1, "TC-01-1", .5, -850 - i),
                 Station("TC-02", 2, "TC-02-1", 0, -1000 - i), Station("TC-02", 2, "TC-02-1", .5, -1050 - i)]);
            var bottom = new MemberForceGroupResultDto("BottomChords", "[O]-BottomChords", "ULS",
                [Station("BC-01", 1, "BC-01-1", 0, 700 + i), Station("BC-01", 1, "BC-01-1", .5, 750 + i),
                 Station("BC-02", 2, "BC-02-1", 0, 900 + i), Station("BC-02", 2, "BC-02-1", .5, 950 + i)]);
            var vierendeel = new MemberForceGroupResultDto("VierendeelMembers", "[O]-VierendeelMembers", "ULS",
                [Station("VT-01", 1, "VT-01-1", 0, -120 - i), Station("VT-01", 1, "VT-01-1", .5, -100 - i),
                 Station("VT-02", 2, "VT-02-1", 0, 140 + i), Station("VT-02", 2, "VT-02-1", .5, 160 + i)]);
            var mega = new MemberForceGroupResultDto("MegaColumns", "[O]-MegaColumns", "ULS",
                [Station("MC-01", 1, "MC-01-1", 0, -2400 - i), Station("MC-01", 1, "MC-01-1", .5, -2450 - i),
                 Station("MC-02", 2, "MC-02-1", 0, -2300 - i), Station("MC-02", 2, "MC-02-1", .5, -2350 - i)]);
            var parameters = new CandidateParametersDto(i, key, key[..3], "H01", key[^3..],
                2.5, "DL 1.0 + LL 2.5 kPa", 20, 0, 500, "pretension");
            results.Add(new CandidateResultDto(key, $@"C:\Study\{key}.sdb", @"C:\base.sdb", "COT.sdb",
                IterationStudyMode.FixHeight, parameters, DateTimeOffset.Now, DateTimeOffset.Now, DateTimeOffset.Now,
                CandidateExtractionStatus.ExtractedOk, [], [], [], [top, bottom, vierendeel, mega]));
        }

        var generator = new MemberForceReportGenerator();
        var dto = generator.Build(results, "COT.sdb", IterationStudyMode.FixHeight);
        Check(dto.Groups.Count == 4 && dto.Groups.All(group => group.Series.Count == 60) &&
              dto.Groups.Select(group => group.GroupName).ToHashSet(StringComparer.Ordinal)
                  .SetEquals(["TopChords", "BottomChords", "VierendeelMembers", "MegaColumns"]),
            "member-force report retains every candidate in chord, Vierendeel, and mega-column groups");
        Check(dto.Groups.Single(group => group.GroupName == "TopChords").Series[0].Stations[0].P < 0 &&
              dto.Groups.Single(group => group.GroupName == "BottomChords").Series[0].Stations[0].P > 0,
            "member-force report preserves signed compression and tension");
        var html = generator.BuildHtml(dto);
        Check(html.Contains("station-by-station envelope") && html.Contains("Station matrix") &&
              html.Contains("individualProfileLimit", StringComparison.OrdinalIgnoreCase) &&
              !html.Contains("http://") && !html.Contains("https://") && !html.Contains("cdn."),
            "member-force HTML is self-contained and exposes scalable profile, envelope, and matrix views");
        Check(html.Contains("className='ms'") && html.Contains("className='pop'") && html.Contains("Reset filters"),
            "member-force HTML uses compact multi-select L/H/P filters");
        Check(html.Contains("id=\"groupFilter\"") && html.Contains("chosenGroups") &&
              html.Contains("<select id=\"group\" hidden"),
            "member-force HTML supports any combination of member groups instead of only one or all");
        Check(html.Contains("station-hit") && html.Contains("Frame object:") && html.Contains("Analysis element:"),
            "member-force chart exposes exact station values and SAP identifiers on hover");
        Check(html.Contains("Start + middle + end per element") && html.Contains("All SAP stations") &&
              html.Contains("elements.flatMap") && html.Contains("sampleLabel:label"),
            "member-force chart defaults to three positions per SAP analysis element without discarding detailed stations");
        Check(html.Contains("VierendeelMembers") && html.Contains("MegaColumns"),
            "member-force HTML exposes Vierendeel and mega-column groups in the shared group selector");
        Check(html.Contains("Candidate definitions") && html.Contains("candidate-pretension-matrix"),
            "member-force HTML includes the shared compact L/H/P candidate definitions");

        var folder = Path.Combine(Path.GetTempPath(), "cotvcf_mf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var writer = new ConsolidatedResultWriter();
            foreach (var result in results.Take(2)) writer.Add(result);
            var workbook = writer.Write(folder, [], "", "COT.sdb", @"C:\base.sdb", IterationStudyMode.FixHeight,
                memberGroups: [new("[O]-TopChords", OutputGroupObjectKind.Frame, ["TC-01", "TC-02"])],
                memberForceCase: "ULS");
            var roundTrip = ConsolidatedResultWriter.Read(workbook);
            var station = roundTrip[0].MemberForceGroups!.Single(group => group.GroupName == "TopChords").Stations[1];
            Check(station.P == -851 && station.ObjectStationM == .5 && station.ElementName == "TC-01-1",
                "member-force workbook round-trips signed analysis-element station rows");
            Check(generator.RegenerateFromWorkbook(folder).EndsWith(MemberForceReportGenerator.ReportFileName),
                "member-force HTML regenerates from the workbook without SAP2000");
        }
        finally { try { Directory.Delete(folder, true); } catch { } }

        ChordSummaryData();
        ChordViews();
        MegaColumnSummary();
    }

    // ---------- Top / Bottom chord: governing force per iteration and one point per physical station ----------
    // ---------- Mega column: governing force per iteration only — no station anywhere ----------

    private static readonly (string Id, double M)[] ChordHeights = [("H01", 20), ("H02", 25), ("H03", 30)];

    private static double TopPeak(int height, int p) => -(150000 - 15000 * (height - 1) + 500 * p);       // compression, kN
    private static double BottomPeak(int height, int p) => 120000 - 12000 * (height - 1) - 300 * p;         // tension, kN
    private static double MegaPeak(int height, int p) => -(200000 + 10000 * (height - 1) + 400 * p);        // compression, kN

    /// <summary>P01–P09 × H20 / H25 / H30 under one load, H30 missing at P08. Each chord is two analysis
    /// elements meeting at station 10 m, read under a Max and a Min result step; the mega columns are two
    /// separate columns read the same way.</summary>
    private static List<CandidateResultDto> ChordStudy(bool summaryGroups = true)
    {
        var results = new List<CandidateResultDto>();
        long run = 0;
        for (var h = 1; h <= 3; h++)
        for (var p = 1; p <= 9; p++)
        {
            run++;
            if (h == 3 && p == 8) continue;                      // this iteration never produced a result
            var key = $"L01_H{h:00}_P{p:00}";
            MemberForceStationResultDto At(string frame, int sequence, double station, string step, double force) => new(
                frame, sequence, frame + "-1", station - (sequence - 1) * 10, station - (sequence - 1) * 10, station, step, 0,
                force, 0, 0, 0, 0, 0, ResultValueStatus.Valid);
            IEnumerable<MemberForceStationResultDto> Chord(string name, Func<double, int, string, double> force) =>
                new[] { "Max", "Min" }.SelectMany(step => new[]
                {
                    At(name + "-1", 1, 0, step, force(0, 1, step)), At(name + "-1", 1, 5, step, force(5, 1, step)), At(name + "-1", 1, 10, step, force(10, 1, step)),
                    At(name + "-2", 2, 10, step, force(10, 2, step)), At(name + "-2", 2, 15, step, force(15, 2, step)), At(name + "-2", 2, 20, step, force(20, 2, step)),
                });
            double top = TopPeak(h, p), bottom = BottomPeak(h, p);
            var groups = new List<MemberForceGroupResultDto>();
            if (summaryGroups)
            {
                // top: 80 % in the first element, the peak in the second, and 1 % more under the Min step at 15 m
                groups.Add(new("TopChord", "[O]-TopChord", "NL CASE-7", Chord("TC",
                    (x, element, step) => element == 1 ? top * .8 : x == 15 && step == "Min" ? top * 1.01 : top).ToArray()));
                // bottom: a little compression at both supports, half the peak in the first element, the peak in the second
                groups.Add(new("BottomChord", "[O]-BottomChord", "NL CASE-7", Chord("BC",
                    (x, element, _) => x is 0 or 20 ? -3000 : element == 1 ? bottom * .5 : bottom).ToArray()));
                // mega columns: 90 % in the first column; the second column governs, at its base under the Max step
                var mega = MegaPeak(h, p);
                groups.Add(new("MegaColumns", "[O]-MegaColumns", "NL CASE-7", Chord("MC",
                    (x, element, step) => element == 1 ? mega * .9 : x == 10 && step == "Max" ? mega : mega * .95).ToArray()));
            }
            groups.Add(new("VRM", "[O]-VRM", "NL CASE-7", [At("VRM01", 1, 0, "Max", -900 - p), At("VRM01", 1, 5, "Max", -950 - p)]));
            results.Add(new CandidateResultDto(key, $@"C:\Study\{key}.sdb", @"C:\base.sdb", "COT.sdb", IterationStudyMode.Combined,
                new CandidateParametersDto(run, key, "L01", ChordHeights[h - 1].Id, $"P{p:00}", 5, "DL 10 + LL 5 kPa", ChordHeights[h - 1].M, 0, null, $"P{p:00} · mixed"),
                DateTimeOffset.Now, DateTimeOffset.Now, DateTimeOffset.Now, CandidateExtractionStatus.ExtractedOk, [], [], [], groups));
        }
        return results;
    }

    private static void ChordSummaryData()
    {
        Check(MemberGroupSummary.Kind("TopChord") == "top" && MemberGroupSummary.Kind("[O]-Top Chords") == "top" &&
              MemberGroupSummary.Kind("BottomChord") == "bottom" && MemberGroupSummary.Kind("bottom_chords") == "bottom" &&
              MemberGroupSummary.Kind("MegaColumns") == "mega" && MemberGroupSummary.Kind("[O]-Mega Column") == "mega" &&
              MemberGroupSummary.Kind("VRM") is null && MemberGroupSummary.Kind("Columns") is null && MemberGroupSummary.Kind("MegaTruss") is null &&
              MemberGroupSummary.Kind("Chord") is null && MemberGroupSummary.Kind("TopBracing") is null,
            "only a group named as a top chord, bottom chord or mega columns gets a summary");

        var dto = new MemberForceReportGenerator().Build(ChordStudy(), "COT.sdb", IterationStudyMode.Combined);
        var top = dto.Groups.Single(g => g.GroupName == "TopChord");
        var bottom = dto.Groups.Single(g => g.GroupName == "BottomChord");
        Check(top.Summary is { Kind: "top", Title: "Top Chord", ByStation: true } && bottom.Summary is { Kind: "bottom", Title: "Bottom Chord", ByStation: true } &&
              dto.Groups.Single(g => g.GroupName == "VRM").Summary is null &&
              top.Summary.Candidates.Count == 26 && top.Summary.Candidates.Select(c => c.IterationKey).SequenceEqual(top.Series.Select(s => s.IterationKey)) &&
              !top.Series.Any(s => s.IterationKey == "L01_H03_P08"),
            "every available L × H × P iteration has a chord summary; the missing iteration is absent, not zero");

        var t = top.Summary!.Candidates.Single(c => c.IterationKey == "L01_H01_P01");
        Check(Close(t.GoverningKN, TopPeak(1, 1) * 1.01) && t is { StationM: 15, ElementName: "TC-2-1", FrameName: "TC-2", Step: "Min · 0" },
            "top chord governs on max |P| over every station and result step, keeping the signed source P and where it occurs");
        Check(t.Profile.Select(point => point[0]).SequenceEqual(new double[] { 0, 5, 10, 15, 20 }) &&
              Close(t.Profile[2][1], TopPeak(1, 1)) && Close(t.Profile[1][1], TopPeak(1, 1) * .8) && Close(t.Profile[3][1], TopPeak(1, 1) * 1.01),
            "top chord profile has one point per physical station — where two elements meet, the governing |P| is kept");

        var b = bottom.Summary!.Candidates.Single(c => c.IterationKey == "L01_H02_P03");
        Check(Close(b.GoverningKN, BottomPeak(2, 3)) && b is { StationM: 10, ElementName: "BC-2-1" } &&
              b.Profile.Count == 5 && Close(b.Profile[2][1], BottomPeak(2, 3)) && b.Profile[0][1] == -3000 && b.Profile[4][1] == -3000,
            "bottom chord governs on max tension P, and its profile keeps the actual result — compression at the supports included");

        MemberForceStationDto Row(string status, double p) => new("F", 1, "F-1", 0, 0, 0, "Max", 0, p, 0, 0, 0, 0, 0, status);
        var none = MemberGroupSummary.Summarise("top", "L01_H01_P01", [Row("NoResult", -5000), Row("Valid", double.NaN)]);
        Check(none is { GoverningKN: null, StationM: null, FrameName: null } && none.Profile.Count == 0,
            "an iteration without a valid station has no governing force and no profile — never 0");
    }

    private static void ChordViews()
    {
        var generator = new MemberForceReportGenerator();
        var html = generator.BuildHtml(generator.Build(ChordStudy(), "COT.sdb", IterationStudyMode.Combined));
        Check(html.Contains("<option value=\"summary\">Summary</option><option value=\"profiles\">All profiles</option>") &&
              !Regex.IsMatch(html, "<option[^>]*>\\s*By Station") && html.Contains("View by Station") && html.Contains("← Summary") &&
              html.Contains("Pretension Candidate") && !html.Contains("http://") && !html.Contains("https://"),
            "Summary is the default display; By Station is a drill-down, not a tab beside it");

        // the report's own script, on the default page: every group, Display = Summary
        var page = new ReportPage(html);
        var panels = page.Html("panels");
        Check(page.RequestedIds.All(page.StaticIds.Contains) && page.Value("view") == "summary" &&
              panels.Contains("data-sum-kind=\"top\" data-sum-view=\"summary\"") && panels.Contains("data-sum-kind=\"bottom\" data-sum-view=\"summary\"") &&
              panels.Contains("data-sum-kind=\"mega\" data-sum-view=\"summary\"") &&
              panels.Contains("<span class=\"title\">VRM · P</span>") && page.Eval("__els['component'].parentElement.hidden") == "false",
            "the report opens on the chord and mega-column summaries; any other group keeps its profile chart and controls");

        // choosing Top Chord in Group
        page.Run("__els['group'].value = '2'; __els['group'].onchange();");
        var top = page.Html("panels");
        Check(page.Value("view") == "summary" && top.Contains("<span class=\"cname\">Top Chord</span><span class=\"cview\">Summary</span>") &&
              !top.Contains("data-sum-kind=\"bottom\"") && !top.Contains("data-sum-kind=\"mega\"") && !top.Contains("VRM · P") &&
              top.Contains("Top Chord – Governing Axial Compression") && top.Contains(">Governing Compression |P| (MN)<") &&
              page.Eval("__els['component'].parentElement.hidden") == "true" && page.Eval("__els['step'].parentElement.hidden") == "true",
            "selecting Top Chord shows its Summary directly, with the generic component / step / position controls out of the way");
        Check(Count(top, "<rect data-bar=") == 26 && Count(top, "<circle data-mark=") == 26 && Count(top, "<path data-trend=") == 3 &&
              new[] { ">H20<", ">H25<", ">H30<", ">P01<", ">P09<" }.All(top.Contains) && top.IndexOf(">P01<", StringComparison.Ordinal) < top.IndexOf(">P09<", StringComparison.Ordinal),
            "the summary has one bar per available height × pretension candidate, grouped P01 → P09, with a trend line per height");
        Check(!top.Contains("data-bar=\"P08|H03\"") && Count(top, "class=\"cna\"") == 1 && top.Contains("P08 · H30&#10;Result unavailable".Replace("&#10;", "\n")) &&
              Count(TrendPath(top, "H03"), "M") == 2 && Count(TrendPath(top, "H01"), "M") == 1 && Count(TrendPath(top, "H01"), "L") == 8 &&
              Count(top, "<circle data-mark=\"H03\"") == 8,
            "P08-H30 has no bar and no marker, and the H30 trend line breaks there — no interpolation, no zero");
        var bar = Regex.Match(top, "<rect data-bar=\"P01\\|H01\" x=\"([\\d.]+)\" y=\"([\\d.]+)\" width=\"([\\d.]+)\"");
        var mark = Regex.Match(top, "<circle data-mark=\"H01\" cx=\"([\\d.]+)\" cy=\"([\\d.]+)\"");
        Check(Math.Abs(Number(bar, 1) + Number(bar, 3) / 2 - Number(mark, 1)) < 0.11 && Number(bar, 2) == Number(mark, 2),
            "each trend marker sits at the top centre of its bar, on the same axis");
        Check(top.Contains(">152.0<") && top.Contains(">" + (Math.Abs(TopPeak(3, 9)) * 1.01 / 1000).ToString("0.0") + "<") &&
              top.Contains("Governing compression: 152.0 MN") && top.Contains("Source P: −152,005 kN · Min · 0") &&
              top.Contains("station 15 m") && top.Contains("Iteration L01_H01_P01") && top.Contains("TopChord · NL CASE-7"),
            "bars are labelled in MN to one decimal as positive compression, and the tooltip traces each one to its source P, station, iteration and case");
        Check(top.Contains("Governing values — the source of every bar") && Count(top, "<tr><td>L01</td>") == 27 && Count(top, "Result unavailable</td>") == 1,
            "a values table lists every bar's source, including the unavailable iteration");

        // drill-down
        page.Run("window.memberReport.summary(2, 'station')");
        var station = page.Html("panels");
        Check(station.Contains("<span class=\"cname\">Top Chord</span><span class=\"cview\">By Station</span>") && station.Contains("data-sum=\"summary\" data-g=\"2\">← Summary</button>") &&
              station.Contains("Pretension Candidate <select data-sump=\"2\">") && Count(station, "<option value=\"P0") == 9 &&
              station.Contains("<option value=\"P01\" selected>") && !station.Contains("data-bar="),
            "View by Station opens the detail view with ← Summary and a Pretension Candidate selector on P01");
        Check(Count(station, "<path data-station=") == 3 && Count(StationPath(station, "H01"), "L") == 4 &&
              station.Contains("Top Chord – Axial Compression by Station · P01") && station.Contains(">Compression |P| (MN)<") &&
              station.Contains(">Left support<") && station.Contains(">Midspan<") && station.Contains(">Right support<") &&
              station.Contains("Station: 10 m\nCompression: 150.5 MN\nSource P: −150,500 kN"),
            "by station: one line per truss height along the physical chord, one governing point per station, left to right");
        Check(Regex.Match(top, "data-bar=\"P01\\|H02\"[^>]*fill=\"(#[0-9a-f]+)\"").Groups[1].Value ==
              Regex.Match(station, "data-station=\"H02\" d=\"[^\"]*\" fill=\"none\" stroke=\"(#[0-9a-f]+)\"").Groups[1].Value &&
              Regex.Match(top, "data-trend=\"H02\"[^>]*stroke-dasharray=\"([^\"]*)\"").Groups[1].Value ==
              Regex.Match(station, "data-station=\"H02\"[^>]*stroke-dasharray=\"([^\"]*)\"").Groups[1].Value,
            "a truss height keeps one colour and line style in Summary and By Station");
        page.Run("window.memberReport.summary(2, 'station', 'P08')");
        station = page.Html("panels");
        Check(Count(station, "<path data-station=") == 2 && station.Contains("H30 — no result for P08") && station.Contains("<option value=\"P08\" selected>"),
            "by station for P08 compares the heights that exist and says H30 has no result");
        page.Run("window.memberReport.summary(2, 'summary')");
        Check(page.Html("panels").Contains("data-sum-view=\"summary\"") && Count(page.Html("panels"), "<rect data-bar=") == 26, "← Summary returns to the governing-force summary");

        // bottom chord: same component, tension
        page.Run("__els['group'].value = '0'; __els['group'].onchange();");
        var bottom = page.Html("panels");
        Check(bottom.Contains("Bottom Chord – Governing Axial Tension") && bottom.Contains(">Governing Tension P (MN)<") &&
              Count(bottom, "<rect data-bar=") == 26 && Count(TrendPath(bottom, "H03"), "M") == 2 &&
              bottom.Contains(">" + (BottomPeak(1, 1) / 1000).ToString("0.0") + "<") && bottom.Contains("Governing tension: 119.7 MN"),
            "Bottom Chord uses the same summary: governing tension max P, positive, with the same gap at P08-H30");
        page.Run("window.memberReport.summary(0, 'station', 'P03')");
        Check(page.Html("panels").Contains("Bottom Chord – Axial Tension by Station · P03") && page.Html("panels").Contains(">Tension P (MN)<") &&
              page.Html("panels").Contains("Source P: −3,000 kN"),
            "Bottom Chord by station plots the stored tension, compression at the supports included");

        // the existing detailed views stay available for a chord
        page.Run("__els['view'].value = 'profiles'; __els['view'].onchange();");
        Check(page.Html("panels").Contains("<span class=\"title\">BottomChord · P</span>") && !page.Html("panels").Contains("data-sum-kind") &&
              page.Eval("__els['component'].parentElement.hidden") == "false",
            "All profiles / Envelope / Station matrix remain available for a chord under Display");

        // deep links used by Tab 06 modules 02 / 03 and by the PDF export
        var linked = new ReportPage(html, "#summary=bottom");
        Check(linked.Value("group") == "0" && linked.Value("view") == "summary" && linked.Html("panels").Contains("data-sum-kind=\"bottom\" data-sum-view=\"summary\"") &&
              !linked.Html("panels").Contains("data-sum-kind=\"top\""),
            "#summary=bottom opens the report straight on the Bottom Chord summary");
        var drilled = new ReportPage(html, "#summary=top&detail=station&pretension=P03");
        Check(drilled.Html("panels").Contains("data-sum-kind=\"top\" data-sum-view=\"station\"") && drilled.Html("panels").Contains("<option value=\"P03\" selected>"),
            "#summary=top&detail=station&pretension=P03 opens the drill-down for that candidate");

        // a study without chord or mega-column groups has nothing to summarise
        var plain = new ReportPage(generator.BuildHtml(generator.Build(ChordStudy(summaryGroups: false), "COT.sdb", IterationStudyMode.Combined)));
        Check(plain.Value("view") == "profiles" && plain.Eval("__els['view'].options[0].value") == "profiles" && plain.Html("panels").Contains("VRM · P"),
            "without a chord or mega-column group the report opens on the profile charts as before");

        // Tab 06 modules 02 / 03 / 05 open through small launcher pages
        var folder = Path.Combine(Path.GetTempPath(), "cotvcf_chord_" + Guid.NewGuid().ToString("N"));
        try
        {
            generator.Generate(folder, ChordStudy(), "COT.sdb", IterationStudyMode.Combined, ["member"]);
            var topLauncher = Path.Combine(folder, MemberForceReportGenerator.TopChordFileName);
            var bottomLauncher = Path.Combine(folder, MemberForceReportGenerator.BottomChordFileName);
            var megaLauncher = Path.Combine(folder, MemberForceReportGenerator.MegaColumnFileName);
            Check(File.ReadAllText(topLauncher).Contains("location.replace(\"Member_Forces_Report.html#summary=top\")") &&
                  File.ReadAllText(bottomLauncher).Contains("Member_Forces_Report.html#summary=bottom") && !File.ReadAllText(topLauncher).Contains("http"),
                "generation writes the pages that open the report on the Top / Bottom Chord summary");
            Check(Path.GetFileName(megaLauncher) == "Mega_Column_Report.html" &&
                  File.ReadAllText(megaLauncher).Contains("location.replace(\"Member_Forces_Report.html#summary=mega\")") &&
                  File.ReadAllText(megaLauncher).Contains("Open the Mega Column summary") && !File.ReadAllText(megaLauncher).Contains("http"),
                "generation writes Mega_Column_Report.html, the page that opens the report on the Mega Column summary");
            generator.Generate(folder, ChordStudy(summaryGroups: false), "COT.sdb", IterationStudyMode.Combined, ["member"]);
            Check(!File.Exists(topLauncher) && !File.Exists(bottomLauncher) && !File.Exists(megaLauncher),
                "a study without chord or mega-column groups leaves no stale summary page behind");
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    private static void MegaColumnSummary()
    {
        var generator = new MemberForceReportGenerator();
        var dto = generator.Build(ChordStudy(), "COT.sdb", IterationStudyMode.Combined);
        Check(dto.Groups.Select(g => g.GroupName).SequenceEqual(["BottomChord", "MegaColumns", "TopChord", "VRM"]), "fixture: mega columns are group 1");
        var mega = dto.Groups[1];
        Check(mega.Summary is { Kind: "mega", Title: "Mega Column", ByStation: false } && mega.Summary.Candidates.Count == 26 &&
              mega.Summary.Candidates.Select(c => c.IterationKey).SequenceEqual(mega.Series.Select(s => s.IterationKey)),
            "a mega-column group gets a summary for every available iteration, flagged as having no By Station view");
        var m = mega.Summary!.Candidates.Single(c => c.IterationKey == "L01_H01_P01");
        Check(Close(m.GoverningKN, MegaPeak(1, 1)) && m is { FrameName: "MC-2", ElementName: "MC-2-1", Step: "Max · 0" },
            "mega columns govern on max |P| over every column and result step, keeping the signed source P and the column it occurs in");
        Check(mega.Summary.Candidates.All(c => c.StationM is null && c.Profile.Count == 0),
            "no station and no station profile is derived for a mega column");

        var html = generator.BuildHtml(dto);
        var page = new ReportPage(html);
        page.Run("__els['group'].value = '1'; __els['group'].onchange();");
        var panel = page.Html("panels");
        Check(page.Value("view") == "summary" && panel.Contains("<span class=\"cname\">Mega Column</span><span class=\"cview\">Summary</span>") &&
              panel.Contains("data-sum-kind=\"mega\" data-sum-view=\"summary\"") && !panel.Contains("data-sum-kind=\"top\"") && !panel.Contains("VRM · P") &&
              panel.Contains("Mega Column – Governing Axial Compression") && panel.Contains(">Governing Compression |P| (MN)<") &&
              page.Eval("__els['component'].parentElement.hidden") == "true",
            "selecting Mega Columns shows the Mega Column summary directly: governing axial compression max |P|");
        Check(Count(panel, "<rect data-bar=") == 26 && Count(panel, "<circle data-mark=") == 26 && Count(panel, "<path data-trend=") == 3 &&
              !panel.Contains("data-bar=\"P08|H03\"") && Count(panel, "class=\"cna\"") == 1 && Count(TrendPath(panel, "H03"), "M") == 2 &&
              new[] { ">H20<", ">H25<", ">H30<", ">P01<", ">P09<" }.All(panel.Contains),
            "the mega-column summary is the same chart: a bar per height × pretension candidate, a trend line per height, and a gap at P08-H30");
        Check(panel.Contains(">200.4<") && panel.Contains("Governing compression: 200.4 MN") && panel.Contains("Source P: −200,400 kN · Max · 0") &&
              panel.Contains("At MC-2 / MC-2-1\nIteration L01_H01_P01") && panel.Contains("MegaColumns · NL CASE-7"),
            "mega-column bars are labelled in MN as positive compression, and the tooltip traces each one to its source P, column, iteration and case");
        // "station-hit" is the CSS class of the hover target, not a label
        Check(!panel.Replace("class=\"station-hit\"", "").Contains("station", StringComparison.OrdinalIgnoreCase),
            "nothing in the mega-column summary indicates a station — no View by Station, no station in the tooltip, no Station column");
        Check(panel.Contains("<th>Frame object</th><th>Analysis element</th><th>Result step</th>") && Count(panel, "<tr><td>L01</td>") == 27 &&
              panel.Contains("<td>MC-2</td><td>MC-2-1</td><td>Max · 0</td>") && panel.Contains("colspan=\"5\">Result unavailable</td>"),
            "the mega-column values table lists every bar's source column and result step");

        page.Run("window.memberReport.summary(1, 'station', 'P03')");
        Check(page.Html("panels") == panel, "a mega column has no By Station view to switch to");
        var linked = new ReportPage(html, "#summary=mega");
        Check(linked.Value("group") == "1" && linked.Value("view") == "summary" && linked.Html("panels") == panel,
            "#summary=mega opens the report straight on the Mega Column summary");
        Check(new ReportPage(html, "#summary=mega&detail=station&pretension=P03").Html("panels") == panel,
            "#summary=mega&detail=station still shows the summary");

        // the chords keep their station; the generic views still show a mega column in full
        page.Run("__els['view'].value = 'profiles'; __els['view'].onchange();");
        Check(page.Html("panels").Contains("<span class=\"title\">MegaColumns · P</span>") && !page.Html("panels").Contains("data-sum-kind"),
            "All profiles / Envelope / Station matrix remain available for the mega columns under Display");
    }

    private static string TrendPath(string html, string heightId) => Regex.Match(html, $"<path data-trend=\"{heightId}\" d=\"([^\"]*)\"").Groups[1].Value;
    private static string StationPath(string html, string heightId) => Regex.Match(html, $"<path data-station=\"{heightId}\" d=\"([^\"]*)\"").Groups[1].Value;
    private static double Number(Match match, int group) => double.Parse(match.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);
    private static bool Close(double? actual, double expected) => actual is { } value && Math.Abs(value - expected) < 0.01;

    private static int Count(string text, string needle)
    {
        var n = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
