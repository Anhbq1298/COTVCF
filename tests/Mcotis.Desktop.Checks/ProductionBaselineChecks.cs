using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Design;
using Mcotis.Desktop.Application.Etabs;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Presentation;

/// <summary>
/// The production tools of the Optimization and Design menus, frozen before the main window moves onto the native
/// document (task of 2026-10-14, §2: "Baseline before M0"). <c>--baseline-capture</c> writes what they give today into
/// <see cref="FixturePath"/>; <c>--optimization-study</c> and <c>--column-design</c> (and <c>--full</c>) compare every
/// later build with it. A difference is a failure unless it is listed in the development log with its cause and
/// approved by the user — then the fixture is captured again in the same change.
/// <list type="bullet">
/// <item>Optimization VCF on the default VCF model with the user guide's nine-case walkthrough (truss depth 18, 20,
/// 22 m × LL_ABOVE 5, 7.5, 10 kPa; the form's own defaults otherwise), read from the real form.</item>
/// <item>Optimization TAS on the built-in tied arch system with its walkthrough (h 2, 3, 4 m × LL 5, 7.5, 10 kPa).</item>
/// <item>The study report of each run (<see cref="OptimizationStudy"/>): sensitivities, summary — where a flat
/// parameter is named —, conclusions and the figures.</item>
/// <item>Column design and column design optimization of the four column types on the guide's illustrative C1 / C2
/// forces, and Run Design on the FEM forces of the default model.</item>
/// </list>
/// Numbers that come out of an analysis are compared to round-off (relative 1E-7 of the larger of the value and 1);
/// everything else exactly (text) or to floating-point round-off (1E-12). The time per optimization case is recorded
/// and compared, but never fails a check: a study more than 1.5 times slower is reported.
/// </summary>
internal static class ProductionBaselineChecks
{
    internal const string FixturePath = "tests/Mcotis.Desktop.Checks/Fixtures/production-baselines.json";
    private const double AnalysisTolerance = 1e-7, ExactTolerance = 1e-12, SlowerFactor = 1.5;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>One frozen value: a number or a text; <see cref="Analysis"/> marks a number an analysis produced.</summary>
    internal sealed record Entry(string Key, double? Number = null, string? Text = null, bool Analysis = false);

    internal sealed record Fixture(string Captured, string Note, IReadOnlyDictionary<string, double> MillisecondsPerCase, IReadOnlyList<Entry> Entries);

    /// <summary>Writes the fixture from the current build. Opt-in: <c>--baseline-capture</c>.</summary>
    public static void Capture()
    {
        var timing = new Dictionary<string, double>(StringComparer.Ordinal);
        var entries = new List<Entry>();
        entries.AddRange(Vcf(timing));
        entries.AddRange(Tas(timing));
        entries.AddRange(Design());
        var fixture = new Fixture(DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "Production baselines of the Optimization and Design menus (task 2026-10-14 §2). Re-capture only with a difference the user approved and the development log records.",
            timing, entries);
        Directory.CreateDirectory(Path.GetDirectoryName(FixturePath)!);
        File.WriteAllText(FixturePath, JsonSerializer.Serialize(fixture, Json));
        Console.WriteLine($"Captured {entries.Count:N0} baseline values into {FixturePath}; ms per case: {string.Join(", ", timing.Select(pair => $"{pair.Key} {pair.Value:0.0}"))}.");
    }

    /// <summary>Optimization VCF and Optimization TAS (with their study reports) against the fixture.</summary>
    public static void RunOptimization()
    {
        var fixture = Read();
        var timing = new Dictionary<string, double>(StringComparer.Ordinal);
        Compare(fixture, "vcf/", Vcf(timing), "Optimization VCF on the default VCF model, the guide's nine cases (depth 18–22 × LL_ABOVE 5–10): every row, the summary line and the study report");
        Compare(fixture, "tas/", Tas(timing), "Optimization TAS on the built-in tied arch system, the guide's nine cases (h 2–4 × LL 5–10): every row, the summary line and the study report");
        foreach (var (study, ms) in timing)
        {
            var before = fixture.MillisecondsPerCase.GetValueOrDefault(study);
            var ratio = before > 0 ? ms / before : double.NaN;
            Console.WriteLine(ratio > SlowerFactor
                ? $"NOTE: {study} takes {ms:0.0} ms per case against {before:0.0} ms at the baseline ({ratio:0.00}×) — more than {SlowerFactor}× slower: report it and propose a fix before merging."
                : $"INFO: {study} takes {ms:0.0} ms per case (baseline {before:0.0} ms, {ratio:0.00}×).");
        }
    }

    /// <summary>Column design and column design optimization against the fixture.</summary>
    public static void RunDesign() =>
        Compare(Read(), "design/", Design(), "Column design and column design optimization of the four types on the guide's C1 / C2 forces, and Run Design on the FEM forces of the default model");

    private static Fixture Read()
    {
        if (!File.Exists(FixturePath)) throw new Exception($"FAIL: the production baselines are missing ({FixturePath}); capture them with --baseline-capture on the build before the change");
        return JsonSerializer.Deserialize<Fixture>(File.ReadAllText(FixturePath), Json)!;
    }

    private static void Compare(Fixture fixture, string prefix, IReadOnlyList<Entry> now, string name)
    {
        var expected = fixture.Entries.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        var actual = now.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var key in expected.Keys.Except(actual.Keys)) problems.Add($"{key} is no longer produced");
        foreach (var key in actual.Keys.Except(expected.Keys)) problems.Add($"{key} is new");
        double worst = 0;
        foreach (var (key, then) in expected)
        {
            if (!actual.TryGetValue(key, out var value)) continue;
            if (then.Text is not null || value.Text is not null)
            {
                if (!string.Equals(then.Text, value.Text, StringComparison.Ordinal)) problems.Add($"{key}: '{value.Text}' instead of '{then.Text}'");
                continue;
            }
            double a = value.Number ?? double.NaN, b = then.Number ?? double.NaN;
            if (a.Equals(b)) continue;     // the same, NaN and ∞ included
            var difference = Math.Abs(a - b) / Math.Max(1, Math.Abs(b));
            worst = Math.Max(worst, difference);
            if (!(difference <= (then.Analysis ? AnalysisTolerance : ExactTolerance))) problems.Add($"{key}: {a:R} instead of {b:R}");
        }
        if (problems.Count > 0)
            throw new Exception($"FAIL: {name} — {problems.Count} difference(s) from {FixturePath}: {string.Join(" | ", problems.Take(8))}{(problems.Count > 8 ? " | …" : "")}");
        Console.WriteLine($"PASS: {name} — {expected.Count:N0} values as at the baseline (largest relative difference {worst:0.#E+0})");
    }

    // ---- Optimization VCF: the walkthrough, read from the real form ----

    private static IReadOnlyList<Entry> Vcf(Dictionary<string, double> timing)
    {
        EnsureApplication();
        var (vm, state, _, _) = FemModelFileChecks.DefaultModelApp();
        var live = state.LoadDefinitions.Single(load => load.ApplyIncrementStep).LoadName;
        var dead = state.LoadDefinitions.Single(load => !load.ApplyIncrementStep).LoadName;
        var form = new OptimizationVcfWindow(vm);
        form.SetRange(VcfOptimization.Depth.Key, "min", "18"); form.SetRange(VcfOptimization.Depth.Key, "max", "22"); form.SetRange(VcfOptimization.Depth.Key, "step", "2");
        form.SetRange(live, "min", "5", "storey"); form.SetRange(live, "max", "10", "storey"); form.SetRange(live, "step", "2.5", "storey");
        form.SetRange(dead, "min", "10", "storey"); form.SetRange(dead, "max", "10", "storey");
        form.SetRange(VcfOptimization.StoriesKey, "min", "10", "storey"); form.SetRange(VcfOptimization.StoriesKey, "max", "10", "storey");
        form.SetRange(VcfOptimization.TributaryKey, "min", "12", "storey"); form.SetRange(VcfOptimization.TributaryKey, "max", "12", "storey");
        var settings = form.FormSettings() with { SpanRatio = 250, UpliftLimitMm = 0 };
        form.Close();
        var problems = VcfOptimization.Validate(VcfGeometry.Analyze(state.Geometry, state.Truss), settings);
        if (problems.Count > 0 || settings.CombinationCount != 9)
            throw new Exception($"FAIL: the VCF walkthrough is not the guide's nine cases ({settings.CombinationCount} combinations; {string.Join(" ", problems)})");
        var watch = Stopwatch.StartNew();
        Pump(() => vm.RunVcfOptimizationAsync(settings));
        timing["Optimization VCF"] = watch.Elapsed.TotalMilliseconds / settings.CombinationCount;
        return Rows("vcf/", vm, settings.DeflectionResult);
    }

    // ---- Optimization TAS: the walkthrough of the guide (TasGuideCapture) ----

    /// <summary>Optimization TAS analysed in Full 3D (M9: what the app starts the tied arch system in) against its baseline,
    /// captured in the XZ plane: every case the same to round-off, and the time per case against the budget.</summary>
    internal static void RunTasInFullThreeD()
    {
        var fixture = Read();
        var timing = new Dictionary<string, double>(StringComparer.Ordinal);
        Compare(fixture, "tas/", Tas(timing, Mcotis.Core.DofMask.Full3D),
            "Optimization TAS in Full 3D — each case of the guide's nine (h 2–4 × LL 5–10) analysed with all six degrees of freedom — against its baseline in the XZ plane: every row, the summary line and the study report");
        var ms = timing["Optimization TAS"];
        var before = fixture.MillisecondsPerCase.GetValueOrDefault("Optimization TAS");
        var ratio = before > 0 ? ms / before : double.NaN;
        Console.WriteLine(ratio > SlowerFactor
            ? $"NOTE: Optimization TAS in Full 3D takes {ms:0.0} ms per case against {before:0.0} ms at the baseline in the plane ({ratio:0.00}×) — more than {SlowerFactor}× slower: report it and propose a fix before merging."
            : $"INFO: Optimization TAS in Full 3D takes {ms:0.0} ms per case (baseline in the plane {before:0.0} ms, {ratio:0.00}×).");
    }

    private static IReadOnlyList<Entry> Tas(Dictionary<string, double> timing, Mcotis.Core.DofMask? dofs = null)
    {
        var vm = EtabsImportChecks.NewViewModel(out _);
        vm.LoadTasModel();
        if (dofs is { } active) vm.ApplyPlanarAnalysisSettings(vm.PlanarAnalysisSettings with { ActiveDofs = active, CableDofs = Mcotis.Core.DofMask.Translations });
        var p = TiedArchParameters.Default;
        var settings = TasOptimization.Start(p) with
        {
            Geometry = new Dictionary<string, VcfRange>
            { ["H1"] = new(30, 30, 1), ["h"] = new(2, 4, 1), ["Bays"] = new(12, 12, 2), ["Spacing"] = new(10, 10, 1) },
            Loads = new Dictionary<string, VcfRange>
            { ["SW"] = new(7, 7, 1), ["SDL"] = new(3, 3, 1), ["LL"] = new(5, 10, 2.5), ["Floors"] = new(10, 10, 1), ["B"] = new(12, 12, 1) },
            DeflectionResult = "SLS1 G+Q full", SpanRatio = 250, UpliftLimitMm = 0,
        };
        if (settings.CombinationCount != 9 || TasOptimization.Validate(settings).Count > 0)
            throw new Exception("FAIL: the TAS walkthrough is not the guide's nine cases");
        var watch = Stopwatch.StartNew();
        Pump(() => vm.RunTasOptimizationAsync(settings));
        timing["Optimization TAS"] = watch.Elapsed.TotalMilliseconds / settings.CombinationCount;
        return Rows("tas/", vm, settings.DeflectionResult);
    }

    /// <summary>Every row of the results table, the summary line and the study report of the run in <paramref name="vm"/>.</summary>
    private static IReadOnlyList<Entry> Rows(string prefix, MainViewModel vm, string? deflectionResult)
    {
        var entries = new List<Entry>
        {
            new(prefix + "deflectionResult", Text: deflectionResult ?? ""),
            new(prefix + "columns", Text: string.Join(" | ", vm.VcfOptimizationColumns.Select(axis => axis.Label))),
            new(prefix + "rows", vm.VcfOptimizationRows.Count),
            new(prefix + "best", Text: vm.VcfOptimizationBest),
            new(prefix + "progress", Text: vm.VcfOptimizationProgress),
        };
        foreach (var row in vm.VcfOptimizationRows)
        {
            var key = $"{prefix}case{row.Case:00}/";
            entries.Add(new(key + "inputs", Text: string.Join(" | ", row.Values)));
            entries.Add(new(key + "deflectionMm", row.MaxDeflectionMm, Analysis: true));
            entries.Add(new(key + "upliftMm", row.MaxUpliftMm, Analysis: true));
            entries.Add(new(key + "utilisation", row.Utilisation, Analysis: true));
            entries.Add(new(key + "costSgd", row.CostSgd));
            entries.Add(new(key + "point", Text: row.Point));
            entries.Add(new(key + "upliftPoint", Text: row.UpliftPoint));
            entries.Add(new(key + "governing", Text: row.Governing));
            entries.Add(new(key + "span", Text: row.Span));
            entries.Add(new(key + "limit", Text: row.Limit));
            entries.Add(new(key + "check", Text: row.Check));
            entries.Add(new(key + "status", Text: row.Status));
        }
        var report = vm.OptimizationReport;
        foreach (var s in report.Sensitivities)
        {
            var key = $"{prefix}sensitivity/{s.Metric} ~ {s.Parameter}/";
            entries.Add(new(key + "swingPercent", s.SwingPercent, Analysis: true));
            entries.Add(new(key + "elasticity", s.Elasticity, Analysis: true));
            entries.Add(new(key + "rSquared", s.RSquared, Analysis: true));
            entries.Add(new(key + "direction", s.Direction));
            entries.Add(new(key + "turningPointX", s.TurningPointX, Analysis: true));
        }
        for (int i = 0; i < report.Summary.Count; i++)
            entries.Add(new($"{prefix}summary/{i:00} {report.Summary[i].Heading}", Text: report.Summary[i].Text));
        for (int i = 0; i < report.Conclusions.Count; i++) entries.Add(new($"{prefix}conclusion/{i:00}", Text: report.Conclusions[i]));
        foreach (var chart in report.Charts)
        {
            var key = $"{prefix}figure/{chart.Number:00}/";
            entries.Add(new(key + "title", Text: chart.Title));
            entries.Add(new(key + "caption", Text: chart.Caption));
            int n = 0;
            foreach (var series in chart.Series)
                foreach (var (x, y) in series.Points) entries.Add(new($"{key}{n++:000}", y, Analysis: true));
        }
        return entries;
    }

    // ---- Design → column design and its optimization ----

    private static IReadOnlyList<Entry> Design()
    {
        var entries = new List<Entry>();
        // the guide's illustrative forces (docs/USER_GUIDE.md, "Column design optimization"; ColumnGuideCapture)
        DesignMemberInput[] guide =
        [
            new("C1", "CFT example", 6, [new("ULS1", [new(-12000, 300, 900), new(-12000, 300, -450)])]),
            new("C2", "CFT example", 6, [new("ULS1", [new(-9000, 500, 1400), new(-9000, 500, -700)])]),
        ];
        var rates = new ColumnOptimizationInput
        {
            SteelRates = ColumnGrades.Steel.ToDictionary(grade => grade.Name, grade => ColumnGrades.SteelRate(grade, [])),
            ConcreteRates = ColumnGrades.Concrete.ToDictionary(grade => grade.Name, grade => ColumnGrades.ConcreteRate(grade, [])), Keep = 10,
        };
        var cases = new (ColumnDesignType Type, double Length, ColumnSectionInput? Section, ColumnOptimizationInput Ranges)[]
        {
            (ColumnDesignType.ConcreteFilledTube, 6, new ColumnSectionInput { Tube = TubeShape.Rectangular, TubeDepth = 600, TubeWidth = 600, TubeThickness = 16, Fck = 60, Fy = 355 },
                rates with { Depth = new(400, 900, 100), Thickness = new(10, 30, 5), SteelFrom = "S355", SteelTo = "S460", ConcreteFrom = "C50/60", ConcreteTo = "C70/85" }),
            (ColumnDesignType.ReinforcedConcrete, 6, null, rates with
            {
                Type = ColumnDesignType.ReinforcedConcrete, OutlineDepth = new(300, 900, 50), ConcreteFrom = "C30/37", ConcreteTo = "C60/75",
                BarDiameters = [20, 25, 32], BarsPerFaceMin = 2, BarsPerFaceMax = 5, Cover = 40,
            }),
            (ColumnDesignType.SteelEncased, 6, null, rates with
            {
                Type = ColumnDesignType.SteelEncased, OutlineDepth = new(600, 900, 100), ProfileDepth = new(300, 500, 100), ProfileWidth = new(300, 400, 100),
                FlangeThickness = new(20, 40, 10), WebThickness = new(15, 25, 10), SteelFrom = "S355", SteelTo = "S460", ConcreteFrom = "C40/50", ConcreteTo = "C60/75",
                BarDiameters = [25, 32], BarsPerFaceMin = 3, BarsPerFaceMax = 4, Cover = 40,
            }),
            (ColumnDesignType.MegaComposite, 20, null, rates with
            {
                Type = ColumnDesignType.MegaComposite, OutlineDepth = new(1800, 2400, 200), ProfileDepth = new(500, 500, 50), ProfileWidth = new(430, 430, 50),
                FlangeThickness = new(40, 60, 20), WebThickness = new(37, 37, 10), ProfileCounts = [4], ProfileEdge = 160,
                SteelFrom = "S460", SteelTo = "S460", ConcreteFrom = "C80/95", ConcreteTo = "C100/115", BarDiameters = [40], BarsPerFaceMin = 13, BarsPerFaceMax = 17, Cover = 50,
            }),
        };
        foreach (var (type, length, section, ranges) in cases)
        {
            var basis = ColumnDesignCodes.Default(type) with
            {
                Members = ["C1", "C2"], LoadCases = ["ULS1"], EffectiveLengthY = length, EffectiveLengthZ = length,
                Section = section ?? ColumnDesignCodes.Default(type).Section,
            };
            var members = guide.Select(member => member with { LengthM = length }).ToArray();
            foreach (var member in members) Designed(entries, $"design/guide/{type}/{member.Member}/", ColumnDesignEngine.Design(basis, member), analysis: false);
            var run = ColumnOptimizer.Run(ranges with { Type = type }, basis, members);
            var key = $"design/optimization/{type}/";
            entries.Add(new(key + "candidates", run.Candidates));
            entries.Add(new(key + "evaluated", run.Evaluated));
            entries.Add(new(key + "passing", run.Best.Count));
            foreach (var best in run.Best)
            {
                var rank = $"{key}rank{best.Rank:00}/";
                entries.Add(new(rank + "section", Text: $"{best.Candidate.Size} · {best.Candidate.SteelGrade} / {best.Candidate.ConcreteGrade} · {best.Candidate.Rebar}"));
                entries.Add(new(rank + "costPerM", best.Candidate.CostPerM));
                entries.Add(new(rank + "utilisation", best.Check.MaxUtilization));
                entries.Add(new(rank + "governing", Text: best.Check.Governing));
            }
        }

        // Run Design on the FEM forces of the default model: every type with members its sections suit
        var (vm, _, _, _) = FemModelFileChecks.DefaultModelApp();
        FemModelFileChecks.Analyze(vm);
        // (the frozen answers are those of the four column designs; the steel beam / column design is checked by --steel-design)
        foreach (var type in Enum.GetValues<ColumnDesignType>().Where(type => type != ColumnDesignType.Steel))
        {
            var basis = vm.DesignBasis(type) with { EffectiveLengthY = 10, EffectiveLengthZ = 10, LoadCases = vm.DesignDefaultCases() };
            entries.Add(new($"design/model/{type}/members", Text: string.Join(" | ", basis.Members)));
            foreach (var design in vm.RunColumnDesign(basis)) Designed(entries, $"design/model/{type}/{design.Member}/", design, analysis: true);
        }
        return entries;
    }

    private static void Designed(List<Entry> entries, string key, ColumnMemberDesign design, bool analysis)
    {
        entries.Add(new(key + "status", Text: design.Status));
        entries.Add(new(key + "maxUtilisation", double.IsFinite(design.MaxUtilization) ? design.MaxUtilization : null, Analysis: analysis));
        if (design.Governing is not { } g) return;
        entries.Add(new(key + "governing", Text: g.Case));
        entries.Add(new(key + "NEd", g.NEdKN, Analysis: analysis));
        entries.Add(new(key + "MEdY", g.MEdYKNm, Analysis: analysis));
        entries.Add(new(key + "MEdZ", g.MEdZKNm, Analysis: analysis));
        entries.Add(new(key + "VEd", g.VEdKN, Analysis: analysis));
        entries.Add(new(key + "axial", g.AxialUtilization, Analysis: analysis));
        entries.Add(new(key + "major", g.MajorUtilization, Analysis: analysis));
        entries.Add(new(key + "minor", g.MinorUtilization, Analysis: analysis));
        entries.Add(new(key + "shear", g.ShearUtilization, Analysis: analysis));
    }

    // ---- plumbing ----

    /// <summary>The optimization windows need the application's resources: create the application when no check has, and
    /// give it the theme when the one another check created has none. Closing the form must not end it.</summary>
    internal static void EnsureApplication()
    {
        var app = System.Windows.Application.Current ?? new System.Windows.Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (app.TryFindResource("Surface") is null)
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Mcotis;component/Presentation/Theme.xaml", UriKind.Relative) });
    }

    /// <summary>Runs an optimization with a dispatcher, so its progress reports land in the table as they do in the app.</summary>
    private static void Pump(Func<Task> start)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            var task = start();
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
}
