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
        window.Close();
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
