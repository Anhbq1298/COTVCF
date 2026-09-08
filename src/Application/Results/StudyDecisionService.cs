using System.Globalization;
using System.Net;
using System.IO;
using ClosedXML.Excel;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;
using COT_VCF.Infrastructure;

namespace COT_VCF.Application.Results;

public enum CandidateDecisionStatus { Pass, Warning, Fail, Incomplete }

public sealed record StudyDecisionCriteria(
    double DeflectionPassLimitMm,
    double DeflectionFailLimitMm,
    double? MinimumCableForceKn,
    double? MaximumCableForceKn,
    double WarningUtilization,
    double DeflectionWeight,
    double PretensionWeight,
    double HeightWeight)
{
    public static StudyDecisionCriteria From(IterationStudyConfig config) => new(
        config.DeflectionPassLimitMm ?? double.NaN,
        config.DeflectionFailLimitMm ?? double.NaN,
        config.MinimumCableForceKn,
        config.MaximumCableForceKn,
        config.DecisionWarningUtilization,
        config.DeflectionRankWeight,
        config.PretensionRankWeight,
        config.HeightRankWeight);
}

public sealed record CandidateDecisionResult(
    int Rank,
    string IterationKey,
    long RunIndex,
    CandidateDecisionStatus Status,
    double? Score,
    double? MaxAbsUzMm,
    double? MinimumCableForceKn,
    double? MaximumCableForceKn,
    double PretensionMetricKn,
    double HeightM,
    IReadOnlyList<string> Reasons,
    string CandidateModelPath)
{
    public string ScoreText => Score is double value ? value.ToString("0.0000", CultureInfo.InvariantCulture) : "—";
    public string DeflectionText => MaxAbsUzMm is double value ? $"{value:0.###} mm" : "Result unavailable";
    public string CableForceText => MinimumCableForceKn is double lo && MaximumCableForceKn is double hi
        ? $"{lo:0.###}..{hi:0.###} kN" : "Not configured / unavailable";
    public string GoverningReason => string.Join(" · ", Reasons);
}

public sealed class StudyDecisionService
{
    public IReadOnlyList<CandidateDecisionResult> Evaluate(
        IReadOnlyList<CandidateResultDto> candidates,
        StudyDecisionCriteria criteria)
    {
        Validate(criteria);
        var raw = candidates.Select(candidate => EvaluateOne(candidate, criteria)).ToArray();
        var deflections = raw.Where(item => item.MaxAbsUzMm.HasValue).Select(item => item.MaxAbsUzMm!.Value).ToArray();
        var pretensions = raw.Select(item => item.PretensionMetricKn).ToArray();
        var heights = raw.Select(item => item.HeightM).ToArray();
        var weightTotal = criteria.DeflectionWeight + criteria.PretensionWeight + criteria.HeightWeight;
        var scored = raw.Select(item => item.Status == CandidateDecisionStatus.Incomplete ? item : item with
        {
            Score = (criteria.DeflectionWeight * Normalize(item.MaxAbsUzMm ?? deflections.DefaultIfEmpty(0).Max(), deflections) +
                     criteria.PretensionWeight * Normalize(item.PretensionMetricKn, pretensions) +
                     criteria.HeightWeight * Normalize(item.HeightM, heights)) / weightTotal
        });
        return scored.OrderBy(item => StatusOrder(item.Status))
            .ThenBy(item => item.Score ?? double.PositiveInfinity)
            .ThenBy(item => item.RunIndex)
            .ThenBy(item => item.IterationKey, StringComparer.Ordinal)
            .Select((item, index) => item with { Rank = index + 1 })
            .ToArray();
    }

    public static void Validate(StudyDecisionCriteria criteria)
    {
        if (!double.IsFinite(criteria.DeflectionPassLimitMm) || criteria.DeflectionPassLimitMm < 0 ||
            !double.IsFinite(criteria.DeflectionFailLimitMm) || criteria.DeflectionFailLimitMm < criteria.DeflectionPassLimitMm)
            throw new InvalidOperationException("Deflection criteria require finite 0 ≤ Pass limit ≤ Fail limit values in mm.");
        if (criteria.MinimumCableForceKn is double min && !double.IsFinite(min) ||
            criteria.MaximumCableForceKn is double max && !double.IsFinite(max) ||
            criteria.MinimumCableForceKn is double lo && criteria.MaximumCableForceKn is double hi && hi < lo)
            throw new InvalidOperationException("Cable-force bounds must be finite and Max must be greater than or equal to Min.");
        if (!double.IsFinite(criteria.WarningUtilization) || criteria.WarningUtilization is <= 0 or >= 1)
            throw new InvalidOperationException("Warning utilization must be between zero and one.");
        var weights = new[] { criteria.DeflectionWeight, criteria.PretensionWeight, criteria.HeightWeight };
        if (weights.Any(value => !double.IsFinite(value) || value < 0) || weights.Sum() <= 0)
            throw new InvalidOperationException("Ranking weights must be finite, nonnegative, and have a positive total.");
    }

    private static CandidateDecisionResult EvaluateOne(CandidateResultDto candidate, StudyDecisionCriteria criteria)
    {
        var reasons = new List<string>();
        var points = candidate.Groups.SelectMany(group => group.Points).ToArray();
        var invalidPoint = points.FirstOrDefault(point => point.Status != ResultValueStatus.Valid || point.UzMm is null);
        var validDeflections = points.Where(point => point.Status == ResultValueStatus.Valid && point.UzMm.HasValue)
            .Select(point => Math.Abs(point.UzMm!.Value)).ToArray();
        var maxDeflection = validDeflections.Length > 0 ? validDeflections.Max() : (double?)null;
        var cableRows = candidate.CableForces ?? [];
        var invalidCable = cableRows.FirstOrDefault(row => row.Status != ResultValueStatus.Valid || row.ForceKN is null);
        var validCableForces = cableRows.Where(row => row.Status == ResultValueStatus.Valid && row.ForceKN.HasValue)
            .Select(row => row.ForceKN!.Value).ToArray();
        var cableCriteriaActive = criteria.MinimumCableForceKn.HasValue || criteria.MaximumCableForceKn.HasValue;
        var pretensionMetric = (candidate.Parameters.PretensionCables ?? [])
            .Select(row => Math.Abs(row.TargetForceKN)).DefaultIfEmpty(Math.Abs(candidate.Parameters.PretensionKN ?? 0)).Max();

        var incomplete = candidate.ExtractionStatus is CandidateExtractionStatus.ExtractionFailed or CandidateExtractionStatus.AnalysisFailed or CandidateExtractionStatus.NotStarted ||
                         points.Length == 0 || invalidPoint is not null ||
                         cableRows.Count > 0 && invalidCable is not null || cableCriteriaActive && cableRows.Count == 0;
        if (incomplete)
        {
            if (points.Length == 0) reasons.Add("No monitored U3 results were extracted.");
            else if (invalidPoint is not null) reasons.Add($"U3 result unavailable at {invalidPoint.PointName} ({invalidPoint.Status}).");
            if (cableCriteriaActive && cableRows.Count == 0) reasons.Add("Cable-force criteria are active but no cable results were extracted.");
            else if (invalidCable is not null) reasons.Add($"Cable force unavailable for {invalidCable.CableName} ({invalidCable.Status}).");
            if (reasons.Count == 0) reasons.Add($"Extraction status is {candidate.ExtractionStatus}.");
            return Create(CandidateDecisionStatus.Incomplete, reasons);
        }

        var status = CandidateDecisionStatus.Pass;
        if (maxDeflection > criteria.DeflectionFailLimitMm)
        {
            status = CandidateDecisionStatus.Fail;
            reasons.Add($"|U3|max {maxDeflection:0.###} mm exceeds Fail limit {criteria.DeflectionFailLimitMm:0.###} mm.");
        }
        else if (maxDeflection > criteria.DeflectionPassLimitMm)
        {
            status = CandidateDecisionStatus.Warning;
            reasons.Add($"|U3|max {maxDeflection:0.###} mm exceeds Pass limit {criteria.DeflectionPassLimitMm:0.###} mm.");
        }
        else reasons.Add($"|U3|max {maxDeflection:0.###} mm ≤ {criteria.DeflectionPassLimitMm:0.###} mm.");

        foreach (var force in validCableForces)
        {
            if (criteria.MinimumCableForceKn is double min && force < min || criteria.MaximumCableForceKn is double max && force > max)
            {
                status = CandidateDecisionStatus.Fail;
                reasons.Add($"Cable force {force:0.###} kN is outside [{criteria.MinimumCableForceKn?.ToString("0.###") ?? "−∞"}, {criteria.MaximumCableForceKn?.ToString("0.###") ?? "+∞"}] kN.");
            }
            else if (status != CandidateDecisionStatus.Fail && NearCableLimit(force, criteria))
            {
                status = CandidateDecisionStatus.Warning;
                reasons.Add($"Cable force {force:0.###} kN is within the {criteria.WarningUtilization:P0} warning margin.");
            }
        }
        if (candidate.ExtractionStatus == CandidateExtractionStatus.ExtractedWithWarning && status == CandidateDecisionStatus.Pass)
        {
            status = CandidateDecisionStatus.Warning;
            reasons.Add("Extraction completed with a warning.");
        }
        return Create(status, reasons);

        CandidateDecisionResult Create(CandidateDecisionStatus resultStatus, IReadOnlyList<string> resultReasons) => new(
            0, candidate.IterationKey, candidate.Parameters.RunIndex, resultStatus, null, maxDeflection,
            validCableForces.Length > 0 ? validCableForces.Min() : null,
            validCableForces.Length > 0 ? validCableForces.Max() : null,
            pretensionMetric, candidate.Parameters.TrussHeightM, resultReasons, candidate.CandidateModelPath);
    }

    private static bool NearCableLimit(double force, StudyDecisionCriteria criteria) =>
        criteria.MaximumCableForceKn is double max && force > max * criteria.WarningUtilization ||
        criteria.MinimumCableForceKn is double min && force < min / criteria.WarningUtilization;
    private static int StatusOrder(CandidateDecisionStatus status) => status switch
    {
        CandidateDecisionStatus.Pass => 0,
        CandidateDecisionStatus.Warning => 1,
        CandidateDecisionStatus.Fail => 2,
        _ => 3
    };
    private static double Normalize(double value, IReadOnlyList<double> population)
    {
        if (population.Count == 0) return 0;
        var min = population.Min();
        var max = population.Max();
        return Math.Abs(max - min) <= 1e-12 * Math.Max(1, Math.Abs(max)) ? 0 : (value - min) / (max - min);
    }
}

public sealed record StudyShortlistArtifacts(string HtmlPath, string WorkbookPath);

public sealed class StudyShortlistExporter
{
    public const string HtmlFileName = "Study_Shortlist.html";
    public const string WorkbookFileName = "Study_Shortlist.xlsx";

    public StudyShortlistArtifacts Export(string studyRoot, IReadOnlyList<CandidateDecisionResult> decisions,
        StudyDecisionCriteria criteria, int count = 10, IReadOnlyList<string>? siblingReports = null)
    {
        Directory.CreateDirectory(studyRoot);
        var rows = decisions.Where(item => item.Status is CandidateDecisionStatus.Pass or CandidateDecisionStatus.Warning)
            .OrderBy(item => item.Rank).Take(Math.Max(1, count)).ToArray();
        var siblings = siblingReports is null
            ? ReportNav.PresentIn(studyRoot, "shortlist")
            : new HashSet<string>(siblingReports, StringComparer.Ordinal);
        var workbook = Path.Combine(studyRoot, WorkbookFileName);
        AtomicFile.Write(workbook, path => WriteWorkbook(path, rows, criteria));
        var html = Path.Combine(studyRoot, HtmlFileName);
        AtomicFile.WriteAllText(html, Html(rows, criteria, siblings));
        return new(html, workbook);
    }

    private static void WriteWorkbook(string path, IReadOnlyList<CandidateDecisionResult> rows, StudyDecisionCriteria criteria)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Shortlist");
        string[] headers = ["Rank", "IterationKey", "Decision", "Score", "MaxAbsU3_mm", "MinCableForce_kN", "MaxCableForce_kN", "PretensionMetric_kN", "Height_m", "GoverningReasons", "CandidateModelPath"];
        for (var c = 0; c < headers.Length; c++) { ws.Cell(1, c + 1).Value = headers[c]; ws.Cell(1, c + 1).Style.Font.Bold = true; }
        for (var r = 0; r < rows.Count; r++)
        {
            var item = rows[r];
            object?[] values = [item.Rank, item.IterationKey, item.Status.ToString(), item.Score, item.MaxAbsUzMm,
                item.MinimumCableForceKn, item.MaximumCableForceKn, item.PretensionMetricKn, item.HeightM,
                item.GoverningReason, item.CandidateModelPath];
            for (var c = 0; c < values.Length; c++)
                if (values[c] is not null) ws.Cell(r + 2, c + 1).Value = XLCellValue.FromObject(values[c]);
        }
        var criteriaSheet = wb.Worksheets.Add("Criteria");
        var criteriaRows = new (string, object?)[] { ("DeflectionPassLimit_mm", criteria.DeflectionPassLimitMm),
            ("DeflectionFailLimit_mm", criteria.DeflectionFailLimitMm), ("MinimumCableForce_kN", criteria.MinimumCableForceKn),
            ("MaximumCableForce_kN", criteria.MaximumCableForceKn), ("WarningUtilization", criteria.WarningUtilization),
            ("DeflectionWeight", criteria.DeflectionWeight), ("PretensionWeight", criteria.PretensionWeight), ("HeightWeight", criteria.HeightWeight) };
        for (var r = 0; r < criteriaRows.Length; r++) { criteriaSheet.Cell(r + 1, 1).Value = criteriaRows[r].Item1; if (criteriaRows[r].Item2 is not null) criteriaSheet.Cell(r + 1, 2).Value = XLCellValue.FromObject(criteriaRows[r].Item2); }
        ws.Columns().AdjustToContents(); criteriaSheet.Columns().AdjustToContents(); wb.SaveAs(path);
    }

    private static string Html(IReadOnlyList<CandidateDecisionResult> rows, StudyDecisionCriteria criteria,
        IReadOnlySet<string> siblingReports)
    {
        static string E(object? value) => WebUtility.HtmlEncode(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
        var body = string.Join("", rows.Select(item => $"<tr><td>{item.Rank}</td><td>{E(item.IterationKey)}</td><td>{item.Status}</td><td>{E(item.ScoreText)}</td><td>{E(item.DeflectionText)}</td><td>{E(item.CableForceText)}</td><td>{item.HeightM:0.###} m</td><td>{E(item.GoverningReason)}</td></tr>"));
        var library = ReportNav.Library("shortlist", siblingReports);
        return "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>COTVCF Study Shortlist</title>" +
               "<style>body{font:14px system-ui;margin:24px;color:#202124}table{border-collapse:collapse;width:100%}th,td{border:1px solid #d4d7dc;padding:7px;text-align:left}th{background:#eef2f5}small{color:#5f6368}.lib{margin:0 0 18px;border:1px solid #d4d7dc;border-radius:6px;padding:8px 12px}.lib summary{font-weight:700;cursor:pointer}.lib ul{display:flex;gap:8px;flex-wrap:wrap;list-style:none;padding:8px 0 0;margin:0}.lib li{display:flex;gap:7px;align-items:center;border:1px solid #d4d7dc;border-radius:4px;padding:6px 9px}.lib li.on{background:#eef2f5;font-weight:700}.lib span{color:#5f6368;font-size:11px}.lib a{color:#175ea8;text-decoration:none}</style></head><body>" +
               library +
               $"<h1>Study Shortlist</h1><p><small>|U3| Pass ≤ {criteria.DeflectionPassLimitMm:0.###} mm · Fail &gt; {criteria.DeflectionFailLimitMm:0.###} mm · Ranking weights D/P/H = {criteria.DeflectionWeight:0.###}/{criteria.PretensionWeight:0.###}/{criteria.HeightWeight:0.###}</small></p>" +
               "<table><thead><tr><th>Rank</th><th>Candidate</th><th>Decision</th><th>Score</th><th>Max |U3|</th><th>Cable force</th><th>Height</th><th>Governing reason</th></tr></thead><tbody>" + body + "</tbody></table></body></html>";
    }
}
