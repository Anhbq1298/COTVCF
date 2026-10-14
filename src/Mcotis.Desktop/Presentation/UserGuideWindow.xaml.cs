using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Mcotis.Desktop.Application;

namespace Mcotis.Desktop.Presentation;

/// <summary>
/// Help → User Guide (F1): an encyclopedia of Mcotis's functions, like a wiki. The contents list the articles by category
/// and filter as you search; an article shows its title, an infobox (category and where to find the function), its lead,
/// a contents box, its sections, See also and the articles that link to it. Links between articles navigate; Back /
/// Forward / Main page as in a browser. The text is docs/USER_GUIDE.md built into the app (<see cref="UserGuide"/>).
/// </summary>
public partial class UserGuideWindow : Window
{
    private static readonly Regex Inline = new(@"\*\*(.+?)\*\*|`([^`]+)`|\[\[([^\]|]+)(?:\|([^\]]+))?\]\]", RegexOptions.Compiled);
    private readonly UserGuide.Guide guide;
    private readonly List<string?> history = [];     // null = the main page; "?query" = search results
    private int position = -1;
    private bool fillingContents;

    public UserGuideWindow()
    {
        InitializeComponent();
        try { guide = UserGuide.Parse(UserGuide.Embedded()); }
        catch (Exception ex) { guide = new UserGuide.Guide([$"The User Guide could not be read: {ex.Message}"], []); }
        FillContents(guide.Articles);
        SearchCount.Text = $"{guide.Articles.Count} articles in {guide.Categories.Count} categories";
        Navigate(null);
    }

    /// <summary>Shows the article called <paramref name="title"/> (null: the main page).</summary>
    public void Open(string? title) => Navigate(title is not null && guide.Find(title) is { } article ? article.Title : null);

    // ---------------- navigation ----------------

    private void Navigate(string? target, bool record = true)
    {
        if (record)
        {
            if (position < history.Count - 1) history.RemoveRange(position + 1, history.Count - position - 1);
            if (position < 0 || history[position] != target) { history.Add(target); position = history.Count - 1; }
        }
        Show(target);
        BackButton.IsEnabled = position > 0;
        ForwardButton.IsEnabled = position < history.Count - 1;
    }

    private void Show(string? target)
    {
        shownArticle = null;
        if (target is null) { Viewer.Document = MainPage(); Breadcrumb.Text = "Main page"; SelectInContents(null); }
        else if (target.StartsWith('?')) { Viewer.Document = SearchPage(target[1..]); Breadcrumb.Text = $"Search: {target[1..]}"; }
        else if (guide.Find(target) is { } article)
        {
            shownArticle = article;
            Viewer.Document = ArticlePage(article);
            Breadcrumb.Text = $"{article.Category}  ›  {article.Title}";
            SelectInContents(article.Title);
        }
        ExportButton.IsEnabled = shownArticle is not null;
        Viewer.Document?.BringIntoView();
    }

    private UserGuideArticle? shownArticle;

    /// <summary>Export to Word…: the shown article as a .docx (A4, Arial 11, line spacing 1.15, with its screenshots).</summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (shownArticle is not { } article) return;
        var name = string.Concat(("Mcotis User Guide - " + article.Title).Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character));
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Export to Word", Filter = "Word document (*.docx)|*.docx", FileName = name + ".docx", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            UserGuideWord.Export(article, dialog.FileName);
            if (MessageBox.Show(this, $"Saved “{article.Title}” to\n{dialog.FileName}\n\nOpen it now?", "Export to Word", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "The document could not be saved: " + ex.Message + "\n\nIs it open in Word? Close it, or choose another name.", "Export to Word",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) { if (position > 0) { position--; Navigate(history[position], record: false); } }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (position < history.Count - 1) { position++; Navigate(history[position], record: false); } }
    private void Home_Click(object sender, RoutedEventArgs e) => Navigate(null);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Back && Keyboard.FocusedElement is not TextBox && position > 0) { Back_Click(this, e); e.Handled = true; return; }
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }

    // ---------------- contents and search ----------------

    private void FillContents(IReadOnlyList<UserGuideArticle> articles)
    {
        fillingContents = true;
        Contents.Items.Clear();
        var home = new TreeViewItem { Header = "Main page", FontWeight = FontWeights.SemiBold, Tag = "" };
        Contents.Items.Add(home);
        foreach (var category in guide.Categories)
        {
            var inCategory = articles.Where(article => article.Category == category).ToArray();
            if (inCategory.Length == 0) continue;
            var node = new TreeViewItem { Header = $"{category} ({inCategory.Length})", IsExpanded = true, FontWeight = FontWeights.SemiBold };
            foreach (var article in inCategory)
                node.Items.Add(new TreeViewItem { Header = article.Title, Tag = article.Title, FontWeight = FontWeights.Normal, ToolTip = article.Where });
            Contents.Items.Add(node);
        }
        fillingContents = false;
    }

    private void SelectInContents(string? title)
    {
        fillingContents = true;
        foreach (TreeViewItem top in Contents.Items)
        {
            if (title is null && Equals(top.Tag, "")) top.IsSelected = true;
            foreach (TreeViewItem item in top.Items)
                if (Equals(item.Tag, title)) { item.IsSelected = true; item.BringIntoView(); }
        }
        fillingContents = false;
    }

    private void Contents_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (fillingContents || e.NewValue is not TreeViewItem { Tag: string tag }) return;
        Navigate(tag.Length == 0 ? null : tag);
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        var found = query.Length == 0 ? guide.Articles : guide.Search(query);
        FillContents(found);
        SearchCount.Text = query.Length == 0 ? $"{guide.Articles.Count} articles in {guide.Categories.Count} categories" : $"{found.Count} article(s) match";
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) return;
        var found = guide.Search(query);
        Navigate(found.Count == 1 ? found[0].Title : "?" + query);
    }

    // ---------------- pages ----------------

    private FlowDocument NewDocument() => new()
    {
        FontFamily = new FontFamily("Segoe UI"), FontSize = 13, PagePadding = new Thickness(28, 20, 28, 28), ColumnWidth = 10_000,
        Background = (Brush)FindResource("Raised"), Foreground = (Brush)FindResource("Text"),
    };

    private static Paragraph Title(string text) => new(new Run(text)) { FontSize = 26, FontFamily = new FontFamily("Georgia"), Margin = new Thickness(0, 0, 0, 2) };

    private Paragraph Rule() => new() { BorderBrush = (Brush)FindResource("Border"), BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 10), FontSize = 2 };

    private Paragraph Heading(string text) => new(new Run(text))
    {
        FontSize = 18, FontFamily = new FontFamily("Georgia"), Margin = new Thickness(0, 16, 0, 6),
        BorderBrush = (Brush)FindResource("Border"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 2),
    };

    private FlowDocument MainPage()
    {
        var document = NewDocument();
        document.Blocks.Add(Title("Mcotis User Guide"));
        document.Blocks.Add(Rule());
        foreach (var block in Blocks(guide.MainPage)) document.Blocks.Add(block);
        foreach (var category in guide.Categories)
        {
            document.Blocks.Add(Heading(category));
            var list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(18, 0, 0, 4) };
            foreach (var article in guide.Articles.Where(article => article.Category == category))
            {
                var item = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                item.Inlines.Add(Link(article.Title, article.Title));
                item.Inlines.Add(new Run(" — " + Plain(article.Lead, 140)) { Foreground = (Brush)FindResource("Muted") });
                list.ListItems.Add(new ListItem(item));
            }
            document.Blocks.Add(list);
        }
        return document;
    }

    private FlowDocument SearchPage(string query)
    {
        var document = NewDocument();
        var found = guide.Search(query);
        document.Blocks.Add(Title("Search results"));
        document.Blocks.Add(new Paragraph(new Run($"{found.Count} article(s) contain “{query}”.")) { Foreground = (Brush)FindResource("Muted") });
        document.Blocks.Add(Rule());
        foreach (var article in found)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
            paragraph.Inlines.Add(new Bold(Link(article.Title, article.Title)) { FontSize = 15 });
            paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run(article.Category + "  ·  " + article.Where) { Foreground = (Brush)FindResource("Success"), FontSize = 11.5 });
            paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run(Plain(article.Lead, 260)));
            document.Blocks.Add(paragraph);
        }
        return document;
    }

    private FlowDocument ArticlePage(UserGuideArticle article)
    {
        var document = NewDocument();
        document.Blocks.Add(Title(article.Title));
        document.Blocks.Add(new Paragraph(new Run("From the Mcotis User Guide  ·  " + article.Category)) { Foreground = (Brush)FindResource("Muted"), FontSize = 11.5, Margin = new Thickness(0, 0, 0, 4) });
        document.Blocks.Add(Rule());

        // infobox: category and where the function is, floated right as on a wiki
        var table = new Table { CellSpacing = 0, FontSize = 12 };
        table.Columns.Add(new TableColumn { Width = new GridLength(78) });
        table.Columns.Add(new TableColumn { Width = new GridLength(190) });
        var rows = new TableRowGroup();
        var header = new TableRow { Background = (Brush)FindResource("Selected") };
        header.Cells.Add(new TableCell(new Paragraph(new Bold(new Run(article.Title))) { TextAlignment = TextAlignment.Center, Margin = new Thickness(4) }) { ColumnSpan = 2 });
        rows.Rows.Add(header);
        void Row(string label, string value)
        {
            var row = new TableRow();
            row.Cells.Add(new TableCell(new Paragraph(new Bold(new Run(label))) { Margin = new Thickness(6, 4, 4, 4) }));
            var cell = new Paragraph { Margin = new Thickness(4) };
            foreach (var inline in Inlines(value)) cell.Inlines.Add(inline);
            row.Cells.Add(new TableCell(cell));
            rows.Rows.Add(row);
        }
        Row("Category", article.Category);
        Row("Where", article.Where);
        if (article.SeeAlso.Count > 0) Row("Related", string.Join(", ", article.SeeAlso.Select(title => $"[[{title}]]")));
        table.RowGroups.Add(rows);
        var box = new Floater(table)
        {
            HorizontalAlignment = HorizontalAlignment.Right, Width = 280, Padding = new Thickness(0), Margin = new Thickness(14, 0, 0, 10),
            BorderBrush = (Brush)FindResource("Border"), BorderThickness = new Thickness(1), Background = (Brush)FindResource("Surface"),
        };
        var lead = article.Sections.FirstOrDefault(section => section.Heading.Length == 0);
        var leadBlocks = lead is null ? [] : Blocks(lead.Lines).ToList();
        if (leadBlocks.FirstOrDefault() is Paragraph first) first.Inlines.InsertBefore(first.Inlines.FirstInline, box);
        else document.Blocks.Add(new Paragraph(box));
        foreach (var block in leadBlocks) document.Blocks.Add(block);

        // contents box when there are sections
        var sections = article.Sections.Where(section => section.Heading.Length > 0).ToArray();
        var anchors = new Dictionary<string, Paragraph>();
        if (sections.Length >= 2)
        {
            var contents = new Section
            {
                BorderBrush = (Brush)FindResource("Border"), BorderThickness = new Thickness(1), Background = (Brush)FindResource("Surface"),
                Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 6, 0, 6),
            };
            contents.Blocks.Add(new Paragraph(new Bold(new Run("Contents"))) { Margin = new Thickness(0, 0, 0, 2) });
            for (var index = 0; index < sections.Length; index++)
            {
                var heading = sections[index].Heading;
                var link = new Hyperlink(new Run($"{index + 1}  {heading}")) { TextDecorations = null };
                link.Click += (_, _) => { if (anchors.TryGetValue(heading, out var anchor)) anchor.BringIntoView(); };
                contents.Blocks.Add(new Paragraph(link) { Margin = new Thickness(8, 0, 0, 0) });
            }
            document.Blocks.Add(contents);
        }
        foreach (var section in sections)
        {
            var heading = Heading(section.Heading);
            anchors[section.Heading] = heading;
            document.Blocks.Add(heading);
            foreach (var block in Blocks(section.Lines)) document.Blocks.Add(block);
        }

        if (article.SeeAlso.Count > 0)
        {
            document.Blocks.Add(Heading("See also"));
            var list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(18, 0, 0, 4) };
            foreach (var title in article.SeeAlso) list.ListItems.Add(new ListItem(new Paragraph(Link(title, title)) { Margin = new Thickness(0, 1, 0, 1) }));
            document.Blocks.Add(list);
        }
        var linkedFrom = guide.LinkedFrom(article.Title);
        if (linkedFrom.Count > 0)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 18, 0, 0), FontSize = 11.5, Foreground = (Brush)FindResource("Muted"), Padding = new Thickness(0, 6, 0, 0),
                BorderBrush = (Brush)FindResource("Border"), BorderThickness = new Thickness(0, 1, 0, 0) };
            paragraph.Inlines.Add(new Run("Linked from: "));
            for (var index = 0; index < linkedFrom.Count; index++)
            {
                if (index > 0) paragraph.Inlines.Add(new Run(" · "));
                paragraph.Inlines.Add(Link(linkedFrom[index].Title, linkedFrom[index].Title));
            }
            document.Blocks.Add(paragraph);
        }
        return document;
    }

    // ---------------- Markdown subset ----------------

    /// <summary>Paragraphs, "- " bullet lists and "1. " numbered lists of <paramref name="lines"/>.</summary>
    private IEnumerable<Block> Blocks(IReadOnlyList<string> lines)
    {
        var paragraph = new List<string>();
        List? list = null;
        var numbered = false;
        Paragraph Para(string text)
        {
            var result = new Paragraph { Margin = new Thickness(0, 0, 0, 8), LineHeight = 20 };
            foreach (var inline in Inlines(text)) result.Inlines.Add(inline);
            return result;
        }
        IEnumerable<Block> Flush()
        {
            if (paragraph.Count > 0) { yield return Para(string.Join(" ", paragraph)); paragraph.Clear(); }
            if (list is not null) { yield return list; list = null; }
        }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            var bullet = line.StartsWith("- ", StringComparison.Ordinal);
            var number = Regex.Match(line, @"^\d+\.\s+(.*)$");
            if (line.Length == 0) { foreach (var block in Flush()) yield return block; continue; }
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                foreach (var block in Flush()) yield return block;
                yield return new Paragraph(new Run(line[4..].Trim())) { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) };
                continue;
            }
            if (UserGuide.ImagePattern.Match(line) is { Success: true } image)
            {
                foreach (var block in Flush()) yield return block;
                foreach (var block in Screenshot(image.Groups[2].Value.Trim(), image.Groups[1].Value.Trim())) yield return block;
                continue;
            }
            if (bullet || number.Success)
            {
                if (paragraph.Count > 0) { yield return Para(string.Join(" ", paragraph)); paragraph.Clear(); }
                if (list is null || numbered != number.Success)
                {
                    if (list is not null) yield return list;
                    numbered = number.Success;
                    list = new List { MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(20, 0, 0, 8), Padding = new Thickness(14, 0, 0, 0) };
                }
                var item = Para(bullet ? line[2..] : number.Groups[1].Value);
                item.Margin = new Thickness(0, 1, 0, 2);
                list.ListItems.Add(new ListItem(item));
                continue;
            }
            if (list is not null) { yield return list; list = null; }
            paragraph.Add(line);
        }
        foreach (var block in Flush()) yield return block;
    }

    /// <summary>A screenshot at most its own size and the page's width, framed, with its caption below.</summary>
    private IEnumerable<Block> Screenshot(string file, string caption)
    {
        if (UserGuide.Image(file) is not { } bytes)
        {
            yield return new Paragraph(new Run($"[missing screenshot: {file}]")) { Foreground = (Brush)FindResource("Error") };
            yield break;
        }
        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.EndInit();
        bitmap.Freeze();
        var picture = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxWidth = bitmap.PixelWidth, HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(picture, caption);
        yield return new BlockUIContainer(new Border
        {
            Child = picture, BorderBrush = (Brush)FindResource("Border"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0), ToolTip = caption,
        });
        if (caption.Length > 0)
            yield return new Paragraph(new Run(caption)) { FontSize = 11.5, FontStyle = FontStyles.Italic, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 4, 0, 14) };
    }

    /// <summary>**bold**, `code` and [[links]] of a line.</summary>
    private IEnumerable<Inline> Inlines(string text)
    {
        var index = 0;
        foreach (Match match in Inline.Matches(text))
        {
            if (match.Index > index) yield return new Run(text[index..match.Index]);
            if (match.Groups[1].Success) yield return new Bold(new Run(match.Groups[1].Value));
            else if (match.Groups[2].Success)
                yield return new Run(match.Groups[2].Value) { FontFamily = (FontFamily)FindResource("MonoFont"), Background = (Brush)FindResource("Surface") };
            else
            {
                var target = match.Groups[3].Value.Trim();
                yield return Link(target, match.Groups[4].Success ? match.Groups[4].Value : target);
            }
            index = match.Index + match.Length;
        }
        if (index < text.Length) yield return new Run(text[index..]);
    }

    private Inline Link(string target, string text)
    {
        if (guide.Find(target) is null) return new Run(text) { Foreground = (Brush)FindResource("Error") };   // a red link: no such article
        var link = new Hyperlink(new Run(text)) { Foreground = (Brush)FindResource("Accent"), TextDecorations = null, ToolTip = target };
        link.Click += (_, _) => Navigate(guide.Find(target)!.Title);
        link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
        link.MouseLeave += (_, _) => link.TextDecorations = null;
        return link;
    }

    /// <summary>The text of a Markdown line without its markup, cut to <paramref name="length"/>.</summary>
    private static string Plain(string text, int length)
    {
        var plain = Inline.Replace(text, match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value
            : match.Groups[4].Success ? match.Groups[4].Value : match.Groups[3].Value);
        return plain.Length <= length ? plain : plain[..(length - 1)].TrimEnd() + "…";
    }
}
