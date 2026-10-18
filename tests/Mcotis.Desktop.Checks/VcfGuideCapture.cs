using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Presentation;

/// <summary>
/// Opt-in regeneration of the screenshots in the Optimization VCF guide article that come before the optimization form:
/// Edit → VCF System, Define → Materials, Frame / Cable Sections (one form per element type), Run → Analysis Options, the
/// run monitor of a real run, and the Design windows. Every image is a real window of the default VCF model.
/// </summary>
internal static class VcfGuideCapture
{
    private const string Prefix = "vcf-";

    internal static void Run()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Mcotis;component/Presentation/Theme.xaml", UriKind.Relative) });
        var (vm, _, _, _) = FemModelFileChecks.DefaultModelApp();

        // Edit → VCF System: Geometry, Cable Force, Storey Force, Sections
        var system = new VcfGeometryWindow(vm); system.Show(); Idle();
        foreach (var (index, name) in new[] { (0, "system-geometry"), (1, "system-cable-force"), (2, "system-storey-force"), (3, "system-sections") })
        {
            if (index >= system.ParameterTabs.Items.Count) continue;
            system.ParameterTabs.SelectedIndex = index; Idle(); Capture(system, name);
        }
        system.Close();

        // Define → Materials, and one material's form
        var materials = new DefineMaterialsWindow(vm); materials.Show(); Idle(); Capture(materials, "materials"); materials.Close();
        var material = vm.Materials.FirstOrDefault(m => m.Name.Contains("S690")) ?? vm.Materials.First();
        var materialForm = new MaterialPropertyWindow(material, false, true, new HashSet<string>()); materialForm.Show(); Idle(); Capture(materialForm, "material-form"); materialForm.Close();

        // Define → Section Properties: the list, then the form of one section of each element type
        vm.EnsureMaterials(); vm.EnsureSections();
        var list = new SectionListWindow(vm, SectionListKind.Frame); list.Show(); Idle(); Capture(list, "frame-sections"); list.Close();
        var taken = new HashSet<string>();
        foreach (var (shapes, name) in new[]
        {
            (new[] { FrameShape.Box, FrameShape.I, FrameShape.Rectangle, FrameShape.Pipe }, "section-steel"),
            (new[] { FrameShape.FilledTube }, "section-cft"),
            (new[] { FrameShape.MegaComposite }, "section-mega"),
            (new[] { FrameShape.Encased }, "section-encased"),
        })
        {
            var section = vm.FrameSections.FirstOrDefault(s => shapes.Contains(s.Shape));
            if (section is null) { Console.WriteLine($"No section for {name}; skipped."); continue; }
            Window form = section.Shape is FrameShape.FilledTube or FrameShape.Encased or FrameShape.MegaComposite
                ? new CompositeSectionWindow(vm, section, taken) : new FrameSectionWindow(vm, section, taken);
            form.Show(); Idle(); Capture(form, name); form.Close();
        }
        var cables = new SectionListWindow(vm, SectionListKind.Cable); cables.Show(); Idle(); Capture(cables, "cable-sections"); cables.Close();
        if (vm.CableSections.FirstOrDefault() is { } cable)
        { var strand = new StrandSectionWindow(vm, cable, taken); strand.Show(); Idle(); Capture(strand, "section-cable"); strand.Close(); }

        // Run → Analysis Options and Load Cases
        var options = new AnalysisOptionsWindow(vm.PlanarAnalysisSettings, _ => { }, planar: true, editable: true, cables: true); options.Show(); Idle(); Capture(options, "analysis-options"); options.Close();

        // Run → Run FEM Analysis, then the monitor
        FemModelFileChecks.Analyze(vm);
        var monitor = new FemRunMonitorWindow(vm); monitor.Show(); Idle(); Capture(monitor, "run-monitor"); monitor.Close();

        // Design menu: the CFT chord design with its results, and the steel beam / column design
        foreach (var (type, name) in new[] { (ColumnDesignType.ConcreteFilledTube, "design-cft"), (ColumnDesignType.MegaComposite, "design-mega"), (ColumnDesignType.Steel, "design-steel") })
        {
            DesignReferenceWindow? dialog = null;
            try
            {
                dialog = new DesignReferenceWindow(vm.DesignBasis(type), vm.DesignMemberOptions(type), vm.DesignCaseNames(), vm.DesignDefaultCases(), vm.DesignRunId,
                    member => vm.DesignSectionDefaults(type, member), null, (members, cases) => vm.DesignMemberInputs(type, members, cases), vm.Materials.ToArray(),
                    vm.FemResultStateText, false, basis => vm.RunColumnDesign(basis)) { Width = 1400, Height = 980 };
                dialog.Show(); Idle();
                dialog.Tabs.SelectedIndex = 0; Idle(); Capture(dialog, name + "-basis");
                dialog.Tabs.SelectedIndex = 1; Idle(); Capture(dialog, name + "-section");
                var run = Visuals<Button>(dialog).First(button => Equals(button.Content, "Run Design"));
                run.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Idle();
                Capture(dialog, name + "-results");
            }
            finally { dialog?.Close(); }
        }
        app.Shutdown();
        Console.WriteLine("VCF guide screenshots regenerated in docs/user-guide/images (vcf-*.png).");
    }



    /// <summary>--vcf-facts: prints the facts of the default VCF model the summary article quotes (releases, supports, deflections, cable forces).</summary>
    internal static void Facts()
    {
        var (vm, _, _, _) = FemModelFileChecks.DefaultModelApp();
        var run = FemModelFileChecks.Analyze(vm)[0];
        foreach (var g in vm.FemSectionProperties.GroupBy(p => p.SectionName ?? "?"))
        {
            var f = g.First();
            Console.WriteLine($"SECTION {g.Key}: {g.Count()} members; A={f.AreaM2:F4} I={f.InPlaneInertiaM4:F4} E={f.ElasticModulusKNPerM2:E3} w={f.WeightPerLengthKNPerM:F1} kN/m; releaseI={g.Count(p => p.ReleaseStartRotation)} releaseJ={g.Count(p => p.ReleaseEndRotation)}; mods={(f.Modifiers is null ? "none" : string.Join(",", f.Modifiers))}; e.g. {string.Join(" ", g.Take(6).Select(p => p.MemberName))}");
        }
        foreach (var p in vm.FemSectionProperties.Where(p => p.ReleaseStartRotation || p.ReleaseEndRotation))
            Console.WriteLine($"RELEASE {p.MemberName} I={p.ReleaseStartRotation} J={p.ReleaseEndRotation}");
        foreach (var (name, r) in run.Results!)
        {
            var bottom = r.Joints.Where(j => j.PointName.StartsWith("BTrPt")).ToArray();
            var top = r.Joints.Where(j => j.PointName.StartsWith("TTrPt")).ToArray();
            Console.WriteLine($"CASE {name}: bottom chord max down {bottom.Max(j => -j.UZM * 1000):F1} mm at {bottom.OrderBy(j => j.UZM).First().PointName}; max up {bottom.Max(j => j.UZM * 1000):F1} mm; top chord max down {top.Max(j => -j.UZM * 1000):F1} mm; max |UX| {r.Joints.Max(j => Math.Abs(j.UXM) * 1000):F1} mm; slack cables {r.Cables.Count(c => !c.Active)} of {r.Cables.Count}; cable T min {r.Cables.Min(c => c.AxialForceKN):F0} max {r.Cables.Max(c => c.AxialForceKN):F0} kN; fit iterations {r.TargetFitIterations}");
            Console.WriteLine("  reactions: " + string.Join("; ", r.SupportReactions.Select(s => $"{s.PointName} Rx={s.ReactionXKN:F0} Rz={s.ReactionZKN:F0}")));
        }
        var rr = run.ResultFor(run.Results.Keys.First(k => k.StartsWith("SLS")));
        foreach (var c in rr.Cables) Console.WriteLine($"CABLE {c.MemberName}: target {c.TargetForceKN:F0} applied {c.AppliedPretensionKN:F0} stage {c.PretensionStageForceKN:F0} SLS {c.AxialForceKN:F0} active {c.Active}");
    }

    /// <summary>--guide-export "Title" file.docx: the article as Export to Word… writes it (to see the report without the window).</summary>
    internal static void ExportArticle(string title, string path) =>
        UserGuideWord.Export(UserGuide.Parse(UserGuide.Embedded()).Find(title) ?? throw new InvalidOperationException("No article " + title), path);

    /// <summary>Figures of the "Summary VCF system" article: the default VCF model's undeformed shape and its SLS / ULS results.</summary>
    internal static void RunSummary()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Mcotis;component/Presentation/Theme.xaml", UriKind.Relative) });
        var (vm, _, _, _) = FemModelFileChecks.DefaultModelApp();
        var run = FemModelFileChecks.Analyze(vm)[0];
        foreach (var (name, result) in run.Results!)
            Console.WriteLine($"{name}: max down {result.MaximumDownwardDeflectionMm:F1} mm; max |N| {result.MaximumAbsoluteAxialKN:F0} kN; max |M| {result.MaximumAbsoluteMomentKNm:F0} kNm; max reaction {result.MaximumSupportReactionKN:F0} kN; iterations {result.NonlinearIterations}");
        void Figure(string caseName, FemResultMode mode, string file)
        {
            var result = run.ResultFor(caseName);
            File.WriteAllBytes($"docs/user-guide/images/vcf-summary-{file}.png", ResultFigureCapture.Capture(vm, null, result, mode));
            Console.WriteLine("  vcf-summary-" + file);
        }
        var sls = run.Results!.Keys.First(k => k.StartsWith("SLS")); var uls = run.Results.Keys.First(k => k.StartsWith("ULS"));
        Figure(sls, FemResultMode.DeflectedShape, "deflection-sls");
        Figure(uls, FemResultMode.AxialForce, "axial-uls");
        Figure(uls, FemResultMode.BendingMoment, "moment-uls");
        app.Shutdown();
    }

    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        int width = (int)Math.Ceiling(content.ActualWidth) + 20, height = (int)Math.Ceiling(content.ActualHeight) + 20;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle((Brush)window.FindResource("Surface"), null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new VisualBrush(content), null, new Rect(10, 10, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory("docs/user-guide/images");
        using var stream = File.Create($"docs/user-guide/images/{Prefix}{name}.png"); encoder.Save(stream);
        Console.WriteLine($"  {Prefix}{name}.png  {width} x {height}");
    }

    private static void Idle() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        var logical = LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().Concat(
            root is Visual ? Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(root)).Select(index => VisualTreeHelper.GetChild(root, index)) : []);
        foreach (var child in logical.Distinct())
        {
            if (child is T match) yield return match;
            foreach (var inner in Visuals<T>(child)) yield return inner;
        }
    }
}
