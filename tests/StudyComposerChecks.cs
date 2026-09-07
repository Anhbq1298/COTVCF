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
        var pathRow = fixHeight.Iterations.Single(r => r.IterationKey == "L02_H03_P04");
        Check(pathRow.IterationDirectory == @"C:\Models\Study\L02_H03_P04" &&
              pathRow.ModelFullPath == @"C:\Models\Study\L02_H03_P04\COT_VCF_L02_H03_P04.sdb",
            "Every iteration previews its own deterministic folder and child model path");
        Check(fixHeight.Iterations[0].Description.Contains("Height 15 \u2192 20 m (\u0394H +5 m, H03)"), "Height description compares against the first-connected baseline");
        var described = fixHeight.Iterations.Single(r => r.IterationKey == "L02_H03_P04").Description;
        Check(described.Contains("Fz -1 \u2192 -2 kN") && described.Contains("Cable-A@PRETENSION target 1300 kN"),
            "Loading description uses the immutable baseline and pretension names its exact target assignment");
        Check(fixHeight.Iterations[0].LoadingCandidate.PointLoads.Count == 2,
            "Loading candidate retains its complete point-load assignment set");
        Check(fixHeight.Iterations[0].LoadingCandidate.Loads.Count == 2 &&
              fixHeight.Iterations[0].LoadingCandidate.Loads.All(load => load.Fz < 0),
            "Loading candidate resolves complete downward point-load assignments");
        Check(fixHeight.Iterations[0].LoadingAssignmentDetails.Contains("LIVE: 2 joints [load2, top]") &&
              fixHeight.Iterations.First(row => row.PretensionCandidateId == "P04").PretensionAssignmentDetails ==
              "Cable-A @ PRETENSION = 1300 kN",
            "Study rows expose exact joint-load and cable target-force assignments");

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

        var elevenJointState = CreateState();
        for (int index = 3; index <= 11; index++)
            elevenJointState.AppliedPoints.Add(new AppliedPoint
            {
                PointName = $"load{index}", X = index, Y = 0, Z = 0, Order = index
            });
        elevenJointState.LoadPatterns.Add("DEAD");
        elevenJointState.LoadDefinitions.Add(new LoadDefinition { LoadName = "DEAD", Qz = 5 });
        var elevenJointCandidate = composer.Compose(elevenJointState).LoadingCandidates.First();
        Check(elevenJointCandidate.JointCount == 11 && elevenJointCandidate.Loads.Count == 22 &&
              elevenJointCandidate.Loads.Select(load => load.PointName).Distinct(StringComparer.Ordinal).Count() == 11 &&
              elevenJointCandidate.Loads.Select(load => load.LoadPattern).Distinct(StringComparer.Ordinal).Count() == 2,
            "Eleven Tab 1 joints and two load patterns produce all 22 point-load assignments");

        SameStepChecks();
    }

    // "Same Step": a follower keeps its OWN Min / Max / Step range but is zipped to its leader by step
    // index — it must not multiply the Cartesian product. "Same As" still copies the leader's force.
    private static void SameStepChecks()
    {
        CablePretensionConfig Range(string name, double min, double max, double step) => new()
        {
            CableName = name, LoadCase = CablePretensionConfig.PretensionLoadCase,
            ApplyRange = true, MinForce = min, MaxForce = max, StepForce = step
        };
        var a = Range("Cable6a", 15000, 20000, 2500);   // [15000, 17500, 20000]
        var b = Range("Cable6b", 20000, 30000, 5000);   // [20000, 25000, 30000] — zipped to Cable6a
        b.SameStepCableName = "Cable6a";
        var c = Range("Cable7a", 30000, 35000, 5000);   // [30000, 35000] — independent axis
        var cables = new List<CablePretensionConfig> { a, b, c };

        Check(b.IsFollower && b.CandidateSummary == "lockstep", "Same Step follower shows 'lockstep'");
        Check(b.CandidateCount == 3 && b.CanEditRange, "Same Step follower keeps its own editable step count");
        Check((int)PretensionCandidates.TotalCount(cables) == 6, "Same Step is zipped not Cartesian: 3 x 2 = 6 (not 18)");
        Check(PretensionCandidates.VariableCount(cables) == 2, "Same Step follower is not a variable axis");

        var combos = PretensionCandidates.Enumerate(cables).ToList();
        Check(combos.Count == 6, "Same Step yields 6 combinations");
        Check(combos[0].CableForces["Cable6a"] == 15000 && combos[0].CableForces["Cable6b"] == 20000, "step 0 zips 15000 -> 20000");
        Check(combos[2].CableForces["Cable6a"] == 17500 && combos[2].CableForces["Cable6b"] == 25000, "step 1 zips 17500 -> 25000");
        Check(combos[4].CableForces["Cable6a"] == 20000 && combos[4].CableForces["Cable6b"] == 30000, "step 2 zips 20000 -> 30000");
        Check(combos.Select(x => x.CableForces["Cable7a"]).Distinct().OrderBy(v => v).SequenceEqual(new[] { 30000.0, 35000.0 }),
            "Cable7a still sweeps its own independent axis");

        var d = new CablePretensionConfig { CableName = "Cable6c", LoadCase = CablePretensionConfig.PretensionLoadCase, SameAsCableName = "Cable6a" };
        var withSameAs = PretensionCandidates.Enumerate(new List<CablePretensionConfig> { a, d }).ToList();
        Check(withSameAs.Count == 3 && withSameAs.All(x => x.CableForces["Cable6c"] == x.CableForces["Cable6a"]),
            "Same As still copies the leader's exact force (not its own range)");

        b.SameStepCableName = null; b.SameAsCableName = "Cable6a";
        Check(b.IsSameAsFollower && !b.IsSameStepFollower && b.SameStepCableName is null,
            "Setting Same As clears Same Step (mutually exclusive)");
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
        public Task<ModelInfo> OpenModelAsync(string modelPath) => throw new NotSupportedException();
        public Task<ModelInfo> SaveAsAsync(string targetPath, ModelInfo expected) => throw new NotSupportedException();
        public Task SaveAsync(ModelInfo expected) => Task.CompletedTask;
        public Task RunAnalysisAsync(ModelInfo expected) => Task.CompletedTask;
        public Task<ModelInfo> GetCurrentModelInfoAsync() => throw new NotSupportedException();
        public Task<ModelSnapshot> ReadAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<PointLoad>> ReadPointLoadsAsync(ModelInfo expected) => Task.FromResult<IReadOnlyList<PointLoad>>([]);
        public Task<Position> GetPointPositionAsync(string pointName, ModelInfo expected) => Task.FromResult(new Position());
        public Task<IReadOnlyList<PointLoad>> GetPointLoadsAsync(string pointName, ModelInfo expected) => Task.FromResult<IReadOnlyList<PointLoad>>([]);
        public Task<IReadOnlyList<CableTargetForce>> GetCableTargetForcesAsync(string cableName, ModelInfo expected) => Task.FromResult<IReadOnlyList<CableTargetForce>>([]);
        public Task<IReadOnlyList<COT_VCF.Domain.Results.JointDisplacement>> GetJointDisplacementsAsync(IReadOnlyList<string> pointNames, string outputCase, bool isCombo, ModelInfo expected) => throw new NotSupportedException();
        public Task<IReadOnlyList<ForceSource>> ReadForcesAsync(IReadOnlyList<StructuralMember> members, ModelInfo expected) =>
            Task.FromResult<IReadOnlyList<ForceSource>>([]);
        public Task ApplyLoadsAsync(IReadOnlyList<PointLoad> loads, ModelInfo expected) => Task.CompletedTask;
        public Task MovePointsAsync(GeometryModel expectedGeometry, GeometryModel proposed, ModelInfo expected) => Task.CompletedTask;
        public Task SetCableTargetForceAsync(string cableName, string loadPattern, double targetForce, ModelInfo expected) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetLoadCaseNamesAsync(ModelInfo expected) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<SapGroupSnapshot>> ReadOutputGroupsAsync(IReadOnlyList<string> groupNames, ModelInfo expected) =>
            Task.FromResult<IReadOnlyList<SapGroupSnapshot>>(groupNames.Select(name => new SapGroupSnapshot(name, false, [], [], [])).ToArray());
        public Task ReplaceOutputGroupsAsync(IReadOnlyList<OutputSapGroupDefinition> groups, ModelInfo expected) => Task.CompletedTask;
        public void Dispose() { }
    }
}
