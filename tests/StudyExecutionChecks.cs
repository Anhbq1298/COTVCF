using System.Collections.Concurrent;
using COT_VCF.Application;
using COT_VCF.Application.Workers;
using COT_VCF.Domain;
using System.IO;
using COT_VCF.Application.Results;
using COT_VCF.Domain.Results;

internal static class StudyExecutionChecks
{
    public static void Run()
    {
        PathServiceIsPureAndDeterministic();
        SnapshotResolvesCompleteCandidates();
        OneWorkerPersistsAndVerifiesOneCandidate();
        ArtifactFailureIsReportedWithoutStalePaths();
        ResumeAndRetryUseLatestCompatibleRun();
        ExplicitRebuildArchivesInsteadOfOverwriting();
        OneWorkerDoesNotAccumulateOrLeakCandidateState();
        FixedModesAndCombinedPhysicallyApplyResolvedCandidates();
        ConnectedBaselineCountsAsWorkerOne();
        TwoWorkersAreParallelAndIndividuallySequential();
        AnalysisFailureNeverBecomesReady();
        VerificationFailureNeverBecomesReady();
        PretensionFailureReturnsToBaselineAndContinues();
        StartupFailureBlocksAllDispatch();
        ExistingChildModelIsReusedNotRebuilt();
        FaultedWorkerIsRemovedWhileHealthyWorkerContinues();
        CancellationStopsAtStageBoundaries();
    }

    private static void ConnectedBaselineCountsAsWorkerOne()
    {
        var (root, _, snapshot) = CreateSnapshot(3, 3);
        try
        {
            var connected = new FakeGateway(1);
            var additionalFactory = new FakeHostFactory();
            var factory = new ConnectedSapInstanceHostFactory(connected, additionalFactory);
            var result = new StudyExecutionCoordinator(factory).BuildAsync(snapshot,
                new InlineProgress<StudyExecutionProgress>(_ => { }), CancellationToken.None).GetAwaiter().GetResult();

            Check(result.Completed == 3 && result.WorkerCount == 3 && additionalFactory.CreateCount == 2,
                "Three selected instances use the connected baseline plus exactly two additional SAP2000 hosts");
            Check(additionalFactory.Gateways.All(gateway => gateway.WorkerId is 2 or 3),
                "Only worker IDs 2 and 3 create additional SAP2000 hosts");
            Check(connected.DisposeCount == 0,
                "Stopping the study pool does not dispose or close the borrowed connected SAP2000 instance");
        }
        finally { DeleteRoot(root); }
    }

    private static void OneWorkerPersistsAndVerifiesOneCandidate()
    {
        var (root, _, snapshot) = CreateSnapshot(1, 1);
        try
        {
            var factory = new FakeHostFactory();
            var stages = new ConcurrentBag<StudyIterationStatus?>();
            var candidateResults = new ConcurrentBag<COT_VCF.Domain.Results.CandidateResultDto>();
            var result = new StudyExecutionCoordinator(factory).BuildAsync(snapshot,
                new InlineProgress<StudyExecutionProgress>(progress => stages.Add(progress.IterationStatus)),
                CancellationToken.None, candidateResults.Add).GetAwaiter().GetResult();
            var job = snapshot.Jobs.Single(candidate => candidate.RunIterate);
            var gateway = factory.Gateways.Single();
            Check(result.Completed == 1 && result.Failed == 0 &&
                  result.Artifacts.State == StudyArtifactState.Ready &&
                  File.Exists(result.Artifacts.WorkbookPath) &&
                  stages.Contains(StudyIterationStatus.RunningAnalysis) &&
                  stages.Contains(StudyIterationStatus.Verifying) &&
                  stages.Contains(StudyIterationStatus.ExtractingResults) && File.Exists(job.ModelFullPath),
                "One worker marks one child Ready only after analysis, verification and result extraction");

            var cr = candidateResults.Single();
            Check(cr.IterationKey == job.IterationId &&
                  cr.ExtractionStatus == COT_VCF.Domain.Results.CandidateExtractionStatus.ExtractedOk &&
                  cr.Groups.Single().Points.Count == 3 &&
                  cr.Groups[0].Points.All(pt => pt.Status == COT_VCF.Domain.Results.ResultValueStatus.Valid) &&
                  cr.Groups[0].Points.Select(pt => pt.Sequence).SequenceEqual([1, 2, 3]),
                "Deflection extraction produces one sorted group of Valid monitored-point Uz per candidate");

            var diagHtmlPath = Path.Combine(job.IterationDirectory, $"Deflection_Diagnostic_{job.IterationId}.html");
            Check(File.Exists(diagHtmlPath), "A candidate diagnostic HTML is written into the candidate folder");
            var diagHtml = File.ReadAllText(diagHtmlPath);
            Check(diagHtml.Contains("Candidate Diagnostic Report") && diagHtml.Contains("QA/QC") &&
                  diagHtml.Contains("Monitored Deflection") && diagHtml.Contains(">top<"),
                "Diagnostic HTML states its QA/QC purpose and lists the extracted monitored points");

            var studyFolder = Path.GetDirectoryName(job.IterationDirectory)!;
            var xlsx = Path.Combine(studyFolder, "Deflection_Results_Source.xlsx");
            var reportHtml = Path.Combine(studyFolder, "Deflection_Report.html");
            Check(File.Exists(xlsx) && File.Exists(reportHtml),
                "Study run writes the consolidated Deflection workbook and the final HTML report to the Study folder");
            var manifest = StudyRunJournal.TryRead(studyFolder);
            Check(result.Run is not null && File.Exists(result.Run.ManifestPath) && manifest is not null &&
                  manifest.RunId == result.Run.RunId && manifest.State == StudyRunState.Completed &&
                  manifest.Candidates.Single(candidate => candidate.Selected).Status == StudyIterationStatus.Ready &&
                  StudyRunJournal.ArtifactsAreIntact(manifest),
                "Study run atomically records candidate status, provenance hashes, and verifiable artifacts");
            var readBack = COT_VCF.Application.Results.ConsolidatedResultWriter.Read(xlsx);
            Check(readBack.Count == 1 && readBack[0].IterationKey == job.IterationId &&
                  readBack[0].Groups.Single().Points.Count == 3 &&
                  readBack[0].Groups[0].Points.All(pt => pt.UzMm.HasValue),
                "Consolidated workbook round-trips the candidate and its monitored-point Uz rows");
            var finalHtml = File.ReadAllText(reportHtml);
            Check(finalHtml.Contains("id=\"report-data\"") && finalHtml.Contains("Deflection Results Viewer") &&
                  finalHtml.Contains(job.IterationId) && finalHtml.Contains("Result Library") &&
                  !finalHtml.Contains("http://") && !finalHtml.Contains("https://") && !finalHtml.Contains("cdn."),
                "Final HTML embeds the report JSON, has the Result Library, and references nothing external");
            Check(gateway.AnalysisAttempts.Contains(Path.GetFullPath(job.ModelFullPath)) &&
                  gateway.SavedAfterAnalysis.Contains(Path.GetFullPath(job.ModelFullPath)),
                "RunAnalysis persists the analyzed child; the worker never re-saves or reopens it");
            Check(SavedCandidateMatches(gateway, snapshot, job),
                "Reloaded single child physically contains its resolved height, loading, and pretension values");
            var diagnostic = File.ReadAllText(Path.Combine(job.IterationDirectory, "build.log"));
            Check(diagnostic.Contains("ANALYZED", StringComparison.Ordinal) &&
                  diagnostic.Contains("VERIFIED", StringComparison.Ordinal),
                "Per-candidate diagnostics record analysis and the verified L/H/P transaction");
        }
        finally { DeleteRoot(root); }
    }

    private static void ArtifactFailureIsReportedWithoutStalePaths()
    {
        var (root, _, snapshot) = CreateSnapshot(1, 1);
        try
        {
            var coordinator = new StudyExecutionCoordinator(new FakeHostFactory(), new FailingArtifactPublisher());
            var result = coordinator.BuildAsync(snapshot,
                new InlineProgress<StudyExecutionProgress>(_ => { }), CancellationToken.None).GetAwaiter().GetResult();
            Check(result.Completed == 1 && result.Artifacts.State == StudyArtifactState.Failed &&
                  result.Artifacts.ErrorMessage!.Contains("simulated artifact failure", StringComparison.Ordinal) &&
                  coordinator.LastDeflectionReportPath is null && coordinator.LastCableForceReportPath is null,
                "artifact failure is explicit and never reuses a prior report path");
            var studyFolder = Path.GetDirectoryName(snapshot.Jobs.Single(job => job.RunIterate).IterationDirectory)!;
            var manifest = StudyRunJournal.TryRead(studyFolder);
            Check(result.Run is not null && manifest?.State == StudyRunState.CompletedWithErrors &&
                  manifest.Artifacts.Count == 0,
                "artifact failure is preserved in the run manifest without publishing stale artifact entries");
        }
        finally { DeleteRoot(root); }
    }

    private static void ResumeAndRetryUseLatestCompatibleRun()
    {
        var (root, _, snapshot) = CreateSnapshot(3, 1);
        try
        {
            var jobs = snapshot.Jobs.Where(job => job.RunIterate).ToArray();
            var original = StudyRunJournal.Start(snapshot);
            original.Report(new StudyExecutionProgress { IterationId = jobs[0].IterationId,
                Stage = StudyWorkerStage.ApplyingGeometry, IterationStatus = StudyIterationStatus.ApplyingGeometry });
            original.Report(new StudyExecutionProgress { IterationId = jobs[1].IterationId,
                Stage = StudyWorkerStage.Waiting, IterationStatus = StudyIterationStatus.Failed, Message = "test failure" });
            original.Report(new StudyExecutionProgress { IterationId = jobs[2].IterationId,
                Stage = StudyWorkerStage.Completed, IterationStatus = StudyIterationStatus.Ready });

            // A newer, incompatible latest manifest must not hide the compatible run retained in RunHistory.
            _ = StudyRunJournal.Start(snapshot with { DeflectionOutputCase = "A_DIFFERENT_CASE" });
            var assessment = new StudyResumeService().Assess(snapshot);
            var resumed = new StudyResumeService().Select(snapshot, assessment, StudyRunIntent.ResumeInterrupted);
            var retried = new StudyResumeService().Select(snapshot, assessment, StudyRunIntent.RetryFailed);
            Check(assessment.Compatible && assessment.Manifest?.RunId == original.RunId &&
                  assessment.ResumeIds.SetEquals([jobs[0].IterationId]) &&
                  assessment.RetryIds.SetEquals([jobs[1].IterationId]),
                "Resume discovery finds the latest compatible history and classifies unfinished, failed, and verified candidates");
            Check(resumed.Jobs.Count(job => job.RunIterate) == 1 &&
                  resumed.Jobs.Single(job => job.RunIterate).IterationId == jobs[0].IterationId &&
                  retried.Jobs.Count(job => job.RunIterate) == 1 &&
                  retried.Jobs.Single(job => job.RunIterate).IterationId == jobs[1].IterationId,
                "Resume and Retry dispatch only their eligible candidate subsets");
        }
        finally { DeleteRoot(root); }
    }

    private static void ExplicitRebuildArchivesInsteadOfOverwriting()
    {
        var (root, _, snapshot) = CreateSnapshot(1, 1);
        try
        {
            var selected = snapshot.Jobs.Single(job => job.RunIterate);
            var excluded = snapshot.Jobs.First(job => !job.RunIterate);
            Directory.CreateDirectory(selected.IterationDirectory);
            Directory.CreateDirectory(excluded.IterationDirectory);
            File.WriteAllText(selected.ModelFullPath, "selected child bytes");
            File.WriteAllText(Path.Combine(excluded.IterationDirectory, "keep.txt"), "excluded");
            var archived = new StudyRebuildArchive().Archive(snapshot);
            var archivedModel = Path.Combine(archived.ArchiveRoot, selected.IterationId, selected.ModelName);
            Check(!Directory.Exists(selected.IterationDirectory) && File.Exists(archivedModel) &&
                  File.ReadAllText(archivedModel) == "selected child bytes" &&
                  File.Exists(Path.Combine(archived.ArchiveRoot, "RebuildArchive.json")),
                "Explicit rebuild moves selected candidate data into a recoverable archive before rebuilding");
            Check(File.Exists(Path.Combine(excluded.IterationDirectory, "keep.txt")),
                "Explicit rebuild never moves or overwrites an excluded candidate folder");
        }
        finally { DeleteRoot(root); }
    }

    private static void AnalysisFailureNeverBecomesReady()
    {
        var (root, _, snapshot) = CreateSnapshot(2, 1);
        try
        {
            var failedId = snapshot.Jobs.First(job => job.RunIterate).IterationId;
            var factory = new FakeHostFactory(failAnalysisIterationId: failedId);
            var result = new StudyExecutionCoordinator(factory).BuildAsync(snapshot,
                new InlineProgress<StudyExecutionProgress>(_ => { }), CancellationToken.None).GetAwaiter().GetResult();
            var gateway = factory.Gateways.Single();
            Check(result.Failed == 1 && result.Completed == 1 && gateway.AnalysisAttempts.Count == 2,
                "An analysis failure leaves that candidate Failed while the recovered worker analyzes the next candidate");
            Check(gateway.SavedAfterAnalysis.Count == 1,
                "A failed analysis is never saved or reported as a Ready analyzed child");
        }
        finally { DeleteRoot(root); }
    }

    private static void OneWorkerDoesNotAccumulateOrLeakCandidateState()
    {
        var (root, _, snapshot) = CreateSnapshot(
            IterationStudyMode.Combined,
            1,
            row => row.IterationId is "L01_H01_P01" or "L01_H02_P02" or "L02_H01_P01");
        try
        {
            var factory = new FakeHostFactory();
            var result = new StudyExecutionCoordinator(factory).BuildAsync(snapshot,
                new InlineProgress<StudyExecutionProgress>(_ => { }), CancellationToken.None).GetAwaiter().GetResult();
            var gateway = factory.Gateways.Single();
            var jobs = snapshot.Jobs.Where(job => job.RunIterate).ToArray();
            Check(result.Completed == 3 && jobs.All(job => SavedCandidateMatches(gateway, snapshot, job)),
                "One worker reloads the baseline and persists exact values for multiple different L/H/P candidates");
            var firstHeight = gateway.SavedPosition(jobs[0].ModelFullPath, "bot").Z;
            var secondHeight = gateway.SavedPosition(jobs[1].ModelFullPath, "bot").Z;
            Check(Close(firstHeight, -jobs[0].Height.DeltaH) && Close(secondHeight, -jobs[1].Height.DeltaH),
                "Successive height candidates are baseline-relative and never cumulative");
            Check(!Close(gateway.SavedLoad(jobs[0].ModelFullPath, "top", "LIVE").Fz,
                         gateway.SavedLoad(jobs[2].ModelFullPath, "top", "LIVE").Fz) &&
                  !Close(gateway.SavedTarget(jobs[0].ModelFullPath, "Cable-A", "PRETENSION").TargetForce,
                         gateway.SavedTarget(jobs[1].ModelFullPath, "Cable-A", "PRETENSION").TargetForce),
                "Loading and pretension assignments do not leak between children on the same worker");
        }
        finally { DeleteRoot(root); }
    }

    private static void FixedModesAndCombinedPhysicallyApplyResolvedCandidates()
    {
        VerifyMode(IterationStudyMode.FixHeight, "H03", null,
            row => row.IterationId is "L01_H03_P01" or "L01_H03_P02" or "L02_H03_P01" or "L02_H03_P02",
            "Fix Height physically applies H03 to all four L/P children");
        VerifyMode(IterationStudyMode.FixPretension, null, "P03",
            row => row.IterationId is "L01_H01_P03" or "L01_H02_P03" or "L02_H01_P03" or "L02_H02_P03",
            "Fix Pretension physically applies P03 to all four L/H children");
        VerifyMode(IterationStudyMode.Combined, null, null,
            row => row.IterationId is "L02_H02_P02",
            "Combined mode uses the same verified L/H/P builder");
    }

    private static void VerifyMode(
        IterationStudyMode mode,
        string? fixedHeight,
        string? fixedPretension,
        Func<StudyIteration, bool> select,
        string assertion)
    {
        var (root, _, snapshot) = CreateSnapshot(mode, 1, select, fixedHeight, fixedPretension);
        try
        {
            var factory = new FakeHostFactory();
            var result = new StudyExecutionCoordinator(factory).BuildAsync(snapshot,
                new InlineProgress<StudyExecutionProgress>(_ => { }), CancellationToken.None).GetAwaiter().GetResult();
            var jobs = snapshot.Jobs.Where(job => job.RunIterate).ToArray();
            Check(jobs.Length > 0 && result.Completed == jobs.Length &&
                  jobs.All(job => SavedCandidateMatches(factory.Gateways.Single(), snapshot, job)), assertion);
        }
        finally { DeleteRoot(root); }
    }

    private static void VerificationFailureNeverBecomesReady()
    {
        var (root, _, snapshot) = CreateSnapshot(2, 1);
        try
        {
            var corruptId = snapshot.Jobs.First(job => job.RunIterate).IterationId;
            var factory = new FakeHostFactory(corruptIterationId: corruptId);
            var result = new StudyExecutionCoordinator(factory).BuildAsync(snapshot,
                new InlineProgress<StudyExecutionProgress>(_ => { }), CancellationToken.None).GetAwaiter().GetResult();
            Check(result.Failed == 1 && result.Completed == 1,
                "A child whose saved loading is corrupted fails verification and never becomes Ready");
            Check(SamePath(factory.Gateways.Single().Current.Path, snapshot.BaselineModelPath),
                "Worker restores the baseline after verification failure and continues with the next candidate");
        }
        finally { DeleteRoot(root); }
    }

    private static void PretensionFailureReturnsToBaselineAndContinues()
    {
        var (root, _, original) = CreateSnapshot(2, 1);
        try
        {
            var jobs = original.Jobs.Where(job => job.RunIterate).ToArray();
            var badPretension = jobs[0].Pretension with
            {
                Assignments = [new PretensionAssignment("Invalid-Cable", "PRETENSION", 1234)]
            };
            var replaced = original.Jobs.Select(job => job.IterationId == jobs[0].IterationId
                ? job with { Pretension = badPretension }
                : job).ToArray();
            var snapshot = original with { Jobs = replaced };
            var factory = new FakeHostFactory();
            var result = new StudyExecutionCoordinator(factory).BuildAsync(snapshot,
                new InlineProgress<StudyExecutionProgress>(_ => { }), CancellationToken.None).GetAwaiter().GetResult();
            Check(result.Failed == 1 && result.Completed == 1,
                "Invalid cable target force leaves the candidate Failed and the worker processes the next job");
            Check(SamePath(factory.Gateways.Single().Current.Path, snapshot.BaselineModelPath),
                "Pretension failure returns the worker to the immutable baseline");
        }
        finally { DeleteRoot(root); }
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
            state.LoadDefinitions[0].MinQz = 999;
            state.Truss.MinimumHeight = 999;
            state.Cables[0].MinForce = 999;
            Check(job.Loading.Loads.All(load => load.Fz == -1) && job.Height.Height == 20 &&
                  job.Pretension.Assignments.Single().TargetForce == 1000,
                "Queued L/H/P values remain unchanged after mutable Tab 1-3 state is edited");
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
            Check(factory.Gateways.Sum(gateway => gateway.AnalysisAttempts.Count) == result.Completed &&
                  factory.Gateways.Sum(gateway => gateway.SavedAfterAnalysis.Count) == result.Completed,
                "Every completed child is analyzed and saved by its assigned worker");

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

    private static void ExistingChildModelIsReusedNotRebuilt()
    {
        var (root, _, snapshot) = CreateSnapshot(1, 1);
        try
        {
            var factory = new FakeHostFactory(reuseHosts: true);
            var coordinator = new StudyExecutionCoordinator(factory);
            var first = coordinator
                .BuildAsync(snapshot, new InlineProgress<StudyExecutionProgress>(_ => { }), CancellationToken.None)
                .GetAwaiter().GetResult();
            Check(first.Completed == 1 && first.Reused == 0, "First run builds the child model normally");

            var job = snapshot.Jobs.Single(candidate => candidate.RunIterate);
            var gateway = factory.Gateways.First(g => g.WorkerId == 1);
            var childBytes = File.ReadAllBytes(job.ModelFullPath);
            var analysisBefore = gateway.AnalysisAttempts.Count;
            var saveAsBefore = gateway.SaveAsSources.Count;

            var second = coordinator
                .BuildAsync(snapshot, new InlineProgress<StudyExecutionProgress>(_ => { }), CancellationToken.None)
                .GetAwaiter().GetResult();

            Check(second.Reused == 1 && second.Completed == 1 && second.Failed == 0,
                "A second run over the same candidate reuses the existing analyzed child instead of erroring");
            Check(gateway.SaveAsSources.Count == saveAsBefore,
                "Reuse never re-creates the child model (no SaveAs)");
            Check(gateway.AnalysisAttempts.Count == analysisBefore,
                "Reusing an already-analyzed child does not re-run its analysis");
            Check(File.ReadAllBytes(job.ModelFullPath).SequenceEqual(childBytes),
                "The existing child .sdb is never overwritten by a reuse");
            var studyFolder = Path.GetDirectoryName(job.IterationDirectory)!;
            Check(File.Exists(Path.Combine(studyFolder, "Deflection_Results_Source.xlsx")) &&
                  File.Exists(Path.Combine(studyFolder, "Deflection_Report.html")),
                "Reused candidates are still consolidated into the deflection source workbook and report");
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
        ConfigureDeflectionOutput(state);
        var composition = new StudyCompositionService().Compose(state);
        foreach (var row in composition.Iterations.Skip(selectedCount)) row.RunIterate = false;
        return (root, state, new StudyExecutionSnapshotFactory().Create(state, composition, workers));
    }

    private static (string Root, ApplicationState State, StudyExecutionSnapshot Snapshot) CreateSnapshot(
        IterationStudyMode mode,
        int workers,
        Func<StudyIteration, bool> select,
        string? fixedHeight = null,
        string? fixedPretension = null)
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        var baselinePath = Path.Combine(root, "COT_VCF.sdb");
        File.WriteAllText(baselinePath, "baseline");
        var state = StudyComposerChecks.CreateState();
        var baseline = state.SessionBaselineModel! with { Path = baselinePath };
        state.Model = baseline;
        state.SessionBaselineModel = baseline;
        state.Study.Mode = mode;
        if (fixedHeight is not null) state.Study.FixedHeightCandidateId = fixedHeight;
        if (fixedPretension is not null) state.Study.FixedPretensionCandidateId = fixedPretension;
        state.Study.OutputDirectory = Path.Combine(root, "Study");
        ConfigureDeflectionOutput(state);
        var composition = new StudyCompositionService().Compose(state);
        foreach (var row in composition.Iterations) row.RunIterate = select(row);
        return (root, state, new StudyExecutionSnapshotFactory().Create(state, composition, workers));
    }

    private static void ConfigureDeflectionOutput(ApplicationState state)
    {
        foreach (var name in new[] { "bot", "mid", "top" }) state.OutputSettings.DeflectionPoints.Add(name);
        state.OutputSettings.DeflectionOutputCaseName = "SERVICE";
    }

    private static bool SavedCandidateMatches(
        FakeGateway gateway,
        StudyExecutionSnapshot snapshot,
        StudyIterationExecutionDto job)
    {
        var expectedGeometry = new COT_VCF.Geometry.TrussGeometryService().BuildCandidateGeometry(
            snapshot.BaselineGeometry, snapshot.TopPoints, snapshot.MiddlePoints, snapshot.BottomPoints, job.Height);
        var geometryMatches = expectedGeometry.Points.Values.All(point =>
            Close(gateway.SavedPosition(job.ModelFullPath, point.Name), point.Position));
        var loadsMatch = job.Loading.Loads.All(expected =>
        {
            var actual = gateway.SavedLoad(job.ModelFullPath, expected.PointName, expected.LoadPattern);
            return Close(actual.Fx, expected.Fx) && Close(actual.Fy, expected.Fy) && Close(actual.Fz, expected.Fz);
        });
        var pretensionMatches = job.Pretension.Assignments.All(expected =>
            Close(gateway.SavedTarget(job.ModelFullPath, expected.CableName, expected.LoadPattern).TargetForce,
                expected.TargetForce));
        return geometryMatches && loadsMatch && pretensionMatches;
    }

    private static bool Close(Position left, Position right) =>
        Close(left.X, right.X) && Close(left.Y, right.Y) && Close(left.Z, right.Z);
    private static bool Close(double left, double right) =>
        Math.Abs(left - right) <= 1e-8 * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));

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

    private sealed class FailingArtifactPublisher : IStudyArtifactPublisher
    {
        public StudyArtifactOutcome Publish(StudyExecutionSnapshot snapshot, IReadOnlyList<CandidateResultDto> results) =>
            throw new IOException("simulated artifact failure");
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FakeHostFactory(
        int failStartId = 0,
        int failRuntimeId = 0,
        string? corruptIterationId = null,
        string? failAnalysisIterationId = null,
        bool reuseHosts = false) : ISapInstanceHostFactory
    {
        private readonly ConcurrentDictionary<int, FakeHost> cache = new();
        public ConcurrentBag<FakeGateway> Gateways { get; } = [];
        public int CreateCount;
        public ISapInstanceHost Create(int instanceId)
        {
            if (reuseHosts && cache.TryGetValue(instanceId, out var existing)) return existing;
            Interlocked.Increment(ref CreateCount);
            var gateway = new FakeGateway(instanceId, failRuntimeId == instanceId,
                corruptIterationId, failAnalysisIterationId);
            Gateways.Add(gateway);
            var host = new FakeHost(instanceId, gateway, failStartId == instanceId);
            if (reuseHosts) cache[instanceId] = host;
            return host;
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

    private sealed class FakeGateway(
        int workerId,
        bool failRuntime = false,
        string? corruptIterationId = null,
        string? failAnalysisIterationId = null) : ISapGateway
    {
        private int activeCalls;
        private int maxConcurrentCalls;
        private GeometryModel currentGeometry = CloneGeometry(StudyComposerChecks.CreateState().SessionBaseline);
        private Dictionary<string, PointLoad> currentLoads = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, CableTargetForce> currentTargets = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SavedState> savedStates = new(StringComparer.OrdinalIgnoreCase);
        private bool analysisComplete;
        public ModelInfo Current { get; private set; } = new("", "26", 6, "kN", "m", false);
        public ConcurrentBag<string> SaveAsSources { get; } = [];
        public ConcurrentBag<PointLoad> AppliedPointLoads { get; } = [];
        public ConcurrentBag<PretensionAssignment> AppliedTargetForces { get; } = [];
        public ConcurrentBag<string> AnalysisAttempts { get; } = [];
        public ConcurrentBag<string> SavedAfterAnalysis { get; } = [];
        public int MaxConcurrentCalls => maxConcurrentCalls;
        public int WorkerId => workerId;
        public int DisposeCount { get; private set; }
        public bool Crashed { get; private set; }

        public Task<ModelInfo> ConnectAsync() => throw new NotSupportedException();
        public Task<ModelSnapshot> ReadAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<PointLoad>> ReadPointLoadsAsync(ModelInfo expected) => Task.FromResult<IReadOnlyList<PointLoad>>([]);
        public async Task<Position> GetPointPositionAsync(string pointName, ModelInfo expected) => await Tracked(async () =>
        {
            Verify(expected); await Task.Delay(1); return currentGeometry.Points[pointName].Position;
        });
        public async Task<IReadOnlyList<PointLoad>> GetPointLoadsAsync(string pointName, ModelInfo expected) => await Tracked<IReadOnlyList<PointLoad>>(async () =>
        {
            Verify(expected); await Task.Delay(1);
            return currentLoads.Values.Where(load => load.PointName.Equals(pointName, StringComparison.OrdinalIgnoreCase)).ToArray();
        });
        public async Task<IReadOnlyList<CableTargetForce>> GetCableTargetForcesAsync(string cableName, ModelInfo expected) => await Tracked<IReadOnlyList<CableTargetForce>>(async () =>
        {
            Verify(expected); await Task.Delay(1);
            return currentTargets.Values.Where(force => force.CableName.Equals(cableName, StringComparison.OrdinalIgnoreCase)).ToArray();
        });
        public Task<IReadOnlyList<ForceSource>> ReadForcesAsync(IReadOnlyList<StructuralMember> members, ModelInfo expected) => Task.FromResult<IReadOnlyList<ForceSource>>([]);
        public Task<IReadOnlyList<string>> GetLoadCaseNamesAsync(ModelInfo expected) => Task.FromResult<IReadOnlyList<string>>(["DEAD", "SERVICE", "PRETENSION"]);
        public async Task<IReadOnlyList<COT_VCF.Domain.Results.JointDisplacement>> GetJointDisplacementsAsync(
            IReadOnlyList<string> pointNames, string outputCase, bool isCombo, ModelInfo expected) => await Tracked<IReadOnlyList<COT_VCF.Domain.Results.JointDisplacement>>(async () =>
        {
            Verify(expected); await Task.Delay(1);
            var rows = new List<COT_VCF.Domain.Results.JointDisplacement>();
            foreach (var name in pointNames)
            {
                if (!currentGeometry.Points.TryGetValue(name, out var p))
                { rows.Add(new(name, 0, 0, 0, null, COT_VCF.Domain.Results.ResultValueStatus.PointNotFound)); continue; }
                if (!analysisComplete)
                { rows.Add(new(name, p.Position.X, p.Position.Y, p.Position.Z, null, COT_VCF.Domain.Results.ResultValueStatus.AnalysisNotRun)); continue; }
                // deterministic synthetic deflection: parabolic-ish along X, in mm
                var uz = -Math.Round(1.5 * (10 - Math.Abs(5 - p.Position.X)), 3);
                rows.Add(new(name, p.Position.X, p.Position.Y, p.Position.Z, uz, COT_VCF.Domain.Results.ResultValueStatus.Valid));
            }
            return rows;
        });
        public async Task<IReadOnlyList<COT_VCF.Domain.Results.CableForceRead>> GetCableForcesAsync(
            IReadOnlyList<string> cableNames, string outputCase, bool isCombo, ModelInfo expected) => await Tracked<IReadOnlyList<COT_VCF.Domain.Results.CableForceRead>>(async () =>
        {
            Verify(expected); await Task.Delay(1);
            var rows = new List<COT_VCF.Domain.Results.CableForceRead>();
            foreach (var name in cableNames)
            {
                if (!analysisComplete)
                { rows.Add(new(name, null, COT_VCF.Domain.Results.ResultValueStatus.AnalysisNotRun)); continue; }
                // deterministic synthetic axial force in kN, keyed off the cable name so it is stable
                var force = 900.0 + (Math.Abs(name.GetHashCode()) % 400);
                rows.Add(new(name, Math.Round(force, 1), COT_VCF.Domain.Results.ResultValueStatus.Valid));
            }
            return rows;
        });
        public Task<IReadOnlyList<SapGroupSnapshot>> ReadOutputGroupsAsync(IReadOnlyList<string> groupNames, ModelInfo expected) =>
            Task.FromResult<IReadOnlyList<SapGroupSnapshot>>(groupNames.Select(name => new SapGroupSnapshot(name, false, [], [], [])).ToArray());
        public Task ReplaceOutputGroupsAsync(IReadOnlyList<OutputSapGroupDefinition> groups, ModelInfo expected) => Task.CompletedTask;

        public async Task<ModelInfo> OpenModelAsync(string modelPath) => await Tracked(async () =>
        {
            if (Crashed) throw new InvalidOperationException("SAP2000 process crashed.");
            await Task.Delay(2);
            var fullPath = Path.GetFullPath(modelPath);
            if (savedStates.TryGetValue(fullPath, out var saved))
                Restore(saved);
            else
            {
                currentGeometry = CloneGeometry(StudyComposerChecks.CreateState().SessionBaseline);
                currentLoads = new(StringComparer.OrdinalIgnoreCase);
                currentTargets = new(StringComparer.OrdinalIgnoreCase);
                analysisComplete = false;
            }
            // A reopened model comes back locked only if it was saved after a completed analysis.
            Current = Current with { Path = fullPath, IsLocked = analysisComplete };
            return Current;
        });

        public async Task<ModelInfo> SaveAsAsync(string targetPath, ModelInfo expected) => await Tracked(async () =>
        {
            if (!SamePath(Current.Path, expected.Path)) throw new InvalidOperationException("SaveAs source was not the active baseline.");
            SaveAsSources.Add(Current.Path);
            await Task.Delay(2);
            File.Copy(Current.Path, targetPath, false);
            Current = expected with { Path = Path.GetFullPath(targetPath), IsLocked = false };
            analysisComplete = false;
            savedStates[Current.Path] = Capture();
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
            currentGeometry = CloneGeometry(proposed);
            analysisComplete = false;
        });

        public async Task ApplyLoadsAsync(IReadOnlyList<PointLoad> loads, ModelInfo expected) => await Tracked(async () =>
        {
            Verify(expected);
            foreach (var load in loads)
            {
                AppliedPointLoads.Add(load);
                currentLoads[Key(load.PointName, load.LoadPattern)] = load with { };
            }
            analysisComplete = false;
            await Task.Delay(3);
        });

        public async Task SetCableTargetForceAsync(string cableName, string loadPattern, double targetForce, ModelInfo expected) => await Tracked(async () =>
        {
            Verify(expected);
            if (!cableName.Equals("Cable-A", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Cable {cableName} no longer exists in the model.");
            AppliedTargetForces.Add(new(cableName, loadPattern, targetForce));
            currentTargets[Key(cableName, loadPattern)] = new(cableName, loadPattern, targetForce, 0.5);
            analysisComplete = false;
            await Task.Delay(3);
        });

        public async Task RunAnalysisAsync(ModelInfo expected) => await Tracked(async () =>
        {
            Verify(expected);
            var path = Path.GetFullPath(Current.Path);
            AnalysisAttempts.Add(path);
            await Task.Delay(3);
            if (!string.IsNullOrWhiteSpace(failAnalysisIterationId) &&
                path.Contains(failAnalysisIterationId, StringComparison.Ordinal))
                throw new InvalidOperationException("SAP2000 analysis failed.");
            // SAP2000's RunAnalysis locks the model and persists it (plus result files) by itself.
            if (!string.IsNullOrWhiteSpace(corruptIterationId) && Current.Path.Contains(corruptIterationId, StringComparison.Ordinal))
                currentLoads = new(StringComparer.OrdinalIgnoreCase);
            analysisComplete = true;
            Current = Current with { IsLocked = true };
            savedStates[Current.Path] = Capture();
            SavedAfterAnalysis.Add(path);
        });

        public async Task SaveAsync(ModelInfo expected) => await Tracked(async () =>
        {
            Verify(expected);
            await Task.Delay(2);
            savedStates[Current.Path] = Capture();
            if (analysisComplete) SavedAfterAnalysis.Add(Path.GetFullPath(Current.Path));
        });

        public Task<ModelInfo> GetCurrentModelInfoAsync() => Task.FromResult(Current);

        public Position SavedPosition(string modelPath, string pointName) =>
            savedStates[Path.GetFullPath(modelPath)].Geometry.Points[pointName].Position;

        public PointLoad SavedLoad(string modelPath, string pointName, string loadPattern) =>
            savedStates[Path.GetFullPath(modelPath)].Loads[Key(pointName, loadPattern)];

        public CableTargetForce SavedTarget(string modelPath, string cableName, string loadPattern) =>
            savedStates[Path.GetFullPath(modelPath)].Targets[Key(cableName, loadPattern)];

        private void Verify(ModelInfo expected)
        {
            if (!SamePath(Current.Path, expected.Path))
                throw new InvalidOperationException($"Worker {workerId} active model identity changed.");
        }

        private SavedState Capture() => new(
            CloneGeometry(currentGeometry),
            currentLoads.ToDictionary(pair => pair.Key, pair => pair.Value with { }, StringComparer.OrdinalIgnoreCase),
            currentTargets.ToDictionary(pair => pair.Key, pair => pair.Value with { }, StringComparer.OrdinalIgnoreCase),
            analysisComplete);

        private void Restore(SavedState state)
        {
            currentGeometry = CloneGeometry(state.Geometry);
            currentLoads = state.Loads.ToDictionary(pair => pair.Key, pair => pair.Value with { }, StringComparer.OrdinalIgnoreCase);
            currentTargets = state.Targets.ToDictionary(pair => pair.Key, pair => pair.Value with { }, StringComparer.OrdinalIgnoreCase);
            analysisComplete = state.AnalysisComplete;
        }

        private static GeometryModel CloneGeometry(GeometryModel source) => new(
            source.Points.ToDictionary(pair => pair.Key, pair => pair.Value with { }, StringComparer.Ordinal),
            source.Members.Select(member => member with { }).ToArray());

        private static string Key(string objectName, string loadPattern) => objectName + "\0" + loadPattern;

        private sealed record SavedState(
            GeometryModel Geometry,
            IReadOnlyDictionary<string, PointLoad> Loads,
            IReadOnlyDictionary<string, CableTargetForce> Targets,
            bool AnalysisComplete);

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

        public void Dispose() => DisposeCount++;
    }
}
