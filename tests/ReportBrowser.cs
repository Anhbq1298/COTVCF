using System.Diagnostics;
using System.IO;
using COT_VCF.Infrastructure;

/// <summary>The checks' real browser for what Jint cannot do — layout. Runs a script inside a report in the
/// installed headless Edge / Chrome (the one <see cref="ReportPdfExporter"/> prints with) and reads values back.</summary>
static class ReportBrowser
{
    /// <summary>Opens a copy of the report with <paramref name="script"/> appended, lets it run, and returns the
    /// lines the script logged with <c>log(key, value)</c>. The report file itself is not changed.</summary>
    public static IReadOnlyDictionary<string, string> RunScript(string htmlPath, string script, string? fragment = null, TimeSpan? timeout = null)
    {
        var html = Path.GetFullPath(htmlPath);
        var browser = ReportPdfExporter.FindBrowser() ?? throw new InvalidOperationException("Microsoft Edge or Google Chrome is needed for the browser checks.");
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
            if (!process.WaitForExit((int)(timeout ?? ReportPdfExporter.DefaultTimeout).TotalMilliseconds))
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
