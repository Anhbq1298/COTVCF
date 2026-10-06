using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using COT_VCF.Application;
using COT_VCF.Application.Results;
using COT_VCF.Configuration;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;
using COT_VCF.Geometry;

/// <summary>One candidate of a SAP2000 study, reduced to what the comparison uses.</summary>
/// <param name="CableUndeformedLengthM">Undeformed length of every cable object in the candidate's own SAP2000 model, and
/// <paramref name="CableChordM"/> the distance between its end joints there — both from SAP2000's text copy of that
/// child model. Null when the study folder held no such file.</param>
internal sealed record SapReferenceCandidate(string Key, string Load, string Height, string Pretension, double TrussHeightM,
    Dictionary<string, double> CableTargetKN, Dictionary<string, double> CableForceKN, Dictionary<string, double> DeflectionMm,
    Dictionary<string, double[]> ReactionKN,       // support → [Fx, Fz]
    Dictionary<string, double>? CableUndeformedLengthM = null, Dictionary<string, double>? CableChordM = null);

/// <summary>The SAP2000 side of the benchmark: results of a completed study and the cases they were read from.</summary>
internal sealed record SapReference(string ModelName, DateTimeOffset GeneratedAt, string CableCase, string DeflectionCase,
    string ReactionCase, List<SapReferenceCandidate> Candidates);

/// <summary>
/// Internal FEM against SAP2000, like for like, on a real study (<c>--fem-sap</c>).
/// <para><b>Same model.</b> The FEM inputs are read from SAP2000's own text copy of the baseline model
/// (<see cref="SapModelText"/>): geometry, sections, materials, self-weight, joint loads, supports and load cases.
/// The candidates are composed from the same project file the study ran with, and are checked against what the
/// study recorded: candidate keys, truss heights and every cable's target force.</para>
/// <para><b>Same stage.</b> SAP2000's results belong to the cases the study read them from. Those are staged: they
/// continue from the pretension case, in which SAP2000 met the cable target forces under the loads of that case.
/// The FEM runs exactly those cases, fits the pretension in that same case, and is compared case for case —
/// cable forces and support reactions in the cable / reaction case, deflections in the deflection case.</para>
/// <para><b>Same loads.</b> The sum of the vertical reactions is compared first: it does not depend on stiffness, so
/// it shows whether both analyses carry the same loads before any force or deflection is compared.</para>
/// The fixture folder holds three files — <c>model.s2k</c>, <c>project.json</c>, <c>sap-results.json</c> — written by
/// <see cref="Capture"/> from a study on disk. Nothing here connects to SAP2000.
/// </summary>
internal static class FemSapBenchmark
{
    public const string DefaultFolder = "tests/fem-sap-benchmark";
    public const string ModelFile = "model.s2k", ProjectFile = "project.json", ReferenceFile = "sap-results.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ΣFz does not depend on stiffness: with the same loads the two analyses must agree almost exactly (observed 0.0013 %).
    private const double TotalLoadTolerancePercent = 0.01;

    /// <summary>Writes the fixture from a study on disk: SAP2000's text copy of the baseline model, the project file
    /// the study ran with, and the study's raw results reduced to cable forces, deflections and support reactions.</summary>
    public static void Capture(string modelTextPath, string projectPath, string rawResultsPath, string folder)
    {
        var raw = new StudyRawDataStore().Read(rawResultsPath);
        // <Study>/RawData/Study_Raw_Results.json → the candidate folders, each with SAP2000's text copy of its child model
        var studyFolder = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(rawResultsPath))) ?? "";
        var childModels = 0;
        (Dictionary<string, double>? Undeformed, Dictionary<string, double>? Chord) CableLengths(string key)
        {
            var candidateFolder = Path.Combine(studyFolder, key);
            var text = Directory.Exists(candidateFolder) ? Directory.GetFiles(candidateFolder, "*.$2k").FirstOrDefault() : null;
            if (text is null) return (null, null);
            var child = SapModelText.Read(text);
            var shape = child.Geometry();
            childModels++;
            return (child.CableShapes().ToDictionary(cable => cable.Key, cable => cable.Value.UndeformedLengthM, StringComparer.Ordinal),
                shape.Members.Where(member => member.Kind == MemberKind.Cable).ToDictionary(member => member.Name, member =>
                {
                    var (a, b) = (shape.Points[member.Start].Position, shape.Points[member.End].Position);
                    return Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2) + Math.Pow(b.Z - a.Z, 2));
                }, StringComparer.Ordinal));
        }
        var candidates = new List<SapReferenceCandidate>();
        foreach (var result in raw.Results.OrderBy(item => item.Parameters.RunIndex))
        {
            var cables = (result.CableForces ?? []).Where(cable => cable.Status == ResultValueStatus.Valid && cable.ForceKN is not null).ToArray();
            var deflections = result.Groups.Where(group => string.Equals(group.OutputCase, raw.DeflectionOutputCase, StringComparison.OrdinalIgnoreCase))
                .SelectMany(group => group.Points).Where(point => point.Status == ResultValueStatus.Valid && point.UzMm is not null)
                .GroupBy(point => point.PointName, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().UzMm!.Value, StringComparer.Ordinal);
            // a static case gives one reaction per support; with several result steps the last is the final state
            var reactions = (result.SupportReactionGroups ?? [])
                .Where(group => string.Equals(group.OutputCase, raw.FoundationReactionResultCase, StringComparison.OrdinalIgnoreCase))
                .SelectMany(group => group.Points).Where(point => point.Status == ResultValueStatus.Valid && point.Fx is not null && point.Fz is not null)
                .GroupBy(point => point.PointName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.OrderBy(point => point.StepNumber).Last() is var last ? new[] { last.Fx!.Value, last.Fz!.Value } : [],
                    StringComparer.Ordinal);
            if (cables.Length == 0 && deflections.Count == 0 && reactions.Count == 0) continue;
            var (undeformed, chord) = CableLengths(result.IterationKey);
            candidates.Add(new(result.IterationKey, result.Parameters.LoadingCandidateId, result.Parameters.HeightCandidateId,
                result.Parameters.PretensionCandidateId, result.Parameters.TrussHeightM,
                cables.Where(cable => cable.TargetForceKN is not null).ToDictionary(cable => cable.CableName, cable => cable.TargetForceKN!.Value, StringComparer.Ordinal),
                cables.ToDictionary(cable => cable.CableName, cable => cable.ForceKN!.Value, StringComparer.Ordinal), deflections, reactions, undeformed, chord));
        }
        Directory.CreateDirectory(folder);
        File.Copy(modelTextPath, Path.Combine(folder, ModelFile), overwrite: true);
        // the project as the study ran it, without its Print Setup: that is presentation only and names a local logo file
        var project = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(projectPath))!.AsObject();
        project.Remove("printSetup");
        File.WriteAllText(Path.Combine(folder, ProjectFile), project.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, ReferenceFile), JsonSerializer.Serialize(
            new SapReference(raw.BaselineModelName, raw.GeneratedAt, raw.CableResultCase, raw.DeflectionOutputCase, raw.FoundationReactionResultCase, candidates), Json));
        Console.WriteLine($"Captured {candidates.Count} SAP2000 candidates of {raw.BaselineModelName} into {folder}" +
            $" — cable lengths read from {childModels} child model text files.");
    }

    private sealed class Failures : IProgress<FemProgress>
    {
        public List<string> Items { get; } = [];
        public void Report(FemProgress value) { if (value.Failure is { } failure) Items.Add($"{value.CandidateId}: {failure}"); }
    }

    /// <param name="Fem">FEM with the cable lengths SAP2000 analysed — the like-for-like value.</param>
    /// <param name="FemChord">FEM as the FEM tab runs it: every cable as long as its chord.</param>
    /// <param name="FemStraight">As <paramref name="FemChord"/>, with cable sag switched off.</param>
    private sealed record Sample(string Candidate, string Height, string Quantity, string Name, double Sap, double Fem, double FemChord, double FemStraight)
    {
        public double Difference => Fem - Sap;
        public double Percent => Math.Abs(Sap) > 1e-9 ? (Fem - Sap) / Math.Abs(Sap) * 100 : 0;
        public double PercentChord => Math.Abs(Sap) > 1e-9 ? (FemChord - Sap) / Math.Abs(Sap) * 100 : 0;
    }

    /// <summary>Runs the comparison on the fixture in <paramref name="folder"/> and writes the report into
    /// <paramref name="reportFolder"/>. Throws when the two analyses are not on the same inputs, or disagree by more
    /// than the bands this benchmark holds them to.</summary>
    public static void Run(string folder, string reportFolder)
    {
        var reference = JsonSerializer.Deserialize<SapReference>(File.ReadAllText(Path.Combine(folder, ReferenceFile)), Json)
            ?? throw new InvalidDataException("The SAP2000 reference results could not be read.");
        var model = SapModelText.Read(Path.Combine(folder, ModelFile));
        var geometry = model.Geometry();
        var sections = model.SectionProperties();
        var loading = model.Loading();
        var supports = model.PinSupports();

        // The project the study ran with, applied to the model exactly as Load Config does.
        var state = new ApplicationState
        {
            Model = new ModelInfo(reference.ModelName, "SAP2000 text file", 6, "kN", "m", false),
            Connection = "Connected",
            Geometry = geometry,
        };
        state.SessionBaseline = geometry;
        state.SessionBaselineModel = state.Model;
        foreach (var pattern in model.LoadPatternNames) state.LoadPatterns.Add(pattern);
        var workflow = new WorkflowService(null!, state);
        var project = new ConfigService(null!, state, workflow).Read(Path.Combine(folder, ProjectFile));
        var check = new ConfigCompatibilityValidator().Validate(project, state, model.LoadCaseNames);
        Check(check.FileValid && check.AllPresentOk,
            "the project file of the study fits the SAP2000 model text: every point, cable, frame, load pattern and load case it refers to" +
            (check.FileValid && check.AllPresentOk ? "" : " — " + string.Join("; ",
                check.FileErrors.Concat(new[] { check.Loading, check.Truss, check.Cable, check.Output, check.Study }.SelectMany(tab => tab.Problems)))));
        new ConfigurationApplicator(state, workflow).Apply(project, check);

        var service = new FemStudyService();
        var sap = reference.Candidates.ToDictionary(candidate => candidate.Key, StringComparer.Ordinal);
        var composed = service.Compose(state);
        // a fixture may hold part of the candidate space (some candidates analysed again); those are the ones compared
        var candidates = composed.Where(candidate => sap.ContainsKey(candidate.Id)).ToArray();
        Check(candidates.Length == sap.Count && candidates.Length > 0,
            $"same candidates: the FEM composes the {sap.Count} L × H × P candidate(s) the SAP2000 study ran, by the same keys" +
            (composed.Count > candidates.Length ? $" (of {composed.Count} in the project)" : ""));
        Check(candidates.All(candidate => Math.Abs(candidate.Height.Height - sap[candidate.Id].TrussHeightM) < 1e-6),
            "same geometry: every candidate has the truss height SAP2000 analysed");
        Check(candidates.All(candidate => sap[candidate.Id].CableTargetKN.All(target =>
                candidate.PretensionTargetsKN.TryGetValue(target.Key, out var fem) && Math.Abs(fem - target.Value) < 1e-6)),
            "same pretension: every cable of every candidate has the target force SAP2000 was given");
        var detected = FemStudyService.FindPinSupportPoints(state.Baseline);
        Check(supports.Count >= 2 && supports.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(detected.OrderBy(name => name, StringComparer.Ordinal)),
            $"same supports: the pins the FEM detects are the joints SAP2000 restrains ({string.Join(", ", supports)})");

        // Same stage: run the cases SAP2000's results were read from. The cases they continue from come along, and the
        // pretension is fitted in the one that applies the target-force pattern, under that case's own loads.
        var tab01 = state.LoadDefinitions.Select(definition => definition.LoadName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var pretension = state.Cables.Select(cable => cable.LoadPattern).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var applied = state.AppliedPoints.Select(point => point.PointName).ToArray();
        var resultCases = new[] { reference.CableCase, reference.ReactionCase, reference.DeflectionCase }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        FemLoadPlan Plan(Func<FemCaseRow, bool> run, IReadOnlyDictionary<string, FemSectionProperties> properties)
        {
            var (patternRows, caseRows, combinationRows) = FemLoadPlan.RowsFromSap(loading, tab01, pretension);
            foreach (var row in caseRows) row.Run = run(row);
            return FemLoadPlan.Build(patternRows, caseRows, combinationRows, loading, properties, state.Baseline, tab01, pretension, applied);
        }
        var plan = Plan(row => resultCases.Contains(row.Name, StringComparer.OrdinalIgnoreCase), sections);
        var stageCase = loading.Cases.Single(item => item.Name.Equals(reference.CableCase, StringComparison.OrdinalIgnoreCase)).InitialCase;
        Check(resultCases.All(name => plan.CaseNames.Contains(name, StringComparer.OrdinalIgnoreCase)) && plan.FitCases.Count == 1 &&
              string.Equals(plan.FitCases[0], stageCase, StringComparison.OrdinalIgnoreCase),
            $"same stage: the FEM solves {string.Join(" and ", resultCases.Select(name => $"'{name}'"))} continuing from '{stageCase}', " +
            "and fits the cable target forces in that case, as SAP2000 does");

        var tolerance = model.TargetForceTolerancePercent.GetValueOrDefault(stageCase ?? "", 1);
        string[] top = state.Truss.TopPoints.ToArray(), middle = state.Truss.MiddlePoints.ToArray(), bottom = state.Truss.BottomPoints.ToArray();
        IReadOnlyList<FemStudyRun> Analyze(IReadOnlyList<FemStudyCandidate> which, IReadOnlyDictionary<string, FemSectionProperties> properties, FemLoadPlan loadPlan, bool cableSag)
        {
            var failures = new Failures();
            var runs = service.Analyze(state.Baseline, top, middle, bottom, supports, which, properties, failures, default, tolerance,
                PlanarFrameSolver.DefaultTargetFitIterations, 2, loadPlan, cableSag);
            if (failures.Items.Count > 0) throw new InvalidOperationException("FEM cases did not solve: " + string.Join(" | ", failures.Items.Take(5)));
            return runs;
        }

        // 1 · As the FEM tab runs it: every cable is as long as its chord in the candidate's geometry.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var physical = Analyze(candidates, sections, plan, cableSag: true).ToDictionary(run => run.Id, StringComparer.Ordinal);
        var straight = Analyze(candidates, sections, plan, cableSag: false).ToDictionary(run => run.Id, StringComparer.Ordinal);

        // 2 · As SAP2000 analysed it. The study changes the truss height by moving joints. The ends of a cable move with
        // its joints, so its chord gets longer — but the cable objects of this model are defined by an absolute
        // undeformed length, and SAP2000 keeps that number. Its own text copy of every child model says so: the
        // undeformed length L₀ stays at the baseline chord while the chord L grows, and the stored relative length drops
        // below 1. SAP2000 therefore analyses each cable with stiffness EA / L₀ and weight γ·A·L₀. The FEM reproduces that
        // with the L₀ SAP2000 recorded for the candidate, by scaling each cable's stiffness area by L / L₀ and its
        // self-weight area by L₀ / L.
        var recorded = reference.Candidates.All(candidate => candidate.CableUndeformedLengthM is not null && candidate.CableChordM is not null);
        var builder = new TrussGeometryService();
        double Chord(GeometryModel shape, StructuralMember member)
        {
            var (a, b) = (shape.Points[member.Start].Position, shape.Points[member.End].Position);
            return Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Z - a.Z, 2));
        }
        var cableMembers = state.Baseline.Members.Where(member => member.Kind == MemberKind.Cable).ToArray();
        var asModelled = new Dictionary<string, FemStudyRun>(StringComparer.Ordinal);
        double longestStretch = 1.0, chordMismatch = 0, baselineMismatch = 0;
        foreach (var candidate in candidates)
        {
            var shape = builder.BuildCandidateGeometry(state.Baseline, top, middle, bottom, candidate.Height);
            var adjusted = new Dictionary<string, FemSectionProperties>(sections, StringComparer.Ordinal);
            foreach (var cable in cableMembers)
            {
                var undeformed = recorded ? sap[candidate.Id].CableUndeformedLengthM![cable.Name] : Chord(state.Baseline, cable);
                if (recorded)
                {
                    chordMismatch = Math.Max(chordMismatch, Math.Abs(sap[candidate.Id].CableChordM![cable.Name] - Chord(shape, cable)));
                    baselineMismatch = Math.Max(baselineMismatch, Math.Abs(undeformed - Chord(state.Baseline, cable)));
                }
                var ratio = Chord(shape, cable) / undeformed;
                longestStretch = Math.Max(longestStretch, ratio);
                var properties = sections[cable.Name];
                adjusted[cable.Name] = properties with
                {
                    AreaM2 = properties.AreaM2 * ratio,
                    GrossAreaM2 = (properties.GrossAreaM2 > 0 ? properties.GrossAreaM2 : properties.AreaM2) / ratio,
                };
            }
            asModelled[candidate.Id] = Analyze([candidate], adjusted, Plan(row => resultCases.Contains(row.Name, StringComparer.OrdinalIgnoreCase), adjusted), cableSag: true).Single();
        }
        Console.WriteLine($"      FEM: {candidates.Length} candidate(s) × 3 variants solved in {clock.Elapsed.TotalSeconds:0.0} s");
        if (recorded)
        {
            Check(chordMismatch < 1e-6,
                $"same cable geometry: in every SAP2000 child model each cable spans the chord the FEM gives it — the cable ends moved with their joints (largest difference {chordMismatch * 1000:0.###} mm)");
            // Either the study carried the cable lengths over to the new chords, or the cables kept their baseline length.
            Check(longestStretch <= 1 + 1e-6 || baselineMismatch < 1e-6,
                longestStretch <= 1 + 1e-6
                    ? $"SAP2000 itself records the cable lengths: in all {candidates.Length} child model(s) every cable's undeformed length is the chord it spans — the lengths followed the joints"
                    : $"SAP2000 itself records the cable lengths: in all {candidates.Length} child models every cable's undeformed length is still its baseline chord " +
                      $"(largest difference {baselineMismatch * 1000:0.###} mm), up to {(1 - 1 / longestStretch) * 100:0.#} % shorter than the chord it now spans");
        }

        // The app runs every SAP2000 case by default. Other cases, including one that applies the target force without
        // self-weight, must not change the staged results: each case uses the pretension of the stage it continues from.
        var everyCase = Plan(row => row.Supported, sections);
        var subset = candidates.Where((_, index) => index % 15 == 0).ToArray();
        var subsetRuns = service.Analyze(state.Baseline, top, middle, bottom, supports, subset, sections, null, default, tolerance,
            PlanarFrameSolver.DefaultTargetFitIterations, 2, everyCase, true);
        Check(everyCase.FitCases.Count > 1 && subsetRuns.Count == subset.Length && subsetRuns.All(run =>
                run.ResultFor(reference.CableCase).Cables.Zip(physical[run.Id].ResultFor(reference.CableCase).Cables)
                    .All(pair => Math.Abs(pair.First.AxialForceKN - pair.Second.AxialForceKN) < 1e-6 * Math.Max(1, pair.Second.AxialForceKN))),
            $"with every SAP2000 case run ({everyCase.FitCases.Count} pretension stages: {string.Join(", ", everyCase.FitCases)}) the staged results are unchanged: " +
            "a case carries the pretension of the stage it continues from");

        var samples = new List<Sample>();
        var stageDeviation = 0.0;
        foreach (var candidate in candidates)
        {
            var target = sap[candidate.Id];
            void Add(string quantity, string name, double sapValue, string resultCase, Func<FemAnalysisResult, double> value) =>
                samples.Add(new(candidate.Id, candidate.HeightCandidateId, quantity, name, sapValue, value(asModelled[candidate.Id].ResultFor(resultCase)),
                    value(physical[candidate.Id].ResultFor(resultCase)), value(straight[candidate.Id].ResultFor(resultCase))));
            foreach (var (cable, force) in target.CableForceKN)
            {
                Add(CableQuantity, cable, force, reference.CableCase, result => result.Cables.Single(item => item.MemberName == cable).AxialForceKN);
                var fitted = asModelled[candidate.Id].ResultFor(reference.CableCase).Cables.Single(item => item.MemberName == cable);
                if (fitted.TargetForceKN > 0 && fitted.PretensionStageForceKN is double stage)
                    stageDeviation = Math.Max(stageDeviation, Math.Abs(stage - fitted.TargetForceKN) / fitted.TargetForceKN * 100);
            }
            foreach (var (point, uz) in target.DeflectionMm)
                Add(DeflectionQuantity, point, uz, reference.DeflectionCase, result => result.Joints.Single(item => item.PointName == point).UZM * 1000);
            foreach (var (support, reaction) in target.ReactionKN)
            {
                Add(ReactionXQuantity, support, reaction[0], reference.ReactionCase, result => result.SupportReactions.Single(item => item.PointName == support).ReactionXKN);
                Add(ReactionZQuantity, support, reaction[1], reference.ReactionCase, result => result.SupportReactions.Single(item => item.PointName == support).ReactionZKN);
            }
            Add(TotalQuantity, "ΣFz", target.ReactionKN.Values.Sum(reaction => reaction[1]), reference.ReactionCase,
                result => result.SupportReactions.Sum(item => item.ReactionZKN));
            if (target.DeflectionMm.Count > 0)
                Add(PeakQuantity, "max −Uz", target.DeflectionMm.Values.Max(uz => -uz), reference.DeflectionCase,
                    result => target.DeflectionMm.Keys.Max(point => -result.Joints.Single(item => item.PointName == point).UZM * 1000));
        }

        // The report is written before anything is judged, so a disagreement can be read in full.
        Directory.CreateDirectory(reportFolder);
        var report = Report(reference, model, plan, samples, candidates.Length, tolerance, supports, longestStretch);
        File.WriteAllText(Path.Combine(reportFolder, "fem-vs-sap.md"), report, new UTF8Encoding(false));
        var csv = new StringBuilder("candidate,height,quantity,name,sap,fem_sap_cable_lengths,fem_chord_cable_lengths,fem_chord_lengths_straight_cables,difference_like_for_like,difference_percent_like_for_like,difference_percent_chord_lengths\n");
        string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        foreach (var sample in samples)
            csv.Append(sample.Candidate).Append(',').Append(sample.Height).Append(",\"").Append(sample.Quantity).Append("\",").Append(sample.Name).Append(',')
                .Append(N(sample.Sap)).Append(',').Append(N(sample.Fem)).Append(',').Append(N(sample.FemChord)).Append(',').Append(N(sample.FemStraight)).Append(',')
                .Append(N(sample.Difference)).Append(',').Append(N(sample.Percent)).Append(',').Append(N(sample.PercentChord)).Append('\n');
        File.WriteAllText(Path.Combine(reportFolder, "fem-vs-sap.csv"), csv.ToString(), new UTF8Encoding(false));
        Console.WriteLine(report);
        Console.WriteLine($"      Report: {Path.GetFullPath(Path.Combine(reportFolder, "fem-vs-sap.md"))} (+ .csv, {samples.Count} compared values)");

        Check(stageDeviation <= tolerance + 1e-9,
            $"pretension stage: every cable is within SAP2000's own Target-Force tolerance of its target (worst {stageDeviation:0.###} % of ±{tolerance:0.###} %)");
        var total = samples.Where(sample => sample.Quantity == TotalQuantity).ToArray();
        var totalWorst = total.Max(sample => Math.Abs(sample.Percent));
        Check(totalWorst <= TotalLoadTolerancePercent,
            $"same loads: ΣFz of the supports agrees with SAP2000 in all {total.Length} candidates (worst {totalWorst:0.#####} %, {total.Max(sample => Math.Abs(sample.Difference)):0.0} kN) — self-weight and study loads are identical");
        var drift = total.Max(sample => Math.Abs(sample.FemChord - sample.Sap));
        if (longestStretch > 1.01)
            Check(drift > 10 * total.Max(sample => Math.Abs(sample.Difference)),
                $"…and analyses the cables at that length: only with it does ΣFz agree in every candidate — with each cable weighed over its chord it drifts by up to {drift:0.0} kN as the truss gets taller");
        foreach (var (quantity, band) in Bands)
        {
            var group = samples.Where(sample => sample.Quantity == quantity).ToArray();
            if (group.Length == 0) continue;
            var scale = group.Max(sample => Math.Abs(sample.Sap));
            var worst = group.Max(sample => Math.Abs(sample.Difference)) / scale * 100;
            Check(worst <= band, $"{quantity}: like for like, the FEM is within {band:0.#} % of SAP2000 over {group.Length} values (worst difference {worst:0.##} % of the largest SAP2000 value, {scale:0.#})");
        }
    }

    private const string CableQuantity = "Cable force [kN]", DeflectionQuantity = "Deflection Uz [mm]", PeakQuantity = "Peak downward deflection δ [mm]",
        ReactionXQuantity = "Reaction Fx [kN]", ReactionZQuantity = "Reaction Fz [kN]", TotalQuantity = "Total vertical reaction [kN]";

    /// <summary>Largest like-for-like difference this benchmark accepts per quantity, in % of the largest SAP2000 value
    /// of that quantity over the study. Set from the agreement observed on this model, with margin: a regression band
    /// for this benchmark, not an accuracy claim for other models.</summary>
    private static readonly (string Quantity, double Percent)[] Bands =
    [
        // observed on this study: 3.8, 3.2, 3.2, 0.32 and 1.2 %
        (CableQuantity, 5), (DeflectionQuantity, 5), (PeakQuantity, 5), (ReactionZQuantity, 1), (ReactionXQuantity, 2),
    ];

    private static string Report(SapReference reference, SapModelText model, FemLoadPlan plan, IReadOnlyList<Sample> samples, int candidates,
        double tolerance, IReadOnlyList<string> supports, double longestStretch)
    {
        var text = new StringBuilder();
        var nonlinearity = model.GeometricNonlinearity;
        var stage = plan.FitCases.FirstOrDefault() ?? "";
        string Row(FormattableString row) => row.ToString(CultureInfo.InvariantCulture);
        text.AppendLine("# Internal FEM against SAP2000 — like for like").AppendLine();
        text.AppendLine($"Model `{reference.ModelName}` · SAP2000 study of {reference.GeneratedAt:yyyy-MM-dd} · {candidates} candidates · {samples.Count} compared values.").AppendLine();
        text.AppendLine("| | SAP2000 | Internal FEM |").AppendLine("|---|---|---|");
        text.AppendLine("| Model | as analysed | read from SAP2000's text copy of the same model |");
        text.AppendLine(Row($"| Pretension stage | case `{stage}`, Target Force ±{tolerance:0.###} % · {nonlinearity.GetValueOrDefault(stage, "linear")} | same case, same loads, same tolerance |"));
        foreach (var name in new[] { reference.CableCase, reference.DeflectionCase }.Distinct(StringComparer.OrdinalIgnoreCase))
            text.AppendLine($"| `{name}` | continues from the pretension stage · {nonlinearity.GetValueOrDefault(name, "linear")} | same loads, continuing from the same stage · small displacements |");
        text.AppendLine("| Cables | catenary cable objects | sagging cable (parabola, Ernst) |");
        text.AppendLine($"| Supports | {string.Join(", ", supports)} pinned | the same pins |").AppendLine();
        text.AppendLine($"Cable forces and reactions are compared in `{reference.CableCase}`, deflections in `{reference.DeflectionCase}`.").AppendLine();

        void Table(string title, string lead, Func<Sample, double> fem)
        {
            text.AppendLine("## " + title).AppendLine().AppendLine(lead).AppendLine();
            text.AppendLine("| Quantity | Values | SAP2000 range | Mean abs. diff. | Worst diff. | Worst, % of largest | Worst at |");
            text.AppendLine("|---|---:|---:|---:|---:|---:|---|");
            foreach (var group in samples.GroupBy(sample => sample.Quantity))
            {
                var scale = group.Max(sample => Math.Abs(sample.Sap));
                var worst = group.MaxBy(sample => Math.Abs(fem(sample) - sample.Sap))!;
                text.AppendLine(Row($"| {group.Key} | {group.Count()} | {group.Min(sample => sample.Sap):0.#} … {group.Max(sample => sample.Sap):0.#} | {group.Average(sample => Math.Abs(fem(sample) - sample.Sap)):0.##} | {fem(worst) - worst.Sap:+0.##;-0.##} | {Math.Abs(fem(worst) - worst.Sap) / scale * 100:0.##} | {worst.Candidate} · {worst.Name} |"));
            }
            text.AppendLine();
        }
        Table("1 · Like for like: the model SAP2000 analysed", longestStretch > 1.01
            ? "The study changes the truss height by moving joints. The cable objects are defined by an absolute undeformed length, which stays at its baseline value, " +
              $"so in the taller candidates SAP2000 analyses cables whose undeformed length is up to {(1 - 1 / longestStretch) * 100:0} % shorter than the chord they span: " +
              $"up to {(longestStretch - 1) * 100:0} % stiffer (EA / L₀) and no heavier than at baseline. Here the FEM is given the cable lengths SAP2000 recorded for each candidate."
            : "In these child models every cable's undeformed length is the chord it spans — the cable lengths followed the joints — so this is also the FEM tab as it runs.", sample => sample.Fem);
        Table("2 · The FEM tab as it runs: every cable as long as its chord",
            "The same comparison with each cable at the length of its chord in the candidate's geometry — the physical cable. " +
            "The difference from table 1 is the effect of the cable lengths in the SAP2000 child models, not of the solver.", sample => sample.FemChord);

        text.AppendLine("## By truss height").AppendLine();
        text.AppendLine("Mean signed difference from SAP2000, in % of each SAP2000 value (worst in brackets).").AppendLine();
        text.AppendLine("| Height | Peak deflection δ — like for like | Peak deflection δ — chord lengths | Cable force — like for like | Cable force — chord lengths |");
        text.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var height in samples.Select(sample => sample.Height).Distinct().OrderBy(name => name, StringComparer.Ordinal))
        {
            string Cell(string quantity, Func<Sample, double> percent)
            {
                var group = samples.Where(sample => sample.Height == height && sample.Quantity == quantity).Select(percent).ToArray();
                return group.Length == 0 ? "—" : Row($"{group.Average():+0.0;-0.0} ({group.MaxBy(Math.Abs):+0.0;-0.0})");
            }
            text.AppendLine($"| {height} | {Cell(PeakQuantity, sample => sample.Percent)} | {Cell(PeakQuantity, sample => sample.PercentChord)} | {Cell(CableQuantity, sample => sample.Percent)} | {Cell(CableQuantity, sample => sample.PercentChord)} |");
        }
        text.AppendLine();

        var cables = samples.Where(sample => sample.Quantity == CableQuantity).ToArray();
        var sagEffect = cables.Length == 0 ? 0 : cables.Max(sample => Math.Abs(sample.FemChord - sample.FemStraight) / Math.Abs(sample.Sap) * 100);
        text.AppendLine(Row($"Cable sag: with the cables at {cables.Min(sample => sample.Sap):0} … {cables.Max(sample => sample.Sap):0} kN, switching the FEM's cable sag off changes a cable force by at most {sagEffect:0.###} % — sag is not what separates the two analyses here.")).AppendLine();

        if (model.Notes.Count > 0 || plan.Notes.Count > 0)
        {
            text.AppendLine("## Notes").AppendLine();
            foreach (var note in model.Notes.Concat(plan.Notes).Distinct()) text.AppendLine("- " + note);
            text.AppendLine();
        }
        text.AppendLine("What still differs by construction: SAP2000 analyses these cases with " +
            string.Join(" / ", new[] { stage, reference.CableCase, reference.DeflectionCase }.Select(name => nonlinearity.GetValueOrDefault(name, "linear")).Distinct()) +
            " geometry, catenary cables and shear-deformable frames; the FEM uses small displacements, the parabolic cable and Euler–Bernoulli frames. " +
            "SAP2000's cable force is the largest along the cable, the FEM's is the chord tension.");
        return text.ToString();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
