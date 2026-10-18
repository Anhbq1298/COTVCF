using System.Net;
using System.Text;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Mcotis.Desktop.Application;

public sealed partial record ResultReport
{
    private void AppendMemberTables(StringBuilder html)
    {
        string E(string text) => WebUtility.HtmlEncode(text);
        html.Append("<style>table{border-collapse:collapse;width:100%;table-layout:fixed;margin:6pt 0 12pt}th,td{border:1px solid #222;padding:5pt;overflow-wrap:anywhere;vertical-align:top}th{background:#8fb3df;font-weight:normal;text-align:center;print-color-adjust:exact}td{text-align:right}td:first-child,td:last-child{text-align:left}tr{break-inside:avoid}thead{display:table-header-group}h2{font-size:11pt;break-after:avoid}</style>");
        foreach (var table in MemberTables)
        {
            html.Append("<h2>Member force summary — ").Append(E(table.CaseName)).Append("</h2><p>").Append(E(ResultMemberSummary.Note)).Append("</p>");
            if (table.Warning is { Length: > 0 }) html.Append("<p>Cable target not fitted: ").Append(E(table.Warning)).Append("</p>");
            html.Append("<table><colgroup><col style='width:22%'><col style='width:16%'><col style='width:16%'><col style='width:16%'><col style='width:30%'></colgroup><thead><tr>");
            foreach (var header in ResultMemberSummary.Headers) html.Append("<th>").Append(E(header)).Append("</th>");
            html.Append("</tr></thead><tbody>");
            foreach (var row in table.Rows)
            {
                html.Append("<tr>");
                foreach (var cell in row.Cells) html.Append("<td>").Append(E(cell)).Append("</td>");
                html.Append("</tr>");
            }
            html.Append("</tbody></table>");
        }
    }

    private void AppendMemberTables(Body body)
    {
        foreach (var summary in MemberTables)
        {
            body.Append(new Paragraph(new ParagraphProperties(new KeepNext()), new Run(new Text("Member force summary — " + summary.CaseName))),
                new Paragraph(new ParagraphProperties(new KeepNext()), new Run(new Text(ResultMemberSummary.Note))));
            if (summary.Warning is { Length: > 0 }) body.Append(new Paragraph(new ParagraphProperties(new KeepNext()), new Run(new Text("Cable target not fitted: " + summary.Warning))));
            body.Append(WordTable(summary), new Paragraph());
        }
    }

    private static Table WordTable(ResultMemberTable summary)
    {
        int total = PageWidth - Left - Right;
        int[] widths = [total * 22 / 100, total * 16 / 100, total * 16 / 100, total * 16 / 100, total - total * 22 / 100 - 3 * (total * 16 / 100)];
        var table = new Table(new TableProperties(new TableWidth { Width = total.ToString(), Type = TableWidthUnitValues.Dxa },
            new TableBorders(new TopBorder { Val = BorderValues.Single, Size = 4 }, new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 }, new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }),
            new TableLayout { Type = TableLayoutValues.Fixed }),
            new TableGrid(widths.Select(w => new GridColumn { Width = w.ToString() })));
        void AddRow(string[] values, bool header)
        {
            var row = new TableRow(new TableRowProperties(new CantSplit()));
            if (header) row.GetFirstChild<TableRowProperties>()!.Append(new TableHeader());
            for (int i = 0; i < values.Length; i++)
            {
                var properties = new TableCellProperties(new TableCellWidth { Width = widths[i].ToString(), Type = TableWidthUnitValues.Dxa });
                if (header) properties.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = "8FB3DF" });
                row.Append(new TableCell(properties, new Paragraph(new ParagraphProperties(
                    new SpacingBetweenLines { After = "60", Before = "60" },
                    new Justification { Val = header ? JustificationValues.Center : i is 0 or 4 ? JustificationValues.Left : JustificationValues.Right }),
                    new Run(new Text(values[i])))));
            }
            table.Append(row);
        }
        AddRow(ResultMemberSummary.Headers, true);
        foreach (var row in summary.Rows) AddRow(row.Cells, false);
        return table;
    }
}
