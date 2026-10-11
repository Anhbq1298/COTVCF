using System.Windows.Media;
using System.Windows.Media.Media3D;
using COT_VCF.Domain;
using COT_VCF.Geometry;

namespace COT_VCF.Presentation.Controls;

/// <summary>
/// Builds a to-scale 3D model of the structure: every frame is its SAP cross-section extruded along the member, oriented
/// by SAP's default local axes and the member's local-axis angle; every cable is a rod of its section area. Section
/// outlines are split into convex pieces (I = 3 plates, box = 4 walls, pipe = ring segments…) so each piece is a simple
/// prism with flat-shaded faces. Coordinates are SAP global (m), Z up.
/// </summary>
public static class StructureModel3D
{
    public sealed record MemberVisual(string Name, GeometryModel3D Model, System.Windows.Media.Media3D.Material BaseMaterial);
    public sealed record Scene(Model3DGroup Model, Rect3D Bounds, int FramesWithSapShape, int FramesApproximated,
        IReadOnlyList<MemberVisual> MemberVisuals);

    private const int CircleSegments = 20;

    public static Scene Build(GeometryModel model, IReadOnlyDictionary<string, FemSectionProperties>? sections)
    {
        var group = new Model3DGroup();
        var steel = Material(Color.FromRgb(0x7D, 0x8A, 0x99));
        var concrete = Material(Color.FromRgb(0xB9, 0xB3, 0xA8));
        var cable = Material(Color.FromRgb(0x2E, 0x90, 0xFA));
        var unknown = Material(Color.FromRgb(0xA0, 0xA6, 0xAE));
        var memberVisuals = new List<MemberVisual>();
        int withShape = 0, approximated = 0;

        var span = model.Points.Count == 0 ? 1 : Math.Max(1e-3, Extent(model));
        foreach (var member in model.Members)
        {
            if (!model.Points.TryGetValue(member.Start, out var a) || !model.Points.TryGetValue(member.End, out var b)) continue;
            var start = new Point3D(a.Position.X, a.Position.Y, a.Position.Z);
            var end = new Point3D(b.Position.X, b.Position.Y, b.Position.Z);
            if ((end - start).Length < 1e-9) continue;
            var section = sections?.GetValueOrDefault(member.Name);
            var mesh = new MeshBuilder();
            System.Windows.Media.Media3D.Material material;
            if (member.Kind == MemberKind.Frame)
            {
                var shape = section?.Shape;
                if (shape is null) approximated++;
                else if (shape.Kind == SectionShapeKind.Equivalent) approximated++;
                else withShape++;
                var outline = shape is not null ? Outline(shape) : Rectangle(span * 0.004, span * 0.004, 0, 0);
                material = shape is null ? unknown : IsConcrete(section?.MaterialName) ? concrete : steel;
                Extrude(mesh, outline, start, end, section?.LocalAngleDeg ?? 0);
            }
            else
            {
                // Cables: solid rod of the gross section area (a minimum size keeps very thin strands visible).
                var area = section is { } s ? (s.GrossAreaM2 > 0 ? s.GrossAreaM2 : s.AreaM2) : 0;
                var diameter = Math.Max(area > 0 ? Math.Sqrt(4 * area / Math.PI) : 0, span * 0.0015);
                material = cable;
                Extrude(mesh, Circle(diameter, 0, 12), start, end, 0);
            }
            if (!mesh.HasGeometry) continue;
            var visual = new GeometryModel3D(mesh.ToMesh(), material) { BackMaterial = material };
            group.Children.Add(visual);
            memberVisuals.Add(new MemberVisual(member.Name, visual, material));
        }
        return new Scene(group, group.Bounds, withShape, approximated, memberVisuals);
    }

    private static double Extent(GeometryModel model)
    {
        var xs = model.Points.Values.Select(point => point.Position.X).ToArray();
        var ys = model.Points.Values.Select(point => point.Position.Y).ToArray();
        var zs = model.Points.Values.Select(point => point.Position.Z).ToArray();
        return Math.Max(xs.Max() - xs.Min(), Math.Max(ys.Max() - ys.Min(), zs.Max() - zs.Min()));
    }

    private static bool IsConcrete(string? material) => material is { } name &&
        (name.StartsWith("C", StringComparison.OrdinalIgnoreCase) && name.Length > 1 && char.IsDigit(name[1]) ||
         name.Contains("conc", StringComparison.OrdinalIgnoreCase) || name.StartsWith("4000Psi", StringComparison.OrdinalIgnoreCase));

    private static DiffuseMaterial Material(Color color)
    {
        var material = new DiffuseMaterial(new SolidColorBrush(color));
        material.Freeze();
        return material;
    }

    // ---- Section outlines as convex polygons in (local 2, local 3) coordinates, centred on the section ----

    private static IReadOnlyList<(double X2, double X3)[]> Outline(FemSectionShape shape)
    {
        double d = shape.Depth, b = shape.Width;
        double tf = Positive(shape.FlangeThickness, d * 0.08), tw = Positive(shape.WebThickness, b * 0.08);
        switch (shape.Kind)
        {
            case SectionShapeKind.Box:
            {
                tf = Math.Min(tf, d / 2); tw = Math.Min(tw, b / 2);
                return
                [
                    Rect(d / 2 - tf, d / 2, -b / 2, b / 2), Rect(-d / 2, -d / 2 + tf, -b / 2, b / 2),
                    Rect(-d / 2 + tf, d / 2 - tf, -b / 2, -b / 2 + tw), Rect(-d / 2 + tf, d / 2 - tf, b / 2 - tw, b / 2)
                ];
            }
            case SectionShapeKind.I:
            {
                var bb = Positive(shape.BottomWidth, b);
                var tfb = Positive(shape.BottomFlangeThickness, tf);
                return
                [
                    Rect(d / 2 - tf, d / 2, -b / 2, b / 2), Rect(-d / 2, -d / 2 + tfb, -bb / 2, bb / 2),
                    Rect(-d / 2 + tfb, d / 2 - tf, -tw / 2, tw / 2)
                ];
            }
            case SectionShapeKind.Channel:
                return
                [
                    Rect(d / 2 - tf, d / 2, -b / 2, b / 2), Rect(-d / 2, -d / 2 + tf, -b / 2, b / 2),
                    Rect(-d / 2 + tf, d / 2 - tf, -b / 2, -b / 2 + tw)
                ];
            case SectionShapeKind.Tee:
                return [Rect(d / 2 - tf, d / 2, -b / 2, b / 2), Rect(-d / 2, d / 2 - tf, -tw / 2, tw / 2)];
            case SectionShapeKind.Angle:
                return [Rect(-d / 2, -d / 2 + tf, -b / 2, b / 2), Rect(-d / 2 + tf, d / 2, -b / 2, -b / 2 + tw)];
            case SectionShapeKind.Circle:
                return Circle(d, 0, CircleSegments);
            case SectionShapeKind.Pipe:
                return Circle(d, Math.Min(Positive(shape.WebThickness, d * 0.05), d / 2), CircleSegments);
            default:
                return Rectangle(d, b, 0, 0);
        }
    }

    private static double Positive(double value, double fallback) => double.IsFinite(value) && value > 0 ? value : fallback;

    private static IReadOnlyList<(double, double)[]> Rectangle(double depth, double width, double offset2, double offset3) =>
        [Rect(offset2 - depth / 2, offset2 + depth / 2, offset3 - width / 2, offset3 + width / 2)];

    private static (double, double)[] Rect(double x2Min, double x2Max, double x3Min, double x3Max) =>
        [(x2Min, x3Min), (x2Max, x3Min), (x2Max, x3Max), (x2Min, x3Max)];

    /// <summary>Solid disc (wall = 0) as one polygon, or a ring as convex wall segments.</summary>
    private static IReadOnlyList<(double, double)[]> Circle(double diameter, double wall, int segments)
    {
        var r = diameter / 2;
        (double, double) At(double radius, int index) =>
            (radius * Math.Cos(2 * Math.PI * index / segments), radius * Math.Sin(2 * Math.PI * index / segments));
        if (wall <= 0 || wall >= r) return [Enumerable.Range(0, segments).Select(index => At(r, index)).ToArray()];
        return Enumerable.Range(0, segments).Select(index => new[] { At(r - wall, index), At(r, index), At(r, index + 1), At(r - wall, index + 1) }).ToArray();
    }

    // ---- Extrusion ----

    /// <summary>SAP default local axes: 1 along the member; for a vertical member 2 = +X, otherwise 2 is the upward
    /// normal in the vertical plane through the member; 3 = 1 × 2; then rotated by the local-axis angle about 1.</summary>
    internal static (Vector3D L1, Vector3D L2, Vector3D L3) LocalAxes(Point3D start, Point3D end, double angleDeg)
    {
        var l1 = end - start;
        l1.Normalize();
        Vector3D l2;
        var horizontal = Math.Sqrt(l1.X * l1.X + l1.Y * l1.Y);
        if (horizontal < 1e-3) l2 = new Vector3D(1, 0, 0);
        else
        {
            l2 = new Vector3D(0, 0, 1) - l1 * l1.Z;
            l2.Normalize();
        }
        var l3 = Vector3D.CrossProduct(l1, l2);
        l3.Normalize();
        if (Math.Abs(angleDeg) > 1e-9)
        {
            var radians = angleDeg * Math.PI / 180;
            var rotated2 = l2 * Math.Cos(radians) + l3 * Math.Sin(radians);
            var rotated3 = -l2 * Math.Sin(radians) + l3 * Math.Cos(radians);
            (l2, l3) = (rotated2, rotated3);
        }
        return (l1, l2, l3);
    }

    /// <summary>Small colour-coded local 1/2/3 arrows at frame midpoints. Selected frames alone are shown when
    /// a frame selection exists; otherwise every frame is shown.</summary>
    public static Model3DGroup BuildLocalAxes(GeometryModel model, IReadOnlyDictionary<string, FemSectionProperties>? sections,
        IReadOnlySet<string>? selectedFrames = null)
    {
        var group = new Model3DGroup();
        var meshes = new[] { new MeshBuilder(), new MeshBuilder(), new MeshBuilder() };
        foreach (var member in model.Members.Where(member => member.Kind == MemberKind.Frame &&
                     (selectedFrames is null || selectedFrames.Count == 0 || selectedFrames.Contains(member.Name))))
        {
            if (!model.Points.TryGetValue(member.Start, out var a) || !model.Points.TryGetValue(member.End, out var b)) continue;
            var start = new Point3D(a.Position.X, a.Position.Y, a.Position.Z);
            var end = new Point3D(b.Position.X, b.Position.Y, b.Position.Z);
            var length = (end - start).Length;
            if (length < 1e-6) continue;
            var section = sections?.GetValueOrDefault(member.Name);
            var (l1, l2, l3) = LocalAxes(start, end, section?.LocalAngleDeg ?? 0);
            var depth = section?.Shape?.Depth ?? 0;
            var width = section?.Shape?.Width ?? 0;
            var origin = start + (end - start) * 0.5 + l2 * Math.Max(depth * 0.55, 0.08) + l3 * Math.Max(width * 0.55, 0.08);
            var arrowLength = Math.Clamp(length * 0.18, 0.7, 2.2);
            AddAxisArrow(meshes[0], origin, l1, arrowLength);
            AddAxisArrow(meshes[1], origin, l2, arrowLength);
            AddAxisArrow(meshes[2], origin, l3, arrowLength);
        }
        var colours = new[] { Color.FromRgb(0xC6, 0x28, 0x28), Color.FromRgb(0x2E, 0x7D, 0x32), Color.FromRgb(0x25, 0x63, 0xEB) };
        for (var index = 0; index < meshes.Length; index++)
            if (meshes[index].HasGeometry)
            {
                var material = Material(colours[index]);
                group.Children.Add(new GeometryModel3D(meshes[index].ToMesh(), material) { BackMaterial = material });
            }
        return group;
    }

    private static void AddAxisArrow(MeshBuilder mesh, Point3D origin, Vector3D direction, double length)
    {
        var tip = origin + direction * length;
        var headBase = origin + direction * (length * 0.73);
        var radius = Math.Clamp(length * 0.022, 0.02, 0.055);
        Extrude(mesh, Circle(radius * 2, 0, 8), origin, headBase, 0);
        var (_, u, v) = LocalAxes(origin, tip, 0);
        for (var index = 0; index < 8; index++)
        {
            var a = index * Math.PI / 4;
            var b = (index + 1) * Math.PI / 4;
            mesh.AddTriangle(headBase + (u * Math.Cos(a) + v * Math.Sin(a)) * radius * 2.8,
                headBase + (u * Math.Cos(b) + v * Math.Sin(b)) * radius * 2.8, tip);
        }
    }

    private static void Extrude(MeshBuilder mesh, IReadOnlyList<(double X2, double X3)[]> outline, Point3D start, Point3D end, double angleDeg)
    {
        var (_, l2, l3) = LocalAxes(start, end, angleDeg);
        foreach (var polygon in outline)
        {
            var bottom = polygon.Select(p => start + l2 * p.X2 + l3 * p.X3).ToArray();
            var top = polygon.Select(p => end + l2 * p.X2 + l3 * p.X3).ToArray();
            for (var index = 0; index < polygon.Length; index++)
            {
                var next = (index + 1) % polygon.Length;
                mesh.AddQuad(bottom[index], bottom[next], top[next], top[index]);
            }
            for (var index = 1; index < polygon.Length - 1; index++)
            {
                mesh.AddTriangle(bottom[0], bottom[index + 1], bottom[index]);
                mesh.AddTriangle(top[0], top[index], top[index + 1]);
            }
        }
    }

    /// <summary>Accumulates flat-shaded triangles (no shared vertices, so every face keeps a sharp edge).</summary>
    private sealed class MeshBuilder
    {
        private readonly Point3DCollection positions = [];
        private readonly Vector3DCollection normals = [];
        private readonly Int32Collection indices = [];
        public bool HasGeometry => indices.Count > 0;

        public void AddTriangle(Point3D a, Point3D b, Point3D c)
        {
            var normal = Vector3D.CrossProduct(b - a, c - a);
            if (normal.Length < 1e-18) return;
            normal.Normalize();
            foreach (var point in new[] { a, b, c })
            {
                indices.Add(positions.Count);
                positions.Add(point);
                normals.Add(normal);
            }
        }

        public void AddQuad(Point3D a, Point3D b, Point3D c, Point3D d)
        {
            AddTriangle(a, b, c);
            AddTriangle(a, c, d);
        }

        public MeshGeometry3D ToMesh()
        {
            var mesh = new MeshGeometry3D { Positions = positions, Normals = normals, TriangleIndices = indices };
            mesh.Freeze();
            return mesh;
        }
    }
}
