using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Mcotis.Desktop.Application;

/// <summary>What a Design window puts in its Word report: a title, the basis in a few lines, one summary table, the step-by-step
/// calculation of each member and the cautions that go with the result.</summary>
public sealed record DesignReport(string Title, IReadOnlyList<string> Basis, IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<(string Heading, string Text)> Calculations, IReadOnlyList<string> Notes);

/// <summary>
/// Export to Word… of the Design windows: the report as a .docx — A4 (landscape for a wide table), Arial, the summary table with a shaded header
/// row and a repeated header, and each member's calculation in a monospaced font so its columns stay aligned. No Word installation is needed.
/// </summary>
public static class DesignReportWord
{
    private const int PortraitWidth = 11906, PortraitHeight = 16838, Margin = 1000;
    private const string Font = "Arial", MonoFont = "Consolas";

    public static void Export(DesignReport report, string path)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;
        var landscape = report.Header.Count > 7;
        var (width, height) = landscape ? (PortraitHeight, PortraitWidth) : (PortraitWidth, PortraitHeight);

        body.Append(Para(report.Title, 32, bold: true, after: 80));
        body.Append(Para($"Mcotis · {DateTime.Now:yyyy-MM-dd}", 18, color: "666666", after: 160));
        foreach (var line in report.Basis) body.Append(Para(line, 20, after: 40));

        if (report.Rows.Count > 0)
        {
            body.Append(Para("Summary", 26, bold: true, before: 200, after: 80));
            body.Append(Table(report, width - 2 * Margin));
        }
        foreach (var note in report.Notes) body.Append(Para("• " + note, 18, before: 60, after: 20));
        foreach (var (heading, text) in report.Calculations)
        {
            body.Append(Para(heading, 24, bold: true, before: 320, after: 80, pageBreakBefore: true));
            foreach (var line in text.Replace("\r\n", "\n").Split('\n')) body.Append(Para(line.Length == 0 ? " " : line, 16, mono: true, after: 0));
        }

        var footerPart = main.AddNewPart<FooterPart>();
        var footer = new Paragraph(new ParagraphProperties(new Justification { Val = JustificationValues.Center }));
        footer.Append(Run($"{report.Title} — preliminary, check before use — page ", 16, color: "666666"));
        footer.Append(new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }));
        footer.Append(new Run(new FieldCode(" PAGE ") { Space = SpaceProcessingModeValues.Preserve }));
        footer.Append(new Run(new FieldChar { FieldCharType = FieldCharValues.End }));
        footerPart.Footer = new Footer(footer);
        body.Append(new SectionProperties(new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footerPart) },
            new PageSize { Width = (uint)width, Height = (uint)height, Orient = landscape ? PageOrientationValues.Landscape : PageOrientationValues.Portrait },
            new PageMargin { Top = Margin, Bottom = Margin, Left = Margin, Right = Margin, Header = 360, Footer = 360, Gutter = 0 }));
        main.Document.Save();
    }

    private static Table Table(DesignReport report, int totalWidth)
    {
        var columns = report.Header.Count;
        var borders = new TableBorders(new[] { typeof(TopBorder), typeof(LeftBorder), typeof(BottomBorder), typeof(RightBorder), typeof(InsideHorizontalBorder), typeof(InsideVerticalBorder) }
            .Select(type => (OpenXmlElement)Activator.CreateInstance(type)!).ToArray());
        foreach (var border in borders.Elements<BorderType>()) { border.Val = BorderValues.Single; border.Size = 4; border.Color = "BBBBBB"; }
        var table = new Table(new TableProperties(new TableWidth { Width = totalWidth.ToString(), Type = TableWidthUnitValues.Dxa }, borders, new TableLayout { Type = TableLayoutValues.Fixed }));
        table.Append(new TableGrid(Enumerable.Range(0, columns).Select(_ => new GridColumn { Width = (totalWidth / columns).ToString() })));
        TableRow Row(IEnumerable<string> cells, bool header)
        {
            var row = new TableRow(new TableRowProperties(header ? [new TableHeader()] : []));
            foreach (var cell in cells)
                row.Append(new TableCell(
                    new TableCellProperties(new TableCellWidth { Width = (totalWidth / columns).ToString(), Type = TableWidthUnitValues.Dxa },
                        new Shading { Val = ShadingPatternValues.Clear, Fill = header ? "DCE6EF" : "FFFFFF" }),
                    Para(cell, 15, bold: header, after: 0)));
            return row;
        }
        table.Append(Row(report.Header, true));
        foreach (var row in report.Rows) table.Append(Row(row, false));
        return table;
    }

    private static Paragraph Para(string text, int halfPoints, bool bold = false, string? color = null, bool mono = false, int before = 0, int after = 120, bool pageBreakBefore = false)
    {
        var properties = new ParagraphProperties(new SpacingBetweenLines { Before = before.ToString(), After = after.ToString(), Line = mono ? "240" : "259", LineRule = LineSpacingRuleValues.Auto });
        if (pageBreakBefore) properties.PrependChild(new PageBreakBefore());
        return new Paragraph(properties, Run(text, halfPoints, bold, color, mono));
    }

    private static Run Run(string text, int halfPoints, bool bold = false, string? color = null, bool mono = false)
    {
        var font = mono ? MonoFont : Font;
        var properties = new RunProperties(new RunFonts { Ascii = font, HighAnsi = font, ComplexScript = font });
        if (bold) properties.Append(new Bold());
        if (color is not null) properties.Append(new Color { Val = color });
        properties.Append(new FontSize { Val = halfPoints.ToString() });
        return new Run(properties, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }
}
