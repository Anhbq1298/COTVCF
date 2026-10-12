using System.IO;
using COT_VCF.Application;
using COT_VCF.Application.Etabs;
using COT_VCF.Configuration;
using COT_VCF.Domain;
using COT_VCF.Geometry;

namespace COT_VCF.Presentation;

/// <summary>What File → Import From ETABS brought in, and whether Run Analysis can start.</summary>
public sealed record EtabsImportSummary(bool ReadyToRun, IReadOnlyList<string> Lines);

// ---- File → Import From ETABS: the model open in ETABS, complete, as an offline Mcotis model ready to run ----
// The running ETABS exports its database tables (EtabsGateway; read-only), EtabsTableReader reads them and EtabsImport
// turns them into Mcotis's geometry (joints label@story), material / section libraries (CFT and steel encased sections
// in Mcotis's own composite form, Section Designer sections as general sections with ETABS's properties and their true
// self-weight), member properties, loading, load cases and combinations. Tab 01 is fitted to the ETABS joint loads as
// for Import From SAP2000. Without ETABS running, an .e2k file ETABS wrote can be imported instead (EtabsE2kReader).
// ETABS is never written; the model becomes an offline model, like Load Config.
public sealed partial class MainViewModel
{
    /// <summary>The running ETABS; set by the app (null in checks that do not need it).</summary>
    internal IEtabsGateway? Etabs { get; set; }
    public bool CanImportFromEtabs => !IsBusy;

    internal async Task<EtabsImportSummary> ImportFromEtabsAsync()
    {
        if (IsBusy) throw new InvalidOperationException("Another operation is running; try again when it has finished.");
        var gateway = Etabs ?? throw new InvalidOperationException("ETABS is not available in this session.");
        EtabsModelExport export;
        IsBusy = true;
        try
        {
            Message = "Reading the model open in ETABS…";
            export = await gateway.ExportActiveModelAsync();
        }
        finally { IsBusy = false; }
        var name = export.FilePath.Length > 0 ? Path.GetFileName(export.FilePath) : "ETABS (Untitled)";
        var model = EtabsTableReader.Read(export.TablesText, export.FilePath.Length > 0 ? export.FilePath : name);
        var info = new ModelInfo(export.FilePath, export.ProgramVersion, 6, "kN", "m", false);
        return ImportEtabsModel(model, info, name, $"read from the running {export.ProgramVersion}");
    }

    /// <summary>The same import from an .e2k file ETABS wrote (File → Export → ETABS .e2k Text File).</summary>
    internal EtabsImportSummary ImportFromE2kFile(string path)
    {
        if (IsBusy) throw new InvalidOperationException("Another operation is running; try again when it has finished.");
        var full = Path.GetFullPath(path);
        var model = EtabsE2kReader.Read(File.ReadAllText(full), full);
        var info = new ModelInfo(full, model.ProgramVersion, 6, "kN", "m", false);
        return ImportEtabsModel(model, info, Path.GetFileName(full), "from the .e2k file");
    }

    internal EtabsImportSummary ImportEtabsModel(EtabsModel etabsModel, ModelInfo info, string modelName, string source)
    {
        if (IsBusy) throw new InvalidOperationException("Another operation is running; try again when it has finished.");
        // everything that can refuse the model does so before the current model is touched
        var imported = EtabsImport.Convert(etabsModel);
        var geometry = imported.Geometry;
        var lines = new List<string>();
        IsBusy = true;
        try
        {
            femWorkingSaveTimer?.Stop();
            if (IsFemModelLocked) DeleteRegisteredFemOutputs();
            femSessionStore.ClearActive();
            applyingFemDatabase = true;
            applyingProject = true;
            ClearFemResults();
            State.Connection = "Offline model";
            State.Model = info;
            State.SessionBaselineModel = info;
            State.SessionBaseline = geometry;
            State.SessionBaselinePointLoads = [];
            State.SessionBaselinePretensionForces = new Dictionary<string, double>(StringComparer.Ordinal);
            State.Geometry = geometry;
            State.Baseline = geometry;
            State.Preview = geometry;
            State.LoadPatterns.Clear();
            foreach (var pattern in imported.Loading.Patterns) State.LoadPatterns.Add(pattern.Name);
            new ConfigurationApplicator(State, workflow).Reset();
            config.Session.Close();
            config.Session.ClearDirty();

            // libraries, then each member's properties from its library section (as Define → Section Properties does)
            Materials.Clear();
            foreach (var material in imported.Materials) Materials.Add(MaterialCosts.WithDefaultCost(material));
            FrameSections.Clear();
            foreach (var section in imported.Sections)
                FrameSections.Add(section.IsComposite && AnalyseComposite(section) is { } analysis ? section with { Given = analysis.Values } : section);
            CableSections.Clear();
            TendonSections.Clear();
            var library = FrameSections.ToDictionary(section => section.Name, StringComparer.Ordinal);
            FemSectionProperties.Clear();
            foreach (var member in imported.Members)
            {
                var property = PropertiesFor(member, library[member.SectionName!]);
                // a Section Designer composite weighs each shape with its own material, not its base material × transformed area
                if (imported.SectionWeights.TryGetValue(member.SectionName!, out var weight) && property.GrossAreaM2 > 0)
                    property = property with
                    {
                        UnitWeightKNPerM3 = weight.WeightPerLengthKNPerM / property.GrossAreaM2,
                        UnitMassTPerM3 = weight.MassPerLengthTPerM / property.GrossAreaM2,
                    };
                FemSectionProperties.Add(property);
            }
            femSapLoading = imported.Loading;
            femInputsOutdated = false;
            offlineLoadCaseNames = femSapLoading.Cases.Select(item => item.Name).ToArray();
            offlineModelPath = $"{modelName} · ETABS";
            isDefaultModel = false;
            blankModelChoiceDismissed = true;
            femInputSnapshot = null;
            femInputDatabasePath = null;
            femWorkingDatabasePath = null;
            femOutputRegistrations.Clear();
            femWorkingDirty = false;
            SelectedAppliedPoint = null;
            SelectedCable = null;

            var frames = geometry.Members.Count;
            var levels = geometry.Points.Values.Select(point => Math.Round(point.Position.Z, 3)).Distinct().Count();
            lines.Add($"Model: {modelName} ({info.Version}), {source} · {geometry.Points.Count} joints on {levels} levels, {frames} frames");
            var composite = FrameSections.Count(section => section.IsComposite);
            var general = FrameSections.Count(section => section.Shape == FrameShape.General);
            lines.Add($"Libraries: {Materials.Count} materials, {FrameSections.Count} frame sections" +
                      (composite + general > 0 ? $" ({composite} CFT / steel encased, {general} Section Designer / general)" : ""));
            lines.Add($"Loading: {femSapLoading.Patterns.Count} load patterns ({femSapLoading.JointLoads.Count} joint, " +
                      $"{femSapLoading.MemberLoads.Count + femSapLoading.MemberPointLoads.Count} frame loads), " +
                      $"{femSapLoading.Cases.Count(item => item.Supported)} static load cases" +
                      (femSapLoading.Cases.Any(item => !item.Supported) ? $" (+{femSapLoading.Cases.Count(item => !item.Supported)} not static, kept off)" : "") +
                      $", {femSapLoading.Combinations.Count} combinations");

            // Tab 02: a truss's chord groups when the model has them; Run FEM Analysis does not need them
            var chords = VcfChordDetection.Detect(geometry);
            if (chords is not null)
            {
                State.Truss.TopPoints = chords.Top;
                State.Truss.MiddlePoints = chords.Middle;
                State.Truss.BottomPoints = chords.Bottom;
                try
                {
                    workflow.CaptureBaseline();
                    lines.Add($"Tab 02: chords from {chords.Source} — truss height {State.Truss.BaselineHeight:0.###} m captured");
                }
                catch (InvalidOperationException ex) { lines.Add($"Tab 02: chord groups found but the baseline was not captured — {ex.Message}"); }
            }
            else lines.Add("Tab 02: no truss chord groups (not needed to run; set them on 02 Truss Height for the truss-height study)");
            lines.Add(geometry.Members.Any(member => member.Kind == MemberKind.Cable)
                ? "Tab 03: ETABS has no cable target forces — add the cable targets on 03 Pretension to run"
                : "Tab 03: no cables — the model runs without a pretension stage");
            lines.Add(ImportTab01(chords?.Top ?? HighestLevelJoints(geometry), "ETABS"));
        }
        finally
        {
            applyingProject = false;
            applyingFemDatabase = false;
            IsBusy = false;
        }

        ResetFemLoadRows();
        FemLoadWarnings.Clear();
        foreach (var warning in femSapLoading.Warnings) FemLoadWarnings.Add(warning);
        Notify(nameof(FemLoadSummary));
        RegenerateStudyPreview();
        SelectedTab = TabFem;
        SelectedFemPage = FemInputsPage;

        // supports: Mcotis pins the lowest column bases; say whether that is what ETABS restrains
        try
        {
            var pins = FemStudyService.FindPinSupportPoints(geometry);
            var etabsSupports = imported.SupportedJoints;
            lines.Add(pins.ToHashSet(StringComparer.Ordinal).SetEquals(etabsSupports)
                ? $"Supports: pins at {string.Join(", ", pins)} — the joints ETABS restrains"
                : $"Supports: Mcotis pins the column bases {string.Join(", ", pins)}; ETABS restrains " +
                  (etabsSupports.Count == 0 ? "no joint in X and Z" : string.Join(", ", etabsSupports.Take(6)) + (etabsSupports.Count > 6 ? " …" : "")) +
                  " — check the supports before relying on the results");
            if (imported.FixedJoints.Count > 0)
                lines.Add($"Supports: ETABS also fixes the rotation at {string.Join(", ", imported.FixedJoints.Take(6))}; Mcotis pins (rotation free)");
        }
        catch (InvalidOperationException ex) { lines.Add("Supports: " + ex.Message); }
        foreach (var note in imported.Notes.Take(10)) lines.Add("Note: " + note);
        if (imported.Notes.Count > 10) lines.Add($"Note: … and {imported.Notes.Count - 10} more.");
        if (femSapLoading.Warnings.Count > 0) lines.Add($"Loads: {femSapLoading.Warnings.Count} load assignment warning(s) — see Define → Load Patterns");

        string? blocker = null;
        try
        {
            femStudy.ComposeModel(State, IsOfflineModel);
            HeldFemProperties();
        }
        catch (InvalidOperationException ex) { blocker = ex.Message; }
        var ready = blocker is null && HasWorkingModel;
        lines.Add(ready ? "Ready: Run → Run FEM Analysis solves the imported model." : $"Not ready to run yet: {blocker ?? "no working model"}");
        FemProgressText = $"Imported {modelName} from ETABS · {geometry.Members.Count} frames, {femSapLoading.Cases.Count(item => item.Supported)} static load cases, " +
                          $"{femSapLoading.Combinations.Count} combinations." + (ready ? " Run Analysis solves it without SAP2000 or ETABS." : "");
        Message = ready ? $"Imported {modelName} from ETABS — ready to run (ETABS unchanged)."
                        : $"Imported {modelName} from ETABS — see the import summary for what is still needed.";
        StartNewMcotisConfigurationFile();
        Notify(nameof(SharedViewerSpanPoints));
        Notify(nameof(SharedViewerMemberSizes));
        Notify(nameof(SharedViewerSections));
        Notify(nameof(SharedViewerPinSupportPoints));
        Notify(nameof(FemMemberSizeHint));
        Update();
        return new EtabsImportSummary(ready, lines);
    }

    /// <summary>The joints of the highest level that horizontal frames connect, along X — the deck of a model without truss
    /// chords, where Tab 01 places its loads.</summary>
    private static IReadOnlyList<string> HighestLevelJoints(GeometryModel geometry)
    {
        var onLevels = geometry.Members.Where(member => member.Kind == MemberKind.Frame &&
                Math.Abs(geometry.Points[member.Start].Position.Z - geometry.Points[member.End].Position.Z) <= 0.001)
            .SelectMany(member => new[] { member.Start, member.End }).Distinct(StringComparer.Ordinal)
            .Select(name => geometry.Points[name]).ToArray();
        if (onLevels.Length == 0) return [];
        var top = onLevels.Max(point => point.Position.Z);
        return onLevels.Where(point => point.Position.Z >= top - 0.001).OrderBy(point => point.Position.X).Select(point => point.Name).ToArray();
    }
}
