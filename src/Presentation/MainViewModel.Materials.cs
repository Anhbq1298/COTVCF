using COT_VCF.Domain;
using COT_VCF.Geometry;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace COT_VCF.Presentation;

// ---- Define → Materials: the material library of the working model ----
// Get From SAP2000 reads the connected model's materials (read-only). Without it the library starts from the materials the
// member properties name. A change made here is applied to the FEM member properties of the members that use the material
// (E, unit weight, unit mass); SAP2000 is never written.
public sealed partial class MainViewModel
{
    public ObservableCollection<MaterialProperty> Materials { get; } = [];

    /// <summary>Members whose FEM properties use <paramref name="material"/>.</summary>
    public int MaterialUsage(string material) =>
        FemSectionProperties.Count(property => string.Equals(property.MaterialName, material, StringComparison.Ordinal));

    /// <summary>Starts the library from the member properties when it is empty: one material per material name, with the
    /// E and unit weight the members carry; the type is guessed (cables → tendon, C… → concrete, S… → steel).</summary>
    internal void EnsureMaterials()
    {
        if (Materials.Count > 0) return;
        foreach (var group in FemSectionProperties.Where(property => !string.IsNullOrWhiteSpace(property.MaterialName))
                     .GroupBy(property => property.MaterialName!, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var first = group.First();
            var kind = group.Any(property => property.IsCable) ? MaterialKind.Tendon
                : Regex.IsMatch(group.Key, @"^C\d") ? MaterialKind.Concrete
                : Regex.IsMatch(group.Key, @"^S\d") ? MaterialKind.Steel : MaterialKind.Other;
            Materials.Add(MaterialProperty.Default(kind, group.Key) with
            {
                Grade = kind is MaterialKind.Concrete or MaterialKind.Steel ? group.Key : "",
                WeightPerVolume = first.UnitWeightKNPerM3,
                ElasticModulus = first.ElasticModulusKNPerM2,
                // the member properties carry no strengths — leave them blank rather than show a template's values
                Fc = null, ExpectedFc = null, Fy = null, Fu = null, ExpectedFy = null, ExpectedFu = null,
                Notes = "From the model's member properties (E, unit weight) — Get From SAP2000 for the full material data.",
                Source = "Model",
            });
        }
    }

    /// <summary>Define → Materials → Get From SAP2000: every material of the connected model.</summary>
    internal async Task<IReadOnlyList<MaterialProperty>> ReadMaterialsFromSapAsync()
    {
        if (IsBusy) throw new InvalidOperationException("Another operation is running; try again when it has finished.");
        IsBusy = true;
        try { return await workflow.ReadMaterialsAsync(); }
        catch (System.Runtime.InteropServices.COMException) when (State.IsSapConnected)
        {
            State.Connection = "Connection Lost";
            throw;
        }
        finally { IsBusy = false; Update(); }
    }

    /// <summary>Define Materials → OK: the library becomes <paramref name="materials"/>; <paramref name="renamed"/> maps old
    /// to new names. Members using a renamed or changed material take its name, E, unit weight and unit mass.</summary>
    internal void CommitMaterials(IReadOnlyList<MaterialProperty> materials, IReadOnlyDictionary<string, string> renamed)
    {
        var before = Materials.ToDictionary(material => material.Name, StringComparer.Ordinal);
        var after = materials.ToDictionary(material => material.Name, StringComparer.Ordinal);
        var changed = 0;
        var properties = FemSectionProperties.ToArray();
        for (var index = 0; index < properties.Length; index++)
        {
            var property = properties[index];
            if (property.MaterialName is not { } name) continue;
            var current = renamed.TryGetValue(name, out var newName) ? newName : name;
            if (!after.TryGetValue(current, out var material)) continue;
            var old = before.GetValueOrDefault(name);
            var same = current == name && old is not null && old.ElasticModulus == material.ElasticModulus &&
                       old.WeightPerVolume == material.WeightPerVolume;
            if (same) continue;
            properties[index] = property with
            {
                MaterialName = current,
                ElasticModulusKNPerM2 = material.ElasticModulus,
                UnitWeightKNPerM3 = material.WeightPerVolume,
                UnitMassTPerM3 = material.MassPerVolume,
            };
            changed++;
        }
        Materials.Clear();
        foreach (var material in materials) Materials.Add(material);
        if (changed > 0)
        {
            FemSectionProperties.Clear();
            foreach (var property in properties) FemSectionProperties.Add(property);
            ClearFemResults();
            Notify(nameof(SharedViewerSections));
            Notify(nameof(SharedViewerMemberSizes));
        }
        Message = changed > 0
            ? $"Materials updated · {changed} member{(changed == 1 ? "" : "s")} take the new material data (FEM results cleared; SAP2000 unchanged)."
            : $"Materials updated · {Materials.Count} materials.";
        Update();
    }
}
