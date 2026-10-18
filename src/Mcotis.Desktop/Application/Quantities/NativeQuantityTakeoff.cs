using Mcotis.Core;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Application.Quantities;

/// <summary>A section group counted from the active native model document.</summary>
public sealed record NativeQuantityRow(string Section, string SectionType, QuantityMemberKind Kind, int MemberCount,
    double? TotalLengthM, double? ConcreteVolumeM3, double? SteelMassT, QuantitySectionFigure? Figure,
    string FigureDescription, string Details, double? ReinforcementRatio)
{
    public string LengthText => Format(TotalLengthM, "m");
    public string ConcreteText => Format(ConcreteVolumeM3, "m³");
    public string SteelText => Format(SteelMassT, "t");
    public double? EstimatedCostSgd { get; init; }
    public string EstimatedCostText => EstimatedCostSgd is { } cost ? $"S$ {cost:N0}" : "Unavailable";
    public string ReinforcementRatioText => ReinforcementRatio is { } ratio ? $"{ratio:P2}" : "—";
    private static string Format(double? value, string unit) => value is { } number ? $"{number:N3} {unit}" : "Unavailable";
}

public sealed record NativeQuantityTotals(int MemberCount, double? TotalLengthM, double? ConcreteVolumeM3, double? SteelMassT)
{
    public string LengthText => Text(TotalLengthM, "m");
    public string ConcreteText => Text(ConcreteVolumeM3, "m³");
    public string SteelText => Text(SteelMassT, "t");
    public double? EstimatedCostSgd { get; init; }
    public string EstimatedCostText => EstimatedCostSgd is { } cost ? $"S$ {cost:N0}" : "Unavailable";
    private static string Text(double? value, string unit) => value is { } number ? $"{number:N3} {unit}" : "Unavailable";
}

public sealed record NativeQuantityTakeoff(IReadOnlyList<NativeQuantityRow> Rows, NativeQuantityTotals Total, IReadOnlyList<string> Notes);

/// <summary>Section-level design utilisation, enveloped over the members assigned to the section.</summary>
public sealed record NativeQuantityUtilizationRow(string Section, string Procedure, int MemberCount, double? Utilization,
    string GoverningCombinations, string CaseEnvelopes, string Status, string Details)
{
    public string UtilizationText => Utilization is { } value
        ? double.IsFinite(value) ? value.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture) : ">9.99"
        : "—";
}

/// <summary>
/// Takes quantities from Mcotis's own document: member endpoints, section assignments, section areas and material types.
/// A shape is drawn only when the document stores enough dimensions to describe it. Composite component breakdowns are
/// not inferred from transformed properties; those are reported as unavailable when the document does not store them.
/// </summary>
public static class NativeQuantityTakeoffCalculator
{
    public static NativeQuantityTakeoff Calculate(ModelDocument document,
        IReadOnlyDictionary<string, MaterialProperty>? costMaterials = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        FemInputSnapshot? transitional = null;
        try { transitional = NativeToLegacyProjection.Read(document); }
        catch (System.Text.Json.JsonException) { /* A damaged transitional payload does not replace native model data. */ }
        var legacyNames = LegacyNames.Of(document);
        var membersByName = transitional?.SectionProperties.GroupBy(property => property.MemberName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal)
            ?? new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal);
        var members = new List<Member>();
        members.AddRange(document.Frames.Values.Select(frame =>
        {
            var assignment = document.AssignmentOfFrame(frame.Id);
            var name = legacyNames[frame.Id];
            var source = assignment.SectionId is not null && membersByName.TryGetValue(name, out var property) ? property : null;
            var rich = source?.SectionName is { } sectionName ? transitional?.FrameSections.FirstOrDefault(section => section.Name == sectionName) : null;
            var native = assignment.SectionId is { } id ? document.FrameSections.GetValueOrDefault(id) : null;
            return new Member(name, frame.StartPointId, frame.EndPointId, assignment.SectionId, QuantityMemberKind.Frame,
                rich?.Name ?? native?.Name ?? "Unassigned", (object?)rich ?? native, source,
                rich is null && native is not null ? document.Materials.GetValueOrDefault(native.MaterialId) : null,
                rich is not null ? transitional?.Materials.FirstOrDefault(material => material.Name == rich.Material) : null);
        }));
        members.AddRange(document.Cables.Values.Select(cable =>
        {
            var assignment = document.AssignmentOfCable(cable.Id);
            var name = legacyNames[cable.Id];
            var source = assignment.SectionId is not null && membersByName.TryGetValue(name, out var property) ? property : null;
            var sectionName = source?.SectionName;
            var rich = sectionName is null ? null : transitional?.CableSections.Concat(transitional.TendonSections).FirstOrDefault(section => section.Name == sectionName);
            var native = assignment.SectionId is { } id ? document.CableSections.GetValueOrDefault(id) : null;
            return new Member(name, cable.StartPointId, cable.EndPointId, assignment.SectionId, QuantityMemberKind.Cable,
                rich?.Name ?? native?.Name ?? "Unassigned", (object?)rich ?? native, source,
                native is { } cableSection ? document.Materials.GetValueOrDefault(cableSection.MaterialId) : null,
                rich is not null ? transitional?.Materials.FirstOrDefault(material => material.Name == rich.Material) : null);
        }));

        var rows = new List<NativeQuantityRow>();
        foreach (var group in members.GroupBy(member => (member.Kind, member.GroupKey)))
        {
            var first = group.First();
            var name = first.SectionName;
            var nativeFrame = first.SectionData as FrameSectionDefinition;
            var nativeCable = first.SectionData as CableSectionDefinition;
            var richFrame = first.SectionData as FrameSectionProperty;
            var richCable = first.SectionData as StrandSectionProperty;
            var lengths = group.Select(member => Length(document, member)).ToArray();
            double? totalLength = lengths.All(length => length is not null) ? lengths.Sum(length => length!.Value) : null;
            var sectionType = richFrame is not null ? FrameSectionProperty.Title(richFrame.Shape) : nativeFrame is not null ? Describe(nativeFrame) :
                richCable is not null || nativeCable is not null ? "Cable" : "—";
            var notes = new List<string>();

            double? concrete = null, steel = null, reinforcementRatio = null;
            if (first.SectionId is null) notes.Add("No section is assigned to these members.");
            else if (first.SectionData is null) notes.Add("The assigned section is not present in the native document.");
            else if (richFrame is not null && richFrame.Composite is { } composite)
            {
                try
                {
                    var parts = CompositeSection.Parts(richFrame.Shape, composite);
                    concrete = totalLength is { } length ? parts.ConcreteArea * length : null;
                    var steelDensity = Density(transitional, richFrame.SteelMaterial);
                    var rebarDensity = Density(transitional, richFrame.RebarMaterial);
                    var rebarArea = parts.RebarArea + parts.LinkAreaPerMetre;
                    if (parts.SteelArea > 0 && steelDensity is null) notes.Add($"Steel mass is unavailable: material '{richFrame.SteelMaterial}' has no positive stored mass density.");
                    if (parts.RebarArea > 0 && parts.ConcreteArea > 0)
                        reinforcementRatio = parts.RebarArea / parts.ConcreteArea;
                    if (rebarArea > 0)
                        notes.Add(rebarDensity is null
                            ? $"Steel mass is structural profile steel only; reinforcement mass is unavailable because material '{richFrame.RebarMaterial}' has no positive stored mass density."
                            : "Steel mass is structural profile steel only; reinforcement mass is not included.");
                    steel = totalLength is { } steelLength && (parts.SteelArea == 0 || steelDensity is not null)
                        ? parts.SteelArea * (steelDensity ?? 0) * steelLength : null;
                }
                catch (ArgumentException) { notes.Add("Composite section component geometry is incomplete."); }
            }
            else if (richFrame is not null && richFrame.Shape == FrameShape.General && richFrame.SapType.Contains("SD", StringComparison.OrdinalIgnoreCase))
                notes.Add("Section Designer component geometry is not stored in a take-off-ready form.");
            else
            {
                var materialType = first.LegacyMaterial?.Kind;
                var nativeType = first.NativeMaterial?.Type;
                var area = richFrame is not null ? richFrame.Values().Area : richCable?.Area ?? nativeFrame?.Properties.Area ?? nativeCable?.Area;
                if (area is not > 0 || !double.IsFinite(area.Value)) notes.Add("The assigned section has no positive stored area.");
                else if (materialType == MaterialKind.Concrete || nativeType == MaterialType.Concrete)
                {
                    concrete = totalLength is { } length ? area.Value * length : null;
                    steel = 0;
                }
                else if (IsSteel(materialType) || IsSteel(nativeType))
                {
                    concrete = 0;
                    var density = first.LegacyMaterial?.MassPerVolume ?? first.NativeMaterial?.MassDensity;
                    if (density is > 0 && double.IsFinite(density.Value)) steel = totalLength is { } length ? area.Value * length * density.Value : null;
                    else notes.Add("The material has no positive stored mass density.");
                }
                else notes.Add($"Material type '{first.LegacyMaterial?.Kind.ToString() ?? nativeType?.ToString() ?? "unknown"}' does not identify a concrete or steel quantity basis.");
            }

            if (totalLength is null) notes.Add("Member length could not be read from the document's endpoint coordinates.");
            var figure = richFrame is not null ? Figure(richFrame, transitional) : nativeFrame is not null ? Figure(nativeFrame, first.NativeMaterial?.Type) :
                richCable is not null ? Figure(richCable.Name, richCable.Area, first.LegacyMaterial?.Kind ?? NativeMaterialKind(first.NativeMaterial?.Type)) :
                nativeCable is not null ? Figure(nativeCable.Name, nativeCable.Area, NativeMaterialKind(first.NativeMaterial?.Type)) : null;
            var figureDescription = FigureDescription(richFrame, richCable, nativeFrame, nativeCable, figure);
            var estimatedCost = EstimateCost(first, richFrame, transitional, costMaterials, totalLength, concrete, steel);
            rows.Add(new(name, sectionType, group.Key.Kind, group.Count(), totalLength, concrete, steel, figure, figureDescription,
                string.Join(" ", notes.Distinct(StringComparer.Ordinal)), reinforcementRatio) { EstimatedCostSgd = estimatedCost });
        }

        rows = rows.OrderBy(row => row.Kind).ThenBy(row => row.Section, StringComparer.Ordinal).ToList();
        var allLength = rows.All(row => row.TotalLengthM is not null);
        var allConcrete = rows.All(row => row.ConcreteVolumeM3 is not null);
        var allSteel = rows.All(row => row.SteelMassT is not null);
        var allCosts = rows.All(row => row.EstimatedCostSgd is not null);
        var total = new NativeQuantityTotals(rows.Sum(row => row.MemberCount), allLength ? rows.Sum(row => row.TotalLengthM!.Value) : null,
            allConcrete ? rows.Sum(row => row.ConcreteVolumeM3!.Value) : null, allSteel ? rows.Sum(row => row.SteelMassT!.Value) : null)
        {
            EstimatedCostSgd = allCosts ? rows.Sum(row => row.EstimatedCostSgd!.Value) : null,
        };
        var notesSummary = rows.Where(row => row.Details.Length > 0)
            .Select(row => $"{row.Kind} · {row.Section}: {row.Details}").ToArray();
        return new(rows, total, notesSummary);
    }

    private sealed record Member(string Name, ObjectId Start, ObjectId End, ObjectId? SectionId, QuantityMemberKind Kind,
        string SectionName, object? SectionData, FemSectionProperties? LegacyProperties, MaterialDefinition? NativeMaterial,
        MaterialProperty? LegacyMaterial)
    {
        public string GroupKey => SectionId is null ? "unassigned" : (SectionName.Length > 0 ? "name:" + SectionName : "id:" + SectionId);
    }

    private static double? Length(ModelDocument document, Member member)
    {
        if (!document.Points.TryGetValue(member.Start, out var start) || !document.Points.TryGetValue(member.End, out var end)) return null;
        var length = start.Position.DistanceTo(end.Position);
        return length > 0 && double.IsFinite(length) ? length : null;
    }

    private static string Describe(FrameSectionDefinition section) =>
        string.Join(" ", SplitWords(section.Shape.ToString()));

    private static bool IsSteel(MaterialKind? kind) => kind is MaterialKind.Steel or MaterialKind.Rebar or MaterialKind.Tendon or MaterialKind.ColdFormed;
    private static bool IsSteel(MaterialType? type) => type is MaterialType.Steel or MaterialType.Rebar or MaterialType.Tendon;

    private static double? EstimateCost(Member member, FrameSectionProperty? richFrame, FemInputSnapshot? snapshot,
        IReadOnlyDictionary<string, MaterialProperty>? costMaterials, double? lengthM, double? concreteVolumeM3, double? steelMassT)
    {
        if (member.SectionId is null || lengthM is not { } length || !double.IsFinite(length)) return null;

        MaterialProperty? Material(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (costMaterials is not null && costMaterials.TryGetValue(name, out var current)) return current;
            return snapshot?.Materials.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        if (richFrame?.Composite is { } composite)
        {
            try
            {
                var parts = CompositeSection.Parts(richFrame.Shape, composite);
                var cost = CompositeSection.Cost(parts, Material(richFrame.Material), Material(richFrame.SteelMaterial), Material(richFrame.RebarMaterial));
                return cost.CostPerM is { } perMetre && double.IsFinite(perMetre) ? perMetre * length : null;
            }
            catch (ArgumentException) { return null; }
        }

        var materialName = member.LegacyMaterial?.Name ?? member.NativeMaterial?.Name ?? member.LegacyProperties?.MaterialName;
        var material = Material(materialName);
        if (member.LegacyMaterial?.Kind == MaterialKind.Concrete || member.NativeMaterial?.Type == MaterialType.Concrete)
            return concreteVolumeM3 is { } concrete && material?.UnitCostPerCubicMetre is { } concreteRate && double.IsFinite(concreteRate)
                ? concrete * concreteRate : null;
        if (IsSteel(member.LegacyMaterial?.Kind) || IsSteel(member.NativeMaterial?.Type))
            return steelMassT is { } steel && material?.UnitCost is { } steelRate && double.IsFinite(steelRate)
                ? steel * steelRate : null;
        return null;
    }

    private static double? Density(FemInputSnapshot? snapshot, string materialName)
    {
        var material = snapshot?.Materials.FirstOrDefault(item => item.Name == materialName);
        return material?.MassPerVolume is > 0 and var density && double.IsFinite(density) ? density : null;
    }

    private static IEnumerable<string> SplitWords(string value)
    {
        for (var start = 0; start < value.Length;)
        {
            var end = start + 1;
            while (end < value.Length && !char.IsUpper(value[end])) end++;
            yield return value[start..end];
            start = end;
        }
    }

    private static string FigureDescription(FrameSectionProperty? richFrame, StrandSectionProperty? richCable,
        FrameSectionDefinition? frame, CableSectionDefinition? cable, QuantitySectionFigure? figure)
    {
        if (richFrame is not null)
        {
            var drawing = LegacyDrawing(richFrame, null);
            if (drawing is not null) return drawing.Description + (drawing.Recovered ? " · recovered outline" : "");
            var values = new[] { richFrame.Depth, richFrame.Width, richFrame.WebThickness, richFrame.FlangeThickness }.Where(value => value > 0).ToArray();
            return values.Length == 0 ? "Section outline unavailable." : string.Join(" · ", values.Select(value => $"{value * 1000:0.###} mm"));
        }
        if (richCable is not null) return CableDescription(richCable.Name, richCable.Area);
        if (frame is not null && figure is null) return "No stored outline dimensions; section geometry is unavailable.";
        if (cable is not null) return CableDescription(cable.Name, cable.Area);
        if (frame is null) return "Section outline unavailable.";
        var dimensions = frame.Dimensions.Where(pair => pair.Value > 0 && double.IsFinite(pair.Value))
            .Select(pair => $"{pair.Key} {pair.Value * 1000:0.###} mm").ToArray();
        return dimensions.Length == 0 ? "Stored section outline" : string.Join(" · ", dimensions);
    }

    private static QuantitySectionFigure? Figure(FrameSectionProperty section, FemInputSnapshot? snapshot)
    {
        if (LegacyDrawing(section, snapshot) is { } drawing) return FromDrawing(drawing);
        var material = snapshot?.Materials.FirstOrDefault(item => item.Name == section.Material);
        var className = material?.Kind switch
        {
            MaterialKind.Concrete => QuantityMaterialClass.Concrete,
            MaterialKind.Steel or MaterialKind.Rebar or MaterialKind.Tendon or MaterialKind.ColdFormed => QuantityMaterialClass.Steel,
            _ => (QuantityMaterialClass?)null,
        };
        var plates = section.Plates();
        if (plates.Count == 0 && section.Shape is FrameShape.Circle or FrameShape.Pipe && section.Depth > 0)
        {
            var outlines = new List<IReadOnlyList<QuantityFigurePoint>> { Circle(section.Depth / 2) };
            if (section.Shape == FrameShape.Pipe && section.WebThickness > 0 && section.Depth / 2 > section.WebThickness)
                outlines.Add(Circle(section.Depth / 2 - section.WebThickness));
            return BuildFigure([new(section.Name, section.Material, className, outlines)]);
        }
        if (plates.Count == 0) return null;
        var shapes = plates.Select(rect => new QuantityFigureShape(section.Name, section.Material, className,
            [Rectangle(rect.X, rect.Y, rect.W, rect.H)])).ToArray();
        return BuildFigure(shapes);
    }

    private static string CableDescription(string name, double area)
    {
        var areaText = $"Area {area * 1e6:0.###} mm²";
        return StrandBundle.Parse(name) is { } bundle
            ? $"{bundle.Count} × Ø{bundle.DiameterMm:0.###} mm strands · {areaText}"
            : $"Equivalent solid round · {areaText}";
    }

    private static MaterialKind? NativeMaterialKind(MaterialType? type) => type switch
    {
        MaterialType.Steel => MaterialKind.Steel,
        MaterialType.Rebar => MaterialKind.Rebar,
        MaterialType.Tendon => MaterialKind.Tendon,
        MaterialType.Concrete => MaterialKind.Concrete,
        _ => null,
    };

    private static QuantitySectionFigure? Figure(string name, double area, MaterialKind? materialType)
    {
        var materialClass = materialType is MaterialKind.Steel or MaterialKind.Rebar or MaterialKind.Tendon or MaterialKind.ColdFormed
            ? QuantityMaterialClass.Steel : (QuantityMaterialClass?)null;
        var material = materialType?.ToString() ?? "Steel";
        if (area is not > 0 || !double.IsFinite(area)) return null;
        if (StrandBundle.Parse(name) is not { } bundle)
        {
            var radius = Math.Sqrt(area / Math.PI);
            return BuildFigure([new(name, material, materialClass, [Circle(radius)])]);
        }

        var strandRadius = bundle.DiameterMm * 0.0005;
        var strands = new List<(double X, double Y)> { (0, 0) };
        for (var ring = 1; strands.Count < bundle.Count; ring++)
        {
            var q = -ring;
            var r = ring;
            foreach (var (dq, dr) in new (int Q, int R)[] { (1, 0), (1, -1), (0, -1), (-1, 0), (-1, 1), (0, 1) })
                for (var step = 0; step < ring && strands.Count < bundle.Count; step++)
                {
                    strands.Add(((q + r * 0.5) * strandRadius * 2, r * Math.Sqrt(3) * strandRadius));
                    q += dq;
                    r += dr;
                }
        }

        var wireArea = StrandBundle.StandardArea(bundle.DiameterMm);
        var shapes = new List<QuantityFigureShape>(bundle.Count * (wireArea is null ? 1 : 8));
        foreach (var (x, y) in strands)
        {
            shapes.Add(new(name, material, materialClass, [Circle(strandRadius)], OutlineOnly: true));
            if (wireArea is null) shapes.Add(new(name, material, materialClass, [Circle(strandRadius)]));
            else
            {
                var wireRadius = Math.Sqrt(wireArea.Value / 7 * 1e-6 / Math.PI);
                shapes.Add(new(name, material, materialClass, [Circle(wireRadius).Select(point => new QuantityFigurePoint(point.X + x, point.Y + y)).ToArray()]));
                for (var wire = 0; wire < 6; wire++)
                {
                    var angle = wire * Math.PI / 3;
                    var centreX = x + 2 * wireRadius * Math.Cos(angle);
                    var centreY = y + 2 * wireRadius * Math.Sin(angle);
                    shapes.Add(new(name, material, materialClass,
                        [Circle(wireRadius).Select(point => new QuantityFigurePoint(point.X + centreX, point.Y + centreY)).ToArray()]));
                }
            }
        }
        return BuildFigure(shapes);
    }

    private static SectionDrawing? LegacyDrawing(FrameSectionProperty section, FemInputSnapshot? snapshot) =>
        SectionDrawings.For(section, material => snapshot?.Materials.FirstOrDefault(item => item.Name == material)?.ElasticModulus / 1e6);

    private static QuantitySectionFigure? FromDrawing(SectionDrawing drawing)
    {
        var shapes = drawing.Regions.Select(region => new QuantityFigureShape(region.Material.ToString(), region.Material.ToString(),
            region.Material == SectionMaterial.Concrete ? QuantityMaterialClass.Concrete : QuantityMaterialClass.Steel,
            region.Polygons.Select(polygon => (IReadOnlyList<QuantityFigurePoint>)polygon.Select(point => new QuantityFigurePoint(point.X3, point.X2)).ToArray()).ToArray(),
            region.Shell)).ToArray();
        return BuildFigure(shapes);
    }

    private static QuantitySectionFigure? BuildFigure(IReadOnlyList<QuantityFigureShape> shapes)
    {
        var all = shapes.SelectMany(shape => shape.Outlines).SelectMany(outline => outline).ToArray();
        return all.Length == 0 ? null : new(shapes, all.Min(point => point.X), all.Min(point => point.Y), all.Max(point => point.X), all.Max(point => point.Y));
    }

    private static QuantitySectionFigure? Figure(FrameSectionDefinition section, MaterialType? materialType)
    {
        double D(params string[] keys) => keys.Select(key => section.Dimensions.TryGetValue(key, out var value) ? value : 0).FirstOrDefault(value => value > 0 && double.IsFinite(value));
        var d = D("t3", "depth", "h");
        var b = D("t2", "width", "b");
        var tf = D("tf", "flangeThickness");
        var tw = D("tw", "webThickness");
        var className = materialType switch
        {
            MaterialType.Concrete => QuantityMaterialClass.Concrete,
            MaterialType.Steel or MaterialType.Rebar or MaterialType.Tendon => QuantityMaterialClass.Steel,
            _ => (QuantityMaterialClass?)null,
        };
        var material = materialType?.ToString() ?? "Unknown";
        var shapes = new List<QuantityFigureShape>();
        IReadOnlyList<(double X, double Y, double W, double H)> rectangles = section.Shape switch
        {
            FrameSectionShape.Rectangle when d > 0 && b > 0 => [(-b / 2, -d / 2, b, d)],
            FrameSectionShape.Box when d > 0 && b > 0 && tf > 0 && tw > 0 =>
                [(-b / 2, d / 2 - tf, b, tf), (-b / 2, -d / 2, b, tf), (-b / 2, -d / 2 + tf, tw, d - 2 * tf), (b / 2 - tw, -d / 2 + tf, tw, d - 2 * tf)],
            FrameSectionShape.ISection when d > 0 && b > 0 && tf > 0 && tw > 0 =>
                [(-b / 2, d / 2 - tf, b, tf), (-b / 2, -d / 2, b, tf), (-tw / 2, -d / 2 + tf, tw, d - 2 * tf)],
            FrameSectionShape.Channel when d > 0 && b > 0 && tf > 0 && tw > 0 =>
                [(-b / 2, d / 2 - tf, b, tf), (-b / 2, -d / 2, b, tf), (-b / 2, -d / 2 + tf, tw, d - 2 * tf)],
            FrameSectionShape.Tee when d > 0 && b > 0 && tf > 0 && tw > 0 =>
                [(-b / 2, d / 2 - tf, b, tf), (-tw / 2, -d / 2, tw, d - tf)],
            FrameSectionShape.Angle when d > 0 && b > 0 && tf > 0 && tw > 0 =>
                [(-b / 2, -d / 2, b, tf), (-b / 2, -d / 2 + tf, tw, d - tf)],
            _ => [],
        };
        if (rectangles.Count > 0)
            shapes.Add(new(section.Name, material, className, rectangles.Select(rect => (IReadOnlyList<QuantityFigurePoint>)Rectangle(rect.X, rect.Y, rect.W, rect.H)).ToArray()));
        else if (section.Shape is FrameSectionShape.Circle or FrameSectionShape.Pipe && d > 0)
        {
            var outlines = new List<IReadOnlyList<QuantityFigurePoint>> { Circle(d / 2) };
            if (section.Shape == FrameSectionShape.Pipe && tw > 0 && d / 2 > tw) outlines.Add(Circle(d / 2 - tw));
            shapes.Add(new(section.Name, material, className, outlines));
        }
        if (shapes.Count == 0) return null;
        var all = shapes.SelectMany(shape => shape.Outlines).SelectMany(outline => outline).ToArray();
        return new(shapes, all.Min(point => point.X), all.Min(point => point.Y), all.Max(point => point.X), all.Max(point => point.Y));
    }

    private static IReadOnlyList<QuantityFigurePoint> Rectangle(double x, double y, double width, double height) =>
        [new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height)];

    private static IReadOnlyList<QuantityFigurePoint> Circle(double radius) => Enumerable.Range(0, 32)
        .Select(index => new QuantityFigurePoint(radius * Math.Cos(index * 2 * Math.PI / 32), radius * Math.Sin(index * 2 * Math.PI / 32))).ToArray();
}
