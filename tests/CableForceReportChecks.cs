using System.IO;
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
        Check(dto.YMin < dto.YMax && dto.YMin > 0, "shared Y-scale is derived from the actual forces");

        var missingRow = dto.Rows.Single(r => r.Run == 3);
        Check(missingRow.Forces["Cable1b-2"] is null, "a missing cable-force result is null, never 0 kN");
        Check(dto.Warnings.Any(w => w.IterationKey == missingRow.IterationKey), "the missing result raises a warning");

        var html = gen.BuildHtml(dto);
        Check(html.Contains("id=\"report-data\"") && html.Contains("Cable Forces Viewer") &&
              html.Contains("Result Library") && html.Contains("Result unavailable") &&
              !html.Contains("http://") && !html.Contains("https://") && !html.Contains("cdn."),
            "Cable Forces HTML is self-contained, has the Result Library, and shows 'Result unavailable' for gaps");
        Check(html.Contains("Candidate definitions") && html.Contains("candidate-pretension-matrix"),
            "Cable Forces HTML includes the shared compact L/H/P candidate definitions");
        var htmlIds = Regex.Matches(html, "\\bid=\\\"([^\\\"]+)\\\"")
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var scriptIds = Regex.Matches(html, "(?:document\\.getElementById|el)\\(\\\"([^\\\"]+)\\\"\\)")
            .Select(match => match.Groups[1].Value).ToArray();
        Check(scriptIds.All(htmlIds.Contains) &&
              new[] { "tornado", "trellis", "heatmap", "gov", "meters" }.All(htmlIds.Contains),
            "Cable Forces report script references only DOM elements present in the narrative report markup");

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

            var overridden = new CableForceReportGenerator()
                .RegenerateFromWorkbook(tmp, new Dictionary<string, double> { ["Cable1a-2"] = 999 });
            Check(File.ReadAllText(overridden).Contains("\"Cable1a-2\":999", StringComparison.Ordinal),
                "a supplied capacity overrides the workbook without rewriting it");

            ConsolidatedResultWriter.UpdateCableCapacities(xlsx, new Dictionary<string, double>());
            var withoutCapacity = new CableForceReportGenerator()
                .RegenerateFromWorkbook(tmp, new Dictionary<string, double>());
            Check(ConsolidatedResultWriter.ReadCableCapacities(xlsx).Count == 0 &&
                  !File.ReadAllText(withoutCapacity).Contains("\"Cable1a-2\":31106.5", StringComparison.Ordinal),
                "regeneration clears stale workbook capacities when no current capacity can be derived");
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
