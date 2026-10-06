using System.Diagnostics;
using System.IO;

namespace COT_VCF.Infrastructure;

public sealed record ReportPdfResult(string PdfPath, string BrowserPath, TimeSpan Elapsed);

/// <summary>The checks' real browser: runs the Chromium engine already on the machine (Microsoft Edge, else Google
/// Chrome) headless to print a report to PDF, or to run a script in it and read the page back. The application
/// itself exports nothing — a report is printed from its own Print Report screen — so this lives with the checks,
/// as the one place the reports' scripts and the composed sheets meet a real layout engine. No SAP2000, no
/// network, and the report file is only read.</summary>
public static class ReportPdfExporter
{
    /// <summary>Tells the report to lay itself out for printing as soon as it has loaded.</summary>
    public const string PrintFragment = "#print";

    public const int MinimumBrowserMajorVersion = 109;

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
            // the report's sheets carry their own header and footer
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
            throw new InvalidOperationException($"{Path.GetFileName(browser)} {major} is too old for the headless print check " +
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

    /// <summary>Opens a copy of the report with <paramref name="script"/> appended, lets it run, and returns the
    /// lines the script logged with <c>log(key, value)</c>. The report file itself is not changed.</summary>
    public static IReadOnlyDictionary<string, string> RunScript(string htmlPath, string script, string? fragment = null, TimeSpan? timeout = null)
    {
        var html = Path.GetFullPath(htmlPath);
        var browser = FindBrowser() ?? throw new InvalidOperationException("Microsoft Edge or Google Chrome is needed for the browser checks.");
        var source = File.ReadAllText(html);
        var copy = Path.Combine(Path.GetDirectoryName(html)!, $".{Path.GetFileNameWithoutExtension(html)}.{Guid.NewGuid():N}.check.html");
        var profile = Path.Combine(Path.GetTempPath(), "cotvcf-check-" + Guid.NewGuid().ToString("N"));
        // the script runs inside this frame; what it logs comes back as "key<TAB>value" lines in a <pre>
        const string harness = "(()=>{const out=[],log=(k,v)=>out.push(k+'\\t'+String(v).replace(/[\\r\\n]+/g,' '));try{\n";
        const string report = "\n}catch(e){log('ERROR',e.message+' @ '+String(e.stack||'').split('\\n')[1])}" +
                              "const pre=document.createElement('pre');pre.id='__check';pre.textContent=out.join('\\n');document.body.appendChild(pre)})();";
        try
        {
            var at = source.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(copy, source[..at] + "<script>" + harness + script + report + "</script>" + source[at..]);
            var start = new ProcessStartInfo(browser) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
            foreach (var argument in new[] { "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions",
                         "--window-size=1500,950", "--user-data-dir=" + profile, "--virtual-time-budget=10000", "--dump-dom",
                         new Uri(copy).AbsoluteUri + (string.IsNullOrEmpty(fragment) ? "" : "#" + fragment.TrimStart('#')) })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"{Path.GetFileName(browser)} could not be started.");
            var read = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit((int)(timeout ?? DefaultTimeout).TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw new TimeoutException($"{Path.GetFileName(browser)} did not finish running the check in {Path.GetFileName(html)} in time.");
            }
            var dom = read.GetAwaiter().GetResult();
            var open = dom.LastIndexOf("<pre id=\"__check\">", StringComparison.Ordinal);
            if (open < 0) throw new InvalidOperationException($"The check script produced no result in {Path.GetFileName(html)}.");
            var text = System.Net.WebUtility.HtmlDecode(dom[(open + 18)..dom.IndexOf("</pre>", open, StringComparison.Ordinal)]);
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r').Split('\t', 2))
                .ToDictionary(pair => pair[0], pair => pair.Length > 1 ? pair[1] : "", StringComparer.Ordinal);
        }
        finally
        {
            try { if (File.Exists(copy)) File.Delete(copy); } catch { /* best effort */ }
            try { if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true); } catch { /* the browser may still be releasing it */ }
        }
    }
}
