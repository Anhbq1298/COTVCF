namespace Mcotis.Desktop.Domain;

/// <summary>Frame section shapes the app can draw and compute; <see cref="General"/> covers Section Designer and any
/// other SAP2000 shape, whose properties are taken as SAP2000 reports them. <see cref="FilledTube"/> (CFT) and
/// <see cref="Encased"/> (steel encased composite) and <see cref="MegaComposite"/> (several encased profiles) are defined by their <see cref="FrameSectionProperty.Composite"/> data.</summary>
public enum FrameShape { Rectangle, Box, I, Pipe, Circle, Channel, Tee, Angle, FilledTube, Encased, MegaComposite, General }

/// <summary>Computed frame section properties [m², m⁴]. I33 is about the 3-axis (bending in the 1-2 plane, depth t3).</summary>
public sealed record SectionValues(double Area, double I33, double I22, double Torsion, double ShearArea2, double ShearArea3);

/// <summary>
/// A frame section as Define → Section Properties → Frame Sections shows it (SAP2000's section forms), in m. Standard
/// shapes keep their dimensions and compute their properties; a <see cref="FrameShape.General"/> section (Section
/// Designer, filled tube, …) keeps the properties SAP2000 reports in <see cref="Given"/> and its dimensions are not editable.
/// <see cref="Modifiers"/> are the section property modifiers in SAP order: A, As2, As3, J, I22, I33, mass, weight.
/// </summary>
public sealed record FrameSectionProperty
{
    public required string Name { get; init; }
    public FrameShape Shape { get; init; } = FrameShape.Rectangle;
    /// <summary>The SAP2000 shape name for a <see cref="FrameShape.General"/> section (e.g. "SD", "FilledTube").</summary>
    public string SapType { get; init; } = "";
    public string Material { get; init; } = "";
    public string Notes { get; init; } = "";
    public int Color { get; init; } = 0xFF00FF;
    public double Depth { get; init; }               // t3 (outside diameter for pipe / circle)
    public double Width { get; init; }               // t2 (top flange width for I / tee)
    public double FlangeThickness { get; init; }     // tf
    public double WebThickness { get; init; }        // tw (wall thickness for pipe)
    public double BottomWidth { get; init; }         // t2b (I section)
    public double BottomFlangeThickness { get; init; } // tfb (I section)
    public double CornerRadius { get; init; }        // box
    public IReadOnlyList<double> Modifiers { get; init; } = [1, 1, 1, 1, 1, 1, 1, 1];
    public SectionValues? Given { get; init; }
    public string Source { get; init; } = "App";
    /// <summary>A CFT / steel encased section's cross-section and design data, in mm and MPa as the Design menu takes them.
    /// <see cref="Material"/> is then the concrete; <see cref="SteelMaterial"/> the tube / encased steel and
    /// <see cref="RebarMaterial"/> the reinforcement. <see cref="Given"/> holds its transformed properties.</summary>
    public ColumnSectionInput? Composite { get; init; }
    public string SteelMaterial { get; init; } = "";
    public string RebarMaterial { get; init; } = "";

    public bool IsComposite => Shape is FrameShape.FilledTube or FrameShape.Encased or FrameShape.MegaComposite && Composite is not null;

    public static string Title(FrameShape shape) => shape switch
    {
        FrameShape.Box => "Box/Tube",
        FrameShape.I => "I/Wide Flange",
        FrameShape.Tee => "Tee",
        FrameShape.FilledTube => "Concrete Filled Tube (CFT)",
        FrameShape.Encased => "Steel Encased Composite",
        FrameShape.MegaComposite => "Mega Composite Column",
        FrameShape.General => "General / Section Designer",
        _ => shape.ToString(),
    };

    /// <summary>A new section of <paramref name="shape"/> with typical dimensions, for Add New Property.</summary>
    public static FrameSectionProperty Default(FrameShape shape, string name, string material) => shape switch
    {
        FrameShape.Box => new() { Name = name, Shape = shape, Material = material, Depth = 0.6, Width = 0.6, FlangeThickness = 0.03, WebThickness = 0.03 },
        FrameShape.I => new() { Name = name, Shape = shape, Material = material, Depth = 0.6, Width = 0.3, FlangeThickness = 0.025, WebThickness = 0.015, BottomWidth = 0.3, BottomFlangeThickness = 0.025 },
        FrameShape.Pipe => new() { Name = name, Shape = shape, Material = material, Depth = 0.5, WebThickness = 0.02 },
        FrameShape.Circle => new() { Name = name, Shape = shape, Material = material, Depth = 0.6 },
        FrameShape.Channel => new() { Name = name, Shape = shape, Material = material, Depth = 0.4, Width = 0.15, FlangeThickness = 0.02, WebThickness = 0.012 },
        FrameShape.Tee => new() { Name = name, Shape = shape, Material = material, Depth = 0.4, Width = 0.3, FlangeThickness = 0.025, WebThickness = 0.015 },
        FrameShape.Angle => new() { Name = name, Shape = shape, Material = material, Depth = 0.2, Width = 0.2, FlangeThickness = 0.02, WebThickness = 0.02 },
        FrameShape.General => new() { Name = name, Shape = shape, Material = material, Given = new SectionValues(0.1, 0.001, 0.001, 0.001, 0.08, 0.08) },
        FrameShape.FilledTube => new FrameSectionProperty { Name = name, Shape = shape, SapType = "FilledTube", Material = material, Composite = new ColumnSectionInput() }.WithCompositeOutline(),
        FrameShape.Encased => new FrameSectionProperty { Name = name, Shape = shape, SapType = "Encased", Material = material,
            Composite = new ColumnSectionInput { Width = 1000, Depth = 1000, SteelDepth = 600, SteelWidth = 500 } }.WithCompositeOutline(),
        FrameShape.MegaComposite => new FrameSectionProperty { Name = name, Shape = shape, SapType = "MegaComposite", Material = material,
            Composite = ColumnSectionInput.MegaDefault }.WithCompositeOutline(),
        _ => new() { Name = name, Shape = FrameShape.Rectangle, Material = material, Depth = 0.6, Width = 0.3 },
    };

        /// <summary>A composite section with its outline (<see cref="Depth"/>, <see cref="Width"/>, tube wall) taken from
    /// <see cref="Composite"/>, in m.</summary>
    public FrameSectionProperty WithCompositeOutline()
    {
        if (Composite is not { } c) return this;
        return Shape == FrameShape.FilledTube
            ? this with { Depth = c.TubeDepth / 1000, Width = (c.Tube == TubeShape.Circular ? c.TubeDepth : c.TubeWidth) / 1000, FlangeThickness = c.TubeThickness / 1000, WebThickness = c.TubeThickness / 1000 }
            : this with { Depth = c.Depth / 1000, Width = c.Width / 1000, FlangeThickness = 0, WebThickness = 0 };
    }

    /// <summary>The section's properties: from the dimensions for a standard shape, as given for a general one; a composite
    /// section's transformed properties are worked out with its materials (see <see cref="CompositeSection"/>) into
    /// <see cref="Given"/>.</summary>
    public SectionValues Values() => Shape switch
    {
        FrameShape.General or FrameShape.FilledTube or FrameShape.Encased or FrameShape.MegaComposite => Given ?? new SectionValues(0, 0, 0, 0, 0, 0),
        FrameShape.Box => BoxValues(),
        FrameShape.Pipe => PipeValues(Depth, WebThickness),
        FrameShape.Circle => PipeValues(Depth, Depth / 2),
        _ => PlateValues(Plates()),
    };

    /// <summary>The section's outline as rectangles (x along 2 / width, y along 3 / depth, centred on the bounding box), for
    /// drawing and for the plate shapes' properties. A box is drawn as its four walls; pipes and circles are drawn as circles.</summary>
    public IReadOnlyList<(double X, double Y, double W, double H)> Plates()
    {
        double d = Depth, b = Width, tf = FlangeThickness, tw = WebThickness;
        return Shape switch
        {
            FrameShape.Rectangle => [(-b / 2, -d / 2, b, d)],
            FrameShape.Box => [(-b / 2, d / 2 - tf, b, tf), (-b / 2, -d / 2, b, tf), (-b / 2, -d / 2 + tf, tw, d - 2 * tf), (b / 2 - tw, -d / 2 + tf, tw, d - 2 * tf)],
            FrameShape.I => [(-b / 2, d / 2 - tf, b, tf), (-BottomWidth / 2, -d / 2, BottomWidth, BottomFlangeThickness),
                (-tw / 2, -d / 2 + BottomFlangeThickness, tw, d - tf - BottomFlangeThickness)],
            FrameShape.Channel => [(-b / 2, d / 2 - tf, b, tf), (-b / 2, -d / 2, b, tf), (-b / 2, -d / 2 + tf, tw, d - 2 * tf)],
            FrameShape.Tee => [(-b / 2, d / 2 - tf, b, tf), (-tw / 2, -d / 2, tw, d - tf)],
            FrameShape.Angle => [(-b / 2, -d / 2, b, tf), (-b / 2, -d / 2 + tf, tw, d - tf)],
            _ => [],
        };
    }

    private SectionValues BoxValues()
    {
        double d = Depth, b = Width, tf = FlangeThickness, tw = WebThickness;
        double bi = b - 2 * tw, di = d - 2 * tf;
        var area = b * d - bi * di;
        var i33 = (b * d * d * d - bi * di * di * di) / 12;
        var i22 = (d * b * b * b - di * bi * bi * bi) / 12;
        // thin-walled closed tube (Bredt), as SAP2000 states it for a box
        var torsion = 2 * tf * tw * Math.Pow(b - tw, 2) * Math.Pow(d - tf, 2) / (b * tw + d * tf - tw * tw - tf * tf);
        return new SectionValues(area, i33, i22, torsion, 2 * d * tw, 2 * b * tf);
    }

    private static SectionValues PipeValues(double outside, double wall)
    {
        var inside = Math.Max(0, outside - 2 * wall);
        var area = Math.PI / 4 * (outside * outside - inside * inside);
        var i = Math.PI / 64 * (Math.Pow(outside, 4) - Math.Pow(inside, 4));
        return new SectionValues(area, i, i, 2 * i, inside > 0 ? area / 2 : 0.9 * area, inside > 0 ? area / 2 : 0.9 * area);
    }

    private static SectionValues PlateValues(IReadOnlyList<(double X, double Y, double W, double H)> plates)
    {
        var area = plates.Sum(p => p.W * p.H);
        if (area <= 0) return new SectionValues(0, 0, 0, 0, 0, 0);
        var cx = plates.Sum(p => p.W * p.H * (p.X + p.W / 2)) / area;
        var cy = plates.Sum(p => p.W * p.H * (p.Y + p.H / 2)) / area;
        var i33 = plates.Sum(p => p.W * p.H * p.H * p.H / 12 + p.W * p.H * Math.Pow(p.Y + p.H / 2 - cy, 2));
        var i22 = plates.Sum(p => p.H * p.W * p.W * p.W / 12 + p.W * p.H * Math.Pow(p.X + p.W / 2 - cx, 2));
        // open thin-walled: J = Σ b t³ / 3 (solid rectangle: a b³ (1/3 − 0.21 b/a (1 − b⁴/12a⁴)))
        var torsion = plates.Count == 1
            ? RectangleTorsion(plates[0].W, plates[0].H)
            : plates.Sum(p => Math.Max(p.W, p.H) * Math.Pow(Math.Min(p.W, p.H), 3) / 3);
        var vertical = plates.Where(p => p.H >= p.W).Sum(p => p.W * p.H);
        var horizontal = plates.Where(p => p.W > p.H).Sum(p => p.W * p.H);
        var single = plates.Count == 1 ? 5.0 / 6 * area : 0;
        return new SectionValues(area, i33, i22, torsion, plates.Count == 1 ? single : vertical, plates.Count == 1 ? single : 5.0 / 6 * horizontal);
    }

    private static double RectangleTorsion(double w, double h)
    {
        double a = Math.Max(w, h), b = Math.Min(w, h);
        return a * b * b * b * (1.0 / 3 - 0.21 * b / a * (1 - Math.Pow(b, 4) / (12 * Math.Pow(a, 4))));
    }
}

/// <summary>A cable or tendon section (Define → Section Properties → Cable / Tendon Sections): material and area [m²].</summary>
public sealed record StrandSectionProperty
{
    public required string Name { get; init; }
    public bool IsTendon { get; init; }
    public string Material { get; init; } = "";
    public double Area { get; init; }
    public string Notes { get; init; } = "";
    public int Color { get; init; } = 0x00FFFF;
    /// <summary>Cable modifiers in SAP order: area, mass, weight (tendons: none).</summary>
    public IReadOnlyList<double> Modifiers { get; init; } = [1, 1, 1];
    public string Source { get; init; } = "App";
    /// <summary>Diameter of a solid round of the same area [m].</summary>
    public double Diameter => Math.Sqrt(4 * Area / Math.PI);
}

/// <summary>
/// A cable or tendon made of parallel 7-wire strands, as one equivalent section: area = number of strands × the steel area
/// of one strand, named by the convention the models use — <c>192T15.7</c> = 192 strands of 15.7 mm.
/// </summary>
public static partial class StrandBundle
{
    /// <summary>Nominal diameter [mm] and nominal steel area [mm²] of the common 7-wire prestressing strands (EN 10138-3). The
    /// area is the steel of the seven wires — less than the circle of the strand's diameter — so it is tabulated, not
    /// computed; a strand that is not listed has no area here and the user enters it.</summary>
    public static IReadOnlyList<(double DiameterMm, double AreaMm2)> StandardStrands { get; } = [(12.5, 93), (12.9, 100), (15.2, 139), (15.3, 140), (15.7, 150)];

    /// <summary>The nominal steel area [mm²] of a standard strand of <paramref name="diameterMm"/>, or null when it is not one.</summary>
    public static double? StandardArea(double diameterMm) =>
        StandardStrands.Where(strand => Math.Abs(strand.DiameterMm - diameterMm) < 1e-6).Select(strand => (double?)strand.AreaMm2).FirstOrDefault();

    /// <summary>The section area [m²] of <paramref name="count"/> strands of <paramref name="strandAreaMm2"/> each.</summary>
    public static double Area(int count, double strandAreaMm2) => count * strandAreaMm2 * 1e-6;

    /// <summary>The section name by convention: number of strands, T, strand diameter [mm] — <c>192T15.7</c>.</summary>
    public static string Name(int count, double diameterMm) =>
        $"{count.ToString(System.Globalization.CultureInfo.InvariantCulture)}T{diameterMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>The strands a name written by the convention states, or null for any other name.</summary>
    public static (int Count, double DiameterMm)? Parse(string name) =>
        Convention().Match(name.Trim()) is { Success: true } match &&
        int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) && count > 0 &&
        double.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var diameter) && diameter > 0
            ? (count, diameter) : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"^(\d+)T(\d+(?:\.\d+)?)$")]
    private static partial System.Text.RegularExpressions.Regex Convention();
}

/// <summary>Everything Get From SAP2000 reads for Define → Section Properties.</summary>
public sealed record SectionLibrary(IReadOnlyList<FrameSectionProperty> Frames, IReadOnlyList<StrandSectionProperty> Cables, IReadOnlyList<StrandSectionProperty> Tendons);
