using Mcotis.Desktop.Application;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Presentation;

/// <summary>The SAP2000 model an export targets and what the export would change there (nothing written yet).</summary>
public sealed record SapExportPreview(ModelInfo Target, SapExportModel Model, SapExportReport Plan);

// ---- File → Export to SAP2000: the Mcotis model written into the open SAP2000 model, ready to run there ----
// Geometry, member sections / local axes / releases / modifiers, materials and sections, supports, load patterns, the Tab 01
// joint loads and Tab 03 cable target forces of Run Analysis, static load cases (with run flags) and linear additive
// combinations. A preview (read-only) comes first; the export does not save the SAP2000 model.
public sealed partial class MainViewModel
{
    public bool CanExportToSap => !IsBusy && HasModelGeometry && FemSectionProperties.Count > 0;

    /// <summary>A section as SAP2000 / ETABS receive it: a mega composite column becomes a rectangular RC column of the same outline and
    /// concrete, a concrete filled tube a steel tube of the same outline and wall, each with modifiers that carry the composite's
    /// stiffness, weight and mass.</summary>
    private FrameSectionProperty SapFrameSection(FrameSectionProperty section) => section.Shape switch
    {
        FrameShape.MegaComposite when AnalyseComposite(section) is { } composite =>
            CompositeSapSection.Rectangle(section, composite.Values, composite.WeightPerMetre, composite.EquivalentUnitMass * composite.Values.Area, composite.Concrete),
        FrameShape.FilledTube when AnalyseComposite(section) is { } composite =>
            CompositeSapSection.Tube(section, composite.Parts, composite.Values, composite.Ecm, composite.WeightPerMetre,
                composite.EquivalentUnitMass * composite.Values.Area, composite.Steel),
        _ => section,
    };

    /// <summary>The Mcotis model as SAP2000 receives it.</summary>
    internal SapExportModel BuildSapExportModel()
    {
        EnsureMaterials();
        EnsureSections();
        // Tab 01 loads and Tab 03 targets as Run Analysis applies them (first value of each range)
        FemStudyCandidate? run = null;
        try { run = femStudy.ComposeModel(State, offlineModel: true); }
        catch (InvalidOperationException) { }
        var cablePatterns = State.Cables.ToDictionary(cable => cable.CableName, cable => cable.LoadPattern, StringComparer.Ordinal);
        var jointLoads = run?.Loads.Where(load => !string.IsNullOrWhiteSpace(load.Pattern))
            .Select(load => new SapExportJointLoad(load.PointName, load.Pattern!, load.FzKN)).ToArray() ?? [];
        var targets = run?.PretensionTargetsKN.Where(target => cablePatterns.ContainsKey(target.Key))
            .Select(target => new SapExportCableTarget(target.Key, cablePatterns[target.Key], target.Value)).ToArray() ?? [];
        // the restraints of the model as it stands — the ones Run FEM Analysis holds
        var restraints = FemJointRestraints;
        return new SapExportModel(
            State.Geometry,
            FemSectionProperties.ToArray(),
            Materials.ToArray(),
            FrameSections.Select(SapFrameSection).ToArray(),
            CableSections.ToArray(),
            FemPinSupportPoints,
            restraints,
            HasAssignedJointRestraints,
            FemActiveDofs,
            FemPatternRows.Select(row => new SapExportPattern(row.Name, row.Type, row.SelfWeightMultiplier)).ToArray(),
            run is null ? [] : State.AppliedPoints.Select(point => point.PointName).ToArray(),
            jointLoads,
            targets,
            FemCaseRows.Where(row => row.Supported).Select(row => new SapExportCase(row.Name, row.Type,
                FemLoadDefinition.Parse(row.Definition).Select(term => (term.Name, term.Factor)).ToArray(), row.InitialCase, row.Run)).ToArray(),
            FemCombinationRows.Where(row => row.Supported).Select(row => new SapExportCombination(row.Name,
                FemLoadDefinition.Parse(row.Definition).Select(term => (term.Name, term.Factor)).ToArray())).ToArray());
    }

    /// <summary>Attaches to the model open in SAP2000 and reports what the export would change there. Writes nothing.</summary>
    internal async Task<SapExportPreview> PreviewSapExportAsync()
    {
        if (IsBusy) throw new InvalidOperationException("Another operation is running; try again when it has finished.");
        IsBusy = true;
        try
        {
            Message = "Checking the export against the model open in SAP2000 (read-only)…";
            var model = BuildSapExportModel();
            var target = await workflow.PeekActiveModelAsync();
            var plan = await workflow.ExportToSapAsync(model, target, unlock: false, preview: true);
            Message = "Export to SAP2000: review the changes and confirm.";
            return new SapExportPreview(target, model, plan);
        }
        catch (System.Runtime.InteropServices.COMException) when (State.IsSapConnected)
        {
            State.Connection = "Connection Lost";
            throw;
        }
        finally { IsBusy = false; Update(); }
    }

    /// <summary>Writes the previewed export into the SAP2000 model (unlocking it when it is locked). Not saved.</summary>
    internal async Task<SapExportReport> ExportToSapAsync(SapExportPreview preview)
    {
        if (IsBusy) throw new InvalidOperationException("Another operation is running; try again when it has finished.");
        IsBusy = true;
        try
        {
            Message = "Exporting the model to SAP2000…";
            var report = await workflow.ExportToSapAsync(preview.Model, preview.Target, unlock: preview.Target.IsLocked);
            Message = $"Exported to SAP2000 ({System.IO.Path.GetFileName(preview.Target.Path)}) — review and save it there, then run the analysis.";
            return report;
        }
        catch (System.Runtime.InteropServices.COMException) when (State.IsSapConnected)
        {
            State.Connection = "Connection Lost";
            throw;
        }
        finally { IsBusy = false; Update(); }
    }

    /// <summary>Optimization → Compare with SAP2000: the member forces of the model open in SAP2000 for a load case. Read only.</summary>
    internal async Task<IReadOnlyList<SapMemberPeak>> ReadSapMemberPeaksAsync(string loadCase)
    {
        try { return await workflow.ReadSapMemberPeaksAsync(State.Geometry, loadCase); }
        catch (System.Runtime.InteropServices.COMException) when (State.IsSapConnected)
        {
            State.Connection = "Connection Lost";
            throw;
        }
    }
}
