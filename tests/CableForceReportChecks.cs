using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using COT_VCF.Application.Results;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;

internal static class CableForceReportChecks
{
    public static void Run()
    {
        var cables = new[] { "Cable1a-2", "Cable1b-2" };
        var loads = new[] { ("L01", 2.5), ("L02", 5.0) };
        var heights = new[] { ("H01", 5.0), ("H02", 7.5) };
        var pret = new[] { ("P01", 500.0, false), ("P02", (double?)null, true) };  // P02 = mixed

        var results = new List<CandidateResultDto>();
        long run = 0;
        foreach (var (lid, lv) in loads)
        foreach (var (hid, hv) in heights)
        foreach (var (pid, pv, mixed) in pret)
        {
            run++;
            var key = $"{lid}_{hid}_{pid}";
            var cf = cables.Select((c, i) =>
            {
                // one deliberately missing result to prove it never becomes 0 kN
                var isMissing = run == 3 && i == 1;
                var target = mixed ? 700 + i * 50 : pv!.Value;
                return new CableForceResultDto(c, target,
                    isMissing ? null : 900 + run * 5 + i * 30,
                    isMissing ? ResultValueStatus.NoResult : ResultValueStatus.Valid,
                    isMissing ? "no row" : null);
            }).ToArray();

            var warnings = cf.Where(x => x.Status != ResultValueStatus.Valid)
                .Select(x => new ResultWarningDto(key, "CableForce", ResultWarningSeverity.Warning, $"{x.CableName}: {x.Status}", DateTimeOffset.Now))
                .ToArray();

            results.Add(new CandidateResultDto(
                key, $@"C:\Study\{key}\{key}.sdb", @"C:\base.sdb", "COT Framing_V1.2.sdb", IterationStudyMode.Combined,
                new CandidateParametersDto(run, key, lid, hid, pid, lv, $"{lid} · {lv} kPa", hv, 0, pv, mixed ? $"{pid} · mixed" : $"{pid} · {pv} kN"),
                DateTimeOffset.Now, DateTimeOffset.Now, DateTimeOffset.Now,
                CandidateExtractionStatus.ExtractedOk, [], warnings, cf));
        }

        var gen = new CableForceReportGenerator();
        var dto = gen.Build(results, "COT Framing_V1.2.sdb", IterationStudyMode.Combined, "CABLE_FORCE");

        Check(dto.Cables.SequenceEqual(cables), "one report entry per configured cable");
        Check(dto.Rows.Count == 8, "one row per study candidate");
        Check(dto.Pretensions.Single(p => p.Id == "P02").Mixed && dto.Pretensions.Single(p => p.Id == "P02").Value is null,
            "a mixed pretension candidate stays categorical — no invented kN value");

        var missingRow = dto.Rows.Single(r => r.Run == 3);
        Check(missingRow.Forces["Cable1b-2"] is null && missingRow.Deltas["Cable1b-2"] is null &&
              missingRow.DeltaPercents["Cable1b-2"] is null && missingRow.Statuses["Cable1b-2"] == "NoResult",
            "a missing cable-force result is null with its status — never 0 kN, and no delta is derived from it");
        Check(dto.Warnings.Any(w => w.IterationKey == missingRow.IterationKey), "the missing result raises a warning");
        Check(dto.Capacities.Count == 0 && dto.CapacitySources.Values.All(s => s == CableCapacityResolver.None) &&
              dto.Governing.All(g => g.CapacityKN is null && g.UtilisationPct is null && g.Status is "no-capacity"),
            "without a supplied capacity there is no limit and no utilisation — nothing is invented");

        var html = gen.BuildHtml(dto);
        Check(html.Contains("id=\"report-data\"") && html.Contains("Cable Forces Viewer") &&
              html.Contains("Result Library") && html.Contains("Result unavailable") &&
              html.Contains("Source Data") && html.Contains("id=\"warns\"") &&
              !html.Contains("http://") && !html.Contains("https://") && !html.Contains("cdn."),
            "Cable Forces HTML is self-contained, keeps the Result Library / Source Data / warnings, and shows 'Result unavailable' for gaps");
        Check(html.Contains("Candidate definitions") && html.Contains("candidate-pretension-matrix"),
            "Cable Forces HTML includes the shared compact L/H/P candidate definitions");
        Check(html.Contains("01 · Cable Force Overview") && html.Contains("02 · Force Development by Pretension Candidate") &&
              html.Contains("03 · Governing / Capacity Review"),
            "the report is organised as overview, force development per pretension candidate, and governing / capacity review");
        Check(new[] { "Cable Grouping", "Side A", "Side B", "Unassigned", "Apply Grouping", "Auto Detect", "Reset" }.All(html.Contains),
            "the report carries the client-side cable grouping controls");
        Check(html.Contains("cable result case: CABLE_FORCE") && !html.Contains("ULS"),
            "the result case shown is the configured one — no hardcoded ULS wording");

        var page = new ReportPage(html);
        Check(page.RequestedIds.Length > 0 && page.RequestedIds.All(page.StaticIds.Contains) &&
              new[] { "overview", "detail", "govtable", "gov", "glist", "gpanel", "tb", "warns", "tip", "vk", "vu", "load" }.All(page.StaticIds.Contains),
            "every DOM element the report script looks up exists in the report markup");
        Check(!page.Hidden("loadwrap") && Count(page.Html("load"), "<option") == 2,
            "more than one loading candidate offers a load selector instead of mixing loads");
        var l1 = page.Html("detail");
        page.Run("window.cableReport.setLoad('L02')");
        Check(l1 != page.Html("detail") && page.Html("detail").Contains("data-chart=\"detail\""),
            "choosing another loading candidate re-renders every chart for that load only");
        Check(page.Html("tb").Contains("Result unavailable") && !page.Html("tb").Contains("NaN"),
            "the Source Data table shows a missing force as 'Result unavailable'");

        // capacity → utilisation view + Result-Library cross-link
        var caps = new Dictionary<string, double> { ["Cable1a-2"] = 2000, ["Cable1b-2"] = 2500 };
        var capDto = gen.Build(results, "COT Framing_V1.2.sdb", IterationStudyMode.Combined, "CABLE_FORCE",
            caps, new[] { "cable", "deflection" });
        Check(capDto.Capacities.Count == 2 && capDto.Capacities["Cable1a-2"] == 2000,
            "configured cable design capacities flow into the report DTO");
        var capHtml = gen.BuildHtml(capDto);
        Check(capHtml.Contains("% of capacity") && capHtml.Contains("Deflection_Report.html") &&
              !capHtml.Contains("http://") && !capHtml.Contains("https://"),
            "the report offers the utilisation toggle and links the sibling deflection report when it exists");

        // workbook round-trip of the cable-force rows
        var tmp = Path.Combine(Path.GetTempPath(), "cotvcf_cf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var writer = new ConsolidatedResultWriter();
            foreach (var r in results) writer.Add(r);
            var xlsx = writer.Write(tmp, [], "SERVICE", "COT Framing_V1.2.sdb", @"C:\base.sdb",
                IterationStudyMode.Combined, caps, "CABLE_FORCE");
            var back = ConsolidatedResultWriter.Read(xlsx);
            var backRow3 = back.Single(r => r.Parameters.RunIndex == 3);
            Check(back.All(r => (r.CableForces?.Count ?? 0) == 2), "workbook round-trips 2 cable-force rows per candidate");
            Check(backRow3.CableForces!.Single(c => c.CableName == "Cable1b-2").ForceKN is null &&
                  backRow3.CableForces!.Single(c => c.CableName == "Cable1b-2").Status == ResultValueStatus.NoResult,
                "workbook preserves the missing cable force as null + its status");
            var regenerated = new CableForceReportGenerator().RegenerateFromWorkbook(tmp);
            Check(regenerated.EndsWith("Cable_Forces_Report.html") &&
                  ConsolidatedResultWriter.ReadCableResultCase(xlsx) == "CABLE_FORCE" &&
                  File.ReadAllText(regenerated).Contains("cable result case: CABLE_FORCE", StringComparison.Ordinal),
                "Cable report regenerates with its exact result case from the workbook alone (no SAP)");

            // fpu / φ are post-processing inputs: changing them must reach the report without touching SAP.
            var cable = new CablePretensionConfig { CableName = "Cable1a-2", SectionAreaM2 = 0.0288023 };
            Check(Math.Abs(cable.ResolveCapacity(1860, 0.5)!.Value - 0.0288023 * 1860 * 1000 * 0.5) < 1e-6 &&
                  Math.Abs(cable.ResolveCapacity(2160, 0.5)!.Value - 0.0288023 * 2160 * 1000 * 0.5) < 1e-6,
                "raising fpu raises the resolved design capacity (area × fpu × 1000 × φ)");

            var raised = new Dictionary<string, double> { ["Cable1a-2"] = 31106.5, ["Cable1b-2"] = 31106.5 };
            var stored = ConsolidatedResultWriter.UpdateCableCapacities(xlsx, raised);
            Check(stored.Count == 2 && ConsolidatedResultWriter.ReadCableCapacities(xlsx)["Cable1a-2"] == 31106.5,
                "new capacities are written back into the workbook's CableProperties sheet");
            Check(ConsolidatedResultWriter.Read(xlsx).Count == back.Count &&
                  ConsolidatedResultWriter.ReadCableResultCase(xlsx) == "CABLE_FORCE",
                "rewriting capacities leaves the rest of the workbook intact");

            var afterFpu = new CableForceReportGenerator().RegenerateFromWorkbook(tmp);
            var afterHtml = File.ReadAllText(afterFpu);
            Check(afterHtml.Contains("31106.5", StringComparison.Ordinal) && !afterHtml.Contains("\"Cable1a-2\":2000", StringComparison.Ordinal),
                "regenerating after an fpu change reports the new capacity, not the one frozen at run time");

            var overridden = File.ReadAllText(new CableForceReportGenerator()
                .RegenerateFromWorkbook(tmp, new Dictionary<string, double> { ["Cable1a-2"] = 999 }));
            Check(overridden.Contains("\"Cable1a-2\":999", StringComparison.Ordinal) &&
                  overridden.Contains("\"Cable1b-2\":31106.5", StringComparison.Ordinal) &&
                  ConsolidatedResultWriter.ReadCableCapacities(xlsx)["Cable1a-2"] == 31106.5,
                "a supplied capacity overrides the workbook for that cable only, without rewriting the workbook");

            var unresolved = File.ReadAllText(new CableForceReportGenerator()
                .RegenerateFromWorkbook(tmp, new Dictionary<string, double>(), cables));
            Check(unresolved.Contains("\"capacities\":{}", StringComparison.Ordinal) &&
                  unresolved.Contains("\"Cable1a-2\":\"invalid\"", StringComparison.Ordinal),
                "a cable on the current Tab 03 that cannot resolve a capacity is unavailable — the stored value is not used as a fallback");

            ConsolidatedResultWriter.UpdateCableCapacities(xlsx, new Dictionary<string, double>());
            var withoutCapacity = new CableForceReportGenerator()
                .RegenerateFromWorkbook(tmp, new Dictionary<string, double>());
            Check(ConsolidatedResultWriter.ReadCableCapacities(xlsx).Count == 0 &&
                  !File.ReadAllText(withoutCapacity).Contains("\"Cable1a-2\":31106.5", StringComparison.Ordinal),
                "regeneration clears stale workbook capacities when no current capacity can be derived");
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }

        ForceMath();
        CableOrderAndAutoDetect();
        CapacityPriority();
        LegacyPackageRegeneratesWithoutSap();
    }

    // ---------- target / final / delta ----------

    private static void ForceMath()
    {
        Check(Near(CableForceMath.DeltaForce(10000, 35411), 25411) && Near(CableForceMath.DeltaPercent(10000, 35411), 254.11),
            "force pickup: delta = final − target, delta % = (final − target) / target × 100 > 0");
        Check(Near(CableForceMath.DeltaForce(30000, 23677), -6323) && Near(CableForceMath.DeltaPercent(30000, 23677), -21.0767),
            "force loss: delta and delta % are negative");
        Check(CableForceMath.DeltaForce(5000, 5000) == 0 && CableForceMath.DeltaPercent(5000, 5000) == 0,
            "no change from target is 0 kN / 0 %");
        Check(Near(CableForceMath.DeltaForce(0, 4200), 4200) && CableForceMath.DeltaPercent(0, 4200) is null,
            "a zero target has a delta force but no delta % (N/A)");
        Check(CableForceMath.DeltaForce(null, 4200) is null && CableForceMath.DeltaPercent(null, 4200) is null,
            "a missing target yields no delta");
        Check(CableForceMath.DeltaForce(5000, null) is null && CableForceMath.DeltaPercent(5000, null) is null &&
              CableForceMath.DeltaForce(5000, double.NaN) is null,
            "a missing final force yields no delta — never a delta against 0");
        Check(Near(CableForceMath.Utilisation(35411, 31106.5), 113.8379) && CableForceMath.Utilisation(35411, null) is null &&
              CableForceMath.Utilisation(35411, 0) is null && CableForceMath.Utilisation(null, 31106.5) is null,
            "utilisation = force / capacity × 100, and N/A without a force or a positive capacity");
        Check(CableForceMath.HeightLabel(20) == "H20" && CableForceMath.HeightLabel(22.5) == "H22.5",
            "height labels carry the actual truss height");
    }

    // ---------- natural order + side suggestion ----------

    private static void CableOrderAndAutoDetect()
    {
        var shuffled = new[] { "Cable10", "Cable2", "Cable1" };
        Check(shuffled.OrderBy(c => c, NaturalNameComparer.Instance).SequenceEqual(["Cable1", "Cable2", "Cable10"]),
            "cables order naturally: Cable1, Cable2, Cable10 — not 1, 10, 2");
        var orderDto = new CableForceReportGenerator().Build(
            [Candidate(1, "L01", "H01", 20, "P01", shuffled.Select(c => new CableForceResultDto(c, 100, 150, ResultValueStatus.Valid)).ToArray())],
            "m.sdb", IterationStudyMode.Combined, "CASE");
        Check(orderDto.Cables.SequenceEqual(["Cable1", "Cable2", "Cable10"]) &&
              orderDto.AutoGroups.Values.All(side => side == CableGroupDetector.Unassigned),
            "the report lists cables in natural order, and numbered cables without a side marker stay Unassigned");
        Check(new[] { "T02", "T01", "NorthCable01", "LC-01", "edge-b", "Edge-A" }.OrderBy(c => c, NaturalNameComparer.Instance)
                .SequenceEqual(["Edge-A", "edge-b", "LC-01", "NorthCable01", "T01", "T02"]),
            "arbitrary cable names still get a deterministic natural-name order");

        var named = new[] { "Cable1a-2", "Cable1b-2", "Tendon-X", "EdgeCable04", "TransferNorth" };
        var suggestion = CableGroupDetector.Suggest(named);
        Check(suggestion.Count == named.Length && suggestion["Cable1a-2"] == "A" && suggestion["Cable1b-2"] == "B" &&
              suggestion["Tendon-X"] == "" && suggestion["EdgeCable04"] == "" && suggestion["TransferNorth"] == "",
            "Auto Detect pairs Cable1a-2 / Cable1b-2 and leaves arbitrary names Unassigned — no forced A/B classification");

        var others = CableGroupDetector.Suggest(["LC-01", "RC-01", "LeftTie", "RightTie", "NorthCable01", "SouthCable01", "T01", "T02"]);
        Check(others["LC-01"] == "A" && others["RC-01"] == "B" && others["LeftTie"] == "A" && others["RightTie"] == "B" &&
              others["NorthCable01"] == "" && others["SouthCable01"] == "" && others["T01"] == "" && others["T02"] == "",
            "L/R and Left/Right pairs are suggested; North/South and plain numbering are not guessed");

        var lone = CableGroupDetector.Suggest(["Cable1a", "Cable2b", "CableA"]);
        Check(lone.Values.All(side => side == ""), "a side letter without its counterpart cable is ambiguous and stays Unassigned");
        var grid = CableGroupDetector.Suggest(["Tie-La", "Tie-Lb", "Tie-Ra", "Tie-Rb"]);
        Check(grid.Values.All(side => side == ""), "a name with two competing side markers is ambiguous and stays Unassigned");
        var series = CableGroupDetector.Suggest(["Stay1a", "Stay1b", "Stay1c"]);
        Check(series.Values.All(side => side == ""), "an a / b / c enumeration is not mistaken for two sides");
    }

    // ---------- capacity source of truth ----------

    private static void CapacityPriority()
    {
        var historical = new Dictionary<string, double> { ["Cable1a-2"] = 20000, ["Tendon-X"] = 15000, ["EdgeCable04"] = 12345 };
        var current = new Dictionary<string, double> { ["Cable1a-2"] = 31106.5 };
        var set = CableCapacityResolver.Resolve(current, ["Cable1a-2", "EdgeCable04"], historical);
        Check(set.Capacities["Cable1a-2"] == 31106.5 && set.Sources["Cable1a-2"] == CableCapacityResolver.Current,
            "the current Tab 03 capacity overrides the historical capacity for the same cable");
        Check(set.Capacities["Tendon-X"] == 15000 && set.Sources["Tendon-X"] == CableCapacityResolver.Stored,
            "a cable that is not on the current Tab 03 keeps the capacity stored for that same cable name");
        Check(!set.Capacities.ContainsKey("EdgeCable04") && set.Sources["EdgeCable04"] == CableCapacityResolver.Invalid,
            "a Tab 03 cable whose capacity cannot be resolved is unavailable — no fallback is fabricated");
        Check(!set.Capacities.ContainsKey("TransferNorth") && !set.Sources.ContainsKey("TransferNorth") &&
              !set.Capacities.ContainsKey("Cable1b-2"),
            "a cable with no current and no historical capacity gets none, and none is borrowed from a similar name");
        var storedOnly = CableCapacityResolver.Resolve(null, null, historical);
        Check(storedOnly.Capacities.Count == 3 && storedOnly.Sources.Values.All(s => s == CableCapacityResolver.Stored),
            "when Tab 03 supplies nothing the stored capacities stand");
        var bad = CableCapacityResolver.Resolve(new Dictionary<string, double> { ["Tendon-X"] = double.NaN, ["Cable1a-2"] = -5 }, null, historical);
        Check(!bad.Capacities.ContainsKey("Tendon-X") && !bad.Capacities.ContainsKey("Cable1a-2"),
            "a non-finite or non-positive current capacity is never used and never replaced by a stored one");
    }

    // ---------- legacy raw package → Tab 06 regenerate → new report, no SAP ----------

    private static readonly string[] LegacyCables = ["Cable1a-2", "Cable1b-2", "Tendon-X", "EdgeCable04", "TransferNorth"];
    private static readonly (string Id, double M)[] LegacyHeights = [("H01", 18), ("H02", 24), ("H03", 35)];
    private static readonly string[] LegacyPretensions = ["P01", "P02", "P03"];
    private const string LegacyCase = "NL STAGED-07: 1.2DL+1.6LL";

    private static double? LegacyTarget(string cable, int p) => cable switch
    {
        "Cable1a-2" or "Cable1b-2" => 8000 + 2000 * p,   // varies with the pretension candidate
        "Tendon-X" => 30000,                             // identical in every candidate → one target level
        "EdgeCable04" => 0,                              // zero target → delta % N/A
        _ => null                                        // monitored but not pretensioned → no target
    };

    private static double LegacyFinal(string cable, int p, double h) => cable switch
    {
        "Cable1a-2" or "Cable1b-2" => (8000 + 2000 * p) * 2.5 + h * 10,   // pickup
        "Tendon-X" => 24000 + 100 * p - h * 10,                           // loss
        "EdgeCable04" => 5000 + h,
        _ => 8000 + p
    };

    /// <summary>A raw package as an older build wrote it: only extracted values — no side, cable position,
    /// delta force / percent, chart group or chart order. Assembled as text so the current writer is not involved.</summary>
    private static string LegacyRawJson()
    {
        static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        var b = new StringBuilder();
        b.Append("{\"schemaVersion\":1,\"generatedAt\":\"2026-03-01T08:00:00+00:00\",\"baselineModelName\":\"Legacy Framing.sdb\",")
         .Append("\"baselineModelPath\":\"C:\\\\Legacy\\\\Legacy Framing.sdb\",\"studyMode\":\"Combined\",\"deflectionPoints\":[\"7\"],")
         .Append("\"deflectionOutputCase\":\"SERVICE\",\"cableResultCase\":\"").Append(LegacyCase).Append("\",")
         .Append("\"cableDesignCapacities\":{\"Cable1a-2\":20000,\"Cable1b-2\":20000,\"Tendon-X\":15000,\"EdgeCable04\":12345},\"results\":[");
        var run = 0;
        for (var hi = 0; hi < LegacyHeights.Length; hi++)
        for (var pi = 0; pi < LegacyPretensions.Length; pi++)
        {
            run++;
            var (hid, hm) = LegacyHeights[hi];
            var pid = LegacyPretensions[pi];
            if (hid == "H03" && pid == "P02") continue;                  // this candidate never produced a result
            var key = $"L01_{hid}_{pid}";
            if (b[^1] == '}') b.Append(',');
            b.Append("{\"iterationKey\":\"").Append(key).Append("\",\"candidateModelPath\":\"C:\\\\Legacy\\\\Study\\\\").Append(key).Append(".sdb\",")
             .Append("\"baselineModelPath\":\"C:\\\\Legacy\\\\Legacy Framing.sdb\",\"baselineModelName\":\"Legacy Framing.sdb\",\"studyMode\":\"Combined\",")
             .Append("\"parameters\":{\"runIndex\":").Append(run).Append(",\"iterationKey\":\"").Append(key)
             .Append("\",\"loadingCandidateId\":\"L01\",\"heightCandidateId\":\"").Append(hid).Append("\",\"pretensionCandidateId\":\"").Append(pid)
             .Append("\",\"loadLevelKPa\":5,\"loadingLabel\":\"5 kPa\",\"trussHeightM\":").Append(N(hm)).Append(",\"deltaHM\":0,\"pretensionKN\":null,\"pretensionLabel\":\"")
             .Append(pid).Append(" · mixed\"},")
             .Append("\"analysisStartedAt\":\"2026-03-01T07:00:00+00:00\",\"analysisCompletedAt\":\"2026-03-01T07:05:00+00:00\",\"extractionCompletedAt\":\"2026-03-01T07:06:00+00:00\",")
             .Append("\"extractionStatus\":\"ExtractedOk\",\"groups\":[{\"groupName\":\"[O]-DeflectionMonitorPts\",\"outputCase\":\"SERVICE\",\"profileAxis\":\"X\",")
             .Append("\"points\":[{\"pointName\":\"7\",\"x\":0,\"y\":0,\"z\":0,\"sequence\":1,\"uzMm\":-12.5,\"status\":\"Valid\",\"warning\":null}]}],\"warnings\":[],\"cableForces\":[");
            for (var ci = 0; ci < LegacyCables.Length; ci++)
            {
                var cable = LegacyCables[ci];
                var noResult = cable == "Tendon-X" && hid == "H01" && pid == "P03";   // one cable row without a force
                if (ci > 0) b.Append(',');
                b.Append("{\"cableName\":\"").Append(cable).Append("\",\"targetForceKN\":")
                 .Append(LegacyTarget(cable, pi + 1) is { } t ? N(t) : "null").Append(",\"forceKN\":")
                 .Append(noResult ? "null" : N(LegacyFinal(cable, pi + 1, hm))).Append(",\"status\":\"")
                 .Append(noResult ? "NoResult" : "Valid").Append("\",\"warning\":null}");
            }
            b.Append("]}");
        }
        return b.Append("]}").ToString();
    }

    private static void LegacyPackageRegeneratesWithoutSap()
    {
        var root = Path.Combine(Path.GetTempPath(), "cotvcf_cf_legacy_" + Guid.NewGuid().ToString("N"));
        var rawFolder = Path.Combine(root, StudyRawDataStore.DirectoryName);
        Directory.CreateDirectory(rawFolder);
        try
        {
            var rawPath = Path.Combine(rawFolder, StudyRawDataStore.JsonFileName);
            var rawText = LegacyRawJson();
            File.WriteAllText(rawPath, rawText);
            Check(!new[] { "side", "cablePosition", "deltaForce", "deltaPercent", "chartGroup", "chartOrder" }
                    .Any(field => rawText.Contains(field, StringComparison.OrdinalIgnoreCase)),
                "the legacy raw package carries none of the report-side presentation fields");

            // Current Tab 03: both Cable1 cables sized (area × fpu × 1000 × φ), EdgeCable04 configured without an
            // area, Tendon-X and TransferNorth no longer configured.
            var tab3 = new[]
            {
                new CablePretensionConfig { CableName = "Cable1a-2", SectionAreaM2 = 0.0288023 },
                new CablePretensionConfig { CableName = "Cable1b-2", SectionAreaM2 = 0.0288023 },
                new CablePretensionConfig { CableName = "EdgeCable04" },
            };
            IReadOnlyDictionary<string, double> Tab3Capacities(double fpu) => tab3
                .Where(c => c.ResolveCapacity(fpu, 0.5) is not null)
                .ToDictionary(c => c.CableName, c => c.ResolveCapacity(fpu, 0.5)!.Value, StringComparer.Ordinal);
            var configured = tab3.Select(c => c.CableName).ToArray();

            var outcome = new StudyPostProcessor().Generate(root, Tab3Capacities(2160), new DeflectionLimitBand(null, null), configured,
                new ReportPrintSettings { CompanyName = "Legacy Engineers", ProjectName = "Legacy Tower", JobNumber = "J-77" });
            var reportPath = outcome.CableForceReportPath!;
            var html = File.ReadAllText(reportPath);
            Check(Path.GetFileName(reportPath) == "Cable_Forces_Report.html" && File.ReadAllText(rawPath) == rawText,
                "an existing legacy raw package regenerates Cable_Forces_Report.html with no SAP and is left untouched");
            Check(html.Contains("cable result case: " + LegacyCase, StringComparison.Ordinal) && !html.Contains("ULS") &&
                  !html.Contains("http://") && !html.Contains("https://") && !html.Contains("cdn."),
                "the regenerated report is self-contained and names the configured result case");
            Check(html.Contains("\"project\":\"Legacy Tower\"") && html.Contains("\"jobNumber\":\"J-77\"") &&
                  html.Contains("\"company\":\"Legacy Engineers\"") && html.Contains("\"title\":\"Cable Forces\""),
                "Regenerate embeds the current Print Setup — header and footer need no SAP either");
            var siblingsOnEngine = new[] { outcome.DeflectionReportPath!,
                    Directory.EnumerateFiles(root, "Deflection_Diagnostic_*.html", SearchOption.AllDirectories).First() }
                .Select(File.ReadAllText).ToArray();
            Check(siblingsOnEngine.All(text => text.Contains("\"project\":\"Legacy Tower\"") && text.Contains("id=\"pr-page\"")),
                "the same Regenerate puts the Print Setup into the deflection report and the candidate diagnostics");

            var raw = new StudyRawDataStore().ReadFromStudyFolder(root);
            var gen = new CableForceReportGenerator();
            CableForceReportDto BuildFor(double fpu)
            {
                var capacitySet = CableCapacityResolver.Resolve(Tab3Capacities(fpu), configured, raw.CableDesignCapacities);
                return gen.Build(raw.Results, raw.BaselineModelName, raw.StudyMode, raw.CableResultCase,
                    capacitySet.Capacities, ["cable"], capacitySet.Sources);
            }
            var dto = BuildFor(2160);

            Check(dto.Cables.SequenceEqual(["Cable1a-2", "Cable1b-2", "EdgeCable04", "Tendon-X", "TransferNorth"]) &&
                  dto.Rows.Count == 8 && dto.Rows.All(r => r.Forces.Count == 5),
                "no cable is lost: arbitrary cable names are all reported, in natural order");
            Check(dto.AutoGroups["Cable1a-2"] == "A" && dto.AutoGroups["Cable1b-2"] == "B" &&
                  new[] { "EdgeCable04", "Tendon-X", "TransferNorth" }.All(c => dto.AutoGroups[c] == ""),
                "ambiguous cables open as Unassigned");
            Check(dto.Heights.Select(h => h.Value).SequenceEqual(new double?[] { 18, 24, 35 }) &&
                  dto.Overview.Select(o => o.HeightM).SequenceEqual(new double[] { 18, 24, 35 }),
                "truss heights are derived from the results (18 / 24 / 35 m), one overview chart each");

            // derived force development
            var p1 = dto.Rows.Single(r => r.IterationKey == "L01_H01_P01");
            Check(p1.Targets["Cable1a-2"] == 10000 && Near(p1.Forces["Cable1a-2"], 25180) &&
                  Near(p1.Deltas["Cable1a-2"], 15180) && Near(p1.DeltaPercents["Cable1a-2"], 151.8),
                "delta force and delta % are derived at report time from the stored target and final force (pickup)");
            Check(Near(p1.Deltas["Tendon-X"], 23920 - 30000) && p1.DeltaPercents["Tendon-X"] < 0,
                "a final force below the target is reported as a force loss");
            Check(Near(p1.Deltas["EdgeCable04"], 5018) && p1.DeltaPercents["EdgeCable04"] is null,
                "a zero target keeps its delta force and reports delta % as N/A");
            Check(!p1.Targets.ContainsKey("TransferNorth") && p1.Deltas["TransferNorth"] is null && p1.DeltaPercents["TransferNorth"] is null &&
                  Near(p1.Forces["TransferNorth"], 8001),
                "a cable without a target still reports its final force, with no delta");
            var gapRow = dto.Rows.Single(r => r.IterationKey == "L01_H01_P03");
            Check(gapRow.Forces["Tendon-X"] is null && gapRow.Deltas["Tendon-X"] is null && gapRow.DeltaPercents["Tendon-X"] is null &&
                  gapRow.Targets["Tendon-X"] == 30000,
                "a missing final force stays null beside its known target");

            // overview ranges
            var h35 = dto.Overview.Single(o => o.HeightId == "H03");
            var h35Cable = h35.Ranges.Single(r => r.Cable == "Cable1a-2");
            Check(h35.ExpectedCount == 3 && h35.AvailableCount == 2 && h35.MissingPretIds.SequenceEqual(["P02"]) &&
                  h35.Note == "Range based on 2 available pretension candidates; P02-H35 unavailable.",
                "a candidate without a result is named in the overview note");
            Check(h35Cable is { TargetMin: 10000, TargetMax: 14000, TargetCount: 2, FinalCount: 2 } &&
                  Near(h35Cable.FinalMin, 25350) && Near(h35Cable.FinalMax, 35350) &&
                  h35Cable is { FinalMinPretId: "P01", FinalMaxPretId: "P03" },
                "overview target / final ranges exclude the missing candidate — no fake zero");
            var h18 = dto.Overview.Single(o => o.HeightId == "H01");
            var h18Tendon = h18.Ranges.Single(r => r.Cable == "Tendon-X");
            Check(h18Tendon is { TargetMin: 30000, TargetMax: 30000, TargetCount: 3, FinalCount: 2 } &&
                  Near(h18Tendon.FinalMin, 23920) && Near(h18Tendon.FinalMax, 24020) &&
                  h18.Note.Contains("1 cable result unavailable", StringComparison.Ordinal),
                "a cable-level gap is excluded from that cable's range, and an identical target is one level, not a range");
            Check(dto.Overview.SelectMany(o => o.Ranges).All(r => r.FinalMin is null or > 0) &&
                  h18.Ranges.Single(r => r.Cable == "TransferNorth") is { TargetMin: null, TargetMax: null, TargetCount: 0 },
                "missing values never enter a range as 0, and a cable without targets has no target range");

            // capacity: current Tab 03 first, by exact cable name
            Check(Near(dto.Capacities["Cable1a-2"], 0.0288023 * 2160 * 1000 * 0.5) && dto.CapacitySources["Cable1a-2"] == "current" &&
                  !html.Contains("\"Cable1a-2\":20000", StringComparison.Ordinal),
                "the regenerated report uses the current Tab 03 capacity, not the 20,000 kN stored with the old results");
            Check(dto.Capacities["Tendon-X"] == 15000 && dto.CapacitySources["Tendon-X"] == "stored",
                "a historical cable that left Tab 03 keeps its own stored capacity");
            Check(!dto.Capacities.ContainsKey("EdgeCable04") && dto.CapacitySources["EdgeCable04"] == "invalid" &&
                  !html.Contains("\"EdgeCable04\":12345", StringComparison.Ordinal),
                "a Tab 03 cable with no resolvable capacity reads as unavailable in the report");
            Check(!dto.Capacities.ContainsKey("TransferNorth") && dto.CapacitySources["TransferNorth"] == "none",
                "a cable with no capacity anywhere has none in the report");

            // governing / capacity review
            var gov = dto.Governing.Where(g => g.Scope == "L01").ToDictionary(g => g.Cable);
            Check(gov["Cable1a-2"] is { IterationKey: "L01_H03_P03", HeightId: "H03", PretId: "P03", Status: "exceed" } &&
                  Near(gov["Cable1a-2"].FinalForceKN, 35350) && Near(gov["Cable1a-2"].UtilisationPct, 35350 / (0.0288023 * 2160 * 1000 * 0.5) * 100),
                "the governing candidate is the largest final force of that cable, checked against its current capacity");
            Check(gov["Tendon-X"] is { Status: "exceed", CapacityKN: 15000 } && Near(gov["Tendon-X"].FinalForceKN, 24060) &&
                  gov["Tendon-X"].IterationKey == "L01_H02_P03",
                "a stored capacity still drives the limit check for its own cable, and a missing result cannot govern");
            Check(gov["EdgeCable04"] is { Status: "no-capacity", CapacityKN: null, UtilisationPct: null } &&
                  gov["TransferNorth"] is { Status: "no-capacity", CapacityKN: null, UtilisationPct: null },
                "without a capacity there is no utilisation and no pass / exceed verdict");
            Check(dto.Governing.Count(g => g.Scope == CableForceReportGenerator.StudyScope) == 5,
                "governing rows are also kept study-wide");

            // fpu change without a study re-run: same raw forces, new capacity
            var low = BuildFor(1860);
            var rowsNow = JsonSerializer.Serialize(dto.Rows);
            Check(JsonSerializer.Serialize(low.Rows) == rowsNow && JsonSerializer.Serialize(low.Overview) == JsonSerializer.Serialize(dto.Overview) &&
                  low.Heights.SequenceEqual(dto.Heights) && low.Pretensions.SequenceEqual(dto.Pretensions),
                "changing fpu leaves target force, final force, heights and pretension candidates unchanged");
            var lowGov = low.Governing.Single(g => g.Scope == "L01" && g.Cable == "Cable1a-2");
            Check(Near(low.Capacities["Cable1a-2"], 0.0288023 * 1860 * 1000 * 0.5) && low.Capacities["Cable1a-2"] < dto.Capacities["Cable1a-2"] &&
                  lowGov.UtilisationPct > gov["Cable1a-2"].UtilisationPct && lowGov.FinalForceKN == gov["Cable1a-2"].FinalForceKN &&
                  low.Capacities["Tendon-X"] == 15000,
                "changing fpu changes the capacity, the limit and the utilisation of the Tab 03 cables only");
            var reDone = new StudyPostProcessor().Generate(root, Tab3Capacities(1860), new DeflectionLimitBand(null, null), configured);
            var reHtml = File.ReadAllText(reDone.CableForceReportPath!);
            Check(reHtml.Contains("\"Cable1a-2\":" + (0.0288023 * 1860 * 1000 * 0.5).ToString("R", CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                  File.ReadAllText(rawPath) == rawText,
                "Regenerate after an fpu change writes the new capacity into the report from the same raw file");

            // A module's own Regenerate asks for its report alone: the workbook, the diagnostics and the other
            // reports are neither rewritten nor lost
            var untouched = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => !string.Equals(path, reDone.CableForceReportPath, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(path => path, path => (File.GetLastWriteTimeUtc(path), new FileInfo(path).Length));
            File.SetLastWriteTimeUtc(reDone.CableForceReportPath!, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var only = new StudyPostProcessor().Generate(root, Tab3Capacities(2160), new DeflectionLimitBand(null, null), configured,
                outputs: StudyOutputs.CableForceReport);
            Check(File.ReadAllText(only.CableForceReportPath!).Contains("\"Cable1a-2\":" + (0.0288023 * 2160 * 1000 * 0.5).ToString("R", CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                  File.GetLastWriteTimeUtc(only.CableForceReportPath!).Year > 2020 && untouched.Count > 3 &&
                  untouched.All(file => File.Exists(file.Key) && File.GetLastWriteTimeUtc(file.Key) == file.Value.Item1 && new FileInfo(file.Key).Length == file.Value.Length),
                $"regenerating the cable report alone rewrites that report and leaves the other {untouched.Count} files of the study as they are");
            Check(only.WorkbookPath == reDone.WorkbookPath && only.DeflectionReportPath == reDone.DeflectionReportPath && File.Exists(only.WorkbookPath!) &&
                  only.MemberForceReportPath == reDone.MemberForceReportPath && only.SupportReactionReportPath == reDone.SupportReactionReportPath,
                "…and still reports the workbook and the other reports that are on disk");
            var none = new StudyPostProcessor().Generate(root, Tab3Capacities(2160), new DeflectionLimitBand(null, null), configured, outputs: StudyOutputs.DeflectionReport);
            Check(none.CableForceReportPath == only.CableForceReportPath && File.GetLastWriteTimeUtc(none.DeflectionReportPath!) > untouched[none.DeflectionReportPath!].Item1 &&
                  File.GetLastWriteTimeUtc(none.WorkbookPath!) == untouched[none.WorkbookPath!].Item1,
                "regenerating the deflection report alone does not rebuild the Excel workbook");

            ClientRendering(gen, dto, raw);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ---------- the report script, executed against the embedded data ----------

    private static void ClientRendering(CableForceReportGenerator gen, CableForceReportDto dto, StudyRawDataPackage raw)
    {
        var n = dto.Pretensions.Count;
        var page = new ReportPage(gen.BuildHtml(dto));
        Check(page.RequestedIds.All(page.StaticIds.Contains), "no stale DOM ids after the report restructuring");
        Check(page.Hidden("loadwrap"), "a single loading candidate hides the load selector");

        // initial state: safe auto-detection only
        Check(page.Text("ginfo").StartsWith("Side A 1 · Side B 1 · 3 cables currently unassigned.", StringComparison.Ordinal),
            "the report opens with the unambiguous pair grouped and says how many cables are unassigned");
        var charts = page.Charts("detail");
        Check(charts.Count == 3 * n && Sides(charts).SequenceEqual(Repeat(["A", "B", ""], n)) &&
              page.Html("detail").Contains("— Unassigned", StringComparison.Ordinal),
            "unassigned cables get their own chart per pretension candidate — never dropped, never mixed into a side");
        Check(page.Charts("overview").Count == 3 && new[] { ">H18<", ">H24<", ">H35<" }.All(charts[0].Contains) &&
              !page.Html("detail").Contains(">H20<") && !page.Html("detail").Contains(">H30<"),
            "every chart labels the study's own truss heights (H18 / H24 / H35)");
        Check(page.Html("glist").Contains("Cable1a-2") && Count(page.Html("glist"), "<select") == 5 &&
              Count(page.Html("glist"), ">Unassigned</option>") == 5 && Count(page.Html("glist"), "value=\"\" selected") == 3,
            "the grouping editor lists every cable found in the results with Side A / Side B / Unassigned");

        // stacked-bar semantics
        var p1a = charts[0];
        Check(Count(p1a, "data-dev=\"pickup\"") == 3 && Count(p1a, "data-dev=\"loss\"") == 0 && Count(p1a, "class=\"tb\"") == 3,
            "pickup: yellow target from 0 with the blue change stacked on top, one bar per truss height");
        var unassigned = charts[2];
        Check(Count(unassigned, "data-dev=\"loss\"") == 3 && Count(unassigned, "class=\"nb\"") == 3 && !unassigned.Contains("NaN"),
            "loss: a blue band from the final force up to the target; a cable without a target is drawn as final force only");
        var p3Unassigned = page.Charts("detail").Single(c => c.Contains("data-pret=\"P03\"") && c.Contains("data-side=\"\""));
        Check(Count(p3Unassigned, "class=\"na\"") >= 1 && Count(p3Unassigned, "data-dev=\"loss\"") == 2,
            "a missing final force is an n/a gap above its target — not a zero-height bar");
        var p2All = page.Charts("detail").Where(c => c.Contains("data-pret=\"P02\"")).ToArray();
        Check(p2All.All(c => c.Contains("class=\"na\"")), "a candidate with no result at one height leaves a gap in every chart");

        // limit lines: equal → one line, different → per-cable segments, none → no line
        Check(Lines(p1a, "common") == 1 && Lines(p1a, "cable") == 0 && p1a.Contains("Cable Force Limit"),
            "a side whose cables share one capacity gets a single cable-force limit line");
        Check(Lines(charts[1], "common") == 1 && Lines(unassigned, "common") == 0 && Lines(unassigned, "cable") == 1,
            "mixed availability: only the cable that has a capacity gets a limit segment — no fake limit for the others");
        var p3a = charts.Single(c => c.Contains("data-pret=\"P03\"") && c.Contains("data-side=\"A\""));
        Check(Count(p3a, "class=\"vl ex\"") == 3 && Count(p3a, "class=\"fc ex\"") == 3 && Count(p3a, "data-dev=\"pickup\"") == 3 &&
              !p1a.Contains("class=\"vl ex\"") && page.Html("detail").Contains("above limit"),
            "a final force above the limit is flagged on its value and marker only; the bar keeps its yellow / blue");

        // 2N charts for N pretension candidates and two populated sides
        page.Run("window.cableReport.applyGroups({'Cable1a-2':'A','EdgeCable04':'A','TransferNorth':'A','Cable1b-2':'B','Tendon-X':'B'})");
        charts = page.Charts("detail");
        Check(charts.Count == 2 * n && Sides(charts).SequenceEqual(Repeat(["A", "B"], n)) &&
              page.Text("ginfo").StartsWith("Side A 3 · Side B 2", StringComparison.Ordinal) && !page.Text("ginfo").Contains("unassigned"),
            "Apply Grouping re-renders client-side: N pretension candidates × two populated sides = 2N detailed charts");
        Check(Lines(charts[0], "common") == 0 && Lines(charts[0], "cable") == 1 && Lines(charts[1], "cable") == 2 &&
              charts[0].Contains(">per cable<"),
            "cables with different capacities get one limit segment each instead of a misleading global line");
        var tableBefore = GroupFree(page.Html("tb"));
        var govBefore = GroupFree(page.Html("govtable"));
        Check(Count(page.Html("tb"), "<tr>") == 8 * 5 && page.Html("tb").Contains("Result unavailable") && page.Html("tb").Contains("NoResult"),
            "Source Data keeps one row per candidate × cable with its result status");

        // one side empty
        page.Run("window.cableReport.applyGroups({'Cable1a-2':'B','Cable1b-2':'B','Tendon-X':'B','EdgeCable04':'B','TransferNorth':'B'})");
        Check(page.Charts("detail").Count == n && Count(page.Html("detail"), "No cables assigned to Side A.") == n,
            "an empty side shows 'No cables assigned to Side A.' instead of an empty chart");
        Check(GroupFree(page.Html("tb")) == tableBefore && GroupFree(page.Html("govtable")) == govBefore,
            "regrouping changes only the Current Group column — forces, deltas, capacities, utilisation and governing rows are identical");

        // nothing assigned → All / Unassigned state
        page.Run("window.cableReport.applyGroups({})");
        Check(page.Charts("detail").Count == n && page.Html("detail").Contains("All cables · Unassigned") &&
              !page.Html("detail").Contains("No cables assigned") && page.Text("ginfo").Contains("5 cables currently unassigned."),
            "with no side assigned the report shows one All / Unassigned chart per pretension candidate");

        // Auto Detect is a suggestion; Reset returns to the report default
        page.Run("window.cableReport.autoDetect()");
        Check(page.Charts("detail").Count == n && page.Text("gstate").Contains("Apply Grouping") &&
              Count(page.Html("glist"), "value=\"A\" selected") == 1 && Count(page.Html("glist"), "value=\"B\" selected") == 1,
            "Auto Detect fills the editor with its suggestion and waits for Apply Grouping");
        page.Run("window.cableReport.resetGroups()");
        Check(page.Charts("detail").Count == 3 * n && page.Text("ginfo").StartsWith("Side A 1 · Side B 1", StringComparison.Ordinal),
            "Reset returns to the report's default grouping");

        // utilisation mode
        page.Run("window.cableReport.setMode('ut')");
        charts = page.Charts("detail");
        Check(charts.All(c => Lines(c, "util") == 1 && Lines(c, "common") == 0 && Lines(c, "cable") == 0) &&
              page.Charts("overview").All(c => Lines(c, "util") == 1) && charts[0].Contains("Design Limit · 100%"),
            "% of capacity draws exactly one 100 % design-limit line per chart");
        Check(page.Text("yinfo").Contains("% of capacity") && charts[2].Contains("Capacity N/A") && !page.Html("detail").Contains("NaN") &&
              double.Parse(Regex.Match(page.Text("yinfo"), "→ ([0-9,]+)").Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture) >= 120,
            "utilisation leaves headroom above 100 % and shows cables without a capacity as N/A");
        page.Run("window.cableReport.setMode('kn')");

        // equal capacities everywhere → one line even with all cables in one chart
        var uniform = dto.Cables.ToDictionary(c => c, c => c == "Tendon-X" ? 31106.5000001 : 31106.5);
        var uniformPage = new ReportPage(gen.BuildHtml(gen.Build(raw.Results, raw.BaselineModelName, raw.StudyMode,
            raw.CableResultCase, uniform, ["cable"])));
        uniformPage.Run("window.cableReport.applyGroups({})");
        Check(uniformPage.Charts("detail").Count == n && uniformPage.Charts("detail").All(c => Lines(c, "common") == 1 && Lines(c, "cable") == 0) &&
              uniformPage.Charts("overview").All(c => Lines(c, "common") == 1),
            "equal capacities within tolerance draw one continuous limit line in every chart");

        // no capacity at all → no limit anywhere, utilisation switched off
        var bare = new ReportPage(gen.BuildHtml(gen.Build(raw.Results, raw.BaselineModelName, raw.StudyMode, raw.CableResultCase)));
        Check(!bare.Html("detail").Contains("data-limit") && !bare.Html("overview").Contains("data-limit") && bare.Disabled("vu") &&
              !bare.Html("tb").Contains("%") && bare.Html("govtable").Contains("Capacity N/A"),
            "without any capacity no limit line is drawn and utilisation reads N/A");
    }

    // ---------- helpers ----------

    private static CandidateResultDto Candidate(long run, string load, string height, double heightM, string pret,
        IReadOnlyList<CableForceResultDto> cableForces)
    {
        var key = $"{load}_{height}_{pret}";
        return new CandidateResultDto(key, $@"C:\Study\{key}.sdb", @"C:\base.sdb", "m.sdb", IterationStudyMode.Combined,
            new CandidateParametersDto(run, key, load, height, pret, 5, "5 kPa", heightM, 0, null, $"{pret} · mixed"),
            DateTimeOffset.Now, DateTimeOffset.Now, DateTimeOffset.Now, CandidateExtractionStatus.ExtractedOk, [], [], cableForces);
    }

    private static bool Near(double? actual, double expected) =>
        actual is { } a && Math.Abs(a - expected) <= 1e-3 * Math.Max(1, Math.Abs(expected));

    private static int Count(string text, string needle)
    {
        var n = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static int Lines(string chart, string kind) => Count(chart, $"data-limit=\"{kind}\"");

    private static IEnumerable<string> Sides(IEnumerable<string> charts) =>
        charts.Select(c => Regex.Match(c, "data-side=\"([^\"]*)\"").Groups[1].Value);

    private static IEnumerable<string> Repeat(string[] pattern, int times) =>
        Enumerable.Range(0, times).SelectMany(_ => pattern);

    /// <summary>Table markup with the presentation-only group names removed.</summary>
    private static string GroupFree(string tableHtml) => Regex.Replace(tableHtml, "<td>(Side A|Side B|Unassigned)</td>", "");

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
