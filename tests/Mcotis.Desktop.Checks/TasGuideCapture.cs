using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Etabs;
using Mcotis.Desktop.Presentation;

/// <summary>Opt-in regeneration of the TAS guide's screenshots from real dialogs and a real nine-case run.</summary>
internal static class TasGuideCapture
{
    internal static void Run()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Mcotis;component/Presentation/Theme.xaml", UriKind.Relative) });
        var vm = EtabsImportChecks.NewViewModel(out _); vm.LoadTasModel();
        var p = TiedArchParameters.Default;
        var settings = TasOptimization.Start(p) with
        {
            Geometry = new Dictionary<string, VcfRange>
            { ["H1"] = new(30, 30, 1), ["h"] = new(2, 4, 1), ["Bays"] = new(12, 12, 2), ["Spacing"] = new(10, 10, 1) },
            Loads = new Dictionary<string, VcfRange>
            { ["SW"] = new(7, 7, 1), ["SDL"] = new(3, 3, 1), ["LL"] = new(5, 10, 2.5), ["Floors"] = new(10, 10, 1), ["B"] = new(12, 12, 1) },
            DeflectionResult = "SLS1 G+Q full", SpanRatio = 250, UpliftLimitMm = 0
        };
        vm.SetTasOptimization(settings);
        var form = new OptimizationTasWindow(vm); form.Show(); Idle();
        var sheets = Descendants<TabControl>(form).First();
        foreach (var (sheet, file) in new[] { ("Geometry", "geometry"), ("Storey Force", "storey-force"), ("Sections", "sections") })
        {
            sheets.SelectedItem = sheets.Items.OfType<TabItem>().Single(t => Equals(t.Header, sheet));
            form.PreviewCorner.SelectedIndex = 2; Idle(); Capture(form, "optimization-tas-" + file);
        }
        var used = form.FormSettings(); form.Close();
        if (used.CombinationCount != 9 || TasOptimization.Validate(used).Count > 0) throw new InvalidOperationException("The illustrated TAS example must have nine valid cases.");
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            var task = vm.RunTasOptimizationAsync(used); var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default); Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult(); Idle();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        if (vm.VcfOptimizationRows.Count != 9 || vm.VcfOptimizationRows.Any(r => r.MaxDeflectionMm is null)) throw new InvalidOperationException("The illustrated run must solve all nine cases.");
        var results = new OptimizationResultsWindow(vm); results.Show(); Idle();
        Capture(results, "optimization-tas-results-run");
        results.ResultTabs.SelectedIndex = 1; Idle(); Capture(results, "optimization-tas-results-chart"); results.Close();
        Console.WriteLine("TAS example: " + vm.VcfOptimizationBest);
        foreach (var row in vm.VcfOptimizationRows) Console.WriteLine($"Case {row.Case}: {string.Join(", ", row.Values)}; deflection {row.Deflection} mm; {row.Check}; {row.Status}");

        // Optional cable sheet is illustrated separately; it is not part of the nine-case baseline example.
        var cableVm = EtabsImportChecks.NewViewModel(out _);
        var elevated = p with { ElevatedCable = true, CableHeight = 10, CablePretension = 20000 };
        if (!cableVm.ApplyTiedArchSystem(elevated).ReadyToRun) throw new InvalidOperationException("Elevated TAS example is not ready.");
        cableVm.SetTasOptimization(settings with
        {
            Geometry = new Dictionary<string, VcfRange>(settings.Geometry) { ["H0"] = new(10, 10, 1) },
            ElevatedCable = true, CableForce = new(10000, 30000), CableSteps = 3
        });
        var cable = new OptimizationTasWindow(cableVm); cable.Show(); Idle();
        var cableSheets = Descendants<TabControl>(cable).First(); cableSheets.SelectedItem = cable.CableSheet;
        Idle(); Capture(cable, "optimization-tas-cable-force"); cable.Close(); app.Shutdown();
        Console.WriteLine("TAS guide screenshots regenerated in docs/user-guide/images.");
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
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory("docs/user-guide/images"); using var stream = File.Create("docs/user-guide/images/" + name + ".png"); encoder.Save(stream);
    }
    private static void Idle() => Dispatcher.CurrentDispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
