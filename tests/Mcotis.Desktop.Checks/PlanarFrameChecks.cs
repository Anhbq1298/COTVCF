using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.SAP2000;

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

        var result = new FemSolver().Solve(geometry, sections,
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

        var meshed = new FemSolver().Solve(geometry, sections,
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

        var continuous = new FemSolver().Solve(new GeometryModel(new Dictionary<string, StructuralPoint>(StringComparer.Ordinal)
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

        var beamOnly = new GeometryModel(new Dictionary<string, StructuralPoint>(StringComparer.Ordinal)
        {
            ["A"] = new("A", new(0, 0, 0)), ["C"] = new("C", new(spanM, 0, 0))
        }, [new("Beam", "A", "C", MemberKind.Frame)]);
        var beamSection = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal) { ["Beam"] = section with { MemberName = "Beam" } };
        const double udl = 10;
        var udlCase = new FemCaseLoads("UDL", new FemLoadSet([], [new FemMemberLoad("Beam", FemLoadDirection.Gravity, 0, 1, udl, udl)], []), 0);
        var pointCase = new FemCaseLoads("PL", new FemLoadSet([], [], [new FemMemberPointLoad("Beam", FemLoadDirection.GlobalZ, 0.3, -loadKN)]), 0);
        var bothCase = new FemCaseLoads("BOTH", FemLoadSet.Sum([udlCase.Loads, pointCase.Loads]), 0);
        var memberLoadResults = new FemSolver().SolveCases(beamOnly, beamSection, new Dictionary<string, FemSectionProperties>(),
            new Dictionary<string, double>(), FemLoadSet.Empty, [udlCase, pointCase, bothCase], ["A", "C"], frameMeshSegments: 10);
        var udlResult = memberLoadResults["UDL"];
        var midspan = udlResult.Frames.Single().Stations!.Single(station => Math.Abs(station.Fraction - 0.5) < 1e-12);
        Check(Math.Abs(midspan.MomentKNm - udl * spanM * spanM / 8) < 1e-6 &&
              Math.Abs(midspan.UZM + 5 * udl * Math.Pow(spanM, 4) / (384 * modulusKNPerM2 * inertiaM4)) < 1e-9 &&
              udlResult.SupportReactions.All(reaction => Math.Abs(reaction.ReactionZKN - udl * spanM / 2) < 1e-6) &&
              Math.Abs(udlResult.Frames.Single().ShearStartKN - udl * spanM / 2) < 1e-6,
            "A uniform gravity member load gives wL²/8 midspan moment, 5wL⁴/384EI deflection and wL/2 reactions");
        var releasedSection = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal)
        {
            ["Beam"] = section with { MemberName = "Beam", ReleaseStartRotation = true, ReleaseEndRotation = true }
        };
        var releasedUdl = new FemSolver().SolveCases(beamOnly, releasedSection, new Dictionary<string, FemSectionProperties>(),
            new Dictionary<string, double>(), FemLoadSet.Empty, [udlCase], ["A", "C"], frameMeshSegments: 10)["UDL"];
        var releasedMid = releasedUdl.Frames.Single().Stations!.Single(station => Math.Abs(station.Fraction - 0.5) < 1e-12);
        Check(Math.Abs(releasedMid.MomentKNm - udl * spanM * spanM / 8) < 1e-6 &&
              Math.Abs(releasedMid.UZM + 5 * udl * Math.Pow(spanM, 4) / (384 * modulusKNPerM2 * inertiaM4)) < 1e-9 &&
              Math.Abs(releasedUdl.Frames.Single().MomentStartKNm) < 1e-6 && Math.Abs(releasedUdl.Frames.Single().MomentEndKNm) < 1e-6,
            "Member loads pass through end-moment releases: released simply supported UDL beam matches wL²/8 and 5wL⁴/384EI");
        var pointResult = memberLoadResults["PL"];
        Check(Math.Abs(pointResult.SupportReactions.Single(reaction => reaction.PointName == "A").ReactionZKN - loadKN * 0.7) < 1e-6 &&
              Math.Abs(pointResult.SupportReactions.Single(reaction => reaction.PointName == "C").ReactionZKN - loadKN * 0.3) < 1e-6 &&
              Math.Abs(pointResult.Frames.Single().Stations!.Max(station => station.MomentKNm) - loadKN * 0.3 * 0.7 * spanM) < 1e-6,
            "A member point load at 0.3L splits the reactions 0.7P / 0.3P with peak moment P·a·b/L");
        var combined = FemResultCombiner.Combine([(udlResult, 1), (pointResult, 1)]);
        var both = memberLoadResults["BOTH"];
        Check(combined.Joints.Zip(both.Joints).All(pair => Math.Abs(pair.First.UZM - pair.Second.UZM) < 1e-12) &&
              combined.Frames.Single().Stations!.Zip(both.Frames.Single().Stations!).All(pair => Math.Abs(pair.First.MomentKNm - pair.Second.MomentKNm) < 1e-6) &&
              combined.SupportReactions.Zip(both.SupportReactions).All(pair => Math.Abs(pair.First.ReactionZKN - pair.Second.ReactionZKN) < 1e-6),
            "A linear-add combination of case results equals the analysis of the summed loads (no cables)");

        var parsed = FemLoadDefinition.Parse("1.2 DEAD + 1.5 LIVE - 0.5 WIND-X + PT");
        Check(parsed.Count == 4 && parsed[0] == ("DEAD", 1.2) && parsed[1] == ("LIVE", 1.5) && parsed[2] == ("WIND-X", -0.5) && parsed[3] == ("PT", 1),
            "Load definitions parse factors, minus terms, unit factors and hyphenated names");

        // Load plan: self-weight (γ·A·weight modifier), SAP loads, Tab 01 replacement at applied points, combination.
        var normalTargetPattern = new FemCaseRow("Normal", "Nonlinear Static", "1 PT", null, true, true, "App", "");
        var purposePlan = FemLoadPlan.Build([new("PT", "Other", 0, "", "")], [normalTargetPattern], [], SapFemLoading.Empty,
            new Dictionary<string, FemSectionProperties>(), geometry, [], ["PT"], []);
        Check(purposePlan.FitCases.Count == 0 && purposePlan.ForCandidate([]).Cases.All(item => item.PretensionFactor == 0),
            "Normal case never fits or applies cable targets merely because its definition includes the target pattern");
        normalTargetPattern.CableTargetForces = true;
        purposePlan = FemLoadPlan.Build([new("PT", "Other", 0, "", "")], [normalTargetPattern], [], SapFemLoading.Empty,
            new Dictionary<string, FemSectionProperties>(), geometry, [], ["PT"], []);
        Check(purposePlan.FitCases.SequenceEqual(["Normal"]) && purposePlan.ForCandidate([]).Cases.Single().PretensionFactor == 1,
            "Explicit Cable Target Forces classification creates the fitting stage");
        var weightedSection = section with { UnitWeightKNPerM3 = 78.5, GrossAreaM2 = 0.01, Modifiers = [1, 1, 1, 1, 1, 1, 1, 2] };
        var planSections = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal)
        {
            ["AB"] = weightedSection with { MemberName = "AB" }, ["BC"] = weightedSection with { MemberName = "BC" }
        };
        var weight = 78.5 * 0.01 * 2;
        var sapLoading = new SapFemLoading(
            [new("DEAD", "Dead", 1), new("LIVE", "Live", 0)],
            [new("LIVE", new FemJointLoad("B", 0, -999)), new("DEAD", new FemJointLoad("B", 0, -20))],
            [new("DEAD", new FemMemberLoad("AB", FemLoadDirection.Gravity, 0, 1, 4, 4))], [], [],
            [new("DEAD", "Linear Static", true, [("DEAD", 1)], null, null), new("LIVE", "Linear Static", true, [("LIVE", 1)], null, null)],
            [new("ULS", "Linear Additive", true, [("DEAD", false, 1.2), ("LIVE", false, 1.5)], null)], []);
        var (patternRows, caseRows, comboRows) = FemLoadPlan.RowsFromSap(sapLoading, ["LIVE"], []);
        var plan = FemLoadPlan.Build(patternRows, caseRows, comboRows, sapLoading, planSections, geometry, ["LIVE"], [], ["B"]);
        var (fitLoads, planCases) = plan.ForCandidate([new FemAppliedLoad("B", 0, -30, "LIVE")]);
        var planResults = plan.Complete(new FemSolver().SolveCases(geometry, planSections, new Dictionary<string, FemSectionProperties>(),
            new Dictionary<string, double>(), fitLoads, planCases, ["A", "C"], frameMeshSegments: 4));
        double TotalRz(string name) => planResults[name].SupportReactions.Sum(reaction => reaction.ReactionZKN);
        var deadTotal = weight * spanM + 20 + 4 * spanM / 2;
        Check(Math.Abs(TotalRz("DEAD") - deadTotal) < 1e-6 && Math.Abs(TotalRz("LIVE") - 30) < 1e-6 &&
              Math.Abs(TotalRz("ULS") - (1.2 * deadTotal + 1.5 * 30)) < 1e-6 && fitLoads.IsEmpty &&
              plan.CaseNames.SequenceEqual(["DEAD", "LIVE"]) && plan.CombinationNames.SequenceEqual(["ULS"]),
            "Load plan applies self-weight (γ·A·weight modifier), SAP joint/member loads, replaces Tab 01 loads at applied points, and combines ULS");

        var noneInitial = sapLoading with
        {
            Cases = [new("DEAD", "Nonlinear Static", true, [("DEAD", 1)], "None", null), new("LIVE", "Linear Static", true, [("LIVE", 1)], "Zero", null)]
        };
        var (_, noneCaseRows, _) = FemLoadPlan.RowsFromSap(noneInitial, ["LIVE"], []);
        var nonePlan = FemLoadPlan.Build(patternRows, noneCaseRows, [], noneInitial, planSections, geometry, ["LIVE"], [], ["B"]);
        Check(noneCaseRows.All(row => row.InitialCase == "") && nonePlan.CaseNames.SequenceEqual(["DEAD", "LIVE"]),
            "SAP's 'None' / 'Zero' initial case means the case starts unstressed (not a case called None)");

        var noStudyCases = sapLoading with { Cases = [sapLoading.Cases[0]], Combinations = [] };
        var (_, fallbackCases, _) = FemLoadPlan.RowsFromSap(noStudyCases with { Patterns = [.. noStudyCases.Patterns, new("PT", "Prestress", 0)] }, ["LIVE"], ["PT"]);
        var fallbackPlan = FemLoadPlan.Build(patternRows.Append(new FemPatternRow("PT", "Prestress", 0, "", "")).ToArray(), fallbackCases, [],
            noStudyCases, planSections, geometry, ["LIVE"], ["PT"], ["B"]);
        Check(fallbackCases.Select(row => (row.Name, row.Definition, row.InitialCase, row.Source)).Skip(1).SequenceEqual([
                  (FemLoadPlan.DefaultPretensionCase, "1 PT + 1 DEAD", "", "App"),
                  (FemLoadPlan.DefaultServiceCase, "1 LIVE", FemLoadPlan.DefaultPretensionCase, "App"),
                  (FemLoadPlan.DefaultUltimateCase, "0.35 DEAD + 1.5 LIVE", FemLoadPlan.DefaultPretensionCase, "App")]) &&
              fallbackPlan.FitCase == FemLoadPlan.DefaultPretensionCase &&
              fallbackPlan.ForCandidate([]).Cases.Single(item => item.Name == FemLoadPlan.DefaultServiceCase).PretensionFactor == 1,
            "Without SAP cases for the study patterns, the default staged cases are added: Pretension (PT + self-weight, the fit stage), then SLS and ULS continuing from it");
        // SAP has the pretension case itself, without self-weight: the added cases bring the whole self-weight
        var ownStage = noStudyCases with
        {
            Patterns = [.. noStudyCases.Patterns, new("PT", "Prestress", 0)],
            Cases = [new("Stress", "Nonlinear Static", true, [("PT", 1)], null, null)]
        };
        var (_, ownStageCases, _) = FemLoadPlan.RowsFromSap(ownStage, ["LIVE"], ["PT"]);
        Check(ownStageCases.Select(row => (row.Name, row.Definition, row.InitialCase)).SequenceEqual([
                  ("Stress", "1 PT", ""), (FemLoadPlan.DefaultServiceCase, "1 DEAD + 1 LIVE", "Stress"), (FemLoadPlan.DefaultUltimateCase, "1.35 DEAD + 1.5 LIVE", "Stress")]),
            "…and when SAP's own pretension case has no self-weight, SLS / ULS continue from it with 1.0 / 1.35 of the self-weight");

        DefaultLoadCases(geometry, planSections, weight * spanM);

        var chordGeometry = new GeometryModel(new Dictionary<string, StructuralPoint>(StringComparer.Ordinal)
        {
            ["A"] = new("A", new(0, 0, 0)), ["B"] = new("B", new(spanM / 2, 0, 0)), ["C"] = new("C", new(spanM, 0, 0)),
            ["S"] = new("S", new(spanM / 2, 0, -3)), ["Q"] = new("Q", new(spanM, 0, -3))
        }, [new("Chord", "A", "C", MemberKind.Frame), new("Post", "B", "S", MemberKind.Frame), new("Tie", "C", "Q", MemberKind.Cable)]);
        var chordSections = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal)
        {
            ["Chord"] = section with { MemberName = "Chord" }, ["Post"] = section with { MemberName = "Post" }
        };
        var chord = new FemSolver().Solve(chordGeometry, chordSections,
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
        var slackResult = new FemSolver().Solve(cableGeometry, beamProperties, cableProperties,
            new Dictionary<string, double> { ["Cable-BD"] = 100 }, [new("B", 0, 500)], ["A", "C", "D"]);
        Check(!slackResult.Cables.Single().Active && slackResult.Cables.Single().AxialForceKN == 0,
            "Pretensioned cable switches cleanly to slack under opposing uplift");
        Check(Math.Abs(slackResult.SupportReactions.Sum(reaction => reaction.ReactionZKN) + 500) < 1e-5 &&
              slackResult.NonlinearIterations <= 100,
            "Damped cable equilibrium converges and balances support reactions after cable slackening");

        var targets = new Dictionary<string, double> { ["Cable-BD"] = 100 };
        var unfitted = new FemSolver().Solve(cableGeometry, beamProperties, cableProperties,
            targets, [], ["A", "C", "D"]);
        var fitted = new FemSolver().Solve(cableGeometry, beamProperties, cableProperties,
            targets, [new("B", 0, -50)], ["A", "C", "D"], targetFitPercent: 1);
        var fittedCable = fitted.Cables.Single();
        var fittedUnloaded = new FemSolver().Solve(cableGeometry, beamProperties, cableProperties,
            new Dictionary<string, double> { ["Cable-BD"] = fittedCable.AppliedPretensionKN }, [], ["A", "C", "D"]);
        Check(Math.Abs(unfitted.Cables.Single().AxialForceKN - 100) > 1 &&
              fittedCable.PretensionStageForceKN is double stage && Math.Abs(stage - 100) <= 1 &&
              Math.Abs(fittedUnloaded.Cables.Single().AxialForceKN - stage) < 1e-6 &&
              fittedCable.AxialForceKN > stage && fittedCable.TargetForceKN == 100 &&
              Math.Abs(fittedCable.AppliedPretensionKN - 100) > 1 && fitted.TargetFitIterations is >= 1 and <= 10,
            "Target-force fitting meets the target in the pretension-only stage, then the loads act on the prestressed state");

        Check(fittedCable.FitRatios.Count == fitted.TargetFitIterations && fittedCable.FitRatios[0] < 0.99 &&
              Math.Abs(fittedCable.FitRatios[^1] - 1) <= 0.01 && Math.Abs(fittedCable.FitRatios[^1] * 100 - fittedCable.PretensionStageForceKN!.Value) < 1e-9,
            "Pretension fitting history gives P_result / P_preset per run, ending at the fitted stage force");

        var fittedMeshed = new FemSolver().Solve(cableGeometry, beamProperties, cableProperties,
            targets, [new("B", 0, -50)], ["A", "C", "D"], targetFitPercent: 1, frameMeshSegments: 10);
        Check(fittedMeshed.Frames.Zip(fitted.Frames).All(pair =>
                  Math.Abs(pair.First.MomentStartKNm - pair.Second.MomentStartKNm) < 1e-6 &&
                  Math.Abs(pair.First.MomentEndKNm - pair.Second.MomentEndKNm) < 1e-6 &&
                  Math.Abs(pair.First.ShearEndKN - pair.Second.ShearEndKN) < 1e-6) &&
              Math.Abs(fittedMeshed.Cables.Single().AxialForceKN - fittedCable.AxialForceKN) < 1e-6,
            "Meshed frames give the same end forces and cable forces as unmeshed frames for joint loads");

        var overloaded = new FemSolver().Solve(cableGeometry, beamProperties, cableProperties,
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
        var coupled = new FemSolver().Solve(coupledGeometry, beamProperties, coupledCables,
            new Dictionary<string, double> { ["Cable-BD"] = 100, ["Cable-BE"] = 99.5 }, [], ["A", "C", "D", "E"], targetFitPercent: 1);
        Check(coupled.Cables.All(cable => cable.PretensionStageForceKN is double force && Math.Abs(force - cable.TargetForceKN) <= 0.01 * cable.TargetForceKN) &&
              coupled.TargetFitIterations <= 10,
            "Opposing cables on one joint are fitted together to their separate targets");
        var infeasible = new FemSolver().Solve(coupledGeometry, beamProperties, coupledCables,
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
        var segmentedTaut = new FemSolver().Solve(segmentedGeometry, beamProperties, segmentedCables,
            segmentedTargets, [new("B", 0, -50)], ["A", "C", "D"], targetFitPercent: 1);
        var segmentedSlack = new FemSolver().Solve(segmentedGeometry, beamProperties, segmentedCables,
            segmentedTargets, [new("B", 0, 5000)], ["A", "C", "D"], targetFitPercent: 1);
        Check(segmentedTaut.Cables.All(cable => cable.PretensionStageForceKN is double force && Math.Abs(force - 100) <= 1) &&
              segmentedSlack.Cables.All(cable => !cable.Active) &&
              Math.Abs(segmentedSlack.SupportReactions.Sum(reaction => reaction.ReactionZKN) + 5000) < 1e-3,
            "A cable split at a cable-only joint solves both taut and fully slack without a singular-tangent stall");

        CableSag(section, modulusKNPerM2);
        SapModelTextReader();

        var studyState = CandidateState();
        var service = new FemStudyService();
        var candidates = service.Compose(studyState);
        Check(candidates.Count == 8 && candidates[0].Id == "L01_H01_P01" && candidates[^1].Id == "L02_H02_P02",
            "FEM candidate composer enumerates the full deterministic 2×2×2 Tab 01–03 space");
        Check(candidates[0].Loads.All(load => load.FzKN == -5) && candidates[4].Loads.All(load => load.FzKN == -10),
            "FEM candidate loads reuse the Tab 01 tributary-force calculation");
        // Run FEM Analysis is not that space: one case, the model at its own height, each range at its first value.
        var model = service.ComposeModel(studyState);
        Check(service.CanComposeModel(studyState) && model.Id == FemStudyService.ModelRunId &&
              model.Height is { Height: 5, DeltaH: 0, TopDz: 0, MidDz: 0, BotDz: 0 } && model.Loads.SequenceEqual(candidates[0].Loads) &&
              model.PretensionTargetsKN.Single() is { Key: "Cable-A", Value: 10 },
            "Run FEM Analysis composes one case from the same inputs: the model's own height, qz 1 kPa and 10 kN — the first value of each range");
        // The Tab 02 chord groups and height range belong to the study: without them the model itself still runs.
        studyState.Truss.TopPoints = [];
        studyState.Truss.MinimumHeight = 7;
        var modelProperties = studyState.Baseline.Members.ToDictionary(member => member.Name, member => member.Kind == MemberKind.Cable
            ? new FemSectionProperties(member.Name, 0.01, modulusKNPerM2, null) : section with { MemberName = member.Name }, StringComparer.Ordinal);
        var modelRuns = service.Analyze(studyState.Baseline, [], [], [], ["A", "B"], [service.ComposeModel(studyState)], modelProperties);
        Check(modelRuns.Count == 1 && modelRuns[0].Id == FemStudyService.ModelRunId && modelRuns[0].Result.Joints.Any(joint => joint.UZM < 0),
            "the run of the model solves its own geometry without the Tab 02 chord groups or a valid height range");
        Check(FemStudyService.FindPinSupportPoints(studyState.Baseline).SequenceEqual(["A", "B"]),
              "Automatic pin supports select lowest vertical column ends and ignore diagonal braces");
    }

    /// <summary>
    /// Define → Load Cases → Default Setup: the staging SAP2000 needs for a cable structure, defined by the app from the
    /// self-weight pattern, the Tab 01 load patterns and the Tab 03 target-force pattern.
    /// </summary>
    private static void DefaultLoadCases(GeometryModel geometry, IReadOnlyDictionary<string, FemSectionProperties> sections, double selfWeightKN)
    {
        string[] tab01 = ["DEAD_ABOVE", "LL_ABOVE"], pretension = ["Pretension"];
        FemPatternRow[] model =
        [
            new("DEAD", "Dead", 1, "", "self-weight"), new("DEAD_ABOVE", "Dead", 0, "Tab 01 study loads", ""),
            new("LL_ABOVE", "Live", 0, "Tab 01 study loads", ""), new("Pretension", "Other", 0, "Tab 03 cable target force", "")
        ];
        var (patterns, cases) = FemLoadPlan.DefaultSetup(model, tab01, pretension);
        Check(patterns.SequenceEqual(model) && cases.All(row => row is { Type: "Nonlinear Static", Run: true, Supported: true, Source: "App" }) &&
              cases.Select(row => (row.Name, row.Definition, row.InitialCase)).SequenceEqual([
                  ("Pretension", "1 Pretension + 1 DEAD", ""),
                  ("SLS: 1.0DL+1.0LL", "1 DEAD_ABOVE + 1 LL_ABOVE", "Pretension"),
                  ("ULS: 1.35DL+1.5LL", "0.35 DEAD + 1.35 DEAD_ABOVE + 1.5 LL_ABOVE", "Pretension")]),
            "Default load cases copy the SAP2000 staging: Pretension = target force + self-weight, SLS = + DL + LL and ULS = + 0.35 SW + 1.35 DL + 1.5 LL, both continuing from Pretension");

        // the loads each case ends up with: the stage's self-weight is carried into the cases that continue from it
        var plan = FemLoadPlan.Build(patterns, cases, [], SapFemLoading.Empty, sections, geometry, tab01, pretension, ["B"]);
        var (fitLoads, planCases) = plan.ForCandidate([new FemAppliedLoad("B", 0, -30, "DEAD_ABOVE"), new FemAppliedLoad("B", 0, -10, "LL_ABOVE")]);
        var results = new FemSolver().SolveCases(geometry, sections, new Dictionary<string, FemSectionProperties>(),
            new Dictionary<string, double>(), fitLoads, planCases, ["A", "C"], frameMeshSegments: 4);
        double TotalRz(string name) => results[name].SupportReactions.Sum(reaction => reaction.ReactionZKN);
        Check(plan.FitCase == "Pretension" && plan.FitCases.SequenceEqual(["Pretension"]) && planCases.All(item => item.PretensionFactor == 1) &&
              Math.Abs(TotalRz("Pretension") - selfWeightKN) < 1e-6 && Math.Abs(TotalRz("SLS: 1.0DL+1.0LL") - (selfWeightKN + 30 + 10)) < 1e-6 &&
              Math.Abs(TotalRz("ULS: 1.35DL+1.5LL") - (1.35 * selfWeightKN + 1.35 * 30 + 1.5 * 10)) < 1e-6,
            "…the cable targets are fitted in Pretension under 1.0 SW, SLS carries 1.0 SW + DL + LL and ULS 1.35 SW + 1.35 DL + 1.5 LL, each with the fitted pretension");

        // a model that lists no pattern yet: the Tab 01 / Tab 03 names become patterns, dead or live by name, and SW is added
        var (bare, bareCases) = FemLoadPlan.DefaultSetup([], ["DL_Above", "LL_Above"], ["PT"]);
        Check(bare.Select(row => (row.Name, row.Type, row.SelfWeightMultiplier)).SequenceEqual([("SW", "Dead", 1), ("PT", "Other", 0), ("DL_Above", "Dead", 0), ("LL_Above", "Live", 0)]) &&
              bareCases.Select(row => row.Definition).SequenceEqual(["1 PT + 1 SW", "1 DL_Above + 1 LL_Above", "0.35 SW + 1.35 DL_Above + 1.5 LL_Above"]),
            "Without load patterns the default setup defines them: SW (self-weight × 1), the Tab 03 pattern, and DL_Above / LL_Above factored as dead / live by name");

        // no cables: nothing to pre-stress, so the loaded cases start unstressed with the whole self-weight
        var (_, unstressed) = FemLoadPlan.DefaultSetup(model, tab01, []);
        Check(unstressed.Select(row => (row.Name, row.Definition, row.InitialCase)).SequenceEqual([
                  ("SLS: 1.0DL+1.0LL", "1 DEAD + 1 DEAD_ABOVE + 1 LL_ABOVE", ""), ("ULS: 1.35DL+1.5LL", "1.35 DEAD + 1.35 DEAD_ABOVE + 1.5 LL_ABOVE", "")]),
            "Without a target-force pattern there is no Pretension case: SLS and ULS start unstressed with 1.0 / 1.35 SW");

        // the rows are the app's to edit: a name is checked, and a case cannot continue from itself
        var edited = cases[1];
        edited.Name = "SLS";
        var rejected = 0;
        foreach (var attempt in new Action[] { () => edited.Name = " ", () => edited.Name = "DL + LL", () => edited.InitialCase = "sls" })
            try { attempt(); } catch (ArgumentException) { rejected++; }
        edited.InitialCase = "None";
        Check(edited is { Name: "SLS", InitialCase: "" } && rejected == 3,
            "A load case can be renamed and re-staged; an empty name, a name with ' + ' and a case continuing from itself are refused");
    }

    /// <summary>
    /// Cable sag. A stay runs from joint B on a pinned beam up to a fixed anchor E (10 m, 3-4-5 slope) and carries its
    /// own weight. The beam is linear, so B moves by its flexibility times the forces on it — taken from two
    /// cable-free solves — and the cable tension has to satisfy one scalar equation, solved here by bisection:
    /// <c>T/k − a/T² = P₀/k − (movement of B along the cable)</c>, a = (w·cos θ)²·L³ / 24. The solver must land on it.
    /// </summary>
    private static void CableSag(FemSectionProperties section, double modulusKNPerM2)
    {
        const double length = 10, cos = 0.8, sin = 0.6, area = 0.01, weight = 2;      // m, –, –, m², kN/m
        var axial = modulusKNPerM2 * area / length;
        var geometry = new GeometryModel(new Dictionary<string, StructuralPoint>(StringComparer.Ordinal)
        {
            ["A"] = new("A", new(-5, 0, 0)),
            ["B"] = new("B", new(0, 0, 0)),
            ["C"] = new("C", new(5, 0, 0)),
            ["E"] = new("E", new(8, 0, 6))
        },
        [
            new("AB", "A", "B", MemberKind.Frame),
            new("BC", "B", "C", MemberKind.Frame),
            new("Stay", "B", "E", MemberKind.Cable)
        ]);
        var beams = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal)
        {
            ["AB"] = section with { MemberName = "AB" },
            ["BC"] = section with { MemberName = "BC" }
        };
        var stay = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal) { ["Stay"] = new("Stay", area, modulusKNPerM2, null) };

        // how far B moves along the cable per unit force on it, from the beam alone
        var beamOnly = new GeometryModel(geometry.Points.Where(point => point.Key != "E").ToDictionary(point => point.Key, point => point.Value, StringComparer.Ordinal),
            geometry.Members.Where(member => member.Kind == MemberKind.Frame).ToArray());
        double Along(double fx, double fz)
        {
            var joint = new FemSolver().Solve(beamOnly, beams, new Dictionary<string, FemSectionProperties>(), new Dictionary<string, double>(),
                [new("B", fx, fz)], ["A", "C"]).Joints.Single(item => item.PointName == "B");
            return cos * joint.UXM + sin * joint.UZM;
        }
        double alongX = Along(1, 0), alongZ = Along(0, 1), alongCable = cos * alongX + sin * alongZ;

        double Reference(double pretension, double cableWeight, bool sag, double px = 0, double pz = 0)
        {
            var normal = cableWeight * cos;
            var a = sag ? normal * normal * length * length * length / 24 : 0;
            // B carries the cable pull, the lumped half of the cable weight and its own joint load
            var other = px * alongX + (pz - cableWeight * length / 2) * alongZ;
            if (a == 0) return Math.Max(0, (pretension / axial - other) / (1 / axial + alongCable));
            double Unbalance(double t) => t / axial + t * alongCable - a / (t * t) - pretension / axial + other;
            double low = 1e-9, high = 1e7;
            for (var step = 0; step < 200; step++)
            {
                var mid = (low + high) / 2;
                if (Unbalance(mid) > 0) high = mid; else low = mid;
            }
            return (low + high) / 2;
        }
        FemLoadSet Loads(double cableWeight, double px = 0, double pz = 0) => new(
            px == 0 && pz == 0 ? [] : [new FemJointLoad("B", px, pz)],
            cableWeight == 0 ? [] : [new FemMemberLoad("Stay", FemLoadDirection.Gravity, 0, 1, cableWeight, cableWeight)], []);
        FemAnalysisResult Solve(double pretension, double cableWeight, bool sag, double px = 0, double pz = 0, double? fit = null) =>
            new FemSolver().SolveCases(geometry, beams, stay, new Dictionary<string, double> { ["Stay"] = pretension },
                Loads(cableWeight), [new FemCaseLoads("Case", Loads(cableWeight, px, pz), 1)], ["A", "C", "E"], default, fit,
                FemSolver.DefaultTargetFitIterations, 1, sag)["Case"];
        bool Same(double value, double expected) => Math.Abs(value - expected) <= 1e-6 * Math.Max(100, Math.Abs(expected));

        // a taut stay (about 3,000 kN once the beam has given way under it): the sag is a small correction
        const double tautPretension = 30000;
        var tautStraight = Solve(tautPretension, weight, sag: false).Cables.Single();
        var taut = Solve(tautPretension, weight, sag: true).Cables.Single();
        Check(Same(tautStraight.AxialForceKN, Reference(tautPretension, weight, false)) && tautStraight.AxialForceKN > 2000 && tautStraight.SagM is null && tautStraight.TangentStiffnessRatio is null,
            "Without cable sag a stay carrying its own weight is the straight tension-only bar, weight lumped to its ends");
        Check(Same(taut.AxialForceKN, Reference(tautPretension, weight, true)) && taut.AxialForceKN > tautStraight.AxialForceKN &&
              taut.AxialForceKN < 1.001 * tautStraight.AxialForceKN,
            "With cable sag a taut stay satisfies T/k − a/T² = extension + P₀/k; the tension is within 0.1 % of the straight cable's, above it");
        var normalLoad = weight * cos;
        var ernst = 1 / (1 + normalLoad * normalLoad * length * length * modulusKNPerM2 * area / (12 * Math.Pow(taut.AxialForceKN, 3)));
        Check(taut.SagM is double tautSag && Math.Abs(tautSag - normalLoad * length * length / (8 * taut.AxialForceKN)) < 1e-12 &&
              taut.TangentStiffnessRatio is double tautRatio && Math.Abs(tautRatio - ernst) < 1e-12 && tautRatio is > 0.99 and < 1,
            "A sagging cable reports its mid-span sag w·cos θ·L² / 8T and Ernst's stiffness ratio 1 / (1 + (w·cos θ·L)²·EA / 12T³)");

        // a lightly tensioned stay (about 200 kN as a straight cable): the sag governs
        var lightStraight = Solve(2000, weight, sag: false).Cables.Single();
        var light = Solve(2000, weight, sag: true).Cables.Single();
        Check(Same(light.AxialForceKN, Reference(2000, weight, true)) && light.AxialForceKN > 1.1 * lightStraight.AxialForceKN &&
              light.TangentStiffnessRatio is < 0.5 && light.SagM is > 0.05,
            "A lightly tensioned stay sags visibly, keeps a fraction of EA / L and carries noticeably more than the straight cable — on the same force law");

        // a stay that a straight cable model would call slack
        var slackStraight = Solve(20, weight, sag: false, pz: 300);
        var slack = Solve(20, weight, sag: true, pz: 300);
        Check(!slackStraight.Cables.Single().Active && slackStraight.Cables.Single().AxialForceKN == 0 &&
              slack.Cables.Single().Active && Same(slack.Cables.Single().AxialForceKN, Reference(20, weight, true, pz: 300)) &&
              slack.Cables.Single().AxialForceKN > 10,
            "Where the straight cable goes slack, the sagging cable keeps the tension its weight needs to hang across the shortened chord");
        Check(Math.Abs(slack.SupportReactions.Sum(reaction => reaction.ReactionZKN) + 300 - weight * length) < 1e-5 &&
              Math.Abs(slack.SupportReactions.Sum(reaction => reaction.ReactionXKN)) < 1e-5 && slack.NonlinearIterations <= 100,
            "The sagging-cable equilibrium converges and the support reactions balance the joint load and the cable weight");

        // nothing on the cable: nothing changes
        var weightless = Solve(tautPretension, 0, sag: true);
        Check(weightless.Cables.Single().AxialForceKN == Solve(tautPretension, 0, sag: false).Cables.Single().AxialForceKN && weightless.Cables.Single().SagM is null,
            "Cable sag changes nothing for a cable the case puts no load on");
        var vertical = new GeometryModel(new Dictionary<string, StructuralPoint>(geometry.Points, StringComparer.Ordinal) { ["D"] = new("D", new(0, 0, 5)) },
            [.. geometry.Members.Where(member => member.Kind == MemberKind.Frame), new("Hanger", "B", "D", MemberKind.Cable)]);
        var hanger = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal) { ["Hanger"] = new("Hanger", area, modulusKNPerM2, null) };
        var hangerLoads = new FemLoadSet([], [new FemMemberLoad("Hanger", FemLoadDirection.Gravity, 0, 1, weight, weight)], []);
        FemCableResponse Hanger(bool sag) => new FemSolver().SolveCases(vertical, beams, hanger, new Dictionary<string, double> { ["Hanger"] = 100 },
            FemLoadSet.Empty, [new FemCaseLoads("Case", hangerLoads, 1)], ["A", "C", "D"], cableSag: sag)["Case"].Cables.Single();
        Check(Hanger(true).SagM is null && Hanger(true).AxialForceKN == Hanger(false).AxialForceKN,
            "A vertical cable does not sag under its own weight: only the load normal to the chord counts");

        // Target Force with sag: the pretension is fitted to the target under the stage loads, cable weight included
        var fitted = Solve(1500, weight, sag: true, pz: -100, fit: 1).Cables.Single();
        var fittedStraight = Solve(1500, weight, sag: false, pz: -100, fit: 1).Cables.Single();
        Check(fitted.PretensionStageForceKN is double stage && Math.Abs(stage - 1500) <= 15 && fitted.TargetForceKN == 1500 &&
              Same(fitted.AxialForceKN, Reference(fitted.AppliedPretensionKN, weight, true, pz: -100)) &&
              fitted.AppliedPretensionKN < fittedStraight.AppliedPretensionKN,
            "Target-force fitting works on the sagging cable: the stage tension meets the target with less lack of fit than a straight cable needs");
        Check(FemResultCombiner.Combine([(Solve(tautPretension, weight, sag: true), 1.35)]).Cables.Single() is { SagM: null, TangentStiffnessRatio: null } combined &&
              Math.Abs(combined.AxialForceKN - 1.35 * taut.AxialForceKN) < 1e-9 &&
              FemCableFitRow.FromRun(new FemStudyRun("L01_H01_P01", "L01", "H01", "P01", Solve(tautPretension, weight, sag: true))).Single() is { SagMm: > 0, StiffnessPercent: > 99 and < 100 },
            "A linear combination of cases carries no sag; the cable table shows sag in mm and the stiffness kept in % for a solved case");

        // end to end, as the FEM tab runs it: SAP's DEAD pattern (self-weight × 1) → the load plan puts γ·A on the cable → it sags
        var planSections = new Dictionary<string, FemSectionProperties>(beams, StringComparer.Ordinal)
        {
            ["Stay"] = stay["Stay"] with { UnitWeightKNPerM3 = weight / area }
        };
        var sapLoading = new SapFemLoading([new("DEAD", "Dead", 1)], [], [], [], [],
            [new("DEAD", "Linear Static", true, [("DEAD", 1)], null, null)], [], []);
        var (patternRows, caseRows, comboRows) = FemLoadPlan.RowsFromSap(sapLoading, [], []);
        var plan = FemLoadPlan.Build(patternRows, caseRows, comboRows, sapLoading, planSections, geometry, [], [], []);
        var (planFitLoads, planCases) = plan.ForCandidate([]);
        FemCableResponse Dead(bool sag) => plan.Complete(new FemSolver().SolveCases(geometry, beams, stay, new Dictionary<string, double> { ["Stay"] = 0 },
            planFitLoads, planCases, ["A", "C", "E"], cableSag: sag))["DEAD"].Cables.Single();
        Check(Same(Dead(true).AxialForceKN, Reference(0, weight, true)) && Dead(true).SagM is > 0 &&
              Same(Dead(false).AxialForceKN, Reference(0, weight, false)) && Dead(false).SagM is null &&
              Dead(true).AxialForceKN > 2 * Dead(false).AxialForceKN,
            "Through the load plan, a SAP self-weight pattern makes the cable sag: a stay with no pretension hangs under its own weight");

        // Pretension stages. SAP2000 meets a Target Force at the end of the case that applies the target-force pattern, so
        // a model with two such cases has two stages, and a case that continues from one of them carries that one's
        // pretension. Here: PT alone (for reference), PT with self-weight, and a loaded case continuing from the latter.
        var staged = new SapFemLoading([new("DEAD", "Dead", 1), new("PT", "Other", 0), new("LIVE", "Live", 0)],
            [new("LIVE", new FemJointLoad("B", 0, -100))], [], [], [],
            [
                new("PT only", "Nonlinear Static", true, [("PT", 1)], null, null),
                new("Pretension", "Nonlinear Static", true, [("PT", 1), ("DEAD", 1)], null, null),
                new("Staged", "Nonlinear Static", true, [("LIVE", 1)], "Pretension", null),
            ], [], []);
        var (stagePatterns, stageCases, stageCombinations) = FemLoadPlan.RowsFromSap(staged, [], ["PT"]);
        var stagePlan = FemLoadPlan.Build(stagePatterns, stageCases, stageCombinations, staged, planSections, geometry, [], ["PT"], []);
        var stages = stagePlan.StagesFor([]);
        Check(stagePlan.FitCases.SequenceEqual(["PT only", "Pretension"]) && stages.Count == 2 &&
              stages[0].FitCase == "PT only" && stages[0].FitLoads.IsEmpty && stages[0].Cases.Select(item => item.Name).SequenceEqual(["PT only"]) &&
              stages[1].FitCase == "Pretension" && stages[1].FitLoads.Members.Count == 1 && stages[1].Cases.Select(item => item.Name).SequenceEqual(["Pretension", "Staged"]) &&
              stages[1].Cases.All(item => item.PretensionFactor == 1),
            "Each case that applies the target-force pattern is a pretension stage of its own; a case continuing from one belongs to that stage");
        var stageTargets = new Dictionary<string, double> { ["Stay"] = 1500 };
        var stageResults = stages.SelectMany(stage => new FemSolver().SolveCases(geometry, beams, planSections.Where(pair => pair.Key == "Stay").ToDictionary(pair => pair.Key, pair => pair.Value),
            stageTargets, stage.FitLoads, stage.Cases, ["A", "C", "E"], default, 0.01, cableSag: true)).ToDictionary(pair => pair.Key, pair => pair.Value.Cables.Single());
        Check(Math.Abs(stageResults["Pretension"].AxialForceKN - 1500) <= 0.15 + 1e-9 && Math.Abs(stageResults["PT only"].AxialForceKN - 1500) <= 0.15 + 1e-9 &&
              stageResults["Staged"].AppliedPretensionKN == stageResults["Pretension"].AppliedPretensionKN &&
              Math.Abs(stageResults["Pretension"].AppliedPretensionKN - stageResults["PT only"].AppliedPretensionKN) > 1 &&
              stageResults["Staged"].AxialForceKN > stageResults["Pretension"].AxialForceKN,
            "Both stages meet the target under their own loads — with different pretensions — and the staged case continues with the pretension of the case it continues from");
    }

    /// <summary>The reader of SAP2000's model text, on the model of the FEM-against-SAP2000 benchmark (<c>--fem-sap</c>
    /// runs the comparison itself).</summary>
    private static void SapModelTextReader()
    {
        var path = System.IO.Path.Combine(FemSapBenchmark.DefaultFolder, FemSapBenchmark.ModelFile);
        if (!System.IO.File.Exists(path)) throw new System.IO.FileNotFoundException("Run the checks from the repository root: the benchmark model text was not found.", path);
        var model = SapModelText.Read(path);
        var geometry = model.Geometry();
        var sections = model.SectionProperties();
        var loading = model.Loading();
        Check(geometry.Points.Count == 41 && geometry.Members.Count(member => member.Kind == MemberKind.Frame) == 38 &&
              geometry.Members.Count(member => member.Kind == MemberKind.Cable) == 16 && sections.Count == 54 &&
              model.PinSupports().SequenceEqual(["198", "199", "200", "205"]),
            "SAP2000 model text: 41 joints, 38 frames, 16 cables, every member with its section, and the four pinned supports");
        var cable = sections["Cable1a-2"];
        var rotated = sections["188"];
        Check(cable.IsCable && Math.Abs(cable.AreaM2 - 0.028802317797652074) < 1e-15 && Math.Abs(cable.ElasticModulusKNPerM2 - 196500599.8511997) < 1e-3 &&
              Math.Abs(cable.WeightPerLengthKNPerM - 0.028802317797652074 * 76.97286394226478) < 1e-9 &&
              rotated.LocalAngleDeg == 90 && rotated.InPlaneInertiaM4 is double inertia && Math.Abs(inertia - 6.215005792682927) < 1e-9,
            "SAP2000 model text: cable area, modulus and weight from its section and material; a mega column turned 90° bends about its other axis (I22, not I33)");
        // concrete-filled steel box: 0.87 m² of steel and 1.53 m² of concrete, reported by SAP2000 as 5.986 m² of concrete
        var chord = sections.Values.First(item => item.SectionName!.StartsWith("CFT_SHS", StringComparison.Ordinal));
        Check(Math.Abs(chord.AreaM2 - 5.98609756097561) < 1e-12 && Math.Abs(chord.WeightPerLengthKNPerM - (0.87 * 76.97286394226478 + 1.53 * 24.992617659999993)) < 1e-6 &&
              model.Notes.Count(note => note.StartsWith("Composite Section Designer section", StringComparison.Ordinal)) == 3,
            "SAP2000 model text: a composite Section Designer section weighs what its shapes weigh (105.2 kN/m), not concrete × its transformed area (149.6 kN/m)");
        var stagedCase = loading.Cases.Single(item => item.Name == "NL ULS001: 1.35DL+1.5LL-staged");
        Check(loading.Patterns.Select(pattern => (pattern.Name, pattern.SelfWeightMultiplier)).SequenceEqual([("DEAD", 1), ("DEAD_ABOVE", 0), ("LL_ABOVE", 0), ("Pretension", 0)]) &&
              stagedCase.InitialCase == "Pretension" && stagedCase.Loads.SequenceEqual([("DEAD_ABOVE", 1.35), ("LL_ABOVE", 1.5), ("DEAD", 0.35)]) &&
              loading.Cases.Single(item => item.Name == "Pretension").Loads.SequenceEqual([("Pretension", 1), ("DEAD", 1)]) &&
              loading.Cases.Single(item => item.Name == "MODAL").Supported == false && loading.JointLoads.Count == 22 &&
              model.GeometricNonlinearity["Pretension"] == "Large Displ" && model.TargetForceTolerancePercent["Pretension"] == 1,
            "SAP2000 model text: load patterns, joint loads and static cases — the staged cases continue from 'Pretension', which applies the target force with self-weight");
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
