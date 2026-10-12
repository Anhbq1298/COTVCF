using System.IO;
using System.Windows;
using System.Windows.Documents;
using COT_VCF.Application;
using COT_VCF.Presentation;

/// <summary>
/// The development log (<c>docs/DEVELOPMENT_LOG.md</c>, Help → Development Plans), compulsory for every change of the app:
/// every entry has the required fields in the agreed format, newest first; the app carries the current file; a broken entry
/// is caught; the Help page lists every entry; the commit hook that enforces it is in the repository.
/// </summary>
internal static class DevelopmentLogChecks
{
    private const string Log = "docs/DEVELOPMENT_LOG.md";

    public static void Run()
    {
        var text = File.ReadAllText(Log);
        var problems = DevelopmentLog.Problems(text, DateOnly.FromDateTime(DateTime.Today));
        Check(problems.Count == 0, "every development log entry has its date, title, Status, Who, Where, What and Checks, newest first" +
            string.Concat(problems.Select(problem => Environment.NewLine + "  - " + problem)));
        var entries = DevelopmentLog.Parse(text);
        Check(entries.Count > 0 && entries.Any(entry => entry.Title == "Export to ETABS" && entry.Status == DevelopmentStatus.Done) &&
              entries.Any(entry => entry.Status == DevelopmentStatus.Planned),
            $"the log records done and planned work ({entries.Count} entries)");
        Check(DevelopmentLog.Embedded().Replace("\r\n", "\n") == text.Replace("\r\n", "\n"),
            "the app carries the current log (rebuild after editing docs/DEVELOPMENT_LOG.md)");

        // a broken entry is caught: a missing field, a wrong status, an older entry above a newer one, a stray line
        var broken = "## 2026-10-01 · Old\n- Status: Done\n- Who: A\n- Where: B\n- What: C\n- Checks: none yet\n" +
                     "## 2026-10-05 · Newer below\n- Status: Finished\n- Who: A\n- What: C\n- Checks: none yet\nloose text\n";
        var found = DevelopmentLog.Problems(broken, new DateOnly(2026, 10, 12));
        Check(found.Any(problem => problem.Contains("'- Where:' is missing")) && found.Any(problem => problem.Contains("Status must be")) &&
              found.Any(problem => problem.Contains("newest first")) && found.Any(problem => problem.Contains("not a '- Field: value' line")),
            "a broken entry is refused with the reason (missing field, unknown status, order, stray line)");

        // the Help page shows every entry (with the app's theme, as the app shows it)
        if (System.Windows.Application.Current is null)
            new System.Windows.Application().Resources.MergedDictionaries.Add(
                new ResourceDictionary { Source = new Uri("/Mcotis;component/Presentation/Theme.xaml", UriKind.Relative) });
        var help = new HelpWindow();
        var document = help.DevelopmentPlansDocument();
        var shown = new TextRange(document.ContentStart, document.ContentEnd).Text;
        Check(entries.All(entry => shown.Contains(entry.Title, StringComparison.Ordinal)) && shown.Contains("Development Plans"),
            "Help → Development Plans lists every entry");
        help.Close();

        Check(File.Exists(".githooks/commit-msg") && File.ReadAllText(".githooks/commit-msg").Contains("docs/DEVELOPMENT_LOG.md") &&
              File.ReadAllText("src/Mcotis.Desktop/Mcotis.Desktop.csproj").Contains("core.hooksPath .githooks"),
            "the commit hook that refuses an app change without a log entry is in the repository, and the build installs it");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
