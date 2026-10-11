using COT_VCF.Domain;
using COT_VCF.Geometry;
using System.Collections.ObjectModel;

namespace COT_VCF.Presentation;

/// <summary>A composite section worked out with the library materials (moduli in kN/m², weight kN/m, γ / ρ equivalent on the
/// transformed area).</summary>
internal sealed record CompositeAnalysis(CompositeParts Parts, SectionValues Values, double Ecm, double Ea, double Es,
    double EquivalentUnitWeight, double EquivalentUnitMass, double WeightPerMetre, CompositeCost Cost,
    MaterialProperty? Concrete, MaterialProperty? Steel, MaterialProperty? Rebar);

// ---- Define → Section Properties: frame, cable and tendon sections of the working model ----
// Get From SAP2000 reads the active model's sections (read-only). Without it the library starts from the sections the member
// properties name. A change made here is applied to the FEM member properties of the members with that section (area,
// in-plane inertia with the member's local axis and modifiers, material data); SAP2000 is never written.
public sealed partial class MainViewModel
{
    public ObservableCollection<FrameSectionProperty> FrameSections { get; } = [];
    public ObservableCollection<StrandSectionProperty> CableSections { get; } = [];
    public ObservableCollection<StrandSectionProperty> TendonSections { get; } = [];

    public int SectionUsage(string section) =>
        FemSectionProperties.Count(property => string.Equals(property.SectionName, section, StringComparison.Ordinal));

    /// <summary>Starts the libraries from the member properties when they are empty.</summary>
    internal void EnsureSections()
    {
        if (FrameSections.Count == 0)
            foreach (var group in FemSectionProperties.Where(property => !property.IsCable && !string.IsNullOrWhiteSpace(property.SectionName))
                         .GroupBy(property => property.SectionName!, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
                FrameSections.Add(FromMember(group.First()));
        if (CableSections.Count == 0)
            foreach (var group in FemSectionProperties.Where(property => property.IsCable && !string.IsNullOrWhiteSpace(property.SectionName))
                         .GroupBy(property => property.SectionName!, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var first = group.First();
                CableSections.Add(new StrandSectionProperty
                {
                    Name = group.Key, Material = first.MaterialName ?? "", Area = first.GrossAreaM2 > 0 ? first.GrossAreaM2 : first.AreaM2, Source = "Model",
                    Notes = "From the model's member properties — Get From SAP2000 for the section as defined.",
                });
            }
    }

    private static FrameSectionProperty FromMember(FemSectionProperties member)
    {
        var area = member.GrossAreaM2 > 0 ? member.GrossAreaM2 : member.AreaM2;
        var shape = member.Shape;
        var common = new FrameSectionProperty
        {
            Name = member.SectionName!, Material = member.MaterialName ?? "", Source = "Model",
            Notes = "From the model's member properties — Get From SAP2000 for the section as defined.",
        };
        return shape?.Kind switch
        {
            SectionShapeKind.Rectangle => common with { Shape = FrameShape.Rectangle, Depth = shape.Depth, Width = shape.Width },
            SectionShapeKind.Box => common with { Shape = FrameShape.Box, Depth = shape.Depth, Width = shape.Width, FlangeThickness = shape.FlangeThickness, WebThickness = shape.WebThickness },
            SectionShapeKind.I => common with { Shape = FrameShape.I, Depth = shape.Depth, Width = shape.Width, FlangeThickness = shape.FlangeThickness, WebThickness = shape.WebThickness,
                BottomWidth = shape.BottomWidth > 0 ? shape.BottomWidth : shape.Width, BottomFlangeThickness = shape.BottomFlangeThickness > 0 ? shape.BottomFlangeThickness : shape.FlangeThickness },
            SectionShapeKind.Pipe => common with { Shape = FrameShape.Pipe, Depth = shape.Depth, WebThickness = shape.WebThickness },
            SectionShapeKind.Circle => common with { Shape = FrameShape.Circle, Depth = shape.Depth },
            SectionShapeKind.Channel => common with { Shape = FrameShape.Channel, Depth = shape.Depth, Width = shape.Width, FlangeThickness = shape.FlangeThickness, WebThickness = shape.WebThickness },
            SectionShapeKind.Tee => common with { Shape = FrameShape.Tee, Depth = shape.Depth, Width = shape.Width, FlangeThickness = shape.FlangeThickness, WebThickness = shape.WebThickness },
            SectionShapeKind.Angle => common with { Shape = FrameShape.Angle, Depth = shape.Depth, Width = shape.Width, FlangeThickness = shape.FlangeThickness, WebThickness = shape.WebThickness },
            // Section Designer / filled tube: the equivalent rectangle carries A, I33 and I22 (d = √(12 I33/A), w = √(12 I22/A))
            _ => common with
            {
                Shape = FrameShape.General, SapType = "SD",
                Given = shape is { Kind: SectionShapeKind.Equivalent }
                    ? new SectionValues(area, area * shape.Depth * shape.Depth / 12, area * shape.Width * shape.Width / 12, 0, 0, 0)
                    : new SectionValues(area, member.InPlaneInertiaM4 ?? 0, member.InPlaneInertiaM4 ?? 0, 0, 0, 0),
            },
        };
    }

    internal async Task<SectionLibrary> ReadSectionsFromSapAsync()
    {
        if (IsBusy) throw new InvalidOperationException("Another operation is running; try again when it has finished.");
        IsBusy = true;
        try { return await workflow.ReadSectionsAsync(); }
        catch (System.Runtime.InteropServices.COMException) when (State.IsSapConnected)
        {
            State.Connection = "Connection Lost";
            throw;
        }
        finally { IsBusy = false; Update(); }
    }

    /// <summary>The FEM properties of <paramref name="member"/> with <paramref name="section"/>: the section's area and
    /// inertia (section modifiers × the member's own, rotated by its local axis), its material's E / γ / ρ from the library
    /// (else the member keeps its own), and its outline for drawing.</summary>
    /// <summary>A CFT / steel encased section worked out with the library materials: its parts, the properties transformed to
    /// the concrete (E = Ecm), the equivalent unit weight / mass that give the true self-weight on the transformed area, and
    /// the cost per metre from the materials' unit costs.</summary>
    /// <summary>The reinforcement a steel encased section uses when the library has no rebar material (B500, E 200 GPa,
    /// the rebar rate).</summary>
    internal static readonly MaterialProperty DefaultRebar = MaterialCosts.WithDefaultCost(MaterialProperty.Default(MaterialKind.Rebar, "B500 (default)") with { Grade = "B500" });

    internal CompositeAnalysis? AnalyseComposite(FrameSectionProperty section)
    {
        if (!section.IsComposite) return null;
        var input = section.Composite!;
        MaterialProperty? Find(string name) => name.Length > 0 ? Materials.FirstOrDefault(item => item.Name == name) : null;
        var concrete = Find(section.Material);
        var steel = Find(section.SteelMaterial);
        var rebar = section.Shape == FrameShape.FilledTube ? null : Find(section.RebarMaterial) is { Kind: MaterialKind.Rebar } bars ? bars : DefaultRebar;
        // EN 1992-1-1 Table 3.1 Ecm = 22 [(fck + 8)/10]^0.3 GPa when the concrete has no modulus
        var ecm = concrete?.ElasticModulus is > 0 and var e ? e : 22e6 * Math.Pow((input.Fck + 8) / 10, 0.3);
        var ea = steel?.ElasticModulus is > 0 and var eSteel ? eSteel : input.Ea * 1000;
        var es = rebar?.ElasticModulus is > 0 and var eRebar ? eRebar : input.Es * 1000;
        var parts = CompositeSection.Parts(section.Shape, input);
        var values = CompositeSection.Transformed(parts, ecm, ea, es);
        double gammaC = concrete?.WeightPerVolume ?? 25, gammaA = steel?.WeightPerVolume ?? 76.97, gammaS = rebar?.WeightPerVolume ?? 76.97;
        double rhoC = concrete?.MassPerVolume ?? gammaC / 9.80665, rhoA = steel?.MassPerVolume ?? gammaA / 9.80665, rhoS = rebar?.MassPerVolume ?? gammaS / 9.80665;
        var reinforcement = parts.RebarArea + parts.LinkAreaPerMetre;
        var weight = parts.ConcreteArea * gammaC + parts.SteelArea * gammaA + reinforcement * gammaS;
        var mass = parts.ConcreteArea * rhoC + parts.SteelArea * rhoA + reinforcement * rhoS;
        var cost = CompositeSection.Cost(parts, concrete, steel, rebar);
        return new CompositeAnalysis(parts, values, ecm, ea, es, values.Area > 0 ? weight / values.Area : gammaC, values.Area > 0 ? mass / values.Area : rhoC,
            weight, cost, concrete, steel, rebar);
    }

    internal FemSectionProperties PropertiesFor(FemSectionProperties member, FrameSectionProperty section)
    {
        var composite = AnalyseComposite(section);
        var values = composite?.Values ?? section.Values();
        double Mod(IReadOnlyList<double>? list, int index) => list is { } values && index < values.Count ? values[index] : 1;
        var radians = member.LocalAngleDeg * Math.PI / 180;
        var i33 = values.I33 * Mod(section.Modifiers, 5) * Mod(member.Modifiers, 5);
        var i22 = values.I22 * Mod(section.Modifiers, 4) * Mod(member.Modifiers, 4);
        var material = Materials.FirstOrDefault(item => item.Name == section.Material);
        var shape = section.Shape switch
        {
            FrameShape.Rectangle => new FemSectionShape(SectionShapeKind.Rectangle, section.Depth, section.Width),
            FrameShape.Box => new FemSectionShape(SectionShapeKind.Box, section.Depth, section.Width, section.FlangeThickness, section.WebThickness),
            FrameShape.I => new FemSectionShape(SectionShapeKind.I, section.Depth, section.Width, section.FlangeThickness, section.WebThickness, section.BottomWidth, section.BottomFlangeThickness),
            FrameShape.Pipe => new FemSectionShape(SectionShapeKind.Pipe, section.Depth, section.Depth, 0, section.WebThickness),
            FrameShape.Circle => new FemSectionShape(SectionShapeKind.Circle, section.Depth, section.Depth),
            FrameShape.Channel => new FemSectionShape(SectionShapeKind.Channel, section.Depth, section.Width, section.FlangeThickness, section.WebThickness),
            FrameShape.Tee => new FemSectionShape(SectionShapeKind.Tee, section.Depth, section.Width, section.FlangeThickness, section.WebThickness),
            FrameShape.Angle => new FemSectionShape(SectionShapeKind.Angle, section.Depth, section.Width, section.FlangeThickness, section.WebThickness),
            FrameShape.FilledTube when section.Composite?.Tube == TubeShape.Circular => new FemSectionShape(SectionShapeKind.Pipe, section.Depth, section.Depth, 0, section.WebThickness),
            FrameShape.FilledTube => new FemSectionShape(SectionShapeKind.Box, section.Depth, section.Width, section.FlangeThickness, section.WebThickness),
            FrameShape.Encased or FrameShape.MegaComposite => new FemSectionShape(SectionShapeKind.Rectangle, section.Depth, section.Width),
            _ => FemSectionShape.EquivalentRectangle(values.Area, values.I33, values.I22) ?? member.Shape,
        };
        return member with
        {
            SectionName = section.Name,
            MaterialName = section.Material.Length > 0 ? section.Material : member.MaterialName,
            AreaM2 = values.Area * Mod(section.Modifiers, 0) * Mod(member.Modifiers, 0),
            GrossAreaM2 = values.Area,
            InPlaneInertiaM4 = i33 * Math.Cos(radians) * Math.Cos(radians) + i22 * Math.Sin(radians) * Math.Sin(radians),
            ElasticModulusKNPerM2 = composite?.Ecm ?? material?.ElasticModulus ?? member.ElasticModulusKNPerM2,
            UnitWeightKNPerM3 = composite?.EquivalentUnitWeight ?? material?.WeightPerVolume ?? member.UnitWeightKNPerM3,
            UnitMassTPerM3 = composite?.EquivalentUnitMass ?? material?.MassPerVolume ?? member.UnitMassTPerM3,
            Shape = shape,
        };
    }

    internal FemSectionProperties PropertiesFor(FemSectionProperties member, StrandSectionProperty section)
    {
        var material = Materials.FirstOrDefault(item => item.Name == section.Material);
        var modifier = member.Modifiers is { Count: > 0 } mods ? mods[0] : 1;
        return member with
        {
            SectionName = section.Name,
            MaterialName = section.Material.Length > 0 ? section.Material : member.MaterialName,
            AreaM2 = section.Area * modifier,
            GrossAreaM2 = section.Area,
            ElasticModulusKNPerM2 = material?.ElasticModulus ?? member.ElasticModulusKNPerM2,
            UnitWeightKNPerM3 = material?.WeightPerVolume ?? member.UnitWeightKNPerM3,
            UnitMassTPerM3 = material?.MassPerVolume ?? member.UnitMassTPerM3,
        };
    }

    /// <summary>Section Properties → OK: the library becomes <paramref name="sections"/>; members with a changed or renamed
    /// section take its properties.</summary>
    internal void CommitFrameSections(IReadOnlyList<FrameSectionProperty> sections, IReadOnlyDictionary<string, string> renamed)
    {
        var before = FrameSections.ToDictionary(section => section.Name, StringComparer.Ordinal);
        var after = sections.ToDictionary(section => section.Name, StringComparer.Ordinal);
        ApplySectionChanges(member => !member.IsCable, renamed, name => after.TryGetValue(name, out var section) ? section : null,
            (name, section) => before.GetValueOrDefault(name) is { } old && old == section, (member, section) => PropertiesFor(member, section));
        FrameSections.Clear();
        foreach (var section in sections) FrameSections.Add(section);
    }

    internal void CommitStrandSections(IReadOnlyList<StrandSectionProperty> sections, IReadOnlyDictionary<string, string> renamed, bool tendons)
    {
        var library = tendons ? TendonSections : CableSections;
        var before = library.ToDictionary(section => section.Name, StringComparer.Ordinal);
        var after = sections.ToDictionary(section => section.Name, StringComparer.Ordinal);
        if (!tendons)
        {
            ApplySectionChanges(member => member.IsCable, renamed, name => after.TryGetValue(name, out var section) ? section : null,
                (name, section) => before.GetValueOrDefault(name) is { } old && old == section, (member, section) => PropertiesFor(member, section));
            // Tab 03: a cable's section, area and capacity follow its (new) section
            foreach (var cable in State.Cables)
                if (FemSectionProperties.FirstOrDefault(property => property.MemberName == cable.CableName) is { } current &&
                    (current.SectionName != cable.SectionName || current.GrossAreaM2 != cable.SectionAreaM2))
                {
                    cable.SectionName = current.SectionName;
                    cable.SectionAreaM2 = current.GrossAreaM2;
                    cable.DesignCapacityKN = cable.ResolveCapacity(State.CableStrengthMpa, State.CableStrengthReductionFactor);
                }
        }
        library.Clear();
        foreach (var section in sections) library.Add(section);
    }

    private void ApplySectionChanges<T>(Func<FemSectionProperties, bool> applies, IReadOnlyDictionary<string, string> renamed,
        Func<string, T?> find, Func<string, T, bool> unchanged, Func<FemSectionProperties, T, FemSectionProperties> build) where T : class
    {
        var properties = FemSectionProperties.ToArray();
        var changed = 0;
        for (var index = 0; index < properties.Length; index++)
        {
            var member = properties[index];
            if (!applies(member) || member.SectionName is not { } name) continue;
            var current = renamed.TryGetValue(name, out var newName) ? newName : name;
            if (find(current) is not { } section) continue;
            if (current == name && unchanged(name, section)) continue;
            properties[index] = build(member, section);
            changed++;
        }
        if (changed > 0)
        {
            FemSectionProperties.Clear();
            foreach (var property in properties) FemSectionProperties.Add(property);
            ClearFemResults();
            Notify(nameof(SharedViewerSections));
            Notify(nameof(SharedViewerMemberSizes));
        }
        Message = changed > 0
            ? $"Sections updated · {changed} member{(changed == 1 ? "" : "s")} take the new section data (FEM results cleared; SAP2000 unchanged)."
            : "Sections updated.";
        Update();
    }
}
