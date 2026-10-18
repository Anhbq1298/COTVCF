using System.IO;
using System.Text.Json;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Design;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;

/// <summary>
/// Design → Tension Cable Design against the Technical Options Study of 18 Oct 2026 (the values of its pages 3–5, worked by its author outside the
/// application): the catalogue's breaking loads and stiffness, the selection and installed rate per metre of every force from 10 to 120 MN at
/// L = 50 m for the VSL, FATZER and Utracon systems, the 120 MN worked example, and the refusals (no tension, no length, too many units, a bad
/// basis). The optimization's Member Utilization check then suggests the assembly for a cable. See docs/analysis/tension-cable-design-verification.md.
/// </summary>
internal static class CableDesignChecks
{
    private static readonly TensionCableBasis All = new() { Systems = [CableSystem.VslSsi2000, CableSystem.FatzerFlc, CableSystem.UtraconPt], MaxUnits = 100 };

    // page 4 / 5 of the study: force [MN], VSL units × code + S$/m, FATZER ropes × Ø + S$/m, Utracon tendons + S$/m
    private static readonly (int T, int VslN, string Vsl, double VslRate, int FatN, string Fat, double FatRate, int UtN, double UtRate)[] Schedule =
    [
        (10, 1, "6-73", 2914, 2, "Ø100", 8352, 2, 2701), (20, 1, "6-151", 5234, 4, "Ø100", 16704, 4, 5402), (30, 2, "6-109", 7994, 6, "Ø100", 25056, 6, 8102),
        (40, 2, "6-151", 10467, 8, "Ø100", 33408, 8, 10803), (50, 2, "6-187", 12603, 7, "Ø120", 40572, 10, 13504), (60, 3, "6-151", 15701, 12, "Ø100", 50112, 12, 16205),
        (70, 3, "6-169", 17252, 10, "Ø120", 57960, 14, 18906), (80, 4, "6-151", 20934, 7, "Ø150", 61194, 16, 21607), (90, 4, "6-169", 23002, 8, "Ø150", 69936, 18, 24307),
        (100, 4, "6-187", 25206, 9, "Ø150", 78678, 20, 27008), (110, 5, "6-169", 28753, 10, "Ø150", 87420, 22, 29709), (120, 6, "6-151", 31401, 11, "Ø150", 96162, 24, 32410),
    ];

    public static void Run()
    {
        // the catalogue: VSL 6-187 has 187 × 279 = 52 173 kN, 26.087 MN at 50 %; a strand's EA is 150 mm² × 195 GPa; the 37-strand tendon 5.162 MN at 50 %
        var vsl187 = TensionCableCatalogue.Units(CableSystem.VslSsi2000).Single(unit => unit.Code == "6-187");
        var utracon = TensionCableCatalogue.Units(CableSystem.UtraconPt).Single();
        Check(Near(vsl187.BreakingLoadKN, 52_173, 1e-9) && Near(vsl187.BreakingLoadKN / 2, 26_086.5, 1e-9) && Near(vsl187.AxialStiffnessKN, 187 * 150 * 195_000.0 / 1000, 1e-9) &&
              TensionCableCatalogue.Units(CableSystem.VslSsi2000).Count == 18 && Near(utracon.BreakingLoadKN / 2, 5161.5, 1e-9),
            "the catalogue has the 18 VSL sizes of the study: 6-187 = 52 173 kN (26.087 MN at 50 %), EA = N × 150 mm² × 195 GPa; the 37-strand tendon 5.162 MN at 50 %");
        var fatzer = TensionCableCatalogue.Units(CableSystem.FatzerFlc);
        Check(fatzer.Count == 5 && fatzer.Single(unit => unit.Code == "Ø150").BreakingLoadKN == 23_000 && fatzer.Single(unit => unit.Code == "Ø150").AxialStiffnessKN == 2_525_000 &&
              fatzer.Single(unit => unit.Code == "Ø100").MassKgPerM == 58 && fatzer.Single(unit => unit.Code == "Ø135").BreakingLoadKN == 17_400 &&
              fatzer.Single(unit => unit.Code == "Ø150").LimitTensionKN == 15_333,
            "FATZER FLC Ø80 … Ø150 are the planning aid's galvanized table: Ø100 10.10 MN / 58 kg/m, Ø135 17.4 MN, Ø150 23.0 MN / 131 kg/m / EA 2525 MN / F_Rd 15.33 MN");

        // the whole schedule: 10 … 120 MN, L = 50 m, 50 % of the breaking load — units, type and installed S$/m of each system
        var worst = 0.0;
        foreach (var row in Schedule)
        {
            var tension = row.T * 1000.0;
            var best = TensionCableDesigner.Candidates(All, tension, 50).ToDictionary(item => item.System);
            var v = best[CableSystem.VslSsi2000];
            var f = best[CableSystem.FatzerFlc];
            var u = best[CableSystem.UtraconPt];
            Check(v.Units == row.VslN && v.Code == row.Vsl && Math.Abs(v.CostPerMSgd - row.VslRate) <= 3, $"{row.T} MN VSL: {v.Assembly} S${v.CostPerMSgd:#,0}/m = the study's {row.VslN} × {row.Vsl} S${row.VslRate:#,0}/m");
            Check(f.Units == row.FatN && f.Code == row.Fat && Math.Abs(f.CostPerMSgd - row.FatRate) <= 3, $"{row.T} MN FATZER: {f.Assembly} S${f.CostPerMSgd:#,0}/m = the study's {row.FatN} × {row.Fat} S${row.FatRate:#,0}/m");
            Check(u.Units == row.UtN && Math.Abs(u.CostPerMSgd - row.UtRate) <= 3, $"{row.T} MN Utracon: {u.Assembly} S${u.CostPerMSgd:#,0}/m = the study's {row.UtN} × 37 S${row.UtRate:#,0}/m");
            worst = Math.Max(worst, Math.Max(Math.Abs(v.CostPerMSgd - row.VslRate), Math.Max(Math.Abs(f.CostPerMSgd - row.FatRate), Math.Abs(u.CostPerMSgd - row.UtRate))));
        }
        Console.WriteLine($"     largest difference from the study's rates over the 36 selections: S${worst:0.0}/m");

        // the 120 MN member of section 3.2 / 3.3: group reference forces 130.43 / 126.50 / 123.88 MN, ΣEA 27 349 / 27 775 / 25 974 MN, extensions 219 / 216 / 231 mm
        var fewest = All with { Priority = CablePriority.FewestUnits };
        var vslUnits = TensionCableDesigner.Candidates(fewest, 120_000, 50).ToDictionary(item => item.System);
        var five = vslUnits[CableSystem.VslSsi2000];
        Check(five.Units == 5 && five.Code == "6-187" && Near(five.GroupCapacityKN, 130_432.5, 1e-9) && Near(five.AxialStiffnessKN / 1000, 27_349, 0.0005) && Math.Abs(five.ElongationMm - 219) < 0.6 &&
              Math.Abs(five.CostPerMSgd - 31_506) <= 3,
            $"120 MN, fewest units: 5 × 6-187 carries {five.GroupCapacityKN / 1000:0.00} MN at 50 %, ΣEA {five.AxialStiffnessKN / 1000:#,0} MN, ΔL {five.ElongationMm:0} mm, S${five.CostPerMSgd:#,0}/m (study: 130.43 / 27 349 / 219 / 31 506)");
        var rope = vslUnits[CableSystem.FatzerFlc];
        var tendon = vslUnits[CableSystem.UtraconPt];
        Check(rope.Units == 11 && Near(rope.GroupCapacityKN / 1000, 126.5, 1e-9) && Near(rope.AxialStiffnessKN / 1000, 27_775, 1e-9) && Math.Abs(rope.ElongationMm - 216) < 0.6 &&
              tendon.Units == 24 && Near(tendon.GroupCapacityKN / 1000, 123.88, 0.0005) && Near(tendon.AxialStiffnessKN / 1000, 25_974, 0.0005) && Math.Abs(tendon.ElongationMm - 231) < 0.6 && tendon.TotalStrands == 888,
            $"120 MN: 11 × Ø150 FLC {rope.GroupCapacityKN / 1000:0.00} MN, ΣEA {rope.AxialStiffnessKN / 1000:#,0}, ΔL {rope.ElongationMm:0} mm; 24 × 37 tendons {tendon.GroupCapacityKN / 1000:0.00} MN, ΣEA {tendon.AxialStiffnessKN / 1000:#,0}, ΔL {tendon.ElongationMm:0} mm");
        var lowest = TensionCableDesigner.Candidates(All, 120_000, 50)[0];
        Check(lowest.Assembly == "6 × VSL SSI 2000 6-151" || lowest.System == CableSystem.UtraconPt, "lowest cost at 120 MN is the VSL 6 × 6-151 (or, where offered, the unqualified Utracon alternative that the study prices below it)");
        // the default basis offers VSL and FATZER only; the cheapest is the VSL 6 × 6-151, 126.39 MN, S$31 401/m (the study's cost-optimised selection)
        var chosen = TensionCableDesigner.Design(TensionCableBasis.Default, "C1", "STRAND", 50, "SLS", 120_000);
        Check(chosen.Selected is { Code: "6-151", Units: 6 } pick && Near(pick.GroupCapacityKN / 1000, 126.387, 0.0005) && chosen.Status == "OK" && chosen.Alternatives.Single().System == CableSystem.FatzerFlc &&
              chosen.Detail.Contains("6 × VSL SSI 2000 6-151") && chosen.Detail.Contains("Other systems"),
            "the default basis (VSL, FATZER) picks 6 × 6-151 = 126.39 MN at 120 MN and lists the best FATZER assembly as the alternative");

        // basis: 60 % needs fewer units; the priorities and the cautions
        var sixty = TensionCableDesigner.Candidates(TensionCableBasis.Default with { ScreeningFactor = 0.6 }, 60_000, 50).First(item => item.System == CableSystem.VslSsi2000);
        var fifty = TensionCableDesigner.Candidates(TensionCableBasis.Default, 60_000, 50).First(item => item.System == CableSystem.VslSsi2000);
        Check(sixty.TotalStrands < fifty.TotalStrands && sixty.GroupCapacityKN >= 60_000 && TensionCableDesigner.Notes(TensionCableBasis.Default with { ScreeningFactor = 0.6 }, sixty).Any(note => note.Contains("above the 50 %")),
            $"60 % of the breaking load sizes 60 MN with {sixty.TotalStrands} strands against {fifty.TotalStrands} at 50 %, and says it needs the supplier's confirmation");
        Check(TensionCableDesigner.Notes(TensionCableBasis.Default with { ForceBasis = CableForceBasis.Ultimate }, fifty).Any(note => note.Contains("NOT an EN 1993-1-11 ULS resistance") || note.Contains("not an EN 1993-1-11 ULS resistance")) &&
              TensionCableDesigner.Notes(TensionCableBasis.Default, fifty).All(note => !note.Contains("ULS resistance")),
            "a factored (ULS) force is cautioned: 50–60 % of the breaking load is not an EN 1993-1-11 resistance");
        var leastSteel = TensionCableDesigner.Candidates(All with { Priority = CablePriority.LeastSteel }, 60_000, 50).First(item => item.System == CableSystem.VslSsi2000);
        Check(leastSteel.SteelAreaMm2 <= fifty.SteelAreaMm2 + 1e-9 && fewest.Priority == CablePriority.FewestUnits,
            $"the least-steel priority never uses more steel ({leastSteel.SteelAreaMm2:#,0} mm²) than the cheapest ({fifty.SteelAreaMm2:#,0} mm²)");

        // statuses: the unqualified system is CHECK, never silently OK
        var onlyUtracon = TensionCableDesigner.Design(new TensionCableBasis { Systems = [CableSystem.UtraconPt] }, "C", "", 50, "", 30_000);
        var onlyFatzer = TensionCableDesigner.Design(new TensionCableBasis { Systems = [CableSystem.FatzerFlc] }, "C", "", 50, "", 50_000);
        Check(onlyUtracon.Status == "CHECK" && onlyUtracon.Notes.Any(note => note.Contains("not shown to be an approved")) &&
              onlyFatzer.Selected is { Code: "Ø120", Units: 7 } && onlyFatzer.Status == "OK" && onlyFatzer.Detail.Contains("limit tension F_Rd"),
            "an unqualified Utracon tendon gives CHECK with the reason, not OK; FATZER's published limit tension is shown beside its selection");

        // refusals: never a made-up stay
        var slack = TensionCableDesigner.Design(TensionCableBasis.Default, "C", "", 50, "SLS", 0);
        var noLength = TensionCableDesigner.Design(TensionCableBasis.Default, "C", "", 0, "SLS", 5_000);
        var tooMany = TensionCableDesigner.Design(TensionCableBasis.Default with { MaxUnits = 3 }, "C", "", 50, "SLS", 120_000);
        var noTension = TensionCableDesigner.Design(TensionCableBasis.Default, "C", "", 50, "", double.NaN);
        var badBasis = TensionCableDesigner.Design(TensionCableBasis.Default with { ScreeningFactor = 0 }, "C", "", 50, "SLS", 5_000);
        var noSystem = TensionCableDesigner.Design(TensionCableBasis.Default with { Systems = [] }, "C", "", 50, "SLS", 5_000);
        Check(slack is { Status: "—", Selected: null } && noLength is { Status: "ERROR", Selected: null } && tooMany is { Status: "ERROR", Selected: null } && tooMany.Error!.Contains("3 parallel units") &&
              noTension is { Status: "ERROR" } && badBasis is { Status: "ERROR" } && noSystem is { Status: "ERROR" },
            "a cable with no tension, no length, more units than allowed, a bad screening level or no system offered is refused with the reason, and no stay is invented");
        Check(TensionCableDesigner.Candidates(All, 5_000, 50)[0].CostPerMSgd < TensionCableDesigner.Candidates(All, 5_000, 20)[0].CostPerMSgd,
            "a shorter stay costs more per metre (the two anchorages are amortised over its length)");
        var rates = TensionCableRates.Entries.Aggregate(new TensionCableRates(), (set, entry) => entry.Set(set, entry.Get(set) * 2));
        Check(rates.Problems().Count == 0 && new TensionCableRates { GstPercent = -1 }.Problems().Count == 1 &&
              Near(TensionCableDesigner.Candidates(All with { Rates = new TensionCableRates { GstPercent = 9 } }, 30_000, 50)[0].CostPerMSgd,
                  TensionCableDesigner.Candidates(All, 30_000, 50)[0].CostPerMSgd * 1.09, 1e-9),
            "every rate is editable, a negative rate is refused and GST of 9 % adds 9 % to the rate");

        Check(TensionCableDesigner.StayLadder.Select(item => item.Selection.TotalStrands).SequenceEqual([73, 151, 218, 302, 374, 453, 507, 604, 676, 748, 845, 906]) &&
              TensionCableDesigner.StayLadder.Select(item => item.Selection.Assembly).Take(3).SequenceEqual(["1 × VSL SSI 2000 6-73", "1 × VSL SSI 2000 6-151", "2 × VSL SSI 2000 6-109"]),
            "the stay ladder is the study's selection schedule of section 04: strands 73 … 906 for 10 … 120 MN");
        Optimization();
        WordExport();
    }

    /// <summary>Export to Word…: the report is a valid .docx that reads back with the title, the summary table (one row per cable) and each calculation.</summary>
    private static void WordExport()
    {
        var designs = new[]
        {
            TensionCableDesigner.Design(TensionCableBasis.Default, "C1", "STRAND", 50, "SLS", 60_000),
            TensionCableDesigner.Design(TensionCableBasis.Default, "C2", "STRAND", 40, "SLS", 0),
            TensionCableDesigner.Design(TensionCableBasis.Default, "C3", "STRAND", 35, "ULS", 12_000),
        };
        var report = TensionCableDesigner.WordReport(TensionCableBasis.Default, designs);
        var path = Path.Combine(Path.GetTempPath(), $"mcotis-cable-design-{Guid.NewGuid():N}.docx");
        try
        {
            DesignReportWord.Export(report, path);
            using var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(path, false);
            var body = document.MainDocumentPart!.Document.Body!;
            var text = body.InnerText;
            var table = body.Elements<DocumentFormat.OpenXml.Wordprocessing.Table>().Single();
            var invalid = new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(document).Take(3).Select(error => error.Description).ToArray();
            Check(invalid.Length == 0, "the exported Word document is valid Open XML (Word opens it without repair)" + (invalid.Length > 0 ? " — " + string.Join(" | ", invalid) : ""));
            Check(report.Rows.Count == 3 && report.Calculations.Count == 2 && table.Elements<DocumentFormat.OpenXml.Wordprocessing.TableRow>().Count() == 4 &&
                  text.Contains("Tension Cable Design") && text.Contains("3 × VSL SSI 2000 6-151") && text.Contains("C3 — ") && text.Contains("not for construction") &&
                  text.Contains("The cable carries no tension") && document.MainDocumentPart.FooterParts.Any(),
                "Export to Word writes a .docx that reads back: the title, a table of one row per cable (3 + header), the calculation of each designed cable (2), a slack cable explained, a page-numbered footer");
        }
        finally { File.Delete(path); }
    }

    /// <summary>The optimization's Member Utilization check suggests the assembly of each cable with the Design basis, and leaves the rest of the row as it was.</summary>
    private static void Optimization()
    {
        var points = new Dictionary<string, StructuralPoint>(StringComparer.Ordinal) { ["A"] = new("A", new(0, 0, 0)), ["B"] = new("B", new(30, 0, 40)), ["C"] = new("C", new(30, 0, 45)) };
        var geometry = new GeometryModel(points, [new StructuralMember("C1", "A", "B", MemberKind.Cable), new StructuralMember("F1", "B", "C", MemberKind.Frame)]);
        var properties = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal)
        {
            ["C1"] = new("C1", 1e-3, 195e6, null, SectionName: "STRAND"), ["F1"] = new("F1", 0.05, 200e6, 1e-3, SectionName: "UB"),
        };
        var candidate = new ParametricCandidate("OPT00001", geometry, properties, null!, [], new Dictionary<string, double>(), null, [], "bottom chord point", 10);
        FemAnalysisResult Solved(double tension) => new([], [], [], [new FemCableResponse("C1", tension, true)], 0);
        var results = new Dictionary<string, FemAnalysisResult> { ["SLS"] = Solved(300), ["ULS"] = Solved(8_000) };

        var plain = new MemberUtilizationCheck(_ => null, 1860, 0.5).Evaluate(candidate, results).Single(item => item.Member == "C1");
        Check(plain.CableDesign is null && plain.SuggestedCable == "", "without a tension cable basis a cable row carries no suggestion (the check is as it was)");

        var check = new MemberUtilizationCheck(_ => null, 1860, 0.5, TensionCableBasis.Default);
        var rows = check.Evaluate(candidate, results);
        var cable = rows.Single(item => item.Member == "C1");
        var length = Math.Sqrt(30 * 30 + 40 * 40);
        var expected = TensionCableDesigner.Design(TensionCableBasis.Default, "C1", "STRAND", length, "ULS", 8_000);
        Check(Math.Abs(cable.Utilization!.Value - plain.Utilization!.Value) < 1e-12 && cable.Check == plain.Check && cable.Case == "ULS" && cable.Note == plain.Note &&
              cable.CableDesign is { } suggested && suggested == expected.Selected && Math.Abs(suggested.LengthM - 50) < 1e-9 && suggested.TensionKN == 8_000 && suggested.GroupCapacityKN >= 8_000 &&
              rows.Single(item => item.Member == "F1").CableDesign is null,
            $"the Member Utilization row of a cable keeps its utilization, check and note and adds the assembly for its governing tension (8 000 kN, 50 m): {cable.SuggestedCable}");

        // a saved study keeps it (a row without it, saved before this existed, reads back as no suggestion)
        var json = JsonSerializer.Serialize(rows);
        var back = JsonSerializer.Deserialize<MemberUtilization[]>(json)!;
        var old = JsonSerializer.Deserialize<MemberUtilization>("{\"Member\":\"C1\",\"Type\":\"Cable\",\"Section\":\"S\",\"Utilization\":0.5,\"Check\":\"OK\",\"Case\":\"ULS\",\"Note\":\"\",\"Role\":\"\"}")!;
        Check(back.Single(item => item.Member == "C1").CableDesign == cable.CableDesign && !json.Contains("\"CableDesign\":null") && old.CableDesign is null,
            "the suggestion is saved with the study's member rows and read back; a study saved before it existed reads as no suggestion");

        // a slack cable: no stay, said plainly
        var slack = new MemberUtilizationCheck(_ => null, 1860, 0.5, TensionCableBasis.Default).Evaluate(candidate, new Dictionary<string, FemAnalysisResult> { ["ULS"] = Solved(0) }).Single(item => item.Member == "C1");
        Check(slack.CableDesign is null, "a cable with no tension has no suggested stay");
    }

    /// <summary>The Design window on the default model: its cables, Run Design on the FEM forces, a single stay, and the optimization window's column. Needs an STA thread; <c>--cable-design-ui</c>.</summary>
    public static void RunDialog()
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("/Mcotis;component/Presentation/Theme.xaml", UriKind.Relative) });
        var (vm, _, _, _) = FemModelFileChecks.DefaultModelApp();
        FemModelFileChecks.Analyze(vm);
        var options = vm.CableDesignOptions();
        Check(options.Count > 0 && options.All(option => option.LengthM > 0 && option.AreaMm2 > 0), $"the default model has {options.Count} tension cables with their length and steel area");
        var designs = vm.RunTensionCableDesign(vm.CableDesignBasis with { Members = options.Select(option => option.Member).ToArray(), LoadCases = vm.CableDesignDefaultCases() });
        Check(designs.Count == options.Count && designs.All(design => design.Status is "OK" or "CHECK" or "—") && designs.Any(design => design.Selected is not null) &&
              designs.Where(design => design.Selected is not null).All(design => design.Selected!.GroupCapacityKN >= design.TensionKN - 1e-6),
            $"Run Design on the model's FEM forces gives every cable with tension an assembly that carries it ({designs.Count(design => design.Selected is not null)} of {designs.Count}; " +
            $"{string.Join(", ", designs.GroupBy(design => design.Status).Select(group => $"{group.Key} × {group.Count()}"))})");

        var panel = new Mcotis.Desktop.Presentation.DesignResultsPanel();
        var column = vm.DesignBasis(ColumnDesignType.Steel) with { LoadCases = vm.DesignDefaultCases() };
        panel.Show(column, vm.RunColumnDesign(column), vm.DesignRunId);
        var columnReport = panel.WordReport();
        Check(columnReport.Rows.Count > 0 && columnReport.Rows.Count == columnReport.Calculations.Count && columnReport.Rows.All(row => row.Count == columnReport.Header.Count),
            $"the shared Design results panel (RC, CFT, steel encased, mega, steel, override) builds a Word report: {columnReport.Rows.Count} members, one calculation each");
        // the VCF system starts with the study's 10 … 120 MN VSL ladder as cable sections (73T15.7 … 906T15.7), once, with the model's cable material
        int[] strands = [73, 151, 218, 302, 374, 453, 507, 604, 676, 748, 845, 906];
        vm.EnsureSections();
        vm.EnsureSections();
        var ladder = vm.CableSections.Where(section => section.Source == TensionCableDesigner.StayLadderSource).ToArray();
        var cableMaterial = vm.FemSectionProperties.First(property => property.IsCable).MaterialName;
        Check(vm.CanOptimizeVcf && ladder.Select(section => section.Name).SequenceEqual(strands.Select(count => $"{count}T15.7")) &&
              ladder.All(section => Math.Abs(section.Area - int.Parse(section.Name.Split('T')[0]) * 150e-6) < 1e-12 && section.Material == cableMaterial) &&
              vm.CableSections.GroupBy(section => section.Name).All(group => group.Count() == 1) && vm.VcfSectionNames(cable: true).Contains("906T15.7"),
            "the VCF system's cable sections include the study's 12-step VSL schedule 73T15.7 … 906T15.7 (N × 150 mm², the model's cable material), once, offered to the optimization");
        vm.CableSections.Remove(vm.CableSections.First(section => section.Name == "151T15.7"));
        vm.EnsureSections();
        Check(vm.CableSections.All(section => section.Name != "151T15.7"), "a predefined section the user deletes is not added back");

        var window = new Mcotis.Desktop.Presentation.TensionCableDesignWindow(vm, []);
        window.Show();
        window.UpdateLayout();
        var buttons = Visuals<System.Windows.Controls.Button>(window).ToArray();
        var sectionStress = Visuals<System.Windows.Controls.TextBox>(window).Distinct().Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Cable check breaking stress MPa");
        var sectionReduction = Visuals<System.Windows.Controls.TextBox>(window).Distinct().Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Cable check reduction factor percent");
        var checkGrid = Visuals<System.Windows.Controls.DataGrid>(window).Distinct().Single(grid => System.Windows.Automation.AutomationProperties.GetName(grid) == "Cable section checks");
        var tabHeaders = Visuals<System.Windows.Controls.TabItem>(window).Distinct().Where(tab => tab.Visibility == System.Windows.Visibility.Visible).Select(tab => (string)tab.Header).ToArray();
        Check(window.Title == "Tension Cable Design" && tabHeaders.SequenceEqual(["Section Check", "Cost Rates", "Iterate"]) &&
              buttons.Any(button => Equals(button.Content, "Check Sections")) && buttons.Any(button => Equals(button.Content, "Iterate")) &&
              buttons.Any(button => Equals(button.Content, "Apply & Re-analyse")) && !buttons.Any(button => Equals(button.Content, "Run Design") && button.IsVisible) &&
              Visuals<System.Windows.Controls.DataGrid>(window).Distinct().Any(grid => System.Windows.Automation.AutomationProperties.GetName(grid) == "Iterated cable sections") &&
              sectionStress.Text == "1860" && sectionReduction.Text == "50" && checkGrid.Columns.Count == 9 && checkGrid.Items.Count == options.Count &&
              ResultReportTables.VslUnit(55) == "1 × 6-55" && ResultReportTables.VslUnit(50) == "1 × 6-55 (50 strands)" && ResultReportTables.VslUnit(200) == "2 × 6-109 (200 strands)",
            "the Tension Cable Design window shows Section Check on the VSL catalogue strand as Iterate (1860 MPa = 279 kN per Ø15.7 strand, 50 %, with each section's VSL unit, already checked on opening), Cost Rates and Iterate; the other tabs are hidden");

        // Iterate: VSL Ø15.7 stays from the Design Basis for the ticked cases, each pass a full analysis of a candidate model
        var iterateSettings = window.ReadIterationSettings()! with { MaximumIterations = 4 };
        var iterated = Pump(() => vm.RunCableSectionOptimizationAsync(iterateSettings, null, CancellationToken.None));
        static int StrandsOf(string product)
        {
            var match = System.Text.RegularExpressions.Regex.Match(product, @"^(\d+) × VSL SSI 2000 6-(\d+)$");
            return match.Success ? int.Parse(match.Groups[1].Value) * int.Parse(match.Groups[2].Value) : -1;
        }
        Check(iterated.Failure is null && iterated.IterationCount >= 1 && iterated.Rows.Count == options.Count &&
              iterated.Rows.All(row => row.Product is { } product && StrandsOf(product) == row.SuggestedStrands && row.CostSgd > 0) &&
              iterated.Rows.All(row => row.SuggestedUr <= iterateSettings.TargetUr + 1e-9 && iterateSettings.DesignCases!.Contains(row.GoverningCase)) &&
              iterated.Rows.GroupBy(row => row.GroupKey).All(group => group.Select(row => row.SuggestedStrands).Distinct().Count() == 1),
            $"Iterate sizes every cable as a standard VSL assembly for its governing design case, within the maximum use, one size per group " +
            $"({iterated.IterationCount} pass(es), {(iterated.Converged ? "converged" : "not converged")}; e.g. {iterated.Rows[0].TendonName}: {iterated.Rows[0].Product})");
        if (iterated.Rows.Any(row => row.CurrentStrands != row.SuggestedStrands))
        {
            var applied = Pump(() => vm.ApplyCableSectionOptimizationAndAnalyzeAsync(iterated, confirmedUnconverged: !iterated.Converged));
            var assigned = vm.CableDesignOptions().ToDictionary(option => option.Member, StringComparer.Ordinal);
            Check(iterateSettings.SyncGroupsOnly && applied && vm.HasFemResults && vm.IsFemModelLocked && iterated.Rows.All(row =>
                      assigned.TryGetValue(row.TendonName, out var option) && Math.Abs(option.AreaMm2 - row.SuggestedStrands * 150) < 1e-6 &&
                      option.Section == $"{row.SuggestedStrands}T15.7"),
                "Apply & Re-analyse assigns the suggested Ø15.7 strand sections and re-runs FEM analysis, so the results are those of the new sections");
        }
        window.Close();

        var optimization = new Mcotis.Desktop.Presentation.OptimizationResultsWindow(vm);
        optimization.Show();
        Check(optimization.MemberGrid.Columns.Any(column => column.Header is System.Windows.Controls.TextBlock { Text: "Suggested cable" }),
            "the Optimization results window's Member Utilization sheet has a Suggested cable column");
        optimization.Close();
    }

    /// <summary>Waits for a UI-thread task while the dispatcher keeps running its continuations.</summary>
    private static T Pump<T>(Func<Task<T>> start)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        try
        {
            var task = start();
            var frame = new System.Windows.Threading.DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            return task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static IEnumerable<T> Visuals<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        var logical = System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>().Concat(
            root is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? Enumerable.Range(0, System.Windows.Media.VisualTreeHelper.GetChildrenCount(root)).Select(index => System.Windows.Media.VisualTreeHelper.GetChild(root, index))
                : []);
        foreach (var child in logical.Distinct())
        {
            if (child is T found) yield return found;
            foreach (var inner in Visuals<T>(child)) yield return inner;
        }
    }

    private static bool Near(double value, double expected, double tolerance) => Math.Abs(value - expected) <= Math.Abs(expected) * tolerance;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
