using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mcotis.Desktop.Application.Design;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Presentation;

/// <summary>Regenerates guide screenshots from real column dialogs and the design checks' illustrative CFT forces.</summary>
internal static class ColumnGuideCapture
{
    internal static void Run()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Mcotis;component/Presentation/Theme.xaml", UriKind.Relative) });
        var inputs = new[]
        {
            new DesignMemberInput("C1", "CFT example", 6, [new("ULS1", [new(-12000, 300, 900), new(-12000, 300, -450)])]),
            new DesignMemberInput("C2", "CFT example", 6, [new("ULS1", [new(-9000, 500, 1400), new(-9000, 500, -700)])])
        };
        var basis = ColumnDesignCodes.Default(ColumnDesignType.ConcreteFilledTube) with
        {
            Members = ["C1", "C2"], LoadCases = ["ULS1"], EffectiveLengthY = 6, EffectiveLengthZ = 6,
            Section = new ColumnSectionInput { Tube = TubeShape.Rectangular, TubeDepth = 600, TubeWidth = 600, TubeThickness = 16, Fck = 60, Fy = 355 }
        };
        DesignReferenceWindow Open(ColumnDesignBasis b) => new(b,
            [new("C1", "CFT example", "S355 / C60/75", 6, true), new("C2", "CFT example", "S355 / C60/75", 6, true)],
            ["ULS1"], ["ULS1"], "Worked example", _ => basis.Section, memberForces: (members, cases) => inputs.Where(m => members.Contains(m.Member)).ToArray())
            { Width = 1400, Height = 980 };
        var window = Open(basis); window.Show(); Idle(); Capture(window, "basis");
        window.Tabs.SelectedIndex = 2; Idle(); Capture(window, "members");
        window.Tabs.SelectedItem = window.OptimizationTab;
        window.OptDepthMin.Text = "400"; window.OptDepthMax.Text = "900"; window.OptDepthStep.Text = "100";
        window.OptThickMin.Text = "10"; window.OptThickMax.Text = "30"; window.OptThickStep.Text = "5";
        window.OptSteelFrom.SelectedItem = "S355"; window.OptSteelTo.SelectedItem = "S460";
        window.OptConcreteFrom.SelectedItem = "C50/60"; window.OptConcreteTo.SelectedItem = "C70/85";
        window.OptRebarBox.IsChecked = false; Idle(); Capture(window, "cft-ranges");
        var scroll = Descendants<ScrollViewer>(window).First(s => s.Content is StackPanel panel && panel.Children.Contains(window.OptTubeGroup));
        scroll.ScrollToBottom(); Idle(); Capture(window, "costs");
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            window.OptRunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (!window.OptRunButton.IsEnabled && DateTime.UtcNow < deadline) { Idle(); Thread.Sleep(10); }
            Idle();
            if (window.OptResults.Items.Count == 0) throw new InvalidOperationException("CFT example returned no passing sections: " + window.OptStatus.Text);
            Capture(window, "results");
            var best = (DesignReferenceWindow.OptimizationRow)window.OptResults.Items[0];
            if (!best.Check.Passes || best.Rank != 1) throw new InvalidOperationException("Illustrated first candidate must pass.");
            Console.WriteLine("Illustrated result: " + window.OptStatus.Text);
            window.OptUseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Idle();
            if (window.Tabs.SelectedIndex != 1) throw new InvalidOperationException("Use Selected did not open Section.");
            Capture(window, "selected-section");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); window.Close(); }
        foreach (var (type, name) in new[] { (ColumnDesignType.ReinforcedConcrete, "rc-ranges"), (ColumnDesignType.SteelEncased, "encased-ranges"), (ColumnDesignType.MegaComposite, "mega-ranges") })
        {
            var other = Open(ColumnDesignCodes.Default(type) with { Members = ["C1"], LoadCases = ["ULS1"],
                EffectiveLengthY = type == ColumnDesignType.MegaComposite ? 20 : 6, EffectiveLengthZ = type == ColumnDesignType.MegaComposite ? 20 : 6 });
            other.Show(); other.Tabs.SelectedItem = other.OptimizationTab; Idle();
            var otherScroll = Descendants<ScrollViewer>(other).First(s => s.Content is StackPanel panel && panel.Children.Contains(other.OptOutlineGroup));
            otherScroll.ScrollToVerticalOffset(type == ColumnDesignType.MegaComposite ? 180 : 70);
            Idle(); Capture(other, name); other.Close();
        }
        app.Shutdown(); Console.WriteLine("Nine column optimization screenshots captured.");
    }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        int width = (int)Math.Ceiling(content.ActualWidth) + 20, height = (int)Math.Ceiling(content.ActualHeight) + 20;
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)window.FindResource("Surface"), null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(10, 10, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory("docs/user-guide/images"); using var stream = File.Create("docs/user-guide/images/column-optimization-" + name + ".png"); encoder.Save(stream);
    }
    private static void Idle() => Dispatcher.CurrentDispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
}
