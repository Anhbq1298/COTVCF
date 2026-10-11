using System.IO;
using System.Reflection;
using System.Windows.Threading;
using COT_VCF.Application;
using COT_VCF.Configuration;
using COT_VCF.Domain;
using COT_VCF.Geometry;
using COT_VCF.Presentation;

/// <summary>
/// 07 FEM → Inputs → Save Model / Load Model, on the benchmark model (<c>tests/fem-sap-benchmark</c>): a model saved
/// while connected loads into a disconnected app, enables only the FEM run, and gives the same FEM results as the
/// connected run — without one call to SAP2000.
/// </summary>
internal static class FemModelFileChecks
{
    public static void Run()
    {
        var folder = FemSapBenchmark.DefaultFolder;
        var text = SapModelText.Read(Path.Combine(folder, "model.s2k"));
        var geometry = text.Geometry();
        var sap = SapDouble.Create(text, geometry);

        // Connected app with the study's project, as after Connect SAP + Load Config.
        var model = new ModelInfo(@"C:\Models\COT Framing_V1.2.sdb", "27", 6, "kN", "m", false);
        var online = new ApplicationState { Model = model, Connection = "Connected", Geometry = geometry, Baseline = geometry, Preview = geometry };
        online.SessionBaselineModel = model;
        online.SessionBaseline = geometry;
        foreach (var pattern in text.LoadPatternNames) online.LoadPatterns.Add(pattern);
        var onlineWorkflow = new WorkflowService(sap.Gateway, online);
        var onlineVm = new MainViewModel(online, onlineWorkflow, new NoSelection(), new ConfigService(sap.Gateway, online, onlineWorkflow));
        var warnings = new List<string>();
        onlineVm.WarningSink = (title, body) => warnings.Add($"{title}: {body}");
        onlineVm.OpenProjectFile(Path.Combine(folder, "project.json"));
        Check(warnings.Count == 0 && online.BaselineCaptured && online.Cables.Count > 0, "the benchmark project loads into the connected app" +
            (warnings.Count > 0 ? " — " + string.Join(" | ", warnings) : ""));
        online.Truss.MaximumHeight = online.Truss.MinimumHeight + online.Truss.Increment; // two heights keep the run short
        onlineVm.SelectedTab = 7;
        Check(onlineVm.SaveFemModel.CanExecute(null) && onlineVm.LoadFemModel.CanExecute(null),
            "Save Model and Load Model are enabled once SAP is connected and the config is loaded");

        var path = Path.Combine(Path.GetTempPath(), $"cotvcf-fem-{Guid.NewGuid():N}{FemModelFile.Extension}");
        try
        {
            var reads = sap.Reads;
            onlineVm.SaveFemModelFileAsync(path).GetAwaiter().GetResult();
            var saved = FemModelFile.Read(path);
            Check(warnings.Count == 0 && File.Exists(path) && sap.Writes == 0 && sap.Reads > reads,
                "Save Model reads SAP2000 (sections, loading, case names), writes the file and never writes the model");
            Check(saved.SectionProperties.Count == text.SectionProperties().Count && saved.Geometry!.Points.Count == geometry.Points.Count &&
                  saved.Geometry.Members.Count == geometry.Members.Count && saved.Loading!.Cases.Count == text.Loading().Cases.Count &&
                  saved.CaseRows.Count == onlineVm.FemCaseRows.Count && saved.Project?.Tab04CableForce?.Cables.Count == online.Cables.Count,
                "the file holds the geometry, every member's properties, the SAP loading, the Loads rows and Tabs 01–05");
            var tuple = saved.Loading!.Cases.SelectMany(item => item.Loads).FirstOrDefault();
            var original = text.Loading().Cases.SelectMany(item => item.Loads).FirstOrDefault();
            Check(tuple == original && tuple.Pattern is { Length: > 0 }, "load-case terms (pattern, factor) survive the round trip");

            var sapMaterials = onlineVm.ReadMaterialsFromSapAsync().GetAwaiter().GetResult();
            Check(sapMaterials.Count == 3 && sapMaterials.All(material => material.Source == "SAP2000") && sap.Writes == 0 &&
                  sapMaterials.Single(material => material.Kind == MaterialKind.Concrete).Fc == 70_000,
                "Define Materials → Get From SAP2000 reads the connected model's materials (read-only)");

            var onlineRuns = Analyze(onlineVm);
            Check(onlineRuns.Count > 0 && warnings.Count == 0, $"connected FEM run completes ({onlineRuns.Count} candidates) · {onlineVm.FemProgressText} · {string.Join(" | ", warnings)}");

            // A fresh, disconnected app: nothing may reach SAP2000.
            var offline = new ApplicationState();
            var silent = SapDouble.Create(text, geometry, refuseAll: true);
            var offlineWorkflow = new WorkflowService(silent.Gateway, offline);
            var vm = new MainViewModel(offline, offlineWorkflow, new NoSelection(), new ConfigService(silent.Gateway, offline, offlineWorkflow));
            vm.WarningSink = (title, body) => warnings.Add($"{title}: {body}");
            Check(!vm.HasWorkingModel && vm.LoadFemModel.CanExecute(null) && !vm.RunFemAnalysis.CanExecute(null) && !vm.SaveFemModel.CanExecute(null),
                "before connecting, only Load Model is available");
            vm.OpenFemModelFile(path);
            Check(warnings.Count == 0 && vm.IsOfflineModel && vm.HasWorkingModel && !offline.IsSapConnected && vm.SelectedTab == 7,
                "Load Model opens the saved model offline on 07 FEM" + (warnings.Count > 0 ? " — " + string.Join(" | ", warnings) : ""));
            Check(offline.BaselineCaptured && offline.Geometry.Points.Count == geometry.Points.Count &&
                  offline.AppliedPoints.Select(point => point.PointName).SequenceEqual(online.AppliedPoints.Select(point => point.PointName)) &&
                  offline.Cables.Count == online.Cables.Count && offline.Truss.CandidateCount == online.Truss.CandidateCount &&
                  vm.FemSectionProperties.Count == onlineVm.FemSectionProperties.Count &&
                  vm.FemCaseRows.Select(row => (row.Name, row.Definition, row.Run)).SequenceEqual(onlineVm.FemCaseRows.Select(row => (row.Name, row.Definition, row.Run))) &&
                  vm.FemCandidateSummary == onlineVm.FemCandidateSummary,
                "Tabs 01–03, the member properties, the Loads rows and the candidate space are restored");
            Check(vm.RunFemAnalysis.CanExecute(null) && vm.SaveFemModel.CanExecute(null) && !vm.ReadFemProperties.CanExecute(null) &&
                  !vm.LoadConfig.CanExecute(null) && vm.StatusLine.StartsWith("Offline model", StringComparison.Ordinal),
                "offline: Run Analysis and Save Model are enabled; Read from SAP and Load Config wait for a connection");

            var offlineRuns = Analyze(vm);
            Check(warnings.Count == 0 && silent.Reads == 0 && silent.Writes == 0 && offlineRuns.Count == onlineRuns.Count,
                $"the offline FEM run completes all {offlineRuns.Count} candidates without calling SAP2000");
            var same = offlineRuns.Zip(onlineRuns).All(pair => pair.First.Id == pair.Second.Id &&
                pair.First.Result.Joints.Zip(pair.Second.Result.Joints).All(joint =>
                    Math.Abs(joint.First.UZM - joint.Second.UZM) < 1e-12 && Math.Abs(joint.First.UXM - joint.Second.UXM) < 1e-12) &&
                pair.First.Result.Cables.Zip(pair.Second.Result.Cables).All(cable => Math.Abs(cable.First.AxialForceKN - cable.Second.AxialForceKN) < 1e-9));
            Check(same, "offline results equal the connected run (every joint displacement and cable force)");

            // File → Default Model sets the connected model aside and opens the built-in default model.
            Check(onlineVm.LoadDefaultModel.CanExecute(null), "File → Default Model is available while connected");
            onlineVm.LoadDefaultModel.Execute(null);
            Check(warnings.Count == 0 && onlineVm.IsDefaultModel && !online.IsSapConnected && online.Model?.Name == "COT Framing_V1.2_100m.sdb" &&
                  online.Cables.Count == 16 && onlineVm.FemRuns.Count == 0 && onlineVm.SelectedTab == 7 && onlineVm.RunFemAnalysis.CanExecute(null) && sap.Writes == 0,
                "File → Default Model replaces the connected model with the default model, ready to run, without touching SAP2000");
            Check(vm.LoadDefaultModel.CanExecute(null), "File → Default Model is available on an offline model");
            vm.LoadDefaultModel.Execute(null);
            Check(warnings.Count == 0 && vm.IsDefaultModel && offline.Cables.Count == 16 && vm.FemRuns.Count == 0,
                "File → Default Model replaces a loaded model with the default model");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        RunDefaultModel();
    }

    /// <summary>The built-in default model (COT Framing_V1.2_100m) opens a disconnected app on 07 FEM, ready to run.</summary>
    private static void RunDefaultModel()
    {
        var file = FemModelFile.ReadDefault();
        var state = new ApplicationState();
        var silent = SapDouble.Create(null!, GeometryModel.Empty, refuseAll: true);
        var workflow = new WorkflowService(silent.Gateway, state);
        var vm = new MainViewModel(state, workflow, new NoSelection(), new ConfigService(silent.Gateway, state, workflow));
        var warnings = new List<string>();
        vm.WarningSink = (title, body) => warnings.Add($"{title}: {body}");
        vm.LoadDefaultFemModel();
        Check(warnings.Count == 0 && vm.IsDefaultModel && vm.SelectedTab == 7 && state.Model?.Name == "COT Framing_V1.2_100m.sdb" &&
              state.Geometry.Points.Count == file.Geometry!.Points.Count && state.Geometry.Members.Count == file.Geometry.Members.Count &&
              vm.FemSectionProperties.Count == file.SectionProperties.Count && state.Cables.Count == 16 && state.AppliedPoints.Count == 11,
            $"the default model {FemModelFile.DefaultModelName} opens offline on 07 FEM with its geometry, properties and Tabs 01–05" +
            (warnings.Count > 0 ? " — " + string.Join(" | ", warnings) : ""));
        Check(vm.RunFemAnalysis.CanExecute(null) && vm.FemStatus == "OFFLINE · READY",
            $"the default model is ready to run ({vm.FemCandidateSummary})");
        var runs = Analyze(vm);
        // Every candidate is attempted offline; a candidate whose equilibrium does not solve is reported, as in a connected run.
        var failed = vm.FemLog.Where(line => line.Contains(" · FAILED · ", StringComparison.Ordinal)).ToArray();
        foreach (var line in failed) Console.WriteLine("  default model: " + line[10..]);
        Check(warnings.Count == 0 && silent.Reads == 0 && runs.Count > 0 && runs.Count + failed.Length == 18 &&
              vm.FemProgressText.StartsWith($"Completed {runs.Count} of 18", StringComparison.Ordinal),
            $"the default case runs without SAP2000: {vm.FemProgressText}" + (warnings.Count > 0 ? " — " + string.Join(" | ", warnings) : ""));
        RunVcfGeometry(vm, state, silent, warnings);
        RunVcfBays();
        RunVcfOptimization();
        RunMaterials();
        RunSections();
        RunCompositeSections();
    }

    /// <summary>CFT and steel encased frame sections: parts, transformed properties, cost per metre, the FEM members that take
    /// them (E = Ecm, true self-weight) and save / load.</summary>
    private static void RunCompositeSections()
    {
        var cft = new ColumnSectionInput { Tube = TubeShape.Rectangular, TubeDepth = 800, TubeWidth = 800, TubeThickness = 20 };
        var parts = CompositeSection.Parts(FrameShape.FilledTube, cft);
        var transformed = CompositeSection.Transformed(parts, 35e6, 210e6, 200e6);
        Check(Math.Abs(parts.ConcreteArea - 0.5776) < 1e-12 && Math.Abs(parts.SteelArea - 0.0624) < 1e-12 &&
              Math.Abs(transformed.Area - (0.5776 + 6 * 0.0624)) < 1e-12 && Math.Abs(transformed.I33 - (Math.Pow(0.76, 4) / 12 + 6 * (Math.Pow(0.8, 4) - Math.Pow(0.76, 4)) / 12)) < 1e-12,
            "a CFT 800×800×20 has Ac 0.5776 and Aa 0.0624 m², transformed to the concrete with Ea/Ecm");
        var circular = CompositeSection.Parts(FrameShape.FilledTube, cft with { Tube = TubeShape.Circular });
        Check(Math.Abs(circular.ConcreteArea - Math.PI / 4 * 0.76 * 0.76) < 1e-12 && circular.ConcreteI33 == circular.ConcreteI22, "a circular CFT is a filled pipe");
        var encased = new ColumnSectionInput { Width = 1000, Depth = 1000, Cover = 40, BarDiameter = 32, BarsAlongWidth = 5, BarsAlongDepth = 3, LinkDiameter = 12, LinkSpacing = 300,
            SteelDepth = 600, SteelWidth = 500, FlangeThickness = 40, WebThickness = 25 };
        var src = CompositeSection.Parts(FrameShape.Encased, encased);
        var aa = 2 * 0.5 * 0.04 + 0.52 * 0.025;
        Check(src.Bars.Count == 16 && Math.Abs(src.SteelArea - aa) < 1e-12 && Math.Abs(src.RebarArea - 16 * Math.PI / 4 * 0.032 * 0.032) < 1e-12 &&
              Math.Abs(src.ConcreteArea + src.SteelArea + src.RebarArea - 1.0) < 1e-12 && src.LinkAreaPerMetre > 0,
            "a steel encased 1000×1000 with an I 600×500 and 16 Ø32 shares its area between concrete, steel and bars");

        var (vm, state, _, warnings) = DefaultModelApp();
        vm.EnsureMaterials();
        vm.EnsureSections();
        var concrete = vm.Materials.Where(item => item.Kind == MaterialKind.Concrete).MaxBy(item => item.Fc ?? 0)!;
        var steel = vm.Materials.First(item => item.Kind == MaterialKind.Steel);
        var section = FrameSectionProperty.Default(FrameShape.FilledTube, "CFT 800x20", concrete.Name) with { SteelMaterial = steel.Name, Composite = cft };
        section = section.WithCompositeOutline();
        var analysis = vm.AnalyseComposite(section)!;
        section = section with { Given = analysis.Values };
        var expectedCost = parts.ConcreteArea * concrete.UnitCostPerCubicMetre!.Value + parts.SteelArea * steel.MassPerVolume * steel.UnitCost!.Value;
        Check(section is { Depth: 0.8, Width: 0.8, WebThickness: 0.02, SapType: "FilledTube" } && analysis.Ecm == concrete.ElasticModulus &&
              analysis.Cost.CostPerM is { } cost && Math.Abs(cost - expectedCost) < 1e-6 && analysis.Cost.Missing.Count == 0,
            $"a CFT section costs its concrete volume × SGD/m³ plus its steel mass × SGD/t ({analysis.Cost.CostPerM:0} SGD/m)");

        vm.CommitFrameSections(vm.FrameSections.Append(section).ToArray(), new Dictionary<string, string>(StringComparer.Ordinal));
        vm.ApplyVcfGeometryAsync(state.Geometry, new Dictionary<string, string>(StringComparer.Ordinal) { ["VRM002"] = section.Name }).GetAwaiter().GetResult();
        var member = vm.FemSectionProperties.Single(property => property.MemberName == "VRM002");
        var trueWeight = parts.ConcreteArea * concrete.WeightPerVolume + parts.SteelArea * steel.WeightPerVolume;
        Check(warnings.Count == 0 && member.SectionName == section.Name && Math.Abs(member.GrossAreaM2 - analysis.Values.Area) < 1e-12 &&
              member.ElasticModulusKNPerM2 == concrete.ElasticModulus && Math.Abs(member.GrossAreaM2 * member.UnitWeightKNPerM3 - trueWeight) < 1e-9 &&
              member.Shape is { Kind: SectionShapeKind.Box, Depth: 0.8 },
            "a member with a CFT section takes the transformed A / I with E = Ecm and its true self-weight" + (warnings.Count > 0 ? " — " + string.Join(" | ", warnings) : ""));

        var path = Path.Combine(Path.GetTempPath(), $"cotvcf-composite-{Guid.NewGuid():N}{FemModelFile.Extension}");
        try
        {
            vm.SaveFemModelFileAsync(path).GetAwaiter().GetResult();
            var (reopened, _, _, _) = DefaultModelApp();
            reopened.OpenFemModelFile(path);
            Check(reopened.FrameSections.SingleOrDefault(item => item.Name == section.Name) is { Shape: FrameShape.FilledTube, Composite: { TubeThickness: 20 } } saved &&
                  saved.SteelMaterial == steel.Name, "a CFT section is saved with the model and loaded again");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>Define → Section Properties on the default model: the library, a change reaching the FEM members, a library
    /// section used by VCF Geometry, save / load, Get From SAP2000.</summary>
    private static void RunSections()
    {
        var (vm, state, _, warnings) = DefaultModelApp();
        vm.EnsureMaterials();
        vm.EnsureSections();
        var shs = vm.FrameSections.Single(section => section.Name == "SHS_1200x1200x120_S460");
        var cfts = vm.FrameSections.Single(section => section.Name.StartsWith("CFT", StringComparison.Ordinal));
        Check(vm.FrameSections.Count == 4 && vm.CableSections.Single().Name == "192T15.7" &&
              shs is { Shape: FrameShape.Box, Depth: 1.2, Width: 1.2, FlangeThickness: 0.12, WebThickness: 0.12, Material: "S460" } && Math.Abs(shs.Values().Area - 0.5184) < 1e-12 &&
              Math.Abs(shs.Values().I33 - 0.10202112) < 1e-9 && cfts.Shape == FrameShape.General && Math.Abs(cfts.Values().Area - 5.98609756097561) < 1e-9 &&
              vm.SectionUsage("SHS_1200x1200x120_S460") == 30,
            "without SAP2000 the section library starts from the member properties (SHS as a 1200×1200×120 box: A 0.5184, I33 0.10202; the CFT as a general section)");

        var thicker = shs with { WebThickness = 0.15 };
        vm.CommitFrameSections(vm.FrameSections.Select(section => section.Name == shs.Name ? thicker : section).ToArray(), new Dictionary<string, string>(StringComparer.Ordinal));
        var vrm = vm.FemSectionProperties.Single(property => property.MemberName == "VRM001");
        var i33 = (Math.Pow(1.2, 4) - 0.9 * Math.Pow(0.96, 3)) / 12;
        Check(Math.Abs(vrm.AreaM2 - 0.576) < 1e-12 && Math.Abs(vrm.InPlaneInertiaM4!.Value - i33) < 1e-12 && vrm.Shape is { Kind: SectionShapeKind.Box, WebThickness: 0.15 } &&
              vm.FemSectionProperties.Single(property => property.MemberName == "TopChord").AreaM2 == cfts.Values().Area,
            "a changed frame section reaches every member that has it (A 0.576 m², I33 from the new web); other members are unchanged");

        var newBox = FrameSectionProperty.Default(FrameShape.Box, "SHS_1000x1000x100", "S460") with { Depth = 1.0, Width = 1.0, FlangeThickness = 0.1, WebThickness = 0.1 };
        vm.CommitFrameSections(vm.FrameSections.Append(newBox).ToArray(), new Dictionary<string, string>(StringComparer.Ordinal));
        Check(vm.VcfSectionNames(cable: false).Contains("SHS_1000x1000x100"), "a section added in Define → Section Properties is offered by VCF Geometry");
        vm.ApplyVcfGeometryAsync(state.Geometry, new Dictionary<string, string>(StringComparer.Ordinal) { ["VRM002"] = "SHS_1000x1000x100" }).GetAwaiter().GetResult();
        var vrm2 = vm.FemSectionProperties.Single(property => property.MemberName == "VRM002");
        Check(warnings.Count == 0 && vrm2.SectionName == "SHS_1000x1000x100" && Math.Abs(vrm2.AreaM2 - 0.36) < 1e-12 && vrm2.ElasticModulusKNPerM2 > 2e8,
            "VCF Geometry can assign a library section no member had yet (A 0.36 m², S460 from the materials)" + (warnings.Count > 0 ? " — " + string.Join(" | ", warnings) : ""));

        var strand = vm.CableSections.Single() with { Area = 0.03 };
        vm.CommitStrandSections([strand], new Dictionary<string, string>(StringComparer.Ordinal), tendons: false);
        Check(vm.FemSectionProperties.Where(property => property.IsCable).All(property => Math.Abs(property.AreaM2 - 0.03) < 1e-12) &&
              state.Cables.All(cable => cable.SectionAreaM2 == 0.03),
            "a changed cable section reaches every cable and its Tab 03 area");

        var path = Path.Combine(Path.GetTempPath(), $"cotvcf-sections-{Guid.NewGuid():N}{FemModelFile.Extension}");
        try
        {
            vm.SaveFemModelFileAsync(path).GetAwaiter().GetResult();
            var (reopened, _, _, reopenWarnings) = DefaultModelApp();
            reopened.OpenFemModelFile(path);
            Check(reopenWarnings.Count == 0 && reopened.FrameSections.Any(section => section.Name == "SHS_1000x1000x100") && reopened.CableSections.Single().Area == 0.03,
                "the section libraries are saved with the model and loaded again");
        }
        finally { if (File.Exists(path)) File.Delete(path); }

        var running = SapDouble.Create(SapModelText.Read(Path.Combine(FemSapBenchmark.DefaultFolder, "model.s2k")), GeometryModel.Empty);
        var offlineState = new ApplicationState();
        var offlineWorkflow = new WorkflowService(running.Gateway, offlineState);
        var offlineVm = new MainViewModel(offlineState, offlineWorkflow, new NoSelection(), new ConfigService(running.Gateway, offlineState, offlineWorkflow));
        offlineVm.LoadDefaultFemModel();
        var library = offlineVm.ReadSectionsFromSapAsync().GetAwaiter().GetResult();
        Check(library.Frames.Count == 2 && library.Cables.Count == 1 && running.Writes == 0 && offlineVm.IsOfflineModel,
            "Section Properties → Get From SAP2000 reads the running SAP2000's sections without Connect SAP (read-only)");
    }

    /// <summary>Define → Materials on the default model: the library, a change reaching the FEM members, save / load.</summary>
    private static void RunMaterials()
    {
        var (vm, state, _, warnings) = DefaultModelApp();
        vm.EnsureMaterials();
        Check(vm.Materials.Take(3).Select(material => material.Name).SequenceEqual(["C70/85", "HDPE Galvanised Strand T15.7 – Y1860S7", "S460"]) &&
              vm.Materials.Single(material => material.Name == "S460") is { Kind: MaterialKind.Steel, ElasticModulus: > 209_999_999 and < 210_000_001, Source: "Model" } &&
              vm.Materials.Single(material => material.Name.StartsWith("HDPE", StringComparison.Ordinal)) is { Kind: MaterialKind.Tendon, Fy: null } &&
              vm.MaterialUsage("S460") == 30,
            "without SAP2000 the material library starts from the member properties (C70/85 concrete, S460 steel, the strand as tendon)");
        string[] standard = ["S355", "S460", "S690", "C70/85", "C80/95", "C90/105", "C100/115", "C120/135", "C130/145", "C150/165", "C175/190", "C185/200"];
        Check(MaterialLibrary.Standard.Select(material => material.Name).SequenceEqual(standard) && standard.All(name => vm.Materials.Count(material => material.Name == name) == 1) &&
              vm.Materials.Count == 3 + standard.Length - 2 &&
              vm.Materials.Single(material => material.Name == "S460") is { Fy: 460_000, Fu: 540_000, Grade: "S460M", UnitCost: 4860 } &&
              vm.Materials.Single(material => material.Name == "C70/85") is { Fc: 70_000, ElasticModulus: > 40_999_999 and < 41_000_001, Source: "Model" } &&
              vm.Materials.Single(material => material.Name == "S355") is { Fy: 355_000, Fu: 470_000, ElasticModulus: 210_000_000, Source: "Library", UnitCost: > 3600 and < 3700 } &&
              vm.Materials.Single(material => material.Name == "S690") is { Fy: 690_000, Fu: 770_000, UnitCost: 7500 } &&
              vm.Materials.Single(material => material.Name == "C90/105") is { Fc: 90_000, ElasticModulus: 43_631_000 } c90 && Math.Abs(c90.UnitCostPerCubicMetre!.Value - 280) < 1e-6 &&
              Math.Abs(vm.Materials.Single(material => material.Name == "C150/165").UnitCostPerCubicMetre!.Value - 1200) < 1e-6 &&
              vm.Materials.Single(material => material.Name == "C185/200") is { Fc: 185_000, WeightPerVolume: 25.5, UnitCost: > 0 },
            "Define → Materials offers the standard S355 / S460 / S690 and C70/85 … C185/200 with strengths, EC2 Ecm and SGD costs; the model's C70/85 and S460 keep their E and gain fck / fy");

        // unit costs (SGD): project rates, BCA averages, estimates
        double PerTonne(MaterialProperty material) => MaterialCosts.Suggest(material).PerTonne ?? double.NaN;
        var concrete = MaterialProperty.Default(MaterialKind.Concrete, "C80/95") with { Fc = 80_000 };
        var c3037 = MaterialProperty.Default(MaterialKind.Concrete, "C30/37");
        var c7085 = vm.Materials.Single(material => material.Name == "C70/85");
        Check(Math.Abs(PerTonne(concrete) * concrete.MassPerVolume - 240) < 1e-9 && Math.Abs(PerTonne(c3037) * c3037.MassPerVolume - 121) < 1e-9 &&
              Math.Abs(c7085.UnitCost!.Value * c7085.MassPerVolume - (121 + 40 * 119.0 / 50)) < 1e-6 && c7085.CostNote.Contains("interpolated", StringComparison.Ordinal) &&
              Math.Abs(PerTonne(MaterialProperty.Default(MaterialKind.Concrete, "C100") with { Fc = 100_000 }) * concrete.MassPerVolume - 320) < 1e-9 &&
              vm.Materials.Single(material => material.Name == "S460").UnitCost == 4860 &&
              PerTonne(MaterialProperty.Default(MaterialKind.Steel, "S690") with { Fy = 690_000 }) == 7500 &&
              Math.Abs(PerTonne(MaterialProperty.Default(MaterialKind.Steel, "S355") with { Fy = 355_000 }) - (4860 - 105 * 2640.0 / 230)) < 1e-9 &&
              PerTonne(MaterialProperty.Default(MaterialKind.Rebar, "Rebar")) == 690 &&
              vm.Materials.Single(material => material.Kind == MaterialKind.Tendon).UnitCost == 12000 &&
              MaterialCosts.WithDefaultCost(MaterialProperty.Default(MaterialKind.Tendon, "Old") with { UnitCost = 1000, CostNote = "≈1,000 SGD/t — estimate" }).UnitCost == 12000 &&
              MaterialCosts.WithDefaultCost(MaterialProperty.Default(MaterialKind.Tendon, "Quoted") with { UnitCost = 9000, CostNote = "Entered rate" }).UnitCost == 9000,
            "materials carry a unit cost in SGD/t: C80/95 240 and 100 MPa 320 /m³, S460 4,860, S690 7,500 and tendon / cable 12,000 /t (project), C30/37 121 /m³ and rebar 690 /t (BCA), C70/85 / S355 estimated");

        var steel = vm.Materials.Single(material => material.Name == "S460");
        var edited = vm.Materials.Select(material => material.Name == "S460"
            ? material with { Name = "S460 mod", ElasticModulus = 200_000_000, WeightPerVolume = 78.5 } : material).ToArray();
        vm.CommitMaterials(edited, new Dictionary<string, string>(StringComparer.Ordinal) { ["S460"] = "S460 mod" });
        var vrm = vm.FemSectionProperties.Single(property => property.MemberName == "VRM001");
        Check(vrm.MaterialName == "S460 mod" && vrm.ElasticModulusKNPerM2 == 200_000_000 && vrm.UnitWeightKNPerM3 == 78.5 &&
              Math.Abs(vrm.UnitMassTPerM3 - 78.5 / MaterialProperty.Gravity) < 1e-12 && vm.MaterialUsage("S460 mod") == 30 && vm.MaterialUsage("S460") == 0 &&
              vm.FemSectionProperties.Single(property => property.MemberName == "TopChord").ElasticModulusKNPerM2 is > 40_999_999 and < 41_000_001,
            "a renamed / changed material reaches every member that uses it (E, unit weight, unit mass); other members are unchanged");

        // Get From SAP2000 on an offline model: attaches to the running SAP2000 for the read only; the app stays offline
        var running = SapDouble.Create(SapModelText.Read(Path.Combine(FemSapBenchmark.DefaultFolder, "model.s2k")), GeometryModel.Empty);
        var offlineState = new ApplicationState();
        var offlineWorkflow = new WorkflowService(running.Gateway, offlineState);
        var offlineVm = new MainViewModel(offlineState, offlineWorkflow, new NoSelection(), new ConfigService(running.Gateway, offlineState, offlineWorkflow));
        offlineVm.WarningSink = (title, body) => warnings.Add($"{title}: {body}");
        offlineVm.LoadDefaultFemModel();
        var fromSap = offlineVm.ReadMaterialsFromSapAsync().GetAwaiter().GetResult();
        Check(fromSap.Count == 3 && running.Writes == 0 && offlineVm.IsOfflineModel && !offlineState.IsSapConnected && offlineState.Model?.Name == "COT Framing_V1.2_100m.sdb",
            "Get From SAP2000 works without Connect SAP: it attaches to the running SAP2000 for the read and the app keeps its offline model");

        var path = Path.Combine(Path.GetTempPath(), $"cotvcf-materials-{Guid.NewGuid():N}{FemModelFile.Extension}");
        try
        {
            vm.SaveFemModelFileAsync(path).GetAwaiter().GetResult();
            var (reopened, _, _, reopenWarnings) = DefaultModelApp();
            reopened.OpenFemModelFile(path);
            Check(warnings.Count == 0 && reopenWarnings.Count == 0 && reopened.Materials.Any(material => material.Name == "S460 mod" && material.ElasticModulus == 200_000_000 && material.UnitCost == 4860),
                "the material library is saved with the model and loaded again");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>Edit → VCF Geometry on the default model: read the system, rebuild it from new parameters, apply it.</summary>
    private static void RunVcfGeometry(MainViewModel vm, ApplicationState state, SapDouble silent, List<string> warnings)
    {
        var analysis = VcfGeometry.Analyze(state.Geometry, state.Truss);
        var p = analysis.Parameters;
        Check(analysis.Bays == 10 && analysis.UniformBays && Near(p.BayWidth, 10) && Near(p.TrussDepth, 20) && Near(p.MiddleChordHeight, 10) &&
              Near(p.ClearHeight, 20) && p.BackSpan is { } span && Near(span, 10) && p.BackStayHeight is { } stay && Near(stay, 10),
            "VCF Geometry reads the default model: 10 bays of 10 m, truss depth 20 m, middle chord 10 m, clear height 20 m, back-span 10 m, back-stay 10 m");
        Check(analysis.Roles["TopChord"] == VcfMemberRole.TopChord && analysis.Roles["VRM015"] == VcfMemberRole.MiddleChord &&
              analysis.Roles["BottomChord"] == VcfMemberRole.BottomChord && analysis.Roles["VRM001"] == VcfMemberRole.VerticalChord &&
              new[] { "170", "181", "186", "187" }.All(name => analysis.Roles[name] == VcfMemberRole.MegaColumn) &&
              new[] { "188", "189", "190", "191" }.All(name => analysis.Roles[name] == VcfMemberRole.EndFrame) &&
              analysis.Roles["Cable1a-2"] == VcfMemberRole.Cable,
            "VCF Geometry tells the chords, vertical chords, mega columns (through the truss and below it), end frames and cables apart");

        var edited = new VcfGeometryParameters(BayWidth: 11, TrussDepth: 25, MiddleChordHeight: 12, ClearHeight: 22, BackSpan: 12, BackStayHeight: 9);
        var geometry = VcfGeometry.Build(analysis, edited);
        Position At(string name) => geometry.Points[name].Position;
        Check(Near(At("TTrPt001").X, -55) && Near(At("TTrPt001").Z, 27) && Near(At("TTrPt011").X, 55) && Near(At("MTrPt003").Z, 14) &&
              Near(At("BTrPt006").X, 0) && Near(At("BTrPt006").Z, 2) && Near(At("201").X, -67) && Near(At("201").Z, -11) &&
              Near(At("198").X, -55) && Near(At("198").Z, -20) && Near(At("205").X, 67) &&
              geometry.Points.Keys.SequenceEqual(state.Geometry.Points.Keys) && geometry.Members.SequenceEqual(state.Geometry.Members),
            "VCF Geometry rebuilds every node from the parameters (bay 11, depth 25, middle 12, clear 22, back-span 12, back-stay 9) and keeps every name");
        Check(VcfGeometry.Validate(analysis, edited with { MiddleChordHeight = 30 }).Count == 1 &&
              VcfGeometry.Validate(analysis, edited with { BackStayHeight = 30 }).Count == 1 &&
              VcfGeometry.Validate(analysis, edited with { BayWidth = 0 }).Count == 1,
            "VCF Geometry refuses a middle chord outside the truss, a back-stay above the bottom chord and a zero bay");

        var candidates = vm.FemCandidateSummary;
        var megaColumn = vm.FemSectionProperties.First(property => property.MemberName == "170");
        vm.ApplyVcfGeometryAsync(geometry, new Dictionary<string, string>(StringComparer.Ordinal) { ["VRM001"] = megaColumn.SectionName! })
            .GetAwaiter().GetResult();
        var vrm001 = vm.FemSectionProperties.Single(property => property.MemberName == "VRM001");
        Check(warnings.Count == 0 && Near(state.Geometry.Points["TTrPt001"].Position.Z, 27) && Near(state.Baseline.Points["TTrPt001"].Position.X, -55) &&
              state.AppliedPoints.Single(point => point.PointName == "TTrPt001") is { } loaded && Near(loaded.X, -55) && Near(loaded.Z, 27) &&
              vrm001.SectionName == megaColumn.SectionName && Near(vrm001.AreaM2, megaColumn.AreaM2) && vrm001.EndRotationReleases == "None" &&
              state.BaselineCaptured && vm.FemCandidateSummary == candidates && vm.FemRuns.Count == 0 && vm.IsOfflineModel,
            "Apply writes the geometry, the loading points and the chosen section to the working model; the candidate space is kept" +
            (warnings.Count > 0 ? " — " + string.Join(" | ", warnings) : ""));
        var runs = Analyze(vm);
        Check(warnings.Count == 0 && silent.Reads == 0 && runs.Count > 0 &&
              runs[0].Result.Joints.Any(joint => joint.PointName == "TTrPt001"),
            $"FEM runs on the edited VCF geometry without SAP2000 ({runs.Count} candidates)");
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

    internal static (MainViewModel Vm, ApplicationState State, SapDouble Sap, List<string> Warnings) DefaultModelApp()
    {
        var state = new ApplicationState();
        var silent = SapDouble.Create(null!, GeometryModel.Empty, refuseAll: true);
        var workflow = new WorkflowService(silent.Gateway, state);
        var vm = new MainViewModel(state, workflow, new NoSelection(), new ConfigService(silent.Gateway, state, workflow));
        var warnings = new List<string>();
        vm.WarningSink = (title, body) => warnings.Add($"{title}: {body}");
        vm.LoadDefaultFemModel();
        return (vm, state, silent, warnings);
    }

    /// <summary>Edit → VCF Geometry with another number of bays.</summary>
    public static void RunVcfBays()
    {
        // 10 → 12 bays
        var (vm, state, silent, warnings) = DefaultModelApp();
        var analysis = VcfGeometry.Analyze(state.Geometry, state.Truss);
        var topology = VcfGeometry.ChangeBays(analysis, 12);
        var g = topology.Geometry;
        StructuralMember M(string name) => g.Members.Single(member => member.Name == name);
        var leftFan = g.Members.Where(member => member.Kind == MemberKind.Cable && member.Start == "TTrPt001" && member.End.StartsWith("BTrPt", StringComparison.Ordinal)).ToArray();
        var rightFan = g.Members.Where(member => member.Kind == MemberKind.Cable && member.Start == "TTrPt013" && member.End.StartsWith("BTrPt", StringComparison.Ordinal)).ToArray();
        Check(g.Points.Count == 47 && topology.TopPoints.Count == 13 && topology.TopPoints[^1] == "TTrPt013" &&
              M("TopChord") is { Start: "TTrPt001", End: "TTrPt013" } && M("170") is { Start: "BTrPt013", End: "TTrPt013" } &&
              M("187") is { Start: "199", End: "BTrPt013" } && M("Cable1b-2") is { Start: "TTrPt013", End: "204" } &&
              g.Members.Count(member => member.Start.StartsWith("MTrPt", StringComparison.Ordinal) && member.End.StartsWith("MTrPt", StringComparison.Ordinal)) == 12 &&
              leftFan.Length == 6 && rightFan.Length == 6 && leftFan.Select(c => c.End).Order().SequenceEqual(["BTrPt002", "BTrPt003", "BTrPt004", "BTrPt005", "BTrPt006", "BTrPt007"]) &&
              rightFan.Select(c => c.End).Order().SequenceEqual(["BTrPt007", "BTrPt008", "BTrPt009", "BTrPt010", "BTrPt011", "BTrPt012"]) &&
              topology.AddedCables.Keys.Order().SequenceEqual(["Cable9a-2", "Cable9b-2"]) && g.Members.Select(member => member.Name).Distinct().Count() == g.Members.Count,
            "12 bays: chord points renumbered to TTrPt013, end members follow the end line, 12 middle-chord bays, fans of 6 to mid-span (Cable9a-2 / 9b-2 added), unique names");
        Check(g.Points["TTrPt013"].Position.X - g.Points["TTrPt001"].Position.X is var spanX && Near(spanX, 120) &&
              Near(g.Points["201"].Position.X, g.Points["TTrPt001"].Position.X - 10),
            "12 bays at the old 10 m bay width: 120 m between mega columns; the end frames move with the ends");

        var working = VcfGeometry.Analyze(topology);
        var geometry = VcfGeometry.Build(working, working.Parameters with { BayWidth = 12.5 });
        vm.ApplyVcfGeometryAsync(geometry, new Dictionary<string, string>(StringComparer.Ordinal), topology).GetAwaiter().GetResult();
        Check(warnings.Count == 0 && state.Truss.TopPoints.Count == 13 && state.AppliedPoints.Count == 13 && state.Cables.Count == 18 &&
              state.Cables.Single(c => c.CableName == "Cable9a-2").SectionName is { Length: > 0 } &&
              vm.FemSectionProperties.Count == geometry.Members.Count && vm.FemSectionProperties.Any(property => property.MemberName == "Cable9b-2") &&
              state.OutputSettings.DeflectionPoints.Count == 13 && state.OutputSettings.CableNames.Count == 18 &&
              state.OutputSettings.MemberForceGroups.Single(group => group.Name == "VRM").Members.Count > 28 &&
              Near(state.Geometry.Points["TTrPt013"].Position.X - state.Geometry.Points["TTrPt001"].Position.X, 150),
            "Apply with 12 bays of 12.5 m: loading points, chord groups, Tab 03 cables (new fan cables copy their template), member properties and output lists follow" +
            (warnings.Count > 0 ? " — " + string.Join(" | ", warnings) : ""));
        var runs = Analyze(vm);
        Check(warnings.Count == 0 && silent.Reads == 0 && runs.Count > 0 && runs[0].Result.Cables.Any(cable => cable.MemberName == "Cable9a-2"),
            $"FEM runs on 12 bays ({runs.Count} candidates, the new fan cables carry force) · {vm.FemCandidateSummary}");
        var path = Path.Combine(Path.GetTempPath(), $"cotvcf-bays-{Guid.NewGuid():N}{FemModelFile.Extension}");
        try
        {
            vm.SaveFemModelFileAsync(path).GetAwaiter().GetResult();
            var (reopened, reopenedState, _, reopenWarnings) = DefaultModelApp();
            reopened.OpenFemModelFile(path);
            Check(warnings.Count == 0 && reopenWarnings.Count == 0 && reopenedState.Geometry.Points.ContainsKey("TTrPt013") && reopenedState.Cables.Count == 18,
                "the 12-bay model saves and loads again with all Tabs 01–05 valid" + (reopenWarnings.Count > 0 ? " — " + string.Join(" | ", reopenWarnings) : ""));
        }
        finally { if (File.Exists(path)) File.Delete(path); }

        // 10 → 8 bays
        var (vm8, state8, _, warnings8) = DefaultModelApp();
        var analysis8 = VcfGeometry.Analyze(state8.Geometry, state8.Truss);
        var topology8 = VcfGeometry.ChangeBays(analysis8, 8);
        vm8.ApplyVcfGeometryAsync(VcfGeometry.Build(VcfGeometry.Analyze(topology8), analysis8.Parameters), new Dictionary<string, string>(StringComparer.Ordinal), topology8)
            .GetAwaiter().GetResult();
        Check(warnings8.Count == 0 && state8.Geometry.Points.ContainsKey("TTrPt009") && !state8.Geometry.Points.ContainsKey("TTrPt010") &&
              state8.Cables.Count == 14 && state8.Cables.All(c => c.CableName is not ("Cable6a-2" or "Cable6b-2")) &&
              state8.Cables.Where(c => c.ReferenceCableName is not null).All(c => state8.Cables.Any(other => other.CableName == c.ReferenceCableName)) &&
              vm8.FemCandidateSummary.Contains("FEM runs", StringComparison.Ordinal) && vm8.RunFemAnalysis.CanExecute(null),
            "8 bays: the outer fan cables (Cable6a/6b) are removed with their Tab 03 rows; cables that followed them become independent; FEM is ready" +
            (warnings8.Count > 0 ? " — " + string.Join(" | ", warnings8) : ""));
    }

    /// <summary>Run → Optimization VCF → Run Optimization on the default model: every case is analysed with the internal FEM,
    /// the deflection follows the inputs, the case equal to the model matches Run FEM Analysis, and nothing changes.</summary>
    private static void RunVcfOptimization()
    {
        var (vm, state, _, warnings) = DefaultModelApp();
        var analysis = VcfGeometry.Analyze(state.Geometry, state.Truss);
        var current = VcfOptimization.Current(analysis);
        var cables = state.Cables.Where(cable => !cable.IsFollower).ToDictionary(cable => cable.CableName, cable => VcfOptimization.CableRange(cable)!, StringComparer.Ordinal);
        var live = state.LoadDefinitions.Single(load => load.ApplyIncrementStep);
        var dead = state.LoadDefinitions.Single(load => !load.ApplyIncrementStep);
        // one Tab 02 height step above the baseline (H02): the baseline itself (H01) does not reach equilibrium in the
        // internal FEM, in Run FEM Analysis as here
        var depth = VcfRange.Fixed(analysis.Parameters.TrussDepth + state.Truss.Increment, 0.5);
        VcfOptimizationSettings Settings(VcfRange bays, VcfRange bay, VcfRange liveQz) => current with
        {
            Geometry = new Dictionary<string, VcfRange>(current.Geometry) { ["Bays"] = bays, ["Bay"] = bay, ["Depth"] = depth },
            CableForces = cables,
            StoreyLoads = new Dictionary<string, VcfRange>(StringComparer.Ordinal)
            {
                [dead.LoadName] = VcfOptimization.LoadRange(dead)!, [live.LoadName] = liveQz,
                [VcfOptimization.StoriesKey] = VcfRange.Fixed(Math.Truncate(state.NumberOfStories), 1),
                [VcfOptimization.TributaryKey] = VcfRange.Fixed(state.TributaryWidthB, 0.5),
            },
        };
        var geometryBefore = state.Geometry;
        var propertiesBefore = vm.FemSectionProperties.ToArray();

        // 10 / 12 m bays × LL at its Tab 01 minimum and 0 kPa: 4 cases; the deflection grows with the span and the load
        var rows = Optimize(vm, Settings(VcfRange.Fixed(10, 1), new VcfRange(10, 12, 2), new VcfRange(0, live.MinQz!.Value, live.MinQz.Value)));
        double Defl(int caseNumber) => rows.Single(row => row.Case == caseNumber).MaxDeflectionMm!.Value;
        Check(rows.Length == 4 && rows.All(row => row.MaxDeflectionMm is > 0 && row.Status.StartsWith("Analysed", StringComparison.Ordinal)) &&
              vm.VcfOptimizationColumns.Select(axis => axis.Label).SequenceEqual(["Bay [m]", $"qz {live.LoadName} [kPa]"]) &&
              Defl(3) > Defl(1) && Defl(4) > Defl(2) && Defl(2) > Defl(1) && Defl(4) > Defl(3) &&
              vm.VcfOptimizationBest.Contains("case 1", StringComparison.Ordinal),
            $"Run Optimization analyses every case (4) and reports its max deflection: wider bays and more live load deflect more ({Defl(1):0.0} … {Defl(4):0.0} mm); the least is named");

        // the same geometry applied with Edit → VCF Geometry and analysed with Run FEM Analysis gives the same deflection
        var (applied, appliedState, _, _) = DefaultModelApp();
        var deeper = analysis.Parameters with { TrussDepth = depth.Min, MiddleChordHeight = VcfOptimization.MiddleChordRatio * depth.Min };
        applied.ApplyVcfGeometryAsync(VcfGeometry.Build(VcfGeometry.Analyze(appliedState.Geometry, appliedState.Truss), deeper),
            new Dictionary<string, string>(StringComparer.Ordinal)).GetAwaiter().GetResult();
        var appliedRuns = Analyze(applied);
        var femMax = appliedRuns.Select(run => (run.Results ?? new Dictionary<string, FemAnalysisResult> { [""] = run.Result }).Values
            .SelectMany(result => result.Joints).Max(joint => -joint.UZM * 1000)).ToArray();
        Check(femMax.Any(value => Math.Abs(value - Defl(2)) < 1e-6),
            $"A case ({Defl(2):0.000} mm) matches Run FEM Analysis on the same geometry applied with Edit -> VCF Geometry " +
            $"(FEM max deflections {string.Join(", ", femMax.Select(value => value.ToString("0.000")))})");

        // the deflection read from the SLS load case, checked against span / 250
        const string sls = "NL SLS001: 1.0DL+1.0LL";
        var (slsCases, slsCombinations) = vm.VcfDeflectionResults();
        var slsRows = Optimize(vm, Settings(VcfRange.Fixed(10, 1), VcfRange.Fixed(10, 1), VcfRange.Fixed(live.MinQz.Value, 1)) with { DeflectionResult = sls, SpanRatio = 250 });
        var slsFem = appliedRuns.Where(run => run.Results?.ContainsKey(sls) == true)
            .Select(run => run.Results![sls].Joints.Max(joint => -joint.UZM * 1000)).ToArray();
        var slsRow = slsRows.Single();
        Check(VcfOptimization.DefaultDeflectionResult(slsCases, slsCombinations) == sls && slsRow.Governing == sls &&
              slsFem.Any(value => Math.Abs(value - slsRow.MaxDeflectionMm!.Value) < 1e-6) && slsRow.MaxDeflectionMm < Defl(2) &&
              slsRow.Span == "100" && slsRow.Limit == (100_000.0 / 250).ToString("0.0") &&
              slsRow.Check == (slsRow.MaxDeflectionMm <= 400 ? "OK" : "NG") && slsRow.Ratio == (slsRow.MaxDeflectionMm!.Value / 400).ToString("0.00") &&
              vm.VcfOptimizationBest.Contains("span / 250", StringComparison.Ordinal),
            $"The deflection is read from '{sls}' (the default) and matches Run FEM Analysis there ({slsRow.Deflection} mm); " +
            $"checked against span / 250 = 100 m / 250 = {slsRow.Limit} mm → δ / limit {slsRow.Ratio} {slsRow.Check}");

        // total cost = Σ gross area × material unit cost per m³ × member length, recomputed here from the same case geometry
        var caseGeometry = VcfGeometry.Build(analysis, deeper);
        var byMember = vm.FemSectionProperties.ToDictionary(property => property.MemberName, StringComparer.Ordinal);
        double expectedCost = 0;
        foreach (var member in caseGeometry.Members.Where(member => byMember.ContainsKey(member.Name)))
        {
            var property = byMember[member.Name];
            var rate = vm.Materials.Single(material => material.Name == property.MaterialName).UnitCostPerCubicMetre!.Value;
            var (a, b) = (caseGeometry.Points[member.Start].Position, caseGeometry.Points[member.End].Position);
            expectedCost += (property.GrossAreaM2 > 0 ? property.GrossAreaM2 : property.AreaM2) * rate *
                Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y) + (b.Z - a.Z) * (b.Z - a.Z));
        }
        Check(slsRow.CostSgd is { } slsCost && Math.Abs(slsCost - expectedCost) < 1e-6 * expectedCost && !slsRow.Status.Contains("No unit cost", StringComparison.Ordinal) &&
              rows.Single(row => row.Case == 3).CostSgd > rows.Single(row => row.Case == 1).CostSgd &&
              rows.Single(row => row.Case == 1).CostSgd == rows.Single(row => row.Case == 2).CostSgd &&
              vm.VcfOptimizationBest.Contains("cheapest passing", StringComparison.Ordinal) == (slsRow.Check == "OK"),
            $"The run reports the total cost of the system: Σ gross area × material SGD/m³ × member length = SGD {slsRow.Cost}; " +
            $"12 m bays cost more than 10 m (SGD {rows.Single(row => row.Case == 3).Cost} vs {rows.Single(row => row.Case == 1).Cost}); the load does not change it");

        // another number of bays runs on the regenerated model
        var bayRows = Optimize(vm, Settings(new VcfRange(8, 12, 4), VcfRange.Fixed(10, 1), VcfRange.Fixed(live.MinQz.Value, 1)));
        Check(bayRows.Length == 2 && bayRows.All(row => row.MaxDeflectionMm is > 0) && bayRows[1].MaxDeflectionMm > bayRows[0].MaxDeflectionMm,
            $"8 and 12 bays run on the regenerated model; the longer span deflects more ({bayRows[0].Deflection} → {bayRows[1].Deflection} mm)" +
            (bayRows.Any(row => row.MaxDeflectionMm is null) ? " — " + string.Join(" | ", bayRows.Select(row => row.Status)) : ""));

        Check(ReferenceEquals(state.Geometry, geometryBefore) && vm.FemSectionProperties.SequenceEqual(propertiesBefore) && warnings.Count == 0,
            "Run Optimization leaves the working model, its member properties and SAP2000 unchanged" + (warnings.Count > 0 ? " — " + string.Join(" | ", warnings) : ""));
    }

    private static VcfOptimizationRow[] Optimize(MainViewModel vm, VcfOptimizationSettings settings)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            var task = vm.RunVcfOptimizationAsync(settings);
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        return vm.VcfOptimizationRows.ToArray();
    }

    /// <summary>Runs the FEM tab's analysis with a dispatcher, so progress reports land as they do in the app.</summary>
    private static IReadOnlyList<FemStudyRun> Analyze(MainViewModel vm)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            var task = vm.AnalyzeFemAsync();
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        return vm.FemRuns.ToArray();
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

    /// <summary>SAP2000 stand-in answering the FEM reads from the model text; any other call counts as a write.
    /// With <c>refuseAll</c> every call is counted (a disconnected app must make none).</summary>
    public class SapDouble : DispatchProxy
    {
        private SapModelText text = null!;
        private GeometryModel geometry = null!;
        private bool refuseAll;
        public int Reads { get; private set; }
        public int Writes { get; private set; }

        public static SapDouble Create(SapModelText text, GeometryModel geometry, bool refuseAll = false)
        {
            var proxy = DispatchProxy.Create<ISapGateway, SapDouble>();
            var double_ = (SapDouble)(object)proxy;
            double_.text = text; double_.geometry = geometry; double_.refuseAll = refuseAll;
            return double_;
        }

        public ISapGateway Gateway => (ISapGateway)(object)this;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (refuseAll) { Reads++; throw new InvalidOperationException($"SAP2000 is not connected ({method?.Name})."); }
            switch (method?.Name)
            {
                case nameof(ISapGateway.GetFemSectionPropertiesAsync):
                    Reads++; return Task.FromResult(text.SectionProperties());
                case nameof(ISapGateway.GetFemLoadingAsync):
                    Reads++; return Task.FromResult(text.Loading());
                case nameof(ISapGateway.GetLoadCaseNamesAsync):
                    Reads++; return Task.FromResult(text.LoadCaseNames);
                case nameof(ISapGateway.GetMaterialsAsync):
                    Reads++;
                    return Task.FromResult<IReadOnlyList<MaterialProperty>>([
                        MaterialProperty.Default(MaterialKind.Concrete, "C70/85") with { Grade = "C70/85", ElasticModulus = 41_000_000, Fc = 70_000, ExpectedFc = 70_000, Source = "SAP2000" },
                        MaterialProperty.Default(MaterialKind.Steel, "S460") with { Grade = "S460", Fy = 460_000, Fu = 550_000, Source = "SAP2000" },
                        MaterialProperty.Default(MaterialKind.Tendon, "HDPE Galvanised Strand T15.7 – Y1860S7") with { Source = "SAP2000" }]);
                case nameof(ISapGateway.GetSectionsAsync):
                    Reads++;
                    return Task.FromResult(new SectionLibrary(
                        [FrameSectionProperty.Default(FrameShape.Box, "SHS_1200x1200x120_S460", "S460") with { Depth = 1.2, Width = 1.2, FlangeThickness = 0.12, WebThickness = 0.12, Source = "SAP2000" },
                         FrameSectionProperty.Default(FrameShape.I, "UB610", "S355") with { Source = "SAP2000" }],
                        [new StrandSectionProperty { Name = "192T15.7", Material = "HDPE Galvanised Strand T15.7 – Y1860S7", Area = 0.028802317797652074, Source = "SAP2000" }],
                        []));
                case nameof(ISapGateway.ConnectAsync):
                    Reads++; return Task.FromResult(new ModelInfo(@"C:\Models\Running.sdb", "27", 6, "kN", "m", false));
                case "Dispose":
                    return null;
                default:
                    Writes++; throw new InvalidOperationException($"Unexpected SAP2000 call {method?.Name}.");
            }
        }
    }
}
