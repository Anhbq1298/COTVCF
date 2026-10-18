using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Design;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Presentation;

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
            ColumnDesignType.Steel => material?.Kind == MaterialKind.Steel && SteelSectionGives(section),
            _ => material?.Kind == MaterialKind.Concrete && section.Shape is FrameShape.Rectangle or FrameShape.Circle,
        };
    }

    /// <summary>A bare steel section the steel beam / column design can size: a doubly symmetric I, a box (RHS) or a pipe (CHS).</summary>
    private static bool SteelSectionGives(FrameSectionProperty section) => section.Composite is null && section.Shape switch
    {
        FrameShape.I => (section.BottomWidth <= 0 || Math.Abs(section.BottomWidth - section.Width) < 1e-9) &&
                        (section.BottomFlangeThickness <= 0 || Math.Abs(section.BottomFlangeThickness - section.FlangeThickness) < 1e-9),
        FrameShape.Box or FrameShape.Pipe => true,
        _ => false,
    };

    /// <summary>The member check of the optimization cases (Member Utilization tab): the Design engine on the columns, tension over
    /// capacity on the cables. A snapshot of the design bases of every section × material of the libraries, so it runs off the UI thread.</summary>
    internal MemberUtilizationCheck BuildMemberUtilizationCheck()
    {
        EnsureSections();
        EnsureMaterials();
        var types = Enum.GetValues<ColumnDesignType>();
        var bases = types.ToDictionary(type => type, type => DesignBasis(type));
        var byKey = new Dictionary<(string Section, string Material), ColumnDesignBasis?>();
        foreach (var section in FrameSections)
            foreach (var material in Materials.Select(item => item.Name).Append(""))
            {
                var found = material == "" ? null : Materials.First(item => item.Name == material);
                // the type is the first the section suits, in the order of the Design menu's own: composite ones before plain RC
                var type = new[] { ColumnDesignType.MegaComposite, ColumnDesignType.SteelEncased, ColumnDesignType.ConcreteFilledTube, ColumnDesignType.ReinforcedConcrete, ColumnDesignType.Steel }
                    .Where(candidate => Suits(candidate, section, found)).Select(candidate => (ColumnDesignType?)candidate).FirstOrDefault();
                byKey[(section.Name, material)] = type is { } suited ? bases[suited] with { Section = DesignSectionDefaults(suited, section.Name, material == "" ? null : material), Members = [], LoadCases = [] } : null;
            }
        return new(property => byKey.GetValueOrDefault((property.SectionName ?? "", property.MaterialName ?? "")), State.CableStrengthMpa, State.CableStrengthReductionFactor,
            CableDesignBasis with { Members = [], LoadCases = [] });
    }

    // ---- Design → Tension Cable Design ----
    // The basis confirmed in the window is kept for the session; the optimization cases suggest each cable's assembly with it
    // (MemberUtilizationCheck), so a stay system, screening level or rate set in the Design window carries into Optimization VCF / TAS.
    internal TensionCableBasis CableDesignBasis { get; set; } = TensionCableBasis.Default;

    /// <summary>A cable of the model offered in Design → Tension Cable Design.</summary>
    public sealed record CableDesignOption(string Member, string Section, double LengthM, double AreaMm2, double? CapacityKN);

    internal IReadOnlyList<CableDesignOption> CableDesignOptions() => FemSectionProperties.Where(property => property.IsCable)
        .OrderBy(property => property.MemberName, StringComparer.Ordinal)
        .Select(property => new CableDesignOption(property.MemberName, property.SectionName ?? "", CableLength(property.MemberName), property.AreaM2 * 1e6,
            property.AreaM2 > 0 && State.CableStrengthMpa > 0 ? property.AreaM2 * State.CableStrengthMpa * 1000.0 * State.CableStrengthReductionFactor : null)).ToArray();

    private double CableLength(string member) => MemberLength(member) is > 0 and var length ? length : VcfOptimization.CableLength(State.Geometry, member);

    /// <summary>The cases ticked by default for the cables: the service (SLS) ones, else every case, as the cable screen is a service-load proxy.</summary>
    internal IReadOnlyList<string> CableDesignDefaultCases()
    {
        var names = DesignCaseNames();
        var service = names.Where(name => name.Contains("SLS", StringComparison.OrdinalIgnoreCase)).ToArray();
        return service.Length > 0 ? service : names;
    }

    /// <summary>Designs each cable of <paramref name="basis"/> for its largest tension over the load cases of the FEM run.</summary>
    internal IReadOnlyList<TensionCableMemberDesign> RunTensionCableDesign(TensionCableBasis basis)
    {
        var run = SelectedFemRun ?? FemRuns.FirstOrDefault() ?? throw new InvalidOperationException("There are no FEM results to design with. Run the FEM analysis (Run → Run FEM Analysis) first; the design basis is kept.");
        var cases = basis.LoadCases.Count > 0 ? basis.LoadCases : DesignCaseNames();
        var options = CableDesignOptions().ToDictionary(option => option.Member, StringComparer.Ordinal);
        var byCase = cases.Select(name => (Name: name, Result: run.ResultFor(name == "Result" ? null : name))).ToArray();
        return basis.Members.Select(member =>
        {
            var option = options.GetValueOrDefault(member);
            var governing = byCase.Select(entry => (entry.Name, Force: entry.Result.Cables.Where(cable => cable.MemberName == member).Select(cable => cable.AxialForceKN).DefaultIfEmpty(double.NaN).Max()))
                .Where(entry => !double.IsNaN(entry.Force)).DefaultIfEmpty((Name: "", Force: double.NaN)).MaxBy(entry => entry.Force);
            return TensionCableDesigner.Design(basis, member, option?.Section ?? "", option?.LengthM ?? 0, governing.Name, governing.Force, option?.AreaMm2, option?.CapacityKN);
        }).ToArray();
    }

    // ---- Design → Override Design Procedure ----
    // A member is designed with the procedure of the Design menu it is assigned, whatever its section suggests. The assignment is
    // the user's, kept for the session; the member's size comes from its own section, its forces from the FEM run.
    private readonly Dictionary<string, ColumnDesignType> designProcedures = new(StringComparer.Ordinal);

    internal ColumnDesignType? DesignProcedureOf(string member) => designProcedures.TryGetValue(member, out var type) ? type : null;

    /// <summary>The procedure the Design menu would offer a member by its section and material alone (none for a section no design takes).</summary>
    internal ColumnDesignType? SuggestedDesignProcedure(string member)
    {
        var property = FemSectionProperties.FirstOrDefault(item => item.MemberName == member);
        var section = FrameSections.FirstOrDefault(item => item.Name == property?.SectionName);
        var material = Materials.FirstOrDefault(item => item.Name == property?.MaterialName);
        return new[] { ColumnDesignType.MegaComposite, ColumnDesignType.SteelEncased, ColumnDesignType.ConcreteFilledTube, ColumnDesignType.ReinforcedConcrete, ColumnDesignType.Steel }
            .Where(candidate => Suits(candidate, section, material)).Select(candidate => (ColumnDesignType?)candidate).FirstOrDefault();
    }

    /// <summary>Why <paramref name="member"/>'s section cannot give <paramref name="type"/> its size; null when it can. A size is never
    /// filled in from a default: an RC column takes its outline from a concrete rectangle or circle, a CFT its tube from a box, a pipe or
    /// a filled-tube section, and a steel encased or mega composite column everything from its Section Designer section.</summary>
    internal string? DesignProcedureProblem(ColumnDesignType type, string member)
    {
        var property = FemSectionProperties.FirstOrDefault(item => item.MemberName == member);
        var section = FrameSections.FirstOrDefault(item => item.Name == property?.SectionName);
        if (section is null) return $"{member} has no section assigned.";
        var composite = section.Composite is not null;
        var gives = type switch
        {
            ColumnDesignType.ReinforcedConcrete => section.Shape is FrameShape.Rectangle or FrameShape.Circle,
            ColumnDesignType.ConcreteFilledTube => composite && section.Shape == FrameShape.FilledTube || section.Shape is FrameShape.Box or FrameShape.Pipe || TubeFromName(section) is not null,
            ColumnDesignType.SteelEncased => composite && section.Shape == FrameShape.Encased,
            ColumnDesignType.Steel => SteelSectionGives(section),
            _ => composite && section.Shape == FrameShape.MegaComposite,
        };
        return gives ? null : $"{member}: section {section.Name} ({FrameSectionProperty.Title(section.Shape)}) gives no size for the {ColumnDesignCodes.Title(type)}.";
    }

    /// <summary>Assigns <paramref name="type"/> (null clears the assignment) to the members; returns the reason for each member refused.</summary>
    internal IReadOnlyList<string> AssignDesignProcedure(IEnumerable<string> members, ColumnDesignType? type)
    {
        var refused = new List<string>();
        foreach (var member in members)
        {
            if (type is not { } procedure) { designProcedures.Remove(member); continue; }
            if (DesignProcedureProblem(procedure, member) is { } problem) refused.Add(problem);
            else designProcedures[member] = procedure;
        }
        return refused;
    }

    /// <summary>Designs each member with the procedure assigned to it: the design basis (code, method, factors, materials) of that
    /// procedure's Design menu, the member's own section, and the FEM forces of the member. The effective length is
    /// <paramref name="effectiveLengthM"/> [m] about both axes (10 m by default in the window).</summary>
    internal IReadOnlyList<ColumnMemberDesign> RunOverriddenDesign(IReadOnlyList<string> members, IReadOnlyList<string> loadCases, double effectiveLengthM)
    {
        IReadOnlyDictionary<string, VcfMemberRole> roles;
        try { roles = VcfGeometry.Analyze(State.Geometry, State.Truss).Roles; }
        catch (InvalidOperationException) { roles = new Dictionary<string, VcfMemberRole>(); }
        var results = new List<ColumnMemberDesign>();
        foreach (var member in members)
        {
            if (DesignProcedureOf(member) is not { } type) continue;
            var input = DesignMemberInputs(type, [member], loadCases).FirstOrDefault();
            var title = ColumnDesignCodes.Title(type);
            ColumnMemberDesign result;
            if (input is null) result = new(member, "", 0, [], null, [], "", "There are no FEM results to design with.");
            else if (input.LengthM <= 0) result = new(member, input.Section, 0, [], null, [], "", "The member has no length in the model.");
            else
            {
                var basis = DesignBasis(type) with
                {
                    Section = DesignSectionDefaults(type, member), Members = [member], LoadCases = loadCases,
                    EffectiveLengthY = effectiveLengthM, EffectiveLengthZ = effectiveLengthM,
                };
                result = ColumnDesignEngine.Design(basis, input);
            }
            results.Add(result with { Procedure = title, Role = roles.TryGetValue(member, out var role) ? VcfGeometry.Title(role) : "" });
        }
        return results;
    }

    /// <summary>The length of a frame of the model's document, by its identity (M7); 0 for a member the document lacks.</summary>
    private double MemberLength(string member)
    {
        var document = NativeDocumentSession.Document;
        if (Mcotis.Desktop.Application.LegacyNames.Of(document).Frame(member) is not { } id || !document.Frames.TryGetValue(id, out var frame) ||
            !document.Points.TryGetValue(frame.StartPointId, out var start) || !document.Points.TryGetValue(frame.EndPointId, out var end)) return 0;
        var (dx, dy, dz) = (end.Position.X - start.Position.X, end.Position.Y - start.Position.Y, end.Position.Z - start.Position.Z);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Section and material values taken from <paramref name="member"/>'s FEM section, in mm and MPa; what the
    /// section does not give (bars, encased steel) keeps the defaults.</summary>
    internal ColumnSectionInput DesignSectionDefaults(ColumnDesignType type, string member)
    {
        var property = FemSectionProperties.FirstOrDefault(item => item.MemberName == member);
        return DesignSectionDefaults(type, property?.SectionName, property?.MaterialName);
    }

    /// <summary>As above for a section and material by name (a case of an optimization has its own sections).</summary>
    private ColumnSectionInput DesignSectionDefaults(ColumnDesignType type, string? sectionName, string? materialName)
    {
        var section = FrameSections.FirstOrDefault(item => item.Name == sectionName);
        var material = Materials.FirstOrDefault(item => item.Name == materialName);
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
            // a bare steel section; the FEM section gives no root radius, so a rolled I is designed without its fillets (on the safe side), and a
            // hollow section is taken as cold-formed (the lower buckling curve) until the user says otherwise; a box has one wall thickness, the thinner
            (ColumnDesignType.Steel, FrameShape.I) => input with
            {
                SteelKind = SteelShape.I, Fabrication = SteelFabrication.Rolled, RootRadius = 0,
                SteelDepth = mm(section.Depth), SteelWidth = mm(section.Width), FlangeThickness = mm(section.FlangeThickness), WebThickness = mm(section.WebThickness),
            },
            (ColumnDesignType.Steel, FrameShape.Box) => input with
            {
                SteelKind = SteelShape.Rhs, Fabrication = SteelFabrication.ColdFormed,
                TubeDepth = mm(section.Depth), TubeWidth = mm(section.Width), TubeThickness = mm(Math.Min(section.FlangeThickness, section.WebThickness)),
            },
            (ColumnDesignType.Steel, FrameShape.Pipe) => input with
            {
                SteelKind = SteelShape.Chs, Fabrication = SteelFabrication.ColdFormed, TubeDepth = mm(section.Depth), TubeThickness = mm(section.WebThickness),
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
        IReadOnlyDictionary<string, VcfMemberRole> roles;
        try { roles = VcfGeometry.Analyze(State.Geometry, State.Truss).Roles; }
        catch (InvalidOperationException) { roles = new Dictionary<string, VcfMemberRole>(); }   // not a VCF system: no roles
        return DesignMemberInputs(basis.Type, basis.Members, basis.LoadCases)
            .Select(input => ColumnDesignEngine.Design(basis, input) with { Role = roles.TryGetValue(input.Member, out var role) ? VcfGeometry.Title(role) : "" }).ToArray();
    }

    /// <summary>The FEM forces of <paramref name="members"/> under <paramref name="loadCases"/> (empty: all) in the design
    /// candidate, as the design engine and the optimisation take them: the six local components of the run's results by
    /// frame identity (<see cref="NativeDesignForces"/>, M7); the planar stations only for a run that has no such result.</summary>
    internal IReadOnlyList<DesignMemberInput> DesignMemberInputs(ColumnDesignType type, IReadOnlyList<string> members, IReadOnlyList<string> loadCases)
    {
        var run = SelectedFemRun ?? FemRuns.FirstOrDefault();
        if (run is null) return [];
        var cases = loadCases.Count > 0 ? loadCases : DesignCaseNames();
        var options = DesignMemberOptions(type).ToDictionary(option => option.Member, StringComparer.Ordinal);
        var combinations = FemCombinationRows.Select(row => (row.Name, row.Definition)).ToArray();
        var native = cases.ToDictionary(name => name, name => run.Native is { } results && name != "Result"
            ? NativeDesignForces.Of(results, name, combinations) : null, StringComparer.Ordinal);
        return members.Select(member =>
        {
            var forces = cases.Select(name =>
            {
                if (native[name] is { } byFrame) return new DesignCaseForces(name, byFrame.GetValueOrDefault(member) ?? []);
                var frame = run.ResultFor(name == "Result" ? null : name).Frames.FirstOrDefault(item => item.MemberName == member);
                IReadOnlyList<DesignStation> stations = frame is null ? [] : frame.Stations is { Count: > 0 } list
                    ? list.Select(station => new DesignStation(station.AxialKN, station.ShearKN, station.MomentKNm)).ToArray()
                    : [new(frame.AxialStartKN, frame.ShearStartKN, frame.MomentStartKNm), new(frame.AxialEndKN, frame.ShearEndKN, frame.MomentEndKNm)];
                return new DesignCaseForces(name, stations);
            }).ToArray();
            var option = options.GetValueOrDefault(member);
            return new DesignMemberInput(member, option?.Section ?? "", option?.LengthM ?? 0, forces);
        }).ToArray();
    }
}
