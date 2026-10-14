using System.IO;
using System.Net;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Infrastructure;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
namespace Mcotis.Desktop.Application;
public enum ResultReportOutput { AxialForce, BendingMoment, Shear, Reactions, Deflection }
public sealed record ResultReportFigure(string CaseName, string Title, byte[] Png, string? Warning)
{
    public string Caption(int number) => $"Figure {number}. {Title} — {CaseName}";
}
public sealed record ResultReport(string ModelName, IReadOnlyList<ResultReportFigure> Figures)
{
    public const int PageWidth = 11906, PageHeight = 16838;
    public const int Top = 1066, Bottom = 726, Right = 896, Left = 1066;
    public static ResultReport Create(string modelName, FemStudyRun run, IReadOnlyList<string> cases, IReadOnlyList<ResultReportOutput> outputs, Func<FemAnalysisResult, FemResultMode, byte[]> capture)
    {
        if (cases.Count == 0 || outputs.Count == 0) throw new InvalidOperationException("Select at least one load case / combination and one result type.");
        foreach (var name in cases)
            if (run.Results is null || !run.Results.ContainsKey(name)) throw new InvalidOperationException($"No solved result is available for '{name}'. Run analysis first.");
        var figures = new List<ResultReportFigure>();
        foreach (var name in cases.Distinct())
        foreach (var output in outputs.Distinct())
        {
            var (mode, title) = output switch
            {
                ResultReportOutput.AxialForce => (FemResultMode.AxialForce, "Axial forces / cables"),
                ResultReportOutput.BendingMoment => (FemResultMode.BendingMoment, "Bending moments"),
                ResultReportOutput.Shear => (FemResultMode.Shear, "Shear forces"),
                ResultReportOutput.Reactions => (FemResultMode.Reactions, "Support reactions"),
                ResultReportOutput.Deflection => (FemResultMode.DeflectedShape, "Deflection"),
                _ => throw new InvalidOperationException("Unsupported graphical output.")
            };
            var result = run.Results![name]; var png = capture(result, mode);
            if (png.Length < 24 || !png.Take(8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) throw new InvalidOperationException("The result view could not be captured.");
            figures.Add(new(name, title, png, result.TargetFitFailure));
        }
        return new(modelName, figures);
    }
    public string Html()
    {
        string E(string text) => WebUtility.HtmlEncode(text);
        var html = new StringBuilder("<!doctype html><html><head><meta charset='utf-8'><style>@page{size:A4;margin:1.88cm 1.58cm 1.28cm 1.88cm}body{font-family:Arial,sans-serif;font-size:11pt;line-height:1.15;margin:0}h1{font-size:11pt}figure{margin:12pt 0;break-inside:avoid}img{display:block;width:100%;height:auto;max-height:210mm;object-fit:contain}figcaption{margin-top:6pt}p{margin:6pt 0}</style></head><body>");
        html.Append("<h1>Mcotis — Analysis results</h1><p>Model: ").Append(E(ModelName)).Append("</p>");
        for (int i = 0; i < Figures.Count; i++)
        {
            var figure = Figures[i];
            html.Append("<figure><img alt='").Append(E(figure.Caption(i + 1))).Append("' src='data:image/png;base64,").Append(Convert.ToBase64String(figure.Png)).Append("'><figcaption>").Append(E(figure.Caption(i + 1))).Append("</figcaption>");
            if (figure.Warning is { Length: > 0 }) html.Append("<p>Cable target not fitted: ").Append(E(figure.Warning)).Append("</p>");
            html.Append("</figure>");
        }
        return html.Append("</body></html>").ToString();
    }
    public void Export(string path, bool pdf)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!; Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".mcotis-results-{Guid.NewGuid():N}{(pdf ? ".html" : ".docx")}");
        try
        {
            if (pdf) { File.WriteAllText(temporary, Html(), new UTF8Encoding(false)); ReportPdfExporter.Export(temporary, path); }
            else { WriteWord(temporary); File.Move(temporary, path, overwrite: true); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private void WriteWord(string path)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart(); var body = new Body(); main.Document = new Document(body);
        main.AddNewPart<StyleDefinitionsPart>().Styles = new Styles(new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(new RunFonts { Ascii = "Arial", HighAnsi = "Arial", EastAsia = "Arial", ComplexScript = "Arial" }, new FontSize { Val = "22" }, new FontSizeComplexScript { Val = "22" })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { Line = "276", LineRule = LineSpacingRuleValues.Auto, After = "120" }))));
        body.Append(new Paragraph(new Run(new Text("Mcotis — Analysis results"))), new Paragraph(new Run(new Text("Model: " + ModelName))));
        for (int i = 0; i < Figures.Count; i++)
        {
            var figure = Figures[i]; var id = (uint)i + 1; var caption = figure.Caption(i + 1);
            var part = main.AddImagePart(ImagePartType.Png); using (var stream = new MemoryStream(figure.Png)) part.FeedData(stream);
            var png = figure.Png;
            long width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
            long height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
            if (width == 0 || height == 0) throw new InvalidOperationException("Invalid result image dimensions.");
            long cx = (PageWidth - Left - Right) * 635L, cy = cx * height / width;
            const long maxHeight = 7560000;
            if (cy > maxHeight) { cx = cx * maxHeight / cy; cy = maxHeight; }
            var graphic = new A.Graphic(new A.GraphicData(new PIC.Picture(
                new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = id, Name = caption, Description = caption }, new PIC.NonVisualPictureDrawingProperties()),
                new PIC.BlipFill(new A.Blip { Embed = main.GetIdOfPart(part) }, new A.Stretch(new A.FillRectangle())),
                new PIC.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }), new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }))) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" });
            body.Append(new Paragraph(new ParagraphProperties(new KeepNext()), new Run(new Drawing(new DW.Inline(
                new DW.Extent { Cx = cx, Cy = cy }, new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                new DW.DocProperties { Id = id, Name = caption, Description = caption }, new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }), graphic)
                { DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0 }))));
            var properties = new ParagraphProperties(); if (figure.Warning is { Length: > 0 }) properties.Append(new KeepNext());
            body.Append(new Paragraph(properties, new Run(new Text(caption))));
            if (figure.Warning is { Length: > 0 }) body.Append(new Paragraph(new Run(new Text("Cable target not fitted: " + figure.Warning))));
        }
        body.Append(new SectionProperties(new PageSize { Width = PageWidth, Height = PageHeight }, new PageMargin { Top = Top, Bottom = Bottom, Left = Left, Right = Right, Header = 0, Footer = 0, Gutter = 0 }));
        main.Document.Save();
    }
}