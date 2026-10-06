using COT_VCF.Domain;
using COT_VCF.Application;
using COT_VCF.Geometry;
using COT_VCF.SAP2000;

internal static class PlanarFrameChecks
{
    public static void Run()
    {
        Check(SapGateway.ResolveEffectiveMaterial("A992", "None", "Frame1", "W14X90") == "A992" &&
              SapGateway.ResolveEffectiveMaterial("A992", "", "Frame1", "W14X90") == "A992" &&
              SapGateway.ResolveEffectiveMaterial("A992", "UserSteel", "Frame1", "W14X90") == "UserSteel",
            "CSI None material overrides fall back to the assigned ETABS/SAP section material");
        Check(RejectsMissingMaterial(), "FEM rejects members whose section and override contain no material");

        const double spanM = 10;
        const double loadKN = 100;
        const double modulusKNPerM2 = 200_000_000;
        const double inertiaM4 = 0.0008;
        var points = new Dictionary<string, StructuralPoint>(StringComparer.Ordinal)
        {
            ["A"] = new("A", new(0, 0, 0)),
            ["B"] = new("B", new(spanM / 2, 0, 0)),
            ["C"] = new("C", new(spanM, 0, 0))
        };
        var geometry = new GeometryModel(points,
        [
            new("AB", "A", "B", MemberKind.Frame),
            new("BC", "B", "C", MemberKind.Frame)
        ]);
        var section = new FemSectionProperties("AB", 0.01, modulusKNPerM2, inertiaM4);
        var sections = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal)
        {
            ["AB"] = section,
            ["BC"] = section with { MemberName = "BC" }
        };

        var result = new PlanarFrameSolver().Solve(geometry, sections,
            new Dictionary<string, FemSectionProperties>(), new Dictionary<string, double>(),
            [new("B", 0, -loadKN)], ["A", "C"]);
        var expectedDeflectionMm = loadKN * Math.Pow(spanM, 3) / (48 * modulusKNPerM2 * inertiaM4) * 1000;
        Check(Math.Abs(result.Joints.Single(joint => joint.PointName == "B").UZM * 1000 + expectedDeflectionMm) < 1e-6,
            "Two-element simply supported beam matches the analytical midpoint deflection");
        Check(result.SupportReactions.All(reaction => Math.Abs(reaction.ReactionZKN - loadKN / 2) < 1e-8),
            "Pin reactions balance the vertical point load");
        Check(Math.Abs(result.MaximumAbsoluteMomentKNm - loadKN * spanM / 4) < 1e-8,
            "Frame end forces recover the analytical peak bending moment");
        Check(result.Frames.All(frame => Math.Abs(Math.Abs(frame.ShearStartKN) - loadKN / 2) < 1e-8 &&
                                         Math.Abs(Math.Abs(frame.ShearEndKN) - loadKN / 2) < 1e-8),
            "Frame end shears recover the simply supported beam reactions");
                Check(FemResultTable.Build(result, FemResultMode.AxialForce).Count == 2 &&
                            FemResultTable.Build(result, FemResultMode.BendingMoment).Count == 2 &&
                            FemResultTable.Build(result, FemResultMode.Shear).Count == 2 &&
                            FemResultTable.Build(result, FemResultMode.Reactions).Count == 2 &&
                            FemResultTable.Build(result, FemResultMode.DeflectedShape).Count == 3,
                        "Each FEM Results mode produces the expected member, support, or joint rows");

        var meshed = new PlanarFrameSolver().Solve(geometry, sections,
            new Dictionary<string, FemSectionProperties>(), new Dictionary<string, double>(),
            [new("B", 0, -loadKN)], ["A", "C"], frameMeshSegments: 10);
        var quarter = meshed.Frames.Single(frame => frame.MemberName == "AB").Stations!.Single(station => Math.Abs(station.Fraction - 0.5) < 1e-12);
        var quarterX = spanM / 4;
        var expectedQuarterDeflection = loadKN * quarterX * (3 * spanM * spanM - 4 * quarterX * quarterX) / (48 * modulusKNPerM2 * inertiaM4);
        Check(meshed.Frames.Single(frame => frame.MemberName == "AB").Stations!.Count == 11 &&
              Math.Abs(meshed.Joints.Single(joint => joint.PointName == "B").UZM - result.Joints.Single(joint => joint.PointName == "B").UZM) < 1e-12 &&
              Math.Abs(Math.Abs(quarter.MomentKNm) - loadKN * spanM / 8) < 1e-6 &&
              Math.Abs(quarter.UZM + expectedQuarterDeflection) < 1e-9 &&
              Math.Abs(meshed.MaximumAbsoluteMomentKNm - loadKN * spanM / 4) < 1e-6,
            "A 10-segment frame mesh keeps joint results and recovers analytical quarter-span moment and deflection");

        var continuous = new PlanarFrameSolver().Solve(new GeometryModel(new Dictionary<string, StructuralPoint>(StringComparer.Ordinal)
            {
                ["A"] = new("A", new(0, 0, 0)), ["M"] = new("M", new(2, 0, 0)), ["B"] = new("B", new(5, 0, 0)), ["C"] = new("C", new(10, 0, 0))
            }, [new("AM", "A", "M", MemberKind.Frame), new("MB", "M", "B", MemberKind.Frame), new("BC", "B", "C", MemberKind.Frame)]),
            new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal) { ["AM"] = section, ["MB"] = section, ["BC"] = section },
            new Dictionary<string, FemSectionProperties>(), new Dictionary<string, double>(), [new("M", 0, -100)], ["A", "B", "C"],
            frameMeshSegments: 4);
        var linear = continuous.Frames.All(frame =>
        {
            var stations = frame.Stations!;
            var slope = (stations[^1].MomentKNm - stations[0].MomentKNm) / (stations[^1].Fraction - stations[0].Fraction);
            return stations.All(station => Math.Abs(stations[0].MomentKNm + slope * station.Fraction - station.MomentKNm) < 1e-6);
        });
        var byName = continuous.Frames.ToDictionary(frame => frame.MemberName);
        Check(linear && Math.Abs(byName["AM"].MomentEndKNm - byName["MB"].MomentStartKNm) < 1e-6 &&
              Math.Abs(byName["MB"].MomentEndKNm - byName["BC"].MomentStartKNm) < 1e-6,
            "Frame moments use one sign convention: linear along each unloaded frame and continuous across shared joints");

        var chordGeometry = new GeometryModel(new Dictionary<string, StructuralPoint>(StringComparer.Ordinal)
        {
            ["A"] = new("A", new(0, 0, 0)), ["B"] = new("B", new(spanM / 2, 0, 0)), ["C"] = new("C", new(spanM, 0, 0)),
            ["S"] = new("S", new(spanM / 2, 0, -3)), ["Q"] = new("Q", new(spanM, 0, -3))
        }, [new("Chord", "A", "C", MemberKind.Frame), new("Post", "B", "S", MemberKind.Frame), new("Tie", "C", "Q", MemberKind.Cable)]);
        var chordSections = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal)
        {
            ["Chord"] = section with { MemberName = "Chord" }, ["Post"] = section with { MemberName = "Post" }
        };
        var chord = new PlanarFrameSolver().Solve(chordGeometry, chordSections,
            new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal) { ["Tie"] = new("Tie", 0.001, modulusKNPerM2, null) },
            new Dictionary<string, double>(), [new("B", 0, -loadKN)], ["A", "C"], frameMeshSegments: 10);
        var chordStations = chord.Frames.Single(frame => frame.MemberName == "Chord").Stations!;
        Check(Math.Abs(chord.Joints.Single(joint => joint.PointName == "B").UZM * 1000 + expectedDeflectionMm) < 1e-6 &&
              chord.SupportReactions.All(reaction => Math.Abs(reaction.ReactionZKN - loadKN / 2) < 1e-6) &&
              chordStations.Count == 22 && Math.Abs(chordStations.Max(station => Math.Abs(station.MomentKNm)) - loadKN * spanM / 4) < 1e-6 &&
              chord.Frames.Count == 2 && chord.Cables.Single().MemberName == "Tie" &&
              FrameJoints.Pieces(chordGeometry.Members[0], chordGeometry.Points.ToDictionary(p => p.Key, p => p.Value.Position)).Count == 2,
            "A full-length frame is split at an intermediate joint so members meeting it connect; each piece is meshed and cables stay single elements");

        var cableGeometry = new GeometryModel(new Dictionary<string, StructuralPoint>(StringComparer.Ordinal)
        {
            ["A"] = new("A", new(-5, 0, 0)),
            ["B"] = new("B", new(0, 0, 0)),
            ["C"] = new("C", new(5, 0, 0)),
            ["D"] = new("D", new(0, 0, 5))
        },
        [
            new("AB", "A", "B", MemberKind.Frame),
            new("BC", "B", "C", MemberKind.Frame),
            new("Cable-BD", "B", "D", MemberKind.Cable)
        ]);
        var beamProperties = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal)
        {
            ["AB"] = section with { MemberName = "AB" },
            ["BC"] = section with { MemberName = "BC" }
        };
        var cableProperties = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal)
        {
            ["Cable-BD"] = new("Cable-BD", 0.01, modulusKNPerM2, null)
        };
        var slackResult = new PlanarFrameSolver().Solve(cableGeometry, beamProperties, cableProperties,
            new Dictionary<string, double> { ["Cable-BD"] = 100 }, [new("B", 0, 500)], ["A", "C", "D"]);
        Check(!slackResult.Cables.Single().Active && slackResult.Cables.Single().AxialForceKN == 0,
            "Pretensioned cable switches cleanly to slack under opposing uplift");
        Check(Math.Abs(slackResult.SupportReactions.Sum(reaction => reaction.ReactionZKN) + 500) < 1e-5 &&
              slackResult.NonlinearIterations <= 100,
            "Damped cable equilibrium converges and balances support reactions after cable slackening");

        var targets = new Dictionary<string, double> { ["Cable-BD"] = 100 };
        var unfitted = new PlanarFrameSolver().Solve(cableGeometry, beamProperties, cableProperties,
            targets, [], ["A", "C", "D"]);
        var fitted = new PlanarFrameSolver().Solve(cableGeometry, beamProperties, cableProperties,
            targets, [new("B", 0, -50)], ["A", "C", "D"], targetFitPercent: 1);
        var fittedCable = fitted.Cables.Single();
        var fittedUnloaded = new PlanarFrameSolver().Solve(cableGeometry, beamProperties, cableProperties,
            new Dictionary<string, double> { ["Cable-BD"] = fittedCable.AppliedPretensionKN }, [], ["A", "C", "D"]);
        Check(Math.Abs(unfitted.Cables.Single().AxialForceKN - 100) > 1 &&
              fittedCable.PretensionStageForceKN is double stage && Math.Abs(stage - 100) <= 1 &&
              Math.Abs(fittedUnloaded.Cables.Single().AxialForceKN - stage) < 1e-6 &&
              fittedCable.AxialForceKN > stage && fittedCable.TargetForceKN == 100 &&
              Math.Abs(fittedCable.AppliedPretensionKN - 100) > 1 && fitted.TargetFitIterations is >= 1 and <= 10,
            "Target-force fitting meets the target in the pretension-only stage, then the loads act on the prestressed state");

        var fittedMeshed = new PlanarFrameSolver().Solve(cableGeometry, beamProperties, cableProperties,
            targets, [new("B", 0, -50)], ["A", "C", "D"], targetFitPercent: 1, frameMeshSegments: 10);
        Check(fittedMeshed.Frames.Zip(fitted.Frames).All(pair =>
                  Math.Abs(pair.First.MomentStartKNm - pair.Second.MomentStartKNm) < 1e-6 &&
                  Math.Abs(pair.First.MomentEndKNm - pair.Second.MomentEndKNm) < 1e-6 &&
                  Math.Abs(pair.First.ShearEndKN - pair.Second.ShearEndKN) < 1e-6) &&
              Math.Abs(fittedMeshed.Cables.Single().AxialForceKN - fittedCable.AxialForceKN) < 1e-6,
            "Meshed frames give the same end forces and cable forces as unmeshed frames for joint loads");

        var overloaded = new PlanarFrameSolver().Solve(cableGeometry, beamProperties, cableProperties,
            targets, [new("B", 0, -200)], ["A", "C", "D"], targetFitPercent: 1);
        Check(overloaded.Cables.Single().PretensionStageForceKN is double overloadedStage && Math.Abs(overloadedStage - 100) <= 1 &&
              overloaded.Cables.Single().AxialForceKN > 150,
            "Loads that alone exceed the cable target no longer block fitting; the final loaded force is reported above target");

        var coupledGeometry = new GeometryModel(new Dictionary<string, StructuralPoint>(cableGeometry.Points, StringComparer.Ordinal)
        {
            ["E"] = new("E", new(0, 0, -5))
        }, [.. cableGeometry.Members, new("Cable-BE", "B", "E", MemberKind.Cable)]);
        var coupledCables = new Dictionary<string, FemSectionProperties>(cableProperties, StringComparer.Ordinal)
        {
            ["Cable-BE"] = new("Cable-BE", 0.01, modulusKNPerM2, null)
        };
        var coupled = new PlanarFrameSolver().Solve(coupledGeometry, beamProperties, coupledCables,
            new Dictionary<string, double> { ["Cable-BD"] = 100, ["Cable-BE"] = 99.5 }, [], ["A", "C", "D", "E"], targetFitPercent: 1);
        Check(coupled.Cables.All(cable => cable.PretensionStageForceKN is double force && Math.Abs(force - cable.TargetForceKN) <= 0.01 * cable.TargetForceKN) &&
              coupled.TargetFitIterations <= 10,
            "Opposing cables on one joint are fitted together to their separate targets");
        var infeasible = new PlanarFrameSolver().Solve(coupledGeometry, beamProperties, coupledCables,
            new Dictionary<string, double> { ["Cable-BD"] = 100, ["Cable-BE"] = 60 }, [new("B", 0, -10)], ["A", "C", "D", "E"],
            targetFitPercent: 1, maximumTargetFitIterations: 20);
        var infeasibleRows = FemCableFitRow.FromRun(new FemStudyRun("L01_H01_P02", "L01", "H01", "P02", infeasible)).ToArray();
        Check(!infeasible.TargetFitConverged && infeasible.TargetFitFailure!.Contains("not achievable") &&
              infeasibleRows.Length == 2 && infeasibleRows.All(row => row.Status == "Not fitted" && row.DeviationPercent is not null) &&
              infeasibleRows.Any(row => Math.Abs(row.DeviationPercent!.Value) > 1),
            "Unachievable targets still return the closest result, marked Not fitted with its deviation, instead of stopping the run");
        var fitRow = FemCableFitRow.FromRun(new FemStudyRun("L01_H01_P01", "L01", "H01", "P01", fitted)).Single();
        Check(fitRow.CableName == "Cable-BD" && fitRow.TargetForceKN == 100 && fitRow.DeviationPercent is double deviation &&
              Math.Abs(deviation) <= 1 && fitRow.FitIterations == fitted.TargetFitIterations,
            "Cable target-fit rows report target, FEM result force, and % deviation per case");

        var segmentedGeometry = new GeometryModel(new Dictionary<string, StructuralPoint>(cableGeometry.Points, StringComparer.Ordinal)
        {
            ["M"] = new("M", new(0, 0, 2.5))
        }, [.. cableGeometry.Members.Where(member => member.Kind == MemberKind.Frame),
            new("Cable-BM", "B", "M", MemberKind.Cable), new("Cable-MD", "M", "D", MemberKind.Cable)]);
        var segmentedCables = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal)
        {
            ["Cable-BM"] = new("Cable-BM", 0.01, modulusKNPerM2, null),
            ["Cable-MD"] = new("Cable-MD", 0.01, modulusKNPerM2, null)
        };
        var segmentedTargets = new Dictionary<string, double> { ["Cable-BM"] = 100, ["Cable-MD"] = 100 };
        var segmentedTaut = new PlanarFrameSolver().Solve(segmentedGeometry, beamProperties, segmentedCables,
            segmentedTargets, [new("B", 0, -50)], ["A", "C", "D"], targetFitPercent: 1);
        var segmentedSlack = new PlanarFrameSolver().Solve(segmentedGeometry, beamProperties, segmentedCables,
            segmentedTargets, [new("B", 0, 5000)], ["A", "C", "D"], targetFitPercent: 1);
        Check(segmentedTaut.Cables.All(cable => cable.PretensionStageForceKN is double force && Math.Abs(force - 100) <= 1) &&
              segmentedSlack.Cables.All(cable => !cable.Active) &&
              Math.Abs(segmentedSlack.SupportReactions.Sum(reaction => reaction.ReactionZKN) + 5000) < 1e-3,
            "A cable split at a cable-only joint solves both taut and fully slack without a singular-tangent stall");

        var studyState = CandidateState();
        var service = new FemStudyService();
        var candidates = service.Compose(studyState);
        Check(candidates.Count == 8 && candidates[0].Id == "L01_H01_P01" && candidates[^1].Id == "L02_H02_P02",
            "FEM candidate composer enumerates the full deterministic 2×2×2 Tab 01–03 space");
        Check(candidates[0].Loads.All(load => load.FzKN == -5) && candidates[4].Loads.All(load => load.FzKN == -10),
            "FEM candidate loads reuse the Tab 01 tributary-force calculation");
        Check(FemStudyService.FindPinSupportPoints(studyState.Baseline).SequenceEqual(["A", "B"]),
              "Automatic pin supports select lowest vertical column ends and ignore diagonal braces");
    }

    private static ApplicationState CandidateState()
    {
        var points = new Dictionary<string, StructuralPoint>(StringComparer.Ordinal)
        {
            ["A"] = new("A", new(0, 0, 0)),
            ["B"] = new("B", new(10, 0, 0)),
            ["T1"] = new("T1", new(0, 0, 5)),
            ["T2"] = new("T2", new(10, 0, 5)),
            ["M1"] = new("M1", new(0, 0, 2.5)),
            ["M2"] = new("M2", new(10, 0, 2.5)),
            ["X"] = new("X", new(5, 0, 0))
        };
        var geometry = new GeometryModel(points, [
            new("CA", "A", "T1", MemberKind.Frame),
            new("CB", "B", "T2", MemberKind.Frame),
            new("Top", "T1", "T2", MemberKind.Frame),
            new("Mid", "M1", "M2", MemberKind.Frame),
            new("LeftWeb", "T1", "M1", MemberKind.Frame),
            new("RightWeb", "T2", "M2", MemberKind.Frame),
            new("Brace", "X", "M1", MemberKind.Frame),
            new("Cable-A", "T1", "T2", MemberKind.Cable)
        ]);
        var state = new ApplicationState
        {
            Model = new ModelInfo(@"C:\Models\FEM.sdb", "27", 0, "kN", "m", false),
            Connection = "Connected",
            Geometry = geometry,
            Baseline = geometry,
            BaselineCaptured = true,
            TributaryWidthB = 1,
            NumberOfStories = 1
        };
        state.Truss.TopPoints = ["T1", "T2"];
        state.Truss.MiddlePoints = ["M1", "M2"];
        state.Truss.BottomPoints = ["A", "B"];
        state.Truss.BaselineHeight = 5;
        state.Truss.MinimumHeight = 5;
        state.Truss.MaximumHeight = 6;
        state.Truss.Increment = 1;
        state.Truss.CurrentHeight = 5;
        state.AppliedPoints.Add(new() { PointName = "T1", X = 0, Z = 5 });
        state.AppliedPoints.Add(new() { PointName = "T2", X = 10, Z = 5 });
        state.LoadPatterns.Add("LIVE");
        state.LoadPatterns.Add("PRETENSION");
        state.LoadDefinitions.Clear();
        state.LoadDefinitions.Add(new LoadDefinition
        {
            LoadName = "LIVE", ApplyIncrementStep = true, MinQz = 1, MaxQz = 2, IncrementStep = 1
        });
        state.Cables.Add(new CablePretensionConfig
        {
            CableName = "Cable-A", LoadCase = "PRETENSION", ApplyRange = true,
            MinForce = 10, MaxForce = 20, StepForce = 10
        });
        return state;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS: {message}");
    }

    private static bool RejectsMissingMaterial()
    {
        try
        {
            SapGateway.ResolveEffectiveMaterial("None", "None", "Frame1", "W14X90");
            return false;
        }
        catch (InvalidOperationException) { return true; }
    }
}