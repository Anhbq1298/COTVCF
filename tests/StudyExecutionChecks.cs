using System.Collections.Concurrent;
using COT_VCF.Application;
using COT_VCF.Application.Workers;
using COT_VCF.Domain;
using System.IO;

internal static class StudyExecutionChecks
{
    public static void Run()
    {
        PathServiceIsPureAndDeterministic();
        SnapshotResolvesCompleteCandidates();
        TwoWorkersAreParallelAndIndividuallySequential();
        StartupFailureBlocksAllDispatch();
        ExistingOutputBlocksBeforeWorkerStartup();
        FaultedWorkerIsRemovedWhileHealthyWorkerContinues();
        CancellationStopsAtStageBoundaries();
    }

    private static void PathServiceIsPureAndDeterministic()
    {
        var root = NewRoot();
        try
        {
            var baseline = Path.Combine(root, "COT_VCF.sdb");
            Directory.CreateDirectory(root);
            File.WriteAllText(baseline, "baseline");
            var paths = new StudyPathService();
            var id = "L02_H03_P04";
            Check(paths.GetStudyRoot(baseline) == Path.Combine(root, "Study") &&
                  paths.GetIterationDirectory(baseline, id) == Path.Combine(root, "Study", id) &&
                  paths.GetIterationModelPath(baseline, id) == Path.Combine(root, "Study", id, $"COT_VCF_{id}.sdb"),
                "Study path service derives root, isolated folder, and child model name");
            Check(!Directory.Exists(Path.Combine(root, "Study")), "Study path preview does not create folders");
        }
        finally { DeleteRoot(root); }
    }

    private static void SnapshotResolvesCompleteCandidates()
    {
        var (root, state, snapshot) = CreateSnapshot(1, 1);
        try
        {
            var job = snapshot.Jobs.Single(candidate => candidate.RunIterate);
            Check(job.IterationId.StartsWith("L01_H", StringComparison.Ordinal) &&
                  job.Loading.Loads.Count == 2 && job.Loading.Loads.All(load => load.LoadPattern == "LIVE") &&
                  job.Pretension.Assignments is [{ CableName: "Cable-A", LoadPattern: "PRETENSION" }],
                "Frozen job resolves loading, height, and pretension assignments");
            Check(snapshot.BaselineGeometry.Points["top"].Position.Z == 15 &&
                  !ReferenceEquals(snapshot.BaselineGeometry.Points, state.Baseline.Points),
                "Execution snapshot owns an immutable baseline geometry copy");
        }
        finally { DeleteRoot(root); }
    }

    private static void TwoWorkersAreParallelAndIndividuallySequential()
    {
        var (root, _, snapshot) = CreateSnapshot(4, 2);
        try
        {
            var factory = new FakeHostFactory();
            var assignments = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var progress = new InlineProgress<StudyExecutionProgress>(value =>
            {
                if (value.WorkerId > 0 && value.IterationId is { } id && value.IterationStatus == StudyIterationStatus.PreparingFolder)
                    assignments.TryAdd(id, value.WorkerId);
            });
            var result = new StudyExecutionCoordinator(factory).BuildAsync(snapshot, progress, CancellationToken.None).GetAwaiter().GetResult();

            Check(result.Completed == 4 && result.Failed == 0 && result.PeakConcurrent == 2,
                "Two requested workers build up to two candidate models concurrently");
            Check(factory.Gateways.Count == 2 && factory.Gateways.All(gateway => gateway.MaxConcurrentCalls == 1),
                "Each SAP worker gateway remains a strictly sequential lane");
            Check(snapshot.Jobs.Where(job => job.RunIterate).All(job => File.Exists(job.ModelFullPath)),
                "Every selected candidate has an isolated child .sdb file");
            Check(factory.Gateways.SelectMany(gateway => gateway.SaveAsSources).All(path => SamePath(path, snapshot.BaselineModelPath)),
                "Every child SaveAs originates from the immutable baseline");
            Check(factory.Gateways.Sum(gateway => gateway.AppliedPointLoads.Count) ==
                  snapshot.Jobs.Where(job => job.RunIterate).Sum(job => job.Loading.Loads.Count) &&
                  factory.Gateways.Sum(gateway => gateway.AppliedTargetForces.Count) ==
                  snapshot.Jobs.Where(job => job.RunIterate).Sum(job => job.Pretension.Assignments.Count),
                "Every worker receives all selected-joint loads and cable target-force assignments");

            var ordered = snapshot.Jobs.Where(job => job.RunIterate).OrderBy(job => job.RunIndex).ToArray();
            Check(assignments[ordered[1].IterationId] == assignments[ordered[2].IterationId],
                "The next available worker receives the next job instead of fixed round-robin assignment");
        }
        finally { DeleteRoot(root); }
    }

    private static void StartupFailureBlocksAllDispatch()
    {
        var (root, _, snapshot) = CreateSnapshot(3, 2);
        try
        {
            var factory = new FakeHostFactory(failStartId: 2);
            Throws(() => new StudyExecutionCoordinator(factory)
                .BuildAsync(snapshot, new InlineProgress<StudyExecutionProgress>(_ => { }), CancellationToken.None)
                .GetAwaiter().GetResult());
            Check(factory.Gateways.Sum(gateway => gateway.SaveAsSources.Count) == 0,
                "Requested workers must all start before any candidate is dispatched");
        }
        finally { DeleteRoot(root); }
    }

    private static void ExistingOutputBlocksBeforeWorkerStartup()
    {
        var (root, _, snapshot) = CreateSnapshot(1, 1);
        try
        {
            var job = snapshot.Jobs.Single(candidate => candidate.RunIterate);
            Directory.CreateDirectory(job.IterationDirectory);
            File.WriteAllText(job.ModelFullPath, "existing");
            var factory = new FakeHostFactory();
            Throws(() => new StudyExecutionCoordinator(factory)
                .BuildAsync(snapshot, new InlineProgress<StudyExecutionProgress>(_ => { }), CancellationToken.None)
                .GetAwaiter().GetResult());
            Check(factory.CreateCount == 0 && File.ReadAllText(job.ModelFullPath) == "existing",
                "Existing candidate output blocks before worker startup and is not overwritten");
        }
        finally { DeleteRoot(root); }
    }

    private static void CancellationStopsAtStageBoundaries()
    {
        var (root, _, snapshot) = CreateSnapshot(6, 2);
        try
        {
            var factory = new FakeHostFactory();
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress<StudyExecutionProgress>(value =>
            {
                if (value.Stage == StudyWorkerStage.ApplyingGeometry) cancellation.Cancel();
            });
            var result = new StudyExecutionCoordinator(factory).BuildAsync(snapshot, progress, cancellation.Token).GetAwaiter().GetResult();
            Check(result.Completed == 0 && result.Cancelled == 6 && result.Failed == 0,
                "Cancellation marks active and queued jobs cancelled at safe stage boundaries");
            Check(factory.Gateways.All(gateway => SamePath(gateway.Current.Path, snapshot.BaselineModelPath)),
                "Cancelled workers return to the immutable baseline before shutdown");
        }
        finally { DeleteRoot(root); }
    }

    private static void FaultedWorkerIsRemovedWhileHealthyWorkerContinues()
    {
        var (root, _, snapshot) = CreateSnapshot(5, 2);
        try
        {
            var factory = new FakeHostFactory(failRuntimeId: 1);
            var result = new StudyExecutionCoordinator(factory)
                .BuildAsync(snapshot, new InlineProgress<StudyExecutionProgress>(_ => { }), CancellationToken.None)
                .GetAwaiter().GetResult();
            Check(result.Failed == 1 && result.Completed == 4 &&
                  factory.Gateways.Single(gateway => gateway.WorkerId == 1).Crashed,
                "A faulted SAP worker is removed while the healthy worker completes the remaining queue");
        }
        finally { DeleteRoot(root); }
    }

    private static (string Root, ApplicationState State, StudyExecutionSnapshot Snapshot) CreateSnapshot(int selectedCount, int workers)
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        var baselinePath = Path.Combine(root, "COT_VCF.sdb");
        File.WriteAllText(baselinePath, "baseline");
        var state = StudyComposerChecks.CreateState();
        var baseline = state.SessionBaselineModel! with { Path = baselinePath };
        state.Model = baseline;
        state.SessionBaselineModel = baseline;
        state.Study.Mode = IterationStudyMode.FixHeight;
        state.Study.FixedHeightCandidateId = "H03";
        state.Study.OutputDirectory = Path.Combine(root, "Study");
        var composition = new StudyCompositionService().Compose(state);
        foreach (var row in composition.Iterations.Skip(selectedCount)) row.RunIterate = false;
        return (root, state, new StudyExecutionSnapshotFactory().Create(state, composition, workers));
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "COTVCF-study-checks-" + Guid.NewGuid().ToString("N"));
    private static void DeleteRoot(string root)
    {
        var full = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("COTVCF-study-checks-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to delete an unexpected test directory.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static void Throws(Action action)
    {
        try { action(); }
        catch (Exception) { return; }
        throw new Exception("Expected the operation to fail.");
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FakeHostFactory(int failStartId = 0, int failRuntimeId = 0) : ISapInstanceHostFactory
    {
        public ConcurrentBag<FakeGateway> Gateways { get; } = [];
        public int CreateCount;
        public ISapInstanceHost Create(int instanceId)
        {
            Interlocked.Increment(ref CreateCount);
            var gateway = new FakeGateway(instanceId, failRuntimeId == instanceId);
            Gateways.Add(gateway);
            return new FakeHost(instanceId, gateway, failStartId == instanceId);
        }
    }

    private sealed class FakeHost(int instanceId, FakeGateway gateway, bool failStart) : ISapInstanceHost
    {
        public int InstanceId => instanceId;
        public ISapGateway Gateway => gateway;
        public Task<ModelInfo> StartAsync(string baselineModelPath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (failStart) throw new InvalidOperationException("SAP2000 license is unavailable.");
            return gateway.OpenModelAsync(baselineModelPath);
        }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeGateway(int workerId, bool failRuntime = false) : ISapGateway
    {
        private int activeCalls;
        private int maxConcurrentCalls;
        public ModelInfo Current { get; private set; } = new("", "26", 6, "kN", "m", false);
        public ConcurrentBag<string> SaveAsSources { get; } = [];
        public ConcurrentBag<PointLoad> AppliedPointLoads { get; } = [];
        public ConcurrentBag<PretensionAssignment> AppliedTargetForces { get; } = [];
        public int MaxConcurrentCalls => maxConcurrentCalls;
        public int WorkerId => workerId;
        public bool Crashed { get; private set; }

        public Task<ModelInfo> ConnectAsync() => throw new NotSupportedException();
        public Task<ModelSnapshot> ReadAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<PointLoad>> ReadPointLoadsAsync(ModelInfo expected) => Task.FromResult<IReadOnlyList<PointLoad>>([]);
        public Task<IReadOnlyList<ForceSource>> ReadForcesAsync(IReadOnlyList<StructuralMember> members, ModelInfo expected) => Task.FromResult<IReadOnlyList<ForceSource>>([]);
        public Task<IReadOnlyList<string>> GetLoadCaseNamesAsync(ModelInfo expected) => Task.FromResult<IReadOnlyList<string>>([]);

        public async Task<ModelInfo> OpenModelAsync(string modelPath) => await Tracked(async () =>
        {
            if (Crashed) throw new InvalidOperationException("SAP2000 process crashed.");
            await Task.Delay(2);
            Current = Current with { Path = Path.GetFullPath(modelPath) };
            return Current;
        });

        public async Task<ModelInfo> SaveAsAsync(string targetPath, ModelInfo expected) => await Tracked(async () =>
        {
            if (!SamePath(Current.Path, expected.Path)) throw new InvalidOperationException("SaveAs source was not the active baseline.");
            SaveAsSources.Add(Current.Path);
            await Task.Delay(2);
            File.Copy(Current.Path, targetPath, false);
            Current = expected with { Path = Path.GetFullPath(targetPath), IsLocked = false };
            return Current;
        });

        public async Task MovePointsAsync(GeometryModel expectedGeometry, GeometryModel proposed, ModelInfo expected) => await Tracked(async () =>
        {
            Verify(expected);
            if (failRuntime && !Crashed)
            {
                Crashed = true;
                throw new InvalidOperationException("SAP2000 process crashed.");
            }
            await Task.Delay(Current.Path.Contains("_P01.sdb", StringComparison.Ordinal) ? 100 : 5);
        });

        public async Task ApplyLoadsAsync(IReadOnlyList<PointLoad> loads, ModelInfo expected) => await Tracked(async () =>
        {
            Verify(expected);
            foreach (var load in loads) AppliedPointLoads.Add(load);
            await Task.Delay(3);
        });

        public async Task SetCableTargetForceAsync(string cableName, string loadPattern, double targetForce, ModelInfo expected) => await Tracked(async () =>
        {
            Verify(expected);
            AppliedTargetForces.Add(new(cableName, loadPattern, targetForce));
            await Task.Delay(3);
        });

        public async Task SaveAsync(ModelInfo expected) => await Tracked(async () =>
        {
            Verify(expected);
            await Task.Delay(2);
        });

        public Task<ModelInfo> GetCurrentModelInfoAsync() => Task.FromResult(Current);

        private void Verify(ModelInfo expected)
        {
            if (!SamePath(Current.Path, expected.Path))
                throw new InvalidOperationException($"Worker {workerId} active model identity changed.");
        }

        private async Task Tracked(Func<Task> action) =>
            await Tracked(async () => { await action(); return true; });
        private async Task<T> Tracked<T>(Func<Task<T>> action)
        {
            var active = Interlocked.Increment(ref activeCalls);
            while (true)
            {
                var current = Volatile.Read(ref maxConcurrentCalls);
                if (active <= current || Interlocked.CompareExchange(ref maxConcurrentCalls, active, current) == current) break;
            }
            try { return await action(); }
            finally { Interlocked.Decrement(ref activeCalls); }
        }

        public void Dispose() { }
    }
}
