using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using ClosedXML.Excel;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;
using COT_VCF.Infrastructure;

namespace COT_VCF.Application.Results;

/// <summary>Consolidated long-form deflection source data for one study. Workers hand their
/// <see cref="CandidateResultDto"/> to <see cref="Add"/> (thread-safe); the study coordinator makes a single
/// <see cref="Write"/> call at the end so the .xlsx is never opened by more than one writer. <see cref="Read"/>
/// re-hydrates the workbook for report regeneration without SAP.</summary>
public sealed class ConsolidatedResultWriter
{
    public const string WorkbookFileName = "Deflection_Results_Source.xlsx";
    public const int SchemaVersion = 1;

    private readonly ConcurrentBag<CandidateResultDto> results = [];

    public void Add(CandidateResultDto result) => results.Add(result);
    public int Count => results.Count;
    public IReadOnlyList<CandidateResultDto> Snapshot() =>
        results.OrderBy(r => r.Parameters.RunIndex).ThenBy(r => r.IterationKey, StringComparer.Ordinal).ToArray();

    public string Write(string studyFolder, IReadOnlyList<string> deflectionPoints, string deflectionCase,
        string baselineName, string baselinePath, IterationStudyMode studyMode,
        IReadOnlyDictionary<string, double>? cableCapacities = null)
    {
        Directory.CreateDirectory(studyFolder);
        var path = Path.Combine(studyFolder, WorkbookFileName);
        var ordered = Snapshot();

        using var wb = new XLWorkbook();
        Candidates(wb.Worksheets.Add("Candidates"), ordered);
        MonitoredPointResults(wb.Worksheets.Add("MonitoredPointResults"), ordered);
        CableForceResults(wb.Worksheets.Add("CableForceResults"), ordered);
        CableProperties(wb.Worksheets.Add("CableProperties"), cableCapacities);
        OutputConfiguration(wb.Worksheets.Add("OutputConfiguration"), deflectionPoints, deflectionCase);
        Warnings(wb.Worksheets.Add("Warnings"), ordered);
        Meta(wb.Worksheets.Add("Meta"), baselineName, baselinePath, studyFolder, studyMode, ordered.Count);

        AtomicFile.Write(path, wb.SaveAs);
        return path;
    }

    // ---------- sheets ----------

    private static void Candidates(IXLWorksheet ws, IReadOnlyList<CandidateResultDto> rows)
    {
        Header(ws, "IterationKey", "RunIndex", "StudyMode", "CandidateModelPath",
            "LoadingCandidateId", "LoadLevel_kPa", "LoadingLabel",
            "HeightCandidateId", "TrussHeight_m", "DeltaH_m",
            "PretensionCandidateId", "Pretension_kN", "PretensionLabel",
            "ExtractionStatus", "AnalysisStart", "AnalysisFinish", "ExtractionFinish",
            "DiagnosticHtmlRelPath", "WarningCount", "PretensionCables", "LoadingComponents");
        var r = 2;
        foreach (var c in rows)
        {
            var p = c.Parameters;
            var col = 1;
            ws.Cell(r, col++).Value = c.IterationKey;
            ws.Cell(r, col++).Value = p.RunIndex;
            ws.Cell(r, col++).Value = c.StudyMode.ToString();
            ws.Cell(r, col++).Value = c.CandidateModelPath;
            ws.Cell(r, col++).Value = p.LoadingCandidateId;
            SetNum(ws.Cell(r, col++), p.LoadLevelKPa);
            ws.Cell(r, col++).Value = p.LoadingLabel;
            ws.Cell(r, col++).Value = p.HeightCandidateId;
            ws.Cell(r, col++).Value = p.TrussHeightM;
            ws.Cell(r, col++).Value = p.DeltaHM;
            ws.Cell(r, col++).Value = p.PretensionCandidateId;
            SetNum(ws.Cell(r, col++), p.PretensionKN);
            ws.Cell(r, col++).Value = p.PretensionLabel;
            ws.Cell(r, col++).Value = c.ExtractionStatus.ToString();
            ws.Cell(r, col++).Value = Iso(c.AnalysisStartedAt);
            ws.Cell(r, col++).Value = Iso(c.AnalysisCompletedAt);
            ws.Cell(r, col++).Value = Iso(c.ExtractionCompletedAt);
            ws.Cell(r, col++).Value = c.DiagnosticReportRelativePath;
            ws.Cell(r, col++).Value = c.Warnings.Count;
            ws.Cell(r, col++).Value = string.Join(";", (p.PretensionCables ?? [])
                .Select(cb => $"{cb.CableName}={cb.TargetForceKN.ToString("0.######", CultureInfo.InvariantCulture)}"));
            ws.Cell(r, col++).Value = string.Join(";", (p.LoadingComponents ?? [])
                .Select(lc => $"{lc.PatternName}={lc.LevelKPa.ToString("0.######", CultureInfo.InvariantCulture)}"));
            r++;
        }
        Finish(ws);
    }

    private static void MonitoredPointResults(IXLWorksheet ws, IReadOnlyList<CandidateResultDto> rows)
    {
        Header(ws, "IterationKey", "RunIndex", "GroupName", "OutputCase", "ProfileAxis",
            "PointName", "Sequence", "X_m", "Y_m", "Z_m", "Uz_mm", "ResultStatus", "Warning");
        var r = 2;
        foreach (var c in rows)
            foreach (var g in c.Groups)
                foreach (var pt in g.Points)
                {
                    var col = 1;
                    ws.Cell(r, col++).Value = c.IterationKey;
                    ws.Cell(r, col++).Value = c.Parameters.RunIndex;
                    ws.Cell(r, col++).Value = g.GroupName;
                    ws.Cell(r, col++).Value = g.OutputCase;
                    ws.Cell(r, col++).Value = g.ProfileAxis.ToString();
                    ws.Cell(r, col++).Value = pt.PointName;
                    ws.Cell(r, col++).Value = pt.Sequence;
                    ws.Cell(r, col++).Value = pt.X;
                    ws.Cell(r, col++).Value = pt.Y;
                    ws.Cell(r, col++).Value = pt.Z;
                    SetNum(ws.Cell(r, col++), pt.UzMm);
                    ws.Cell(r, col++).Value = pt.Status.ToString();
                    ws.Cell(r, col++).Value = pt.Warning ?? "";
                    r++;
                }
        Finish(ws);
    }

    private static void CableForceResults(IXLWorksheet ws, IReadOnlyList<CandidateResultDto> rows)
    {
        Header(ws, "IterationKey", "RunIndex", "Cable", "TargetForce_kN", "Force_kN", "ResultStatus", "Warning");
        var r = 2;
        foreach (var c in rows)
            foreach (var cf in c.CableForces ?? [])
            {
                var col = 1;
                ws.Cell(r, col++).Value = c.IterationKey;
                ws.Cell(r, col++).Value = c.Parameters.RunIndex;
                ws.Cell(r, col++).Value = cf.CableName;
                SetNum(ws.Cell(r, col++), cf.TargetForceKN);
                SetNum(ws.Cell(r, col++), cf.ForceKN);
                ws.Cell(r, col++).Value = cf.Status.ToString();
                ws.Cell(r, col++).Value = cf.Warning ?? "";
                r++;
            }
        Finish(ws);
    }

    /// <summary>Per-cable design capacity [kN] — study-global post-processing input, so the utilisation views
    /// in Cable_Forces_Report.html survive a SAP-free regeneration from this workbook alone.</summary>
    private static void CableProperties(IXLWorksheet ws, IReadOnlyDictionary<string, double>? capacities)
    {
        Header(ws, "Cable", "DesignCapacity_kN");
        var r = 2;
        foreach (var kv in (capacities ?? new Dictionary<string, double>())
            .Where(kv => double.IsFinite(kv.Value) && kv.Value > 0)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            ws.Cell(r, 1).Value = kv.Key;
            SetNum(ws.Cell(r, 2), kv.Value);
            r++;
        }
        Finish(ws);
    }

    private static void OutputConfiguration(IXLWorksheet ws, IReadOnlyList<string> points, string outputCase)
    {
        Header(ws, "GroupName", "PointName", "OutputCase", "Direction", "ProfileAxis");
        var r = 2;
        foreach (var name in points)
        {
            ws.Cell(r, 1).Value = SapResultExtractor.GroupName;
            ws.Cell(r, 2).Value = name;
            ws.Cell(r, 3).Value = outputCase;
            ws.Cell(r, 4).Value = "Uz";
            ws.Cell(r, 5).Value = "X";
            r++;
        }
        Finish(ws);
    }

    private static void Warnings(IXLWorksheet ws, IReadOnlyList<CandidateResultDto> rows)
    {
        Header(ws, "IterationKey", "Category", "Severity", "Message", "Timestamp");
        var r = 2;
        foreach (var c in rows)
            foreach (var w in c.Warnings)
            {
                ws.Cell(r, 1).Value = w.IterationKey;
                ws.Cell(r, 2).Value = w.Category;
                ws.Cell(r, 3).Value = w.Severity.ToString();
                ws.Cell(r, 4).Value = w.Message;
                ws.Cell(r, 5).Value = Iso(w.Timestamp);
                r++;
            }
        Finish(ws);
    }

    private static void Meta(IXLWorksheet ws, string baselineName, string baselinePath, string studyFolder,
        IterationStudyMode mode, int candidateCount)
    {
        Header(ws, "Key", "Value");
        var kv = new (string, string)[]
        {
            ("SchemaVersion", SchemaVersion.ToString()),
            ("Module", "Deflection"),
            ("BaselineModelName", baselineName),
            ("BaselineModelPath", baselinePath),
            ("StudyFolder", studyFolder),
            ("StudyMode", mode.ToString()),
            ("CandidateCount", candidateCount.ToString()),
            ("GeneratedAt", Iso(DateTimeOffset.Now)),
        };
        for (var i = 0; i < kv.Length; i++) { ws.Cell(i + 2, 1).Value = kv[i].Item1; ws.Cell(i + 2, 2).Value = kv[i].Item2; }
        Finish(ws);
    }

    // ---------- read-back for report regeneration ----------

    /// <summary>The per-cable design capacities [kN] stored by <see cref="Write"/>. Empty when the workbook
    /// predates the CableProperties sheet or no capacity was configured.</summary>
    public static IReadOnlyDictionary<string, double> ReadCableCapacities(string workbookPath)
    {
        using var wb = new XLWorkbook(workbookPath);
        if (!wb.Worksheets.Contains("CableProperties")) return new Dictionary<string, double>();
        return wb.Worksheet("CableProperties").RowsUsed().Skip(1)
            .Where(row => !string.IsNullOrWhiteSpace(row.Cell(1).GetString()) && row.Cell(2).TryGetValue<double>(out var v) && double.IsFinite(v) && v > 0)
            .ToDictionary(row => row.Cell(1).GetString(), row => row.Cell(2).GetDouble(), StringComparer.Ordinal);
    }

    public static IReadOnlyList<CandidateResultDto> Read(string workbookPath)
    {
        using var wb = new XLWorkbook(workbookPath);
        var meta = wb.Worksheet("Meta").RowsUsed().Skip(1)
            .ToDictionary(row => row.Cell(1).GetString(), row => row.Cell(2).GetString(), StringComparer.OrdinalIgnoreCase);
        var studyMode = Enum.TryParse<IterationStudyMode>(meta.GetValueOrDefault("StudyMode"), out var m) ? m : IterationStudyMode.Combined;
        var baselineName = meta.GetValueOrDefault("BaselineModelName", "");
        var baselinePath = meta.GetValueOrDefault("BaselineModelPath", "");

        var cableForceByKey = wb.Worksheets.Contains("CableForceResults")
            ? wb.Worksheet("CableForceResults").RowsUsed().Skip(1)
                .ToLookup(row => row.Cell(1).GetString(), row => new CableForceResultDto(
                    row.Cell(3).GetString(),
                    row.Cell(4).TryGetValue<double>(out var tf) ? tf : null,
                    row.Cell(5).TryGetValue<double>(out var f) ? f : null,
                    Enum.TryParse<ResultValueStatus>(row.Cell(6).GetString(), out var s) ? s : ResultValueStatus.ApiError,
                    string.IsNullOrEmpty(row.Cell(7).GetString()) ? null : row.Cell(7).GetString()),
                    StringComparer.Ordinal)
            : null;

        var pointRows = wb.Worksheet("MonitoredPointResults").RowsUsed().Skip(1)
            .Select(row => new
            {
                Key = row.Cell(1).GetString(),
                Group = row.Cell(3).GetString(),
                Case = row.Cell(4).GetString(),
                Axis = row.Cell(5).GetString(),
                Point = row.Cell(6).GetString(),
                Seq = (int)row.Cell(7).GetDouble(),
                X = row.Cell(8).GetDouble(),
                Y = row.Cell(9).GetDouble(),
                Z = row.Cell(10).GetDouble(),
                Uz = row.Cell(11).TryGetValue<double>(out var uz) ? (double?)uz : null,
                Status = Enum.TryParse<ResultValueStatus>(row.Cell(12).GetString(), out var s) ? s : ResultValueStatus.ApiError,
                Warning = row.Cell(13).GetString(),
            })
            .ToLookup(x => x.Key, StringComparer.Ordinal);

        var warnRows = wb.Worksheet("Warnings").RowsUsed().Skip(1)
            .Select(row => new ResultWarningDto(row.Cell(1).GetString(), row.Cell(2).GetString(),
                Enum.TryParse<ResultWarningSeverity>(row.Cell(3).GetString(), out var sev) ? sev : ResultWarningSeverity.Warning,
                row.Cell(4).GetString(), ParseIso(row.Cell(5).GetString())))
            .ToLookup(w => w.IterationKey, StringComparer.Ordinal);

        var candidates = new List<CandidateResultDto>();
        foreach (var row in wb.Worksheet("Candidates").RowsUsed().Skip(1))
        {
            var key = row.Cell(1).GetString();
            var cables = row.Cell(20).GetString()
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => { var i = t.LastIndexOf('='); return i <= 0
                    ? null
                    : new PretensionCableDto(t[..i], double.TryParse(t[(i + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0); })
                .Where(c => c is not null).Select(c => c!).ToArray();
            var loadComponents = row.Cell(21).GetString()
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => { var i = t.LastIndexOf('='); return i <= 0
                    ? null
                    : new LoadComponentDto(t[..i], double.TryParse(t[(i + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var q) ? q : 0); })
                .Where(c => c is not null).Select(c => c!).ToArray();
            var parameters = new CandidateParametersDto(
                (long)row.Cell(2).GetDouble(), key,
                row.Cell(5).GetString(),
                row.Cell(8).GetString(),
                row.Cell(11).GetString(),
                row.Cell(6).TryGetValue<double>(out var ll) ? ll : null,
                row.Cell(7).GetString(),
                row.Cell(9).GetDouble(),
                row.Cell(10).GetDouble(),
                row.Cell(12).TryGetValue<double>(out var pk) ? pk : null,
                row.Cell(13).GetString(),
                cables,
                loadComponents);

            var groups = pointRows[key]
                .GroupBy(x => (x.Group, x.Case, x.Axis))
                .Select(grp => new MonitoredPointGroupResultDto(
                    grp.Key.Group, grp.Key.Case,
                    Enum.TryParse<ProfileCoordinateAxis>(grp.Key.Axis, out var a) ? a : ProfileCoordinateAxis.X,
                    grp.OrderBy(x => x.Seq).Select(x => new MonitoredPointResultDto(
                        x.Point, x.X, x.Y, x.Z, x.Seq, x.Status == ResultValueStatus.Valid ? x.Uz : null,
                        x.Status, string.IsNullOrEmpty(x.Warning) ? null : x.Warning)).ToArray()))
                .ToArray();

            candidates.Add(new CandidateResultDto(
                key, row.Cell(4).GetString(), baselinePath, baselineName, studyMode, parameters,
                ParseIso(row.Cell(15).GetString()), ParseIso(row.Cell(16).GetString()), ParseIso(row.Cell(17).GetString()),
                Enum.TryParse<CandidateExtractionStatus>(row.Cell(14).GetString(), out var es) ? es : CandidateExtractionStatus.ExtractedOk,
                groups, warnRows[key].ToArray(),
                cableForceByKey?[key].ToArray() ?? []));
        }
        return candidates.OrderBy(c => c.Parameters.RunIndex).ToArray();
    }

    // ---------- helpers ----------

    private static void Header(IXLWorksheet ws, params string[] names)
    {
        for (var i = 0; i < names.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = names[i];
            cell.Style.Font.Bold = true;
        }
        ws.SheetView.FreezeRows(1);
    }

    private static void Finish(IXLWorksheet ws) => ws.Columns().AdjustToContents();

    private static void SetNum(IXLCell cell, double? value) { if (value is { } v) cell.Value = v; }
    private static string Iso(DateTimeOffset t) => t.ToString("o", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseIso(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : default;
}
