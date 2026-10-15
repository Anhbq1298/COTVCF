using Mcotis.Desktop.Application;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Presentation;

// ---- One window (M8 of the main-window migration): a native document becomes the main window's model ----
// Edit → TemplateBuilding… generates its building into the main window, and File → Open… opens a .mcotis of the native
// workspace there. The document is taken in as Import From ETABS takes a model (NativeDocumentImport reads it; what the
// main window's model cannot hold yet is refused with the reasons before anything changes), and the model it gives is
// solved as the document was: the same objects by name, the same values, the same degrees of freedom.
public sealed partial class MainViewModel
{
    /// <summary>While a native document is taken in: the identity its model keeps (the document's own).</summary>
    private Guid? importedIdentity;

    /// <summary>Puts <paramref name="document"/> in the main window as its model. <paramref name="path"/>: the .mcotis file
    /// it came from, which Save then overwrites; null for a model just generated (TemplateBuilding), which Save asks a file
    /// for. Throws <see cref="InvalidOperationException"/> with every reason when the main window cannot hold the model yet;
    /// the open model is then unchanged.</summary>
    internal NativeImportModel OpenNativeDocument(Mcotis.Core.ModelDocument document, string displayName, string? path,
        LegacyNativeDocumentMigration.Source source)
    {
        if (IsBusy) throw new InvalidOperationException("Another operation is running; try again when it has finished.");
        var imported = NativeDocumentImport.Read(document);
        IsBusy = true;
        try
        {
            femWorkingSaveTimer?.Stop();
            if (IsFemModelLocked) DeleteRegisteredFemOutputs();
            femSessionStore.ClearActive();
            applyingFemDatabase = true;
            applyingProject = true;
            ClearFemResults();
            var info = new ModelInfo(path ?? "", "Mcotis native model", 6, "kN", "m", false);
            State.Connection = "Offline model";
            State.Model = info;
            State.SessionBaselineModel = info;
            State.SessionBaseline = imported.Geometry;
            State.SessionBaselinePointLoads = [];
            State.SessionBaselinePretensionForces = new Dictionary<string, double>(StringComparer.Ordinal);
            State.JointRestraints = imported.Restraints;
            State.JointMasses = imported.JointMasses;
            State.Geometry = imported.Geometry;
            State.Baseline = imported.Geometry;
            State.Preview = imported.Geometry;
            State.LoadPatterns.Clear();
            foreach (var pattern in imported.Loading.Patterns) State.LoadPatterns.Add(pattern.Name);
            new ConfigurationApplicator(State, workflow).Reset();
            config.Session.Close();
            config.Session.ClearDirty();
            // the cable target forces: each cable's pretension row, a fixed target in its pattern
            foreach (var (cable, pattern, force) in imported.CableTargets)
            {
                var section = imported.Members.First(member => member.MemberName == cable).SectionName;
                State.Cables.Add(new CablePretensionConfig
                {
                    CableName = cable, LoadPattern = pattern, TargetForce = force, SectionName = section,
                    SectionAreaM2 = imported.CableSections.FirstOrDefault(item => item.Name == section)?.Area ?? 0,
                });
            }

            // libraries, then each member's properties from its library section, as an import works them out
            Materials.Clear();
            foreach (var material in imported.Materials) Materials.Add(MaterialCosts.WithDefaultCost(material));
            FrameSections.Clear();
            foreach (var section in imported.FrameSections) FrameSections.Add(section);
            CableSections.Clear();
            foreach (var section in imported.CableSections) CableSections.Add(section);
            TendonSections.Clear();
            var frames = FrameSections.ToDictionary(section => section.Name, StringComparer.Ordinal);
            var strands = CableSections.ToDictionary(section => section.Name, StringComparer.Ordinal);
            FemSectionProperties.Clear();
            foreach (var member in imported.Members)
            {
                var property = member.IsCable ? PropertiesFor(member, strands[member.SectionName!]) : PropertiesFor(member, frames[member.SectionName!]);
                // the mass density the document states, not weight / g
                if (imported.MassDensities.TryGetValue(member.MemberName, out var density)) property = property with { UnitMassTPerM3 = density };
                FemSectionProperties.Add(property);
            }

            femSapLoading = imported.Loading;
            femInputsOutdated = false;
            var settings = imported.Settings;
            FemTargetFitPercent = settings.CableFitTolerancePercent;
            FemTargetFitIterations = settings.CableFitMaxIterations;
            FemFrameMeshSegments = settings.FrameMeshSegments;
            FemCableSag = settings.CableSag;
            (femActiveDofs, femCableDofs) = (settings.ActiveDofs, settings.CableDofs);
            offlineLoadCaseNames = imported.Cases.Select(item => item.Name).ToArray();
            offlineModelPath = displayName;
            isDefaultModel = false;
            blankModelChoiceDismissed = true;
            femInputSnapshot = null;
            femInputDatabasePath = null;
            femWorkingDatabasePath = null;
            femOutputRegistrations.Clear();
            femWorkingDirty = false;
            SelectedAppliedPoint = null;
            SelectedCable = null;

            // the load cases and combinations as the document defines them: their kinds, what each continues from or
            // takes its stiffness from, their mass sources and modes, whether they run
            var setups = imported.Cases.ToDictionary(item => item.Name, StringComparer.Ordinal);
            var patterns = imported.Loading.Patterns.Select(pattern => new FemPatternRow(pattern.Name, pattern.Type, pattern.SelfWeightMultiplier, "",
                Loads(imported.Loading, pattern))).ToArray();
            var cases = imported.Loading.Cases.Select(item =>
            {
                var setup = setups[item.Name];
                return new FemCaseRow(item.Name, item.Type, item.Loads.Count > 0 ? FemLoadDefinition.Format(item.Loads) : "", item.InitialCase,
                    setup.Run, true, "Native", "") { Options = setup.Options, CableTargetForces = setup.FitCableTargetForces };
            }).ToArray();
            var combinations = imported.Loading.Combinations.Select(item => new FemCombinationRow(item.Name, item.Type,
                FemLoadDefinition.Format(item.Items.Select(term => (term.Name, term.Factor))), imported.CombinationRuns.GetValueOrDefault(item.Name, true), true, "Native", "")).ToArray();
            SetFemLoadRows(patterns, cases, combinations);
            // SetFemLoadRows clears the previous model's mass sources; install the imported identities afterward
            // so a modal case still refers to the source its native document actually contains.
            femMassSources = imported.MassSources.ToList();
            SelectedFemResultName = FemCombinationRows.FirstOrDefault(row => row.Run)?.Name ?? FemLoadSelectionNames.LastOrDefault();

            // Tab 01 holds the storey loads of a parametric system, which every run of this window reads. A model with
            // loads of its own is given none — 0 kPa on its top joints in a pattern without joint loads there, as Import
            // From ETABS does when nothing fits — so the run takes the model's loads exactly as the document has them.
            State.AppliedPoints.Clear();
            State.LoadDefinitions.Clear();
            var top = HighestLevelJoints(State.Geometry);
            var loaded = imported.Loading.JointLoads.Where(item => top.Contains(item.Load.PointName)).Select(item => item.Pattern).ToHashSet(StringComparer.Ordinal);
            if (top.Count >= 2 && imported.Loading.Patterns.FirstOrDefault(pattern => !loaded.Contains(pattern.Name)) is { } placeholder)
            {
                workflow.AddAppliedPoints(top);
                State.LoadDefinitions.Add(new LoadDefinition { LoadName = placeholder.Name, Qz = 0 });
            }
        }
        finally
        {
            applyingProject = false;
            applyingFemDatabase = false;
            IsBusy = false;
        }

        FemLoadWarnings.Clear();
        Notify(nameof(FemLoadSummary));
        RegenerateStudyPreview();
        SelectedTab = TabFem;
        SelectedFemPage = FemInputsPage;
        // the model keeps the document's identity; its restraints are the document's — none is added for it. A model
        // without members has no document to make: the window then holds an empty one, never the previous model's.
        ClearNativeDocument();
        importedIdentity = document.DocumentId.Value;
        // the document's grids and levels come with it (the window's configuration has no place for them)
        carriedReferenceGeometry = document.ReferenceGeometry.IsEmpty ? null : document.ReferenceGeometry;
        try { StartNewMcotisConfigurationFile(source, generator: null, migrateRestraints: false); }
        finally { importedIdentity = null; carriedReferenceGeometry = null; }
        if (path is not null && McotisModelFile.IsModelFile(path)) femInputDatabasePath = path;
        RestoreOptimizationStudy(null, NativeDocumentSession.Document, "");
        FemProgressText = $"Opened {displayName} · {imported.Geometry.Points.Count} joints, {State.Geometry.Members.Count(member => member.Kind == MemberKind.Frame)} frames, " +
            $"{State.Geometry.Members.Count(member => member.Kind != MemberKind.Frame)} cables, {imported.Cases.Count} load cases, {imported.Loading.Combinations.Count} combinations · " +
            $"degrees of freedom {DofText(imported.Settings.ActiveDofs)}." + string.Concat(imported.Notes.Select(note => " " + note));
        Message = NativeDocumentProblem is { } problem ? $"Opened {displayName}, but it has no native document: {problem}" : FemProgressText;
        Notify(nameof(SharedViewerSpanPoints));
        Notify(nameof(SharedViewerMemberSizes));
        Notify(nameof(SharedViewerSections));
        Notify(nameof(SharedViewerPinSupportPoints));
        Notify(nameof(FemMemberSizeHint));
        Update();
        return imported;

        static string DofText(Mcotis.Core.DofMask dofs) => dofs == Mcotis.Core.DofMask.Full3D ? "Full 3D" : dofs.ToString();
    }

    /// <summary>What a load pattern of the model carries, for the Load Patterns table.</summary>
    private static string Loads(SapFemLoading loading, SapLoadPattern pattern)
    {
        var joints = loading.JointLoads.Count(load => load.Pattern == pattern.Name);
        var members = loading.MemberLoads.Count(load => load.Pattern == pattern.Name) + loading.MemberPointLoads.Count(load => load.Pattern == pattern.Name);
        var parts = new List<string>();
        if (pattern.SelfWeightMultiplier != 0) parts.Add($"self-weight × {pattern.SelfWeightMultiplier:G6}");
        if (joints > 0) parts.Add($"{joints} joint load{(joints == 1 ? "" : "s")}");
        if (members > 0) parts.Add($"{members} frame load{(members == 1 ? "" : "s")}");
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }
}
