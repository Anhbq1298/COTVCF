using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Presentation;

/// <summary>
/// Help → User Guide: docs/USER_GUIDE.md is well formed (every article with its category, place and lead; every link and
/// See also leads to an article), the app carries the current text, every menu command of the main window is covered,
/// search finds articles, and the window shows the main page, an article with its infobox and links, and navigates.
/// Run with <c>--user-guide</c>; part of the default suite.
/// </summary>
internal static class UserGuideChecks
{
    private const string GuidePath = "docs/USER_GUIDE.md";
    private const string MenuPath = "src/Mcotis.Desktop/Presentation/MainWindow.xaml";

    public static void Run()
    {
        var source = File.ReadAllText(GuidePath);
        var guide = UserGuide.Parse(source);
        var problems = guide.Problems();
        Check(problems.Count == 0 && guide.Articles.Count >= 50 && guide.Categories.Count >= 8 && guide.MainPage.Count > 0,
            $"the User Guide has {guide.Articles.Count} articles in {guide.Categories.Count} categories, each with its category, place and lead, " +
            "and every link leads to an article" + (problems.Count > 0 ? " — " + string.Join(" | ", problems.Take(5)) : ""));
        Check(UserGuide.Embedded().Replace("\r\n", "\n") == source.Replace("\r\n", "\n"), "the app carries the current User Guide (rebuild after editing docs/USER_GUIDE.md)");

        // every command of the main window's menu bar is named in the guide
        var xaml = File.ReadAllText(MenuPath);
        var start = xaml.IndexOf("<Menu x:Name=\"MainMenu\"", StringComparison.Ordinal);
        var menu = xaml[start..xaml.IndexOf("</Menu>", start, StringComparison.Ordinal)];
        static string Normal(string text) => Regex.Replace(text.Replace("_", "").Replace("…", ""), @"\s+", " ").Trim();
        var text = Normal(source).ToLowerInvariant();
        var headers = Regex.Matches(menu, "<MenuItem\\b[^>]*?Header=\"([^\"]*)\"").Select(match => Normal(match.Groups[1].Value)).Where(header => header.Length > 0).Distinct().ToArray();
        var missing = headers.Where(header => !text.Contains(header.ToLowerInvariant(), StringComparison.Ordinal)).ToArray();
        Check(headers.Length > 60 && missing.Length == 0,
            $"every one of the {headers.Length} menu commands is covered by the User Guide" + (missing.Length > 0 ? " — missing: " + string.Join(", ", missing) : ""));

        var delete = guide.Search("delete selected");
        var optimisation = guide.Search("cheapest column");
        Check(delete.FirstOrDefault()?.Title == "Delete objects" && optimisation.Any(article => article.Title == "Column design optimization") &&
              guide.LinkedFrom("Column design optimization").Any(article => article.Title == "Column design") && guide.Search("").Count == guide.Articles.Count,
            "search finds articles (title matches first) and each article knows the articles that link to it");

        // the window, with the app's theme as the app shows it
        if (System.Windows.Application.Current is null)
            new System.Windows.Application().Resources.MergedDictionaries.Add(
                new System.Windows.ResourceDictionary { Source = new Uri("/Mcotis;component/Presentation/Theme.xaml", UriKind.Relative) });
        var window = new UserGuideWindow();
        var main = new TextRange(window.Viewer.Document.ContentStart, window.Viewer.Document.ContentEnd).Text;
        window.Open("Column design optimization");
        var article = new TextRange(window.Viewer.Document.ContentStart, window.Viewer.Document.ContentEnd).Text;
        var links = Descendants(window.Viewer.Document).OfType<Hyperlink>().ToArray();
        Check(main.Contains("Mcotis User Guide", StringComparison.Ordinal) && main.Contains("Introduction to Mcotis", StringComparison.Ordinal) &&
              article.Contains("Column design optimization", StringComparison.Ordinal) && article.Contains("Design → any column design → Optimization tab", StringComparison.Ordinal) &&
              article.Contains("Contents", StringComparison.Ordinal) && article.Contains("See also", StringComparison.Ordinal) && article.Contains("Linked from", StringComparison.Ordinal) &&
              links.Length > 5 && window.BackButton.IsEnabled && window.Breadcrumb.Text.Contains("Design", StringComparison.Ordinal),
            $"the window shows the main page, then an article with its infobox, contents, See also, Linked from and {links.Length} links, and can go back");
        var column = guide.Find("Column design optimization")!;
        Check(column.Images.Count == 9 && column.Images.All(image => UserGuide.Image(image.File) is { Length: > 10_000 }) &&
              window.Viewer.Document.Blocks.OfType<BlockUIContainer>().Count() == 9 &&
              Enumerable.Range(1, 8).All(step => article.Contains($"Step {step}")) && article.Contains("illustrative forces") && article.Contains("Use Selected"),
            "Column design optimization shows eight steps and nine embedded screenshots covering all four types, rates, results and Use Selected");
        // screenshots: Optimization VCF shows the form's four sheets and the results' two
        var vcf = guide.Find("Optimization VCF")!;
        window.Open(vcf.Title);
        var vcfText = new TextRange(window.Viewer.Document.ContentStart, window.Viewer.Document.ContentEnd).Text;
        Check(Enumerable.Range(1, 8).All(step => vcfText.Contains($"Step {step}")) && vcfText.Contains("Troubleshooting") &&
              vcfText.Contains("× length") && vcfText.Contains("bottom-chord") && vcfText.Contains("27 cases"),
            "Optimization VCF explains all eight steps, fan rules, case counts, displacement checks and troubleshooting");
        var pictures = window.Viewer.Document.Blocks.OfType<BlockUIContainer>().Count();
        Check(vcf.Images.Count == 6 && vcf.Images.All(image => UserGuide.Image(image.File) is { Length: > 10_000 }) && pictures == 6,
            $"Optimization VCF shows its {vcf.Images.Count} screenshots (built into the app) in the window ({pictures} pictures)");
        var tas = guide.Find("Optimization TAS")!;
        window.Open(tas.Title);
        var tasText = new TextRange(window.Viewer.Document.ContentStart, window.Viewer.Document.ContentEnd).Text;
        Check(tas.Images.Count == 6 && tas.Images.All(image => UserGuide.Image(image.File) is { Length: > 10_000 }) &&
              window.Viewer.Document.Blocks.OfType<BlockUIContainer>().Count() == 6 &&
              Enumerable.Range(1, 8).All(step => tasText.Contains($"Step {step}")) && tasText.Contains("SLS1 G+Q full") && tasText.Contains("nine cases"),
            "Optimization TAS shows eight walkthrough steps and six embedded screenshots, including the optional cable and real Run/Chart results");
        window.Close();

        // Export to Word: A4, Arial 11, line spacing 1.15, with the screenshots
        var path = Path.Combine(Path.GetTempPath(), $"mcotis-guide-{Guid.NewGuid():N}.docx");
        try
        {
            UserGuideWord.Export(vcf, path);
            using var word = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(path, false);
            var part = word.MainDocumentPart!;
            var body = part.Document.Body!;
            var page = body.Elements<DocumentFormat.OpenXml.Wordprocessing.SectionProperties>().Single().GetFirstChild<DocumentFormat.OpenXml.Wordprocessing.PageSize>()!;
            var defaults = part.StyleDefinitionsPart!.Styles!.DocDefaults!;
            var font = defaults.RunPropertiesDefault!.RunPropertiesBaseStyle!.GetFirstChild<DocumentFormat.OpenXml.Wordprocessing.RunFonts>()!;
            var size = defaults.RunPropertiesDefault.RunPropertiesBaseStyle.GetFirstChild<DocumentFormat.OpenXml.Wordprocessing.FontSize>()!;
            var spacing = defaults.ParagraphPropertiesDefault!.ParagraphPropertiesBaseStyle!.GetFirstChild<DocumentFormat.OpenXml.Wordprocessing.SpacingBetweenLines>()!;
            var styleFonts = part.StyleDefinitionsPart.Styles.Elements<DocumentFormat.OpenXml.Wordprocessing.Style>()
                .Select(style => style.StyleRunProperties?.GetFirstChild<DocumentFormat.OpenXml.Wordprocessing.RunFonts>()?.Ascii?.Value).OfType<string>().Distinct().ToArray();
            var docText = body.InnerText;
            var schemaErrors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(word).Select(error => error.Description).Take(3).ToArray();
            Check(schemaErrors.Length == 0, "the exported Word document is valid Open XML (Word opens it without repair)" +
                (schemaErrors.Length > 0 ? " — " + string.Join(" | ", schemaErrors) : ""));
            Check(page.Width?.Value == 11906 && page.Height?.Value == 16838 && font.Ascii?.Value == "Arial" && styleFonts.SequenceEqual(["Arial"]) &&
                  size.Val?.Value == "22" && spacing.Line?.Value == "276" && spacing.LineRule?.Value == DocumentFormat.OpenXml.Wordprocessing.LineSpacingRuleValues.Auto &&
                  part.ImageParts.Count() == 6 && body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Drawing>().Count() == 6 &&
                  docText.Contains("Optimization VCF", StringComparison.Ordinal) && docText.Contains("Cable Force", StringComparison.Ordinal) &&
                  docText.Contains("Where: Optimization → Optimization VCF…", StringComparison.Ordinal) && !docText.Contains("[[", StringComparison.Ordinal) &&
                  body.Descendants<DocumentFormat.OpenXml.Wordprocessing.NumberingProperties>().Any(),
                $"Export to Word writes the article on A4 in Arial 11 with 1.15 line spacing, its 6 screenshots, headings, lists and plain-text links ({new FileInfo(path).Length / 1024} KB)");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        var tasPath = Path.Combine(Path.GetTempPath(), $"mcotis-tas-guide-{Guid.NewGuid():N}.docx");
        try
        {
            UserGuideWord.Export(tas, tasPath);
            using var word = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(tasPath, false);
            Check(!new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(word).Any() && word.MainDocumentPart!.ImageParts.Count() == 6 &&
                  word.MainDocumentPart.Document.InnerText.Contains("Step 8") && word.MainDocumentPart.Document.InnerText.Contains("27 combinations"),
                "Optimization TAS exports to valid Word with all six screenshots and the baseline/optional cable instructions");
        }
        finally { if (File.Exists(tasPath)) File.Delete(tasPath); }
        var columnPath = Path.Combine(Path.GetTempPath(), $"mcotis-column-guide-{Guid.NewGuid():N}.docx");
        try
        {
            UserGuideWord.Export(column, columnPath);
            using var word = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(columnPath, false);
            Check(!new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(word).Any() && word.MainDocumentPart!.ImageParts.Count() == 9 &&
                  word.MainDocumentPart.Document.InnerText.Contains("Step 8") && word.MainDocumentPart.Document.InnerText.Contains("250,000"),
                "Column design optimization exports to valid Word with nine screenshots and the detailed sizing workflow");
        }
        finally { if (File.Exists(columnPath)) File.Delete(columnPath); }
        // All article exports use the same page/style policy, not just the illustrated optimization articles.
        var formatPath = Path.Combine(Path.GetTempPath(), $"mcotis-guide-format-{Guid.NewGuid():N}.docx");
        try
        {
            foreach (var entry in guide.Articles)
            {
                UserGuideWord.Export(entry, formatPath);
                using var word = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(formatPath, false);
                var mainPart = word.MainDocumentPart!;
                var section = mainPart.Document.Body!.Elements<DocumentFormat.OpenXml.Wordprocessing.SectionProperties>().Single();
                var page = section.GetFirstChild<DocumentFormat.OpenXml.Wordprocessing.PageSize>()!;
                var margins = section.GetFirstChild<DocumentFormat.OpenXml.Wordprocessing.PageMargin>()!;
                var styles = mainPart.StyleDefinitionsPart!.Styles!;
                if (page.Width?.Value != 11906 || page.Height?.Value != 16838 ||
                    margins.Top?.Value != 1066 || margins.Bottom?.Value != 726 || margins.Left?.Value != 1066 || margins.Right?.Value != 896 ||
                    styles.Descendants<DocumentFormat.OpenXml.Wordprocessing.FontSize>().Any(size => size.Val?.Value != "22") ||
                    styles.Descendants<DocumentFormat.OpenXml.Wordprocessing.RunFonts>().Any(font => font.Ascii?.Value != "Arial") ||
                    mainPart.Document.Descendants<DocumentFormat.OpenXml.Wordprocessing.RunFonts>().Any(font => font.Ascii?.Value != "Arial") ||
                    styles.Descendants<DocumentFormat.OpenXml.Wordprocessing.SpacingBetweenLines>().Any(spacing => spacing.Line?.Value != "276"))
                    throw new Exception("Incorrect Word format for " + entry.Title);
                foreach (var image in mainPart.Document.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent>())
                    if (image.Cx?.Value > (11906 - 1066 - 896) * 635L) throw new Exception("Screenshot exceeds page width: " + entry.Title);
            }
            Check(true, $"All {guide.Articles.Count} guide articles export as A4, Arial 11 throughout, 1.15 spacing, requested margins and correctly sized screenshots");
        }
        finally { if (File.Exists(formatPath)) File.Delete(formatPath); }
    }

    private static IEnumerable<TextElement> Descendants(FlowDocument document)
    {
        var stack = new Stack<object>(document.Blocks.Cast<object>().Reverse());
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            if (item is TextElement element) yield return element;
            IEnumerable<object> children = item switch
            {
                Paragraph paragraph => paragraph.Inlines,
                Span span => span.Inlines,
                List list => list.ListItems,
                ListItem listItem => listItem.Blocks,
                Section section => section.Blocks,
                Floater floater => floater.Blocks,
                Table table => table.RowGroups.SelectMany(group => group.Rows).SelectMany(row => row.Cells).SelectMany(cell => cell.Blocks),
                _ => [],
            };
            foreach (var child in children.Reverse()) stack.Push(child);
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
