using COT_VCF.Application;
using COT_VCF.Configuration;
using COT_VCF.Domain;

internal static class StudyComposerChecks
{
    public static void Run()
    {
        var state = CreateState();
        var composer = new StudyCompositionService();

        state.Study.Mode = IterationStudyMode.FixHeight;
        state.Study.FixedHeightCandidateId = "H03";
        var fixHeight = composer.Compose(state);
        Check(fixHeight.Iterations.Count == 36, "Fix Height generates 6 x 1 x 6 rows");
        Check(fixHeight.Iterations.All(r => r.HeightCandidateId == "H03"), "Fix Height retains H03 for every row");
        Check(fixHeight.Iterations.Single(r => r.IterationKey == "L02_H03_P04").SapModelName == "COT_VCF_L02_H03_P04.sdb", "Stable IDs generate the expected SAP filename");
        Check(fixHeight.Iterations[0].Description.Contains("Height 15 \u2192 20 m (\u0394H +5 m, H03)"), "Height description compares against the first-connected baseline");
        var described = fixHeight.Iterations.Single(r => r.IterationKey == "L02_H03_P04").Description;
        Check(described.Contains("Fz -1 \u2192 -2 kN") && described.Contains("Cable-A 900 \u2192 1300"),
            "Loading and pretension descriptions use immutable baseline values");
        Check(fixHeight.Iterations[0].LoadingCandidate.PointLoads.Count == 2,
            "Loading candidate retains its complete point-load assignment set");

        state.Study.Mode = IterationStudyMode.FixPretension;
        state.Study.FixedPretensionCandidateId = "P02";
        var fixPretension = composer.Compose(state);
        Check(fixPretension.Iterations.Count == 36, "Fix Pretension generates 6 x 6 x 1 rows");
        Check(fixPretension.Iterations.All(r => r.PretensionCandidateId == "P02"), "Fix Pretension retains P02 for every row");

        state.Study.FixedPretensionCandidateId = "";
        var defaultedPretension = composer.Compose(state);
        Check(defaultedPretension.EffectiveFixedPretensionId == "P01" &&
              defaultedPretension.Iterations.Count == 36 &&
              defaultedPretension.Iterations.All(r => r.PretensionCandidateId == "P01"),
            "Fix Pretension defaults an unavailable selection to P01");
        state.Study.FixedPretensionCandidateId = "P02";

        state.Study.Mode = IterationStudyMode.Combined;
        var combined = composer.Compose(state);
        Check(combined.Iterations.Count == 216, "Combined generates 6 x 6 x 6 rows");
        Check(combined.Iterations[0].IterationKey == "L01_H01_P01" &&
              combined.Iterations[1].IterationKey == "L01_H01_P02" &&
              combined.Iterations[6].IterationKey == "L01_H02_P01" &&
              combined.Iterations[36].IterationKey == "L02_H01_P01", "Cartesian ordering is deterministic");

        foreach (var row in combined.Iterations.Take(4)) state.Study.SetRunSelected(row.IterationKey, false);
        var restored = composer.Compose(state);
        Check(restored.Iterations.Count(r => r.RunIterate) == 212, "Run selection restores by stable iteration key");

        var saved = new ConfigurationExtractor().ExtractStudySection(state);
        Check(saved.StudyMode == IterationStudyMode.Combined && saved.ExcludedIterationKeys.Count == 4,
            "Tab 5 mode and exclusions are included in config extraction");

        var loadedState = CreateState();
        var loadCheck = new ConfigValidationResult();
        loadCheck.Study.Present = true;
        var project = new CotVcfConfigDto { Tab05IterationStudy = saved };
        new ConfigurationApplicator(loadedState, new WorkflowService(new NoOpSap(), loadedState)).Apply(project, loadCheck);
        var loadedStudy = composer.Compose(loadedState);
        Check(loadedState.Study.Mode == IterationStudyMode.Combined && loadedStudy.Iterations.Count == 216 &&
              loadedStudy.Iterations.Count(r => r.RunIterate) == 212,
            "Config load rebuilds rows and restores Run Iterate selections by key");
    }

    internal static ApplicationState CreateState()
    {
        var geometry = new GeometryModel(new Dictionary<string, StructuralPoint>
        {
            ["top"] = new("top", new(0, 0, 15)),
            ["mid"] = new("mid", new(0, 0, 7.5)),
            ["bot"] = new("bot", new(0, 0, 0)),
            ["load2"] = new("load2", new(2, 0, 0)),
        }, [new("Cable-A", "top", "bot", MemberKind.Cable)]);
        var model = new ModelInfo(@"C:\Models\COT_VCF.sdb", "26", 6, "kN", "m", false);
        var state = new ApplicationState
        {
            Connection = "Connected",
            Model = model,
            Geometry = geometry,
            Baseline = geometry,
            SessionBaselineModel = model,
            SessionBaseline = geometry,
            BaselineCaptured = true,
            NumberOfStories = 1,
            TributaryWidthB = 1,
        };
        state.LoadPatterns.Add("LIVE");
        state.LoadPatterns.Add(CablePretensionConfig.PretensionLoadCase);
        state.AppliedPoints.Add(new AppliedPoint { PointName = "top", X = 0, Y = 0, Z = 15, Order = 1 });
        state.AppliedPoints.Add(new AppliedPoint { PointName = "load2", X = 2, Y = 0, Z = 0, Order = 2 });
        state.SessionBaselinePointLoads =
        [
            new PointLoad("top", "LIVE", 0, 0, -1),
            new PointLoad("load2", "LIVE", 0, 0, -1),
        ];
        state.SessionBaselinePretensionForces = new Dictionary<string, double>(StringComparer.Ordinal) { ["Cable-A"] = 900 };
        state.LoadDefinitions.Clear();
        state.LoadDefinitions.Add(new LoadDefinition
        {
            LoadName = "LIVE", ApplyIncrementStep = true, MinQz = 1, MaxQz = 6, IncrementStep = 1
        });
        state.Truss.TopPoints = ["top"];
        state.Truss.MiddlePoints = ["mid"];
        state.Truss.BottomPoints = ["bot"];
        state.Truss.BaselineHeight = 15;
        state.Truss.MinimumHeight = 18;
        state.Truss.MaximumHeight = 23;
        state.Truss.Increment = 1;
        state.Truss.CurrentHeight = 18;
        state.Cables.Add(new CablePretensionConfig
        {
            CableName = "Cable-A", LoadCase = CablePretensionConfig.PretensionLoadCase,
            ApplyRange = true, MinForce = 1000, MaxForce = 1500, StepForce = 100
        });
        state.Study.OutputDirectory = @"C:\Study\Models";
        return state;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    private sealed class NoOpSap : ISapGateway
    {
        public Task<ModelInfo> ConnectAsync() => throw new NotSupportedException();
        public Task<ModelSnapshot> ReadAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<PointLoad>> ReadPointLoadsAsync(ModelInfo expected) => Task.FromResult<IReadOnlyList<PointLoad>>([]);
        public Task<IReadOnlyList<ForceSource>> ReadForcesAsync(IReadOnlyList<StructuralMember> members, ModelInfo expected) =>
            Task.FromResult<IReadOnlyList<ForceSource>>([]);
        public Task ApplyLoadsAsync(IReadOnlyList<PointLoad> loads, ModelInfo expected) => Task.CompletedTask;
        public Task MovePointsAsync(GeometryModel expectedGeometry, GeometryModel proposed, ModelInfo expected) => Task.CompletedTask;
        public Task SetCableTargetForceAsync(string cableName, string loadPattern, double targetForce, ModelInfo expected) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetLoadCaseNamesAsync(ModelInfo expected) => Task.FromResult<IReadOnlyList<string>>([]);
        public void Dispose() { }
    }
}
