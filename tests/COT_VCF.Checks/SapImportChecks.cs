using System.IO;
using System.Reflection;
using COT_VCF.Application;
using COT_VCF.Configuration;
using COT_VCF.Domain;
using COT_VCF.Presentation;

/// <summary>
/// File → Import From SAP2000 on the benchmark model (<c>tests/fem-sap-benchmark/model.s2k</c>) served by a read-only
/// SAP2000 stand-in: a fresh app imports the geometry, member properties, libraries, loading, Tab 02 chord groups and the
/// Tab 03 cable targets, is ready to run, runs FEM — and never writes to SAP2000.
/// </summary>
internal static class SapImportChecks
{
    public static void Run()
    {
        var text = SapModelText.Read(Path.Combine(FemSapBenchmark.DefaultFolder, "model.s2k"));
        var geometry = text.Geometry();
        var sap = ImportSap.Create(text, geometry);
        var state = new ApplicationState();
        var workflow = new WorkflowService(sap.Gateway, state);
        var vm = new MainViewModel(state, workflow, new NoSelection(), new ConfigService(sap.Gateway, state, workflow));

        var summary = vm.ImportFromSapAsync().GetAwaiter().GetResult();
        var cables = geometry.Members.Count(member => member.Kind == MemberKind.Cable);
        Check(summary.ReadyToRun && vm.CanRunFemAnalysis && state.IsSapConnected && state.BaselineCaptured,
            "Import From SAP2000 leaves the model ready to run — " + string.Join(" | ", summary.Lines));
        Check(state.Geometry.Members.Count == geometry.Members.Count && vm.FemSectionProperties.Count == geometry.Members.Count &&
              vm.Materials.Count == 3 && vm.FrameSections.Count == 2 && vm.CableSections.Count == 1 && vm.FemPatternRows.Count > 0,
            "it imports the geometry, every member's properties, the material / section libraries and the load patterns");
        Check(state.Truss.TopPoints.Count == 11 && state.Truss.TopPoints[0] == "TTrPt001" && state.Truss.MiddlePoints.Count == 11 &&
              state.Truss.BottomPoints.Count == 11,
            "the Tab 02 chord groups are found from the TTrPt / MTrPt / BTrPt joints");
        Check(state.Cables.Count == cables && state.Cables.All(cable => cable.TargetForce == 30_000 && cable.LoadPattern == "Pretension" && cable.SectionAreaM2 > 0),
            "Tab 03 takes every cable with its SAP2000 target force, load pattern and section");
        // Tab 01 is fitted to the SAP2000 joint loads: the forces it places equal the ones it replaces, point by point
        var sapLoads = text.Loading().JointLoads.GroupBy(item => (item.Load.PointName, Pattern: item.Pattern.ToUpperInvariant()))
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Load.FzKN));
        var tab01 = StudyCompositionService.BuildPointLoads(state, LoadingCandidates.Enumerate(state.LoadDefinitions.ToArray()).First());
        Check(state.LoadDefinitions.Count > 0 && state.AppliedPoints.Count >= 2 && tab01.Count > 0 &&
              tab01.All(row => sapLoads.TryGetValue((row.AppliedPt, row.Case.ToUpperInvariant()), out var fz) && Math.Abs(fz - row.FzKn) <= 1e-6 * Math.Max(1, Math.Abs(fz))),
            $"Tab 01 ({string.Join(", ", state.LoadDefinitions.Select(definition => $"{definition.LoadName} {definition.Qz} kPa"))} on {state.AppliedPoints.Count} joints) " +
            "reproduces the SAP2000 joint loads it replaces");

        var runs = FemModelFileChecks.Analyze(vm);
        Check(runs.Count == 1 && sap.Writes == 0, $"the imported model runs FEM, and SAP2000 was never written ({runs.Count} runs, unexpected calls: {string.Join(", ", sap.Unexpected)}; {vm.FemProgressText})");

        // Export to SAP2000 of the imported model gives SAP2000 back what it holds: every member and section, the joint loads
        // Tab 01 replaces (equal to SAP's), the cable target forces, the static cases and the combinations
        var export = vm.BuildSapExportModel();
        Check(export.Geometry.Members.Count == geometry.Members.Count && export.Members.Count == geometry.Members.Count &&
              export.Tab01Points.Count == state.AppliedPoints.Count &&
              export.JointLoads.All(load => sapLoads.TryGetValue((load.PointName, load.Pattern.ToUpperInvariant()), out var fz) && Math.Abs(fz - load.FzKN) <= 1e-6 * Math.Max(1, Math.Abs(fz))) &&
              export.CableTargets.Count == cables && export.CableTargets.All(target => target.ForceKN == 30_000 && target.Pattern == "Pretension") &&
              export.Cases.Select(item => item.Name).SequenceEqual(vm.FemCaseRows.Where(row => row.Supported).Select(row => row.Name)) &&
              export.Patterns.Count == vm.FemPatternRows.Count && export.PinSupports.Count > 0,
            $"Export to SAP2000 carries the imported model back: {export.Members.Count} members, {export.JointLoads.Count} joint loads equal to SAP's, " +
            $"{export.CableTargets.Count} cable targets, {export.Cases.Count} cases, {export.Combinations.Count} combinations");
        var preview = vm.PreviewSapExportAsync().GetAwaiter().GetResult();
        Check(sap.Exports.Count == 1 && sap.Exports[0].Preview && !sap.Exports[0].Unlock && preview.Target.Path == @"C:\Models\Imported.sdb",
            "the export is previewed first, read-only, against the model open in SAP2000");
        var report = vm.ExportToSapAsync(preview).GetAwaiter().GetResult();
        Check(sap.Exports.Count == 2 && !sap.Exports[1].Preview && ReferenceEquals(sap.Exports[1].Model, preview.Model) && report.Lines.Count > 0 && sap.Writes == 0,
            "after the preview the same export model is written to that SAP2000 model");

        // the chords of a model that does not use the COT joint names are found from its levels
        var renamed = new GeometryModel(
            geometry.Points.ToDictionary(point => "J" + point.Key, point => point.Value with { Name = "J" + point.Key }, StringComparer.Ordinal),
            geometry.Members.Select(member => member with { Start = "J" + member.Start, End = "J" + member.End }).ToArray());
        var chords = VcfChordDetection.Detect(renamed);
        Check(chords is not null && chords.Top.SequenceEqual(state.Truss.TopPoints.Select(name => "J" + name)) &&
              chords.Bottom.SequenceEqual(state.Truss.BottomPoints.Select(name => "J" + name)),
            "without the COT names the chord groups come from the three highest levels of horizontal frames");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    private sealed class NoSelection : ISelectionService
    {
        public IReadOnlyList<string>? Select(string title, IReadOnlyList<string> available, IReadOnlyList<string> selected) => null;
    }

    /// <summary>A read-only SAP2000 holding the benchmark model; every cable carries 30 MN in pattern "Pretension".</summary>
    private class ImportSap : DispatchProxy
    {
        private SapModelText text = null!;
        private GeometryModel geometry = null!;
        private static readonly ModelInfo Model = new(@"C:\Models\Imported.sdb", "27", 6, "kN", "m", false);
        public int Writes { get; private set; }
        public List<string> Unexpected { get; } = [];
        public List<(SapExportModel Model, bool Unlock, bool Preview)> Exports { get; } = [];
        public ISapGateway Gateway { get; private set; } = null!;

        public static ImportSap Create(SapModelText text, GeometryModel geometry)
        {
            var gateway = Create<ISapGateway, ImportSap>();
            var proxy = (ImportSap)(object)gateway;
            proxy.text = text;
            proxy.geometry = geometry;
            proxy.Gateway = gateway;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var cables = geometry.Members.Where(member => member.Kind == MemberKind.Cable).ToArray();
            switch (method?.Name)
            {
                case nameof(ISapGateway.ConnectAsync):
                case nameof(ISapGateway.GetCurrentModelInfoAsync):
                    return Task.FromResult(Model);
                case nameof(ISapGateway.ReadAsync):
                    return Task.FromResult(new ModelSnapshot(Model, geometry, text.LoadPatternNames));
                case nameof(ISapGateway.ReadPointLoadsAsync):
                    return Task.FromResult<IReadOnlyList<PointLoad>>([]);
                case nameof(ISapGateway.ReadForcesAsync):
                    return Task.FromResult<IReadOnlyList<ForceSource>>(cables.Select(cable => new ForceSource(cable.Name, cable.Kind, "Pretension", 30_000)).ToArray());
                case nameof(ISapGateway.GetCableTargetForcesAsync):
                    return Task.FromResult<IReadOnlyList<CableTargetForce>>([new((string)args![0]!, "Pretension", 30_000, 0.5)]);
                case nameof(ISapGateway.GetCableSectionsAsync):
                    return Task.FromResult<IReadOnlyList<CableSection>>(((IReadOnlyList<string>)args![0]!)
                        .Select(name => new CableSection(name, 0.028802317797652074, "192T15.7")).ToArray());
                case nameof(ISapGateway.GetFemSectionPropertiesAsync):
                    return Task.FromResult(text.SectionProperties());
                case nameof(ISapGateway.GetFemLoadingAsync):
                    return Task.FromResult(text.Loading());
                case nameof(ISapGateway.GetLoadCaseNamesAsync):
                    return Task.FromResult(text.LoadCaseNames);
                case nameof(ISapGateway.GetMaterialsAsync):
                    return Task.FromResult<IReadOnlyList<MaterialProperty>>([
                        MaterialProperty.Default(MaterialKind.Concrete, "C70/85") with { Source = "SAP2000" },
                        MaterialProperty.Default(MaterialKind.Steel, "S460") with { Source = "SAP2000" },
                        MaterialProperty.Default(MaterialKind.Tendon, "HDPE Galvanised Strand T15.7 – Y1860S7") with { Source = "SAP2000" }]);
                case nameof(ISapGateway.GetSectionsAsync):
                    return Task.FromResult(new SectionLibrary(
                        [FrameSectionProperty.Default(FrameShape.Box, "SHS_1200x1200x120_S460", "S460") with { Source = "SAP2000" },
                         FrameSectionProperty.Default(FrameShape.I, "UB610", "S355") with { Source = "SAP2000" }],
                        [new StrandSectionProperty { Name = "192T15.7", Material = "HDPE Galvanised Strand T15.7 – Y1860S7", Area = 0.028802317797652074, Source = "SAP2000" }],
                        []));
                case nameof(ISapGateway.ExportModelAsync):
                    Exports.Add(((SapExportModel)args![0]!, (bool)args[2]!, (bool)args[3]!));
                    return Task.FromResult(new SapExportReport(Model.Path, [(bool)args[3]! ? "preview" : "exported"]));
                case "Dispose":
                    return null;
                default:
                    Writes++;
                    Unexpected.Add(method?.Name ?? "?");
                    throw new InvalidOperationException($"Import must only read SAP2000 (called {method?.Name}).");
            }
        }
    }
}
