using COT_VCF.Application.Design;
using COT_VCF.Domain;

namespace COT_VCF.Presentation;

/// <summary>A frame member offered in the Design Reference member list.</summary>
public sealed record DesignMemberOption(string Member, string Section, string Material, double LengthM, bool Suggested);

// ---- Design → RC Column / CFT / Steel Encased Composite ----
// The design basis confirmed in the Design Reference dialog is kept per column type, so reopening a design shows the
// last code, method, section, members and factors used. The design runs on the forces of the FEM run of the model.
public sealed partial class MainViewModel
{
    private readonly Dictionary<ColumnDesignType, ColumnDesignBasis> designBases = [];

    internal ColumnDesignBasis DesignBasis(ColumnDesignType type)
    {
        if (designBases.TryGetValue(type, out var basis)) return basis;
        var members = DesignMemberOptions(type);
        var first = members.FirstOrDefault(member => member.Suggested);
        return ColumnDesignCodes.Default(type) with
        {
            Section = first is null ? ColumnDesignCodes.Default(type).Section : DesignSectionDefaults(type, first.Member),
            Members = members.Where(member => member.Suggested).Select(member => member.Member).ToArray(),
        };
    }

    internal void SetDesignBasis(ColumnDesignBasis basis) => designBases[basis.Type] = basis;

    /// <summary>The FEM run whose forces the design uses (null before Run FEM Analysis).</summary>
    internal string? DesignRunId => (SelectedFemRun ?? FemRuns.FirstOrDefault())?.Id;

    /// <summary>Every frame member, ticked when its section suits <paramref name="type"/> (box / pipe or filled tube for CFT,
    /// Section Designer for steel encased, concrete rectangle / circle for RC).</summary>
    internal IReadOnlyList<DesignMemberOption> DesignMemberOptions(ColumnDesignType type)
    {
        EnsureSections();
        EnsureMaterials();
        return FemSectionProperties.Where(property => !property.IsCable)
            .OrderBy(property => property.MemberName, StringComparer.Ordinal)
            .Select(property =>
            {
                var section = FrameSections.FirstOrDefault(item => item.Name == property.SectionName);
                var material = Materials.FirstOrDefault(item => item.Name == property.MaterialName);
                return new DesignMemberOption(property.MemberName, property.SectionName ?? "", property.MaterialName ?? "",
                    MemberLength(property.MemberName), Suits(type, section, material));
            }).ToArray();
    }

    private static bool Suits(ColumnDesignType type, FrameSectionProperty? section, MaterialProperty? material)
    {
        if (section is null) return false;
        var name = section.Name + " " + section.SapType;
        return type switch
        {
            ColumnDesignType.ConcreteFilledTube => section.Shape == FrameShape.FilledTube || name.Contains("filled", StringComparison.OrdinalIgnoreCase) ||
                                                   name.Contains("CFT", StringComparison.OrdinalIgnoreCase) ||
                                                   section.Shape is FrameShape.Box or FrameShape.Pipe && material?.Kind == MaterialKind.Concrete,
            ColumnDesignType.SteelEncased => section.Shape == FrameShape.Encased || name.Contains("SRC", StringComparison.OrdinalIgnoreCase) ||
                                             name.Contains("encase", StringComparison.OrdinalIgnoreCase),
            ColumnDesignType.MegaComposite => section.Shape == FrameShape.MegaComposite || name.Contains("mega", StringComparison.OrdinalIgnoreCase),
            _ => material?.Kind == MaterialKind.Concrete && section.Shape is FrameShape.Rectangle or FrameShape.Circle,
        };
    }

    private double MemberLength(string member)
    {
        var frame = State.Geometry.Members.FirstOrDefault(item => item.Name == member);
        if (frame is null || !State.Geometry.Points.TryGetValue(frame.Start, out var start) || !State.Geometry.Points.TryGetValue(frame.End, out var end)) return 0;
        var (dx, dy, dz) = (end.Position.X - start.Position.X, end.Position.Y - start.Position.Y, end.Position.Z - start.Position.Z);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Section and material values taken from <paramref name="member"/>'s FEM section, in mm and MPa; what the
    /// section does not give (bars, encased steel) keeps the defaults.</summary>
    internal ColumnSectionInput DesignSectionDefaults(ColumnDesignType type, string member)
    {
        var property = FemSectionProperties.FirstOrDefault(item => item.MemberName == member);
        var section = FrameSections.FirstOrDefault(item => item.Name == property?.SectionName);
        var material = Materials.FirstOrDefault(item => item.Name == property?.MaterialName);
        var concrete = material?.Kind == MaterialKind.Concrete ? material : Materials.FirstOrDefault(item => item.Kind == MaterialKind.Concrete);
        var steel = material?.Kind == MaterialKind.Steel ? material : Materials.FirstOrDefault(item => item.Kind == MaterialKind.Steel);
        var rebar = Materials.FirstOrDefault(item => item.Kind == MaterialKind.Rebar);
        var input = ColumnDesignCodes.Default(type).Section;
        if (concrete?.Fc is > 0 and var fc) input = input with { Fck = fc / 1000 };
        if (steel?.Fy is > 0 and var fy) input = input with { Fy = fy / 1000, Ea = steel.ElasticModulus / 1000 };
        if (rebar?.Fy is > 0 and var fyk) input = input with { Fyk = fyk / 1000 };
        if (section is null) return input;
        // a composite section defined in the app carries its design input
        if (section.Composite is { } composite &&
            (type, section.Shape) is (ColumnDesignType.ConcreteFilledTube, FrameShape.FilledTube) or (ColumnDesignType.SteelEncased, FrameShape.Encased)
                or (ColumnDesignType.MegaComposite, FrameShape.MegaComposite))
            return composite;
        if (type == ColumnDesignType.ConcreteFilledTube && TubeFromName(section) is { } tube) return tube(input);
        double mm(double metres) => Math.Round(metres * 1000, 1);
        return (type, section.Shape) switch
        {
            (ColumnDesignType.ReinforcedConcrete, FrameShape.Rectangle) => input with { Width = mm(section.Width), Depth = mm(section.Depth) },
            (ColumnDesignType.ReinforcedConcrete, FrameShape.Circle) => input with { Width = mm(section.Depth), Depth = mm(section.Depth) },
            (ColumnDesignType.ConcreteFilledTube, FrameShape.Box) => input with
            {
                Tube = TubeShape.Rectangular, TubeDepth = mm(section.Depth), TubeWidth = mm(section.Width),
                TubeThickness = mm(Math.Max(section.FlangeThickness, section.WebThickness)),
            },
            (ColumnDesignType.ConcreteFilledTube, FrameShape.Pipe) => input with { Tube = TubeShape.Circular, TubeDepth = mm(section.Depth), TubeThickness = mm(section.WebThickness) },
            (ColumnDesignType.SteelEncased, FrameShape.I) => input with
            {
                SteelDepth = mm(section.Depth), SteelWidth = mm(section.Width), FlangeThickness = mm(section.FlangeThickness), WebThickness = mm(section.WebThickness),
            },
            _ => input,
        };
    }

    /// <summary>
    /// A filled tube read from a Section Designer name such as <c>CFT_SHS[1200x2000x150_S460]</c> (H × B × t, steel and concrete grade) or
    /// <c>CFT_CHS[1000x25]</c> (D × t): SAP2000 gives such a section no dimensions. The larger side goes in-plane when the
    /// section is stiffer about its 3-axis (I33 ≥ I22).
    /// </summary>
    private static Func<ColumnSectionInput, ColumnSectionInput>? TubeFromName(FrameSectionProperty section)
    {
        var grade = System.Text.RegularExpressions.Regex.Match(section.Name, @"S(\d{3})(?!\d)");
        double? fy = grade.Success ? double.Parse(grade.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
        var concreteGrade = System.Text.RegularExpressions.Regex.Match(section.Name, @"C(\d{2,3})/\d{2,3}");
        double? fck = concreteGrade.Success ? double.Parse(concreteGrade.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
        static double N(System.Text.RegularExpressions.Group group) => double.Parse(group.Value, System.Globalization.CultureInfo.InvariantCulture);
        var three = System.Text.RegularExpressions.Regex.Match(section.Name, @"(\d+(?:\.\d+)?)x(\d+(?:\.\d+)?)x(\d+(?:\.\d+)?)");
        if (three.Success && !section.Name.Contains("CHS", StringComparison.OrdinalIgnoreCase))
        {
            double first = N(three.Groups[1]), second = N(three.Groups[2]), t = N(three.Groups[3]);
            var values = section.Values();
            var largerInPlane = values.I33 >= values.I22;
            double depth = largerInPlane ? Math.Max(first, second) : Math.Min(first, second);
            double width = largerInPlane ? Math.Min(first, second) : Math.Max(first, second);
            return input => input with { Tube = TubeShape.Rectangular, TubeDepth = depth, TubeWidth = width, TubeThickness = t, Fy = fy ?? input.Fy, Fck = fck ?? input.Fck };
        }
        var two = System.Text.RegularExpressions.Regex.Match(section.Name, @"(\d+(?:\.\d+)?)x(\d+(?:\.\d+)?)");
        if (two.Success && (section.Name.Contains("CHS", StringComparison.OrdinalIgnoreCase) || section.Name.Contains("pipe", StringComparison.OrdinalIgnoreCase)))
            return input => input with { Tube = TubeShape.Circular, TubeDepth = N(two.Groups[1]), TubeThickness = N(two.Groups[2]), Fy = fy ?? input.Fy, Fck = fck ?? input.Fck };
        return null;
    }

    /// <summary>Load cases / combinations of the FEM run, in Results order.</summary>
    internal IReadOnlyList<string> DesignCaseNames()
    {
        var run = SelectedFemRun ?? FemRuns.FirstOrDefault();
        if (run?.Results is not { Count: > 0 } results) return run is null ? [] : ["Result"];
        var order = FemResultNames;
        return order.Count > 0 ? order : results.Keys.ToArray();
    }

    /// <summary>The cases ticked by default: the ULS ones (by name), else the combinations set to run, else every case.</summary>
    internal IReadOnlyList<string> DesignDefaultCases()
    {
        var names = DesignCaseNames();
        var ultimate = names.Where(name => name.Contains("ULS", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (ultimate.Length > 0) return ultimate;
        var combinations = FemCombinationRows.Where(row => row.Run).Select(row => row.Name).Where(names.Contains).ToArray();
        return combinations.Length > 0 ? combinations : names;
    }

    /// <summary>Designs every member of <paramref name="basis"/> with the forces of the FEM run.</summary>
    internal IReadOnlyList<ColumnMemberDesign> RunColumnDesign(ColumnDesignBasis basis)
    {
        var run = SelectedFemRun ?? FemRuns.FirstOrDefault();
        if (run is null) return [];
        var cases = basis.LoadCases.Count > 0 ? basis.LoadCases : DesignCaseNames();
        var options = DesignMemberOptions(basis.Type).ToDictionary(option => option.Member, StringComparer.Ordinal);
        return basis.Members.Select(member =>
        {
            var forces = cases.Select(name =>
            {
                var frame = run.ResultFor(name == "Result" ? null : name).Frames.FirstOrDefault(item => item.MemberName == member);
                IReadOnlyList<DesignStation> stations = frame is null ? [] : frame.Stations is { Count: > 0 } list
                    ? list.Select(station => new DesignStation(station.AxialKN, station.ShearKN, station.MomentKNm)).ToArray()
                    : [new(frame.AxialStartKN, frame.ShearStartKN, frame.MomentStartKNm), new(frame.AxialEndKN, frame.ShearEndKN, frame.MomentEndKNm)];
                return new DesignCaseForces(name, stations);
            }).ToArray();
            var option = options.GetValueOrDefault(member);
            return ColumnDesignEngine.Design(basis, new DesignMemberInput(member, option?.Section ?? "", option?.LengthM ?? 0, forces));
        }).ToArray();
    }
}
