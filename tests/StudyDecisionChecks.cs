using COT_VCF.Application.Results;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;

internal static class StudyDecisionChecks
{
    public static void Run()
    {
        var candidates = new[]
        {
            Candidate("PASS_LOW", 1, 10, 10),
            Candidate("PASS_HIGH", 2, 20, 10),
            Candidate("WARNING", 3, 60, 10),
            Candidate("FAIL", 4, 80, 10),
            Candidate("INCOMPLETE", 5, null, 10)
        };
        var criteria = new StudyDecisionCriteria(50, 75, null, null, 0.9, 1, 0, 0);
        var decisions = new StudyDecisionService().Evaluate(candidates, criteria);

        Check(decisions.Select(item => item.IterationKey).SequenceEqual(
                  ["PASS_LOW", "PASS_HIGH", "WARNING", "FAIL", "INCOMPLETE"]) &&
              decisions.Select(item => item.Status).SequenceEqual(
                  [CandidateDecisionStatus.Pass, CandidateDecisionStatus.Pass, CandidateDecisionStatus.Warning,
                   CandidateDecisionStatus.Fail, CandidateDecisionStatus.Incomplete]) &&
              decisions.Select(item => item.Rank).SequenceEqual([1, 2, 3, 4, 5]) &&
              decisions[^1].Score is null,
            "Decision service ranks complete Pass, Warning, and Fail candidates before incomplete results");
    }

    private static CandidateResultDto Candidate(string key, long runIndex, double? uzMm, double height)
    {
        IReadOnlyList<MonitoredPointGroupResultDto> groups = uzMm is double value
            ? [new MonitoredPointGroupResultDto("DEFLECTION", "SERVICE", ProfileCoordinateAxis.X,
                [new MonitoredPointResultDto("P1", 0, 0, 0, 1, value, ResultValueStatus.Valid, null)])]
            : [];
        return new CandidateResultDto(key, $@"C:\Study\{key}\{key}.sdb", @"C:\base.sdb", "base.sdb",
            IterationStudyMode.Combined,
            new CandidateParametersDto(runIndex, key, "L01", "H01", "P01", 1, "L01", height, 0, 100, "P01"),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            uzMm.HasValue ? CandidateExtractionStatus.ExtractedOk : CandidateExtractionStatus.ExtractionFailed,
            groups, []);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
