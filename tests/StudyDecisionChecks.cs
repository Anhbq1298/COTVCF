using COT_VCF.Application.Results;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;

internal static class StudyDecisionChecks
{
    public static void Run()
    {
        // Peak downward deflection δ = −min(Uz): supports sit at 0, the middle point carries the span deflection.
        var candidates = new[]
        {
            Candidate("IN_LOW", 1, [0, -110, 0]),
            Candidate("IN_HIGH", 2, [0, -140, 0]),
            Candidate("EDGE_MAX", 3, [0, -150, 0]),
            Candidate("TOO_STIFF", 4, [0, -60, 0]),
            Candidate("HOGGING", 5, [0, 120, 0]),
            Candidate("TOO_SOFT", 6, [0, -180, 0]),
            Candidate("INCOMPLETE", 7, null)
        };
        var criteria = new StudyDecisionCriteria(100, 150, null, null, 0.9, 1, 0, 0);
        var decisions = new StudyDecisionService().Evaluate(candidates, criteria);

        Check(decisions.Select(item => item.IterationKey).SequenceEqual(
                  ["IN_LOW", "IN_HIGH", "EDGE_MAX", "TOO_STIFF", "HOGGING", "TOO_SOFT", "INCOMPLETE"]) &&
              decisions.Select(item => item.Status).SequenceEqual(
                  [CandidateDecisionStatus.Pass, CandidateDecisionStatus.Pass, CandidateDecisionStatus.Pass,
                   CandidateDecisionStatus.Fail, CandidateDecisionStatus.Fail, CandidateDecisionStatus.Fail,
                   CandidateDecisionStatus.Incomplete]) &&
              decisions.Select(item => item.Rank).SequenceEqual([1, 2, 3, 4, 5, 6, 7]) &&
              decisions[^1].Score is null,
            "Decision service passes only candidates whose peak downward U3 lies inside the 100–150 mm limit band");
        var hogging = decisions.Single(item => item.IterationKey == "HOGGING");
        Check(hogging.PeakDownUzMm == 0 && hogging.MaxAbsUzMm == 120 &&
              hogging.GoverningReason.Contains("below limit band Min", StringComparison.Ordinal),
            "An upward-deflected candidate fails the band on its peak downward deflection, not on |U3|");
        Check(decisions.Single(item => item.IterationKey == "TOO_SOFT").GoverningReason.Contains("exceeds limit band Max", StringComparison.Ordinal),
            "A candidate deflecting past the Max limit reports the exceeded bound");

        var open = new StudyDecisionService().Evaluate(candidates, StudyDecisionCriteria.Default);
        Check(open.Where(item => item.Status != CandidateDecisionStatus.Incomplete).All(item =>
                  item.Status == CandidateDecisionStatus.Pass && item.GoverningReason.Contains("no deflection limit band", StringComparison.Ordinal)),
            "Without a limit band deflection is not a pass/fail criterion");

        var maxOnly = new StudyDecisionService().Evaluate(candidates, criteria with { DeflectionLimitMinMm = null });
        Check(maxOnly.Where(item => item.Status == CandidateDecisionStatus.Pass).Select(item => item.IterationKey).Order(StringComparer.Ordinal)
                  .SequenceEqual(["EDGE_MAX", "HOGGING", "IN_HIGH", "IN_LOW", "TOO_STIFF"]),
            "A Max-only limit band is an open-ended serviceability cap");

        try
        {
            StudyDecisionService.Validate(criteria with { DeflectionLimitMinMm = 160 });
            throw new Exception("FAIL: A limit band with Max below Min was accepted");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("limit band", StringComparison.Ordinal))
        {
            Console.WriteLine("PASS: A limit band with Max below Min is rejected");
        }

        Check(DeflectionLimitBand.PeakDownMm([]) is null && DeflectionLimitBand.PeakDownMm([0, -5, -2]) == 5 &&
              DeflectionLimitBand.PeakDownMm([3, 7]) == -3 && DeflectionLimitBand.PeakDownMm([0]) is double zero && !double.IsNegative(zero),
            "Peak downward deflection is −min(Uz), with an unsigned zero for level supports");
        Check(new DeflectionLimitBand(100, 150).Label == "100–150 mm" && new DeflectionLimitBand(null, 150).Label == "≤ 150 mm" &&
              new DeflectionLimitBand(100, null).Label == "≥ 100 mm" && !new DeflectionLimitBand(null, null).IsActive,
            "Limit band labels cover closed, Max-only, Min-only and inactive bands");

        DeflectionReportCarriesTheLimitBand(candidates, criteria, decisions);
    }

    private static void DeflectionReportCarriesTheLimitBand(
        IReadOnlyList<CandidateResultDto> candidates, StudyDecisionCriteria criteria, IReadOnlyList<CandidateDecisionResult> decisions)
    {
        var generator = new DeflectionReportGenerator();
        var dto = generator.Build(candidates, @"C:\Study", "base", @"C:\base.sdb", IterationStudyMode.Combined, [], criteria.DeflectionBand);
        var series = dto.Groups.Single().Series;
        var passing = decisions.Where(item => item.Status == CandidateDecisionStatus.Pass).Select(item => item.IterationKey).ToHashSet();
        Check(dto.LimitBand == new DeflectionLimitBandDto(100, 150) &&
              series.All(s => s.PeakDownMm == decisions.Single(d => d.IterationKey == s.IterationKey).PeakDownUzMm) &&
              series.Where(s => s.PeakDownMm is double down && criteria.DeflectionBand.Contains(down)).Select(s => s.IterationKey).ToHashSet().SetEquals(passing),
            "Deflection report carries the limit band and each candidate's peak ↓Uz, so its in-band count matches the Shortlist");

        var html = generator.BuildHtml(dto);
        Check(html.Contains("\"limitBand\":{\"minMm\":100,\"maxMm\":150}", StringComparison.Ordinal) &&
              html.Contains("limit band</b> 100–150 mm", StringComparison.Ordinal) &&
              html.Contains("id=\"bandToggle\"", StringComparison.Ordinal),
            "Deflection report HTML embeds the band and explains the shaded strip");

        var none = generator.Build(candidates, @"C:\Study", "base", @"C:\base.sdb", IterationStudyMode.Combined, [], new DeflectionLimitBand(null, null));
        Check(none.LimitBand is null && generator.BuildHtml(none).Contains("\"limitBand\":null", StringComparison.Ordinal),
            "Deflection report omits the band when none is configured");
    }

    private static CandidateResultDto Candidate(string key, long runIndex, double[]? uzMm)
    {
        IReadOnlyList<MonitoredPointGroupResultDto> groups = uzMm is { } values
            ? [new MonitoredPointGroupResultDto("DEFLECTION", "SERVICE", ProfileCoordinateAxis.X,
                values.Select((uz, i) => new MonitoredPointResultDto($"P{i + 1}", i, 0, 0, i + 1, uz, ResultValueStatus.Valid, null)).ToArray())]
            : [];
        return new CandidateResultDto(key, $@"C:\Study\{key}\{key}.sdb", @"C:\base.sdb", "base.sdb",
            IterationStudyMode.Combined,
            new CandidateParametersDto(runIndex, key, "L01", "H01", "P01", 1, "L01", 10, 0, 100, "P01"),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            uzMm is not null ? CandidateExtractionStatus.ExtractedOk : CandidateExtractionStatus.ExtractionFailed,
            groups, []);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
