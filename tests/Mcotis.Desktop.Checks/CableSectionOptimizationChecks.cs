using Mcotis.Core;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Design;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Presentation;
using NativeTargetForce = Mcotis.Core.CableTargetForce;
using System.IO;

internal static class CableSectionOptimizationChecks
{
    public static void Run()
    {
        CheckSettingsAndNearZeroHandling();
        CheckSharedSectionGrouping();
        CheckRunAndApply();
        CheckStaySystems();
    }

    /// <summary>Each stay system is analysed and applied with its own material: A × E = the catalogue EA and A × γ = the catalogue mass of
    /// every unit, the section named [brand]_[model]_[units]x[unit] and read back, Iterate's trial = the applied model.</summary>
    private static void CheckStaySystems()
    {
        Check(TensionCableCatalogue.All.All(unit => Math.Abs(unit.SteelAreaMm2 * unit.ModulusMpa / 1000 - unit.AxialStiffnessKN) <= unit.AxialStiffnessKN * 1e-12 &&
                                                    TensionCableCatalogue.Material(unit) is var material &&
                                                    Math.Abs(unit.SteelAreaMm2 * 1e-6 * material.ElasticModulus - unit.AxialStiffnessKN) <= unit.AxialStiffnessKN * 1e-12 &&
                                                    Math.Abs(unit.SteelAreaMm2 * 1e-6 * material.MassPerVolume * 1000 - unit.MassKgPerM) <= unit.MassKgPerM * 1e-12) &&
              TensionCableCatalogue.Find(CableSystem.FatzerFlc, "Ø100") is { SteelAreaMm2: 6806.25, ModulusMpa: 160_000 } &&
              TensionCableCatalogue.Find(CableSystem.VslSsi2000, "6-187") is { ModulusMpa: 195_000 } vsl && Math.Abs(vsl.DensityKgPerM3 - 1.172 / 150e-6) < 1e-6,
            "every catalogue unit's material gives A × E = its catalogue EA and A × γ = its catalogue mass: strand 195 GPa, FATZER rope 160 GPa on EA ÷ E (Ø100: 6,806 mm²)");

        var names = TensionCableCatalogue.All.Select(unit => TensionCableCatalogue.SectionName(unit, 3)).ToArray();
        Check(names.Contains("VSL_SSI2000_3x6-187") && names.Contains("FATZER_FLC_3xD100") && names.Contains("UTRACON_UPS_3x37T15.7") &&
              TensionCableCatalogue.All.All(unit => TensionCableCatalogue.ParseSection(TensionCableCatalogue.SectionName(unit, 3)) == (unit, 3)) &&
              TensionCableCatalogue.ParseSection("FATZER_FLC_2xD100 (2)") is { Units: 2, Unit.Code: "Ø100" } &&
              TensionCableCatalogue.ParseSection("748T15.7") is null && TensionCableCatalogue.ParseSection("VSL_SSI2000_2x6-188") is null &&
              ResultReportTables.CableCapacityKN("FATZER_FLC_2xD100", 2 * 6806.25e-6) == 2 * 10_100 * .5 &&
              ResultReportTables.CableCapacityKN("VSL_SSI2000_4x6-187", 748 * 150e-6) is { } strands && Math.Abs(strands - 748 * 279 * .5) < 1e-6,
            "stay sections are named [brand]_[model]_[units]x[unit] and read back; a FATZER rope's capacity is its units × Fmin × f, a strand stay's its area × 1,860 MPa × f");

        foreach (var system in new[] { CableSystem.FatzerFlc, CableSystem.UtraconPt })
        {
            var viewModel = EtabsImportChecks.NewViewModel(out _);
            viewModel.OpenNativeDocument(CreateDocument(twoCables: false), "Stay system check", null, LegacyNativeDocumentMigration.Source.Imported("stay system check"));
            var document = viewModel.NativeDocumentSession.Document;
            var snapshot = NativeToLegacyProjection.Read(document) ?? throw new Exception("FAIL: the stay check model has no configuration snapshot");
            var settings = CableSectionOptimizationSettings.Default with { TargetUr = 1, BreakingStressMpa = 1860, PreliminaryCoefficient = .5, StayBasis = TensionCableBasis.Default, StaySystem = system };
            var run = CableSectionOptimizer.Run(document, snapshot, settings);
            var row = run.Rows.Single();
            var unit = TensionCableCatalogue.Units(system)[0];
            Check(run.Failure is null && run.Converged && row.Suggested == CableSize.Of(unit, 1) && row.AxialStiffnessKN == unit.AxialStiffnessKN &&
                  Math.Abs(row.CapacityKN - unit.BreakingLoadKN * .5) < 1e-9 && run.Iterations[^1].TrialSizes![row.GroupKey] == row.Suggested &&
                  new TensionCableDesignWindow.IterateRow(row).Status == (system == CableSystem.UtraconPt ? "CHECK" : "OK"),
                $"Iterate with {TensionCableCatalogue.ShortTitle(system)} sizes the 50 kN cable as 1 × {unit.Name} (EA {unit.AxialStiffnessKN / 1000:#,0} MN, capacity {unit.BreakingLoadKN * .5:#,0} kN) and converges on it" +
                (system == CableSystem.UtraconPt ? "; the unqualified Utracon tendon reads CHECK" : ""));

            Check(viewModel.ApplyCableSectionOptimization(run, confirmedUnconverged: false), $"{TensionCableCatalogue.ShortTitle(system)}: Apply assigns the suggested stay");
            var applied = viewModel.NativeDocumentSession.Document;
            var cable = applied.Cables.Values.Single();
            var section = applied.CableSections[applied.AssignmentOfCable(cable.Id).SectionId!.Value];
            var material = applied.Materials[section.MaterialId];
            var property = viewModel.FemSectionProperties.Single(item => item.IsCable);
            Check(property.SectionName == TensionCableCatalogue.SectionName(unit, 1) && property.MaterialName == unit.MaterialName &&
                  Math.Abs(section.Area * material.ElasticModulus - unit.AxialStiffnessKN) <= unit.AxialStiffnessKN * 1e-9 &&
                  Math.Abs(section.Area * material.WeightDensity - unit.MassKgPerM * MaterialProperty.Gravity / 1000) <= unit.MassKgPerM * 1e-9 &&
                  viewModel.CableStayStiffnessMatches(property.MemberName),
                $"Apply names the section {property.SectionName} in material {property.MaterialName}; the analysed model has A × E = {unit.AxialStiffnessKN / 1000:#,0} MN and the catalogue {unit.MassKgPerM} kg/m");

            // the trial Iterate converged on is the applied model: iterating it again analyses it unchanged, with the same deflections
            var again = CableSectionOptimizer.Run(applied, NativeToLegacyProjection.Read(applied)!, settings with { MaximumIterations = 1 });
            var trial = run.Iterations[^1].PretensionDeflections;
            var reanalysed = again.Iterations[0].PretensionDeflections;
            Check(again.Rows.Single() is { Changes: false } && trial.Count == reanalysed.Count && trial.Zip(reanalysed).All(pair =>
                      Math.Abs(pair.First.UXM - pair.Second.UXM) + Math.Abs(pair.First.UYM - pair.Second.UYM) + Math.Abs(pair.First.UZM - pair.Second.UZM) <= 1e-12),
                $"{TensionCableCatalogue.ShortTitle(system)}: the applied section is read back as the same assembly, and the applied model deflects as Iterate's trial did (same EA and mass)");
        }
    }

    private static void CheckSettingsAndNearZeroHandling()
    {
        var settings = CableSectionOptimizationSettings.Default;
        Check(settings is { TargetUr: 0.90, BreakingStressMpa: 2160, PreliminaryCoefficient: 0.50,
                  MaximumIterations: 30, ConvergenceTolerancePercent: 1.00 } &&
              Math.Abs(settings.StrandCapacityKN - 162) < 1e-12 && settings.SuggestedStrands(50) == 1 &&
              settings.SuggestedStrands(150) == 2 && Math.Abs(settings.Utilisation(145.8, 1) - .9) < 1e-12,
            "15.7 mm strand sizing keeps the approved defaults, 162 kN capacity per strand, and rounds the suggested count up to meet target UR");

        var force = CableSectionOptimizer.MaximumRelativeChange(
            new Dictionary<string, double> { ["cable"] = 0 }, new Dictionary<string, double> { ["cable"] = 5e-10 },
            null, forceFloorKN: true);
        var deflection = CableSectionOptimizer.MaximumRelativeChange(
            new Dictionary<string, double> { ["joint UX"] = 0 }, new Dictionary<string, double> { ["joint UX"] = 5e-10 },
            null, forceFloorKN: false);
        Check(force.Value is { } forceValue && double.IsFinite(forceValue) && Math.Abs(forceValue - .05) < 1e-9 &&
              deflection.Value is { } deflectionValue && double.IsFinite(deflectionValue) && Math.Abs(deflectionValue - 50) < 1e-9,
            "near-zero force and deflection convergence use fixed numerical floors, keeping the relative-change values finite and reproducible");
    }

    private static void CheckSharedSectionGrouping()
    {
        var document = CreateDocument(twoCables: true);
        var plan = CableSectionOptimizer.Prepare(document);
        Check(plan.Groups.Count == 1 && plan.Groups[0].CableNames.SequenceEqual(["T1", "T2"]) &&
              plan.Groups[0].CurrentStrands == 10 && plan.FitCaseNames.SequenceEqual(["Pretension"]),
            "cables assigned the same initial native section are one strand-count group, while target-force fitting cases are retained in model order");

        var separate = CreateDocument(twoCables: true, separateSections: true);
        var independent = CableSectionOptimizer.Prepare(separate);
        var synced = CableSectionOptimizer.Prepare(separate, new FemInputSnapshot
        {
            CableSetup = new() { Cables = [new() { CableName = "T2", SyncMode = CableSyncMode.SameStep, ReferenceCableName = "T1" }] },
        });
        Check(independent.Groups.Count == 2 && synced.Groups.Count == 1 && synced.Groups[0].CableNames.SequenceEqual(["T1", "T2"]) &&
              synced.Groups[0].CurrentStrands == 12 && synced.Groups[0].SectionName == "10T15.7 / 12T15.7",
            "a Cable Force Sync follower and its reference with different sections form one group that starts from the larger strand count");
        var sharedOnly = CableSectionOptimizer.Prepare(document, null, syncGroupsOnly: true);
        var sharedSynced = CableSectionOptimizer.Prepare(document, new FemInputSnapshot
        {
            CableSetup = new() { Cables = [new() { CableName = "T2", SyncMode = CableSyncMode.SameStep, ReferenceCableName = "T1" }] },
        }, syncGroupsOnly: true);
        Check(sharedOnly.Groups.Count == 2 && sharedSynced.Groups.Count == 1,
            "grouped by Sync only, two cables sharing a section are sized apart unless one follows the other");
    }

    private static void CheckRunAndApply()
    {
        var document = CreateDocument(twoCables: false);
        var viewModel = EtabsImportChecks.NewViewModel(out _);
        viewModel.OpenNativeDocument(document, "Cable optimizer check", null,
            LegacyNativeDocumentMigration.Source.Imported("native optimizer check"));
        var currentDocument = viewModel.NativeDocumentSession.Document;
        var snapshot = NativeToLegacyProjection.Read(currentDocument)
            ?? throw new Exception("FAIL: the imported native model has no configuration snapshot");
        var cableId = currentDocument.Cables.Values.Single().Id;
        var originalSectionId = currentDocument.AssignmentOfCable(cableId).SectionId
            ?? throw new Exception("FAIL: the optimizer fixture cable has no section assignment");
        var originalSection = currentDocument.CableSections[originalSectionId];
        var originalTarget = currentDocument.Loads.Values.OfType<NativeTargetForce>().Single();
        var settings = CableSectionOptimizationSettings.Default;
        var run = CableSectionOptimizer.Run(currentDocument, snapshot, settings);

        Check(run.IterationCount >= 3 && run.IterationCount <= settings.MaximumIterations && run.Rows.Count == 1 &&
              run.Rows[0] is { TendonName: "T1", CurrentStrands: 10, SuggestedStrands: 1 } &&
              run.Rows[0].TargetForceKN == originalTarget.Force && Math.Abs(run.Rows[0].CalculatedForceKN - originalTarget.Force) < 1e-8 &&
              Math.Abs(run.Rows[0].CurrentUr - originalTarget.Force / (10 * settings.StrandCapacityKN)) < 1e-10 &&
              Math.Abs(run.Rows[0].SuggestedUr - originalTarget.Force / settings.StrandCapacityKN) < 1e-10 &&
              currentDocument.CableSections[originalSectionId] == originalSection,
            "the review keeps target and calculated force distinct, recalculates UR, and leaves the initial section untouched while iterating");

        Check(run.Iterations.SelectMany(iteration => iteration.CableForces).All(sample => sample.TargetForceKN == originalTarget.Force) &&
              run.Iterations.All(iteration => iteration.PretensionDeflections.Count > 0 &&
                  iteration.PretensionDeflections.All(sample => sample.CaseName == "Pretension")) &&
              run.Converged == (run.MaximumForceChangePercent is { } maxForce && maxForce <= settings.ConvergenceTolerancePercent &&
                                run.MaximumDeflectionChangePercent is { } maxDeflection && maxDeflection <= settings.ConvergenceTolerancePercent) &&
              (!run.Converged || run.Iterations[^1].TrialStrandCounts[run.Rows[0].GroupKey] == run.Rows[0].SuggestedStrands),
            "convergence requires both the maximum cable-force and consistent pretension-stage joint-deflection changes to pass, with original target forces unchanged");

        Check(!CableSectionOptimizer.MeetsDualConvergence(false, .5, 1.01, 1) &&
              !CableSectionOptimizer.MeetsDualConvergence(false, 1.01, .5, 1) &&
              !CableSectionOptimizer.MeetsDualConvergence(true, .5, .5, 1) &&
              CableSectionOptimizer.MeetsDualConvergence(false, 1, 1, 1),
            "neither force nor pretension deflection can pass alone, pending strand-count changes block convergence, and both values at tolerance pass");

        var vslSettings = settings with { TargetUr = 1, BreakingStressMpa = 1860, PreliminaryCoefficient = .5, StayBasis = TensionCableBasis.Default };
        var vsl = CableSectionOptimizer.Run(currentDocument, snapshot, vslSettings);
        Check(vsl.Failure is null && vsl.Rows.Single() is { SuggestedStrands: 12, Product: "1 × VSL SSI 2000 6-12", CostSgd: > 0 } &&
              Math.Abs(vslSettings.StrandCapacityKN - TensionCableCatalogue.StrandBreakingKN * .5) < 1e-9,
            "with a VSL stay basis a 50 kN tendon rounds up to the smallest standard unit, 1 × VSL SSI 2000 6-12 (12 × Ø15.7, 279 kN × 0.5 per strand), with its indicative cost");
        var needsAnalysis = false;
        try { CableSectionOptimizer.Run(currentDocument, snapshot, vslSettings with { DesignCases = ["Pretension"] }); }
        catch (ArgumentException) { needsAnalysis = true; }
        Check(needsAnalysis, "sizing for design load cases is refused without the model's analysis of each candidate");

        var onePass = CableSectionOptimizer.Run(currentDocument, snapshot, settings with { MaximumIterations = 1 });
        Check(onePass.IterationCount == 1 && !onePass.Converged && onePass.Rows.Single().Status == "Maximum iterations" &&
              onePass.MaximumForceChangePercent is null && onePass.MaximumDeflectionChangePercent is null,
            "a one-pass limit cannot report convergence because consecutive force and deflection results do not exist");
        var confirmationRequired = false;
        try { viewModel.ApplyCableSectionOptimization(onePass, confirmedUnconverged: false); }
        catch (InvalidOperationException) { confirmationRequired = true; }
        Check(confirmationRequired && viewModel.NativeDocumentSession.Document.CableSections[originalSectionId] == originalSection,
            "an unconverged suggestion cannot be applied until the caller has confirmed it explicitly");

        var folder = Path.Combine(Path.GetTempPath(), "mcotis-cable-optimization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var modelPath = Path.Combine(folder, "Cable test" + McotisModelFile.Extension);
            viewModel.SaveFemModelFileAsync(modelPath).GetAwaiter().GetResult();
            run = Task.Run(() => viewModel.RunCableSectionOptimizationAsync(settings, null, CancellationToken.None))
                .GetAwaiter().GetResult();
            var restored = McotisModelFile.ReadBeside<CableSectionOptimizationRun>(modelPath, CableSectionOptimizationRun.FileName);
            Check(restored is not null && restored.Iterations.Count == run.Iterations.Count &&
                  restored.Iterations.SelectMany(iteration => iteration.CableForces).All(sample => sample.TargetForceKN == originalTarget.Force) &&
                  restored.Rows.SequenceEqual(run.Rows),
                "the complete target/calculated force and convergence history is saved beside a rooted native model and can be reopened");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        viewModel.ToggleFemModelLock.Execute(null);
        var lockedBeforeApply = viewModel.IsFemModelLocked;
        var applied = viewModel.ApplyCableSectionOptimization(run, confirmedUnconverged: !run.Converged);
        Check(lockedBeforeApply && applied && !viewModel.IsFemModelLocked,
            "Apply Suggested Tendon on a locked model unlocks it first, clearing that revision's results, and then applies the sections");
        var after = viewModel.NativeDocumentSession.Document;
        var targetAfter = after.Loads.Values.OfType<NativeTargetForce>().Single();
        var assigned = after.AssignmentOfCable(cableId).SectionId;
        var section = assigned is { } sectionId ? after.CableSections[sectionId] : null;
        Check(applied && targetAfter == originalTarget && section is { Area: 150e-6 } && viewModel.CanUndoModel,
            "Apply Suggested Tendon changes only the strand section, preserves the exact target-force load, and creates one undoable document edit");

        Check(viewModel.UndoModel() && viewModel.NativeDocumentSession.Document.Loads.Values.OfType<NativeTargetForce>().Single() == originalTarget &&
              viewModel.NativeDocumentSession.Document.AssignmentOfCable(cableId).SectionId == originalSectionId &&
              viewModel.NativeDocumentSession.Document.CableSections[originalSectionId] == originalSection,
            "one Undo restores the original shared section and target-force assignment after applying the tendon suggestion");
    }

    private static ModelDocument CreateDocument(bool twoCables, bool separateSections = false)
    {
        var editor = ModelDocument.Create().Edit();
        var (steel, strand, beam, cableSection) = (ObjectId.New(), ObjectId.New(), ObjectId.New(), ObjectId.New());
        var prestress = ObjectId.New();
        editor.PutMaterial(new(steel, "Steel", MaterialType.Steel, 2e8, .3, 0, 0));
        editor.PutMaterial(new(strand, "Strand", MaterialType.Tendon, 1.6e8, .3, 0, 0));
        editor.PutFrameSection(new(beam, "Beam", steel, FrameSectionShape.General, new(.01, .0008, .0004, .00001)));
        editor.PutCableSection(new(cableSection, "10T15.7", strand, 1500e-6));
        var root = editor.AddPoint(new(0, 0, 0), "Root").Id;
        var tip = editor.AddPoint(new(4, 0, 0), "Tip").Id;
        var anchor = editor.AddPoint(new(4, 0, 3), "Anchor").Id;
        editor.SetRestraint([root], Restraint.Fixed);
        editor.SetRestraint([anchor], Restraint.Pinned);
        editor.SetFrameSection([editor.AddFrame(root, tip, "Arm").Id], beam);
        var first = editor.AddCable(tip, anchor, "T1").Id;
        editor.SetCableSection([first], cableSection);
        if (twoCables)
        {
            var second = editor.AddCable(tip, anchor, "T2").Id;
            var secondSection = cableSection;
            if (separateSections)
            {
                secondSection = ObjectId.New();
                editor.PutCableSection(new(secondSection, "12T15.7", strand, 1800e-6));
            }
            editor.SetCableSection([second], secondSection);
        }
        editor.PutLoadPattern(new(prestress, "PT", LoadPatternType.Prestress));
        editor.PutLoad(new NativeTargetForce(ObjectId.New(), prestress, first, 50));
        var fit = ObjectId.New();
        editor.PutLoadCase(new(fit, "Pretension", LoadCaseType.NonlinearStatic, [new(prestress, 1)]) { FitCableTargetForces = true });
        editor.SetSettings(new AnalysisSettings(1e-6, 100, 1, false));
        return editor.Commit().Document;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }
}
