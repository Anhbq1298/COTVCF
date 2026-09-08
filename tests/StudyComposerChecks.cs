using COT_VCF.Application;
using COT_VCF.Configuration;
using COT_VCF.Domain;
using System.Numerics;

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

        var filteredState = CreateState();
        filteredState.Study.Mode = IterationStudyMode.Combined;
        filteredState.Study.UseCandidateFilters = true;
        filteredState.Study.MaxLoadingKpa = 3;
        filteredState.Study.MinHeightM = 20;
        filteredState.Study.MaxPretensionKn = 1200;
        filteredState.Study.MaxLoadingToHeightRatio = 0.12;
        filteredState.Study.MaxPretensionToHeightRatio = 55;
        var filtered = composer.Compose(filteredState);
        Check(filtered.UnfilteredRunCount == 216 && filtered.ExpectedRunCount == 20 &&
              filtered.Iterations.Count == 20 && filtered.FilterAudit.ExcludedCount == 196,
            "Persisted axis bounds and cross-dimension L/H + P/H predicates produce an exact audited count");
        var filteredDto = new ConfigurationExtractor().ExtractStudySection(filteredState);
        var restoredFilterState = CreateState();
        var filterCheck = new ConfigValidationResult();
        filterCheck.Study.Present = true;
        new ConfigurationApplicator(restoredFilterState, new WorkflowService(new NoOpSap(), restoredFilterState))
            .Apply(new CotVcfConfigDto { Tab05IterationStudy = filteredDto }, filterCheck);
        Check(restoredFilterState.Study.UseCandidateFilters &&
              restoredFilterState.Study.MaxLoadingToHeightRatio == 0.12 &&
              composer.Compose(restoredFilterState).ExpectedRunCount == 20,
            "Candidate filters round-trip through the project configuration and reproduce the same study rows");

        var billionState = CreateState();
        billionState.Study.Mode = IterationStudyMode.Combined;
        billionState.LoadDefinitions[0].MaxQz = 1000;
        billionState.Truss.MinimumHeight = 18;
        billionState.Truss.MaximumHeight = 1017;
        billionState.Truss.Increment = 1;
        billionState.Cables[0].MinForce = 1000;
        billionState.Cables[0].MaxForce = 1999;
        billionState.Cables[0].StepForce = 1;
        billionState.Study.UseCandidateFilters = true;
        billionState.Study.MaxLoadingKpa = 1;
        billionState.Study.MaxHeightM = 18;
        billionState.Study.MaxPretensionKn = 1000;
        var reduced = composer.Compose(billionState);
        Check(reduced.UnfilteredRunCount == BigInteger.Parse("1000000000") &&
              reduced.ExpectedRunCount == 1 && reduced.Iterations.Count == 1,
            "A billion-row Cartesian space is counted exactly and reduced without materializing the product");

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

        CableSyncChecks();
    }

    // Cable Sync: Independent contributes a Cartesian axis; SameValues copies the reference's values;
    // SameStep keeps its OWN Min / Max / Step range but zips its candidate index to the reference.
    // Neither follower multiplies the product. Only Independent → Independent references are honoured.
    private static void CableSyncChecks()
    {
        CablePretensionConfig Range(string name, double min, double max, double step) => new()
        {
            CableName = name, LoadCase = CablePretensionConfig.PretensionLoadCase,
            ApplyRange = true, MinForce = min, MaxForce = max, StepForce = step
        };

        // TEST 1 — Same Step zip
        var a = Range("Cable6a", 15000, 20000, 2500);   // [15000, 17500, 20000]
        var b = Range("Cable6b", 20000, 30000, 5000);   // [20000, 25000, 30000] — zipped to Cable6a
        b.SyncMode = CableSyncMode.SameStep; b.ReferenceCableName = "Cable6a";
        Check(b.IsSameStepFollower && b.CandidateSummary == "lockstep" && b.SyncSummary == "Step · Cable6a",
            "Same Step follower shows 'lockstep' / 'Step · <ref>'");
        Check(b.CandidateCount == 1 && b.CanEditRange && !b.CanEditStepForce && !b.CanEditTarget,
            "Same Step follower edits its own Min/Max but not Step (derived) or Target");
        var t1 = PretensionCandidates.Enumerate(new List<CablePretensionConfig> { a, b }).ToList();
        Check(t1.Count == 3
            && t1[0].CableForces["Cable6a"] == 15000 && t1[0].CableForces["Cable6b"] == 20000
            && t1[1].CableForces["Cable6a"] == 17500 && t1[1].CableForces["Cable6b"] == 25000
            && t1[2].CableForces["Cable6a"] == 20000 && t1[2].CableForces["Cable6b"] == 30000,
            "TEST 1 — Same Step zips index-for-index, total = 3 (not 9)");

        // Same Step derives its spacing from the reference — no Step Force needed, no count mismatch possible
        var bd = Range("Cable6bd", 10000, 16000, 999);   // Step value here is ignored
        bd.SyncMode = CableSyncMode.SameStep; bd.ReferenceCableName = "Cable6a";
        var td = PretensionCandidates.Enumerate(new List<CablePretensionConfig> { a, bd }).ToList();
        Check(td.Count == 3
            && td[0].CableForces["Cable6bd"] == 10000 && td[1].CableForces["Cable6bd"] == 13000 && td[2].CableForces["Cable6bd"] == 16000,
            "Same Step spacing is derived to land on exactly the reference's value count");

        // TEST 2 — Cartesian × Same Step
        var c = Range("Cable7a", 30000, 45000, 5000);   // [30000, 35000, 40000, 45000] — independent axis
        var t2 = new List<CablePretensionConfig> { a, b, c };
        Check((int)PretensionCandidates.TotalCount(t2) == 12 && PretensionCandidates.VariableCount(t2) == 2,
            "TEST 2 — Independent A(3) × Independent C(4) with SameStep B = 12 (not 3·3·4)");
        Check(PretensionCandidates.Enumerate(t2).Select(x => x.CableForces["Cable7a"]).Distinct().Count() == 4,
            "TEST 2 — Cable7a still sweeps its own axis");

        // TEST 3 — Same Values
        var e = new CablePretensionConfig { CableName = "Cable6c", LoadCase = CablePretensionConfig.PretensionLoadCase,
            SyncMode = CableSyncMode.SameValues, ReferenceCableName = "Cable6a" };
        Check(e.CandidateSummary == "linked" && e.CandidateCount == 1 && !e.CanEditRange && !e.CanEditTarget,
            "TEST 3 — Same Values follower is 'linked', fields locked");
        var t3 = PretensionCandidates.Enumerate(new List<CablePretensionConfig> { a, e }).ToList();
        Check(t3.Count == 3 && t3.All(x => x.CableForces["Cable6c"] == x.CableForces["Cable6a"]),
            "TEST 3 — Same Values follower copies the reference's exact values");

        // TEST 5 — shallow chains resolve transitively to the Independent root
        var chained = Range("Cable6d", 5000, 15000, 1);              // own Min/Max, own step ignored
        chained.SyncMode = CableSyncMode.SameStep; chained.ReferenceCableName = "Cable6b";   // Cable6b follows Cable6a
        var t5 = new List<CablePretensionConfig> { a, b, chained };
        Check(PretensionCandidates.IsFollower(chained, t5)
              && ReferenceEquals(CableSyncGraph.RootOf(chained, t5), a),
            "TEST 5 — a chain C → B → A resolves to root A and C is a follower (no extra axis)");
        var t5c = PretensionCandidates.Enumerate(t5).ToList();
        Check(t5c.Count == 3
              && t5c[0].CableForces["Cable6d"] == 5000 && t5c[1].CableForces["Cable6d"] == 10000 && t5c[2].CableForces["Cable6d"] == 15000,
            "TEST 5 — chained follower is spread over the root's value count");
        // Same Ratio / Same Offset
        var ratio = new CablePretensionConfig { CableName = "Cable6r", LoadCase = CablePretensionConfig.PretensionLoadCase,
            SyncMode = CableSyncMode.SameRatio, ReferenceCableName = "Cable6a", SyncFactor = 1.1 };
        var offset = new CablePretensionConfig { CableName = "Cable6o", LoadCase = CablePretensionConfig.PretensionLoadCase,
            SyncMode = CableSyncMode.SameOffset, ReferenceCableName = "Cable6a", SyncFactor = 500 };
        var tr = PretensionCandidates.Enumerate(new List<CablePretensionConfig> { a, ratio, offset }).ToList();
        Check(tr.Count == 3
              && tr[1].CableForces["Cable6r"] == 17500 * 1.1 && tr[1].CableForces["Cable6o"] == 17500 + 500,
            "TEST 5 — Same Ratio = reference × k, Same Offset = reference + Δ, neither adds an axis");
        Check((int)PretensionCandidates.TotalCount(new List<CablePretensionConfig> { a, ratio, offset }) == 3,
            "TEST 5 — Ratio / Offset followers do not multiply the Cartesian product");

        // TEST 6 — cycle detection over hand-edited config
        var x1 = new CablePretensionConfig { CableName = "X1", LoadCase = CablePretensionConfig.PretensionLoadCase, SyncMode = CableSyncMode.SameStep, ReferenceCableName = "X2" };
        var x2 = new CablePretensionConfig { CableName = "X2", LoadCase = CablePretensionConfig.PretensionLoadCase, SyncMode = CableSyncMode.SameStep, ReferenceCableName = "X1" };
        var cyc = PretensionCandidates.FindCycle(new List<CablePretensionConfig> { x1, x2 });
        Check(cyc.Count == 2 && cyc.Contains("X1") && cyc.Contains("X2"), "TEST 6 — circular Sync reference is detected");
        Check(PretensionCandidates.FindCycle(t2).Count == 0, "acyclic graph reports no cycle");

        // TEST 7 — master with followers
        Check(PretensionCandidates.FollowersOf(a, t2).Select(f => f.CableName).OrderBy(n => n).SequenceEqual(new[] { "Cable6b" }),
            "TEST 7 — FollowersOf reports the cables that reference a master");

        // mode/reference hygiene
        b.SyncMode = CableSyncMode.Independent;
        Check(b.ReferenceCableName is null && b.CandidateSummary == "3", "Setting Independent clears the reference");
        var self = new CablePretensionConfig { CableName = "Cable9", LoadCase = CablePretensionConfig.PretensionLoadCase, SyncMode = CableSyncMode.SameValues };
        self.ReferenceCableName = "Cable9";
        Check(self.ReferenceCableName is null, "A cable can never reference itself");
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
        public Task<IReadOnlyList<CableSection>> GetCableSectionsAsync(IReadOnlyList<string> cableNames, ModelInfo expected) => Task.FromResult<IReadOnlyList<CableSection>>([]);
        public Task<IReadOnlyList<COT_VCF.Domain.Results.JointDisplacement>> GetJointDisplacementsAsync(IReadOnlyList<string> pointNames, string outputCase, bool isCombo, ModelInfo expected) => throw new NotSupportedException();
        public Task<IReadOnlyList<COT_VCF.Domain.Results.CableForceRead>> GetCableForcesAsync(IReadOnlyList<string> cableNames, string outputCase, bool isCombo, ModelInfo expected) => throw new NotSupportedException();
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
