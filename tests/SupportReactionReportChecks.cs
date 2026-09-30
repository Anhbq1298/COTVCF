using System.IO;
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
        Check(html.Contains("Candidate definitions") && html.Contains("Truss-height candidates"),
            "support-reaction HTML includes the shared L/H/P candidate definitions");

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
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
