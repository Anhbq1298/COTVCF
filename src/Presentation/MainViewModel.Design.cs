using COT_VCF.Application.Design;
using COT_VCF.Domain;

namespace COT_VCF.Presentation;

/// <summary>A frame member offered in the Design Reference member list.</summary>
public sealed record DesignMemberOption(string Member, string Section, string Material, double LengthM, bool Suggested);

// ---- Design → RC Column / CFT / Steel Encased Composite ----
// The design basis confirmed in the Design Reference dialog is kept per column type, so reopening a design shows the
// last code, method, section, members and factors used. The design runs on the forces of the selected FEM candidate.
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
            Section = first is null ? new ColumnSectionInput() : DesignSectionDefaults(type, first.Member),
            Members = members.Where(member => member.Suggested).Select(member => member.Member).ToArray(),
        };
    }

    internal void SetDesignBasis(ColumnDesignBasis basis) => designBases[basis.Type] = basis;

    /// <summary>The FEM candidate whose forces the design uses.</summary>
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
            ColumnDesignType.ConcreteFilledTube => name.Contains("filled", StringComparison.OrdinalIgnoreCase) || name.Contains("CFT", StringComparison.OrdinalIgnoreCase) ||
                                                   section.Shape is FrameShape.Box or FrameShape.Pipe && material?.Kind == MaterialKind.Concrete,
            ColumnDesignType.SteelEncased => name.Contains("SRC", StringComparison.OrdinalIgnoreCase) || name.Contains("encase", StringComparison.OrdinalIgnoreCase),
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
        var input = new ColumnSectionInput();
        if (concrete?.Fc is > 0 and var fc) input = input with { Fck = fc / 1000 };
        if (steel?.Fy is > 0 and var fy) input = input with { Fy = fy / 1000, Ea = steel.ElasticModulus / 1000 };
        if (rebar?.Fy is > 0 and var fyk) input = input with { Fyk = fyk / 1000 };
        if (section is null) return input;
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

    /// <summary>Load cases / combinations of the design candidate, in Results order.</summary>
    internal IReadOnlyList<string> DesignCaseNames()
    {
        var run = SelectedFemRun ?? FemRuns.FirstOrDefault();
        if (run?.Results is not { Count: > 0 } results) return run is null ? [] : ["Result"];
        var order = FemResultNames;
        return order.Count > 0 ? order : results.Keys.ToArray();
    }

    /// <summary>The cases ticked by default: the combinations set to run, else every case.</summary>
    internal IReadOnlyList<string> DesignDefaultCases()
    {
        var names = DesignCaseNames();
        var combinations = FemCombinationRows.Where(row => row.Run).Select(row => row.Name).Where(names.Contains).ToArray();
        return combinations.Length > 0 ? combinations : names;
    }

    /// <summary>Designs every member of <paramref name="basis"/> with the FEM forces of the design candidate.</summary>
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
