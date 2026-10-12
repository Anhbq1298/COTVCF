using System.IO;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Etabs;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Presentation;

/// <summary>
/// File → Export to ETABS. Offline: the export model of the CoT 120 m arch (imported from its .e2k) carries exactly the
/// loads ETABS defined and each section's true weight. <c>--etabs-export-live</c> (opt-in) runs the real export in a
/// private hidden ETABS this check starts on a blank model — never the user's ETABS — then imports the result back and
/// runs it: the geometry, the loads and the FEM results must come back as they went out, and a second export must find
/// nothing left to change.
/// </summary>
internal static class EtabsExportChecks
{
    private const string EtabsExe = @"C:\Program Files\Computers and Structures\ETABS 23\ETABS.exe";
    private static string E2k => Path.Combine(EtabsImportChecks.Folder, "CoT120.e2k");

    public static void Run()
    {
        var vm = NewViewModel();
        vm.ImportFromE2kFile(E2k);
        var export = vm.BuildEtabsExportModel();
        var etabs = EtabsE2kReader.Read(File.ReadAllText(E2k), E2k).JointLoads
            .GroupBy(load => load.Pattern.ToUpperInvariant()).ToDictionary(group => group.Key, group => group.Sum(load => load.Fz));
        var exported = export.JointLoads.GroupBy(load => load.Pattern.ToUpperInvariant()).ToDictionary(group => group.Key, group => group.Sum(load => load.Load.FzKN));
        Check(etabs.Count == exported.Count && etabs.All(pair => exported.TryGetValue(pair.Key, out var fz) && Math.Abs(fz - pair.Value) < 1e-6) &&
              export.JointLoads.Count == 40 && export.GravityLoadsLeftOut == 0,
            "the ETABS export carries every joint load Run Analysis applies — Tab 01's in place of the ETABS loads it replaces, equal to them");
        var rib = vm.FemSectionProperties.First(property => property.SectionName == "RSC 3.52x2.64 P");
        Check(export.SectionWeights.TryGetValue("RSC 3.52x2.64 P", out var weight) && Math.Abs(weight.WeightPerLength - rib.WeightPerLengthKNPerM) < 1e-9 &&
              export.Base.Members.Count == 143 && export.Base.PinSupports.SequenceEqual(["1@Base", "2@Base"]),
            $"it carries each section's true weight per metre (arch rib {weight.WeightPerLength:0.##} kN/m), every member and the pins");
    }

    public static void RunLive()
    {
        var gateway = EtabsGatewayFactory.StartPrivate(EtabsExe);
        try
        {
            var source = NewViewModel();
            source.ImportFromE2kFile(E2k);
            source.Etabs = gateway;
            var preview = source.PreviewEtabsExportAsync().GetAwaiter().GetResult();
            foreach (var line in preview.Plan.Lines) Console.WriteLine("  preview: " + line);
            Check(preview.Plan.Lines.Any(line => line.StartsWith("Stories: the model is empty", StringComparison.Ordinal)) &&
                  preview.Plan.Lines.Any(line => line.Contains("remove 0 frame(s), add 143", StringComparison.Ordinal)),
                "the preview of an export into a blank ETABS model sets its stories to the joint levels and adds every member");
            var report = source.ExportToEtabsAsync(preview).GetAwaiter().GetResult();
            foreach (var line in report.Lines) Console.WriteLine("  export: " + line);
            var again = source.PreviewEtabsExportAsync().GetAwaiter().GetResult();
            Check(again.Plan.Lines.Any(line => line.Contains("143 member(s) match", StringComparison.Ordinal) && line.Contains("remove 0 frame(s), add 0", StringComparison.Ordinal)) &&
                  again.Plan.Lines.Any(line => line.StartsWith("Stories: kept (every joint level is a story)", StringComparison.Ordinal)),
                "exported again, every member matches an ETABS frame and nothing is added or removed: " + string.Join(" | ", again.Plan.Lines.Take(3)));

            // the ETABS model, imported back, is the model that went out
            var back = NewViewModel();
            back.Etabs = gateway;
            var summary = back.ImportFromEtabsAsync().GetAwaiter().GetResult();
            foreach (var line in summary.Lines) Console.WriteLine("  import: " + line);
            Check(summary.ReadyToRun && Ends(back.State.Geometry).SetEquals(Ends(source.State.Geometry)),
                "imported back from ETABS, it has the same 143 members between the same points, and is ready to run");
            static Dictionary<((long, long, long), string), double> Loads(MainViewModel vm) => vm.BuildEtabsExportModel().JointLoads
                .GroupBy(load => (Key(vm.State.Geometry.Points[load.Load.PointName].Position), load.Pattern.ToUpperInvariant()))
                .ToDictionary(group => group.Key, group => group.Sum(load => load.Load.FzKN));
            var sent = Loads(source);
            var received = Loads(back);
            Check(sent.All(pair => received.TryGetValue(pair.Key, out var fz) && Math.Abs(fz - pair.Value) < 1e-6),
                "every joint load came back on its joint, in its pattern");

            var original = FemModelFileChecks.Analyze(source).Single();
            var roundTrip = FemModelFileChecks.Analyze(back).Single();
            string At(MainViewModel vm, double x, double z) => vm.State.Geometry.Points.Values.Single(point => Math.Abs(point.Position.X - x) < 1e-6 && Math.Abs(point.Position.Z - z) < 1e-6).Name;
            foreach (var name in new[] { "DEAD", "DEAD_above", "LL_left", "ULS1 1.35G+1.5Q full" })
            {
                var a = original.ResultFor(name);
                var b = roundTrip.ResultFor(roundTrip.Results!.Keys.Single(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)));
                var (ua, ub) = (a.Joints.Single(joint => joint.PointName == At(source, 50, 33)).UZM, b.Joints.Single(joint => joint.PointName == At(back, 50, 33)).UZM);
                var (ra, rb) = (a.SupportReactions.Sum(item => item.ReactionZKN), b.SupportReactions.Sum(item => item.ReactionZKN));
                // ETABS keeps a section weight modifier to about six figures
                Check(Math.Abs(ua - ub) <= 1e-4 * Math.Abs(ua) && Math.Abs(ra - rb) <= 1e-5 * Math.Abs(ra),
                    $"'{name}' gives the same mid-span deflection ({ua * 1000:0.###} / {ub * 1000:0.###} mm) and reactions ({ra:N1} / {rb:N1} kN) after the round trip through ETABS");
            }
            // a model that has had objects (all deleted, as when a model is emptied in ETABS) keeps its stories: the export of the
            // default model — cables, joints down to −20 m — still goes in, and comes back between the same points
            var emptied = gateway.EmptyPrivateModelAsync().GetAwaiter().GetResult();
            var vcf = NewViewModel();
            vcf.LoadDefaultFemModel(replaceCurrent: true);
            vcf.Etabs = gateway;
            var plan = vcf.PreviewEtabsExportAsync().GetAwaiter().GetResult();
            var written = vcf.ExportToEtabsAsync(plan).GetAwaiter().GetResult();
            foreach (var line in written.Lines) Console.WriteLine("  export (emptied model): " + line);
            Check(emptied == 143 && written.Lines.Any(line => line.Contains("ETABS kept its 7 stories", StringComparison.Ordinal)) &&
                  written.Lines.Any(line => line.Contains("tension-only", StringComparison.Ordinal)),
                "into a model emptied in ETABS, the export keeps ETABS's stories (ETABS refuses new ones there), says so, and writes the cables as tension-only frames");
            var vcfBack = NewViewModel();
            vcfBack.Etabs = gateway;
            var vcfSummary = vcfBack.ImportFromEtabsAsync().GetAwaiter().GetResult();
            Check(Ends(vcfBack.State.Geometry).SetEquals(Ends(vcf.State.Geometry)) &&
                  vcfBack.State.Geometry.Points.Values.Min(point => point.Position.Z) < -19.999,
                $"imported back, the default model has the same {vcf.State.Geometry.Members.Count} members between the same points, down to −20 m — " +
                string.Join(" | ", vcfSummary.Lines.Take(2)));
        }
        finally { EtabsGatewayFactory.Close(gateway); }
    }

    private static (long, long, long) Key(Position p) => ((long)Math.Round(p.X * 1000), (long)Math.Round(p.Y * 1000), (long)Math.Round(p.Z * 1000));

    /// <summary>The members as pairs of end points (ETABS turns beams to run towards +X, so not in their order).</summary>
    private static HashSet<((long, long, long), (long, long, long))> Ends(GeometryModel geometry) => geometry.Members.Select(member =>
    {
        var (a, b) = (Key(geometry.Points[member.Start].Position), Key(geometry.Points[member.End].Position));
        return a.CompareTo(b) <= 0 ? (a, b) : (b, a);
    }).ToHashSet();

    private static MainViewModel NewViewModel()
    {
        var sap = FemModelFileChecks.SapDouble.Create(null!, GeometryModel.Empty, refuseAll: true);
        var state = new ApplicationState();
        var workflow = new WorkflowService(sap.Gateway, state);
        return new MainViewModel(state, workflow, new NoSelection(), new ConfigService(sap.Gateway, state, workflow));
    }

    private sealed class NoSelection : ISelectionService
    {
        public IReadOnlyList<string>? Select(string title, IReadOnlyList<string> available, IReadOnlyList<string> selected) => null;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}

/// <summary>The private ETABS of the live checks (internal API of the app).</summary>
internal static class EtabsGatewayFactory
{
    public static Mcotis.Desktop.SAP2000.EtabsGateway StartPrivate(string exe) => Mcotis.Desktop.SAP2000.EtabsGateway.StartPrivateAsync(exe).GetAwaiter().GetResult();

    public static void Close(Mcotis.Desktop.SAP2000.EtabsGateway gateway)
    {
        gateway.ClosePrivateAsync().GetAwaiter().GetResult();
        gateway.Dispose();
    }
}
