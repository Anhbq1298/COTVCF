using COT_VCF.Application;
using COT_VCF.Configuration;
using COT_VCF.Domain;
using COT_VCF.Geometry;
using COT_VCF.SAP2000;
using System.IO;
using System.Text.Json;

/// <summary>
/// Live check (<c>--cable-length-live &lt;baseline.sdb&gt; &lt;project.json&gt; [--analyse]</c>): does a cable keep
/// its length relative to its chord when the study changes the truss height?
/// <para>Starts a SAP2000 instance of its own on a copy of the baseline under <c>.work</c>, saves a child model, moves
/// the joints to the tallest height of the project the way a study worker does, saves, and reads what SAP2000 itself
/// wrote about the cables into the child model's text copy.</para>
/// <para>With <c>--analyse</c> it then runs that one candidate — tallest height, first loading, first pretension —
/// through the study's own build, analysis and extraction, and compares the SAP2000 result with the internal FEM
/// (<see cref="FemSapBenchmark"/>) and with the result the same candidate had in the benchmark study.</para>
/// It never attaches to a SAP2000 the user has open, and never writes to the source model or its folder.
/// </summary>
internal static class CableLengthLiveCheck
{
    public static async Task RunAsync(string sourceBaseline, string projectPath, bool analyse)
    {
        var source = Path.GetFullPath(sourceBaseline);
        if (!File.Exists(source)) throw new FileNotFoundException("The baseline model was not found.", source);
        var root = Path.GetFullPath(Path.Combine(".work", "cable-length-verification", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(Path.Combine(root, "child"));
        var baselinePath = Path.Combine(root, Path.GetFileName(source));
        File.Copy(source, baselinePath, false);
        var childPath = Path.Combine(root, "child", Path.GetFileNameWithoutExtension(source) + "_tallest.sdb");

        var state = new ApplicationState { Connection = "Connected" };
        GeometryModel baseline, proposed;
        double height;
        await using (var host = new SapInstanceHost(1))
        {
            var info = await host.StartAsync(baselinePath, CancellationToken.None);
            var gateway = host.Gateway;
            var snapshot = await gateway.ReadAsync();
            Console.WriteLine($"LIVE: SAP2000 {info.Version} opened {info.Name} — {snapshot.Geometry.Points.Count} joints, " +
                              $"{snapshot.Geometry.Members.Count(member => member.Kind == MemberKind.Cable)} cables.");

            state.Model = info;
            state.Geometry = snapshot.Geometry;
            state.SessionBaselineModel = info;
            state.SessionBaseline = snapshot.Geometry;
            state.SessionBaselinePointLoads = await gateway.ReadPointLoadsAsync(info);
            foreach (var pattern in snapshot.LoadPatterns) state.LoadPatterns.Add(pattern);
            var workflow = new WorkflowService(gateway, state);
            var config = new ConfigService(gateway, state, workflow);
            var project = config.Read(Path.GetFullPath(projectPath));
            var check = await config.ValidateAsync(project);
            if (!check.FileValid || !check.AllPresentOk)
                throw new InvalidOperationException("The project does not fit the model: " + string.Join("; ",
                    check.FileErrors.Concat(new[] { check.Loading, check.Truss, check.Cable, check.Output, check.Study }.SelectMany(tab => tab.Problems))));
            config.Apply(project, check);

            // the tallest candidate of the project, built exactly as a study worker builds it
            height = state.Truss.CandidateHeights[^1];
            var delta = height - state.Truss.BaselineHeight;
            var (top, middle, bottom) = TrussGeometryService.ChordDeltas(state.Truss.Reference, delta);
            var candidate = new TrussCandidate(state.Truss.CandidateHeights.Count, height, delta, state.Truss.Reference.ToString(), top, middle, bottom);
            baseline = state.Baseline;
            proposed = new TrussGeometryService().BuildCandidateGeometry(baseline, state.Truss.TopPoints.ToArray(), state.Truss.MiddlePoints.ToArray(),
                state.Truss.BottomPoints.ToArray(), candidate);

            var child = await gateway.SaveAsAsync(childPath, info);
            await gateway.MovePointsAsync(baseline, proposed, child);
            await gateway.SaveAsync(child);
            Console.WriteLine($"LIVE: child model saved with the truss at {height:0.###} m (baseline {state.Truss.BaselineHeight:0.###} m): {childPath}");
        }

        // What SAP2000 wrote about the cables into the child model's own text copy.
        var textPath = Path.ChangeExtension(childPath, ".$2k");
        if (!File.Exists(textPath)) throw new FileNotFoundException("SAP2000 wrote no text copy of the child model.", textPath);
        var text = SapModelText.Read(textPath);
        var saved = text.Geometry();
        var shapes = text.CableShapes();
        static double Chord(GeometryModel geometry, StructuralMember member)
        {
            var (a, b) = (geometry.Points[member.Start].Position, geometry.Points[member.End].Position);
            return Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2) + Math.Pow(b.Z - a.Z, 2));
        }
        var cables = saved.Members.Where(member => member.Kind == MemberKind.Cable).OrderBy(member => member.Name, StringComparer.Ordinal).ToArray();
        Console.WriteLine("cable         definition            baseline chord   chord now   undeformed length   relative length");
        foreach (var cable in cables)
        {
            var shape = shapes[cable.Name];
            Console.WriteLine($"{cable.Name,-12}  {shape.Type,-20} {Chord(baseline, cable),12:0.000} m {Chord(saved, cable),9:0.000} m {shape.UndeformedLengthM,15:0.000} m {shape.RelativeLength,15:0.0000}");
        }
        var lengthened = cables.Count(cable => Math.Abs(Chord(saved, cable) - Chord(baseline, cable)) > 1e-6);
        Check(cables.Length > 0 && cables.All(cable => Math.Abs(Chord(saved, cable) - Chord(proposed, cable)) < 1e-6) && lengthened > 0,
            $"SAP2000 saved the child model with the joints at the tallest height ({height:0.###} m): {lengthened} of {cables.Length} cables span a different chord than in the baseline");
        // the relative length each cable had in the baseline, from SAP2000's text copy of the source model when there is one
        var sourceText = Path.ChangeExtension(source, ".$2k");
        var before = File.Exists(sourceText) ? SapModelText.Read(sourceText).CableShapes() : null;
        var relative = cables.Select(cable => (Cable: cable, Baseline: before is not null && before.TryGetValue(cable.Name, out var was) ? was.RelativeLength : 1.0,
            Now: shapes[cable.Name].UndeformedLengthM / Chord(saved, cable))).ToArray();
        Check(relative.All(item => Math.Abs(item.Now - shapes[item.Cable.Name].RelativeLength) < 1e-4),
            "SAP2000's own record is consistent: the relative length it lists is the undeformed length over the chord between the cable's joints");
        var off = relative.Max(item => Math.Abs(item.Now - item.Baseline));
        var stale = cables.Min(cable => Chord(baseline, cable) / Chord(saved, cable));
        Check(off < 1e-6,
            $"every cable has the relative undeformed length it had in the baseline ({relative.Min(item => item.Baseline):0.###} … {relative.Max(item => item.Baseline):0.###}) on its new chord — " +
            $"largest deviation {off * 100:0.#####} % (left alone, the most affected cable would be at {stale:0.###} of its chord)");
        Check(shapes.Values.All(shape => shape.Type == "Undeformed Length"),
            "the cables are still defined the way the engineer defined them: by their undeformed length");
        Console.WriteLine($"Cable lengths follow the joints. Child model and SAP2000's text copy: {Path.GetDirectoryName(childPath)}");

        if (analyse) await Analyse(state, source, projectPath, root);
    }

    /// <summary>One candidate through the study itself — build, SAP2000 analysis, extraction — then against the FEM.</summary>
    private static async Task Analyse(ApplicationState state, string source, string projectPath, string root)
    {
        var sourceText = Path.ChangeExtension(source, ".$2k");
        if (!File.Exists(sourceText)) throw new FileNotFoundException("The comparison with the FEM needs SAP2000's text copy of the baseline beside it.", sourceText);

        // tallest height, first loading, first pretension — and nothing else
        var all = new StudyCompositionService().Compose(state);
        var chosen = all.Iterations.Where(iteration => iteration.HeightCandidateId == all.HeightCandidates[^1].Id)
            .OrderBy(iteration => iteration.LoadingCandidateId, StringComparer.Ordinal).ThenBy(iteration => iteration.PretensionCandidateId, StringComparer.Ordinal).First().IterationId;
        foreach (var iteration in all.Iterations) state.Study.SetRunSelected(iteration.IterationId, iteration.IterationId == chosen);
        var composition = new StudyCompositionService().Compose(state);
        var snapshot = new StudyExecutionSnapshotFactory().Create(state, composition, 1);
        Console.WriteLine($"LIVE: analysing {chosen} in SAP2000 through the study's own build path …");
        var coordinator = new StudyExecutionCoordinator(new SapInstanceHostFactory());
        var result = await coordinator.BuildAsync(snapshot, new Progress<StudyExecutionProgress>(progress =>
            Console.WriteLine($"LIVE: {progress.IterationId} {progress.Stage} · {progress.IterationStatus}")), CancellationToken.None);
        if (result.Completed != 1 || result.Failed != 0 || coordinator.LastArtifactOutcome.RawJsonPath is not { } rawJson)
            throw new InvalidOperationException($"The live study run did not complete: completed={result.Completed}, failed={result.Failed}.");

        // The same like-for-like comparison as the benchmark, on this one freshly analysed candidate.
        var fixture = Path.Combine(root, "fixture");
        FemSapBenchmark.Capture(sourceText, projectPath, rawJson, fixture);
        FemSapBenchmark.Run(fixture, Path.Combine(root, "report"));

        // …and against what SAP2000 gave for the same candidate when the cables kept their baseline length.
        var earlierPath = Path.Combine(FemSapBenchmark.DefaultFolder, FemSapBenchmark.ReferenceFile);
        if (!File.Exists(earlierPath)) return;
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var now = JsonSerializer.Deserialize<SapReference>(File.ReadAllText(Path.Combine(fixture, FemSapBenchmark.ReferenceFile)), json)!.Candidates.Single();
        var earlier = JsonSerializer.Deserialize<SapReference>(File.ReadAllText(earlierPath), json)!.Candidates.FirstOrDefault(candidate => candidate.Key == chosen);
        if (earlier is null) return;
        Console.WriteLine($"SAP2000, {chosen}: cables at their baseline length (benchmark study) → cables at their chord length (now)");
        Console.WriteLine($"  peak downward deflection   {earlier.DeflectionMm.Values.Max(uz => -uz),10:0.0} mm → {now.DeflectionMm.Values.Max(uz => -uz),10:0.0} mm");
        foreach (var cable in now.CableForceKN.Keys.OrderBy(name => name, StringComparer.Ordinal).Where(name => name.Contains('a')))
            Console.WriteLine($"  {cable,-12} force         {earlier.CableForceKN[cable],10:0.0} kN → {now.CableForceKN[cable],10:0.0} kN");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
