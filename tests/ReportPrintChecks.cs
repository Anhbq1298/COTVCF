using System.IO;
using System.Text.RegularExpressions;
using COT_VCF.Application.Results;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;
using COT_VCF.Infrastructure;

/// <summary>The shared Print Report mechanism: Print Setup defaults and logo embedding, the sheets a report composes
/// for itself (size, header and footer, the saved setup), the order of its blocks, the screen ⇄ paper chart
/// re-layout — and, with a real browser (<c>--report-pdf</c>), the Print Report screen itself.</summary>
internal static class ReportPrintChecks
{
    private const string TinyPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
    private static readonly DateTimeOffset Issued = new(2026, 10, 5, 9, 30, 0, TimeSpan.Zero);

    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cotvcf_print_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Settings(folder);
            Logo(folder);
            Sheets(folder);
            SavedSetup();
            CableReportOnPaper(folder);
            EveryReportPrints(folder);
            Package(folder);
            Exporter(folder);
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    // ---------- Print Setup ----------

    private static void Settings(string folder)
    {
        var defaults = new ReportPrintSettings();
        Check(defaults is { PaperSize: ReportPaperSize.A4, Orientation: ReportPageOrientation.Landscape, ShowPageNumbers: true,
                  ShowDate: true, IncludeSourceData: false, CompanyName: "", LogoPath: "", ProjectName: "" },
            "Print Setup defaults to A4 landscape with page numbers and date, and no Source Data table");

        var changed = new List<string?>();
        var settings = new ReportPrintSettings();
        settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        settings.CompanyName = "ACME Structures"; settings.PaperSize = ReportPaperSize.A3; settings.CompanyName = "ACME Structures";
        Check(changed.SequenceEqual([nameof(ReportPrintSettings.CompanyName), nameof(ReportPrintSettings.PaperSize)]),
            "Print Setup raises a change only when a value really changes");

        var copy = settings.Clone();
        copy.CompanyName = "Other";
        Check(settings.CompanyName == "ACME Structures" && copy.PaperSize == ReportPaperSize.A3 && changed.Count == 2,
            "a cloned Print Setup is detached from the one being edited");

        var store = new ReportPrintSettingsStore(Path.Combine(folder, "prefs", ReportPrintSettingsStore.FileName));
        Check(store.Load() is { CompanyName: "", PaperSize: ReportPaperSize.A4 }, "no saved Print Setup loads as defaults");
        settings.ProjectName = "Tower"; settings.Orientation = ReportPageOrientation.Portrait; settings.ShowDate = false;
        Check(store.TrySave(settings) && File.ReadAllText(store.FilePath).Contains("\"paperSize\": \"A3\"", StringComparison.Ordinal),
            "Print Setup is saved as readable JSON in its own per-user file");
        Check(store.Load() is { CompanyName: "ACME Structures", ProjectName: "Tower", PaperSize: ReportPaperSize.A3,
                  Orientation: ReportPageOrientation.Portrait, ShowDate: false, ShowPageNumbers: true },
            "Print Setup round-trips through its store");
        File.WriteAllText(store.FilePath, "{ not json");
        Check(store.Load() is { CompanyName: "", PaperSize: ReportPaperSize.A4 }, "an unreadable Print Setup file falls back to defaults");

        var dto = ReportPrintEngine.Describe(null, " Cable Forces ", "Model.sdb", Issued);
        Check(dto is { Title: "Cable Forces", Model: "Model.sdb", Issued: "05 Oct 2026", Paper: "A4", Orientation: "landscape",
                  Logo: "", Company: "", ShowPageNumbers: true },
            "without a Print Setup a report still prints its title, model, date and page numbers");
    }

    // ---------- logo ----------

    private static void Logo(string folder)
    {
        var png = Path.Combine(folder, "logo.png");
        File.WriteAllBytes(png, Convert.FromBase64String(TinyPng));
        Check(ReportPrintEngine.LogoDataUri(png, out var pngProblem) == "data:image/png;base64," + TinyPng && pngProblem is null,
            "a PNG logo is embedded into the report as a data URI");

        var svg = Path.Combine(folder, "logo.svg");
        File.WriteAllText(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\"/></svg>");
        var svgUri = ReportPrintEngine.LogoDataUri(svg, out _);
        Check(svgUri is not null && svgUri.StartsWith("data:image/svg+xml;base64,", StringComparison.Ordinal) && !svgUri.Contains("http"),
            "an SVG logo is embedded too — vector, and with no external reference left in the report");

        Check(ReportPrintEngine.LogoDataUri("", out var none) is null && none is null &&
              ReportPrintEngine.LogoDataUri(Path.Combine(folder, "missing.png"), out var missing) is null && missing!.Contains("not found") &&
              ReportPrintEngine.LogoDataUri(WriteFile(folder, "logo.tif", 10), out var type) is null && type!.Contains("Unsupported") &&
              ReportPrintEngine.LogoDataUri(WriteFile(folder, "empty.png", 0), out var empty) is null && empty!.Contains("empty") &&
              ReportPrintEngine.LogoDataUri(WriteFile(folder, "huge.png", ReportPrintEngine.MaxLogoBytes + 1), out var huge) is null && huge!.Contains("limit"),
            "no logo, a missing file, an unsupported type, an empty file and an oversized file are all reported, never embedded");
        Check(ReportPrintEngine.DescribeLogo("").StartsWith("No logo", StringComparison.Ordinal) &&
              ReportPrintEngine.DescribeLogo(png).StartsWith("✓ logo.png", StringComparison.Ordinal) &&
              ReportPrintEngine.DescribeLogo(Path.Combine(folder, "missing.png")).StartsWith("⚠", StringComparison.Ordinal),
            "the Print Setup card says what will be printed top-left");

        var dto = ReportPrintEngine.Describe(new ReportPrintSettings { LogoPath = Path.Combine(folder, "missing.png"), CompanyName = "ACME" },
            "Cable Forces", "Model.sdb", Issued);
        Check(dto is { Logo: "", Company: "ACME" }, "a logo that cannot be embedded simply prints the company name instead");
    }

    // ---------- the sheets a report composes for itself ----------

    private static void Sheets(string folder)
    {
        var gen = new CableForceReportGenerator();
        var report = gen.Build(Study(loads: 1), "Model.sdb", IterationStudyMode.Combined, "CASE-1", Capacities());
        var logo = Path.Combine(folder, "logo.png");
        var settings = new ReportPrintSettings
        {
            CompanyName = "ACME Structures", LogoPath = logo, ProjectName = "Tower \"A\" <2> & Co", JobNumber = "24-0187",
            PreparedBy = "M. Bui", CheckedBy = "T. Nguyen", Revision = "B", FooterNote = "Preliminary",
        };
        var html = gen.BuildHtml(report, ReportPrintEngine.Describe(settings, CableForceReportGenerator.ReportTitle, "Model.sdb", Issued));
        Check(html.Contains("<style id=\"pr-page\" media=\"print\">") && html.Contains("id=\"print-data\"") && html.Contains(".pr-sheet{") &&
              Regex.IsMatch(html, "<button id=\"pr-btn\"[^>]*>Print Report</button>") && html.Contains("Print / Save as PDF") &&
              html.Contains("id=\"pr-screen\"") && html.Contains("Report setup"),
            "the report carries the Print Report mechanism: the button, the setup screen with its preview, the sheet styles");
        Check(Regex.Matches(html, "@media print").Count == 1 && !html.Contains("@top-left") && !html.Contains("counter(page"),
            "paper rules live on the sheets — one print stylesheet, the engine's, shows the sheets and nothing else");

        var page = new ReportPage(html);
        var css = page.Eval("window.reportPrint.css()");
        Check(css == "@page{size:297mm 210mm;margin:0}" && page.Text("pr-page") == css,
            "a sheet is the whole page: the page rule gives the paper size and no margins — header and footer are part of each sheet");
        Check(page.Eval("window.reportPrint.page().sheetWidthPx") == "1122" && page.Eval("window.reportPrint.page().sheetHeightPx") == "793" &&
              page.Eval("window.reportPrint.page().contentPx") == "1046" && page.Eval("window.reportPrint.page().contentHeightPx") == "684",
            "A4 landscape: the sheet, and inside its header band, footer band and side margins the area charts are laid out against");

        var head = page.Eval("window.reportPrint.headHtml()");
        Check(head.StartsWith("<div class=\"l\"><img alt=\"\" src=\"data:image/png;base64," + TinyPng + "\"></div>", StringComparison.Ordinal),
            "sheet header, left: the company logo");
        Check(head.Contains("<div class=\"c\">Cable Forces<span>Model.sdb</span></div>") &&
              head.EndsWith("<div class=\"r\"><b>Tower &quot;A&quot; &lt;2&gt; &amp; Co</b>Job No. 24-0187 · Rev B</div>", StringComparison.Ordinal),
            "sheet header: the report name and model in the centre; project, job number and revision on the right — user text cannot leave its cell");
        Check(page.Eval("window.reportPrint.footHtml(2, 7)") ==
              "<div class=\"l\">ACME Structures<br>Prepared by M. Bui · Checked by T. Nguyen</div><div class=\"c\">Preliminary</div><div class=\"r\">05 Oct 2026<br>Page 2 of 7</div>",
            "sheet footer: company and signatories, the note, the date and Page n of N");

        page.Run("var s = window.reportPrint.state; s.company = '<img src=x onerror=alert(1)>'; s.logo = ''; s.showPageNumbers = false; s.showDate = false;");
        Check(page.Eval("window.reportPrint.headHtml()").StartsWith("<div class=\"l\">&lt;img src=x onerror=alert(1)&gt;</div>", StringComparison.Ordinal) &&
              page.Eval("window.reportPrint.footHtml(1, 3)").EndsWith("<div class=\"r\"><br></div>", StringComparison.Ordinal),
            "without a logo the company name is printed top-left, as text; page numbers and the date can be switched off");

        page.Run("window.reportPrint.state.paper = 'A3'");
        Check(page.Eval("window.reportPrint.css()") == "@page{size:420mm 297mm;margin:0}" && page.Eval("window.reportPrint.page().contentPx") == "1511",
            "A3 gives a larger sheet and more width for the charts");
        page.Run("window.reportPrint.state.paper = 'Letter'; window.reportPrint.state.orientation = 'portrait'");
        Check(page.Eval("window.reportPrint.css()") == "@page{size:215.9mm 279.4mm;margin:0}" && page.Eval("window.reportPrint.page().widthMm") == "215.9",
            "orientation and Letter paper are honoured");

        ReportPrintDto Print(Func<ReportPrintDto, ReportPrintDto> change) =>
            change(ReportPrintEngine.Describe(new ReportPrintSettings { CompanyName = "ACME" }, "Cable Forces", "Model.sdb", Issued));
        var forged = new ReportPage(gen.BuildHtml(report, Print(dto => dto with { Logo = "x\"><script>alert(1)</script>" })));
        Check(forged.Eval("window.reportPrint.state.logo") == "" && forged.Eval("window.reportPrint.headHtml()").StartsWith("<div class=\"l\">ACME</div>", StringComparison.Ordinal),
            "a logo value that is not an embedded image is never written into a sheet");
        var plain = new ReportPage(gen.BuildHtml(report));
        Check(plain.Eval("window.reportPrint.headHtml()") == "<div class=\"l\"></div><div class=\"c\">Cable Forces<span>Model.sdb</span></div><div class=\"r\"></div>" &&
              plain.Eval("window.reportPrint.footHtml(1, 1)").Contains("Page 1 of 1") && plain.RequestedIds.All(plain.StaticIds.Contains),
            "a report generated without a Print Setup still prints its name, model and page numbers");
    }

    // ---------- the setup the browser keeps for the user ----------

    private static void SavedSetup()
    {
        const string key = "cotvcf.printSetup.v1";
        var gen = new CableForceReportGenerator();
        var report = gen.Build(Study(loads: 1), "Model.sdb", IterationStudyMode.Combined, "CASE-1", Capacities());
        var html = gen.BuildHtml(report, ReportPrintEngine.Describe(
            new ReportPrintSettings { CompanyName = "ACME", ProjectName = "Tower", PaperSize = ReportPaperSize.A3 }, "Cable Forces", "Model.sdb", Issued));

        var fresh = new ReportPage(html);
        Check(fresh.Eval("window.reportPrint.state.saved") == "false" && fresh.Eval("window.reportPrint.state.company") == "ACME" &&
              fresh.Eval("window.reportPrint.state.paper") == "A3" && fresh.Stored(key) == "",
            "a report opens on the Print Setup it was generated with, and stores nothing until the user changes something");

        var saved = new Dictionary<string, string>
        {
            [key] = "{\"company\":\"Meinhardt Singapore\",\"preparedBy\":\"AB/PP\",\"paper\":\"A4\",\"orientation\":\"portrait\",\"showDate\":false,\"includeSource\":true," +
                    "\"project\":42,\"logo\":\"javascript:alert(1)\"}",
        };
        var again = new ReportPage(html, storage: saved);
        Check(again.Eval("window.reportPrint.state.saved") == "true" && again.Eval("window.reportPrint.state.company") == "Meinhardt Singapore" &&
              again.Eval("window.reportPrint.state.preparedBy") == "AB/PP" && again.Eval("window.reportPrint.state.paper") == "A4" &&
              again.Eval("window.reportPrint.state.orientation") == "portrait" && again.Eval("window.reportPrint.state.showDate") == "false" &&
              again.Eval("window.reportPrint.state.includeSource") == "true",
            "the user's latest setup, kept by the browser, is used for the next report — Company: Meinhardt Singapore, Prepared by: AB/PP");
        Check(again.Eval("window.reportPrint.state.project") == "Tower" && again.Eval("window.reportPrint.state.logo") == "" &&
              again.Eval("window.reportPrint.headHtml()").Contains("<div class=\"l\">Meinhardt Singapore</div>"),
            "a stored value of the wrong kind, or a stored logo that is not an image, is ignored");
        again.Run("window.reportPrint.reset()");
        Check(again.Eval("window.reportPrint.state.company") == "ACME" && again.Eval("window.reportPrint.state.paper") == "A3",
            "the report's own defaults can be brought back");

        var linked = new ReportPage(html, "#print&paper=Letter&source=0", storage: saved);
        Check(linked.Eval("window.reportPrint.state.paper") == "Letter" && linked.Eval("window.reportPrint.state.includeSource") == "false" &&
              linked.Eval("window.reportPrint.state.company") == "Meinhardt Singapore",
            "options in the page address win over the stored setup for that page only");
        Check(new ReportPage(html, storage: new Dictionary<string, string> { [key] = "{ not json" }).Eval("window.reportPrint.state.company") == "ACME",
            "an unreadable stored setup is ignored");
    }

    // ---------- screen ⇄ paper ----------

    private static void CableReportOnPaper(string folder)
    {
        var gen = new CableForceReportGenerator();
        var svgLogo = Path.Combine(folder, "logo.svg");
        var path = gen.Generate(Path.Combine(folder, "study"), Study(loads: 2), "Model.sdb", IterationStudyMode.Combined, "CASE-1",
            Capacities(), ["cable"], print: new ReportPrintSettings { CompanyName = "ACME", LogoPath = svgLogo, ProjectName = "Tower", IncludeSourceData = false });
        var html = File.ReadAllText(path);
        Check(html.Contains("\"project\":\"Tower\"") && html.Contains("data:image/svg\\u002Bxml;base64,") &&
              !html.Contains("http://") && !html.Contains("https://") && !html.Contains("cdn."),
            "a generated report embeds its Print Setup and logo and stays self-contained");

        static int Width(string chart) => int.Parse(Regex.Match(chart, "<svg width=\"(\\d+)\"").Groups[1].Value);
        var page = new ReportPage(html);
        var screen = Width(page.Charts("detail")[0]);
        Check(page.RequestedIds.All(page.StaticIds.Contains) && page.Eval("window.reportPrint.state.active") == "false" &&
              page.Text("pview").StartsWith("Printed view: Force (kN) · load L01", StringComparison.Ordinal),
            "on screen the report is in its normal layout and keeps a one-line record of the view for the printout");

        page.Run("__els['pr-btn'].onclick()");
        Check(page.PrintCalls == 0 && page.Eval("window.reportPrint.state.open") == "true" && page.Eval("window.reportPrint.state.active") == "true" &&
              page.BodyHasClass("pr-open"),
            "Print Report opens the setup screen — it does not start printing");
        page.Run("window.reportPrint.close()");
        Check(page.Eval("window.reportPrint.state.open") == "false" && page.Eval("window.reportPrint.state.active") == "false" &&
              !page.BodyHasClass("pr-open") && !page.BodyHasClass("pr-on") && Width(page.Charts("detail")[0]) == screen,
            "closing the setup screen returns the report to its screen layout");

        page.Fire("beforeprint");                                   // Ctrl+P, the browser menu, or a headless print
        var paper = Width(page.Charts("detail")[0]);
        Check(page.Eval("window.reportPrint.state.active") == "true" && page.Eval("window.reportPrint.state.open") == "false" && paper != screen && paper <= 1200 &&
              page.Charts("detail").Count == 2 * 2 && page.Charts("overview").Count == 3 && page.BodyHasClass("pr-nosrc") && page.BodyHasClass("pr-active"),
            "the browser's own Print lays the same sheets out — charts against the sheet, never the window — without the Source Data table by default");
        Check(page.Charts("detail")[0].Contains("class=\"dl\">+") && !page.Charts("detail")[0].Contains("class=\"dl\" transform") &&
              Regex.IsMatch(page.Charts("detail")[0], "class=\"vl[^\"]*\">\\d{2},\\d{3}<"),
            "on A4 landscape the charts keep full value labels and upright percentage labels");
        page.Fire("afterprint");
        Check(page.Eval("window.reportPrint.state.active") == "false" && Width(page.Charts("detail")[0]) == screen && !page.BodyHasClass("pr-active"),
            "after printing the report returns to its screen layout");

        page.Run("window.reportPrint.state.includeSource = true; window.reportPrint.print()");
        Check(page.PrintCalls == 1 && page.Eval("window.reportPrint.state.active") == "true" && !page.BodyHasClass("pr-nosrc"),
            "Print / Save as PDF lays the sheets out and opens the browser's print dialog; Include source data table is honoured");
        page.Fire("afterprint");

        page.Run("window.reportPrint.state.paper = 'A3'");
        page.Fire("beforeprint");
        Check(Width(page.Charts("detail")[0]) > paper && Width(page.Charts("detail")[0]) <= 1501, "a larger sheet gives the charts more room instead of scaling them");
        page.Fire("afterprint");

        // opened for printing: laid out for paper as soon as it has loaded, with the options from the page address
        var exported = new ReportPage(html, "#print&paper=A3&orientation=portrait&mode=ut&load=L02&source=1");
        Check(exported.Eval("window.reportPrint.state.active") == "true" && exported.Eval("window.reportPrint.state.paper") == "A3" &&
              exported.Eval("window.reportPrint.state.orientation") == "portrait" && !exported.BodyHasClass("pr-nosrc") &&
              exported.Text("pr-page") == "@page{size:297mm 420mm;margin:0}",
            "opened with #print the report is in print layout on load, on the requested sheet");
        Check(exported.Value("load") == "L02" && exported.Text("pview").StartsWith("Printed view: % of capacity · load L02", StringComparison.Ordinal) &&
              exported.Charts("detail").All(chart => chart.Contains("data-limit=\"util\"")),
            "the page address can choose the value mode and the loading candidate to print");
        exported.Fire("afterprint");
        Check(exported.Eval("window.reportPrint.state.active") == "true", "a page opened for printing stays in print layout");
        var ignored = new ReportPage(html, "#print&paper=Poster&load=L99&mode=xyz&%E0%A4%A=1");
        Check(ignored.Eval("window.reportPrint.state.paper") == "A4" && ignored.Value("load") == "L01" &&
              ignored.Text("pview").Contains("Force (kN)"),
            "unknown or malformed options fall back to the report defaults");
    }

    // ---------- every report and diagnostic is on the engine ----------

    private static void EveryReportPrints(string folder)
    {
        var logo = Path.Combine(folder, "logo.png");
        var print = new ReportPrintSettings { CompanyName = "ACME", LogoPath = logo, ProjectName = "Tower", JobNumber = "24-0187" };
        foreach (var (name, title, path) in GenerateAll(Path.Combine(folder, "all"), print))
        {
            var html = File.ReadAllText(path);
            var perCandidate = name == "diagnostic";
            Check(html.Contains("<style id=\"pr-page\" media=\"print\">") && html.Contains("id=\"print-data\"") && Regex.Matches(html, "@media print").Count == 1 &&
                  Regex.IsMatch(html, "<button id=\"pr-btn\"[^>]*>Print Report</button>") && html.Contains("Print / Save as PDF") &&
                  !html.Contains("http://") && !html.Contains("https://") && !html.Contains("cdn."),
                $"{name}: has a Print Report button on the shared mechanism, keeps its paper rules on the sheets, and stays self-contained");
            Check(html.Contains("\"project\":\"Tower\"") && html.Contains("\"jobNumber\":\"24-0187\"") &&
                  html.Contains("data:image/png;base64,") == !perCandidate && (!perCandidate || html.Contains("\"logo\":\"\"")),
                perCandidate ? $"{name}: embeds the Print Setup without a logo copy in every candidate file (company name top-left)"
                             : $"{name}: embeds the Print Setup and the logo");

            // the engine alone: these reports' own scripts need a real browser, which the opt-in --report-pdf check uses
            var page = new ReportPage(html, scripts: 1);
            var head = page.Eval("window.reportPrint.headHtml()");
            Check(page.Eval("window.reportPrint.css()") == "@page{size:297mm 210mm;margin:0}" && head.Contains($"<div class=\"c\">{title}<span>Model.sdb</span></div>") &&
                  head.Contains("<b>Tower</b>Job No. 24-0187") && head.Contains("<img alt=\"\" src=\"data:image/png") == !perCandidate &&
                  (!perCandidate || head.StartsWith("<div class=\"l\">ACME</div>", StringComparison.Ordinal)) &&
                  page.Eval("window.reportPrint.footHtml(1, 2)").Contains("Page 1 of 2") && page.RequestedIds.All(page.StaticIds.Contains),
                $"{name}: prints \"{title}\" with the project, logo and page numbers on every sheet");
            page.Fire("beforeprint");
            var onPaper = page.BodyHasClass("pr-on") && page.BodyHasClass("pr-nosrc");
            page.Fire("afterprint");
            Check(onPaper && !page.BodyHasClass("pr-on"), $"{name}: switches to the paper layout for printing and back afterwards");
        }

        var files = GenerateAll(Path.Combine(folder, "hooks"), print).ToDictionary(item => item.Name, item => File.ReadAllText(item.Path));
        Check(new[] { "deflection", "member", "support" }.All(name => files[name].Contains("data-print-controls") && files[name].Contains("data-print-view")),
            "chart reports hand their toolbar to the engine, which prints what the hidden filters were set to");
        Check(files["deflection"].Contains("id=\"matrixWrap\" data-print=\"source\" data-pr=\"source\"") && files["deflection"].Contains("id=\"matrix\" data-print-fit") &&
              files["member"].Contains("class=\"matrix-wrap\" data-print-fit") && files["support"].Contains("class=\"matrix-wrap\" data-print-fit") &&
              files["cable"].Contains("data-print=\"source\" data-pr=\"source\""),
            "source tables are optional on paper, and wide matrices are scaled to the sheet instead of being cut off");
        Check(files["deflection"].Contains("PRINT.register(") && files["deflection"].Contains("printW || chart.clientWidth") &&
              files["member"].Contains("OPT.group") && files["support"].Contains("OPT.group") && files["deflection"].Contains("OPT.group"),
            "Deflection redraws its charts for the sheet, and each chart report accepts its view from the page address");

        // content order: information, legends and general notes come before the charts — in the page itself too
        static bool Before(string html, string first, string second) => html.IndexOf(first, StringComparison.Ordinal) is var a and >= 0 && html.IndexOf(second, StringComparison.Ordinal) > a;
        Check(new[] { "deflection", "member", "support", "cable", "optimization" }.All(name => files[name].Contains("data-pr=\"info\"") && files[name].Contains("data-pr=\"note\"")) &&
              new[] { "deflection", "member", "support", "cable", "optimization" }.All(name => files[name].Contains("data-pr=\"legend\"") && files[name].Contains("data-pr=\"chart\"")),
            "every report marks its blocks by role: information, legend, general note, chart");
        Check(Before(files["deflection"], "<details id=\"candKey\" data-pr=\"legend\">", "<div id=\"panels\">") &&
              Before(files["deflection"], "<div id=\"warns\" data-pr=\"note\">", "<div id=\"panels\">") &&
              Before(files["cable"], "<div class=\"leg\" id=\"legend\" data-pr=\"legend\">", "01 · Cable Force Overview") &&
              Before(files["cable"], "<div id=\"warns\" data-pr=\"note\">", "01 · Cable Force Overview") &&
              Before(files["member"], "<div id=\"warnings\" data-pr=\"note\">", "<main id=\"panels\">") &&
              Before(files["support"], "<div id=\"warnings\" data-pr=\"note\">", "<main id=\"panels\">"),
            "legends, candidate definitions and warnings stand ahead of the chart section — never between or after the charts");
        Check(files["member"].Contains("<div class=\"clegend\" data-pr=\"legend\">") && !files["member"].Contains("</div>${legend}${summaryChart") &&
              files["support"].Contains("</p>${roleLegend()}${kpiCards()}") && !files["support"].Contains("${roleLegend(comp)}"),
            "a summary states its legend once, ahead of its charts, instead of repeating it in every chart");
        Check(files["deflection"].Contains("function makePanel(init){") && files["deflection"].Contains("panelsEl.appendChild(host);") &&
              !files["deflection"].Contains("anchorAfter") && files["deflection"].Contains("+' – Copy'") && files["deflection"].Contains("from:id"),
            "Deflection keeps its charts as one ordered list: a chart — a duplicate too — always joins the end, with its own id");
    }

    // ---------- the Print Report screen in a real browser ----------

    /// <summary>What every report's Print Report screen must do, read back from the browser.</summary>
    private const string ScreenScript = """
        let printed = 0; window.print = () => { printed++; };
        const R = window.reportPrint, q = s => document.querySelector(s), qa = s => Array.from(document.querySelectorAll(s));
        const sheets = () => qa('#pr-pages > .pr-sheet');
        const order = () => qa('#pr-pages .pr-body [data-pr], #pr-pages .pr-sec').map(e => e.classList.contains('pr-sec') ? '[' + e.textContent + ']' : e.getAttribute('data-pr')).join(' ');
        const DR = window.deflectionReport;
        if (DR){
          DR.duplicate(0); DR.duplicate(0); DR.duplicate(1);
          log('charts', DR.charts().map(c => c.id + '<' + (c.duplicatedFrom || '') + ':' + c.title).join(' | '));
          log('chartDom', qa('#panels > section.panel').map(x => x.getAttribute('data-pr-id')).join(','));
          const name = qa('#panels > section.panel').pop().querySelector('.pv-name'); name.value = 'Renamed copy'; name.dispatchEvent(new Event('input')); name.dispatchEvent(new Event('change'));
          log('titles', DR.charts().map(c => c.title).join(' | '));
        }
        q('#pr-btn').click();
        log('opened', !q('#pr-screen').hidden && !q('#pr-screen').classList.contains('pr-ghost') && getComputedStyle(q('#pr-screen')).position === 'fixed');
        log('printedOnOpen', printed);
        log('layout', Math.round(q('.pr-setup').getBoundingClientRect().right) <= Math.round(q('#pr-view').getBoundingClientRect().left) + 1);
        log('fields', qa('#pr-screen input[data-k]').map(i => i.getAttribute('data-k')).join(',') + '|' + ['pr-logo-pick','pr-paper','pr-orient','pr-numbers','pr-date','pr-src','pr-go'].filter(id => !!document.getElementById(id)).join(','));
        log('sheets', sheets().length);
        log('size', sheets()[0].style.width + 'x' + sheets()[0].style.height);
        log('order', order());
        const m = R.model();
        log('model', ['info','legends','generalAnnotations','summary','charts','sourceTables'].map(k => k + '=' + m[k].length).join(' '));
        log('modelCharts', m.charts.filter(c => c.type !== 'heading').map(c => c.id).join(','));
        log('head', sheets()[0].querySelector('.pr-head').innerText.replace(/\s+/g, ' ').trim());
        log('logo', !!sheets()[0].querySelector('.pr-head img'));
        log('footLast', sheets()[sheets().length - 1].querySelector('.pr-foot').innerText.replace(/\s+/g, ' ').trim());
        const gone = e => getComputedStyle(e).display === 'none' || !e.getClientRects().length;
        log('controlsHidden', qa('#pr-pages .no-print').every(gone) && !qa('#pr-pages select, #pr-pages textarea, #pr-pages input[type=text], #pr-pages input:not([type])').length
          && qa('#pr-pages button').every(b => gone(b) || (getComputedStyle(b).borderTopWidth === '0px' && /rgba\(0, 0, 0, 0\)|transparent/.test(getComputedStyle(b).backgroundColor))));
        log('vector', qa('#pr-pages svg').length + ' svg, ' + qa('#pr-pages canvas, #pr-pages .pr-body img').length + ' raster');
        log('overflow', sheets().filter(s => { const b = s.querySelector('.pr-body'); return b.scrollHeight > b.clientHeight + 1; }).length);
        const type = (key, value) => { const i = q('#pr-screen input[data-k="' + key + '"]'); i.value = value; i.dispatchEvent(new Event('input')); };
        type('company', 'Meinhardt Singapore'); type('preparedBy', 'AB/PP');
        log('typed', sheets().every(s => s.querySelector('.pr-foot .l').innerText.includes('Meinhardt Singapore') && s.querySelector('.pr-foot .l').innerText.includes('Prepared by AB/PP')));
        log('stored', (localStorage.getItem('cotvcf.printSetup.v1') || '').includes('"company":"Meinhardt Singapore"'));
        const before = sheets().length;
        const pick = (id, value) => { const s = q(id); s.value = value; s.dispatchEvent(new Event('change')); };
        pick('#pr-paper', 'A3'); log('a3', sheets()[0].style.width + 'x' + sheets()[0].style.height);
        pick('#pr-orient', 'portrait'); log('a3portrait', sheets()[0].style.width + 'x' + sheets()[0].style.height);
        pick('#pr-paper', 'A4'); pick('#pr-orient', 'landscape');
        log('restored', sheets().length === before);
        q('#pr-src').click();
        log('source', qa('#pr-pages [data-pr="source"]').length + ' in ' + sheets().length + ' sheets; last=' + order().split(' ').pop());
        q('#pr-src').click();
        q('#pr-numbers').click(); q('#pr-date').click();
        log('noNumbers', JSON.stringify(sheets()[0].querySelector('.pr-foot .r').innerText.trim()));
        q('#pr-numbers').click(); q('#pr-date').click();
        q('#pr-go').click(); log('printedOnGo', printed);
        q('#pr-close').click();
        log('closed', q('#pr-screen').hidden && !sheets().length && document.body.className.trim() === '');
        localStorage.removeItem('cotvcf.printSetup.v1');
        """;

    private static void PrintReportScreen(string folder, string logo)
    {
        var print = new ReportPrintSettings { CompanyName = "ACME", LogoPath = logo, ProjectName = "Tower", JobNumber = "24-0187", Revision = "B", FooterNote = "Preliminary" };
        foreach (var (name, title, path) in GenerateAll(folder, print))
        {
            var r = ReportBrowser.RunScript(path, ScreenScript);
            var tagged = name != "diagnostic";
            if (r.TryGetValue("ERROR", out var error)) throw new Exception($"FAIL: {name}: the Print Report check script stopped — {error} (after: {string.Join(", ", r.Keys)})");
            Check(r["opened"] == "true" && r["printedOnOpen"] == "0" && r["layout"] == "true",
                $"{name}: Print Report opens a setup screen — settings on the left, the preview on the right — and does not print");
            Check(r["fields"] == "company,project,jobNumber,revision,preparedBy,checkedBy,footerNote|pr-logo-pick,pr-paper,pr-orient,pr-numbers,pr-date,pr-src,pr-go",
                $"{name}: the setup has Company, Project, Job No., Revision, Prepared by, Checked by, Footer note, Logo, Paper, Orientation, Page numbers, Date and Include source data table");
            Check(int.Parse(r["sheets"]) >= 1 && r["size"] == "1122pxx793px" && r["overflow"] == "0" && r["head"].Contains(title) && r["head"].Contains("Tower") &&
                  r["head"].Contains("Job No. 24-0187 · Rev B") && r["logo"] == (tagged ? "true" : "false") && r["footLast"].EndsWith($"Page {r["sheets"]} of {r["sheets"]}", StringComparison.Ordinal),
                $"{name}: the preview is {r["sheets"]} A4 landscape sheet(s), each with the header and footer, none overflowing");
            if (tagged)
            {
                var order = r["order"].Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(role => !role.StartsWith('[') && !role.EndsWith(']') && role is "info" or "legend" or "note" or "summary" or "chart" or "source").ToArray();
                string[] rank = ["info", "legend", "note", "summary", "chart", "source"];
                Check(order.Length > 2 && order[0] == "info" && order.Contains("legend") && order.Contains("chart") &&
                      order.Zip(order.Skip(1)).All(pair => Array.IndexOf(rank, pair.First) <= Array.IndexOf(rank, pair.Second)) && r["order"].Contains("[Legend"),
                    $"{name}: the sheets run information → legends → general notes → summary → charts ({r["model"]})");
            }
            Check(r["controlsHidden"] == "true" && r["vector"].EndsWith(", 0 raster", StringComparison.Ordinal),
                $"{name}: buttons and filters are not on the sheets, and the charts stay vector graphics ({r["vector"]})");
            Check(r["typed"] == "true" && r["stored"] == "true" && r["a3"] == "1587pxx1122px" && r["a3portrait"] == "1122pxx1587px" && r["restored"] == "true",
                $"{name}: typing Company and Prepared by updates every sheet at once and is kept by the browser; paper and orientation re-lay the sheets");
            Check(r["noNumbers"] == "\"\"" && r["printedOnGo"] == "1" && r["closed"] == "true",
                $"{name}: page numbers and date can be switched off; Print / Save as PDF prints once; Close restores the report");
            if (name is "deflection" or "support" or "cable" or "optimization")
                Check(int.Parse(r["source"].Split(' ')[0]) > 0 && r["source"].EndsWith("last=source", StringComparison.Ordinal),
                    $"{name}: Include source data table adds the source table after the charts ({r["source"]})");
            if (name != "deflection") continue;
            Check(r["chartDom"] == "chart_deflection_01,chart_deflection_02,chart_deflection_03,chart_deflection_04" && r["modelCharts"] == r["chartDom"] &&
                  r["charts"].Split(" | ").Select(c => c.Split(':')[0]).SequenceEqual(["chart_deflection_01<", "chart_deflection_02<chart_deflection_01",
                      "chart_deflection_03<chart_deflection_01", "chart_deflection_04<chart_deflection_02"]),
                "deflection: Duplicate appends the copy to the END of the charts — A, A–Copy, A–Copy, (A–Copy)–Copy — on screen, in the model and on the sheets");
            var titles = r["titles"].Split(" | ");
            Check(titles.Length == 4 && titles[3] == "Renamed copy" && titles[0] == titles[1].Replace(" – Copy", "") && titles[1].EndsWith(" – Copy", StringComparison.Ordinal),
                "deflection: a copy starts from its source's configuration and name, and is independent afterwards");
        }
    }

    /// <summary>One of every report on the engine, generated the way Tab 06 does.</summary>
    private static IEnumerable<(string Name, string Title, string Path)> GenerateAll(string folder, ReportPrintSettings print)
    {
        var results = FullStudy();
        string[] siblings = ["deflection", "member", "support", "cable"];
        yield return ("deflection", DeflectionReportGenerator.ReportTitle, new DeflectionReportGenerator().Generate(folder, results,
            "Model.sdb", @"C:\base.sdb", IterationStudyMode.Combined, siblings, new DeflectionLimitBand(10, 200), print));
        yield return ("member", MemberForceReportGenerator.ReportTitle, new MemberForceReportGenerator().Generate(folder, results,
            "Model.sdb", IterationStudyMode.Combined, siblings, print));
        yield return ("support", SupportReactionReportGenerator.ReportTitle, new SupportReactionReportGenerator().Generate(folder, results,
            "Model.sdb", IterationStudyMode.Combined, siblings, print));
        yield return ("cable", CableForceReportGenerator.ReportTitle, new CableForceReportGenerator().Generate(folder, results,
            "Model.sdb", IterationStudyMode.Combined, "CASE-1", Capacities(), siblings, print: print));
        var qualification = StudyQualifier.Evaluate(results.Select(CandidateMetrics.From).ToArray(),
            new QualificationCriteria(true, new DeflectionLimitBand(null, 60), true, 1.0), Capacities());
        yield return ("optimization", OptimizationSummaryReport.ReportTitle, new OptimizationSummaryReport().Generate(folder,
            qualification, "Model.sdb", IterationStudyMode.Combined, ["optimization", .. siblings], print));
        yield return ("diagnostic", $"{CandidateDiagnosticReportGenerator.ReportTitle} · {results[0].IterationKey}",
            new CandidateDiagnosticReportGenerator().Write(results[0], Path.Combine(folder, results[0].IterationKey), print));
    }

    /// <summary>The cable study with deflection, member-force and support-reaction results on every candidate.</summary>
    private static List<CandidateResultDto> FullStudy() => Study(loads: 1).Select((result, i) =>
    {
        var points = Enumerable.Range(0, 5).Select(n => new MonitoredPointResultDto($"P{n + 1}", n * 10.0, 0, 0, n + 1,
            -(20 + 15 * i) * Math.Sin((n + .5) / 5 * Math.PI), ResultValueStatus.Valid, null)).ToArray();
        MemberForceStationResultDto Station(string frame, int sequence, double station, double p) => new(
            frame, sequence, frame + "-1", station, station, sequence - 1 + station, "Step", 1, p, p * .1, 0, 0, 0, p * .3, ResultValueStatus.Valid);
        SupportReactionPointResultDto Support(string point, int sequence, double x, double fz) => new(
            point, sequence, x, 0, 0, "Step", 1, fz * .05, 0, fz, 0, 0, 0, ResultValueStatus.Valid);
        return result with
        {
            Groups = [new MonitoredPointGroupResultDto("Monitored Deflection", "SERVICE", ProfileCoordinateAxis.X, points)],
            MemberForceGroups = [new MemberForceGroupResultDto("TopChord", "[O]-TopChord", "CASE-1",
                [Station("TC-01", 1, 0, -900 - 20 * i), Station("TC-01", 1, .5, -950 - 20 * i), Station("TC-02", 2, 0, -700 - 20 * i), Station("TC-02", 2, .5, -720 - 20 * i)])],
            SupportReactionGroups =
            [
                new SupportReactionGroupResultDto("Left", "[O]-FoundationLeft", "CASE-1", [Support("200", 1, -60, -400 - 10 * i), Support("198", 2, -50, 5000 + 100 * i)]),
                new SupportReactionGroupResultDto("Right", "[O]-FoundationRight", "CASE-1", [Support("199", 1, 50, 5100 + 100 * i), Support("205", 2, 60, -380 - 10 * i)]),
            ],
        };
    }).ToList();

    // ---------- Print All Reports: the package ----------

    private static void Package(string folder)
    {
        var root = Path.Combine(folder, "package");
        var print = new ReportPrintSettings { CompanyName = "ACME </script> & Co", ProjectName = "Tower", PaperSize = ReportPaperSize.A3 };
        foreach (var _ in GenerateAll(root, print)) { }
        var items = StudyReportPackage.Items(root);
        var summaries = new[]
        {
            (Title: "Top Chord", Launcher: MemberForceReportGenerator.TopChordFileName, Options: "summary=top"),
            (Title: "Bottom Chord", Launcher: MemberForceReportGenerator.BottomChordFileName, Options: "summary=bottom"),
            (Title: "Mega Column", Launcher: MemberForceReportGenerator.MegaColumnFileName, Options: "summary=mega"),
        }.Where(summary => File.Exists(Path.Combine(root, summary.Launcher))).ToArray();
        string[] expected = ["Optimization Summary", "Deflection", .. summaries.Length > 0 ? summaries.Select(summary => summary.Title) : ["Member Forces"],
            "Support Reactions", "Cable Forces"];
        Check(items.Select(item => item.Title).SequenceEqual(expected) && items.All(item => File.Exists(Path.Combine(root, item.FileName))) &&
              summaries.All(summary => items.Any(item => item.Title == summary.Title && item.FileName == MemberForceReportGenerator.ReportFileName && item.Options == summary.Options)) &&
              items.Where(item => item.FileName != MemberForceReportGenerator.ReportFileName).All(item => item.Options is null),
            $"Print All Reports takes the reports of a folder in module order — {string.Join(", ", expected)}");
        foreach (var summary in summaries) File.Delete(Path.Combine(root, summary.Launcher));
        Check(StudyReportPackage.Items(root).Count(item => item.FileName == MemberForceReportGenerator.ReportFileName && item.Options is null && item.Title == "Member Forces") == 1 &&
              StudyReportPackage.Items(Path.Combine(root, "nowhere")).Count == 0,
            "without a group Summary the member-force report is printed as it opens; a folder without reports gives nothing to print");

        var html = StudyReportPackage.Html(root, items, print, "Model.sdb - Study Reports");
        Check(System.Text.RegularExpressions.Regex.Matches(html, "<iframe ").Count == items.Count &&
              html.Contains(new Uri(Path.Combine(root, DeflectionReportGenerator.ReportFileName)).AbsoluteUri + "#print=package\"", StringComparison.Ordinal) &&
              summaries.All(summary => html.Contains(MemberForceReportGenerator.ReportFileName + "#print=package&amp;" + summary.Options + "\"", StringComparison.Ordinal)) &&
              html.IndexOf("Optimization_Summary.html", StringComparison.Ordinal) < html.IndexOf("Cable_Forces_Report.html", StringComparison.Ordinal),
            "the package page loads every report in its own frame, opened as one report of a package, in order");
        Check(!html.Contains("ACME </script>", StringComparison.Ordinal) && html.Contains("ACME \\u003C/script\\u003E \\u0026 Co", StringComparison.Ordinal) &&
              html.Contains("\"paper\":\"A3\"", StringComparison.Ordinal) && html.Contains("\"project\":\"Tower\"", StringComparison.Ordinal) &&
              html.Contains("<title>Model.sdb - Study Reports</title>", StringComparison.Ordinal),
            "the package hands every report the current Print Setup, embedded safely");

        var old = Path.Combine(root, "Old_Report.html");
        File.WriteAllText(old, new string('x', 2_500_000) + "<script>const auto = options.print != null;</script>");
        var cut = Path.Combine(root, "Cut_Report.html");
        File.WriteAllText(cut, new string('x', (1 << 20) - 10) + "const packaged = options.print === \"package\" && true;");
        Check(items.All(item => ReportPrintEngine.SupportsPackage(Path.Combine(root, item.FileName))) && ReportPrintEngine.SupportsPackage(cut) &&
              !ReportPrintEngine.SupportsPackage(old) && !ReportPrintEngine.SupportsPackage(Path.Combine(root, "missing.html")),
            "a report generated by this version can join a package; one from an earlier version is recognised");

        var earlier = Path.Combine(folder, "earlier");
        Directory.CreateDirectory(earlier);
        File.Copy(old, Path.Combine(earlier, DeflectionReportGenerator.ReportFileName));
        string? refused = null, nothing = null;
        try { ReportPdfExporter.ExportPackage(earlier, Path.Combine(earlier, "all.pdf"), print, "t"); } catch (InvalidOperationException ex) { refused = ex.Message; }
        try { ReportPdfExporter.ExportPackage(Path.Combine(folder, "nowhere"), Path.Combine(earlier, "all.pdf"), print, "t"); } catch (InvalidOperationException ex) { nothing = ex.Message; }
        Check(refused is not null && refused.Contains(DeflectionReportGenerator.ReportFileName, StringComparison.Ordinal) && refused.Contains("Regenerate", StringComparison.Ordinal) &&
              nothing is not null && nothing.Contains("no report to print", StringComparison.Ordinal) && !File.Exists(Path.Combine(earlier, "all.pdf")),
            "Print All Reports refuses reports from an earlier version — regenerate first — and an empty folder, and writes nothing");
    }

    // ---------- PDF exporter ----------

    private static void Exporter(string folder)
    {
        var html = Path.Combine(folder, "My Study", "Cable Forces.html");
        var pdf = Path.Combine(folder, "My Study", "out", "Cable Forces.pdf");
        var arguments = ReportPdfExporter.BuildArguments(html, pdf, Path.Combine(folder, "profile"), "#paper=A3&mode=ut");
        Check(arguments.Contains("--headless=new") && arguments.Contains("--no-pdf-header-footer") &&
              arguments.Contains("--print-to-pdf=" + pdf) && arguments.Contains("--user-data-dir=" + Path.Combine(folder, "profile")),
            "the checks' browser prints headless, with its own profile and without the browser's own header and footer");
        Check(arguments.Contains("--virtual-time-budget=10000") &&
              ReportPdfExporter.BuildArguments(html, pdf, "p", virtualTimeBudgetMs: 30000).Contains("--virtual-time-budget=30000"),
            "a package of reports is given more time to lay itself out than a single report");
        var counted = Path.Combine(folder, "counted.pdf");
        File.WriteAllText(counted, "%PDF-1.4\n1 0 obj <</Type /Pages /Count 3>>\n2 0 obj <</Type /Page /Parent 1 0 R>>\n3 0 obj <</Type/Page>>\n4 0 obj <</Type /Page\n>>\n");
        Check(ReportPdfExporter.CountPages(counted) == 3 && ReportPdfExporter.CountPages(Path.Combine(folder, "no.pdf")) == 0,
            "the pages of a written PDF are counted for the status line");
        Check(arguments[^1].StartsWith("file:///", StringComparison.Ordinal) && arguments[^1].Contains("My%20Study/Cable%20Forces.html") &&
              arguments[^1].EndsWith("#print&paper=A3&mode=ut", StringComparison.Ordinal) &&
              ReportPdfExporter.BuildArguments(html, pdf, "p")[^1].EndsWith("Cable%20Forces.html#print", StringComparison.Ordinal),
            "the report is opened by file URL with the print fragment and any report options");
        var browser = ReportPdfExporter.FindBrowser();
        Check(browser is null || File.Exists(browser), "the exporter only reports a browser that is installed");
        var threw = false;
        try { ReportPdfExporter.Export(Path.Combine(folder, "nothing.html")); } catch (FileNotFoundException) { threw = true; }
        Check(threw && !File.Exists(Path.Combine(folder, "nothing.pdf")), "exporting a report that was never generated fails clearly and writes nothing");
    }

    /// <summary>Opt-in (<c>--report-pdf</c>): with the installed browser, prints every report to a real PDF and
    /// drives the Print Report screen — the one place the chart reports' own scripts and the composed sheets meet a
    /// layout engine. Needs Microsoft Edge or Google Chrome; never touches SAP2000.</summary>
    public static void RunPdfExport()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cotvcf_pdf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var logo = Path.Combine(folder, "logo.png");
            File.WriteAllBytes(logo, Convert.FromBase64String(TinyPng));
            var html = new CableForceReportGenerator().Generate(folder, Study(loads: 1), "Model.sdb", IterationStudyMode.Combined, "CASE-1",
                Capacities(), ["cable"], print: new ReportPrintSettings { CompanyName = "ACME", LogoPath = logo, ProjectName = "Tower", JobNumber = "24-0187" });
            var result = ReportPdfExporter.Export(html);
            var bytes = File.ReadAllBytes(result.PdfPath);
            Check(result.PdfPath == Path.ChangeExtension(html, ".pdf") && bytes.Length > 20_000 &&
                  System.Text.Encoding.ASCII.GetString(bytes, 0, 5) == "%PDF-",
                $"{Path.GetFileName(result.BrowserPath)} printed the report to a {bytes.Length / 1024} KB PDF in {result.Elapsed.TotalSeconds:0.0} s");
            Check(Directory.GetFiles(folder, "*.tmp.pdf").Length == 0, "no temporary file is left beside the PDF");
            var again = ReportPdfExporter.Export(html, options: "paper=A3&mode=ut");
            Check(File.Exists(again.PdfPath) && new FileInfo(again.PdfPath).Length > 20_000, "exporting again replaces the PDF (A3, % of capacity)");

            var all = Path.Combine(folder, "all");
            foreach (var (name, _, path) in GenerateAll(all, new ReportPrintSettings { CompanyName = "ACME", LogoPath = logo, ProjectName = "Tower" }))
            {
                var printed = ReportPdfExporter.Export(path, options: name is "deflection" ? "source=1" : name is "member" or "support" ? "view=matrix" : null);
                var pdf = File.ReadAllBytes(printed.PdfPath);
                Check(pdf.Length > 15_000 && System.Text.Encoding.ASCII.GetString(pdf, 0, 5) == "%PDF-",
                    $"{name}: {Path.GetFileName(path)} printed to a {pdf.Length / 1024} KB PDF in {printed.Elapsed.TotalSeconds:0.0} s");
                if (name != "support") continue;
                // its default view (Summary, Left foundation) and the By Candidate drill-down
                foreach (var (label, options) in new[] { ("summary", "source=1"), ("by candidate", "detail=candidate&pretension=P02&height=H03") })
                {
                    var view = ReportPdfExporter.Export(path, Path.Combine(all, $"support {label}.pdf"), options);
                    Check(new FileInfo(view.PdfPath).Length > 15_000, $"support: the {label} view printed to a {new FileInfo(view.PdfPath).Length / 1024} KB PDF");
                }
            }
            // Print All Reports: one PDF of the whole folder — as many pages as the reports printed one by one
            var items = StudyReportPackage.Items(all);
            var single = items.Sum(item => ReportPdfExporter.Export(Path.Combine(all, item.FileName), Path.Combine(all, "one.pdf"), item.Options).PageCount);
            var package = ReportPdfExporter.ExportPackage(all, Path.Combine(folder, "out", "All Reports.pdf"),
                new ReportPrintSettings { CompanyName = "ACME", LogoPath = logo, ProjectName = "Tower" }, "Model.sdb - Study Reports");
            Check(single >= items.Count && package.PageCount == single && new FileInfo(package.PdfPath).Length > 50_000,
                $"Print All Reports wrote {items.Count} reports as one {package.PageCount}-page PDF in {package.Elapsed.TotalSeconds:0.0} s — the pages of the reports printed one by one ({single})");
            var portrait = ReportPdfExporter.ExportPackage(all, Path.Combine(folder, "out", "All Reports A3.pdf"),
                new ReportPrintSettings { CompanyName = "ACME", PaperSize = ReportPaperSize.A3, Orientation = ReportPageOrientation.Portrait }, "Model.sdb - Study Reports");
            var box = System.Text.RegularExpressions.Regex.Match(System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(portrait.PdfPath)),
                @"/MediaBox\s*\[\s*0\s+0\s+([\d.]+)\s+([\d.]+)");
            Check(box.Success && Math.Abs(double.Parse(box.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - 841.9) < 1.5 &&
                  Math.Abs(double.Parse(box.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) - 1190.6) < 1.5 && portrait.PageCount >= items.Count,
                $"the package prints with the Print Setup handed to it, not the one a report was generated with: A3 portrait, {portrait.PageCount} pages");
            PrintReportScreen(Path.Combine(folder, "screen"), logo);
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    // ---------- fixtures ----------

    private static readonly string[] Cables = Enumerable.Range(1, 8).SelectMany(n => new[] { $"Cable{n}a-2", $"Cable{n}b-2" }).ToArray();

    private static IReadOnlyDictionary<string, double> Capacities() => Cables.ToDictionary(c => c, _ => 31106.5);

    /// <summary>16 cables (8 per side) × 3 truss heights × 2 pretension candidates, for one or two loads.</summary>
    private static List<CandidateResultDto> Study(int loads)
    {
        var results = new List<CandidateResultDto>();
        long run = 0;
        for (var l = 1; l <= loads; l++)
        foreach (var (hid, hm) in new[] { ("H01", 20.0), ("H02", 25.0), ("H03", 30.0) })
        for (var p = 1; p <= 2; p++)
        {
            run++;
            var key = $"L{l:00}_{hid}_P{p:00}";
            var forces = Cables.Select((c, i) => new CableForceResultDto(c, 6000 + 1500 * (i / 2), (6000 + 1500 * (i / 2)) * 2.4 + hm * 40 * l + p * 300,
                ResultValueStatus.Valid)).ToArray();
            results.Add(new CandidateResultDto(key, $@"C:\Study\{key}.sdb", @"C:\base.sdb", "Model.sdb", IterationStudyMode.Combined,
                new CandidateParametersDto(run, key, $"L{l:00}", hid, $"P{p:00}", 5.0 * l, $"{5 * l} kPa", hm, 0, null, $"P{p:00} · mixed"),
                DateTimeOffset.Now, DateTimeOffset.Now, DateTimeOffset.Now, CandidateExtractionStatus.ExtractedOk, [], [], forces));
        }
        return results;
    }

    private static string WriteFile(string folder, string name, int bytes)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
