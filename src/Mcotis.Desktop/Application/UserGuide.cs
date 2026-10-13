using System.IO;
using System.Text.RegularExpressions;

namespace Mcotis.Desktop.Application;

/// <summary>A section of a User Guide article: its heading (empty for the lead) and its Markdown lines.</summary>
public sealed record UserGuideSection(string Heading, IReadOnlyList<string> Lines);

/// <summary>
/// An article of the User Guide: its title, category, where a user finds the function in the app, the article's lead
/// (first paragraph, also the search summary), its sections, and the articles it lists under See also.
/// </summary>
public sealed record UserGuideArticle(string Title, string Category, string Where, IReadOnlyList<string> SeeAlso, IReadOnlyList<UserGuideSection> Sections)
{
    public string Lead => string.Join(" ", Sections.FirstOrDefault(section => section.Heading.Length == 0)?.Lines.TakeWhile(line => line.Trim().Length > 0) ?? []);
    public string Text => string.Join("\n", Sections.SelectMany(section => section.Lines.Prepend(section.Heading)));

    /// <summary>Titles this article links to with [[Title]] or [[Title|text]], and under See also.</summary>
    public IEnumerable<string> Links => UserGuide.LinkPattern.Matches(Text).Select(match => match.Groups[1].Value.Trim()).Concat(SeeAlso).Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Help → User Guide: the encyclopedia of Mcotis's functions, docs/USER_GUIDE.md built into the app. Format: an article is
/// a <c># Title</c> line followed by <c>Category:</c>, <c>Where:</c> and optionally <c>See also:</c> (titles separated by
/// <c>;</c>) lines, a blank line, then Markdown — paragraphs, <c>## Section</c> headings, <c>- </c> bullets, <c>1. </c>
/// numbered steps, <c>**bold**</c>, <c>`code`</c> and <c>[[Article title]]</c> / <c>[[Article title|text]]</c> links. Text
/// before the first article is the main page. The order of first appearance sets the order of the categories.
/// </summary>
public static class UserGuide
{
    public const string ResourceName = "Mcotis.UserGuide.md";
    internal static readonly Regex LinkPattern = new(@"\[\[([^\]|]+)(?:\|([^\]]+))?\]\]", RegexOptions.Compiled);

    public sealed record Guide(IReadOnlyList<string> MainPage, IReadOnlyList<UserGuideArticle> Articles)
    {
        public IReadOnlyList<string> Categories => Articles.Select(article => article.Category).Distinct().ToArray();
        public UserGuideArticle? Find(string title) => Articles.FirstOrDefault(article => article.Title.Equals(title.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>Articles that link to <paramref name="title"/> (in their text or See also).</summary>
        public IReadOnlyList<UserGuideArticle> LinkedFrom(string title) =>
            Articles.Where(article => !article.Title.Equals(title, StringComparison.OrdinalIgnoreCase) &&
                                      article.Links.Any(link => link.Equals(title, StringComparison.OrdinalIgnoreCase))).ToArray();

        /// <summary>Articles matching every word of <paramref name="query"/>, ranked by how many of the words their title holds,
        /// then their menu path, then the guide's order.</summary>
        public IReadOnlyList<UserGuideArticle> Search(string query)
        {
            var words = query.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return Articles;
            bool All(string text) => words.All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
            int Count(string text) => words.Count(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
            var order = Articles.Select((article, index) => (article, index)).ToDictionary(pair => pair.article, pair => pair.index);
            return Articles.Where(article => All(article.Title + " " + article.Where + " " + article.Category + " " + article.Text))
                .OrderByDescending(article => Count(article.Title)).ThenByDescending(article => Count(article.Where)).ThenBy(article => order[article]).ToArray();
        }

        /// <summary>What is wrong with the guide: an article without a category, place or text, a duplicate title, a
        /// link or See also to an article that does not exist.</summary>
        public IReadOnlyList<string> Problems()
        {
            var problems = new List<string>();
            foreach (var group in Articles.GroupBy(article => article.Title, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
                problems.Add($"Two articles are called '{group.Key}'.");
            foreach (var article in Articles)
            {
                if (article.Category.Length == 0) problems.Add($"'{article.Title}' has no Category.");
                if (article.Where.Length == 0) problems.Add($"'{article.Title}' has no Where.");
                if (article.Lead.Length == 0) problems.Add($"'{article.Title}' has no lead paragraph.");
                foreach (var link in article.Links.Where(link => Find(link) is null)) problems.Add($"'{article.Title}' links to '{link}', which is not an article.");
            }
            foreach (Match match in LinkPattern.Matches(string.Join("\n", MainPage)))
                if (Find(match.Groups[1].Value) is null) problems.Add($"The main page links to '{match.Groups[1].Value}', which is not an article.");
            return problems;
        }
    }

    public static string Embedded()
    {
        using var stream = typeof(UserGuide).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The User Guide is not built into this copy of Mcotis.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static Guide Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var main = new List<string>();
        var articles = new List<UserGuideArticle>();
        string? title = null, category = "", where = "";
        var seeAlso = new List<string>();
        var sections = new List<UserGuideSection>();
        string heading = "";
        var body = new List<string>();
        var inHeader = false;

        void CloseSection()
        {
            while (body.Count > 0 && body[^1].Trim().Length == 0) body.RemoveAt(body.Count - 1);
            if (heading.Length > 0 || body.Count > 0) sections.Add(new UserGuideSection(heading, body.ToArray()));
            body.Clear();
        }
        void CloseArticle()
        {
            if (title is null) return;
            CloseSection();
            articles.Add(new UserGuideArticle(title, category ?? "", where ?? "", seeAlso.ToArray(), sections.ToArray()));
            sections.Clear();
            seeAlso.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                CloseArticle();
                title = line[2..].Trim();
                category = where = "";
                heading = "";
                inHeader = true;
                continue;
            }
            if (title is null) { main.Add(line); continue; }
            if (inHeader)
            {
                if (line.StartsWith("Category:", StringComparison.Ordinal)) { category = line["Category:".Length..].Trim(); continue; }
                if (line.StartsWith("Where:", StringComparison.Ordinal)) { where = line["Where:".Length..].Trim(); continue; }
                if (line.StartsWith("See also:", StringComparison.Ordinal))
                {
                    seeAlso.AddRange(line["See also:".Length..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    continue;
                }
                if (line.Length == 0) { inHeader = false; continue; }
                inHeader = false;
            }
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                CloseSection();
                heading = line[3..].Trim();
                continue;
            }
            body.Add(line);
        }
        CloseArticle();
        while (main.Count > 0 && main[^1].Trim().Length == 0) main.RemoveAt(main.Count - 1);
        return new Guide(main, articles);
    }
}
