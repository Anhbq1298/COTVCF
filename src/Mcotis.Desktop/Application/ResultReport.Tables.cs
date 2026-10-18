using System.IO;
using System.Net;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace Mcotis.Desktop.Application;

public sealed partial record ResultReport
{
    /// <summary>Height of a section drawing in a table cell [EMU]: 1.2 cm.</summary>
    private const long CellImageHeight = 432000;

    private void AppendTables(StringBuilder html)
    {
        string E(string text) => WebUtility.HtmlEncode(text);
        html.Append("<style>table{border-collapse:collapse;width:100%;table-layout:fixed;margin:6pt 0 12pt;font-size:9pt}th,td{border:1px solid #222;padding:4pt;overflow-wrap:anywhere;vertical-align:middle}" +
                    "th{background:#8fb3df;font-weight:normal;text-align:center;print-color-adjust:exact}td{text-align:right}td.l{text-align:left}td.c{text-align:center}td img{height:1.2cm;width:auto;display:inline-block}" +
                    "tr{break-inside:avoid}tr.t td{font-weight:bold}thead{display:table-header-group}h2{font-size:11pt;break-after:avoid;margin:12pt 0 4pt}p.n{font-size:9pt;margin:0 0 4pt}" +
                    "@page land{size:A4 landscape;margin:1.58cm 1.28cm 1.58cm 1.88cm}.land{page:land}</style>");
        bool landscape = false;
        foreach (var table in Tables)
        {
            if (table.Landscape && !landscape) { html.Append("<div class='land'>"); landscape = true; }
            html.Append("<h2>").Append(E(table.Title)).Append("</h2>");
            if (table.Note is { Length: > 0 } note) html.Append("<p class='n'>").Append(E(note)).Append("</p>");
            var sum = table.Columns.Sum(column => column.Width);
            html.Append("<table><colgroup>");
            foreach (var column in table.Columns) html.Append("<col style='width:").Append((column.Width / sum * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append("%'>");
            html.Append("</colgroup><thead><tr>");
            foreach (var column in table.Columns) html.Append("<th>").Append(E(column.Header)).Append("</th>");
            html.Append("</tr></thead><tbody>");
            for (int r = 0; r < table.Rows.Count; r++)
            {
                html.Append(r >= table.Rows.Count - table.TotalRows ? "<tr class='t'>" : "<tr>");
                for (int c = 0; c < table.Columns.Count; c++)
                {
                    var cell = c < table.Rows[r].Count ? table.Rows[r][c] : new ReportCell("");
                    if (cell.Image is { } png)
                        html.Append("<td class='c'><img alt='").Append(E(cell.Text)).Append("' src='data:image/png;base64,").Append(Convert.ToBase64String(png)).Append("'></td>");
                    else html.Append(table.Columns[c].Left ? "<td class='l'>" : "<td>").Append(E(cell.Text)).Append("</td>");
                }
                html.Append("</tr>");
            }
            html.Append("</tbody></table>");
        }
        if (landscape) html.Append("</div>");
    }

    /// <summary>The tables in Word. The portrait section ends before the first landscape table; <paramref name="lastSection"/> is then landscape.</summary>
    private void AppendTables(Body body, MainDocumentPart main, ref uint drawingId, out bool lastSectionLandscape)
    {
        lastSectionLandscape = false;
        foreach (var table in Tables)
        {
            if (table.Landscape && !lastSectionLandscape)
            {
                body.Append(new Paragraph(new ParagraphProperties(PortraitSection())));
                lastSectionLandscape = true;
            }
            body.Append(new Paragraph(new ParagraphProperties(new KeepNext()), new Run(new RunProperties(new Bold()), new Text(table.Title))));
            if (table.Note is { Length: > 0 } note)
                body.Append(new Paragraph(new ParagraphProperties(new KeepNext()), new Run(new RunProperties(new FontSize { Val = "18" }), new Text(note) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve })));
            body.Append(WordTable(table, main, ref drawingId), new Paragraph());
        }
    }

    internal static SectionProperties PortraitSection() =>
        new(new PageSize { Width = PageWidth, Height = PageHeight }, new PageMargin { Top = Top, Bottom = Bottom, Left = Left, Right = Right, Header = 0, Footer = 0, Gutter = 0 });

    internal static SectionProperties LandscapeSection() =>
        new(new PageSize { Width = PageHeight, Height = PageWidth, Orient = PageOrientationValues.Landscape },
            new PageMargin { Top = Right, Bottom = Right, Left = Left, Right = Bottom, Header = 0, Footer = 0, Gutter = 0 });

    private static Table WordTable(ReportTable source, MainDocumentPart main, ref uint drawingId)
    {
        int total = source.Landscape ? PageHeight - Left - Bottom : PageWidth - Left - Right;
        var sum = source.Columns.Sum(column => column.Width);
        var widths = source.Columns.Select(column => (int)(total * column.Width / sum)).ToArray();
        var table = new Table(new TableProperties(new TableWidth { Width = total.ToString(), Type = TableWidthUnitValues.Dxa },
            new TableBorders(new TopBorder { Val = BorderValues.Single, Size = 4 }, new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 }, new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }),
            new TableLayout { Type = TableLayoutValues.Fixed }),
            new TableGrid(widths.Select(w => new GridColumn { Width = w.ToString() })));
        void AddRow(IReadOnlyList<ReportCell> values, bool header, bool bold, ref uint id)
        {
            var row = new TableRow(new TableRowProperties(new CantSplit()));
            if (header) row.GetFirstChild<TableRowProperties>()!.Append(new TableHeader());
            for (int i = 0; i < widths.Length; i++)
            {
                var cell = i < values.Count ? values[i] : new ReportCell("");
                var properties = new TableCellProperties(new TableCellWidth { Width = widths[i].ToString(), Type = TableWidthUnitValues.Dxa });
                if (header) properties.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = "8FB3DF" });
                properties.Append(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });
                var justification = header || cell.Image is not null ? JustificationValues.Center : source.Columns[i].Left ? JustificationValues.Left : JustificationValues.Right;
                var paragraph = new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "40", Before = "40" }, new Justification { Val = justification }));
                if (cell.Image is { } png) paragraph.Append(new Run(CellImage(main, png, widths[i], ++id, cell.Text)));
                else
                {
                    var runProperties = bold ? new RunProperties(new Bold(), new FontSize { Val = "18" }) : new RunProperties(new FontSize { Val = "18" });
                    paragraph.Append(new Run(runProperties, new Text(cell.Text) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }));
                }
                row.Append(new TableCell(properties, paragraph));
            }
            table.Append(row);
        }
        uint next = drawingId;
        AddRow(source.Columns.Select(column => new ReportCell(column.Header)).ToArray(), true, false, ref next);
        for (int r = 0; r < source.Rows.Count; r++) AddRow(source.Rows[r], false, r >= source.Rows.Count - source.TotalRows, ref next);
        drawingId = next;
        return table;
    }

    /// <summary>A section drawing in a table cell: 1.2 cm high, narrower than its column.</summary>
    private static Drawing CellImage(MainDocumentPart main, byte[] png, int columnDxa, uint id, string name)
    {
        var part = main.AddImagePart(ImagePartType.Png);
        using (var stream = new MemoryStream(png)) part.FeedData(stream);
        long width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        long height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        long cy = CellImageHeight, cx = height > 0 ? cy * width / height : cy;
        long maxWidth = (columnDxa - 200) * 635L;
        if (cx > maxWidth && maxWidth > 0) { cy = cy * maxWidth / cx; cx = maxWidth; }
        var graphic = new A.Graphic(new A.GraphicData(new PIC.Picture(
            new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = id, Name = name }, new PIC.NonVisualPictureDrawingProperties()),
            new PIC.BlipFill(new A.Blip { Embed = main.GetIdOfPart(part) }, new A.Stretch(new A.FillRectangle())),
            new PIC.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }), new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" });
        return new Drawing(new DW.Inline(new DW.Extent { Cx = cx, Cy = cy }, new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
            new DW.DocProperties { Id = id, Name = name, Description = name }, new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }), graphic)
            { DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0 });
    }
}
