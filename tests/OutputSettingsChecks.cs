using COT_VCF.Application;
using COT_VCF.Configuration;
using COT_VCF.Domain;

internal static class OutputSettingsChecks
{
    public static void Run()
    {
        Check(OutputGroupNaming.MemberGroupName("TopChords") == "[O]-TopChords", "Member output group receives [O]- prefix");
        Check(OutputGroupNaming.MemberGroupName("[O]-TopChords") == "[O]-TopChords", "Output group prefix is not duplicated");
        Check(OutputUnits.Force == "kN" && OutputUnits.Moment == "kNm" && OutputUnits.Displacement == "mm", "Output units are fixed");

        var geometry = new GeometryModel(new Dictionary<string, StructuralPoint>
        {
            ["P1"] = new("P1", new(0, 0, 0)),
            ["P2"] = new("P2", new(1, 0, 0)),
            ["P3"] = new("P3", new(2, 0, 0)),
            ["P4"] = new("P4", new(3, 0, 0)),
        },
        [
            new StructuralMember("F1", "P1", "P2", MemberKind.Frame),
            new StructuralMember("F2", "P2", "P3", MemberKind.Frame),
            new StructuralMember("C1", "P1", "P4", MemberKind.Cable),
        ]);
        var model = new ModelInfo(@"C:\Models\Parent.sdb", "26", 6, "kN", "m", false);
        var state = new ApplicationState
        {
            Connection = "Connected",
            Model = model,
            SessionBaselineModel = model,
            SessionBaseline = geometry,
            Geometry = geometry,
            Baseline = geometry,
        };
        state.OutputSettings.DeflectionPoints.Add("P2");
        state.OutputSettings.FoundationLeftPoints.Add("P1");
        state.OutputSettings.FoundationLeftPoints.Add("P2");
        state.OutputSettings.FoundationRightPoints.Add("P3");
        state.OutputSettings.FoundationRightPoints.Add("P4");
        state.OutputSettings.CableNames.Add("C1");
        state.OutputSettings.CableResultLoadCase = "CASE1";
        state.OutputSettings.DeflectionOutputCaseName = "CASE1";
        state.OutputSettings.MemberForceResultLoadCase = "CASE1";
        state.OutputSettings.FoundationReactionResultLoadCase = "CASE1";
        var top = new MemberForceOutputGroup { Name = "TopChords" };
        top.Members.Add("F1");
        state.OutputSettings.MemberForceGroups.Add(top);

        var valid = new OutputSettingsValidator().Validate(state, ["CASE1"]);
        Check(valid.Count == 0, "Complete output settings validate");

        state.OutputSettings.DeflectionLimitMinMm = 150;
        state.OutputSettings.DeflectionLimitMaxMm = 100;
        Check(new OutputSettingsValidator().Validate(state, ["CASE1"]).Any(i => i.Message.Contains("limit band Max") && i.Severity == ValidationSeverity.Error),
            "A deflection limit band with Max below Min is an error");
        state.OutputSettings.DeflectionLimitMinMm = -5;
        state.OutputSettings.DeflectionLimitMaxMm = null;
        Check(new OutputSettingsValidator().Validate(state, ["CASE1"]).Any(i => i.Message.Contains("limit band Min") && i.Severity == ValidationSeverity.Error),
            "A negative deflection limit is an error");
        state.OutputSettings.DeflectionLimitMinMm = 100;
        state.OutputSettings.DeflectionLimitMaxMm = 150;
        Check(new OutputSettingsValidator().Validate(state, ["CASE1"]).Count == 0 &&
              COT_VCF.Application.Results.StudyDecisionCriteria.From(state).DeflectionBand == new DeflectionLimitBand(100, 150),
            "A 100–150 mm deflection limit band validates and becomes the Shortlist deflection criterion");

        state.OutputSettings.DeflectionOutputCaseName = "";
        var noCase = new OutputSettingsValidator().Validate(state, ["CASE1"]);
        Check(noCase.Any(i => i.Message.Contains("deflection output case") && i.Severity == ValidationSeverity.Warning),
            "Monitored points without a deflection output case are incomplete, not invalid");
        state.OutputSettings.DeflectionOutputCaseName = "MISSING";
        Check(new OutputSettingsValidator().Validate(state, ["CASE1"]).Any(i => i.Message.Contains("MISSING") && i.Severity == ValidationSeverity.Error),
            "A deflection output case that no longer exists is an error");
        state.OutputSettings.DeflectionOutputCaseName = "CASE1";

        var duplicate = new MemberForceOutputGroup { Name = "Other" };
        duplicate.Members.Add("F1");
        state.OutputSettings.MemberForceGroups.Add(duplicate);
        Check(new OutputSettingsValidator().Validate(state, ["CASE1"]).Any(issue => issue.Message.Contains("assigned to both")),
            "A frame cannot belong to two member-force output groups");
        state.OutputSettings.MemberForceGroups.Remove(duplicate);

        var dto = new ConfigurationExtractor().ExtractOutputSection(state);
        Check(dto.MemberForceGroups.Single().SapGroupName == "[O]-TopChords" &&
              dto.FoundationLeftPointNames.Count == 2 && dto.CableResultLoadCase == "CASE1" &&
              dto.DeflectionOutputCaseName == "CASE1" && dto.MemberForceResultLoadCase == "CASE1" &&
              dto.FoundationReactionResultLoadCase == "CASE1" &&
              dto.DeflectionLimitMinMm == 100 && dto.DeflectionLimitMaxMm == 150,
            "Output settings extract to project DTO");

        var target = new ApplicationState { Geometry = geometry, SessionBaseline = geometry, Model = model, SessionBaselineModel = model, Connection = "Connected" };
        var project = new CotVcfConfigDto { Tab05OutputSettings = dto };
        var check = new ConfigValidationResult();
        check.Output.Present = true;
        new ConfigurationApplicator(target, new WorkflowService(new FakeSap(model), target)).Apply(project, check);
        Check(target.OutputSettings.MemberForceGroups.Single().SapGroupName == "[O]-TopChords" &&
              target.OutputSettings.CableResultLoadCase == "CASE1" &&
              target.OutputSettings.DeflectionOutputCaseName == "CASE1" &&
              target.OutputSettings.MemberForceResultLoadCase == "CASE1" &&
              target.OutputSettings.FoundationReactionResultLoadCase == "CASE1" &&
              target.OutputSettings.DeflectionLimitBand == new DeflectionLimitBand(100, 150),
            "Output settings restore without writing SAP");

        var fake = new FakeSap(model);
        new OutputGroupPreparationService(fake).PrepareParentAsync(state).GetAwaiter().GetResult();
        Check(fake.ReplaceCalls == 1 && fake.SaveCalls == 1, "Run preflight assigns output groups once and saves parent once");
        Check(fake.Groups.ContainsKey("[O]-TopChords") && fake.Groups.ContainsKey(OutputGroupNaming.DeflectionMonitor) &&
              fake.Groups.ContainsKey(OutputGroupNaming.FoundationLeft) && fake.Groups.ContainsKey(OutputGroupNaming.FoundationRight) &&
              fake.Groups.ContainsKey(OutputGroupNaming.CableMonitor),
            "Run preflight materializes every configured output group");

        fake.Active = model with { Path = @"C:\Models\Study\Child.sdb" };
        try
        {
            new OutputGroupPreparationService(fake).PrepareParentAsync(state).GetAwaiter().GetResult();
            throw new Exception("FAIL: Child model was accepted as parent");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("parent baseline model"))
        {
            Console.WriteLine("PASS: Output-group write is blocked when active SAP is not the parent");
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    private sealed class FakeSap(ModelInfo model) : ISapGateway
    {
        public ModelInfo Active { get; set; } = model;
        public int ReplaceCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public Dictionary<string, SapGroupSnapshot> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<ModelInfo> ConnectAsync() => Task.FromResult(Active);
        public Task<ModelInfo> OpenModelAsync(string modelPath) => throw new NotSupportedException();
        public Task<ModelInfo> SaveAsAsync(string targetPath, ModelInfo expected) => throw new NotSupportedException();
        public Task SaveAsync(ModelInfo expected) { SaveCalls++; return Task.CompletedTask; }
        public Task RunAnalysisAsync(ModelInfo expected) => throw new NotSupportedException();
        public Task<ModelInfo> GetCurrentModelInfoAsync() => Task.FromResult(Active);
        public Task<ModelSnapshot> ReadAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<PointLoad>> ReadPointLoadsAsync(ModelInfo expected) => Task.FromResult<IReadOnlyList<PointLoad>>([]);
        public Task<Position> GetPointPositionAsync(string pointName, ModelInfo expected) => throw new NotSupportedException();
        public Task<IReadOnlyList<PointLoad>> GetPointLoadsAsync(string pointName, ModelInfo expected) => Task.FromResult<IReadOnlyList<PointLoad>>([]);
        public Task<IReadOnlyList<CableTargetForce>> GetCableTargetForcesAsync(string cableName, ModelInfo expected) => Task.FromResult<IReadOnlyList<CableTargetForce>>([]);
        public Task<IReadOnlyList<CableSection>> GetCableSectionsAsync(IReadOnlyList<string> cableNames, ModelInfo expected) => Task.FromResult<IReadOnlyList<CableSection>>([]);
        public Task<IReadOnlyList<COT_VCF.Domain.Results.JointDisplacement>> GetJointDisplacementsAsync(IReadOnlyList<string> pointNames, string outputCase, bool isCombo, ModelInfo expected) => throw new NotSupportedException();
        public Task<IReadOnlyList<COT_VCF.Domain.Results.CableForceRead>> GetCableForcesAsync(IReadOnlyList<string> cableNames, string outputCase, bool isCombo, ModelInfo expected) => throw new NotSupportedException();
        public Task<IReadOnlyList<COT_VCF.Domain.Results.FrameForceRead>> GetFrameForcesAsync(IReadOnlyList<string> frameNames, string outputCase, bool isCombo, ModelInfo expected) => throw new NotSupportedException();
        public Task<IReadOnlyList<ForceSource>> ReadForcesAsync(IReadOnlyList<StructuralMember> members, ModelInfo expected) => Task.FromResult<IReadOnlyList<ForceSource>>([]);
        public Task ApplyLoadsAsync(IReadOnlyList<PointLoad> loads, ModelInfo expected) => throw new NotSupportedException();
        public Task MovePointsAsync(GeometryModel expectedGeometry, GeometryModel proposed, ModelInfo expected) => throw new NotSupportedException();
        public Task SetCableTargetForceAsync(string cableName, string loadPattern, double targetForce, ModelInfo expected) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetLoadCaseNamesAsync(ModelInfo expected) => Task.FromResult<IReadOnlyList<string>>(["CASE1"]);
        public Task<IReadOnlyList<SapGroupSnapshot>> ReadOutputGroupsAsync(IReadOnlyList<string> groupNames, ModelInfo expected) =>
            Task.FromResult<IReadOnlyList<SapGroupSnapshot>>(groupNames.Select(name => Groups.TryGetValue(name, out var group)
                ? group : new SapGroupSnapshot(name, false, [], [], [])).ToArray());
        public Task ReplaceOutputGroupsAsync(IReadOnlyList<OutputSapGroupDefinition> groups, ModelInfo expected)
        {
            ReplaceCalls++;
            foreach (var group in groups)
            {
                Groups[group.Name] = group.ObjectKind switch
                {
                    OutputGroupObjectKind.Point => new(group.Name, true, group.ObjectNames.ToArray(), [], []),
                    OutputGroupObjectKind.Frame => new(group.Name, true, [], group.ObjectNames.ToArray(), []),
                    OutputGroupObjectKind.Cable => new(group.Name, true, [], [], group.ObjectNames.ToArray()),
                    _ => throw new InvalidOperationException()
                };
            }
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }
}
