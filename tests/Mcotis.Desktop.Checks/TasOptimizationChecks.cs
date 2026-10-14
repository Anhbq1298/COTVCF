using System.Windows.Threading;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Etabs;
using Mcotis.Desktop.Presentation;

/// <summary>
/// Optimization → Optimization TAS, offline: the search space of the tied arch system, run case by case with the internal
/// FEM through the same run loop and results table as Optimization VCF. A case with the open model's inputs reproduces
/// Run FEM Analysis of File → Load TAS…; cases that cannot be built are skipped with the reason; section candidates per
/// role change the result; the open model is never changed. Part of <c>--etabs-import</c> and the default suite.
/// </summary>
internal static class TasOptimizationChecks
{
    private const string Sls = "SLS1 G+Q full";

    public static void Run()
    {
        var d = TiedArchParameters.Default;

        // Mcotis opens with the tied arch system (App startup), ready to run
        var opened = EtabsImportChecks.NewViewModel(out _);
        opened.LoadStartupModel();
        Check(opened.TiedArchParameters == d && opened.OfflineModelName == "TAS - Tied Arch System" && opened.CanRunFemAnalysis &&
              opened.State.Geometry.Points.Count == 30 && opened.State.Geometry.Members.Count == 59,
            $"Mcotis opens with the tied arch system ({opened.OfflineModelName}: {opened.State.Geometry.Points.Count} joints, {opened.State.Geometry.Members.Count} frames), ready to run");

        CheckElevatedCable(d);

        // the form's start: geometry fixed at the model, the loads of the storeys above over their ranges
        var start = TasOptimization.Start(d);
        Check(TasOptimization.Validate(start).Count == 0 && start.GeometryCount == 4 && start.Geometry["h"] == new VcfRange(2, 5, 1) && start.ForceCount == 9 * 8 * 3 * 6 &&
              start.CombinationCount == 4 * 1296 && TasOptimization.ParametersAt(start, range => range.Max).DeckHeight == 35,
            $"Optimization TAS starts from the model's geometry with h 2–5 @ 1 (H2 = H1 + h: 32 … 35 m) and SW 7–15 @ 1, SDL 3–10 @ 1, LL 5–10 @ 2.5, n 5–10 @ 1: {start.CombinationCount:N0} combinations");

        // one case with the model's own inputs is the model: its deck deflection is that of Run FEM Analysis
        var reference = EtabsImportChecks.NewViewModel(out _);
        reference.LoadTasModel();
        var run = FemModelFileChecks.Analyze(reference)[0].ResultFor(Sls);
        var deck = TasOptimizationStudy.TopChord(reference.State.Geometry, d).ToHashSet(StringComparer.Ordinal);
        var expected = run.Joints.Where(joint => deck.Contains(joint.PointName)).Max(joint => -joint.UZM * 1000);

        var vm = EtabsImportChecks.NewViewModel(out _);
        vm.LoadTasModel();
        var before = vm.State.Geometry;
        var one = Fixed(start, d) with { DeflectionResult = Sls };
        var rows = Optimize(vm, one);
        Check(rows.Length == 1 && rows[0].MaxDeflectionMm is { } mm && Math.Abs(mm - expected) < 1e-6 && rows[0].Governing == Sls &&
              rows[0].Span == "120" && rows[0].Limit == "480.0" && rows[0].Check is "OK" or "NG" && vm.OptimizationTitle == "Optimization TAS" &&
              ReferenceEquals(vm.State.Geometry, before),
            $"a case with the model's inputs deflects as Run FEM Analysis of Load TAS ({rows.FirstOrDefault()?.Deflection} mm vs {expected:0.0} mm at {rows.FirstOrDefault()?.Point}; " +
            $"limit L/250 = 480 mm, {rows.FirstOrDefault()?.Check}); the open model is unchanged");

        // the geometry varies; an odd number of bays cannot be built and is skipped with the reason
        var geometry = one with
        {
            Geometry = new Dictionary<string, VcfRange>(one.Geometry, StringComparer.Ordinal)
            {
                [TasOptimization.ArchRise.Key] = new(25, 30, 5), [TasOptimization.Bays.Key] = new(10, 12, 1),
            },
        };
        rows = Optimize(vm, geometry);
        var odd = rows.Where(row => row.Values[vm.VcfOptimizationColumns.ToList().FindIndex(axis => axis.Key == "geometry:Bays")] == "11").ToArray();
        var same = rows.Single(row => row.Values.SequenceEqual(["30", "12"]));
        Check(rows.Length == 6 && vm.VcfOptimizationColumns.Select(axis => axis.Label).SequenceEqual(["H1 [m]", "Bays"]) &&
              odd.Length == 2 && odd.All(row => row.MaxDeflectionMm is null && row.Status.Contains("even", StringComparison.Ordinal)) &&
              rows.Count(row => row.MaxDeflectionMm is not null) == 4 && Math.Abs(same.MaxDeflectionMm!.Value - expected) < 1e-6 &&
              rows.Single(row => row.Values.SequenceEqual(["25", "10"])).Span == "100",
            $"H1 25 … 30 × bays 10 … 12: 6 cases, the 2 with 11 bays skipped ({odd.FirstOrDefault()?.Status}), the H1 30 / 12 bays case is the model");
        // the Chart sheet of the results window: the run's parametric study, utilisation against L / ratio
        var study = vm.OptimizationReport;
        Check(study.Charts.Count > 0 && study.Charts.Select(chart => chart.Number).SequenceEqual(Enumerable.Range(1, study.Charts.Count)) &&
              study.Charts.All(chart => chart.Series.Any(series => series.Points.Count > 0) && chart.Series.All(series => series.Points.All(point => double.IsFinite(point.Y)))) &&
              study.Charts.Any(chart => chart.XTitle.Contains("H1", StringComparison.Ordinal)) &&
              study.Charts.Any(chart => chart.YTitle.Contains("L / 250", StringComparison.Ordinal) || chart.Title.Contains("L / 250", StringComparison.Ordinal)) &&
              study.Summary.Count > 0,
            $"after Optimization TAS runs, the Chart sheet holds {study.Charts.Count} numbered figures of the study (e.g. Figure 1: {study.Charts.FirstOrDefault()?.Title}) and a summary");

        // a section per role: the top chord tried with two sections gives two different answers
        var sections = one with { RoleSections = new Dictionary<TiedArchRole, IReadOnlyList<string>> { [TiedArchRole.Deck] = ["CFT Deck 700x400x20", "CFT Brace 700x700x25"] } };
        rows = Optimize(vm, sections);
        Check(rows.Length == 2 && vm.VcfOptimizationColumns.Single().Label == "Top chord (deck)" && Math.Abs(rows[0].MaxDeflectionMm!.Value - expected) < 1e-6 &&
              rows[1].MaxDeflectionMm is { } stiffer && Math.Abs(stiffer - expected) > 0.01,
            $"the top chord with its TAS section and with CFT Brace 700x700x25: {rows[0].Deflection} and {rows[1].Deflection} mm");

        // what the form refuses
        var tooFew = one with { Geometry = new Dictionary<string, VcfRange>(one.Geometry, StringComparer.Ordinal) { [TasOptimization.Bays.Key] = new(4, 12, 2) } };
        var lowDeck = one with { Geometry = new Dictionary<string, VcfRange>(one.Geometry, StringComparer.Ordinal) { [TasOptimization.DeckRise.Key] = new(0, 3, 1) } };
        Check(TasOptimization.Validate(tooFew).Any(problem => problem.StartsWith("At the minimum", StringComparison.Ordinal) && problem.Contains("even", StringComparison.Ordinal)) &&
              TasOptimization.Validate(lowDeck).Any(problem => problem.Contains("H2", StringComparison.Ordinal)) &&
              TasOptimization.Validate(one with { SpanRatio = 0 }).Any(problem => problem.StartsWith("Deflection limit", StringComparison.Ordinal)),
            "bays below 6 at the minimum corner, H2 not above H1 and a span ratio of 0 are refused before the run");
    }

    /// <summary>Edit → Tied Arch System with an elevated cable: the arch springs at H0 on columns of its own section, pinned
    /// on the ground; the tie joins the springing points, meets its target in the Pretension case, and the other cases
    /// continue from that prestressed state.</summary>
    private static void CheckElevatedCable(TiedArchParameters d)
    {
        var p = d with { ElevatedCable = true, CableHeight = 10, CablePretension = 20_000 };
        var vm = EtabsImportChecks.NewViewModel(out _);
        var summary = vm.ApplyTiedArchSystem(p);
        var g = vm.State.Geometry;
        var tie = g.Members.SingleOrDefault(member => member.Kind == Mcotis.Desktop.Domain.MemberKind.Cable);
        var columns = g.Members.Where(member => member.Name is "COLL-0" or "COLR-0").ToArray();
        var sectionOf = vm.FemSectionProperties.ToDictionary(property => property.MemberName, property => property.SectionName);
        Check(summary.ReadyToRun && vm.TiedArchParameters == p && tie is { Name: "TIE", Start: "1@EL10", End: "2@EL10" } && columns.Length == 2 &&
              columns.All(column => sectionOf[column.Name] == "RSC 3.52x2.64 P" && g.Points[column.Start].Position.Z == 0 && g.Points[column.End].Position.Z == 10) &&
              sectionOf["TIE"] == "192T15.7" && vm.State.Cables.Single() is { CableName: "TIE", LoadPattern: "Pretension", TargetForce: 20_000 } &&
              FemStudyService.FindPinSupportPoints(g).SequenceEqual(["1@Base", "2@Base"]) &&
              g.Points.Values.Max(point => point.Position.Z) == 43 && g.Points.ContainsKey("8@EL40"),
            $"with an elevated cable the arch springs at H0 = 10 m on columns of the arch section pinned on the ground, the tie TIE (192T15.7) joins " +
            $"1@EL10 and 2@EL10 with a 20,000 kN target, the crown is at 40 m and the deck at 43 m — {string.Join(" | ", summary.Lines.Where(line => line.StartsWith("Tab 03", StringComparison.Ordinal) || line.StartsWith("Ready", StringComparison.Ordinal) || line.StartsWith("Not ready", StringComparison.Ordinal)))}");

        var rise = ArchRise.Find(g, vm.FemPinSupportPoints, member => sectionOf.GetValueOrDefault(member));
        Check(rise is { Support: "1@EL10", Crown: "8@EL40", Rise: 30 },
            $"the viewer still dimensions H1 = {rise?.Rise:0.###} m, from the springing {rise?.Support} (on its column) to the crown {rise?.Crown}");

        var sls = vm.FemCaseRows.SingleOrDefault(row => row.Name == Sls);
        Check(vm.FemCaseRows.Single(row => row.Name == "Pretension").CableTargetForces && sls is not null && sls.InitialCase == "Pretension" &&
              vm.FemCombinationRows.All(row => row.Name != Sls),
            $"the Pretension case meets the tie's target; {Sls} is a case continuing from it ({sls?.Definition})");

        var runs = FemModelFileChecks.Analyze(vm);
        Check(runs.Count == 1, "the elevated system solves: " + vm.FemProgressText);
        var run = runs[0];
        var pretension = run.ResultFor("Pretension").Cables.Single(cable => cable.MemberName == "TIE").AxialForceKN;
        var loaded = run.ResultFor(Sls);
        var tieLoaded = loaded.Cables.Single(cable => cable.MemberName == "TIE").AxialForceKN;
        var deck = TasOptimizationStudy.TopChord(g, p).ToHashSet(StringComparer.Ordinal);
        var deflection = loaded.Joints.Where(joint => deck.Contains(joint.PointName)).Max(joint => -joint.UZM * 1000);
        Check(Math.Abs(pretension - 20_000) <= 20_000 * vm.FemTargetFitPercent / 100 + 1 && tieLoaded > pretension && deflection > 0,
            $"the run meets the tie's target in Pretension ({pretension:N0} kN) and the loaded {Sls} pulls it harder ({tieLoaded:N0} kN); deck deflection {deflection:0.0} mm");

        // Optimization TAS on the elevated system: H0 and the tie's pretension are in its space; a case with the model's
        // inputs is the model, and the pretension steps as the VCF cables do
        var start = TasOptimization.Start(p);
        var one = Fixed(start, p) with { DeflectionResult = Sls };
        Check(start.ElevatedCable && start.Geometry.ContainsKey("H0") && start.CableForce.IsFixed && start.CableForce.Min == 20_000 && TasOptimization.Validate(one).Count == 0,
            "Optimization TAS of the elevated system has H0 among its geometry ranges and the tie's 20,000 kN pretension");
        var rows = Optimize(vm, one);
        Check(rows.Length == 1 && rows[0].MaxDeflectionMm is { } same && Math.Abs(same - deflection) < 1e-6 && rows[0].Status.StartsWith("Analysed", StringComparison.Ordinal),
            $"a case with the elevated system's inputs deflects as Run FEM Analysis ({rows.FirstOrDefault()?.Deflection} mm vs {deflection:0.0} mm; {rows.FirstOrDefault()?.Status})");
        rows = Optimize(vm, one with { CableForce = new VcfForceSpan(10_000, 30_000), CableSteps = 3 });
        Check(rows.Length == 3 && vm.VcfOptimizationColumns.Single().Label == "TIE [kN]" && rows.Select(row => row.Values[0]).SequenceEqual(["10000", "20000", "30000"]) &&
              Math.Abs(rows[1].MaxDeflectionMm!.Value - deflection) < 1e-6 && rows.Select(row => row.MaxDeflectionMm!.Value).Distinct().Count() == 3,
            $"the tie's pretension 10,000 … 30,000 kN in 3 steps: {string.Join(" / ", rows.Select(row => row.Deflection))} mm");
    }

    /// <summary>The settings with h and every load fixed at the model's.</summary>
    private static TasOptimizationSettings Fixed(TasOptimizationSettings settings, TiedArchParameters p) => settings with
    {
        Geometry = new Dictionary<string, VcfRange>(settings.Geometry, StringComparer.Ordinal)
        {
            [TasOptimization.DeckRise.Key] = VcfRange.Fixed(p.DeckHeight - p.ArchRise, 1),
        },
        Loads = new Dictionary<string, VcfRange>(StringComparer.Ordinal)
        {
            [TasOptimization.SelfWeight.Key] = VcfRange.Fixed(p.SelfWeight, 1), [TasOptimization.SuperimposedDead.Key] = VcfRange.Fixed(p.SuperimposedDead, 1),
            [TasOptimization.Live.Key] = VcfRange.Fixed(p.Live, 2.5), [TasOptimization.Floors.Key] = VcfRange.Fixed(p.Floors, 1),
            [TasOptimization.Width.Key] = VcfRange.Fixed(p.TributaryWidth, 1),
        },
    };

    /// <summary>Runs Optimization TAS with a dispatcher, so the progress reports land in the table as they do in the app.</summary>
    private static VcfOptimizationRow[] Optimize(MainViewModel vm, TasOptimizationSettings settings)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            var task = vm.RunTasOptimizationAsync(settings);
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        return vm.VcfOptimizationRows.ToArray();
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
