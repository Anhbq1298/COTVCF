using System.Diagnostics;
using System.IO;
using System.Text;
using COT_VCF.Application.Results;
using COT_VCF.Domain;

namespace COT_VCF.Infrastructure;

public sealed record ReportPdfResult(string PdfPath, string BrowserPath, TimeSpan Elapsed, int PageCount = 0);

/// <summary>Writes reports to PDF without a print dialog: runs the Chromium engine already on the machine
/// (Microsoft Edge, else Google Chrome) headless on the report, which lays itself out with its own print engine,
/// so the PDF holds the same sheets as the report's Print Report preview. Used by <b>Print All Reports</b>
/// (<see cref="ExportPackage"/> — every report of a folder as one document) and by the checks. No SAP2000, no
/// network, and the report files are only read.</summary>
public static class ReportPdfExporter
{
    /// <summary>Tells the report to lay itself out for printing as soon as it has loaded.</summary>
    public const string PrintFragment = "#print";

    public const int MinimumBrowserMajorVersion = 109;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    /// <summary>A package loads every report of a study; a large study takes its time.</summary>
    public static readonly TimeSpan PackageTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Edge first (part of Windows), then Chrome. Null when neither is installed.</summary>
    public static string? FindBrowser()
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ];
        string[] relative = [@"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe"];
        return relative.SelectMany(rel => roots.Where(root => !string.IsNullOrEmpty(root)).Select(root => Path.Combine(root, rel)))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>The headless command line. <paramref name="options"/> are extra report options appended to the
    /// print fragment, e.g. <c>paper=A3&amp;mode=ut</c>.</summary>
    public static IReadOnlyList<string> BuildArguments(string htmlPath, string pdfPath, string profileDirectory, string? options = null,
        int virtualTimeBudgetMs = 10000)
    {
        var url = new Uri(Path.GetFullPath(htmlPath)).AbsoluteUri + PrintFragment
                  + (string.IsNullOrWhiteSpace(options) ? "" : "&" + options.Trim().TrimStart('#', '&'));
        return
        [
            "--headless=new",
            "--disable-gpu",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-extensions",
            // a private profile: never attaches to (or is swallowed by) a browser window the user has open
            "--user-data-dir=" + profileDirectory,
            // the report's sheets carry their own header and footer
            "--no-pdf-header-footer",
            "--virtual-time-budget=" + virtualTimeBudgetMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--print-to-pdf=" + Path.GetFullPath(pdfPath),
            url,
        ];
    }

    /// <summary>Print <paramref name="htmlPath"/> to <paramref name="pdfPath"/> (default: the same name with
    /// .pdf beside the report). Throws with a message fit for the user when it cannot.</summary>
    public static ReportPdfResult Export(string htmlPath, string? pdfPath = null, string? options = null, TimeSpan? timeout = null,
        int virtualTimeBudgetMs = 10000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(htmlPath);
        var html = Path.GetFullPath(htmlPath);
        if (!File.Exists(html)) throw new FileNotFoundException("The report to print does not exist. Generate it first.", html);
        var pdf = Path.GetFullPath(string.IsNullOrWhiteSpace(pdfPath) ? Path.ChangeExtension(html, ".pdf") : pdfPath);

        var browser = FindBrowser()
            ?? throw new InvalidOperationException("Microsoft Edge or Google Chrome is needed to write a PDF, and neither was found. " +
                                                   "Open the report in a browser and use its Print Report button instead.");
        var major = FileVersionInfo.GetVersionInfo(browser).ProductMajorPart;
        if (major > 0 && major < MinimumBrowserMajorVersion)
            throw new InvalidOperationException($"{Path.GetFileName(browser)} {major} is too old to write the PDF " +
                                                $"(version {MinimumBrowserMajorVersion} or newer is needed). Update the browser and try again.");

        var directory = Path.GetDirectoryName(pdf)!;
        Directory.CreateDirectory(directory);
        var profile = Path.Combine(Path.GetTempPath(), "cotvcf-print-" + Guid.NewGuid().ToString("N"));
        var temporary = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(pdf)}.{Guid.NewGuid():N}.tmp.pdf");
        var clock = Stopwatch.StartNew();
        try
        {
            var start = new ProcessStartInfo(browser) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in BuildArguments(html, temporary, profile, options, virtualTimeBudgetMs)) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException($"{Path.GetFileName(browser)} could not be started.");
            if (!process.WaitForExit((int)(timeout ?? DefaultTimeout).TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw new TimeoutException($"{Path.GetFileName(browser)} did not finish printing {Path.GetFileName(html)} in time.");
            }
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                throw new InvalidOperationException($"{Path.GetFileName(browser)} produced no PDF for {Path.GetFileName(html)} (exit code {process.ExitCode}).");

            var pages = CountPages(temporary);
            try { File.Move(temporary, pdf, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"{Path.GetFileName(pdf)} could not be replaced — close it in your PDF viewer and print again.", ex);
            }
            return new ReportPdfResult(pdf, browser, clock.Elapsed, pages);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { /* best effort */ }
            try { if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true); } catch { /* the browser may still be releasing it */ }
        }
    }

    /// <summary>Every report of <paramref name="folder"/> as one PDF at <paramref name="pdfPath"/>, printed with
    /// <paramref name="settings"/> as they are now and numbered through. Throws with a message fit for the user
    /// when there is nothing to print or a report predates the package (it must be regenerated).</summary>
    public static ReportPdfResult ExportPackage(string folder, string pdfPath, ReportPrintSettings? settings, string title, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        var items = StudyReportPackage.Items(folder);
        if (items.Count == 0) throw new InvalidOperationException("There is no report to print yet. Generate the outputs first.");
        var old = items.Select(item => item.FileName).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !ReportPrintEngine.SupportsPackage(Path.Combine(folder, name))).ToArray();
        if (old.Length > 0)
            throw new InvalidOperationException($"{string.Join(", ", old)} {(old.Length == 1 ? "was" : "were")} generated by an earlier version " +
                                                "and cannot be printed together. Regenerate the reports, then print again.");

        var page = Path.Combine(Path.GetTempPath(), $"cotvcf-package-{Guid.NewGuid():N}.html");
        try
        {
            File.WriteAllText(page, StudyReportPackage.Html(folder, items, settings, title), new UTF8Encoding(false));
            return Export(page, pdfPath, timeout: timeout ?? PackageTimeout, virtualTimeBudgetMs: 30000);
        }
        finally
        {
            try { if (File.Exists(page)) File.Delete(page); } catch { /* best effort */ }
        }
    }

    /// <summary>Pages of a PDF as this exporter's browser writes it (page objects are not compressed); 0 when it
    /// cannot be told.</summary>
    public static int CountPages(string pdfPath)
    {
        try
        {
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(pdfPath));
            return System.Text.RegularExpressions.Regex.Matches(text, @"/Type\s*/Page(?![a-zA-Z])").Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
