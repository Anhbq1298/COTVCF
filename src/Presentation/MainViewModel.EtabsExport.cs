using COT_VCF.Application;
using COT_VCF.Application.Etabs;
using COT_VCF.Domain;
using COT_VCF.Geometry;

namespace COT_VCF.Presentation;

/// <summary>The ETABS model an export targets and what the export would change there (nothing written yet).</summary>
public sealed record EtabsExportPreview(EtabsExportModel Model, EtabsExportReport Plan);

// ---- File → Export to ETABS: the Mcotis model written into the model open in ETABS, ready to run there ----
// What Export to SAP2000 writes, plus every load Mcotis holds (an ETABS target may be empty): the joint loads as Run
// Analysis applies them and the frame loads. A read-only preview comes first; the export does not save the ETABS model.
public sealed partial class MainViewModel
{
    public bool CanExportToEtabs => CanExportToSap;

    /// <summary>The Mcotis model as ETABS receives it.</summary>
    internal EtabsExportModel BuildEtabsExportModel()
    {
        var model = BuildSapExportModel();
        var points = State.Geometry.Points;
        var members = State.Geometry.Members.Select(member => member.Name).ToHashSet(StringComparer.Ordinal);
        // joint loads as Run Analysis applies them: a Tab 01 pattern's loads at the loaded joints give way to the Tab 01 loads
        var replaced = model.Tab01Points.Count == 0 ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : model.JointLoads.Select(load => load.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loaded = model.Tab01Points.ToHashSet(StringComparer.Ordinal);
        var jointLoads = femSapLoading.JointLoads
            .Where(item => points.ContainsKey(item.Load.PointName) && !(replaced.Contains(item.Pattern) && loaded.Contains(item.Load.PointName)))
            .Concat(model.JointLoads.Select(load => new SapPatternJointLoad(load.Pattern, new FemJointLoad(load.PointName, 0, load.FzKN))))
            .ToArray();
        // weight and mass per metre of each section as the FEM members carry them (before member modifiers)
        var weights = FemSectionProperties.Where(property => property.SectionName is not null && property.GrossAreaM2 > 0)
            .GroupBy(property => property.SectionName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (group.First().UnitWeightKNPerM3 * group.First().GrossAreaM2, group.First().UnitMassTPerM3 * group.First().GrossAreaM2),
                StringComparer.OrdinalIgnoreCase);
        return new EtabsExportModel(model, jointLoads,
            femSapLoading.MemberLoads.Where(item => members.Contains(item.Load.MemberName)).ToArray(),
            femSapLoading.MemberPointLoads.Where(item => members.Contains(item.Load.MemberName)).ToArray(),
            femSapLoading.GravityLoads.Count(item => members.Contains(item.MemberName)), weights);
    }

    /// <summary>Attaches to the model open in ETABS and reports what the export would change there. Writes nothing.</summary>
    internal async Task<EtabsExportPreview> PreviewEtabsExportAsync()
    {
        if (IsBusy) throw new InvalidOperationException("Another operation is running; try again when it has finished.");
        var gateway = Etabs ?? throw new InvalidOperationException("ETABS is not available in this session.");
        IsBusy = true;
        try
        {
            Message = "Checking the export against the model open in ETABS (read-only)…";
            var model = BuildEtabsExportModel();
            var plan = await gateway.PreviewExportAsync(model);
            Message = "Export to ETABS: review the changes and confirm.";
            return new EtabsExportPreview(model, plan);
        }
        finally { IsBusy = false; Update(); }
    }

    /// <summary>Writes the previewed export into the ETABS model (unlocking it when it is locked). Not saved.</summary>
    internal async Task<EtabsExportReport> ExportToEtabsAsync(EtabsExportPreview preview)
    {
        if (IsBusy) throw new InvalidOperationException("Another operation is running; try again when it has finished.");
        var gateway = Etabs ?? throw new InvalidOperationException("ETABS is not available in this session.");
        IsBusy = true;
        try
        {
            Message = "Exporting the model to ETABS…";
            var report = await gateway.ExportModelAsync(preview.Model, preview.Plan.Target, unlock: preview.Plan.Target.IsLocked);
            Message = $"Exported to ETABS ({report.Target.Name}) — review and save it there, then run the analysis.";
            return report;
        }
        finally { IsBusy = false; Update(); }
    }
}
