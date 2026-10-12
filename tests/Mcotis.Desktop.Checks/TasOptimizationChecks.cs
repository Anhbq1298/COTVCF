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

        // the form's start: geometry fixed at the model, the loads of the storeys above over their ranges
        var start = TasOptimization.Start(d);
        Check(TasOptimization.Validate(start).Count == 0 && start.GeometryCount == 1 && start.ForceCount == 9 * 8 * 3 * 6 && start.CombinationCount == 1296,
            $"Optimization TAS starts from the model's geometry with SW 7–15 @ 1, SDL 3–10 @ 1, LL 5–10 @ 2.5, n 5–10 @ 1: {start.CombinationCount:N0} combinations");

        // one case with the model's own inputs is the model: its deck deflection is that of Run FEM Analysis
        var reference = EtabsImportChecks.NewViewModel(out _);
        reference.LoadTasModel();
        var run = FemModelFileChecks.Analyze(reference)[0].ResultFor(Sls);
        var deck = TasOptimizationRunner.TopChord(reference.State.Geometry, d).ToHashSet(StringComparer.Ordinal);
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

        // a section per role: the top chord tried with two sections gives two different answers
        var sections = one with { RoleSections = new Dictionary<TiedArchRole, IReadOnlyList<string>> { [TiedArchRole.Deck] = ["CFT Deck 700x400x20", "CFT Brace 700x700x25"] } };
        rows = Optimize(vm, sections);
        Check(rows.Length == 2 && vm.VcfOptimizationColumns.Single().Label == "Top chord (deck)" && Math.Abs(rows[0].MaxDeflectionMm!.Value - expected) < 1e-6 &&
              rows[1].MaxDeflectionMm is { } stiffer && Math.Abs(stiffer - expected) > 0.01,
            $"the top chord with its TAS section and with CFT Brace 700x700x25: {rows[0].Deflection} and {rows[1].Deflection} mm");

        // what the form refuses
        var tooFew = one with { Geometry = new Dictionary<string, VcfRange>(one.Geometry, StringComparer.Ordinal) { [TasOptimization.Bays.Key] = new(4, 12, 2) } };
        var lowDeck = one with { Geometry = new Dictionary<string, VcfRange>(one.Geometry, StringComparer.Ordinal) { [TasOptimization.DeckHeight.Key] = new(30, 33, 1) } };
        Check(TasOptimization.Validate(tooFew).Any(problem => problem.StartsWith("At the minimum", StringComparison.Ordinal) && problem.Contains("even", StringComparison.Ordinal)) &&
              TasOptimization.Validate(lowDeck).Any(problem => problem.Contains("H2", StringComparison.Ordinal)) &&
              TasOptimization.Validate(one with { SpanRatio = 0 }).Any(problem => problem.StartsWith("Deflection limit", StringComparison.Ordinal)),
            "bays below 6 at the minimum corner, H2 not above H1 and a span ratio of 0 are refused before the run");
    }

    /// <summary>The settings with every load fixed at the model's.</summary>
    private static TasOptimizationSettings Fixed(TasOptimizationSettings settings, TiedArchParameters p) => settings with
    {
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
