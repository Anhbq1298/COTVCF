using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Presentation;
using Mcotis.Desktop.Presentation.Controls;

/// <summary>
/// 07 FEM → Inputs → Save Model / Load Model, on the benchmark model (<c>tests/fem-sap-benchmark</c>): a model saved
/// while connected loads into a disconnected app, enables only the FEM run, and gives the same FEM results as the
/// connected run — without one call to SAP2000.
/// </summary>
internal static class FemModelFileChecks
{
    public static void Run()
    {
        RunBlankModelFlow();
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
        online.Truss.MaximumHeight = online.Truss.MinimumHeight + online.Truss.Increment; // an edited range, to see it saved
        onlineVm.SelectedTab = 7;
        Check(onlineVm.SaveFemModel.CanExecute(null) && onlineVm.LoadFemModel.CanExecute(null),
            "Save Model and Load Model are enabled once SAP is connected and the config is loaded");

        var path = Path.Combine(Path.GetTempPath(), $"mcotis-fem-{Guid.NewGuid():N}{FemModelFile.Extension}");
        try
        {
            // Run Analysis solves what Mcotis holds: it does not fetch the sections and loading from SAP2000 itself.
            var reads = sap.Reads;
            var refused = Analyze(onlineVm);
            Check(refused.Count == 0 && sap.Reads == reads && sap.Writes == 0 && warnings.Count == 1 &&
                  warnings[0].Contains("Read FEM Properties from SAP", StringComparison.Ordinal),
                "connected, before the FEM properties are read: Run Analysis makes no call to SAP2000 and points to Read FEM Properties from SAP" +
                (warnings.Count > 0 ? " — " + warnings[0] : ""));
            warnings.Clear();
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

            // The app was renamed: a file says Mcotis.FemModel now, and one saved under the old name must still open.
            var savedText = File.ReadAllText(path);
            var written = $"\"format\": \"{FemModelFileDto.FormatName}\"";
            Check(saved.Format == "Mcotis.FemModel" && savedText.Contains(written, StringComparison.Ordinal) &&
                  FemModelFile.Parse(savedText.Replace(written, $"\"format\": \"{FemModelFileDto.LegacyFormatName}\"")).Geometry!.Points.Count == geometry.Points.Count &&
                  Refuses(() => FemModelFile.Parse(savedText.Replace(written, "\"format\": \"Other.FemModel\""))),
                "the file is written as Mcotis.FemModel, one saved under the old format name still opens, another format is refused");

            var sapMaterials = onlineVm.ReadMaterialsFromSapAsync().GetAwaiter().GetResult();
            Check(sapMaterials.Count == 3 && sapMaterials.All(material => material.Source == "SAP2000") && sap.Writes == 0 &&
                  sapMaterials.Single(material => material.Kind == MaterialKind.Concrete).Fc == 70_000,
                "Define Materials → Get From SAP2000 reads the connected model's materials (read-only)");

            reads = sap.Reads;
            var onlineRuns = Analyze(onlineVm);
            Check(onlineRuns.Count == 1 && onlineRuns[0].Id == FemStudyService.ModelRunId && warnings.Count == 0 && sap.Reads == reads && sap.Writes == 0 &&
                  onlineVm.FemRunSummary.StartsWith("1 FEM run", StringComparison.Ordinal),
                $"connected: Run Analysis is one run of the model held, without a call to SAP2000 · {onlineVm.FemRunSummary} · {onlineVm.FemProgressText} · {string.Join(" | ", warnings)}");

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
                  vm.FemRunSummary == onlineVm.FemRunSummary,
                "Tabs 01–03, the member properties, the Loads rows and what Run Analysis solves are restored");
            Check(vm.RunFemAnalysis.CanExecute(null) && vm.SaveFemModel.CanExecute(null) && !vm.ReadFemProperties.CanExecute(null) &&
                  !vm.LoadConfig.CanExecute(null) && vm.StatusLine.StartsWith("Offline model", StringComparison.Ordinal),
                "offline: Run Analysis and Save Model are enabled; Read from SAP and Load Config wait for a connection");
            // 01 Loading only edits what the app holds, so its add / remove commands work on an offline model too
            var loadRows = offline.LoadDefinitions.Count;
            Check(vm.AddPoints.CanExecute(null) && vm.RemoveAppliedPoints.CanExecute(null) && vm.AddLoadDefinition.CanExecute(null), "offline: the 01 Loading add / remove commands are enabled");
            vm.AddLoadDefinition.Execute(null);
            var extraLoad = offline.LoadDefinitions[^1];
            Check(offline.LoadDefinitions.Count == loadRows + 1 && vm.DeleteLoadDefinition.CanExecute(extraLoad), "offline: + Add Load adds a load row to the model");
            vm.DeleteLoadDefinition.Execute(extraLoad);
            Check(offline.LoadDefinitions.Count == loadRows && silent.Reads == 0, "offline: the row is removed again, without a call to SAP2000");

            var offlineRuns = Analyze(vm);
            Check(warnings.Count == 0 && silent.Reads == 0 && silent.Writes == 0 && offlineRuns.Count == 1,
                "the offline FEM run solves the model without calling SAP2000");
            var same = offlineRuns.Zip(onlineRuns).All(pair => pair.First.Id == pair.Second.Id &&
                pair.First.Result.Joints.Zip(pair.Second.Result.Joints).All(joint =>
                    Math.Abs(joint.First.UZM - joint.Second.UZM) < 1e-12 && Math.Abs(joint.First.UXM - joint.Second.UXM) < 1e-12) &&
                pair.First.Result.Cables.Zip(pair.Second.Result.Cables).All(cable => Math.Abs(cable.First.AxialForceKN - cable.Second.AxialForceKN) < 1e-9));
            Check(same, "offline results equal the connected run (every joint displacement and cable force)");

            // File → Load VCF… sets the connected model aside and opens the built-in default model.
            Check(onlineVm.LoadDefaultModel.CanExecute(null), "File → Load VCF… is available while connected");
            onlineVm.LoadDefaultModel.Execute(null);
            Check(warnings.Count == 0 && onlineVm.IsDefaultModel && !online.IsSapConnected && online.Model?.Name == "COT Framing_V1.2_100m.sdb" &&
                  online.Cables.Count == 16 && onlineVm.FemRuns.Count == 0 && onlineVm.SelectedTab == 7 && onlineVm.RunFemAnalysis.CanExecute(null) && sap.Writes == 0,
                "File → Load VCF… replaces the connected model with the default model, ready to run, without touching SAP2000");
            Check(vm.LoadDefaultModel.CanExecute(null), "File → Load VCF… is available on an offline model");
            vm.LoadDefaultModel.Execute(null);
            Check(warnings.Count == 0 && vm.IsDefaultModel && offline.Cables.Count == 16 && vm.FemRuns.Count == 0,
                "File → Load VCF… replaces a loaded model with the default model");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        RunDefaultModel();
        RunDeleteObjects();
        RunFrameLocalAxes();
    }

    private static void RunFrameLocalAxes()
    {
        var state = new ApplicationState();
        var silent = SapDouble.Create(null!, GeometryModel.Empty, refuseAll: true);
        var workflow = new WorkflowService(silent.Gateway, state);
        var vm = new MainViewModel(state, workflow, new NoSelection(), new ConfigService(silent.Gateway, state, workflow));
        vm.LoadDefaultFemModel();
        var section = FrameSectionProperty.Default(FrameShape.Rectangle, "AXIS_CHECK", "S460") with { Depth = 2, Width = 0.4 };
        vm.CommitFrameSections(vm.FrameSections.Append(section).ToArray(), new Dictionary<string, string>());
        vm.AssignSection(["VRM001"], section.Name, cable: false);
        var before = vm.FemSectionProperties.Single(property => property.MemberName == "VRM001");
        var untouched = vm.FemSectionProperties.Single(property => property.MemberName == "VRM002");
        Check(before.LocalAngleDeg == 0 && before.InPlaneInertiaM4 is > 0,
            "assigning a rectangular section starts with its local 3-axis bending in the 2D FEM");

        var changed = vm.AssignFrameLocalAxis(["VRM001"], 90);
        var rotated = vm.FemSectionProperties.Single(property => property.MemberName == "VRM001");
        Check(changed == 1 && rotated.LocalAngleDeg == 90 &&
              Math.Abs(rotated.InPlaneInertiaM4!.Value - section.Values().I22) < 1e-12 &&
              vm.FemSectionProperties.Single(property => property.MemberName == "VRM002") == untouched &&
              vm.SharedViewerSections!["VRM001"].LocalAngleDeg == 90 && silent.Reads == 0 && silent.Writes == 0,
            "Assign Frame Local Axes rotates only the selected frame: 90° uses I22 in FEM and updates the 3D section view without SAP2000");

        var beam = new GeometryModel(new Dictionary<string, StructuralPoint>(StringComparer.Ordinal)
        {
            ["A"] = new("A", new(0, 0, 0)), ["B"] = new("B", new(5, 0, 0)), ["C"] = new("C", new(10, 0, 0)),
        }, [new("AB", "A", "B", MemberKind.Frame), new("BC", "B", "C", MemberKind.Frame)]);
        StructureModel3D.Scene Scene(FemSectionProperties property) => StructureModel3D.Build(beam,
            new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal)
            {
                ["AB"] = property with { MemberName = "AB" }, ["BC"] = property with { MemberName = "BC" },
            });
        var beforeScene = Scene(before);
        var rotatedScene = Scene(rotated);
        Check(beforeScene.MemberVisuals.Select(member => member.Name).SequenceEqual(["AB", "BC"]) &&
              rotatedScene.MemberVisuals.Count == 2 && beforeScene.Bounds.SizeZ > rotatedScene.Bounds.SizeZ * 4,
            "the 3D model keeps each frame identifiable for selection and turns its visible section with the local axes");
        double Deflection(FemSectionProperties property)
        {
            var frames = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal)
            {
                ["AB"] = property with { MemberName = "AB" }, ["BC"] = property with { MemberName = "BC" },
            };
            var result = new FemSolver().Solve(beam, frames, new Dictionary<string, FemSectionProperties>(),
                new Dictionary<string, double>(), [new("B", 0, -100)], ["A", "C"]);
            return -result.Joints.Single(joint => joint.PointName == "B").UZM;
        }
        Check(Math.Abs(Deflection(rotated) / Deflection(before) - before.InPlaneInertiaM4!.Value / rotated.InPlaneInertiaM4!.Value) < 1e-8,
            "the frame FEM deflection changes with the rotated in-plane inertia");

        // the deflected shape in 3D: each member through its stations, enlarged, shaded by |U|; the model as built as a guide
        static FemFrameStation Station(double fraction, double uz) => new(fraction, 0, 0, 0, 0, uz);
        var sag = new FemAnalysisResult([new("A", 0, 0, 0), new("B", 0, -0.02, 0), new("C", 0, 0, 0)], [],
        [
            new("AB", 0, 0, 0, 0, 0, 0, [Station(0, 0), Station(0.5, -0.012), Station(1, -0.02)]),
            new("BC", 0, 0, 0, 0, 0, 0, [Station(0, -0.02), Station(0.5, -0.012), Station(1, 0)]),
        ], [], 0);
        var beamSections = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal) { ["AB"] = before with { MemberName = "AB" }, ["BC"] = before with { MemberName = "BC" } };
        var deflected = StructureModel3D.BuildDeflected(beam, beamSections, sag, contour: true, 0, 0, undeformedGuide: true);
        var plain = StructureModel3D.BuildDeflected(beam, beamSections, sag, contour: false, 0, 40, undeformedGuide: false);
        var shades = ((System.Windows.Media.Media3D.MeshGeometry3D)deflected.MemberVisuals[0].Model.Geometry).TextureCoordinates.Select(point => point.X).ToArray();
        Check(deflected.Deflected is { Contour: true } shape && Math.Abs(shape.MaximumMm - 20) < 1e-9 && Math.Abs(shape.Magnification - 0.05 * 10 / 0.02) < 1e-6 &&
              shape.ContourMinMm == 0 && Math.Abs(shape.ContourMaxMm - 20) < 1e-9 &&
              deflected.MemberVisuals.Select(member => member.Name).SequenceEqual(["AB", "BC"]) && deflected.Model.Children.Count == 3 && plain.Model.Children.Count == 2 &&
              Math.Abs(deflected.Bounds.Z - (beforeScene.Bounds.Z - 0.5)) < 1e-6 && Math.Abs(deflected.Bounds.SizeX - beforeScene.Bounds.SizeX) < 1e-9 &&
              shades.Min() == 0 && Math.Abs(shades.Max() - 1) < 1e-9 && shades.Any(value => Math.Abs(value - 0.6) < 1e-9) &&
              plain.Deflected is { Contour: false, ContourMaxMm: 40 } && beforeScene.Deflected is null,
            "the 3D deflected shape moves each frame through its stations (mid-span 20 mm drawn × 25 = 0.5 m down), shades it by |U| from 0 to the largest " +
            "displacement, keeps each member identifiable, and adds the model as built as a guide when asked");

        var path = Path.Combine(Path.GetTempPath(), $"mcotis-axis-{Guid.NewGuid():N}{FemInputDatabase.Extension}");
        try
        {
            vm.SaveFemModelFileAsync(path).GetAwaiter().GetResult();
            var openedState = new ApplicationState();
            var openedWorkflow = new WorkflowService(silent.Gateway, openedState);
            var reopened = new MainViewModel(openedState, openedWorkflow, new NoSelection(),
                new ConfigService(silent.Gateway, openedState, openedWorkflow));
            reopened.OpenFemModelFile(path);
            var saved = reopened.FemSectionProperties.Single(property => property.MemberName == "VRM001");
            Check(saved.LocalAngleDeg == 90 && Math.Abs(saved.InPlaneInertiaM4!.Value - rotated.InPlaneInertiaM4!.Value) < 1e-12,
                "the selected frame's local-axis angle and effective FEM inertia survive saving and reopening the MCOTiS database");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void RunBlankModelFlow()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcotis-blank-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = new ApplicationState();
            var silent = SapDouble.Create(null!, GeometryModel.Empty, refuseAll: true);
            var workflow = new WorkflowService(silent.Gateway, state);
            var sessionStore = new Mcotis.Desktop.Infrastructure.FemWorkingSessionStore(root);
            var vm = new MainViewModel(state, workflow, new NoSelection(), new ConfigService(silent.Gateway, state, workflow),
                femSessionStore: sessionStore);

            vm.LoadDefaultFemModel();
            Check(vm.IsDefaultModel && vm.HasModelGeometry && !vm.ShowBlankModelChoice &&
                  sessionStore.TryReadManifest() is not null && silent.Reads == 0,
                "startup opens the built-in default model without a SAP2000 read or blank-model choice");

            vm.InitializeBlankMcotisConfiguration();
            Check(vm.CurrentFileName == "Untitled" && vm.WindowTitle.EndsWith(" — Untitled", StringComparison.Ordinal), "a new configuration is 'Untitled' in the window title");
            Check(vm.IsOfflineModel && vm.HasWorkingModel && !vm.HasModelGeometry && vm.CanDefineFemModel &&
                  vm.ShowBlankModelChoice && !vm.CanEditVcfGeometry && state.Model?.Name == "Unsaved model" && silent.Reads == 0,
                "New Config opens a blank unsaved model with Define available and no SAP2000 read");

            vm.ContinueWithBlankModel.Execute(null);
            Check(!vm.ShowBlankModelChoice && !vm.HasModelGeometry && vm.CanDefineFemModel && !vm.IsDefaultModel,
                "Continue with Blank Model dismisses the choice and keeps the empty model editable");
            vm.InitializeBlankMcotisConfiguration();
            Check(vm.ShowBlankModelChoice && !vm.HasModelGeometry,
                "New Config offers the template and blank-model choices again");

            vm.LoadDefaultModel.Execute(null);
            Check(vm.IsDefaultModel && vm.HasModelGeometry && !vm.ShowBlankModelChoice && sessionStore.TryReadManifest() is not null,
                "the blank-model template option opens the built-in model and hides the blank choice");
            vm.InitializeBlankMcotisConfiguration();
            Check(vm.ShowBlankModelChoice && !vm.HasModelGeometry && sessionStore.TryReadManifest() is null,
                "New Config returns to a blank model and prevents the previous template from reopening next time");

            vm.EnsureMaterials();
            var steel = vm.Materials.First(material => material.Kind == MaterialKind.Steel);
            var tendon = MaterialCosts.WithDefaultCost(MaterialProperty.Default(MaterialKind.Tendon, "T1860"));
            vm.Materials.Add(tendon);
            var frame = FrameSectionProperty.Default(FrameShape.Rectangle, "BLANK_FRAME", steel.Name) with { Depth = 0.8, Width = 0.5 };
            var cable = new StrandSectionProperty { Name = "BLANK_CABLE", Material = tendon.Name, Area = 0.002, Source = "User" };
            vm.CommitFrameSections([frame], new Dictionary<string, string>());
            vm.CommitStrandSections([cable], new Dictionary<string, string>(), tendons: false);
            Check(vm.CanEditVcfGeometry && !vm.HasModelGeometry,
                "after frame and cable sections are defined, Edit is available while the model remains blank");

            var editor = vm.AnalyzeVcfGeometryForEdit();
            Check(editor.Geometry.Points.Count > 0 && !vm.HasModelGeometry,
                "opening Edit uses a temporary VCF canvas and does not create geometry before Apply");
            var assignments = editor.Geometry.Members.ToDictionary(member => member.Name,
                member => member.Kind == MemberKind.Frame ? frame.Name : cable.Name, StringComparer.Ordinal);
            vm.ApplyVcfGeometryAsync(editor.Geometry, assignments).GetAwaiter().GetResult();
            Check(vm.HasModelGeometry && state.BaselineCaptured &&
                  vm.FemSectionProperties.Count == editor.Geometry.Members.Count &&
                  vm.FemSectionProperties.Where(property => property.IsCable).All(property => property.SectionName == cable.Name) &&
                  vm.FemSectionProperties.Where(property => !property.IsCable).All(property => property.SectionName == frame.Name) &&
                  silent.Reads == 0 && silent.Writes == 0,
                "Apply creates the VCF objects from the defined sections without reading or writing SAP2000");
            vm.FlushFemWorkingSession();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Edit → Delete on the default model: a deleted cable leaves Tab 03 and its properties; a deleted joint takes its
    /// connected members, its loads and its Tab 01 / Tab 02 entries; the edited model still runs, without SAP2000.</summary>
    private static void RunDeleteObjects()
    {
        var state = new ApplicationState();
        var silent = SapDouble.Create(null!, GeometryModel.Empty, refuseAll: true);
        var workflow = new WorkflowService(silent.Gateway, state);
        var vm = new MainViewModel(state, workflow, new NoSelection(), new ConfigService(silent.Gateway, state, workflow));
        vm.LoadDefaultFemModel();
        var cable = state.Cables[0].CableName;
        var points = state.Geometry.Points.Count;
        var members = state.Geometry.Members.Count;
        vm.DeleteObjectsAsync([cable], []).GetAwaiter().GetResult();
        Check(state.Geometry.Members.Count == members - 1 && state.Geometry.Points.Count == points &&
              state.Cables.All(item => item.CableName != cable) && vm.FemSectionProperties.All(item => item.MemberName != cable) &&
              state.Cables.All(item => item.ReferenceCableName != cable || item.SyncMode == CableSyncMode.Independent),
            $"Delete removes cable {cable} from the geometry, its properties and Tab 03; its end joints stay");

        // a loaded top-chord joint: its connected members, Tab 01 point and Tab 02 group entry go with it
        var joint = state.AppliedPoints[^1].PointName;
        var connected = vm.MembersDeletedWith([], [joint]);
        var before = state.Geometry.Members.Count;
        var (removedMembers, removedJoints) = vm.DeleteObjectsAsync([], [joint]).GetAwaiter().GetResult();
        Check(removedJoints == 1 && removedMembers == connected.Count && connected.Count > 0 &&
              state.Geometry.Members.Count == before - connected.Count && !state.Geometry.Points.ContainsKey(joint) &&
              state.Geometry.Members.All(member => member.Start != joint && member.End != joint) &&
              state.AppliedPoints.All(point => point.PointName != joint) && !state.Truss.TopPoints.Contains(joint) &&
              vm.FemSectionProperties.Count == state.Geometry.Members.Count && silent.Writes == 0,
            $"Delete of joint {joint} removes it with its {connected.Count} connected members and its Tab 01 / Tab 02 entries");
        var runs = Analyze(vm);
        Check(runs.Count == 1 && silent.Reads == 0 && silent.Writes == 0, "the edited model runs FEM without SAP2000");
    }

    /// <summary>The built-in default model (COT Framing_V1.2_100m) opens a disconnected app on 07 FEM, ready to run.</summary>
    private static void RunDefaultModel()
    {
        var bundled = FemInputDatabase.ReadDefault();
        var source = FemInputDatabase.Read(Path.GetFullPath("src/Mcotis.Desktop/Resources/Default.cotfem.db"));
        Check(FemInputDatabase.RevisionHash(bundled.Snapshot) == FemInputDatabase.RevisionHash(source.Snapshot),
            "the bundled default database matches src/Mcotis.Desktop/Resources/Default.cotfem.db");
        var file = bundled.Snapshot.ToLegacy();
        var state = new ApplicationState();
        var silent = SapDouble.Create(null!, GeometryModel.Empty, refuseAll: true);
        var workflow = new WorkflowService(silent.Gateway, state);
        var vm = new MainViewModel(state, workflow, new NoSelection(), new ConfigService(silent.Gateway, state, workflow));
        var warnings = new List<string>();
        vm.WarningSink = (title, body) => warnings.Add($"{title}: {body}");
        vm.LoadDefaultFemModel();
        Check(warnings.Count == 0 && vm.IsDefaultModel && vm.SelectedTab == 7 && state.Model?.Name == "COT Framing_V1.2_100m.sdb" &&
              state.Geometry.Points.Count == file.Geometry!.Points.Count && state.Geometry.Members.Count == file.Geometry.Members.Count &&
              vm.FemSectionProperties.Count == file.SectionProperties.Count && state.Cables.Count == 16 && state.AppliedPoints.Count == 11 &&
              vm.FemCaseRows.Select(row => (row.Name, row.Definition)).SequenceEqual(file.CaseRows.Select(row => (row.Name, row.Definition))) &&
              vm.Materials.Count == file.Materials!.Count && vm.FrameSections.Count == file.FrameSections!.Count &&
              !vm.IsFemModelLocked && vm.FemRuns.Count == 0 && vm.OfflineModelName == FemInputDatabase.DefaultModelName,
            $"the default model {FemInputDatabase.DefaultModelName} opens offline on 07 FEM with its database geometry, sections, materials and load cases" +
            (warnings.Count > 0 ? " — " + string.Join(" | ", warnings) : ""));
        // Tabs 01–03 of the default model span 3 loadings × 6 heights × 1 pretension. That is the parametric study's space;
        // Run Analysis is one run of the model itself: its own truss height, LL_ABOVE at the first value of its 5 … 10 kPa range.
        Check(vm.RunFemAnalysis.CanExecute(null) && vm.FemStatus == "OFFLINE · READY" && state.Truss.CandidateCount == 6 &&
              vm.FemRunSummary.StartsWith("1 FEM run", StringComparison.Ordinal) && vm.FemRunSummary.Contains("truss height 20 m", StringComparison.Ordinal) &&
              vm.FemRunSummary.Contains("LL_ABOVE qz 5 kPa", StringComparison.Ordinal),
            $"the default model is ready to run ({vm.FemRunSummary})");
        var runs = Analyze(vm);
        var midSpan = runs.SelectMany(run => run.Loads ?? []).Where(load => load.PointName == "TTrPt006").Select(load => load.FzKN).Order().ToArray();
        Check(warnings.Count == 0 && silent.Reads == 0 && runs.Count == 1 && runs[0].Id == FemStudyService.ModelRunId && vm.FemCableFitRows.All(row => !row.Failed) &&
              midSpan.SequenceEqual([-12000.0, -6000.0]) && vm.FemStatus == "ANALYSIS COMPLETE" && vm.FemMonitorProgressText == "Finished" &&
              vm.FemProgressText.StartsWith("Analysis complete", StringComparison.Ordinal),
            $"Run Analysis on the default model is one run of the model as defined, without SAP2000 (mid-span Fz {string.Join(" / ", midSpan)} kN): " +
            vm.FemProgressText + (warnings.Count > 0 ? " — " + string.Join(" | ", warnings) : ""));
        UnlockForFurtherEdits(vm, "Run locks the FEM model; Unlock clears its output before load definitions can be edited");
        RunLoadCases(vm, state, runs[0], warnings);
        RunVcfGeometry(vm, state, silent, warnings);
        RunVcfBays();
        RunVcfOptimization();
        RunMemberInformation();
        RunSaveAs();
        RunSectionDrawings();
        RunStrandBundle();
        RunMaterials();
        RunSections();
        RunCompositeSections();
    }

    /// <summary>Define → Load Cases on the default model: it opens on the app's default setup — SAP2000's staging for a cable
    /// structure — which gives what the staged cases saved from SAP2000 give, and the cases can be defined in the app.</summary>
    private static void RunLoadCases(MainViewModel vm, ApplicationState state, FemStudyRun defaultRun, List<string> warnings)
    {
        const string pretension = FemLoadPlan.DefaultPretensionCase, sls = FemLoadPlan.DefaultServiceCase, uls = FemLoadPlan.DefaultUltimateCase;
        Check(vm.FemCaseRows.Select(row => (row.Name, row.Type, row.Definition, row.InitialCase, row.Run, row.Source)).SequenceEqual([
                  (pretension, "Nonlinear Static", "1 Pretension + 1 DEAD", "", true, "App"),
                  (sls, "Nonlinear Static", "1 DEAD_ABOVE + 1 LL_ABOVE", pretension, true, "App"),
                  (uls, "Nonlinear Static", "0.35 DEAD + 1.35 DEAD_ABOVE + 1.5 LL_ABOVE", pretension, true, "App")]) &&
              vm.FemCombinationRows.Count == 0 && vm.FemPatternRows.Select(row => row.Name).SequenceEqual(["DEAD", "DEAD_ABOVE", "LL_ABOVE", "Pretension"]) &&
              vm.HasSapLoadCases && vm.FemInitialCaseChoices.SequenceEqual(["", pretension, sls, uls]),
            "the default model opens on the default load cases: Pretension = target force + SW, then SLS = + DL + LL and ULS = + 0.35 SW + 1.35 DL + 1.5 LL continue from it");
        Check(defaultRun.Results!.Keys.SequenceEqual([pretension, sls, uls]) && defaultRun.Results.Values.All(result => result.TargetFitConverged) &&
              defaultRun.Results[pretension].Cables.All(cable => cable.TargetForceKN <= 0 || Math.Abs(cable.AxialForceKN - cable.PretensionStageForceKN!.Value) < 1e-3) &&
              defaultRun.Results[uls].MaximumDownwardDeflectionMm > defaultRun.Results[sls].MaximumDownwardDeflectionMm,
            $"Run Analysis solves them staged: the cable targets are met in {pretension}, and δ grows from SLS to ULS " +
            $"({defaultRun.Results[sls].MaximumDownwardDeflectionMm:0.0} → {defaultRun.Results[uls].MaximumDownwardDeflectionMm:0.0} mm)");

        // SAP Cases: the load cases saved from SAP2000. Its staged cases are the ones the default setup copies.
        vm.ApplySapFemLoadCases();
        Check(vm.FemRuns.Count == 0 && vm.FemCaseRows.Count == 11 && vm.FemCaseRows.All(row => row.Source == "SAP") &&
              vm.FemCaseRows.Single(row => row.Name == "NL ULS001: 1.35DL+1.5LL-staged") is { Definition: "1.35 DEAD_ABOVE + 1.5 LL_ABOVE + 0.35 DEAD", InitialCase: "Pretension" },
            "SAP Cases shows the load cases saved from SAP2000 again (11, with its own staged cases) and clears the results");
        var sapRun = Analyze(vm).Single();
        bool Same(string appCase, string sapCase) =>
            defaultRun.Results[appCase].Joints.Zip(sapRun.Results![sapCase].Joints).All(pair => Math.Abs(pair.First.UZM - pair.Second.UZM) < 1e-7 && Math.Abs(pair.First.UXM - pair.Second.UXM) < 1e-7) &&
            defaultRun.Results[appCase].Cables.Zip(sapRun.Results[sapCase].Cables).All(pair => Math.Abs(pair.First.AxialForceKN - pair.Second.AxialForceKN) < 0.05) &&
            defaultRun.Results[appCase].SupportReactions.Zip(sapRun.Results[sapCase].SupportReactions).All(pair => Math.Abs(pair.First.ReactionZKN - pair.Second.ReactionZKN) < 0.05);
        Check(Same(pretension, "Pretension") && Same(sls, "NL SLS001: 1.0DL+1.0LL-staged") && Same(uls, "NL ULS001: 1.35DL+1.5LL-staged"),
            "the default setup mimics SAP2000's: Pretension, SLS and ULS give the displacements, cable forces and reactions of SAP2000's Pretension and staged SLS / ULS cases");

        UnlockForFurtherEdits(vm);
        // Default Setup again, then cases of one's own
        vm.ApplyDefaultFemLoadCases();
        var added = vm.AddFemLoadCase()!;
        Check(vm.FemCaseRows.Count == 4 && ReferenceEquals(vm.SelectedFemCaseRow, added) && vm.FemInitialCaseChoices[^1] == "Case 1" &&
              added is { Name: "Case 1", Type: "Nonlinear Static", Definition: "1 DEAD_ABOVE + 1 LL_ABOVE", InitialCase: pretension, Run: true, Source: "App" },
            "+ Add defines a nonlinear static case with the Tab 01 loads, continuing from the pretension case");
        added.Name = "Half live";
        added.Definition = "1 DEAD_ABOVE + 0.5 LL_ABOVE";
        var refusedName = false;
        try { added.Name = sls; } catch (ArgumentException) { refusedName = true; }
        var stage = vm.FemCaseRows[0];
        stage.Name = "PT+SW stage";
        Check(refusedName && added.Name == "Half live" && vm.FemCaseRows.Skip(1).All(row => row.InitialCase == "PT+SW stage") &&
              vm.FemInitialCaseChoices.SequenceEqual(["", "PT+SW stage", sls, uls, "Half live"]),
            "a case is renamed in the table (a name already taken is refused), and the cases that continue from a renamed case follow it");
        Check(!vm.RemoveFemLoadCase(stage) && warnings.Count == 1 && warnings[0].Contains("is used by", StringComparison.Ordinal) && vm.FemCaseRows.Count == 4,
            "- Remove keeps a case that others continue from: " + (warnings.Count > 0 ? warnings[0] : ""));
        warnings.Clear();
        var ownRun = Analyze(vm).Single();
        double Peak(string name) => ownRun.Results![name].MaximumDownwardDeflectionMm;
        Check(ownRun.Results!.Keys.SequenceEqual(["PT+SW stage", sls, uls, "Half live"]) && Peak("Half live") < Peak(sls) && Peak("Half live") > Peak("PT+SW stage") &&
              Math.Abs(Peak(sls) - defaultRun.Results[sls].MaximumDownwardDeflectionMm) < 1e-6,
            $"a case defined in the app is solved with the others: half the live load deflects less than SLS ({Peak("Half live"):0.0} < {Peak(sls):0.0} mm)");

        UnlockForFurtherEdits(vm);
        var path = Path.Combine(Path.GetTempPath(), $"mcotis-cases-{Guid.NewGuid():N}{FemModelFile.Extension}");
        try
        {
            vm.SaveFemModelFileAsync(path).GetAwaiter().GetResult();
            var (reopened, _, _, reopenWarnings) = DefaultModelApp();
            reopened.OpenFemModelFile(path);
            Check(reopenWarnings.Count == 0 && reopened.FemCaseRows.Select(row => (row.Name, row.Definition, row.InitialCase, row.Source))
                      .SequenceEqual(vm.FemCaseRows.Select(row => (row.Name, row.Definition, row.InitialCase, row.Source))) &&
                  reopened.FemInitialCaseChoices.SequenceEqual(["", "PT+SW stage", sls, uls, "Half live"]),
                "the load cases defined in the app are saved with the model and loaded again");
        }
        finally { if (File.Exists(path)) File.Delete(path); }

        Check(vm.RemoveFemLoadCase(added) && vm.FemCaseRows.Count == 3 && !vm.FemInitialCaseChoices.Contains("Half live") && warnings.Count == 0,
            "- Remove deletes a case nothing depends on");
        vm.ApplyDefaultFemLoadCases();
        Check(vm.FemCaseRows.Select(row => row.Name).SequenceEqual([pretension, sls, uls]) && vm.FemRuns.Count == 0 &&
              vm.FemProgressText.StartsWith("Default load cases defined", StringComparison.Ordinal),
            "Default Setup puts the default load cases back: " + vm.FemProgressText);

        // The flow: define a load pattern → give it a load (Edit → VCF System → Storey Force) → use it in a load case → Run
        var sdl = vm.AddFemLoadPattern(" SDL_ABOVE ", "Dead");
        Check(vm.CanDefineLoadPatterns && sdl is { Name: "SDL_ABOVE", Type: "Dead", SelfWeightMultiplier: 0, Role: "", Loads: "none" } &&
              state.LoadPatterns.Contains("SDL_ABOVE") && vm.FemPatternRows.Count == 5 && warnings.Count == 0,
            "Define → Load Patterns adds a load pattern to the model; it carries nothing until a load is assigned to it");
        Check(vm.AddFemLoadPattern("sdl_above", "Dead") is null && vm.AddFemLoadPattern("A + B", "Dead") is null && vm.AddFemLoadPattern("", "Dead") is null &&
              warnings.Count == 3 && vm.FemPatternRows.Count == 5,
            "a pattern name that is taken, holds ' + ' or is empty is refused: " + string.Join(" | ", warnings));
        warnings.Clear();
        // Storey Force: one fixed qz per load pattern. A value that is the one the model already uses leaves the row as it
        // is (LL_ABOVE keeps its 01 Loading range); a new one is fixed.
        vm.SetStoreyLoads(state.TributaryWidthB, state.NumberOfStories, [("DEAD_ABOVE", 10), ("LL_ABOVE", 5), ("SDL_ABOVE", 2)]);
        Check(state.LoadDefinitions.Select(load => (load.LoadName, load.ApplyIncrementStep, load.CandidateValues[0])).SequenceEqual([
                  ("DEAD_ABOVE", false, 10.0), ("LL_ABOVE", true, 5.0), ("SDL_ABOVE", false, 2.0)]) &&
              sdl is { Role: "Tab 01 loads" } && sdl.Loads == $"VCF storey loads: {state.AppliedPoints.Count} joints" && vm.FemPatternRows.Single(row => row.Name == "DEAD") is { Role: "Self-weight", Loads: "self-weight" } &&
              vm.FemPatternRows.Single(row => row.Name == "Pretension").Role == "Tab 03 cable target force",
            "a storey load on the pattern (VCF System → Storey Force, one qz) gives it its load; the table says what each pattern carries now");
        var sdlCase = vm.AddFemLoadCase()!;
        sdlCase.Name = "SDL";
        sdlCase.Definition = "1 SDL_ABOVE";
        var sdlRun = Analyze(vm).Single();
        double Rz(string name) => sdlRun.Results![name].SupportReactions.Sum(reaction => reaction.ReactionZKN);
        var sdlTotal = 2.0 * (100 * 12) * 10;     // qz 2 kPa × (100 m × B 12 m) × 10 storeys
        UnlockForFurtherEdits(vm);
        Check(sdlCase.InitialCase == pretension && Math.Abs(Rz("SDL") - Rz(pretension) - sdlTotal) < 1e-3 * sdlTotal,
            $"a load case with the new pattern, continuing from Pretension, carries its load: ΣRz grows by {Rz("SDL") - Rz(pretension):0} kN = 2 kPa × 1,200 m² × 10 storeys");
        vm.ApplyDefaultFemLoadCases();
        Check(vm.FemCaseRows.Select(row => row.Definition).SequenceEqual(["1 Pretension + 1 DEAD", "1 DEAD_ABOVE + 1 LL_ABOVE + 1 SDL_ABOVE",
                  "0.35 DEAD + 1.35 DEAD_ABOVE + 1.5 LL_ABOVE + 1.35 SDL_ABOVE"]),
            "Default Setup takes the new pattern into SLS and ULS, factored as dead load by its type");
        Check(!vm.RemoveFemLoadPattern(sdl) && warnings.Count == 1 && warnings[0].Contains("01 Loading", StringComparison.Ordinal) && warnings[0].Contains("load case", StringComparison.Ordinal),
            "- Remove keeps a load pattern that is in use: " + (warnings.Count > 0 ? warnings[0] : ""));
        warnings.Clear();
        vm.SetStoreyLoads(state.TributaryWidthB, state.NumberOfStories, [("DEAD_ABOVE", 10), ("LL_ABOVE", 5)]);
        vm.ApplyDefaultFemLoadCases();
        Check(vm.RemoveFemLoadPattern(sdl) && !state.LoadPatterns.Contains("SDL_ABOVE") && vm.FemPatternRows.Count == 4 && warnings.Count == 0 &&
              vm.FemCaseRows[2].Definition == "0.35 DEAD + 1.35 DEAD_ABOVE + 1.5 LL_ABOVE",
            "once nothing uses it the pattern is removed, and the default cases are the three again");

        // Edit → VCF System → Cable Force: the target force of the model's cables
        var leader = state.Cables.Single(cable => cable.CableName == "Cable1a-2");
        var untouchedTarget = state.Cables.Single(cable => cable.CableName == "Cable2a-2").TargetForce;
        vm.SetCableTargetForces(new Dictionary<string, double> { ["Cable1a-2"] = 90_000, ["Cable1b-2"] = 1 });
        var forceRun = Analyze(vm).Single();
        double Target(string cable) => forceRun.Results![pretension].Cables.Single(item => item.MemberName == cable).TargetForceKN;
        double Stage(string cable) => forceRun.Results![pretension].Cables.Single(item => item.MemberName == cable).AxialForceKN;
        Check(leader is { ApplyRange: false, TargetForce: 90_000.0 } && Target("Cable1a-2") == 90_000 && Target("Cable1b-2") == 90_000 &&
              Math.Abs(Stage("Cable1a-2") - 90_000) < 0.1 * 90_000 && Target("Cable2a-2") == untouchedTarget,
            $"a target force set for a cable is the one Run meets in Pretension (Cable1a-2 {Stage("Cable1a-2"):0} kN for 90,000); its follower Cable1b-2 keeps following, the other cables keep theirs");
        UnlockForFurtherEdits(vm);
        vm.SetCableTargetForces(new Dictionary<string, double> { ["Cable1a-2"] = 100_000 });
        // Configure a Same Step follower: it has its own end points, but keeps following the reference's step.
        var stepFollower = state.Cables.Single(cable => cable.CableName == "Cable4b-2");
        stepFollower.SyncMode = CableSyncMode.SameStep;
        vm.SetCableTargetForces(new Dictionary<string, double> { ["Cable4b-2"] = 42_000 });
        var stepValue = PretensionCandidates.Enumerate(state.Cables.ToArray()).First().CableForces["Cable4b-2"];
        vm.SetCableTargetForces(new Dictionary<string, double> { ["Cable4b-2"] = 40_000 });
        Check(stepFollower is { IsSameStepFollower: true, MinForce: 40_000.0, MaxForce: 40_000.0 } && stepValue == 42_000,
            "a cable that follows another's step takes one value too (Cable4b-2 42,000 kN) and keeps its sync");

        // Run asks which load cases to analyse; without Run Now nothing starts
        var asked = 0;
        vm.ChooseFemLoadCases = () => { asked++; return false; };
        vm.RunFemAnalysis.Execute(null);
        Check(asked == 1 && vm.FemRuns.Count == 0 && vm.RunFemAnalysis.CanExecute(null),
            "Run opens the choice of load cases first; leaving it without Run Now starts no analysis");
        vm.ChooseFemLoadCases = null;
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

        var path = Path.Combine(Path.GetTempPath(), $"mcotis-composite-{Guid.NewGuid():N}{FemModelFile.Extension}");
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

        var path = Path.Combine(Path.GetTempPath(), $"mcotis-sections-{Guid.NewGuid():N}{FemModelFile.Extension}");
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

        var path = Path.Combine(Path.GetTempPath(), $"mcotis-materials-{Guid.NewGuid():N}{FemModelFile.Extension}");
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

        var heights = state.Truss.CandidateCount;
        var megaColumn = vm.FemSectionProperties.First(property => property.MemberName == "170");
        vm.ApplyVcfGeometryAsync(geometry, new Dictionary<string, string>(StringComparer.Ordinal) { ["VRM001"] = megaColumn.SectionName! })
            .GetAwaiter().GetResult();
        var vrm001 = vm.FemSectionProperties.Single(property => property.MemberName == "VRM001");
        Check(warnings.Count == 0 && Near(state.Geometry.Points["TTrPt001"].Position.Z, 27) && Near(state.Baseline.Points["TTrPt001"].Position.X, -55) &&
              state.AppliedPoints.Single(point => point.PointName == "TTrPt001") is { } loaded && Near(loaded.X, -55) && Near(loaded.Z, 27) &&
              vrm001.SectionName == megaColumn.SectionName && Near(vrm001.AreaM2, megaColumn.AreaM2) && vrm001.EndRotationReleases == "None" &&
              state.BaselineCaptured && state.Truss.CandidateCount == heights && vm.FemRuns.Count == 0 && vm.IsOfflineModel &&
              vm.FemRunSummary.Contains("truss height 25 m", StringComparison.Ordinal),
            "Apply writes the geometry, the loading points and the chosen section to the working model; the Tab 02 height range follows the new height" +
            (warnings.Count > 0 ? " — " + string.Join(" | ", warnings) : ""));
        var runs = Analyze(vm);
        Check(warnings.Count == 0 && silent.Reads == 0 && runs.Count == 1 &&
              runs[0].Result.Joints.Any(joint => joint.PointName == "TTrPt001"),
            $"Run Analysis solves the edited VCF geometry without SAP2000 · {vm.FemProgressText}");
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
        Check(warnings.Count == 0 && silent.Reads == 0 && runs.Count == 1 && runs[0].Result.Cables.Any(cable => cable.MemberName == "Cable9a-2"),
            $"Run Analysis solves the 12-bay model (the new fan cables carry force) · {vm.FemRunSummary} · {vm.FemProgressText}");
        var path = Path.Combine(Path.GetTempPath(), $"mcotis-bays-{Guid.NewGuid():N}{FemModelFile.Extension}");
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
              vm8.FemRunSummary.StartsWith("1 FEM run", StringComparison.Ordinal) && vm8.RunFemAnalysis.CanExecute(null),
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
        var cables = VcfOptimization.ModelCableForces(state.Cables.ToArray());   // every cable at its Tab 03 force, the fan not graded
        var live = state.LoadDefinitions.Single(load => load.ApplyIncrementStep);
        var dead = state.LoadDefinitions.Single(load => !load.ApplyIncrementStep);
        // a truss one Tab 02 height step deeper than the model, so the cases differ from the working model
        var depth = VcfRange.Fixed(analysis.Parameters.TrussDepth + state.Truss.Increment, 0.5);
        VcfOptimizationSettings Settings(VcfRange bays, VcfRange bay, VcfRange liveQz, VcfRange? trussDepth = null) => current with
        {
            Geometry = new Dictionary<string, VcfRange>(current.Geometry) { ["Bays"] = bays, ["Bay"] = bay, ["Depth"] = trussDepth ?? depth },
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

        // the study case that takes every input at the model's own value is the run of Run FEM Analysis: one model, two entries
        // (the optimization reads the deflection on the bottom chord points, BTrPt… — so does this comparison)
        var bottomPoints = analysis.BottomPoints.ToHashSet(StringComparer.Ordinal);
        double BottomMax(IEnumerable<FemAnalysisResult> results) =>
            results.SelectMany(result => result.Joints).Where(joint => bottomPoints.Contains(joint.PointName)).Max(joint => -joint.UZM * 1000);
        var modelRun = Analyze(vm).Single();
        var modelMax = BottomMax(modelRun.Results!.Values);
        var modelCase = Optimize(vm, Settings(VcfRange.Fixed(10, 1), VcfRange.Fixed(10, 1), VcfRange.Fixed(live.MinQz.Value, 1),
            VcfRange.Fixed(analysis.Parameters.TrussDepth, 0.5))).Single();
        Check(modelCase.MaxDeflectionMm is { } caseMax && Math.Abs(caseMax - modelMax) < 1e-6 && modelCase.Status.StartsWith("Analysed", StringComparison.Ordinal),
            $"Run FEM Analysis is the one run of the model (bottom chord {modelMax:0.000} mm, at {modelCase.Point}); the Optimization VCF case at the model's own values gives the same ({modelCase.Deflection} mm)");

        // the same geometry applied with Edit → VCF Geometry and analysed with Run FEM Analysis gives the same deflection
        var (applied, appliedState, _, _) = DefaultModelApp();
        var deeper = analysis.Parameters with { TrussDepth = depth.Min, MiddleChordHeight = VcfOptimization.MiddleChordRatio * depth.Min };
        applied.ApplyVcfGeometryAsync(VcfGeometry.Build(VcfGeometry.Analyze(appliedState.Geometry, appliedState.Truss), deeper),
            new Dictionary<string, string>(StringComparer.Ordinal)).GetAwaiter().GetResult();
        var appliedRuns = Analyze(applied);
        var femMax = appliedRuns.Select(run => BottomMax((run.Results ?? new Dictionary<string, FemAnalysisResult> { [""] = run.Result }).Values)).ToArray();
        Check(femMax.Any(value => Math.Abs(value - Defl(2)) < 1e-6),
            $"A case ({Defl(2):0.000} mm) matches Run FEM Analysis on the same geometry applied with Edit -> VCF Geometry " +
            $"(FEM max deflections {string.Join(", ", femMax.Select(value => value.ToString("0.000")))})");

        // the deflection read from the SLS load case — the default setup's staged SLS — checked against span / 250
        const string sls = FemLoadPlan.DefaultServiceCase;
        var (slsCases, slsCombinations) = vm.VcfDeflectionResults();
        // Keep uplift outside this deflection comparison; a separate high-pretension case checks uplift below.
        var slsRows = Optimize(vm, Settings(VcfRange.Fixed(10, 1), VcfRange.Fixed(10, 1), VcfRange.Fixed(live.MinQz.Value, 1)) with { DeflectionResult = sls, SpanRatio = 250, UpliftLimitMm = 1_000 });
        var slsFem = appliedRuns.Where(run => run.Results?.ContainsKey(sls) == true)
            .Select(run => BottomMax([run.Results![sls]])).ToArray();
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

        // ---- cable forces: one shared step, the fan cables by their length ----
        var configs = state.Cables.ToArray();
        var topology12 = VcfGeometry.ChangeBays(analysis, 12);
        var model12 = VcfGeometry.Analyze(topology12);
        var geometry12 = VcfGeometry.Build(model12, analysis.Parameters);
        string[] fanA = ["Cable2a-2", "Cable3a-2", "Cable4a-2", "Cable5a-2", "Cable6a-2", "Cable9a-2"];
        double Length12(string name) => VcfOptimization.CableLength(geometry12, name);
        Check(VcfGeometry.FanCables(analysis).Select(fan => (fan.Name, fan.Side, fan.Order)).SequenceEqual(
                  fanA.Take(5).Select((name, index) => (name, 0, index + 1)).Concat(fanA.Take(5).Select((name, index) => (name.Replace("a-2", "b-2"), 1, index + 1)))) &&
              VcfGeometry.FanCables(model12).Where(fan => fan.Side == 0).Select(fan => fan.Name).SequenceEqual(fanA) &&
              VcfOptimization.FitGradient(analysis, VcfOptimization.CableTargets(cables, 0, analysis, analysis.Geometry, null, configs)) == 318,
            "The fan cables are read from the model (top corner → bottom chord: Cable2 … Cable6 a / b, the nearest first; 12 bays add Cable9); " +
            "the model's own fan forces rise 318 kN per m from the shortest to the longest");

        // + kN per m: T = T shortest + g × (L − L shortest), rounded to 100 kN, on both sides, for whatever fan the bays give
        var linear = new VcfCableForces(3, cables.Cables.Where(pair => pair.Key is "Cable1a-2" or "Cable7a-2" or "Cable8a-2").ToDictionary(pair => pair.Key, pair => pair.Value),
            VcfFanGrading.Linear, new VcfForceSpan(30_000, 34_000), 300);
        var first12 = VcfOptimization.CableTargets(linear, 0, model12, geometry12, topology12.AddedCables, configs);
        var last12 = VcfOptimization.CableTargets(linear, 2, model12, geometry12, topology12.AddedCables, configs);
        Check(fanA.All(name => first12[name] == VcfOptimization.RoundForce(30_000 + 300 * (Length12(name) - Length12("Cable2a-2"))) &&
                               first12[name.Replace("a-2", "b-2")] == first12[name] && last12[name] - first12[name] == 4_000) &&
              first12["Cable2a-2"] == 30_000 && first12["Cable9a-2"] == 42_300 && fanA.Zip(fanA.Skip(1)).All(pair => first12[pair.Second] > first12[pair.First]) &&
              first12["Cable1a-2"] == 100_000 && last12["Cable1a-2"] == 105_000 && first12["Cable1b-2"] == first12["Cable1a-2"] &&
              first12["Cable7b-2"] == first12[configs.Single(cable => cable.CableName == "Cable7b-2").ReferenceCableName!],
            "Graded + 300 kN per m at 12 bays: every fan cable takes 30,000 kN + 300 × its extra length, rounded to 100 kN (Cable9a-2, 63.2 m: 42,300 kN), " +
            "the same on both sides; all cables step together (fan + 4,000, Cable1a-2 100,000 → 105,000 over 3 steps); followers keep following");
        var topology8 = VcfGeometry.ChangeBays(analysis, 8);
        var model8 = VcfGeometry.Analyze(topology8);
        var first8 = VcfOptimization.CableTargets(linear, 0, model8, VcfGeometry.Build(model8, analysis.Parameters), topology8.AddedCables, configs);
        Check(!first8.ContainsKey("Cable6a-2") && !first8.ContainsKey("Cable9a-2") && fanA.Take(4).All(name => first8[name] == first12[name]) && first8.Count == 14,
            "At 8 bays the fan has four cables a side with the same forces by length; the cables the smaller fan drops have none");

        // × length, and Manual: a fan cable more bays add grows from the cable it copies by the length ratio
        var proportional = VcfOptimization.CableTargets(linear with { Grading = VcfFanGrading.Proportional }, 0, model12, geometry12, topology12.AddedCables, configs);
        var manualBase = VcfOptimization.CableTargets(cables, 0, analysis, analysis.Geometry, null, configs);
        var manual12 = VcfOptimization.CableTargets(cables, 0, model12, geometry12, topology12.AddedCables, configs);
        Check(fanA.All(name => proportional[name] == VcfOptimization.RoundForce(30_000 * Length12(name) / Length12("Cable2a-2"))) && proportional["Cable9a-2"] == 84_900 &&
              manual12["Cable6a-2"] == manualBase["Cable6a-2"] && manual12["Cable5a-2"] == manualBase["Cable5a-2"] &&
              manual12["Cable9a-2"] == VcfOptimization.RoundForce(manualBase["Cable6a-2"] * Length12("Cable9a-2") / Length12("Cable6a-2")) &&
              manual12["Cable9b-2"] == manual12["Cable9a-2"],
            $"× length takes 30,000 kN × L / L shortest (Cable9a-2 84,900 kN); Manual keeps the database's forces and gives the new Cable9a-2 Cable6a-2's {manualBase["Cable6a-2"]:0} kN scaled by 63.2 / 53.9 m = {manual12["Cable9a-2"]:0} kN");

        // the run: one cable-step column, bays × steps cases (a cable a smaller fan drops does not multiply the cases)
        var stepRows = Optimize(vm, Settings(new VcfRange(8, 12, 4), VcfRange.Fixed(10, 1), VcfRange.Fixed(live.MinQz.Value, 1)) with { CableForces = linear });
        Check(stepRows.Length == 6 && stepRows.All(row => row.MaxDeflectionMm is > 0) &&
              vm.VcfOptimizationColumns.Select(axis => axis.Label).SequenceEqual(["Bays", "Cable2a-2 [kN]"]) &&
              stepRows.Select(row => string.Join(" / ", row.Values)).SequenceEqual(new[] { 8, 12 }.SelectMany(bays => new[] { 30_000, 32_000, 34_000 }.Select(force => $"{bays} / {force}"))) &&
              stepRows.Select(row => row.MaxDeflectionMm).Distinct().Count() == 6,
            "Run Optimization with a graded fan: 2 bay counts × 3 shared cable steps = 6 cases, each with its own deflection; the steps show as the shortest fan cable's force " +
            $"(8 bays {string.Join(", ", stepRows.Take(3).Select(row => row.Deflection))} mm · 12 bays {string.Join(", ", stepRows.Skip(3).Select(row => row.Deflection))} mm)" +
            (stepRows.Any(row => row.MaxDeflectionMm is null) ? " — " + string.Join(" | ", stepRows.Select(row => row.Status)) : ""));

        // ---- uplift limit: the span must not rise above its unloaded position (default 0 mm) ----
        // Use an explicit high-pretension fan so this limit is tested independently of the default database's forces.
        var modelSls = Settings(VcfRange.Fixed(10, 1), VcfRange.Fixed(10, 1), VcfRange.Fixed(live.MinQz.Value, 1), VcfRange.Fixed(analysis.Parameters.TrussDepth, 0.5)) with { DeflectionResult = sls };
        var liftedSettings = modelSls with { CableForces = linear with { Steps = 1, Fan = VcfForceSpan.Fixed(30_000), GradientKnPerM = 318 } };
        var lifted = Optimize(vm, liftedSettings).Single();
        var chordRise = lifted.MaxUpliftMm ?? 0;
        Check(liftedSettings.UpliftLimitMm == 0 && chordRise > 100 && lifted.UpliftPoint == "BTrPt006" &&
              lifted.MaxDeflectionMm < 400 && lifted.Point is "BTrPt001" or "BTrPt011" && lifted.Check == "NG" &&
              lifted.Status.Contains($"uplift {lifted.Uplift} mm at BTrPt006 is above the 0 mm limit", StringComparison.Ordinal) &&
              vm.VcfOptimizationBest.Contains("uplift ≤ 0 mm", StringComparison.Ordinal) && vm.VcfOptimizationBest.StartsWith("0 of 1 cases pass", StringComparison.Ordinal),
            $"The uplift limit (default 0 mm) catches a 30,000 kN fan that lifts the span: mid-span rises {lifted.Uplift} mm (BTrPt006) in '{sls}' " +
            $"while the largest downward displacement of the bottom chord, {lifted.Deflection} mm at {lifted.Point} over a mega column, passes span / 250 — the case is NG, and the status says why");
        var allowed = Optimize(vm, liftedSettings with { UpliftLimitMm = Math.Ceiling(chordRise) }).Single();
        Check(allowed.Check == "OK" && allowed.MaxUpliftMm == lifted.MaxUpliftMm && !allowed.Status.Contains("uplift", StringComparison.Ordinal),
            $"With the uplift limit at {Math.Ceiling(chordRise):0} mm the same case is OK");
        // a fan at half the force leaves the span below its unloaded position: no uplift on the bottom chord. (The back-stay
        // still pulls the outer column tops 201 / 204 up about 2 mm there — those joints are not checked.)
        var slack = Optimize(vm, modelSls with { CableForces = linear with { Steps = 1, Fan = VcfForceSpan.Fixed(15_000), GradientKnPerM = 318 } }).Single();
        Check(slack.MaxUpliftMm == 0 && slack.Uplift == 0.ToString("0.0") && slack.UpliftPoint == "" && slack.Point == "BTrPt006" &&
              slack.Check == (slack.MaxDeflectionMm <= 400 ? "OK" : "NG") && slack.Check == "OK",
            $"With the fan at half its force mid-span governs ({slack.Deflection} mm at BTrPt006) and no bottom chord point rises: uplift 0.0 mm, OK at the default limit");

        Check(ReferenceEquals(state.Geometry, geometryBefore) && vm.FemSectionProperties.SequenceEqual(propertiesBefore) && warnings.Count == 0,
            "Run Optimization leaves the working model, its member properties and SAP2000 unchanged" + (warnings.Count > 0 ? " — " + string.Join(" | ", warnings) : ""));
    }

    /// <summary>Right-click a member → Frame / Cable Information: what the form lists on the default model.</summary>
    private static void RunMemberInformation()
    {
        var (vm, state, silent, warnings) = DefaultModelApp();
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            static string? Value(IReadOnlyList<InformationTab> tabs, string tab, string section, string row) =>
                tabs.Single(item => item.Title == tab).Sections.SingleOrDefault(item => item.Title == section)?.Rows.SingleOrDefault(item => item.Name == row)?.Value;
            StructuralMember Member(string name) => state.Geometry.Members.Single(member => member.Name == name);

            var cable = vm.MemberInformation(Member("Cable1a-2"));
            var cableProperty = vm.FemSectionProperties.Single(property => property.MemberName == "Cable1a-2");
            Check(cable.Select(tab => tab.Title).SequenceEqual(["Geometry", "Assignments", "Loads"]) &&
                  Value(cable, "Geometry", "Identification", "Label") == "Cable1a-2" && Value(cable, "Geometry", "Identification", "Object type") == "Cable" &&
                  Value(cable, "Geometry", "Geometry", "Length") == "31.623 m" && Value(cable, "Geometry", "End I", "Point") == "TTrPt001" &&
                  Value(cable, "Geometry", "End I", "Z") == "20 m" && Value(cable, "Geometry", "End J", "Point") == "201" && Value(cable, "Geometry", "End J", "X") == "-60 m" &&
                  Value(cable, "Assignments", "Section", "Section property") == cableProperty.SectionName && cableProperty.SectionName == "192T15.7" &&
                  Value(cable, "Assignments", "Analysis properties", "Area A") == "0.028802 m²" && Value(cable, "Assignments", "Analysis properties", "In-plane inertia I") is null &&
                  Value(cable, "Assignments", "Local axes and releases", "Local-axis angle") is null &&
                  Value(cable, "Assignments", "Pretension (03 Pretension)", "Target force") == "100,000 … 105,000 kN @ 10,000" &&
                  Value(cable, "Assignments", "Pretension (03 Pretension)", "Force sync") == "Independent" &&
                  Value(cable, "Assignments", "Pretension (03 Pretension)", "Design capacity") == "55,992 kN" &&
                  Value(cable, "Loads", "Target force", "Pretension") == "100,000 … 105,000 kN @ 10,000",
                "Cable Information lists the cable's geometry (31.623 m, TTrPt001 → 201), its section and analysis properties, and its 03 Pretension target " +
                "(100,000 … 105,000 kN, capacity 55,992 kN) under Assignments and Loads");

            var chord = vm.MemberInformation(Member("TopChord"));
            var chordProperty = vm.FemSectionProperties.Single(property => property.MemberName == "TopChord");
            var selfWeight = vm.FemPatternRows.First(pattern => Math.Abs(pattern.SelfWeightMultiplier) > 1e-12);
            var weight = Value(chord, "Loads", "Self-weight", selfWeight.Name);
            Check(Value(chord, "Geometry", "Identification", "Object type") == "Frame" && Value(chord, "Geometry", "Identification", "Role in the VCF system") == "Top chord" &&
                  Value(chord, "Geometry", "Geometry", "Length") == "100 m" && Value(chord, "Geometry", "Geometry", "Slope from horizontal") == "0°" &&
                  Value(chord, "Assignments", "Section", "Section property") == chordProperty.SectionName && Value(chord, "Assignments", "Section", "Material") == chordProperty.MaterialName &&
                  Value(chord, "Assignments", "Analysis properties", "In-plane inertia I") is { Length: > 0 } &&
                  Value(chord, "Assignments", "Local axes and releases", "Moment release at End I") == (chordProperty.ReleaseStartRotation ? "Released" : "Continuous") &&
                  Value(chord, "Assignments", "Property modifiers", "Moment of inertia (I33)") is { Length: > 0 } &&
                  Value(chord, "Assignments", "Pretension (03 Pretension)", "Target force") is null &&
                  weight is not null && weight.EndsWith("kN/m, gravity", StringComparison.Ordinal) &&
                  silent.Reads == 0 && warnings.Count == 0,
                $"Frame Information lists the top chord's role, length (100 m), section ({chordProperty.SectionName}, {chordProperty.MaterialName}), local axes and releases, " +
                $"and its self-weight in {selfWeight.Name} ({weight}) — without a call to SAP2000");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previousCulture; }
    }

    /// <summary>The 3D view extrudes the real sections: composite sections of the library, and Section Designer sections whose
    /// outline their name states and their stored properties confirm; a composite section's enclosing material is see-through.</summary>
    private static void RunSectionDrawings()
    {
        var (vm, state, silent, warnings) = DefaultModelApp();
        static (double Depth, double Width) Size(SectionRegion region) =>
            (region.Polygons.SelectMany(polygon => polygon).Max(point => point.X2) - region.Polygons.SelectMany(polygon => polygon).Min(point => point.X2),
             region.Polygons.SelectMany(polygon => polygon).Max(point => point.X3) - region.Polygons.SelectMany(polygon => polygon).Min(point => point.X3));
        static bool Is((double Depth, double Width) size, double depth, double width) => Math.Abs(size.Depth - depth) < 1e-9 && Math.Abs(size.Width - width) < 1e-9;
        string SectionOf(string member) => vm.FemSectionProperties.Single(property => property.MemberName == member).SectionName!;

        // the default model holds its composite sections as Section Designer properties only: the names give the outlines back
        var drawings = vm.SharedViewerSectionDrawings!;
        var chord = drawings["TopChord"];
        var narrow = drawings.Values.First(drawing => drawing.Description.StartsWith("Concrete 3000 × 1500", StringComparison.Ordinal));
        var wide = drawings.Values.First(drawing => drawing.Description.StartsWith("Concrete 2500 × 3000", StringComparison.Ordinal));
        Check(SectionOf("TopChord") == "CFT_SHS[1200x2000x150_S460]_C70/85" && chord is { Recovered: true, Composite: true, Description: "Concrete filled tube 2000 × 1200 × 150 mm" } &&
              chord.Regions.Select(region => (region.Material, region.Shell)).SequenceEqual([(SectionMaterial.Concrete, false), (SectionMaterial.Steel, true)]) &&
              Is(Size(chord.Regions[0]), 1.7, 0.9) && Is(Size(chord.Regions[1]), 2.0, 1.2) && chord.Regions[1].Polygons.Count == 4 &&
              SectionOf("170") == "MegaCol_1.5x3_C70/85-1" && ReferenceEquals(drawings["170"], narrow) && narrow is { Recovered: true, Composite: false } && Is(Size(narrow.Regions[0]), 3.0, 1.5) &&
              wide is { Recovered: true, Composite: false } && Is(Size(wide.Regions[0]), 2.5, 3.0) &&
              SectionOf("VRM001") == "SHS_1200x1200x120_S460" && !drawings.ContainsKey("VRM001") && !drawings.ContainsKey("Cable1a-2"),
            "Section Designer sections get their real outline back from their name once it reproduces their A, I33 and I22: the chords a 2000 × 1200 × 150 tube with its " +
            "concrete core (not the 2274 × 1450 equivalent solid), the mega columns 3000 × 1500 and 2500 × 3000 concrete, each with the depth where the inertias put it; " +
            "a plain box (SHS 1200 × 1200 × 120) needs no drawing");

        var stored = vm.FrameSections.Single(section => section.Name == "CFT_SHS[1200x2000x150_S460]_C70/85");
        double? Modulus(string material) => vm.Materials.FirstOrDefault(item => item.Name == material)?.ElasticModulus / 1e6;
        var given = stored.Given!;
        Check(SectionDrawings.Recover(stored, Modulus) is { Composite: true } &&
              SectionDrawings.Recover(stored with { Given = given with { Area = given.Area * 1.03 } }, Modulus) is null &&
              SectionDrawings.Recover(stored with { Given = given with { I33 = given.I33 * 0.97 } }, Modulus) is null &&
              SectionDrawings.Recover(stored with { Name = "CFT chord" }, Modulus) is null &&
              SectionDrawings.Recover(stored with { Name = "CFT_SHS[1000x2000x150_S460]_C70/85" }, Modulus) is null &&
              SectionDrawings.Recover(stored with { Name = "SHS_1200x2000x150", Material = "S460", Given = new SectionValues(0.87, 0.431525, 0.184725, 0, 0, 0) }, Modulus)
                  is { Composite: false, Description: "Steel tube 2000 × 1200 × 150 mm" },
            "An outline is taken from a name only when it reproduces the stored properties within 1 %: 3 % off in area or inertia, other dimensions in the name or no " +
            "dimensions at all leave the section as its equivalent solid; a hollow tube is told from a filled one by the same check");

        // the composite sections of the library are drawn as defined
        SectionDrawing Defined(FrameShape shape, Func<ColumnSectionInput, ColumnSectionInput>? change = null)
        {
            var section = FrameSectionProperty.Default(shape, "S", "C50/60");
            return SectionDrawings.For(change is null ? section : section with { Composite = change(section.Composite!) }, Modulus)!;
        }
        var (tube, round, encased, mega) = (Defined(FrameShape.FilledTube), Defined(FrameShape.FilledTube, c => c with { Tube = TubeShape.Circular }),
            Defined(FrameShape.Encased), Defined(FrameShape.MegaComposite));
        Check(tube is { Composite: true, Recovered: false } && Is(Size(tube.Regions[0]), 0.76, 0.76) && Is(Size(tube.Regions[1]), 0.8, 0.8) && tube.Regions[1] is { Material: SectionMaterial.Steel, Shell: true } &&
              round.Regions[1].Polygons.Count == 24 && Is(Size(round.Regions[1]), 0.8, 0.8) && round.Regions[0] is { Material: SectionMaterial.Concrete, Shell: false } &&
              encased.Regions.Select(region => (region.Material, region.Shell, region.Polygons.Count)).SequenceEqual([(SectionMaterial.Steel, false, 3), (SectionMaterial.Concrete, true, 1)]) &&
              Is(Size(encased.Regions[0]), 0.6, 0.5) && Is(Size(encased.Regions[1]), 1.0, 1.0) &&
              mega.Regions[0].Polygons.Count == 12 && Is(Size(mega.Regions[0]), 1.4 + 0.5, 1.4 + 0.43) && Is(Size(mega.Regions[1]), 2.2, 2.2) &&
              SectionDrawings.For(FrameSectionProperty.Default(FrameShape.Box, "B", "S355"), Modulus) is null,
            "Library composite sections are drawn as defined: a CFT as its tube around the concrete core (rectangular or round), a steel encased section as its I profile " +
            "inside the concrete, a mega composite column with its four profiles; the enclosing material is the shell");

        // 3D: one piece per material, the shell of a composite section see-through and drawn after everything solid
        var real = StructureModel3D.Build(state.Geometry, vm.SharedViewerSections, drawings);
        var equivalent = StructureModel3D.Build(state.Geometry, vm.SharedViewerSections);
        var pieces = real.Model.Children.OfType<System.Windows.Media.Media3D.GeometryModel3D>().ToArray();
        var chordPieces = real.MemberVisuals.Where(visual => visual.Name == "TopChord").ToArray();
        static byte Alpha(System.Windows.Media.Media3D.Material material) =>
            ((System.Windows.Media.SolidColorBrush)((System.Windows.Media.Media3D.DiffuseMaterial)material).Brush).Color.A;
        var firstShell = Array.FindIndex(pieces, piece => piece.BackMaterial is null);
        Check(chordPieces.Length == 2 && chordPieces[0].BackMaterial is not null && Alpha(chordPieces[0].BaseMaterial) == 255 &&
              chordPieces[1].BackMaterial is null && chordPieces[1].Model.BackMaterial is null &&
              Alpha(chordPieces[1].BaseMaterial) == (byte)Math.Round(255 * StructureModel3D.CompositeShellOpacity) &&
              firstShell > 0 && pieces.Skip(firstShell).All(piece => piece.BackMaterial is null) &&
              real.MemberVisuals.Count(visual => visual.Name == "170") == 1 && real.MemberVisuals.Single(visual => visual.Name == "170").BackMaterial is not null &&
              Math.Abs(real.Bounds.Z + real.Bounds.SizeZ - 21.0) < 1e-9 && equivalent.Bounds.Z + equivalent.Bounds.SizeZ > 21.1 &&
              real.FramesApproximated == 0 && equivalent.FramesApproximated > 0 && silent.Reads == 0 && warnings.Count == 0,
            "The 3D model extrudes the real sections: a CFT chord as a solid concrete core inside a see-through steel tube (front faces only, drawn after everything " +
            $"solid, opacity {StructureModel3D.CompositeShellOpacity:0.##}); the truss tops out at Z +21.0 with the 2.0 m deep tube, not +{equivalent.Bounds.Z + equivalent.Bounds.SizeZ:0.00} " +
            "with the equivalent solid; no frame is left as an equivalent solid");
    }

    /// <summary>A cable as a bundle of strands: area = number × steel area of one strand, named by the convention.</summary>
    private static void RunStrandBundle()
    {
        Check(StrandBundle.Name(192, 15.7) == "192T15.7" && StrandBundle.Name(37, 15.2) == "37T15.2" && StrandBundle.Name(12, 18) == "12T18" &&
              StrandBundle.Parse("192T15.7") == (192, 15.7) && StrandBundle.Parse(" 7T12.9 ") == (7, 12.9) &&
              StrandBundle.Parse("192T15.7-A") is null && StrandBundle.Parse("Cable1") is null && StrandBundle.Parse("0T15.7") is null,
            "A strand bundle is named number, T, strand diameter (192T15.7); only a name written that way is read back as strands");
        Check(StrandBundle.StandardArea(15.7) == 150 && StrandBundle.StandardArea(15.3) == 140 && StrandBundle.StandardArea(15.2) == 139 &&
              StrandBundle.StandardArea(12.9) == 100 && StrandBundle.StandardArea(12.5) == 93 && StrandBundle.StandardArea(16) is null &&
              Math.Abs(StrandBundle.Area(192, 150) - 0.0288) < 1e-15 &&
              Math.Abs(new StrandSectionProperty { Name = "192T15.7", Area = StrandBundle.Area(192, 150) }.Diameter - 0.19149) < 1e-5,
            "The listed 7-wire strands carry their nominal steel area (15.7 mm: 150 mm²; an unlisted diameter has none): 192 × 150 mm² = 0.0288 m², " +
            "a solid round of 191.5 mm — the model's 192T15.7 cable");
    }

    /// <summary>File → Save Config (Ctrl+S) and Save Config As… (Ctrl+Shift+S), with the Save dialog answered by the check.</summary>
    private static void RunSaveAs()
    {
        var (vm, _, silent, warnings) = DefaultModelApp();
        var folder = Path.Combine(Path.GetTempPath(), $"mcotis-saveas-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var first = Path.Combine(folder, "first" + FemInputDatabase.Extension);
            var second = Path.Combine(folder, "second" + FemInputDatabase.Extension);
            var asked = new List<(string? Folder, string FileName)>();
            string? answer = Path.Combine(folder, "first");   // typed without the extension
            vm.AskFemSavePath = (startFolder, fileName) => { asked.Add((startFolder, fileName)); return answer; };

            const string product = "Mcotis — Meinhardt Cities of Tomorrow Integrated Structures — ";
            var untitled = vm.WindowTitle;
            vm.SaveFemModel.Execute(null);
            Check(untitled == product + "Default model" && vm.WindowTitle == product + "first" + FemInputDatabase.Extension && vm.CurrentFileName == "first" + FemInputDatabase.Extension,
                "The window title names the file beside the product: 'Default model' until the configuration is saved, then its file name");
            Check(vm.SaveFemModel.CanExecute(null) && vm.SaveFemModelAs.CanExecute(null) && asked.Count == 1 &&
                  asked[0].FileName.EndsWith(FemInputDatabase.Extension, StringComparison.Ordinal) && File.Exists(first) &&
                  vm.FemProgressText.StartsWith("Saved MCOTiS config first" + FemInputDatabase.Extension, StringComparison.Ordinal),
                "Save on a configuration that has no file yet asks for one (and adds the extension to the name typed)");
            vm.SaveFemModel.Execute(null);
            Check(asked.Count == 1 && vm.FemProgressText.StartsWith("Saved MCOTiS config first", StringComparison.Ordinal),
                "Save again overwrites the active configuration without asking");

            answer = second;
            var firstWritten = File.GetLastWriteTimeUtc(first);
            vm.SaveFemModelAs.Execute(null);
            Check(vm.WindowTitle == product + "second" + FemInputDatabase.Extension, "Save As puts the new file name in the window title");
            Check(asked.Count == 2 && asked[1] == (folder, "first" + FemInputDatabase.Extension) && File.Exists(second) &&
                  File.GetLastWriteTimeUtc(first) == firstWritten && vm.FemProgressText.StartsWith("Saved MCOTiS config second", StringComparison.Ordinal),
                "Save As asks for a file, starting from the active configuration's folder and name, writes the new file and leaves the old one as it was");
            vm.SaveFemModel.Execute(null);
            Check(asked.Count == 2 && File.GetLastWriteTimeUtc(first) == firstWritten && vm.FemProgressText.StartsWith("Saved MCOTiS config second", StringComparison.Ordinal),
                "after Save As, Save overwrites the new file: it is the active configuration");

            answer = null;
            var files = Directory.GetFiles(folder).Length;
            var message = vm.FemProgressText;
            vm.SaveFemModelAs.Execute(null);
            Check(asked.Count == 3 && Directory.GetFiles(folder).Length == files && vm.FemProgressText == message && silent.Reads == 0 && warnings.Count == 0,
                "cancelling Save As writes nothing and keeps the active configuration");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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
    internal static IReadOnlyList<FemStudyRun> Analyze(MainViewModel vm)
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

    private static void UnlockForFurtherEdits(MainViewModel vm, string? checkName = null)
    {
        Check(vm.IsFemModelLocked && vm.UnlockFemModel.CanExecute(null), checkName ?? "the completed FEM run leaves the model locked");
        vm.UnlockFemModel.Execute(null);
        Check(!vm.IsFemModelLocked && vm.FemRuns.Count == 0 && !vm.HasFemResults,
            "Unlock removes the run output and requires a new analysis before results are available");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    private static bool Refuses(Action read)
    {
        try { read(); return false; }
        catch (InvalidDataException) { return true; }
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
