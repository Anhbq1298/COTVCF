using System.Diagnostics;
using System.IO;

namespace COT_VCF.Infrastructure;

public sealed record ReportPdfResult(string PdfPath, string BrowserPath, TimeSpan Elapsed);

/// <summary>Prints a self-contained study report to PDF with the Chromium engine already on the machine
/// (Microsoft Edge, else Google Chrome) running headless. The report lays itself out for paper — sheet, margins,
/// header / footer with logo, page numbers — so the PDF is exactly what the browser's Print gives: text stays
/// text and inline-SVG charts stay vector. No SAP2000, no network, and the report file is only read.</summary>
public static class ReportPdfExporter
{
    /// <summary>Tells the report to lay itself out for printing as soon as it has loaded.</summary>
    public const string PrintFragment = "#print";

    /// <summary>Page-margin headers / footers need Chromium 131 or newer.</summary>
    public const int MinimumBrowserMajorVersion = 131;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

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
    public static IReadOnlyList<string> BuildArguments(string htmlPath, string pdfPath, string profileDirectory, string? options = null)
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
            // the report draws its own header and footer in the page margins
            "--no-pdf-header-footer",
            "--virtual-time-budget=10000",
            "--print-to-pdf=" + Path.GetFullPath(pdfPath),
            url,
        ];
    }

    /// <summary>Print <paramref name="htmlPath"/> to <paramref name="pdfPath"/> (default: the same name with
    /// .pdf beside the report). Throws with a message fit for the user when it cannot.</summary>
    public static ReportPdfResult Export(string htmlPath, string? pdfPath = null, string? options = null, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(htmlPath);
        var html = Path.GetFullPath(htmlPath);
        if (!File.Exists(html)) throw new FileNotFoundException("The report to print does not exist. Generate it first.", html);
        var pdf = Path.GetFullPath(string.IsNullOrWhiteSpace(pdfPath) ? Path.ChangeExtension(html, ".pdf") : pdfPath);

        var browser = FindBrowser()
            ?? throw new InvalidOperationException("Microsoft Edge or Google Chrome is needed to export a PDF, and neither was found. " +
                                                   "Open the report in a browser and use its Print button instead.");
        var major = FileVersionInfo.GetVersionInfo(browser).ProductMajorPart;
        if (major > 0 && major < MinimumBrowserMajorVersion)
            throw new InvalidOperationException($"{Path.GetFileName(browser)} {major} is too old to print page headers and footers " +
                                                $"(version {MinimumBrowserMajorVersion} or newer is needed). Update the browser and try again.");

        var directory = Path.GetDirectoryName(pdf)!;
        Directory.CreateDirectory(directory);
        var profile = Path.Combine(Path.GetTempPath(), "cotvcf-print-" + Guid.NewGuid().ToString("N"));
        var temporary = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(pdf)}.{Guid.NewGuid():N}.tmp.pdf");
        var clock = Stopwatch.StartNew();
        try
        {
            var start = new ProcessStartInfo(browser) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in BuildArguments(html, temporary, profile, options)) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException($"{Path.GetFileName(browser)} could not be started.");
            if (!process.WaitForExit((int)(timeout ?? DefaultTimeout).TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw new TimeoutException($"{Path.GetFileName(browser)} did not finish printing {Path.GetFileName(html)} in time.");
            }
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                throw new InvalidOperationException($"{Path.GetFileName(browser)} produced no PDF for {Path.GetFileName(html)} (exit code {process.ExitCode}).");

            try { File.Move(temporary, pdf, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"{Path.GetFileName(pdf)} could not be replaced — close it in your PDF viewer and export again.", ex);
            }
            return new ReportPdfResult(pdf, browser, clock.Elapsed);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { /* best effort */ }
            try { if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true); } catch { /* the browser may still be releasing it */ }
        }
    }
}
