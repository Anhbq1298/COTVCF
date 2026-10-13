using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Workers;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.SAP2000;
using System.IO;

internal static class LiveStudyCheck
{
    public static async Task RunAsync(string sourceBaseline, bool matrix = false)
    {
        var source = Path.GetFullPath(sourceBaseline);
        if (!File.Exists(source)) throw new FileNotFoundException("Live-study baseline was not found.", source);

        var root = Path.GetFullPath(Path.Combine(".work", "live-study-verification", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(root);
        var baselinePath = Path.Combine(root, Path.GetFileName(source));
        File.Copy(source, baselinePath, false);

        ModelSnapshot model;
        IReadOnlyList<PointLoad> baselineLoads;
        await using (var reader = new SapInstanceHost(1))
        {
            var info = await reader.StartAsync(baselinePath, CancellationToken.None);
            model = await reader.Gateway.ReadAsync();
            baselineLoads = await reader.Gateway.ReadPointLoadsAsync(info);
        }

        var top = RequiredPoints(model.Geometry, "TTrusPt");
        var middle = RequiredPoints(model.Geometry, "MTrusPt");
        var bottom = RequiredPoints(model.Geometry, "BTrusPt");
        var cables = model.Geometry.Members.Where(member => member.Kind == MemberKind.Cable)
            .OrderBy(member => member.Name, StringComparer.Ordinal).ToArray();
        if (cables.Length == 0) throw new InvalidOperationException("The live baseline contains no cable objects.");
        var targetPattern = model.LoadPatterns.FirstOrDefault(pattern =>
                                pattern.Equals("PRETENSION", StringComparison.OrdinalIgnoreCase))
                            ?? model.LoadPatterns.FirstOrDefault()
                            ?? throw new InvalidOperationException("The live baseline contains no load pattern for target-force verification.");
        var loadingPatterns = model.LoadPatterns.Take(2).ToArray();

        var state = new ApplicationState
        {
            Connection = "Connected",
            Model = model.Info,
            Geometry = model.Geometry,
            Baseline = model.Geometry,
            SessionBaselineModel = model.Info,
            SessionBaseline = model.Geometry,
            SessionBaselinePointLoads = baselineLoads,
            BaselineCaptured = true,
            TributaryWidthB = 12,
            NumberOfStories = 10
        };
        foreach (var pattern in model.LoadPatterns) state.LoadPatterns.Add(pattern);
        foreach (var pointName in top)
        {
            var point = model.Geometry.Points[pointName];
            state.AppliedPoints.Add(new AppliedPoint
            {
                PointName = pointName,
                X = point.Position.X,
                Y = point.Position.Y,
                Z = point.Position.Z,
                Order = state.AppliedPoints.Count + 1,
                IsSelected = true
            });
        }
        state.LoadDefinitions.Clear();
        for (var index = 0; index < loadingPatterns.Length; index++)
            state.LoadDefinitions.Add(new LoadDefinition
            {
                LoadName = loadingPatterns[index],
                ApplyIncrementStep = matrix && index == loadingPatterns.Length - 1,
                Qz = index == 0 ? 5 : 7,
                MinQz = 7,
                MaxQz = 8,
                IncrementStep = 1
            });

        var baselineHeight = top.Average(name => model.Geometry.Points[name].Position.Z) -
                             bottom.Average(name => model.Geometry.Points[name].Position.Z);
        state.Truss.TopPoints = top;
        state.Truss.MiddlePoints = middle;
        state.Truss.BottomPoints = bottom;
        state.Truss.Reference = ReferenceChord.Middle;
        state.Truss.BaselineHeight = baselineHeight;
        state.Truss.MinimumHeight = baselineHeight + 2;
        state.Truss.MaximumHeight = baselineHeight + (matrix ? 4 : 2);
        state.Truss.Increment = 2;
        state.Truss.CurrentHeight = baselineHeight + 2;
        for (var cableIndex = 0; cableIndex < cables.Length; cableIndex++)
        {
            var cable = cables[cableIndex];
            state.Cables.Add(new CablePretensionConfig
            {
                CableName = cable.Name,
                LoadPattern = targetPattern,
                ApplyRange = matrix && cableIndex == 0,
                TargetForce = 1000,
                MinForce = 1000,
                MaxForce = 1100,
                StepForce = 100
            });
        }
        state.Study.Mode = IterationStudyMode.Combined;

        var composition = new StudyCompositionService().Compose(state);
        var expectedCount = matrix ? 8 : 1;
        if (composition.Iterations.Count != expectedCount)
            throw new InvalidOperationException($"Expected {expectedCount} live verification candidate(s); composed {composition.Iterations.Count}.");
        var snapshot = new StudyExecutionSnapshotFactory().Create(state, composition, matrix ? 2 : 1);
        var result = await new StudyExecutionCoordinator(new SapInstanceHostFactory()).BuildAsync(
            snapshot, new Progress<StudyExecutionProgress>(progress =>
                Console.WriteLine($"LIVE: worker={progress.WorkerId} iteration={progress.IterationId} stage={progress.Stage} status={progress.IterationStatus}")),
            CancellationToken.None);
        if (result.Completed != expectedCount || result.Failed != 0)
            throw new InvalidOperationException($"Live study build did not complete: completed={result.Completed}, failed={result.Failed}.");

        var jobs = snapshot.Jobs.Where(candidate => candidate.RunIterate).ToArray();
        await using var independentReader = new SapInstanceHost(2);
        await independentReader.StartAsync(jobs[0].ModelFullPath, CancellationToken.None);
        var verifier = new IterationCandidateVerifier();
        var geometryCount = 0;
        var pointLoadCount = 0;
        var cableTargetCount = 0;
        foreach (var job in jobs)
        {
            var child = await independentReader.Gateway.OpenModelAsync(job.ModelFullPath);
            var expectedGeometry = new TrussGeometryService().BuildCandidateGeometry(
                snapshot.BaselineGeometry, snapshot.TopPoints, snapshot.MiddlePoints, snapshot.BottomPoints, job.Height);
            var verification = await verifier.VerifyAsync(
                independentReader.Gateway, snapshot, job, expectedGeometry, child, CancellationToken.None);
            geometryCount += verification.GeometryPointsVerified;
            pointLoadCount += verification.PointLoadsVerified;
            cableTargetCount += verification.CableTargetForcesVerified;
            Console.WriteLine($"LIVE VERIFIED FILE: {job.ModelFullPath}");
        }
        Console.WriteLine($"LIVE VERIFIED COUNTS: files={jobs.Length}, geometry={geometryCount}, " +
                          $"pointLoads={pointLoadCount}, cableTargets={cableTargetCount}");
        Console.WriteLine($"LIVE TARGET PATTERN: {targetPattern}");
    }

    private static string[] RequiredPoints(GeometryModel geometry, string prefix)
    {
        var names = geometry.Points.Keys.Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (names.Length == 0) throw new InvalidOperationException($"No points matching {prefix} were found in the live baseline.");
        return names;
    }
}
