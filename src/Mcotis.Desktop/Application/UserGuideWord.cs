using System.IO;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace Mcotis.Desktop.Application;

/// <summary>
/// User Guide → Export to Word…: an article as a Word document — A4, margins top/left 1.88 cm, bottom 1.28 cm,
/// right 1.58 cm, Arial 11 with 1.15 line spacing
/// throughout; the title, the category and menu path, the lead, the sections (## → Heading 1, ### → Heading 2), bullet and
/// numbered lists, bold and code, links as their text, the screenshots scaled to the page width with their captions,
/// See also, and the article title with the page number in the footer.
/// </summary>
public static class UserGuideWord
{
    public const int PageWidth = 11906, PageHeight = 16838;                  // twips: A4
    public const int MarginTop = 1066, MarginBottom = 726, MarginLeft = 1066, MarginRight = 896;
    public const string Font = "Arial";
    public const int FontHalfPoints = 22;                                      // 11 pt
    public const int LineSpacing = 276;                                        // 1.15 × 240
    private const long EmuPerPixel = 9525;                                     // at 96 dpi
    private const long ContentWidthEmu = (PageWidth - MarginLeft - MarginRight) * 635L;
    private static readonly Regex Inline = new(@"\*\*(.+?)\*\*|`([^`]+)`|\[\[([^\]|]+)(?:\|([^\]]+))?\]\]", RegexOptions.Compiled);
    private static readonly Regex Numbered = new(@"^\d+\.\s+(.*)$", RegexOptions.Compiled);
    private const int BulletNumbering = 1, DecimalAbstract = 2;

    public static void Export(UserGuideArticle article, string path)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new Document(new Body());
        AddStyles(main);
        var numbering = AddNumbering(main);
        var body = main.Document.Body!;
        var images = 0u;
        var nextNumbering = 10;

        body.Append(Para(article.Title, "Title"));
        body.Append(Para($"Mcotis User Guide  ·  {article.Category}", "Subtitle"));
        var where = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Where" }));
        where.Append(TextRun("Where: ", bold: true));
        foreach (var run in Runs(article.Where)) where.Append(run);
        body.Append(where);

        foreach (var section in article.Sections)
        {
            if (section.Heading.Length > 0) body.Append(Para(section.Heading, "Heading1"));
            var paragraph = new List<string>();
            int? listNumber = null;
            void Flush()
            {
                if (paragraph.Count > 0) body.Append(Rich(string.Join(" ", paragraph)));
                paragraph.Clear();
            }
            foreach (var raw in section.Lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) { Flush(); listNumber = null; continue; }
                if (line.StartsWith("### ", StringComparison.Ordinal)) { Flush(); listNumber = null; body.Append(Para(line[4..].Trim(), "Heading2")); continue; }
                if (UserGuide.ImagePattern.Match(line) is { Success: true } image)
                {
                    Flush();
                    listNumber = null;
                    if (UserGuide.Image(image.Groups[2].Value.Trim()) is { } bytes) body.Append(Picture(main, bytes, ++images, image.Groups[1].Value.Trim()));
                    if (image.Groups[1].Value.Trim() is { Length: > 0 } caption) body.Append(Para(caption, "Caption"));
                    continue;
                }
                if (line.StartsWith("- ", StringComparison.Ordinal)) { Flush(); listNumber = null; body.Append(ListItem(line[2..], BulletNumbering)); continue; }
                if (Numbered.Match(line) is { Success: true } step)
                {
                    Flush();
                    if (listNumber is null)
                    {
                        listNumber = nextNumbering++;
                        numbering.Append(new NumberingInstance(new AbstractNumId { Val = DecimalAbstract },
                            new LevelOverride(new StartOverrideNumberingValue { Val = 1 }) { LevelIndex = 0 }) { NumberID = listNumber });
                    }
                    body.Append(ListItem(step.Groups[1].Value, listNumber.Value));
                    continue;
                }
                listNumber = null;
                paragraph.Add(line);
            }
            Flush();
        }
        if (article.SeeAlso.Count > 0)
        {
            body.Append(Para("See also", "Heading1"));
            foreach (var title in article.SeeAlso) body.Append(ListItem(title, BulletNumbering));
        }

        // footer: the article and the page number
        var footerPart = main.AddNewPart<FooterPart>();
        var footer = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Footer" }));
        footer.Append(TextRun($"Mcotis User Guide — {article.Title} — page "));
        footer.Append(new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }));
        footer.Append(new Run(new FieldCode(" PAGE ") { Space = SpaceProcessingModeValues.Preserve }));
        footer.Append(new Run(new FieldChar { FieldCharType = FieldCharValues.End }));
        footerPart.Footer = new Footer(footer);
        body.Append(new SectionProperties(
            new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footerPart) },
            new PageSize { Width = PageWidth, Height = PageHeight },
            new PageMargin { Top = MarginTop, Bottom = MarginBottom, Left = MarginLeft, Right = MarginRight, Header = 360, Footer = 360, Gutter = 0 }));
        main.Document.Save();
    }

    // ---------------- paragraphs ----------------

    private static Paragraph Para(string text, string style) =>
        new(new ParagraphProperties(new ParagraphStyleId { Val = style }), TextRun(text));

    private static Paragraph Rich(string text)
    {
        var paragraph = new Paragraph();
        foreach (var run in Runs(text)) paragraph.Append(run);
        return paragraph;
    }

    private static Paragraph ListItem(string text, int numbering)
    {
        var paragraph = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "ListParagraph" },
            new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = numbering })));
        foreach (var run in Runs(text)) paragraph.Append(run);
        return paragraph;
    }

    /// <summary>Runs of a Markdown line: **bold**, code and links as text, all in the shared Arial format.</summary>
    private static IEnumerable<Run> Runs(string text)
    {
        var index = 0;
        foreach (Match match in Inline.Matches(text))
        {
            if (match.Index > index) yield return TextRun(text[index..match.Index]);
            if (match.Groups[1].Success) yield return TextRun(match.Groups[1].Value, bold: true);
            else if (match.Groups[2].Success) yield return TextRun(match.Groups[2].Value, code: true);
            else yield return TextRun(match.Groups[4].Success ? match.Groups[4].Value : match.Groups[3].Value);
            index = match.Index + match.Length;
        }
        if (index < text.Length) yield return TextRun(text[index..]);
    }

    private static Run TextRun(string text, bool bold = false, bool code = false)
    {
        var run = new Run();
        if (bold || code)
        {
            var properties = new RunProperties();
            if (code) properties.Append(new RunFonts { Ascii = Font, HighAnsi = Font, ComplexScript = Font, EastAsia = Font });
            if (bold) properties.Append(new Bold());
            run.Append(properties);
        }
        run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return run;
    }

    /// <summary>A screenshot inline, its own size up to the width between the margins.</summary>
    private static Paragraph Picture(MainDocumentPart main, byte[] png, uint id, string caption)
    {
        var part = main.AddImagePart(ImagePartType.Png);
        using (var stream = new MemoryStream(png)) part.FeedData(stream);
        // PNG IHDR: width and height, big-endian, at bytes 16 and 20
        long width = png.Length > 24 ? (png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19]) : 800;
        long height = png.Length > 24 ? (png[20] << 24 | png[21] << 16 | png[22] << 8 | png[23]) : 600;
        var cx = Math.Min(width * EmuPerPixel, ContentWidthEmu);
        var cy = cx * height / Math.Max(1, width);
        var name = $"Screenshot {id}";
        var graphic = new A.Graphic(new A.GraphicData(
            new PIC.Picture(
                new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = id, Name = name, Description = caption }, new PIC.NonVisualPictureDrawingProperties()),
                new PIC.BlipFill(new A.Blip { Embed = main.GetIdOfPart(part) }, new A.Stretch(new A.FillRectangle())),
                new PIC.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }),
                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
                    new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = "C7CCD2" })) { Width = 6350 })))
        { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" });
        var inline = new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
            new DW.DocProperties { Id = id, Name = name, Description = caption },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            graphic)
        { DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0 };
        return new Paragraph(new ParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = "120", After = "0" }), new Run(new Drawing(inline)));
    }

    // ---------------- styles and numbering ----------------

    private static void AddStyles(MainDocumentPart main)
    {
        var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
        RunFonts Arial() => new() { Ascii = Font, HighAnsi = Font, ComplexScript = Font, EastAsia = Font };
        Style Paragraph(string id, string name, int halfPoints, bool bold = false, bool italic = false, string? colour = null,
            int before = 0, int after = 160, bool keepNext = false)
        {
            // schema order of rPr: rFonts, b, i, color, sz, szCs
            var run = new StyleRunProperties(Arial());
            if (bold) run.Append(new Bold());
            if (italic) run.Append(new Italic());
            if (colour is not null) run.Append(new Color { Val = colour });
            run.Append(new FontSize { Val = halfPoints.ToString() }, new FontSizeComplexScript { Val = halfPoints.ToString() });
            // schema order of pPr: keepNext before spacing
            var paragraph = new StyleParagraphProperties();
            if (keepNext) paragraph.Append(new KeepNext());
            paragraph.Append(new SpacingBetweenLines { Before = before.ToString(), After = after.ToString(), Line = LineSpacing.ToString(), LineRule = LineSpacingRuleValues.Auto });
            return new Style(new StyleName { Val = name }, new BasedOn { Val = "Normal" }, new PrimaryStyle(), paragraph, run) { Type = StyleValues.Paragraph, StyleId = id };
        }
        var styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(Arial(), new FontSize { Val = FontHalfPoints.ToString() }, new FontSizeComplexScript { Val = FontHalfPoints.ToString() },
                    new Languages { Val = "en-SG" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "160", Line = LineSpacing.ToString(), LineRule = LineSpacingRuleValues.Auto }))),
            new Style(new StyleName { Val = "Normal" }, new PrimaryStyle(),
                new StyleParagraphProperties(new SpacingBetweenLines { After = "160", Line = LineSpacing.ToString(), LineRule = LineSpacingRuleValues.Auto }),
                new StyleRunProperties(Arial(), new FontSize { Val = FontHalfPoints.ToString() })) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
            Paragraph("Title", "Title", FontHalfPoints, bold: true, colour: "1F4788", after: 60),
            Paragraph("Subtitle", "Subtitle", FontHalfPoints, colour: "6B7178", after: 60),
            Paragraph("Where", "Where", FontHalfPoints, after: 240),
            Paragraph("Heading1", "heading 1", FontHalfPoints, bold: true, colour: "1F4788", before: 240, after: 120, keepNext: true),
            Paragraph("Heading2", "heading 2", FontHalfPoints, bold: true, colour: "1A1D21", before: 180, after: 80, keepNext: true),
            Paragraph("Caption", "caption", FontHalfPoints, italic: true, colour: "595959", before: 60, after: 240),
            Paragraph("ListParagraph", "List Paragraph", FontHalfPoints, after: 60),
            Paragraph("Footer", "footer", FontHalfPoints, colour: "6B7178", after: 0));
        stylesPart.Styles = styles;
        stylesPart.Styles.Save();
    }

    private static Numbering AddNumbering(MainDocumentPart main)
    {
        var part = main.AddNewPart<NumberingDefinitionsPart>();
        Level Level(NumberFormatValues format, string text) => new(
            new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = format }, new LevelText { Val = text },
            new LevelJustification { Val = LevelJustificationValues.Left },
            new PreviousParagraphProperties(new Indentation { Left = "720", Hanging = "360" }),
            new NumberingSymbolRunProperties(new RunFonts { Ascii = Font, HighAnsi = Font, Hint = FontTypeHintValues.Default })) { LevelIndex = 0 };
        var numbering = new Numbering(
            new AbstractNum(Level(NumberFormatValues.Bullet, "•")) { AbstractNumberId = 1 },
            new AbstractNum(Level(NumberFormatValues.Decimal, "%1.")) { AbstractNumberId = DecimalAbstract },
            new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = BulletNumbering });
        part.Numbering = numbering;
        return numbering;
    }
}
