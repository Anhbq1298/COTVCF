using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Application;

/// <summary>
/// A composite column as SAP2000 and ETABS receive it. A mega composite column goes as it is here: a rectangular reinforced concrete section of the same outline
/// and concrete grade, whose property modifiers make its stiffness, self-weight and mass those of the composite column in
/// Mcotis. Area, shear areas, torsion constant, both inertias and the weight and mass per metre are each the composite's
/// value over the rectangle's (the section's own modifiers multiply on top, as they do in Mcotis). A concrete filled tube goes
/// as <see cref="Tube"/>.
/// </summary>
internal static class CompositeSapSection
{
    /// <summary>The rectangular section for <paramref name="section"/>. <paramref name="values"/> are the composite's properties
    /// transformed to the concrete, <paramref name="weightPerMetre"/> [kN/m] and <paramref name="massPerMetre"/> [t/m] its true
    /// weight and mass; <paramref name="concrete"/> gives the unit weight and mass the rectangle would have.</summary>
    public static FrameSectionProperty Rectangle(FrameSectionProperty section, SectionValues values, double weightPerMetre, double massPerMetre, MaterialProperty? concrete)
    {
        double b = section.Width, h = section.Depth;
        var area = b * h;
        if (!(area > 0) || !(values.Area > 0)) return section;
        double Ratio(double composite, double rectangle) => rectangle > 0 && composite > 0 ? composite / rectangle : 1;
        var shearTarget = 5.0 / 6 * values.Area;
        var shear = Ratio(shearTarget, 5.0 / 6 * area);
        var torsion = values.Torsion > 0 ? values.Torsion : values.I22 + values.I33;
        var factors = new[]
        {
            Ratio(values.Area, area),
            values.ShearArea2 > 0 ? Ratio(values.ShearArea2, 5.0 / 6 * area) : shear,
            values.ShearArea3 > 0 ? Ratio(values.ShearArea3, 5.0 / 6 * area) : shear,
            Ratio(torsion, TorsionConstant(b, h)),
            Ratio(values.I22, h * b * b * b / 12),
            Ratio(values.I33, b * h * h * h / 12),
            concrete?.MassPerVolume is > 0 and var rho ? Ratio(massPerMetre, rho * area) : 1,
            concrete?.WeightPerVolume is > 0 and var gamma ? Ratio(weightPerMetre, gamma * area) : 1,
        };
        var own = section.Modifiers.Count >= 8 ? section.Modifiers : [1.0, 1, 1, 1, 1, 1, 1, 1];
        return section with
        {
            Shape = FrameShape.Rectangle, SapType = "", Composite = null, Given = null, SteelMaterial = "", RebarMaterial = "",
            FlangeThickness = 0, WebThickness = 0,
            Modifiers = factors.Select((factor, index) => factor * own[index]).ToArray(),
            Notes = $"Mcotis mega composite column {section.Name} as a rectangular RC column: the property modifiers give the stiffness, weight and mass of the composite. {section.Notes}".Trim(),
        };
    }

    /// <summary>St Venant torsion constant of a b × h rectangle, J = a·s³·(1/3 − 0.21·(s/a)·(1 − s⁴/(12 a⁴))), a ≥ s.</summary>
    public static double TorsionConstant(double b, double h)
    {
        double a = Math.Max(b, h), s = Math.Min(b, h);
        return a * s * s * s * (1.0 / 3 - 0.21 * (s / a) * (1 - Math.Pow(s / a, 4) / 12));
    }

    /// <summary>A concrete filled tube as a steel tube (box or pipe) of the same outline and wall, in the tube's steel: its
    /// modifiers make E·A, E·I, G·J (E<sub>steel</sub> × modifier = E<sub>cm</sub> × the composite's value, transformed to the
    /// concrete) and the weight and mass per metre those of the composite tube in Mcotis.</summary>
    public static FrameSectionProperty Tube(FrameSectionProperty section, CompositeParts parts, SectionValues values, double concreteModulus,
        double weightPerMetre, double massPerMetre, MaterialProperty? steel)
    {
        if (steel is not { ElasticModulus: > 0 } || section.Composite is not { } c || !(parts.SteelArea > 0) || !(values.Area > 0) || !(concreteModulus > 0)) return section;
        var circular = c.Tube == TubeShape.Circular;
        double t = c.TubeThickness / 1000, h = c.TubeDepth / 1000, b = circular ? h : c.TubeWidth / 1000;
        // J of the tube alone: a thin-walled closed section's 4 Am² t / s, or 2 I of a pipe
        double jTube = circular ? 2 * parts.SteelI33
            : 4 * Math.Pow((b - t) * (h - t), 2) * t / (2 * ((b - t) + (h - t)));
        var e = steel.ElasticModulus;
        double Ratio(double composite, double tube) => tube > 0 && composite > 0 ? concreteModulus * composite / (e * tube) : 1;
        var shear = Ratio(5.0 / 6 * values.Area, 5.0 / 6 * parts.SteelArea);
        var torsion = values.Torsion > 0 ? values.Torsion : values.I22 + values.I33;
        double Unit(double perMetre, double unit, double area) => unit > 0 && perMetre > 0 ? perMetre / (unit * area) : 1;
        var factors = new[]
        {
            Ratio(values.Area, parts.SteelArea),
            values.ShearArea2 > 0 ? Ratio(values.ShearArea2, 5.0 / 6 * parts.SteelArea) : shear,
            values.ShearArea3 > 0 ? Ratio(values.ShearArea3, 5.0 / 6 * parts.SteelArea) : shear,
            Ratio(torsion, jTube),
            Ratio(values.I22, parts.SteelI22),
            Ratio(values.I33, parts.SteelI33),
            Unit(massPerMetre, steel.MassPerVolume, parts.SteelArea),
            Unit(weightPerMetre, steel.WeightPerVolume, parts.SteelArea),
        };
        var own = section.Modifiers.Count >= 8 ? section.Modifiers : [1.0, 1, 1, 1, 1, 1, 1, 1];
        return section with
        {
            Shape = circular ? FrameShape.Pipe : FrameShape.Box, SapType = "", Composite = null, Given = null, Material = section.SteelMaterial,
            SteelMaterial = "", RebarMaterial = "", Depth = h, Width = b, FlangeThickness = t, WebThickness = t, CornerRadius = 0,
            Modifiers = factors.Select((factor, index) => factor * own[index]).ToArray(),
            Notes = $"Mcotis concrete filled tube {section.Name} as a steel tube: the property modifiers give the stiffness, weight and mass of the composite. {section.Notes}".Trim(),
        };
    }
}
