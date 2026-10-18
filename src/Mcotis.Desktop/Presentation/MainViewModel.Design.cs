using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Design;
using Mcotis.Desktop.Application.Quantities;
using Mcotis.Desktop.Domain;
using System.Text.Json;

namespace Mcotis.Desktop.Presentation;

/// <summary>A frame member offered in the Design Reference member list.</summary>
public sealed record DesignMemberOption(string Member, string Section, string Material, double LengthM, bool Suggested);

// ---- Design → RC Column / CFT / Steel Encased Composite ----
// The design basis confirmed in the Design Reference dialog is kept per column type, so reopening a design shows the
// last code, method, section, members and factors used. The design runs on the forces of the FEM run of the model.
public sealed partial class MainViewModel
{
    private const string DesignParametersKey = "mcotis.design.parameters.v1";
    private sealed class DesignParameterStore
    {
        public Dictionary<ColumnDesignType, ColumnDesignBasis> Bases { get; set; } = [];
        public Dictionary<string, StoredMemberDesignParameters> Members { get; set; } = new(StringComparer.Ordinal);
        public TensionCableBasis? CableBasis { get; set; }
    }
    private sealed record StoredMemberDesignParameters(ColumnDesignType? Procedure, ColumnDesignType? OverrideProcedure,
        ColumnDesignBasis? ColumnBasis, TensionCableBasis? CableBasis, string Section, string? FemRunId,
        string LastStatus = "Not run");

    private readonly Dictionary<ColumnDesignType, ColumnDesignBasis> designBases = [];

    internal ColumnDesignBasis DesignBasis(ColumnDesignType type)
    {
        var stored = ReadDesignParameterStore();
        if (stored.Bases.TryGetValue(type, out var basis))
        {
            designBases[type] = basis;
            return basis;
        }
        designBases.Remove(type);
        var members = DesignMemberOptions(type);
        var first = members.FirstOrDefault(member => member.Suggested);
        return ColumnDesignCodes.Default(type) with
        {
            Section = first is null ? ColumnDesignCodes.Default(type).Section : DesignSectionDefaults(type, first.Member),
            Members = members.Where(member => member.Suggested).Select(member => member.Member).ToArray(),
        };
    }

    internal void SetDesignBasis(ColumnDesignBasis basis)
    {
        designBases[basis.Type] = basis;
        var store = ReadDesignParameterStore();
        store.Bases[basis.Type] = basis;
        foreach (var member in basis.Members)
        {
            var previous = store.Members.GetValueOrDefault(member);
            store.Members[member] = new(basis.Type, previous?.OverrideProcedure, basis with { Members = [member] }, null,
                FemSectionProperties.FirstOrDefault(item => item.MemberName == member)?.SectionName ?? "", DesignRunId);
        }
        SaveDesignParameterStore(store);
    }

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
    internal TensionCableBasis CableDesignBasis
    {
        get => ReadDesignParameterStore().CableBasis ?? TensionCableBasis.Default;
        set
        {
            var store = ReadDesignParameterStore();
            store.CableBasis = value;
            foreach (var member in value.Members)
            {
                var previous = store.Members.GetValueOrDefault(member);
                store.Members[member] = new(null, previous?.OverrideProcedure, null,
                    value with { Members = [member] }, FemSectionProperties.FirstOrDefault(item => item.MemberName == member)?.SectionName ?? "", DesignRunId);
            }
            SaveDesignParameterStore(store);
        }
    }

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

    internal ColumnDesignType? DesignProcedureOf(string member)
    {
        if (designProcedures.TryGetValue(member, out var type)) return type;
        if (ReadDesignParameterStore().Members.GetValueOrDefault(member)?.OverrideProcedure is not { } found) return null;
        designProcedures[member] = found;
        return found;
    }

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
        var store = ReadDesignParameterStore();
        foreach (var member in members)
        {
            if (type is not { } procedure)
            {
                designProcedures.Remove(member);
                if (store.Members.TryGetValue(member, out var prior)) store.Members[member] = prior with { OverrideProcedure = null };
                continue;
            }
            if (DesignProcedureProblem(procedure, member) is { } problem) refused.Add(problem);
            else
            {
                designProcedures[member] = procedure;
                var prior = store.Members.GetValueOrDefault(member);
                store.Members[member] = new(prior?.Procedure, procedure, prior?.ColumnBasis, prior?.CableBasis,
                    FemSectionProperties.FirstOrDefault(item => item.MemberName == member)?.SectionName ?? "", prior?.FemRunId, prior?.LastStatus ?? "Not run");
            }
        }
        SaveDesignParameterStore(store);
        return refused;
    }

    /// <summary>Designs each member with the procedure assigned to it: the design basis (code, method, factors, materials) of that
    /// procedure's Design menu, the member's own section, and the FEM forces of the member. The effective length is
    /// <paramref name="effectiveLengthM"/> [m] about both axes (10 m by default in the window).</summary>
    internal IReadOnlyList<ColumnMemberDesign> RunOverriddenDesign(IReadOnlyList<string> members, IReadOnlyList<string> loadCases, double effectiveLengthM)
    {
        var storedParameters = ReadDesignParameterStore();
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
                var previous = storedParameters.Members.GetValueOrDefault(member);
                storedParameters.Members[member] = new(type, type, basis, null, input.Section, DesignRunId, result.Status);
            }
            results.Add(result with { Procedure = title, Role = roles.TryGetValue(member, out var role) ? VcfGeometry.Title(role) : "" });
        }
        SaveDesignParameterStore(storedParameters);
        return results;
    }

    private DesignParameterStore ReadDesignParameterStore()
    {
        var document = NativeDocumentSession.Document;
        if (!document.ApplicationData.TryGetValue(DesignParametersKey, out var json)) return new();
        try { return JsonSerializer.Deserialize<DesignParameterStore>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private void SaveDesignParameterStore(DesignParameterStore store)
    {
        var document = NativeDocumentSession.Document;
        var json = JsonSerializer.Serialize(store);
        if (document.ApplicationData.TryGetValue(DesignParametersKey, out var held) && held == json) return;
        NativeDocumentSession.Execute(Mcotis.Application.DocumentCommands.Edit("Store member design parameters", editor =>
            editor.SetApplicationData(DesignParametersKey, json)));
        ScheduleFemWorkingSave();
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

    /// <summary>Section-level utilisation summaries from the selected FEM run and the same design engines as Design.</summary>
    internal IReadOnlyList<NativeQuantityUtilizationRow> QuantityUtilizationSummary() => DesignUtilization().Sections;

    /// <summary>Runs the design of every member on the current FEM run: the section summaries of Show Quantities and each designed
    /// member's UR per load case (empty when the results are not those of the current model).</summary>
    internal (IReadOnlyList<NativeQuantityUtilizationRow> Sections, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> MemberCases) DesignUtilization()
    {
        var run = SelectedFemRun ?? FemRuns.FirstOrDefault();
        if (run is null || FemResultState != Mcotis.Application.ResultState.Current)
            return ([new("—", "—", 0, null, "—", "", "RUN FEM", "Run FEM Analysis for the current model before calculating utilization.")],
                new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal));
        var allCases = DesignCaseNames();
        var caseOrder = allCases.Select((name, index) => (name, index)).ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);
        var store = ReadDesignParameterStore();
        var parameters = new Dictionary<string, StoredMemberDesignParameters>(StringComparer.Ordinal);
        foreach (var property in FemSectionProperties)
        {
            var section = property.SectionName ?? "(no section assigned)";
            var prior = store.Members.GetValueOrDefault(property.MemberName);
            if (property.IsCable)
            {
                var basis = prior?.Section == section && prior.CableBasis is not null
                    ? prior.CableBasis : store.CableBasis ?? TensionCableBasis.Default;
                var cases = (basis.LoadCases.Count > 0 ? basis.LoadCases : allCases).Where(allCases.Contains).Distinct(StringComparer.Ordinal).ToArray();
                parameters[property.MemberName] = new(null, prior?.OverrideProcedure, null,
                    basis with { Members = [property.MemberName], LoadCases = cases }, section, run.Id, "Not run");
                continue;
            }

            var type = prior?.OverrideProcedure ?? DesignProcedureOf(property.MemberName) ?? SuggestedDesignProcedure(property.MemberName);
            if (type is null) continue;
            var hasStoredInput = prior?.Procedure == type && prior.Section == section && prior.ColumnBasis is not null;
            var basisForType = hasStoredInput ? prior!.ColumnBasis! : DesignBasis(type.Value);
            var sectionInput = hasStoredInput
                ? basisForType.Section : DesignSectionDefaults(type.Value, property.MemberName);
            var chosenCases = (basisForType.LoadCases.Count > 0 ? basisForType.LoadCases : allCases)
                .Where(allCases.Contains).Distinct(StringComparer.Ordinal).ToArray();
            var l0 = prior?.OverrideProcedure == type
                ? basisForType.EffectiveLengthY ?? 10
                : (double?)null;
            var memberBasis = basisForType with
            {
                Section = sectionInput,
                Members = [property.MemberName],
                LoadCases = chosenCases,
                EffectiveLengthY = l0 ?? basisForType.EffectiveLengthY,
                EffectiveLengthZ = l0 ?? basisForType.EffectiveLengthZ,
            };
            parameters[property.MemberName] = new(type, prior?.OverrideProcedure, memberBasis, null, section, run.Id, "Not run");
        }
        // Store a complete per-member input snapshot before running any checks. The summary below is emitted only after
        // every applicable member has been evaluated against this FEM run.
        foreach (var (member, input) in parameters) store.Members[member] = input;
        SaveDesignParameterStore(store);

        var samples = new List<(string Section, string Procedure, string Member, IReadOnlyList<(string Case, double Util)> Cases, string Status, string Details)>();

        foreach (var property in FemSectionProperties.OrderBy(property => property.MemberName, StringComparer.Ordinal))
        {
            var section = property.SectionName ?? "(no section assigned)";
            parameters.TryGetValue(property.MemberName, out var memberParameters);
            if (property.IsCable)
            {
                var cableBasis = memberParameters?.CableBasis ?? store.CableBasis ?? TensionCableBasis.Default;
                var caseNames = cableBasis.LoadCases;
                var cableCases = caseNames.Where(name => ResultReportTables.IsSlsCase(name, allCases)).Select(name => (Case: name, Force: run.ResultFor(name).Cables
                    .Where(cable => cable.MemberName == property.MemberName).Select(cable => cable.AxialForceKN)
                    .DefaultIfEmpty(double.NaN).Max())).Where(item => double.IsFinite(item.Force)).ToArray();
                if (cableCases.Length == 0)
                {
                    samples.Add((section, "Tension Cable Design", property.MemberName, [], "NO RESULT", "No cable forces are available in the selected SLS cases."));
                    store.Members[property.MemberName] = memberParameters! with { FemRunId = run.Id, LastStatus = "NO RESULT" };
                    continue;
                }
                // The UR of the section the cable has now — a suggested stay counts only once it is applied:
                // capacity = area × 1,860 MPa × 50 %: the VSL Ø15.7 strand (279 kN) screened at 50 %, as Section Check and Iterate.
                var governingForce = cableCases.MaxBy(item => item.Force);
                double? capacity = property.AreaM2 > 0
                    ? property.AreaM2 * 1e6 * ResultReportTables.CableBreakingStressMpa * ResultReportTables.CableReductionFactor / 1000 : null;
                var utilizationCases = capacity is > 0
                    ? cableCases.Select(item => (item.Case, Util: Math.Max(0, item.Force) / capacity.Value)).ToArray()
                    : [];
                var slack = governingForce.Force <= 1e-6;
                if (slack) utilizationCases = cableCases.Select(item => (item.Case, Util: 0.0)).ToArray();
                var cableStatus = slack ? "OK" : capacity is null ? "NO RESULT" : "OK";
                samples.Add((section, "Tension Cable Design", property.MemberName, utilizationCases, cableStatus,
                    slack ? "Cable is slack in the selected cases; UR = 0.00."
                    : capacity is > 0 ? $"UR = tension ÷ (section area × {ResultReportTables.CableBreakingStressMpa:0} MPa × {ResultReportTables.CableReductionFactor:0%}) of the assigned section."
                    : "The cable has no section area."));
                store.Members[property.MemberName] = memberParameters! with { FemRunId = run.Id, LastStatus = cableStatus };
                continue;
            }

            var type = memberParameters?.Procedure;
            if (type is null)
            {
                samples.Add((section, "Not designed", property.MemberName, [], "NOT DESIGNED",
                    "No current design procedure is compatible with this section."));
                continue;
            }

            var basis = memberParameters!.ColumnBasis!;
            var caseNamesForMember = basis.LoadCases;
            try
            {
                var input = DesignMemberInputs(type.Value, [property.MemberName], caseNamesForMember).FirstOrDefault();
                var design = input is null
                    ? new ColumnMemberDesign(property.MemberName, section, MemberLength(property.MemberName), [], null, [], "", "No FEM forces are available for this member.")
                    : ColumnDesignEngine.Design(basis, input);
                samples.Add((section, ColumnDesignCodes.Title(type.Value), property.MemberName,
                    design.Cases.Where(result => ResultReportTables.IsUlsCase(result.Case, allCases)).Select(result => (result.Case, result.MaxUtilization)).ToArray(), design.Status,
                    design.Error ?? design.Detailing.FirstOrDefault(check => !check.Pass)?.Text ?? ""));
                store.Members[property.MemberName] = memberParameters with { FemRunId = run.Id, LastStatus = design.Status };
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or ArithmeticException)
            {
                samples.Add((section, ColumnDesignCodes.Title(type.Value), property.MemberName, [], "ERROR", ex.Message));
                store.Members[property.MemberName] = memberParameters with { FemRunId = run.Id, LastStatus = "ERROR" };
            }
        }
        SaveDesignParameterStore(store);

        var memberCases = samples.Where(sample => sample.Cases.Count > 0 && sample.Status is not ("ERROR" or "NO RESULT"))
            .GroupBy(sample => sample.Member, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyDictionary<string, double>)group.SelectMany(sample => sample.Cases)
                .GroupBy(item => item.Case, StringComparer.Ordinal).ToDictionary(item => item.Key, item => item.Max(value => value.Util), StringComparer.Ordinal),
                StringComparer.Ordinal);
        return (samples.GroupBy(sample => (sample.Section, sample.Procedure))
            .Select(group =>
            {
                var perCase = group.SelectMany(sample => sample.Cases).GroupBy(item => item.Case, StringComparer.Ordinal)
                    .Select(cases => (Case: cases.Key, Util: cases.Max(item => item.Util)))
                    .OrderBy(item => caseOrder.GetValueOrDefault(item.Case, int.MaxValue)).ThenBy(item => item.Case, StringComparer.Ordinal).ToArray();
                var incomplete = group.Any(sample => sample.Status is "ERROR" or "NO RESULT");
                if (incomplete) perCase = [];
                var envelope = perCase.Length == 0 ? (double?)null : perCase.Max(item => item.Util);
                var governing = envelope is { } max
                    ? string.Join(", ", perCase.Where(item => Math.Abs(item.Util - max) <= 1e-9 * Math.Max(1, Math.Abs(max))).Select(item => item.Case))
                    : "—";
                var status = group.Any(sample => sample.Status == "ERROR") ? "ERROR" : group.Any(sample => sample.Status == "NO RESULT") ? "NO RESULT" : envelope is null ? group.First().Status :
                    envelope > 1 ? "FAIL" : group.Any(sample => sample.Status == "CHECK") ? "CHECK" : "OK";
                var details = string.Join(" ", group.Select(sample => sample.Details).Where(text => text.Length > 0)
                    .Distinct(StringComparer.Ordinal).Take(4));
                var casesText = string.Join(" · ", perCase.Select(item => $"{item.Case}: {item.Util:0.00}"));
                return new NativeQuantityUtilizationRow(group.Key.Section, group.Key.Procedure, group.Count(), envelope, governing, casesText, status, details);
            })
            .OrderBy(row => row.Section, StringComparer.Ordinal).ThenBy(row => row.Procedure, StringComparer.Ordinal).ToArray(), memberCases);
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
