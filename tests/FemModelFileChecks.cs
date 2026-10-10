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
                case "Dispose":
                    return null;
                default:
                    Writes++; throw new InvalidOperationException($"Unexpected SAP2000 call {method?.Name}.");
            }
        }
    }
}
