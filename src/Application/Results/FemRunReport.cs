using System.Globalization;
using System.Net;
using System.Text;
using COT_VCF.Domain;
using COT_VCF.Geometry;

namespace COT_VCF.Application.Results;

/// <summary>A self-contained result viewer for the actual completed FEM cases, independent of MB study data.</summary>
public static class FemRunReport
{
    public static string BuildHtml(FemStudyRun run, string model, string? selectedCase = null, ReportPrintSettings? print = null)
    {
        if (run.Results is not { Count: > 0 } results)
            throw new InvalidOperationException("No completed load-case results are available.");
        static string E(string value) => WebUtility.HtmlEncode(value);
        static string N(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—";
        var b = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>FEM Results</title><style>");
        b.Append(ReportPrintEngine.Css).Append("body{font:14px 'Segoe UI',sans-serif;color:#3a4048;background:#f3f4f6;margin:24px}header,section,.toolbar{background:white;padding:16px;margin-bottom:12px}h1{font-size:22px}h2{font-size:17px}select{padding:6px;margin-right:16px}table{border-collapse:collapse;width:100%;font-size:12px}th,td{border:1px solid #c7ccd2;padding:6px;text-align:right}th:first-child,td:first-child{text-align:left}th{background:#e6e8eb}td{font-variant-numeric:tabular-nums}.warning{color:#a44b00}[hidden]{display:none!important}")
            .Append("</style>").Append(ReportPrintEngine.PageStyle).Append("</head><body><header data-pr=\"info\"><h1>FEM Results</h1><p>")
            .Append(E(model)).Append("</p><p>Completed analysis snapshot · ").Append(E(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"))).Append("</p></header>");
        b.Append("<div class=\"toolbar\" data-print-controls><label>Load case / combination <select id=\"case\">");
        var names = results.Keys.ToArray();
        for (var i = 0; i < names.Length; i++)
            b.Append($"<option value=\"{i}\"{(names[i] == selectedCase ? " selected" : "")}>{E(names[i])}</option>");
        b.Append("</select></label><label>Results <select id=\"topic\"><option value=\"0\">Deflection</option><option value=\"1\">Frame forces</option><option value=\"2\">Support reactions</option><option value=\"3\">Cable forces / target fit</option></select></label>")
            .Append(ReportPrintEngine.ToolbarButton).Append("</div><p data-print-view id=\"view\"></p>");
        void Table(string title, string[] headers, IEnumerable<string[]> rows)
        {
            b.Append("<h2>").Append(E(title)).Append("</h2><table><thead><tr>");
            foreach (var header in headers) b.Append("<th>").Append(E(header)).Append("</th>");
            b.Append("</tr></thead><tbody>");
            foreach (var row in rows)
            {
                b.Append("<tr>");
                foreach (var cell in row) b.Append("<td>").Append(E(cell)).Append("</td>");
                b.Append("</tr>");
            }
            b.Append("</tbody></table>");
        }
        for (var i = 0; i < names.Length; i++)
        {
            var r = results[names[i]];
            void Start(int topic) => b.Append($"<section data-case=\"{i}\" data-topic=\"{topic}\" data-pr=\"chart\" hidden>");
            Start(0);
            Table("Joint displacements", ["Point", "Ux [mm]", "Uz [mm]", "Rotation [rad]"],
                r.Joints.Select(j => new[] { j.PointName, N(j.UXM * 1000), N(j.UZM * 1000), N(j.RotationRad) }));
            b.Append("</section>");
            Start(1);
            Table("Frame forces at mesh stations", ["Member", "Station [%]", "N [kN]", "V [kN]", "M [kN·m]"],
                r.Frames.SelectMany(f => (f.Stations is { Count: > 0 } stations ? stations : new[] {
                    new FemFrameStation(0, f.AxialStartKN, f.ShearStartKN, f.MomentStartKNm, 0, 0),
                    new FemFrameStation(1, f.AxialEndKN, f.ShearEndKN, f.MomentEndKNm, 0, 0) })
                    .Select(s => new[] { f.MemberName, N(s.Fraction * 100), N(s.AxialKN), N(s.ShearKN), N(s.MomentKNm) })));
            b.Append("</section>");
            Start(2);
            Table("Support reactions", ["Point", "Rx [kN]", "Rz [kN]"],
                r.SupportReactions.Select(s => new[] { s.PointName, N(s.ReactionXKN), N(s.ReactionZKN) }));
            b.Append("</section>");
            Start(3);
            if (r.TargetFitFailure is { } failure) b.Append("<p class=\"warning\">Not fitted: ").Append(E(failure)).Append("</p>");
            Table("Cable forces", ["Cable", "Target [kN]", "Stage force [kN]", "Result force [kN]", "State", "Sag [mm]", "Stiffness [%]"],
                r.Cables.Select(c => new[] { c.MemberName, N(c.TargetForceKN), N(c.PretensionStageForceKN), N(c.AxialForceKN),
                    c.Active ? "Taut" : "Slack", N(c.SagM * 1000), N(c.TangentStiffnessRatio * 100) }));
            b.Append("</section>");
        }
        b.Append("<script>const c=document.getElementById('case'),t=document.getElementById('topic');function show(){document.querySelectorAll('[data-case]').forEach(s=>s.hidden=s.dataset.case!==c.value||s.dataset.topic!==t.value);document.getElementById('view').textContent=c.options[c.selectedIndex].text+' · '+t.options[t.selectedIndex].text;}c.onchange=t.onchange=show;show();</script>")
            .Append(ReportPrintEngine.Scripts(ReportPrintEngine.Describe(print, "FEM Results", model, DateTimeOffset.Now)))
            .Append("</body></html>");
        return b.ToString();
    }
}
