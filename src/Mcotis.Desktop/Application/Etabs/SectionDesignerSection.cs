namespace Mcotis.Desktop.Application.Etabs;

/// <summary>A Section Designer section worked out as ETABS does: A, I33, I22 transformed to the base material [m², m⁴]
/// and the true weight and mass per metre (each shape with its own material). <see cref="Exact"/> is false when a shape
/// lies partly over different materials and its share was integrated on a fine grid instead of exactly.</summary>
public sealed record SectionDesignerValues(double Area, double I33, double I22, double WeightPerLengthKNPerM, double MassPerLengthTPerM, bool Exact);

/// <summary>
/// Properties of a Section Designer section from its shapes. Rules, checked against ETABS 23.2 on the "RSC 3.52x2.64 P"
/// arch rib (ETABS: A 6.23357 m², I33 9.82927 m⁴, I22 5.02872 m⁴ — reproduced to the fifth figure):
/// <list type="bullet">
/// <item>a shape higher in Z-order replaces what it covers (an embedded steel shape displaces the concrete; an opening
/// removes it);</item>
/// <item>circles and pipes are the regular 32-sided polygons inscribed in them, as ETABS draws them;</item>
/// <item>each shape counts with E / E_base for stiffness, and its own unit weight and mass for the self-weight;</item>
/// <item>X runs along the local 3-axis and Y along the local 2-axis, so I33 = ∫ y² dA and I22 = ∫ x² dA about the
/// transformed centroid. The section ANGLE of an .e2k SDSECTION does not rotate these (ETABS reports them unrotated).</item>
/// </list>
/// Each shape adds (its weight − the weight under it) × its own integrals, exact when everything under it is one material;
/// otherwise its share is integrated cell by cell.
/// </summary>
public static class SectionDesignerSection
{
    public const int CircleSides = 32;
    private const int Samples = 48, GridCells = 400;

    /// <summary>A shape's material: E [kN/m²], unit weight [kN/m³] and unit mass [t/m³]; null when unknown.</summary>
    public static SectionDesignerValues Compute(IReadOnlyList<EtabsSdShape> shapes, double baseModulus,
        Func<string, (double E, double Weight, double Mass)?> material)
    {
        if (!(baseModulus > 0)) throw new InvalidOperationException("The Section Designer base material has no elastic modulus.");
        var ordered = shapes.OrderBy(shape => shape.ZOrder).Select(shape =>
        {
            (double E, double Weight, double Mass) props = (0, 0, 0);
            if (shape.Material is { } name)
                props = material(name) ?? throw new InvalidOperationException($"Section Designer material {name} is not defined in the model.");
            return (Shape: shape, Region: Region(shape), Stiffness: props.E / baseModulus, props.Weight, props.Mass);
        }).ToArray();

        double area = 0, sy = 0, sx = 0, iyy = 0, ixx = 0, weight = 0, mass = 0;
        var exact = true;
        for (var k = 0; k < ordered.Length; k++)
        {
            var (_, region, stiffness, unitWeight, unitMass) = ordered[k];
            int? Under(double x, double y)
            {
                for (var j = k - 1; j >= 0; j--) if (Contains(ordered[j].Region, x, y)) return j;
                return null;
            }
            var (minX, minY, maxX, maxY) = Bounds(region);
            var under = new HashSet<int?>();
            for (var i = 0; i < Samples; i++)
            for (var j = 0; j < Samples; j++)
            {
                var x = minX + (i + 0.5) / Samples * (maxX - minX);
                var y = minY + (j + 0.5) / Samples * (maxY - minY);
                if (Contains(region, x, y)) under.Add(Under(x, y));
            }
            if (under.Count <= 1)
            {
                var below = under.SingleOrDefault();
                double ds = stiffness - (below is { } b ? ordered[b].Stiffness : 0);
                double dw = unitWeight - (below is { } w ? ordered[w].Weight : 0);
                double dm = unitMass - (below is { } m ? ordered[m].Mass : 0);
                foreach (var (polygon, sign) in region)
                {
                    var (a, fx, fy, gxx, gyy) = Integrals(polygon);
                    area += sign * ds * a; sx += sign * ds * fy; sy += sign * ds * fx; ixx += sign * ds * gxx; iyy += sign * ds * gyy;
                    weight += sign * dw * a; mass += sign * dm * a;
                }
                continue;
            }
            // several materials under the shape: integrate its share cell by cell
            exact = false;
            double cw = (maxX - minX) / GridCells, ch = (maxY - minY) / GridCells, cell = cw * ch;
            for (var i = 0; i < GridCells; i++)
            for (var j = 0; j < GridCells; j++)
            {
                var x = minX + (i + 0.5) * cw;
                var y = minY + (j + 0.5) * ch;
                if (!Contains(region, x, y)) continue;
                var below = Under(x, y);
                double ds = stiffness - (below is { } b ? ordered[b].Stiffness : 0);
                area += ds * cell; sx += ds * y * cell; sy += ds * x * cell;
                ixx += ds * (y * y + ch * ch / 12) * cell; iyy += ds * (x * x + cw * cw / 12) * cell;
                weight += (unitWeight - (below is { } w ? ordered[w].Weight : 0)) * cell;
                mass += (unitMass - (below is { } m ? ordered[m].Mass : 0)) * cell;
            }
        }
        if (!(area > 0)) throw new InvalidOperationException("The Section Designer section has no area.");
        var yc = sx / area;
        var xc = sy / area;
        return new SectionDesignerValues(area, ixx - area * yc * yc, iyy - area * xc * xc, weight, mass, exact);
    }

    /// <summary>The shape as polygons with a sign: +1 material, −1 a hole (a pipe's bore).</summary>
    private static (double X, double Y)[][] Polygons(EtabsSdShape shape) => shape.Kind switch
    {
        EtabsSdShapeKind.Rectangle => [Rectangle(shape)],
        EtabsSdShapeKind.Circle => [Circle(shape.X, shape.Y, shape.Depth / 2)],
        _ => [Circle(shape.X, shape.Y, shape.Depth / 2), Circle(shape.X, shape.Y, shape.Depth / 2 - shape.Thickness)],
    };

    private static ((double X, double Y)[] Polygon, int Sign)[] Region(EtabsSdShape shape)
    {
        var polygons = Polygons(shape);
        if (shape.Kind == EtabsSdShapeKind.Pipe && !(shape.Depth / 2 - shape.Thickness > 0)) return [(polygons[0], 1)];
        return polygons.Select((polygon, index) => (polygon, index == 0 ? 1 : -1)).ToArray();
    }

    private static (double X, double Y)[] Rectangle(EtabsSdShape shape)
    {
        double hw = shape.Width / 2, hd = shape.Depth / 2, angle = shape.RotationDeg * Math.PI / 180;
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return new[] { (-hw, -hd), (hw, -hd), (hw, hd), (-hw, hd) }
            .Select(p => (shape.X + p.Item1 * c - p.Item2 * s, shape.Y + p.Item1 * s + p.Item2 * c)).ToArray();
    }

    private static (double X, double Y)[] Circle(double x, double y, double radius) => Enumerable.Range(0, CircleSides)
        .Select(i => (x + radius * Math.Cos(2 * Math.PI * i / CircleSides), y + radius * Math.Sin(2 * Math.PI * i / CircleSides))).ToArray();

    /// <summary>Area, ∫x dA, ∫y dA, ∫y² dA, ∫x² dA of a simple polygon about the origin (counter-clockwise or not).</summary>
    private static (double A, double Sx, double Sy, double Ixx, double Iyy) Integrals((double X, double Y)[] p)
    {
        double a = 0, fx = 0, fy = 0, gxx = 0, gyy = 0;
        for (var i = 0; i < p.Length; i++)
        {
            var (x0, y0) = p[i];
            var (x1, y1) = p[(i + 1) % p.Length];
            var cross = x0 * y1 - x1 * y0;
            a += cross / 2;
            fx += (x0 + x1) * cross / 6;
            fy += (y0 + y1) * cross / 6;
            gxx += (y0 * y0 + y0 * y1 + y1 * y1) * cross / 12;
            gyy += (x0 * x0 + x0 * x1 + x1 * x1) * cross / 12;
        }
        var sign = a < 0 ? -1 : 1;
        return (sign * a, sign * fx, sign * fy, sign * gxx, sign * gyy);
    }

    private static bool Contains(((double X, double Y)[] Polygon, int Sign)[] region, double x, double y)
    {
        var inside = false;
        foreach (var (polygon, sign) in region)
            if (Inside(polygon, x, y)) inside = sign > 0;
            else if (sign > 0) return false;
        return inside;
    }

    private static bool Inside((double X, double Y)[] p, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
            if ((p[i].Y > y) != (p[j].Y > y) && x < (p[j].X - p[i].X) * (y - p[i].Y) / (p[j].Y - p[i].Y) + p[i].X)
                inside = !inside;
        return inside;
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(((double X, double Y)[] Polygon, int Sign)[] region)
    {
        var points = region.Where(item => item.Sign > 0).SelectMany(item => item.Polygon).ToArray();
        return (points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
    }
}
