using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Mcotis.Desktop.Application;

namespace Mcotis.Desktop.Presentation;

/// <summary>Help: the workflow overview, the Tab 07 FEM procedure documentation and the development plans (the
/// development log built into the app, <see cref="DevelopmentLog"/>).</summary>
public partial class HelpWindow : Window
{
    public enum Topic { Workflow, FemProcedure, DevelopmentPlans }

    public HelpWindow()
    {
        InitializeComponent();
        DevelopmentPlans.Document = DevelopmentPlansDocument();
    }

    public void Select(Topic topic) => Topics.SelectedIndex = (int)topic;

    /// <summary>The development log as a document: what is being built and planned first, then what is done, newest first.</summary>
    internal FlowDocument DevelopmentPlansDocument()
    {
        var document = new FlowDocument();
        Paragraph Styled(string key, string text) => new(new Run(text)) { Style = (Style)FindResource(key) };
        document.Blocks.Add(Styled("H1", "Development Plans"));
        IReadOnlyList<DevelopmentLogEntry> entries;
        try { entries = DevelopmentLog.Parse(DevelopmentLog.Embedded()); }
        catch (Exception ex)
        {
            document.Blocks.Add(new Paragraph(new Run("The development log could not be read: " + ex.Message)));
            return document;
        }
        int Count(DevelopmentStatus status) => entries.Count(entry => entry.Status == status);
        document.Blocks.Add(new Paragraph(new Run(
            $"{Count(DevelopmentStatus.InProgress)} in progress · {Count(DevelopmentStatus.Planned)} planned · {Count(DevelopmentStatus.Done)} done. " +
            "Every function added to Mcotis is recorded here by whoever builds it — compulsory for every team member and coding " +
            "agent (docs/DEVELOPMENT_LOG.md; a commit that changes the app without an entry is refused)."))
        { Foreground = (Brush)FindResource("Muted") });

        foreach (var (status, heading) in new[] { (DevelopmentStatus.InProgress, "In progress"), (DevelopmentStatus.Planned, "Planned"), (DevelopmentStatus.Done, "Done") })
        {
            var group = entries.Where(entry => entry.Status == status).ToArray();
            if (group.Length == 0) continue;
            document.Blocks.Add(Styled("H2", heading));
            foreach (var entry in group)
            {
                var title = new Paragraph { Margin = new Thickness(0, 8, 0, 2) };
                title.Inlines.Add(new Run(entry.Date.ToString("yyyy-MM-dd") + "  ") { Foreground = (Brush)FindResource("Muted") });
                title.Inlines.Add(new Bold(new Run(entry.Title)));
                document.Blocks.Add(title);
                var details = new Paragraph { Margin = new Thickness(14, 0, 0, 6) };
                void Line(string label, string value, bool last = false)
                {
                    details.Inlines.Add(new Run(label + "  ") { Foreground = (Brush)FindResource("Muted") });
                    details.Inlines.Add(new Run(value));
                    if (!last) details.Inlines.Add(new LineBreak());
                }
                Line("Where", entry.Where);
                Line("What", entry.What);
                Line("Who", entry.Who);
                Line("Checks", entry.Checks, last: true);
                document.Blocks.Add(details);
            }
        }
        return document;
    }
}
