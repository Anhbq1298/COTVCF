using System.IO;
using System.Text.RegularExpressions;
using COT_VCF.Application.Results;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;
using COT_VCF.Infrastructure;

/// <summary>The shared report print engine: Print Setup, logo embedding, the paged layout the report builds for
/// itself (sheet, margins, header / footer in the page margins), the screen ⇄ paper chart re-layout, and the
/// headless PDF exporter's command line.</summary>
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
            PageRules(folder);
            CableReportOnPaper(folder);
            EveryReportPrints(folder);
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

    // ---------- the @page rule the report builds for itself ----------

    private static void PageRules(string folder)
    {
        var gen = new CableForceReportGenerator();
        var report = gen.Build(Study(loads: 1), "Model.sdb", IterationStudyMode.Combined, "CASE-1", Capacities());
        var logo = Path.Combine(folder, "logo.png");
        var settings = new ReportPrintSettings
        {
            CompanyName = "ACME Structures", LogoPath = logo, ProjectName = "Tower \"A\" \\ Phase 2\nx", JobNumber = "24-0187",
            PreparedBy = "M. Bui", CheckedBy = "T. Nguyen", Revision = "B", FooterNote = "Preliminary",
        };
        var html = gen.BuildHtml(report, ReportPrintEngine.Describe(settings, CableForceReportGenerator.ReportTitle, "Model.sdb", Issued));
        Check(html.Contains("<style id=\"pr-page\" media=\"print\">") && html.Contains("@media print") && html.Contains("id=\"print-data\"") &&
              html.Contains("id=\"pr-btn\"") && html.Contains("Print / Save as PDF") && html.Contains("data-print=\"source\""),
            "the report carries the print engine: page-rule holder, print stylesheet, settings block, Print button and panel");

        var page = new ReportPage(html);
        var css = page.Eval("window.reportPrint.css()");
        Check(css.StartsWith("@page{size:A4 landscape;margin:24mm 38px 16mm 38px;", StringComparison.Ordinal) && page.Text("pr-page") == css,
            "the sheet and its margins come from an @page rule built from the Print Setup");
        Check(Regex.IsMatch(css, "@top-left\\{[^}]*content:\"\";background:url\\(\"data:image/png;base64," + Regex.Escape(TinyPng) + "\"\\) no-repeat left bottom"),
            "the company logo is printed in the top-left page-margin box");
        Check(css.Contains("content:\"Tower \\\"A\\\" \\\\ Phase 2 x\" \"\\A \" \"Cable Forces · Model.sdb\";", StringComparison.Ordinal),
            "header centre: project over report name and model — quotes, backslashes and line breaks cannot leave the CSS string");
        Check(css.Contains("content:\"Job No. 24-0187 · Rev B\" \"\\A \" \"05 Oct 2026\";", StringComparison.Ordinal),
            "header right: job number, revision and the date the report was generated");
        Check(css.Contains("content:\"ACME Structures\" \"\\A \" \"Prepared by M. Bui · Checked by T. Nguyen\";", StringComparison.Ordinal) &&
              css.Contains("content:\"Preliminary\";", StringComparison.Ordinal) &&
              css.Contains("content:\"Page \" counter(page) \" of \" counter(pages);", StringComparison.Ordinal),
            "footer: company and signatories, the note, and Page N of M");
        foreach (var (band, share) in new[] { ("top", "left"), ("bottom", "left") })
        {
            var side = int.Parse(Regex.Match(css, $"@{band}-{share}\\{{[^}}]*width:(\\d+)px").Groups[1].Value);
            var centre = Regex.Match(css, $"@{band}-center\\{{[^}}]*width:calc\\(297mm - (\\d+)px\\)").Groups[1].Value;
            var right = Regex.Match(css, $"@{band}-right\\{{[^}}]*width:(\\d+)px").Groups[1].Value;
            Check(centre == (2 * 38 + 2 * side).ToString() && right == side.ToString(),
                $"the three {band} margin boxes tile the sheet width exactly, so their rule prints as one unbroken line");
        }
        Check(page.Eval("window.reportPrint.page().contentPx") == "1046" && page.Value("pr-paper") == "A4" && page.Value("pr-orient") == "landscape" &&
              page.Text("pr-info").Contains("logo") && page.Text("pr-info").Contains("Print Setup"),
            "the Print panel opens on the Print Setup sheet and says what the header and footer carry");

        page.Run("__els['pr-paper'].onchange({ target: { value: 'A3' } })");
        Check(page.Eval("window.reportPrint.css()").StartsWith("@page{size:A3 landscape;", StringComparison.Ordinal) &&
              page.Eval("window.reportPrint.page().contentPx") == "1511" && page.Text("pr-page").Contains("calc(420mm - "),
            "choosing A3 in the report rebuilds the page rule and the width charts are laid out against");
        page.Run("__els['pr-orient'].onchange({ target: { value: 'portrait' } })");
        page.Run("__els['pr-paper'].onchange({ target: { value: 'Letter' } })");
        Check(page.Eval("window.reportPrint.css()").StartsWith("@page{size:letter portrait;", StringComparison.Ordinal) &&
              page.Eval("window.reportPrint.page().widthMm") == "215.9",
            "orientation and Letter paper are honoured");
        page.Run("__els['pr-paper'].onchange({ target: { value: 'B0' } })");
        Check(page.Eval("window.reportPrint.state.paper") == "Letter", "an unknown paper size is ignored");

        ReportPrintDto Print(Func<ReportPrintDto, ReportPrintDto> change) =>
            change(ReportPrintEngine.Describe(new ReportPrintSettings { CompanyName = "ACME" }, "Cable Forces", "Model.sdb", Issued));
        string CssOf(ReportPrintDto dto) => new ReportPage(gen.BuildHtml(report, dto)).Eval("window.reportPrint.css()");

        var noLogo = CssOf(Print(dto => dto));
        Check(Regex.IsMatch(noLogo, "@top-left\\{[^}]*content:\"ACME\";") && !noLogo.Contains("url("),
            "without a logo the company name is printed top-left");
        var forged = CssOf(Print(dto => dto with { Logo = "x\") } body{display:none} @page{ (\"" }));
        Check(!forged.Contains("url(") && !forged.Contains("display:none"), "a logo value that is not an embedded image is never written into the page rule");
        var bare = CssOf(Print(dto => dto with { ShowPageNumbers = false, ShowDate = false, Company = "" }));
        Check(!bare.Contains("counter(page)") && !bare.Contains("05 Oct 2026") && bare.Contains("content:\"Cable Forces\" \"\\A \" \"Model.sdb\";"),
            "page numbers and date can be switched off; the report name and model are always printed");

        var plain = new ReportPage(gen.BuildHtml(report));
        Check(plain.Eval("window.reportPrint.css()").Contains("counter(pages)") && plain.RequestedIds.All(plain.StaticIds.Contains),
            "a report generated without a Print Setup still prints with a header, footer and page numbers");
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

        page.Fire("beforeprint");                                   // Ctrl+P, the browser menu, or a headless print
        var paper = Width(page.Charts("detail")[0]);
        Check(page.Eval("window.reportPrint.state.active") == "true" && paper != screen && paper <= 1200 &&
              page.Charts("detail").Count == 2 * 2 && page.Charts("overview").Count == 3 && page.BodyHasClass("pr-nosrc"),
            "printing re-lays the charts out against the sheet (never the window) and leaves the Source Data table out by default");
        Check(page.Charts("detail")[0].Contains("class=\"dl\">+") && !page.Charts("detail")[0].Contains("class=\"dl\" transform") &&
              Regex.IsMatch(page.Charts("detail")[0], "class=\"vl[^\"]*\">\\d{2},\\d{3}<"),
            "on A4 landscape the charts keep full value labels and upright percentage labels");
        page.Fire("afterprint");
        Check(page.Eval("window.reportPrint.state.active") == "false" && Width(page.Charts("detail")[0]) == screen,
            "after printing the report returns to its screen layout");

        page.Run("__els['pr-src'].onchange({ target: { checked: true } })");
        page.Run("__els['pr-go'].onclick()");
        Check(page.PrintCalls == 1 && page.Eval("window.reportPrint.state.active") == "true" && !page.BodyHasClass("pr-nosrc") && page.Checked("pr-src"),
            "the Print button lays the page out and opens the browser's print dialog; Include Source Data is honoured");
        page.Fire("afterprint");

        page.Run("__els['pr-paper'].onchange({ target: { value: 'A3' } })");
        page.Fire("beforeprint");
        Check(Width(page.Charts("detail")[0]) > paper && Width(page.Charts("detail")[0]) <= 1501, "a larger sheet gives the charts more room instead of scaling them");
        page.Fire("afterprint");

        // opened by the PDF exporter: laid out for paper as soon as it has loaded, with the options from the URL
        var exported = new ReportPage(html, "#print&paper=A3&orientation=portrait&mode=ut&load=L02&source=1");
        Check(exported.Eval("window.reportPrint.state.active") == "true" && exported.Eval("window.reportPrint.state.paper") == "A3" &&
              exported.Eval("window.reportPrint.state.orientation") == "portrait" && !exported.BodyHasClass("pr-nosrc") &&
              exported.Text("pr-page").StartsWith("@page{size:A3 portrait;", StringComparison.Ordinal),
            "opened with #print the report is in print layout on load, on the requested sheet");
        Check(exported.Value("load") == "L02" && exported.Text("pview").StartsWith("Printed view: % of capacity · load L02", StringComparison.Ordinal) &&
              exported.Charts("detail").All(chart => chart.Contains("data-limit=\"util\"")),
            "the exporter can choose the value mode and the loading candidate to print");
        exported.Fire("afterprint");
        Check(exported.Eval("window.reportPrint.state.active") == "true", "an exported page stays in print layout");
        var ignored = new ReportPage(html, "#print&paper=Poster&load=L99&mode=xyz&%E0%A4%A=1");
        Check(ignored.Eval("window.reportPrint.state.paper") == "A4" && ignored.Value("load") == "L01" &&
              ignored.Text("pview").Contains("Force (kN)"),
            "unknown or malformed URL options fall back to the report defaults");
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
            Check(html.Contains("<style id=\"pr-page\" media=\"print\">") && html.Contains("id=\"print-data\"") && html.Contains("@media print") &&
                  html.Contains("id=\"pr-btn\"") && html.Contains("Print / Save as PDF") &&
                  !html.Contains("http://") && !html.Contains("https://") && !html.Contains("cdn."),
                $"{name}: carries the print engine (page rule, print stylesheet, Print button) and stays self-contained");
            Check(html.Contains("\"project\":\"Tower\"") && html.Contains("\"jobNumber\":\"24-0187\"") &&
                  html.Contains("data:image/png;base64,") == !perCandidate && (!perCandidate || html.Contains("\"logo\":\"\"")),
                perCandidate ? $"{name}: embeds the Print Setup without a logo copy in every candidate file (company name top-left)"
                             : $"{name}: embeds the Print Setup and the logo");

            // the engine alone: these reports' own scripts need a real browser, which the opt-in --report-pdf check uses
            var page = new ReportPage(html, scripts: 1);
            var css = page.Eval("window.reportPrint.css()");
            Check(css.StartsWith("@page{size:A4 landscape;", StringComparison.Ordinal) && css.Contains($"\"{title} · Model.sdb\"") &&
                  css.Contains("\"Tower\"") && css.Contains("counter(pages)") && css.Contains("url(\"data:image/png") == !perCandidate &&
                  page.RequestedIds.All(page.StaticIds.Contains),
                $"{name}: prints \"{title}\" with the project, logo and page numbers in the page margins");
            page.Fire("beforeprint");
            var onPaper = page.BodyHasClass("pr-on") && page.BodyHasClass("pr-nosrc");
            page.Fire("afterprint");
            Check(onPaper && !page.BodyHasClass("pr-on"), $"{name}: switches to the paper layout for printing and back afterwards");
        }

        var files = GenerateAll(Path.Combine(folder, "hooks"), print).ToDictionary(item => item.Name, item => File.ReadAllText(item.Path));
        Check(new[] { "deflection", "member", "support" }.All(name => files[name].Contains("data-print-controls") && files[name].Contains("data-print-view")),
            "chart reports hand their toolbar to the engine, which prints what the hidden filters were set to");
        Check(files["deflection"].Contains("id=\"matrixWrap\" data-print=\"source\"") && files["deflection"].Contains("id=\"matrix\" data-print-fit") &&
              files["member"].Contains("class=\"matrix-wrap\" data-print-fit") && files["support"].Contains("class=\"matrix-wrap\" data-print-fit") &&
              files["cable"].Contains("data-print=\"source\""),
            "source tables are optional on paper, and wide matrices are scaled to the sheet instead of being cut off");
        Check(files["deflection"].Contains("PRINT.register(") && files["deflection"].Contains("printW || chart.clientWidth") &&
              files["member"].Contains("OPT.group") && files["support"].Contains("OPT.group") && files["deflection"].Contains("OPT.group"),
            "Deflection redraws its charts for the sheet, and each chart report accepts its view from the export URL");
    }

    /// <summary>One of every report on the engine, generated the way Tab 06 does.</summary>
    private static IEnumerable<(string Name, string Title, string Path)> GenerateAll(string folder, ReportPrintSettings print)
    {
        var results = FullStudy();
        string[] siblings = ["deflection", "member", "support", "cable", "shortlist"];
        yield return ("deflection", DeflectionReportGenerator.ReportTitle, new DeflectionReportGenerator().Generate(folder, results,
            "Model.sdb", @"C:\base.sdb", IterationStudyMode.Combined, siblings, new DeflectionLimitBand(10, 200), print));
        yield return ("member", MemberForceReportGenerator.ReportTitle, new MemberForceReportGenerator().Generate(folder, results,
            "Model.sdb", IterationStudyMode.Combined, siblings, print));
        yield return ("support", SupportReactionReportGenerator.ReportTitle, new SupportReactionReportGenerator().Generate(folder, results,
            "Model.sdb", IterationStudyMode.Combined, siblings, print));
        yield return ("cable", CableForceReportGenerator.ReportTitle, new CableForceReportGenerator().Generate(folder, results,
            "Model.sdb", IterationStudyMode.Combined, "CASE-1", Capacities(), siblings, print: print));
        var criteria = StudyDecisionCriteria.Default;
        yield return ("shortlist", StudyShortlistExporter.ReportTitle, new StudyShortlistExporter().Export(folder,
            new StudyDecisionService().Evaluate(results, criteria), criteria, siblingReports: siblings, print: print, baselineName: "Model.sdb").HtmlPath);
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
        SupportReactionPointResultDto Support(string point, int sequence, double fz) => new(
            point, sequence, sequence * 10, 0, 0, "Step", 1, fz * .05, 0, fz, 0, 0, 0, ResultValueStatus.Valid);
        return result with
        {
            Groups = [new MonitoredPointGroupResultDto("Monitored Deflection", "SERVICE", ProfileCoordinateAxis.X, points)],
            MemberForceGroups = [new MemberForceGroupResultDto("TopChord", "[O]-TopChord", "CASE-1",
                [Station("TC-01", 1, 0, -900 - 20 * i), Station("TC-01", 1, .5, -950 - 20 * i), Station("TC-02", 2, 0, -700 - 20 * i), Station("TC-02", 2, .5, -720 - 20 * i)])],
            SupportReactionGroups =
            [
                new SupportReactionGroupResultDto("Left", "[O]-FoundationLeft", "CASE-1", [Support("200", 1, 5000 + 100 * i), Support("198", 2, -400 - 10 * i)]),
                new SupportReactionGroupResultDto("Right", "[O]-FoundationRight", "CASE-1", [Support("199", 1, 5100 + 100 * i), Support("205", 2, -380 - 10 * i)]),
            ],
        };
    }).ToList();

    // ---------- PDF exporter ----------

    private static void Exporter(string folder)
    {
        var html = Path.Combine(folder, "My Study", "Cable Forces.html");
        var pdf = Path.Combine(folder, "My Study", "out", "Cable Forces.pdf");
        var arguments = ReportPdfExporter.BuildArguments(html, pdf, Path.Combine(folder, "profile"), "#paper=A3&mode=ut");
        Check(arguments.Contains("--headless=new") && arguments.Contains("--no-pdf-header-footer") &&
              arguments.Contains("--print-to-pdf=" + pdf) && arguments.Contains("--user-data-dir=" + Path.Combine(folder, "profile")),
            "the PDF exporter prints headless, with its own profile and without the browser's own header and footer");
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

    /// <summary>Opt-in (<c>--report-pdf</c>): prints every report to a real PDF with the installed browser — the
    /// one place the chart reports' own scripts run. Needs Microsoft Edge or Google Chrome; never touches SAP2000.</summary>
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
            }
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
