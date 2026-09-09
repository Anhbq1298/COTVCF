using COT_VCF.Application.Results;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;
using System.IO;

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
            var parameters = new CandidateParametersDto(i, key, key[..3], "H01", key[^3..],
                2.5, "DL 1.0 + LL 2.5 kPa", 20, 0, 500, "pretension");
            results.Add(new CandidateResultDto(key, $@"C:\Study\{key}.sdb", @"C:\base.sdb", "COT.sdb",
                IterationStudyMode.FixHeight, parameters, DateTimeOffset.Now, DateTimeOffset.Now, DateTimeOffset.Now,
                CandidateExtractionStatus.ExtractedOk, [], [], [], [top, bottom]));
        }

        var generator = new MemberForceReportGenerator();
        var dto = generator.Build(results, "COT.sdb", IterationStudyMode.FixHeight);
        Check(dto.Groups.Count == 2 && dto.Groups.All(group => group.Series.Count == 60),
            "member-force report retains every candidate in both chord groups");
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
        Check(html.Contains("station-hit") && html.Contains("Frame object:") && html.Contains("Analysis element:"),
            "member-force chart exposes exact station values and SAP identifiers on hover");
        Check(html.Contains("Start + middle + end per element") && html.Contains("All SAP stations") &&
              html.Contains("elements.flatMap") && html.Contains("sampleLabel:label"),
            "member-force chart defaults to three positions per SAP analysis element without discarding detailed stations");

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
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
