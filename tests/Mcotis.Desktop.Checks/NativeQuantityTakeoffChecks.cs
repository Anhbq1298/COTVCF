using Mcotis.Core;
using Mcotis.Desktop.Application.Quantities;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Infrastructure;
using Mcotis.Desktop.Presentation;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class NativeQuantityTakeoffChecks
{
    public static void Run()
    {
        var session = new Mcotis.Application.DocumentSession();
        var concrete = ObjectId.New();
        var steel = ObjectId.New();
        var rectangular = ObjectId.New();
        var steelI = ObjectId.New();
        var p0 = ObjectId.New();
        var p5 = ObjectId.New();
        var p8 = ObjectId.New();
        var c0 = ObjectId.New();
        var c3 = ObjectId.New();
        var f1 = ObjectId.New();
        var f2 = ObjectId.New();
        var f3 = ObjectId.New();
        session.Execute(Mcotis.Application.DocumentCommands.PutMaterial(new(concrete, "C40", MaterialType.Concrete, 3.2e7, .2, 24, 2.4)));
        session.Execute(Mcotis.Application.DocumentCommands.PutMaterial(new(steel, "S355", MaterialType.Steel, 2.1e8, .3, 77, 7.85)));
        session.Execute(Mcotis.Application.DocumentCommands.PutFrameSection(new(rectangular, "RC 300", concrete, FrameSectionShape.Rectangle,
            new(.06, .0002, .00045))
        {
            Dimensions = FrameSectionDefinition.NoDimensions.AddRange(new Dictionary<string, double> { ["t3"] = .3, ["t2"] = .2 }),
        }));
        session.Execute(Mcotis.Application.DocumentCommands.PutFrameSection(new(steelI, "I 400", steel, FrameSectionShape.ISection,
            new(.02, .0005, .0001))
        {
            Dimensions = FrameSectionDefinition.NoDimensions.AddRange(new Dictionary<string, double> { ["t3"] = .4, ["t2"] = .2, ["tf"] = .02, ["tw"] = .01 }),
        }));
        session.Execute(Mcotis.Application.DocumentCommands.Edit("Build quantity model", editor =>
        {
            editor.AddPoint(new(0, 0, 0), "P0", p0);
            editor.AddPoint(new(0, 0, 5), "P5", p5);
            editor.AddPoint(new(0, 0, 8), "P8", p8);
            editor.AddPoint(new(3, 0, 0), "C0", c0);
            editor.AddPoint(new(3, 0, 3), "C3", c3);
            editor.AddFrame(p0, p5, "F1", f1);
            editor.AddFrame(p5, p8, "F2", f2);
            editor.AddFrame(p0, p8, "F3", f3);
            editor.SetFrameSection([f1, f2], rectangular);
            editor.SetFrameSection([f3], steelI);
        }));

        var takeoff = NativeQuantityTakeoffCalculator.Calculate(session.Document);
        var rc = takeoff.Rows.Single(row => row.Section == "RC 300");
        Check(rc.MemberCount == 2 && Math.Abs(rc.TotalLengthM!.Value - 8) < 1e-9 && Math.Abs(rc.ConcreteVolumeM3!.Value - .48) < 1e-9 &&
              rc.SteelMassT == 0 && rc.Figure is { Shapes.Count: 1 },
            "native takeoff groups frames by their assigned concrete section, totals model geometry and draws stored dimensions");

        var steelRow = takeoff.Rows.Single(row => row.Section == "I 400");
        Check(steelRow.SectionType == "I Section" && Math.Abs(steelRow.TotalLengthM!.Value - 8) < 1e-9 &&
              Math.Abs(steelRow.SteelMassT!.Value - 1.256) < 1e-9 && steelRow.ConcreteVolumeM3 == 0 && steelRow.Figure is not null,
            "native takeoff calculates steel mass from stored area and material mass density and shows the section type");

        var withUnassigned = session.Document.Edit();
        var free = ObjectId.New();
        withUnassigned.AddCable(c0, c3, "C1", free);
        var resultWithUnassigned = NativeQuantityTakeoffCalculator.Calculate(withUnassigned.Commit().Document);
        Check(resultWithUnassigned.Rows.Any(row => row.Section == "Unassigned" && row.Kind == QuantityMemberKind.Cable &&
              row.TotalLengthM == 3 && row.ConcreteVolumeM3 is null && row.SteelMassT is null) &&
              resultWithUnassigned.Total.ConcreteVolumeM3 is null,
            "native takeoff calls unassigned quantities unavailable and does not silently omit them from totals");

        Console.WriteLine("Native model quantity checks passed.");
    }

    public static void RunUi()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Mcotis;component/Presentation/Theme.xaml", UriKind.Relative) });
        var state = StudyComposerChecks.CreateState();
        var sap = new Program.FakeSap { Snapshot = new(state.Model!, state.Geometry, state.LoadPatterns.ToArray()) };
        var workflow = new WorkflowService(sap, state);
        var path = Path.GetFullPath(Path.Combine(".work", "native-quantities-ui-" + Guid.NewGuid().ToString("N")));
        var vm = new MainViewModel(state, workflow, new NoSelection(), new ConfigService(sap, state, workflow),
            femSessionStore: new FemWorkingSessionStore(path));
        vm.LoadDefaultFemModel(replaceCurrent: true);
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 800 };
        try
        {
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var resultMenu = window.MainMenu.Items.OfType<MenuItem>().Single(item => (item.Header as string)?.Replace("_", "") == "Result");
            var quantities = resultMenu.Items.OfType<MenuItem>().Single(item => (item.Header as string)?.Replace("_", "") == "Show Quantities…");
            quantities.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, quantities));
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var dialog = window.OwnedWindows.OfType<QuantityTakeoffWindow>().Single();
            dialog.UpdateLayout();
            Check(dialog.IsVisible && dialog.RowsGrid.Items.Count > 0 && dialog.TotalsLabel.Text.Contains("members", StringComparison.Ordinal),
                "Result → Show Quantities opens a read-only table calculated from the active native document");
            var cft = dialog.RowsGrid.Items.OfType<NativeQuantityRow>().FirstOrDefault(row => row.SectionType == "Concrete Filled Tube (CFT)");
            Check(cft is { Figure: { Shapes.Count: > 1 }, ConcreteVolumeM3: > 0, SteelMassT: > 0 } && cft.MemberCount > 1,
                "the native document's embedded section metadata groups CFT members and displays both concrete and steel parts");
            var mega = dialog.RowsGrid.Items.OfType<NativeQuantityRow>().FirstOrDefault(row => row.SectionType == "Mega Composite Column");
            Check(mega is { Figure: { Shapes.Count: > 1 }, ConcreteVolumeM3: > 0 } && mega.MemberCount > 0 &&
                  (mega.SteelMassT is > 0 || mega.Details.Contains("mass is unavailable", StringComparison.OrdinalIgnoreCase)),
                "the native document displays mega composite geometry and reports steel mass only when its material density is stored");
            dialog.Close();
        }
        finally { window.Close(); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class NoSelection : ISelectionService
    {
        public IReadOnlyList<string>? Select(string title, IReadOnlyList<string> available, IReadOnlyList<string> selected) => null;
    }
}
