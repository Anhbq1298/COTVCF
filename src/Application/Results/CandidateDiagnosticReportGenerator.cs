using System.IO;
using System.Net;
using System.Text;
using COT_VCF.Domain.Results;

namespace COT_VCF.Application.Results;

/// <summary>Writes <c>Deflection_Diagnostic_&lt;key&gt;.html</c> into a candidate folder — a standalone
/// QA/QC / traceability document for one candidate. Not the consolidated study report.</summary>
public sealed class CandidateDiagnosticReportGenerator
{
    public string Write(CandidateResultDto result, string iterationDirectory)
    {
        Directory.CreateDirectory(iterationDirectory);
        var path = Path.Combine(iterationDirectory, result.DiagnosticReportFileName);
        File.WriteAllText(path, BuildHtml(result), new UTF8Encoding(false));
        return path;
    }

    public string BuildHtml(CandidateResultDto r)
    {
        var p = r.Parameters;
        var b = new StringBuilder();
        b.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        b.Append($"<title>Deflection Diagnostic — {E(r.IterationKey)}</title>");
        b.Append("<style>").Append(Css).Append("</style></head><body>");

        b.Append("<header><h1>Candidate Diagnostic Report — Deflection · U<sub>z</sub></h1>");
        b.Append("<p class=\"purpose\">Records the analysis configuration, execution status, extracted monitored-point "
               + "deflections, and validation information for this individual study candidate. "
               + "For traceability, QA/QC, debugging and result verification — <strong>not</strong> the consolidated study report.</p>");
        b.Append($"<p class=\"gen\">Generated {E(r.ExtractionCompletedAt.ToString("yyyy-MM-dd HH:mm:ss"))} · schema 1</p></header>");

        b.Append("<section><h2>Candidate Identity</h2>").Append(Kv(
            ("Candidate ID", r.IterationKey),
            ("Run index", p.RunIndex.ToString()),
            ("Study mode", r.StudyMode.ToString()),
            ("Candidate SDB", r.CandidateModelPath),
            ("Baseline model", $"{r.BaselineModelName}  ({r.BaselineModelPath})"))).Append("</section>");

        b.Append("<section><h2>Candidate Parameters</h2>").Append(Kv(
            ("Loading candidate", $"{p.LoadingCandidateId} — {p.LoadingLabel}"),
            ("Load level", p.LoadLevelKPa is { } q ? $"{q:0.###} kPa" : "(mixed / n-a)"),
            ("Height candidate", p.HeightCandidateId),
            ("Truss height", $"{p.TrussHeightM:0.####} m"),
            ("ΔH from baseline", $"{p.DeltaHM:+0.####;-0.####;0} m"),
            ("Pretension candidate", $"{p.PretensionCandidateId} — {p.PretensionLabel}"),
            ("Pretension target", p.PretensionKN is { } f ? $"{f:0.###} kN" : "(mixed / none)"))).Append("</section>");

        b.Append("<section><h2>Analysis &amp; Extraction</h2>").Append(Kv(
            ("Extraction status", r.ExtractionStatus.ToString()),
            ("Analysis start", r.AnalysisStartedAt.ToString("yyyy-MM-dd HH:mm:ss")),
            ("Analysis finish", r.AnalysisCompletedAt.ToString("yyyy-MM-dd HH:mm:ss")),
            ("Extraction finish", r.ExtractionCompletedAt.ToString("yyyy-MM-dd HH:mm:ss")),
            ("Output component", "U3 — Global Z · mm"),
            ("Output case", r.Groups.Count > 0 ? r.Groups[0].OutputCase : "(none)"))).Append("</section>");

        foreach (var g in r.Groups)
        {
            b.Append($"<section><h2>Monitored Points — {E(g.GroupName)}</h2>");
            b.Append($"<p class=\"meta\">Output case <code>{E(g.OutputCase)}</code> · profile axis {g.ProfileAxis} · {g.Points.Count} point(s)</p>");
            b.Append("<table><thead><tr><th>#</th><th>Point</th><th class=\"n\">X [m]</th><th class=\"n\">Y [m]</th>"
                   + "<th class=\"n\">Z [m]</th><th class=\"n\">U<sub>z</sub> [mm]</th><th>Status</th></tr></thead><tbody>");
            foreach (var pt in g.Points)
                b.Append($"<tr class=\"{(pt.Status == ResultValueStatus.Valid ? "" : "bad")}\">"
                       + $"<td>{pt.Sequence}</td><td>{E(pt.PointName)}</td>"
                       + $"<td class=\"n\">{pt.X:0.####}</td><td class=\"n\">{pt.Y:0.####}</td><td class=\"n\">{pt.Z:0.####}</td>"
                       + $"<td class=\"n\">{(pt.UzMm is { } u ? u.ToString("0.###") : "—")}</td>"
                       + $"<td>{pt.Status}{(pt.Warning is null ? "" : $" · {E(pt.Warning)}")}</td></tr>");
            b.Append("</tbody></table></section>");
        }

        b.Append("<section><h2>Validation &amp; Warnings</h2>");
        if (r.Warnings.Count == 0) b.Append("<p class=\"ok\">No warnings.</p>");
        else
        {
            b.Append("<table><thead><tr><th>Severity</th><th>Category</th><th>Message</th><th>Time</th></tr></thead><tbody>");
            foreach (var w in r.Warnings)
                b.Append($"<tr class=\"{(w.Severity == ResultWarningSeverity.Error ? "bad" : "warn")}\">"
                       + $"<td>{w.Severity}</td><td>{E(w.Category)}</td><td>{E(w.Message)}</td>"
                       + $"<td>{w.Timestamp:HH:mm:ss}</td></tr>");
            b.Append("</tbody></table>");
        }
        b.Append("</section>");

        b.Append("</body></html>");
        return b.ToString();
    }

    private static string Kv(params (string K, string V)[] rows)
    {
        var b = new StringBuilder("<table class=\"kv\"><tbody>");
        foreach (var (k, v) in rows) b.Append($"<tr><th>{E(k)}</th><td>{E(v)}</td></tr>");
        return b.Append("</tbody></table>").ToString();
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private const string Css = """
    :root{--acc:#1F4788;--acc2:#2E90FA;--bd:#C7CCD2;--su:#F3F4F6;--tx:#1A1A1A;--mu:#666;--bad:#C62828;--warn:#F57C00;--ok:#2E7D32}
    *{box-sizing:border-box}body{margin:0;padding:24px;font:13px/1.5 "Segoe UI",system-ui,sans-serif;color:var(--tx);background:#fff}
    header{border-bottom:2px solid var(--acc);padding-bottom:12px;margin-bottom:18px}
    h1{font-size:18px;margin:0 0 6px}h2{font-size:13px;text-transform:uppercase;letter-spacing:.04em;color:var(--acc);margin:22px 0 8px}
    .purpose{color:var(--mu);max-width:70ch;margin:6px 0}.gen{color:var(--mu);font:11px/1.4 Consolas,monospace;margin:4px 0 0}
    .meta{color:var(--mu);margin:0 0 8px}code{font:12px Consolas,monospace;background:var(--su);padding:1px 4px;border-radius:2px}
    table{border-collapse:collapse;width:100%;font-size:12px}th,td{border:1px solid var(--bd);padding:4px 8px;text-align:left}
    thead th{background:var(--su);color:var(--mu);font-weight:600}.n{text-align:right;font-family:Consolas,monospace}
    table.kv{width:auto}table.kv th{background:var(--su);color:var(--mu);white-space:nowrap}
    tr.bad td{background:#FDECEA}tr.warn td{background:#FFF4E5}.ok{color:var(--ok)}
    """;
}
