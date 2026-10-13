using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcotis.Desktop.Application.Results;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Domain.Results;

/// <summary>Tab 06A: candidate metrics, the two acceptance criteria, and the optimized report set.</summary>
internal static class OptimizationChecks
{
    public static void Run()
    {
        Metrics();
        Criteria();
        NotAssessed();
        Settings();
        var root = Path.Combine(Path.GetTempPath(), "mcotis_opt_" + Guid.NewGuid().ToString("N"));
        try
        {
            MetricsStore(Path.Combine(root, "store"));
            OptimizedSet(Path.Combine(root, "set"));
            ThousandCandidates(Path.Combine(root, "thousand"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ---------- fixture ----------

    private static readonly IReadOnlyDictionary<string, double> Capacities =
        new Dictionary<string, double>(StringComparer.Ordinal) { ["Cable-A"] = 1000, ["Cable-B"] = 1000 };

    private static double Delta(int l, int h, int p) => 40 + 30 * (h - 1) + 5 * (p - 1) + 20 * (l - 1);         // peak downward deflection, mm
    private static double ForceA(int l, int p) => 600 + 40 * (p - 1) + 50 * (l - 1);                              // governing cable, kN

    /// <summary>L × H × P candidates. δ grows with height and pretension; Cable-A's force with pretension, so with
    /// 1,000 kN capacities its utilisation is 0.60 + 0.04 (p − 1). <paramref name="full"/> adds a chord group and
    /// located foundation supports so every report of the set can be written.</summary>
    private static List<CandidateResultDto> Study(int loads = 1, int heights = 3, int pretensions = 9, bool full = false)
    {
        var results = new List<CandidateResultDto>();
        long run = 0;
        for (var l = 1; l <= loads; l++)
        for (var h = 1; h <= heights; h++)
        for (var p = 1; p <= pretensions; p++)
        {
            run++;
            var key = $"L{l:00}_H{h:00}_P{p:00}";
            var delta = Delta(l, h, p);
            var points = new[] { -delta * .5, -delta, -delta * .4 }.Select((uz, n) =>
                new MonitoredPointResultDto($"M{n + 1}", n * 10.0, 0, 0, n + 1, uz, ResultValueStatus.Valid, null)).ToArray();
            var cables = new[]
            {
                new CableForceResultDto("Cable-A", 500, ForceA(l, p), ResultValueStatus.Valid),
                new CableForceResultDto("Cable-B", 500, 500 + 10 * (p - 1), ResultValueStatus.Valid),
            };
            List<MemberForceGroupResultDto> members = [];
            List<SupportReactionGroupResultDto> supports = [];
            if (full)
            {
                MemberForceStationResultDto Station(int sequence, double station, double force) => new(
                    $"TC-{sequence}", sequence, $"TC-{sequence}-1", station - (sequence - 1) * 10, station - (sequence - 1) * 10, station, "Max", 0,
                    force, 0, 0, 0, 0, 0, ResultValueStatus.Valid);
                members.Add(new("TopChord", "[O]-TopChord", "CASE-1", [Station(1, 0, -900 - 10 * p), Station(1, 10, -1000 - 10 * p - 50 * h), Station(2, 20, -950 - 10 * p)]));
                SupportReactionPointResultDto Support(string point, int sequence, double x, double fz) => new(
                    point, sequence, x, 0, 0, "Max", 0, fz * .1, 0, fz, 0, 0, 0, ResultValueStatus.Valid);
                supports.Add(new("Left", "[O]-FoundationLeft", "CASE-1", [Support("200", 1, -60, -400 - 10 * p), Support("198", 2, -50, 5000 + 100 * h)]));
                supports.Add(new("Right", "[O]-FoundationRight", "CASE-1", [Support("199", 1, 50, 5000 + 100 * h), Support("205", 2, 60, -400 - 10 * p)]));
            }
            results.Add(new CandidateResultDto(key, $@"C:\Study\{key}.sdb", @"C:\base.sdb", "COT.sdb", IterationStudyMode.Combined,
                new CandidateParametersDto(run, key, $"L{l:00}", $"H{h:00}", $"P{p:00}", 5, $"DL 10 + LL {5 * l} kPa", 20 + 2.5 * (h - 1), 0, null, $"P{p:00} · mixed"),
                DateTimeOffset.Now, DateTimeOffset.Now, DateTimeOffset.Now, CandidateExtractionStatus.ExtractedOk,
                [new MonitoredPointGroupResultDto("Monitored Deflection", "SERVICE", ProfileCoordinateAxis.X, points)], [], cables, members, supports));
        }
        return results;
    }

    private static QualificationCriteria Limits(double? min, double? max, double utilisation = 0.8, bool deflection = true, bool cable = true) =>
        new(deflection, new DeflectionLimitBand(min, max), cable, utilisation);

    private static StudyQualification Qualify(IReadOnlyList<CandidateResultDto> study, QualificationCriteria criteria,
        IReadOnlyDictionary<string, double>? capacities = null) =>
        StudyQualifier.Evaluate(study.Select(CandidateMetrics.From).ToArray(), criteria, capacities ?? Capacities);

    private static CandidateQualification Of(StudyQualification q, string key) => q.Candidates.Single(c => c.Metrics.IterationKey == key);

    // ---------- checks ----------

    private static void Metrics()
    {
        var study = Study();
        var m = CandidateMetrics.From(study.Single(r => r.IterationKey == "L01_H02_P03"));
        Check(m is { PeakDownMm: 80, DeflectionProblem: null, ExtractionProblem: null, LoadId: "L01", HeightId: "H02", PretensionId: "P03", TrussHeightM: 22.5 } &&
              m.Cables.Select(c => (c.CableName, c.ForceKN)).SequenceEqual([("Cable-A", 680.0), ("Cable-B", 520.0)]),
            "candidate metrics: δ = −min(Uz) over the monitored points, and the final analysed force of every cable");

        var first = study[0];
        var broken = first with { Groups = [first.Groups[0] with { Points = [first.Groups[0].Points[0], first.Groups[0].Points[1] with { Status = ResultValueStatus.NoResult, UzMm = null }] }] };
        var lost = CandidateMetrics.From(broken);
        var noPoints = CandidateMetrics.From(first with { Groups = [] });
        var noCable = CandidateMetrics.From(first with { CableForces = [first.CableForces![0] with { Status = ResultValueStatus.NoResult, ForceKN = null }] });
        var failed = CandidateMetrics.From(first with { ExtractionStatus = CandidateExtractionStatus.AnalysisFailed });
        Check(lost.PeakDownMm is null && lost.DeflectionProblem!.Contains("M2") && noPoints is { PeakDownMm: null, DeflectionProblem: "No monitored Uz results." } &&
              noCable.Cables.Single() is { CableName: "Cable-A", ForceKN: null } && failed.ExtractionProblem!.Contains("AnalysisFailed"),
            "a missing or invalid result leaves the metric unstated with its reason — δ is never taken over the remaining points, a force never read as 0");
    }

    private static void Criteria()
    {
        var study = Study();
        var q = Qualify(study, Limits(50, 100));
        var s = q.Summary;
        Check(s is { Total: 27, Qualified: 11, FailedDeflectionOnly: 7, FailedCableOnly: 4, FailedBoth: 5, NotAssessed: 0, NotQualified: 16 } &&
              s.Headline == "Qualified 11 / 27" && q.Qualified.Count() == 11,
            "a candidate qualifies when it passes every active criterion — 11 of 27 under δ 50–100 mm and cable utilisation ≤ 0.80");
        Check(Of(q, "L01_H01_P03") is { Status: QualificationStatus.Qualified, Deflection: CriterionVerdict.Pass, Metrics.PeakDownMm: 50 } &&
              Of(q, "L01_H01_P02") is { Status: QualificationStatus.NotQualified, Deflection: CriterionVerdict.Fail, Cable: CriterionVerdict.Pass } &&
              Of(q, "L01_H02_P07") is { Deflection: CriterionVerdict.Pass, Metrics.PeakDownMm: 100, Cable: CriterionVerdict.Fail } &&
              Of(q, "L01_H01_P06") is { Status: QualificationStatus.Qualified, GoverningUtilisation: 0.8, GoverningCable: "Cable-A", GoverningForceKN: 800, GoverningCapacityKN: 1000 },
            "the bounds are inclusive: δ exactly at Min or Max passes, and so does a cable exactly at the utilisation limit");
        Check(Of(q, "L01_H01_P02").Reason == "δ 45 mm is below Min 50 mm" && Of(q, "L01_H03_P02").Reason == "δ 105 mm exceeds Max 100 mm" &&
              Of(q, "L01_H01_P08").Reason == "Cable-A utilisation 88.0 % exceeds 80.0 %" &&
              Of(q, "L01_H03_P09").Reason == "δ 140 mm exceeds Max 100 mm · Cable-A utilisation 92.0 % exceeds 80.0 %" &&
              Of(q, "L01_H01_P03").Reason == "Passes every active criterion",
            "a candidate that is not qualified names each failed criterion with its governing value, the limit, and the governing cable");
        Check(s is { MinPeakDownMm: 40, MaxPeakDownMm: 140 } && Math.Abs(s.MinUtilisation!.Value - 0.6) < 1e-12 && Math.Abs(s.MaxUtilisation!.Value - 0.92) < 1e-12 &&
              s.CablesWithCapacity.SequenceEqual(["Cable-A", "Cable-B"]) && s.CablesWithoutCapacity.Count == 0,
            "the summary carries the study's own range of δ and of governing cable utilisation, as hints for the limits");

        Check(Qualify(study, Limits(null, 100, cable: false)).Summary.Qualified == 17 && Qualify(study, Limits(105, null, cable: false)).Summary.Qualified == 10 &&
              Qualify(study, Limits(null, null)) is { Criteria.DeflectionActive: false, Summary: { Qualified: 18, FailedDeflectionOnly: 0 } },
            "a blank deflection bound is open; with both blank the deflection criterion does not apply");
        var none = Qualify(study, Limits(50, 100, deflection: false, cable: false));
        Check(none.Summary.Qualified == 27 && !none.Criteria.AnyActive && none.Candidates.All(c => c is { Deflection: CriterionVerdict.Inactive, Cable: CriterionVerdict.Inactive, Reason: "No criterion active" }) &&
              Of(none, "L01_H03_P09").GoverningUtilisation is > 0.91,
            "with both criteria off every candidate qualifies, and the governing utilisation is still reported");
        Check(Qualify(study, Limits(50, 100, utilisation: 1.0)).Summary is { Qualified: 15, FailedCableOnly: 0 },
            "the cable limit is a share of the design capacity: at 1.00 no cable of this study fails");

        static bool Throws(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
        Check(Limits(-5, null).Problem()!.Contains("Min") && Limits(100, 50).Problem()!.Contains("greater than or equal") &&
              Limits(null, null, utilisation: 0).Problem()!.Contains("utilisation") && Limits(null, null, utilisation: 0, cable: false).Problem() is null &&
              Throws(() => Qualify(study, Limits(100, 50))) && Throws(() => Qualify(study, Limits(null, null, utilisation: double.NaN))),
            "unusable limits are reported and never applied");
        Check(Limits(50, 150).Label == "Deflection δ 50–150 mm · Cable utilisation ≤ 0.80" &&
              Limits(null, null, deflection: false, cable: false).Label == "Deflection: no limit · Cable force: no limit",
            "the criteria describe themselves for the reports");
    }

    private static void NotAssessed()
    {
        var study = Study(heights: 1, pretensions: 3);                  // δ 40, 45, 50 · utilisation 0.60, 0.64, 0.68
        var a = study[0];
        study[0] = a with { Groups = [a.Groups[0] with { Points = [a.Groups[0].Points[0] with { Status = ResultValueStatus.NoResult, UzMm = null }] }] };
        var q = Qualify(study, Limits(null, 100));
        Check(Of(q, "L01_H01_P01") is { Status: QualificationStatus.NotAssessed, Deflection: CriterionVerdict.NotAssessed, Cable: CriterionVerdict.Pass, StatusText: "Not assessed" } &&
              Of(q, "L01_H01_P01").Reason.Contains("Uz unavailable at M1") && q.Summary is { Qualified: 2, NotAssessed: 1, NotQualified: 0 },
            "a candidate whose deflection cannot be stated is not assessed — never qualified, never read as 0 mm");
        Check(Of(Qualify(study, Limits(null, 100, deflection: false)), "L01_H01_P01").Status == QualificationStatus.Qualified,
            "a missing result only matters to a criterion that is active");
        Check(Of(Qualify(study, Limits(null, 100, utilisation: 0.5)), "L01_H01_P01") is { Status: QualificationStatus.NotQualified, Cable: CriterionVerdict.Fail, Deflection: CriterionVerdict.NotAssessed },
            "a criterion that fails makes the candidate not qualified whatever else is missing");

        var onlyA = new Dictionary<string, double>(StringComparer.Ordinal) { ["Cable-A"] = 1000, ["Cable-B"] = 0 };
        var missing = Qualify(Study(heights: 1, pretensions: 3), Limits(null, null), onlyA);
        Check(missing.Summary is { Qualified: 0, NotAssessed: 3 } && missing.Summary.CablesWithoutCapacity.SequenceEqual(["Cable-B"]) &&
              missing.Candidates.All(c => c.Cable == CriterionVerdict.NotAssessed && c.Reason == "No design capacity for Cable-B." && c.GoverningCable == "Cable-A"),
            "a cable without a design capacity cannot be assessed — it never passes by default");
        Check(Qualify(Study(heights: 1, pretensions: 3), Limits(null, null, utilisation: 0.62), onlyA).Summary is { NotQualified: 2, NotAssessed: 1 },
            "…but a cable that is above the limit still fails the candidate");

        var b = Study(heights: 1, pretensions: 2);
        b[0] = b[0] with { CableForces = [b[0].CableForces![0], b[0].CableForces![1] with { Status = ResultValueStatus.NoResult, ForceKN = null }] };
        b[1] = b[1] with { ExtractionStatus = CandidateExtractionStatus.ExtractionFailed };
        var gaps = Qualify(b, Limits(null, null));
        Check(Of(gaps, "L01_H01_P01") is { Status: QualificationStatus.NotAssessed, Reason: "Cable force unavailable for Cable-B." } &&
              Of(gaps, "L01_H01_P02").Status == QualificationStatus.NotAssessed && Of(gaps, "L01_H01_P02").Reason.StartsWith("Extraction status is ExtractionFailed") &&
              Of(Qualify(b, Limits(null, null, deflection: false, cable: false)), "L01_H01_P02").Status == QualificationStatus.NotAssessed &&
              Qualify(b.Select(r => r with { CableForces = [] }).ToArray(), Limits(null, null)).Candidates.All(c => c.Reason.Contains("No cable-force results.")),
            "an unavailable cable force, no cable results, or an analysis that did not complete: not assessed");
    }

    private static void Settings()
    {
        var output = new OutputSettingsConfiguration();
        Check(QualificationCriteria.From(output) is { DeflectionEnabled: true, CableEnabled: true, MaxCableUtilization: 1.0, DeflectionActive: false, CableActive: true },
            "Tab 06A defaults: both criteria on, cable limit 1.00, no deflection bound until one is entered");
        var changes = new List<string?>();
        output.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        output.DeflectionLimitMinMm = 50; output.DeflectionLimitMaxMm = 150; output.MaxCableUtilization = 0.85; output.CableCriterionEnabled = false;
        Check(QualificationCriteria.From(output) == new QualificationCriteria(true, new DeflectionLimitBand(50, 150), false, 0.85) &&
              changes.Contains(nameof(OutputSettingsConfiguration.MaxCableUtilization)) && changes.Contains(nameof(OutputSettingsConfiguration.CableCriterionEnabled)),
            "the criteria are the Tab 04 limit band plus the Tab 06A switches and cable limit — one deflection range, edited in either place");

        var state = new ApplicationState();
        state.OutputSettings.DeflectionCriterionEnabled = false; state.OutputSettings.MaxCableUtilization = 0.9;
        var dto = new ConfigurationExtractor().ExtractOutputSection(state);
        var older = JsonSerializer.Deserialize<OutputSettingsConfigDto>("{\"DeflectionLimitMaxMm\":120}")!;
        Check(dto is { DeflectionCriterionEnabled: false, CableCriterionEnabled: true, MaxCableUtilization: 0.9 } &&
              older is { DeflectionCriterionEnabled: true, CableCriterionEnabled: true, MaxCableUtilization: 1.0, DeflectionLimitMaxMm: 120 },
            "the criteria are saved with the project; a project saved before Tab 06A loads with both criteria on and the limit at 1.00");
        Check(typeof(Mcotis.Desktop.Application.StudyExecutionSnapshot).GetProperties().All(p =>
                  !p.Name.Contains("Criteri") && !p.Name.Contains("Utiliz") && !p.Name.Contains("DeflectionLimit")),
            "the acceptance criteria are not part of the execution snapshot, so they can never invalidate a run");
    }

    /// <summary>A small complete study for the Tab 06A UI check: 27 candidates with every result group.</summary>
    internal static void WriteStudy(string studyFolder, string baselinePath) =>
        WriteRaw(studyFolder, Study(full: true), new(StringComparer.Ordinal) { ["Cable-A"] = 1000, ["Cable-B"] = 1000 }, baselinePath);

    private static string WriteRaw(string studyFolder, IReadOnlyList<CandidateResultDto> results, Dictionary<string, double>? stored = null,
        string baselinePath = @"C:\base.sdb") =>
        new StudyRawDataStore().Write(studyFolder, new StudyRawDataPackage
        {
            GeneratedAt = DateTimeOffset.UtcNow, BaselineModelName = "COT.sdb", BaselineModelPath = baselinePath, StudyMode = IterationStudyMode.Combined,
            DeflectionPoints = ["M1", "M2", "M3"], DeflectionOutputCase = "SERVICE", CableResultCase = "CASE-1",
            CableDesignCapacities = stored ?? new(StringComparer.Ordinal), Results = results.ToList(),
        }).JsonPath;

    private static void MetricsStore(string study)
    {
        var raw = WriteRaw(study, Study(), new(StringComparer.Ordinal) { ["Cable-A"] = 1200 });
        var index = new StudyMetricsStore().Load(study);
        var sidecar = StudyMetricsStore.SidecarPath(study);
        Check(index.Candidates.Count == 27 && index is { BaselineModelName: "COT.sdb", StudyMode: IterationStudyMode.Combined } &&
              index.StoredCableCapacities["Cable-A"] == 1200 && index.RawLength == new FileInfo(raw).Length && File.Exists(sidecar) &&
              Path.GetDirectoryName(sidecar) == Path.GetDirectoryName(raw),
            "the metrics are derived from the raw package once and kept in a sidecar beside it");

        // prove the next load comes from the sidecar: the raw file is made unreadable without touching its identity
        var stamp = File.GetLastWriteTimeUtc(raw);
        var bytes = File.ReadAllBytes(raw);
        File.WriteAllBytes(raw, new byte[bytes.Length]);
        File.SetLastWriteTimeUtc(raw, stamp);
        var cached = new StudyMetricsStore().Load(study);
        Check(cached.Candidates.Count == 27 && cached.Candidates[4].PeakDownMm == index.Candidates[4].PeakDownMm &&
              cached.Candidates[4].Cables.Count == 2 && cached.RawIdentity == index.RawIdentity,
            "while the raw file is unchanged the metrics come from the sidecar — the raw package is not read again");

        File.SetLastWriteTimeUtc(WriteRaw(study, Study(pretensions: 2)), stamp.AddMinutes(1));
        var rebuilt = new StudyMetricsStore().Load(study);
        Check(rebuilt.Candidates.Count == 6 && rebuilt.RawIdentity != index.RawIdentity && new StudyMetricsStore().Load(study).Candidates.Count == 6,
            "when the raw data changes the metrics are derived again");
        File.WriteAllText(sidecar, "{ not json");
        Check(new StudyMetricsStore().Load(study).Candidates.Count == 6, "an unreadable sidecar is ignored and rebuilt — it is only a cache");
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void OptimizedSet(string study)
    {
        WriteRaw(study, Study(full: true));
        var print = new ReportPrintSettings { CompanyName = "ACME", ProjectName = "Tower" };
        var full = new StudyPostProcessor().Generate(study, Capacities, new DeflectionLimitBand(50, 100), ["Cable-A", "Cable-B"], print);
        string[] fullReports = [full.DeflectionReportPath!, full.MemberForceReportPath!, full.SupportReactionReportPath!, full.CableForceReportPath!, full.WorkbookPath!];
        var before = fullReports.Select(Hash).ToArray();
        Check(!File.Exists(Path.Combine(study, "Study_Shortlist.html")) && !File.Exists(Path.Combine(study, "Study_Shortlist.xlsx")) &&
              !File.ReadAllText(full.DeflectionReportPath!).Contains("Shortlist") && !File.ReadAllText(full.DeflectionReportPath!).Contains("Optimization Summary"),
            "Tab 06 no longer writes a shortlist, and its reports do not link one — nor an optimization summary that is not theirs");

        var criteria = Limits(50, 100);
        var set = new StudyOptimizationProcessor().Generate(study, criteria, Capacities, ["Cable-A", "Cable-B"], print);
        var folder = Path.Combine(study, "Optimized");
        Check(set.Folder == folder && set.Qualification.Summary is { Qualified: 11, Total: 27 } &&
              new[] { "Optimization_Summary.html", "Optimization_Criteria.json", "Deflection_Report.html", "Member_Forces_Report.html", "Top_Chord_Report.html",
                      "Support_Reactions_Report.html", "Cable_Forces_Report.html" }.All(name => File.Exists(Path.Combine(folder, name))) &&
              new[] { set.SummaryPath, set.DeflectionReportPath!, set.MemberForceReportPath!, set.SupportReactionReportPath!, set.CableForceReportPath! }.All(path => Path.GetDirectoryName(path) == folder),
            "Generate Optimized Reports writes the summary, the criteria record and the four chart reports into Study/Optimized");
        Check(fullReports.Select(Hash).SequenceEqual(before) && !File.Exists(Path.Combine(study, "Optimization_Summary.html")),
            "the full reports in the study folder are not touched");

        var deflection = File.ReadAllText(set.DeflectionReportPath!);
        var member = File.ReadAllText(set.MemberForceReportPath!);
        var support = File.ReadAllText(set.SupportReactionReportPath!);
        var cable = File.ReadAllText(set.CableForceReportPath!);
        const string banner = "<div class=\"scope\"><b>Optimized set</b> · Qualified candidates: 11 of 27 · Deflection δ 50–100 mm · Cable utilisation ≤ 0.80</div>";
        Check(new[] { deflection, member, support, cable }.All(html => html.Contains(banner) &&
                  html.Contains("<a href=\"./Optimization_Summary.html\">Optimization Summary</a>") && !html.Contains("http://") && !html.Contains("https://")) &&
              new ReportPage(member, scripts: 1).Eval("window.reportPrint.data.title") == "Member Forces · Optimized set" &&
              new ReportPage(cable, scripts: 1).Eval("window.reportPrint.data.title") == "Cable Forces · Optimized set",
            "every optimized report says which candidates it covers — in its header, in its printed page header — and links the summary");
        Check(new[] { deflection, member, support, cable }.All(html => html.Contains("L01_H01_P03") && html.Contains("L01_H03_P01") && !html.Contains("L01_H03_P09") && !html.Contains("L01_H01_P01")) &&
              Regex.Matches(deflection, "\"iterationKey\":\"").Count == 11 && deflection.Contains("\"candidateCount\":11"),
            "the optimized reports hold the qualified candidates only — the existing charts on a subset, not new ones");
        Check(deflection.Contains("\"diagnosticReportRelativePath\":\"../L01_H01_P03/Deflection_Diagnostic_L01_H01_P03.html\"") &&
              File.Exists(Path.Combine(study, "L01_H01_P03", "Deflection_Diagnostic_L01_H01_P03.html")),
            "an optimized deflection curve still opens its candidate diagnostic, which stays in the study folder");

        // identity follows the full study, and a slot outside the set says so
        Check(member.Contains("\"scope\":{\"label\":\"Optimized set\"") && member.Contains("\"order\":{\"loadId\":[\"L01\"],\"heightId\":[\"H01\",\"H02\",\"H03\"],\"pretensionId\":[\"P01\",\"P02\"") &&
              Regex.Matches(Regex.Match(member, "\"excluded\":\\[(.*?)\\]\\]").Value, "\\[\"L01\"").Count == 16,
            "an optimized report carries the full study's candidate ids and the iterations outside the set");
        var page = new ReportPage(member, "#summary=top");
        var chart = page.Html("panels");
        Check(Regex.Matches(chart, "<rect data-bar=").Count == 11 && Regex.Matches(chart, ">NQ</text>").Count == 7 && !chart.Contains(">n/a<") &&
              chart.Contains("P01 · H20\nNot qualified — outside the optimized set") && chart.Contains("· NQ = not qualified") &&
              chart.Contains("data-bar=\"P03|H01\"") && !chart.Contains("data-bar=\"P01|H01\"") && !chart.Contains(">P07<") && Regex.Matches(chart, "Not qualified</td>").Count == 7,
            "a combination that has results but did not qualify stays blank and reads NQ / not qualified — distinct from n/a, never a zero");
        var reactions = new ReportPage(support).Html("panels");
        Check(Regex.Matches(reactions, "<rect data-bar=\"fz\\|").Count == 11 * 3 && reactions.Contains(">NQ</text>") && reactions.Contains("· NQ = not qualified") &&
              reactions.Contains("Left Foundation\nNot qualified — outside the optimized set") && cable.Contains("not qualified") && cable.Contains("NQ = not qualified"),
            "the Support Reactions and Cable Forces reports mark the same combinations as not qualified");

        // a height that drops out of the set must not hand its colour to the next one
        var fullTop = new ReportPage(File.ReadAllText(full.MemberForceReportPath!), "#summary=top").Html("panels");
        var highOnly = new StudyOptimizationProcessor().Generate(study, Limits(105, null), Capacities, ["Cable-A", "Cable-B"]);
        var highTop = new ReportPage(File.ReadAllText(highOnly.MemberForceReportPath!), "#summary=top").Html("panels");
        string Fill(string html, string bar) => Regex.Match(html, $"<rect data-bar=\"{Regex.Escape(bar)}\"[^>]*fill=\"(#[0-9a-f]+)\"").Groups[1].Value;
        Check(highOnly.Qualification.Qualified.All(c => c.Metrics.HeightId == "H03") && highOnly.Qualification.Summary.Qualified == 5 &&
              Fill(highTop, "P02|H03").Length == 7 && Fill(highTop, "P02|H03") == Fill(fullTop, "P02|H03") && Fill(fullTop, "P02|H03") != Fill(fullTop, "P02|H01") &&
              !highTop.Contains(">H20<") && Regex.Matches(highTop, "<rect data-bar=").Count == 5,
            "identity follows the full study: a truss height keeps its colour in the optimized charts when other heights drop out");

        // the summary page
        new StudyOptimizationProcessor().Generate(study, criteria, Capacities, ["Cable-A", "Cable-B"], print);
        var summary = File.ReadAllText(set.SummaryPath);
        Check(summary.Contains("<h1>Optimization Summary</h1>") && summary.Contains(banner) && summary.Contains("<li class=\"on\">Optimization Summary<span>viewing</span></li>") &&
              summary.Contains("<a href=\"./Deflection_Report.html\">") && summary.Contains("id=\"pr-btn\"") && summary.Contains("\"title\":\"Optimization Summary\"") &&
              !summary.Contains("http://") && !summary.Contains("https://"),
            "Optimization_Summary.html is a self-contained page on the print engine with the set's Result Library");
        Check(summary.Contains("<div class=\"kl\">Qualified</div><div class=\"kv\">11 <span>of 27</span></div>") &&
              summary.Contains("<div class=\"kl\">Not qualified</div><div class=\"kv\">16 <span></span></div><div class=\"kw\">deflection only 7 · cable only 4 · both 5</div>") &&
              summary.Contains("<div class=\"kl\">Not assessed</div><div class=\"kv\">0 ") && summary.Contains("40–140 <span>mm δ</span>") &&
              summary.Contains("<td>Deflection within range</td><td>δ 50–100 mm</td>") && summary.Contains("<td>Cable forces below the limit</td><td>utilisation ≤ 0.80 (80.0 % of capacity)</td>"),
            "the summary states the criteria and the counts: qualified, failed deflection only / cable only / both, not assessed");
        var map = Regex.Match(summary, "<svg class=\"cchart\" data-chart=\"acceptance\".*?</svg>", RegexOptions.Singleline).Value;
        Check(Regex.Matches(map, "data-point=").Count == 27 && Regex.Matches(map, "data-status=\"Qualified\"").Count == 11 &&
              Regex.Matches(map, "data-status=\"NotQualified\"").Count == 16 && map.Contains("data-window=\"accept\"") &&
              map.Contains(">δ min 50 mm<") && map.Contains(">δ max 100 mm<") && map.Contains(">utilisation limit 80 %<") &&
              map.Contains("<title>L01_H01_P08 — δ 75 mm · utilisation 88.0 % (Cable-A) · Not qualified: Cable-A utilisation 88.0 % exceeds 80.0 %</title>"),
            "the acceptance map plots every candidate by δ and governing cable utilisation, with the limits and the acceptance window");
        string Table(string name) => Regex.Match(summary, $"<table class=\"ctable\" data-table=\"{name}\">.*?</table>", RegexOptions.Singleline).Value;
        Check(Regex.Matches(Table("qualified"), "<tr data-status=\"Qualified\">").Count == 11 && !Table("qualified").Contains("NotQualified") &&
              Regex.Matches(Table("all"), "<tr data-status=").Count == 27 && Table("all").Contains("<td class=\"st notqualified\">Not qualified</td><td>δ 45 mm is below Min 50 mm</td>") &&
              Table("qualified").Contains("<td>L01_H01_P06</td><td>L01</td><td>H20</td><td>P06</td><td class=\"n\">65</td><td class=\"n\">80.0</td><td>Cable-A</td><td class=\"n\">800</td><td class=\"n\">1,000</td>") &&
              summary.Contains("data-pr=\"source\"><summary>All candidates"),
            "the summary lists the qualified candidates with their governing values, and every candidate with its status and reason");

        // traceability and staleness
        var record = StudyOptimizationProcessor.ReadRecord(study)!;
        var index = new StudyMetricsStore().Load(study);
        Check(record is { TotalCandidates: 27, DeflectionEnabled: true, DeflectionMinMm: 50, DeflectionMaxMm: 100, CableEnabled: true, MaxCableUtilization: 0.8 } &&
              record.QualifiedIterationKeys.Count == 11 && record.QualifiedIterationKeys.Contains("L01_H03_P01") && record.RawIdentity == index.RawIdentity &&
              record.CapacitySignature == "Cable-A=1000;Cable-B=1000",
            "Optimization_Criteria.json records the criteria, the capacities, the raw data and the qualified iteration keys");
        var lower = new Dictionary<string, double>(StringComparer.Ordinal) { ["Cable-A"] = 900, ["Cable-B"] = 1000 };
        Check(StudyOptimizationProcessor.IsCurrent(record, index, criteria, Capacities) &&
              !StudyOptimizationProcessor.IsCurrent(record, index, Limits(50, 110), Capacities) &&
              !StudyOptimizationProcessor.IsCurrent(record, index, Limits(50, 100, utilisation: 0.85), Capacities) &&
              !StudyOptimizationProcessor.IsCurrent(record, index, Limits(50, 100, cable: false), Capacities) &&
              !StudyOptimizationProcessor.IsCurrent(record, index, criteria, lower) &&
              !StudyOptimizationProcessor.IsCurrent(record, index with { RawLength = index.RawLength + 1 }, criteria, Capacities),
            "the optimized reports are out of date once the criteria, a cable capacity or the raw data change");

        // nothing qualifies
        var empty = new StudyOptimizationProcessor().Generate(study, Limits(0, 1), Capacities, ["Cable-A", "Cable-B"]);
        var emptySummary = File.ReadAllText(empty.SummaryPath);
        Check(empty.Qualification.Summary.Qualified == 0 && empty is { DeflectionReportPath: null, MemberForceReportPath: null, SupportReactionReportPath: null, CableForceReportPath: null } &&
              Directory.GetFiles(folder, "*.html").Select(Path.GetFileName).SequenceEqual(["Optimization_Summary.html"]) &&
              emptySummary.Contains("data-empty=\"qualified\">No candidate qualifies under these criteria") && !emptySummary.Contains("Deflection_Report.html") &&
              StudyOptimizationProcessor.ReadRecord(study)!.QualifiedIterationKeys.Count == 0 && fullReports.Select(Hash).SequenceEqual(before),
            "when no candidate qualifies only the summary is written, and the charts of an earlier set are removed");
        Check(ReadRecordMissing(Path.Combine(study, "nowhere")), "a study without an optimized set has no record");
    }

    private static bool ReadRecordMissing(string study) => StudyOptimizationProcessor.ReadRecord(study) is null;

    private static void ThousandCandidates(string study)
    {
        var results = Study(loads: 10, heights: 10, pretensions: 10);
        WriteRaw(study, results);
        var clock = Stopwatch.StartNew();
        var index = new StudyMetricsStore().Load(study);
        var derive = clock.Elapsed;
        clock.Restart();
        var criteria = Limits(100, 300, utilisation: 0.9);
        StudyQualification q = null!;
        for (var pass = 0; pass < 20; pass++) q = StudyQualifier.Evaluate(index.Candidates, criteria with { MaxCableUtilization = 0.9 + pass * 1e-3 }, Capacities);
        var each = clock.Elapsed.TotalMilliseconds / 20;
        var expected = results.Count(r =>
        {
            var m = CandidateMetrics.From(r);
            return m.PeakDownMm is >= 100 and <= 300 && m.Cables.All(c => c.ForceKN / 1000 <= 0.919);
        });
        Check(index.Candidates.Count == 1000 && q.Summary.Total == 1000 && q.Summary.Qualified == expected && expected is > 100 and < 900 && each < 100,
            $"1,000 candidates: metrics derived once in {derive.TotalMilliseconds:0} ms, criteria re-applied to them in {each:0.0} ms — no raw data read");
        clock.Restart();
        var set = new StudyOptimizationProcessor().Generate(study, criteria, Capacities, ["Cable-A", "Cable-B"]);
        Check(set.Qualification.Summary.Total == 1000 && set.Qualification.Summary.Qualified > 100 && File.Exists(set.SummaryPath) && File.Exists(set.DeflectionReportPath) &&
              Regex.Matches(File.ReadAllText(set.SummaryPath), "data-point=").Count == 1000,
            $"1,000 candidates: the optimized set of {set.Qualification.Summary.Qualified} is written in {clock.Elapsed.TotalSeconds:0.0} s, reading the raw data once");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
