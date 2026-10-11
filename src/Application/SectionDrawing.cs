using System.Globalization;
using System.Text.RegularExpressions;
using COT_VCF.Domain;

namespace COT_VCF.Application;

public enum SectionMaterial { Steel, Concrete }

/// <summary>One material of a cross-section: convex polygons in the section's axes — local 2 (depth) and local 3 (width) —
/// in m, centred on the section. <see cref="Shell"/> marks the region that encloses the others (the tube of a CFT, the
/// concrete of an encased section): the 3D view draws it see-through so the material inside shows.</summary>
public sealed record SectionRegion(SectionMaterial Material, IReadOnlyList<(double X2, double X3)[]> Polygons, bool Shell = false);

/// <summary>
/// A frame cross-section as it really is, for the 3D view: its materials as <see cref="Regions"/>, the inner ones first.
/// <see cref="Recovered"/> is true for a section the model holds only as transformed properties (Section Designer …) whose
/// dimensions were read from its name and checked against those properties (<see cref="SectionDrawings.Recover"/>).
/// </summary>
public sealed record SectionDrawing(string Description, IReadOnlyList<SectionRegion> Regions, bool Recovered = false)
{
    /// <summary>More than one material: the shell is drawn see-through.</summary>
    public bool Composite => Regions.Select(region => region.Material).Distinct().Count() > 1;
}

/// <summary>
/// The real outline of a frame section where an equivalent rectangle would mislead: the composite sections (CFT tube with
/// its concrete core, steel encased and mega composite with their profiles) and, for a general / Section Designer section,
/// the outline its name states — accepted only when it reproduces the section's stored A, I33 and I22. A standard shape
/// (box, I, pipe …) needs none: its <c>FemSectionShape</c> already is its outline.
/// </summary>
public static partial class SectionDrawings
{
    private const double Mm = 0.001;
    private const int CircleSegments = 24;
    /// <summary>A recovered outline must reproduce each stored property within this.</summary>
    public const double RecoveryTolerance = 0.01;

    /// <summary>The drawing of <paramref name="section"/>, or null when its plain shape is its outline (or nothing better is
    /// known). <paramref name="modulusGPa"/> gives a material's E by name, for the transformed properties of a recovery.</summary>
    public static SectionDrawing? For(FrameSectionProperty section, Func<string, double?> modulusGPa)
    {
        if (section.Composite is { } c)
            switch (section.Shape)
            {
                case FrameShape.FilledTube when c.Tube == TubeShape.Circular:
                    return CircularTube(c.TubeDepth * Mm, c.TubeThickness * Mm);
                case FrameShape.FilledTube:
                    return Tube(c.TubeDepth * Mm, c.TubeWidth * Mm, c.TubeThickness * Mm, filled: true);
                case FrameShape.Encased:
                    return Encased($"Steel encased {Text(c.Depth)} × {Text(c.Width)} mm with I {Text(c.SteelDepth)} × {Text(c.SteelWidth)}", c, [(0, 0)]);
                case FrameShape.MegaComposite:
                    return Encased($"Mega composite {Text(c.Depth)} × {Text(c.Width)} mm with {c.ProfileCount} × I {Text(c.SteelDepth)} × {Text(c.SteelWidth)}", c,
                        CompositeSection.ProfileCentres(c));
            }
        return section.Shape == FrameShape.General ? Recover(section, modulusGPa) : null;
    }

    private static string Text(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static (double, double)[] Rect(double x2Min, double x2Max, double x3Min, double x3Max) =>
        [(x2Min, x3Min), (x2Max, x3Min), (x2Max, x3Max), (x2Min, x3Max)];

    /// <summary>A rectangular tube of depth × width × wall [m] as its four walls, with its concrete core when filled.</summary>
    private static SectionDrawing Tube(double depth, double width, double wall, bool filled, bool recovered = false)
    {
        var t = Math.Min(wall, Math.Min(depth, width) / 2);
        SectionRegion walls = new(SectionMaterial.Steel,
        [
            Rect(depth / 2 - t, depth / 2, -width / 2, width / 2), Rect(-depth / 2, -depth / 2 + t, -width / 2, width / 2),
            Rect(-depth / 2 + t, depth / 2 - t, -width / 2, -width / 2 + t), Rect(-depth / 2 + t, depth / 2 - t, width / 2 - t, width / 2),
        ], Shell: true);
        var size = $"{Text(depth * 1000)} × {Text(width * 1000)} × {Text(wall * 1000)} mm";
        if (!filled || depth - 2 * t <= 0 || width - 2 * t <= 0) return new($"Steel tube {size}", [walls], recovered);
        SectionRegion core = new(SectionMaterial.Concrete, [Rect(-depth / 2 + t, depth / 2 - t, -width / 2 + t, width / 2 - t)]);
        return new($"Concrete filled tube {size}", [core, walls], recovered);
    }

    private static SectionDrawing CircularTube(double diameter, double wall)
    {
        double r = diameter / 2, inner = Math.Max(0, r - wall);
        (double, double) At(double radius, int index) => (radius * Math.Cos(2 * Math.PI * index / CircleSegments), radius * Math.Sin(2 * Math.PI * index / CircleSegments));
        SectionRegion ring = new(SectionMaterial.Steel, Enumerable.Range(0, CircleSegments)
            .Select(index => new[] { At(inner, index), At(r, index), At(r, index + 1), At(inner, index + 1) }).ToArray(), Shell: true);
        SectionRegion core = new(SectionMaterial.Concrete, [Enumerable.Range(0, CircleSegments).Select(index => At(inner, index)).ToArray()]);
        var size = $"Ø {Text(diameter * 1000)} × {Text(wall * 1000)} mm";
        return inner > 0 ? new($"Concrete filled tube {size}", [core, ring]) : new($"Steel round {size}", [ring]);
    }

    /// <summary>The concrete outline with an I profile at every centre (Y across the width, Z along the depth; mm): flanges
    /// across the width, the strong axis in-plane — as the section forms draw it. Reinforcement is not drawn.</summary>
    private static SectionDrawing Encased(string description, ColumnSectionInput c, IReadOnlyList<(double Y, double Z)> centres)
    {
        double ha = c.SteelDepth * Mm, ba = c.SteelWidth * Mm, tf = Math.Min(c.FlangeThickness * Mm, ha / 2), tw = c.WebThickness * Mm;
        var plates = new List<(double, double)[]>();
        foreach (var (y, z) in centres)
        {
            var (along, across) = (z * Mm, y * Mm);
            plates.Add(Rect(along + ha / 2 - tf, along + ha / 2, across - ba / 2, across + ba / 2));
            plates.Add(Rect(along - ha / 2, along - ha / 2 + tf, across - ba / 2, across + ba / 2));
            if (ha - 2 * tf > 0) plates.Add(Rect(along - ha / 2 + tf, along + ha / 2 - tf, across - tw / 2, across + tw / 2));
        }
        return new(description,
        [
            new(SectionMaterial.Steel, plates),
            new(SectionMaterial.Concrete, [Rect(-c.Depth * Mm / 2, c.Depth * Mm / 2, -c.Width * Mm / 2, c.Width * Mm / 2)], Shell: true),
        ]);
    }

    // ---- a general / Section Designer section: the outline its name states, if it reproduces the stored properties ----

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*[xX×]\s*(\d+(?:\.\d+)?)(?:\s*[xX×]\s*(\d+(?:\.\d+)?))?")]
    private static partial Regex Dimensions();

    [GeneratedRegex(@"S\d{3}")]
    private static partial Regex SteelGrade();

    /// <summary>
    /// The outline of a section held only as transformed properties, read from its name and checked against them:
    /// <list type="bullet">
    /// <item>three dimensions (<c>…1200x2000x150…</c>) — a rectangular tube, hollow or concrete filled, accepted when A,
    /// I33 and I22 each match within <see cref="RecoveryTolerance"/> (a filled tube transformed to the section's own
    /// material with Ea / Ec);</item>
    /// <item>two dimensions (<c>…1.5x3…</c>) on a concrete section — a solid block whose embedded steel is not known:
    /// accepted when the stored properties are those of the block plus a little steel (never less than the block's own,
    /// at most 35 % more area and 60 % more inertia). It is drawn as concrete alone.</item>
    /// </list>
    /// Either way the orientation — which dimension is the depth — is the one the inertias agree with. Null when the name
    /// states no dimensions or they do not fit: the section is then drawn as its equivalent solid, as before.
    /// </summary>
    public static SectionDrawing? Recover(FrameSectionProperty section, Func<string, double?> modulusGPa)
    {
        if (section.Given is not { Area: > 0, I33: > 0, I22: > 0 } given || Dimensions().Match(section.Name) is not { Success: true } match) return null;
        static double Metres(double value) => value > 50 ? value * Mm : value;   // sections are named in mm, mega columns in m
        double Number(int group) => Metres(double.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture));
        static bool Near(double value, double stored) => Math.Abs(value - stored) <= RecoveryTolerance * stored;
        var (a, b) = (Number(1), Number(2));
        var orientations = a == b ? [(a, b)] : new[] { (a, b), (b, a) };

        if (match.Groups[3].Success)
        {
            var wall = Number(3);
            var concrete = modulusGPa(section.Material);
            var steels = SteelGrade().Matches(section.Name).Select(grade => modulusGPa(grade.Value)).Append(210).Append(205).Append(200)
                .Where(modulus => modulus is > 0).Select(modulus => modulus!.Value).Distinct().ToArray();
            foreach (var (depth, width) in orientations)
            {
                if (2 * wall >= Math.Min(depth, width)) continue;
                double coreDepth = depth - 2 * wall, coreWidth = width - 2 * wall;
                var (areaCore, i33Core, i22Core) = (coreDepth * coreWidth, coreWidth * Math.Pow(coreDepth, 3) / 12, coreDepth * Math.Pow(coreWidth, 3) / 12);
                var (areaTube, i33Tube, i22Tube) = (depth * width - areaCore, width * Math.Pow(depth, 3) / 12 - i33Core, depth * Math.Pow(width, 3) / 12 - i22Core);
                if (Near(areaTube, given.Area) && Near(i33Tube, given.I33) && Near(i22Tube, given.I22)) return Tube(depth, width, wall, filled: false, recovered: true);
                if (concrete is not > 0) continue;
                foreach (var steel in steels)
                {
                    var n = steel / concrete.Value;
                    if (Near(areaCore + n * areaTube, given.Area) && Near(i33Core + n * i33Tube, given.I33) && Near(i22Core + n * i22Tube, given.I22))
                        return Tube(depth, width, wall, filled: true, recovered: true);
                }
            }
            return null;
        }

        if (!IsConcrete(section.Material)) return null;
        foreach (var (depth, width) in orientations)
        {
            static bool Carries(double block, double stored, double most) => stored >= block * (1 - RecoveryTolerance) && stored <= block * most;
            if (Carries(depth * width, given.Area, 1.35) && Carries(width * Math.Pow(depth, 3) / 12, given.I33, 1.6) && Carries(depth * Math.Pow(width, 3) / 12, given.I22, 1.6))
                return new($"Concrete {Text(depth * 1000)} × {Text(width * 1000)} mm (embedded steel not known)",
                    [new(SectionMaterial.Concrete, [Rect(-depth / 2, depth / 2, -width / 2, width / 2)])], Recovered: true);
        }
        return null;
    }

    /// <summary>A material named as a concrete grade (C70/85 …) or as concrete.</summary>
    public static bool IsConcrete(string? material) => material is { Length: > 1 } name &&
        (name[0] is 'C' or 'c' && char.IsDigit(name[1]) || name.Contains("conc", StringComparison.OrdinalIgnoreCase));
}
