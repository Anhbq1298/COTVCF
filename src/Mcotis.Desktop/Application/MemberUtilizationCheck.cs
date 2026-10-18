using Mcotis.Desktop.Application.Design;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using System.Text.Json.Serialization;

namespace Mcotis.Desktop.Application;

/// <summary>One member of an optimization case with its utilization: a cable's tension over its design capacity, a column's
/// governing utilization from the Design engine (<see cref="ColumnDesignEngine"/>). A member no engine checks has no
/// utilization and says why.</summary>
/// <param name="Type">Cable, Column (a section the Design engine takes) or Frame (one it does not).</param>
/// <param name="Check">OK, FAIL (utilization above 1), CHECK (a detailing rule fails), ERROR, or — when nothing was checked.</param>
/// <param name="Case">The load case / combination that governs.</param>
/// <param name="Role">The member's role in the system (mega column, bottom chord …); empty when the system has none or a study saved before roles were kept.</param>
/// <param name="CableDesign">For a cable: the stay assembly Design → Tension Cable Design suggests for its governing tension (number and type of units); null for any other member.</param>
public sealed record MemberUtilization(string Member, string Type, string Section,
    [property: JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)] double? Utilization,
    string Check, string Case, string Note, string Role = "",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TensionCableSelection? CableDesign = null)
{
    /// <summary>The suggested stay assembly of a cable in one line; empty for a member with none.</summary>
    [JsonIgnore]
    public string SuggestedCable => CableDesign?.Summary ?? "";

    public string UtilizationText => Utilization is { } value ? (double.IsFinite(value) ? value.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture) : ">9.99") : "—";
}

/// <summary>
/// The member check of an optimization case (Optimization → results → Member Utilization): every member of the case's model.
/// Cables: the largest tension of the solved results over the design capacity (area × strength × φ, as the cable force
/// report). Frames whose section a design type takes (<see cref="Basis"/>): the Design engine, with the Design window's code,
/// method and factors, on the forces of the ULS results (all solved results when none is named ULS), with the study's effective length l0 (10 m unless changed on the Sections tab). Every other frame is listed
/// as not designed. Works on a snapshot — it may run off the UI thread.
/// </summary>
public sealed class MemberUtilizationCheck(Func<FemSectionProperties, ColumnDesignBasis?> basis, double cableStrengthMpa, double cablePhi, TensionCableBasis? cableDesign = null)
{
    /// <summary>The Design → Tension Cable Design basis the cables' suggested assemblies are made with (null: none suggested).</summary>
    public TensionCableBasis? CableDesign { get; } = cableDesign;

    /// <summary>The Design basis of a member's section (type, code, method, factors and the section's input), or null when no design type takes it.</summary>
    public Func<FemSectionProperties, ColumnDesignBasis?> Basis { get; } = basis;

    public IReadOnlyList<MemberUtilization> Evaluate(ParametricCandidate candidate, IReadOnlyDictionary<string, FemAnalysisResult> results)
    {
        var rows = new List<MemberUtilization>();
        var designCases = results.Keys.Where(name => name.Contains("ULS", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (designCases.Length == 0) designCases = results.Keys.ToArray();
        foreach (var member in candidate.Geometry.Members)
        {
            if (!candidate.Properties.TryGetValue(member.Name, out var property)) continue;
            var section = property.SectionName ?? "";
            var role = candidate.MemberRoles is { } roles && roles.TryGetValue(member.Name, out var found) ? found : "";
            try
            {
                rows.Add((property.IsCable ? Cable(candidate, member.Name, section, property, results) : Frame(candidate, member, section, property, results, designCases)) with { Role = role });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or ArithmeticException)
            {
                rows.Add(new(member.Name, property.IsCable ? "Cable" : "Frame", section, null, "ERROR", "", ex.Message, role));
            }
        }
        return rows;
    }

    private MemberUtilization Cable(ParametricCandidate candidate, string member, string section, FemSectionProperties property, IReadOnlyDictionary<string, FemAnalysisResult> results)
    {
        var capacity = property.AreaM2 > 0 && cableStrengthMpa > 0 && cablePhi is > 0 and <= 1 ? property.AreaM2 * cableStrengthMpa * 1000.0 * cablePhi : (double?)null;
        var governing = results.Select(pair => (Case: pair.Key, Force: pair.Value.Cables.Where(cable => cable.MemberName == member).Select(cable => cable.AxialForceKN).DefaultIfEmpty(double.NaN).Max()))
            .Where(entry => !double.IsNaN(entry.Force)).DefaultIfEmpty((Case: "", Force: double.NaN)).MaxBy(entry => entry.Force);
        if (double.IsNaN(governing.Force)) return new(member, "Cable", section, null, "—", "", "No cable force in the solved results.");
        var suggestion = Suggest(candidate, member, section, property, governing.Case, governing.Force, capacity);
        if (capacity is not { } cap) return new(member, "Cable", section, null, "—", governing.Case, $"No design capacity (section area, cable strength or φ missing); tension {governing.Force:0.#} kN." + SuggestionNote(suggestion),
            CableDesign: suggestion?.Selected);
        var utilization = Math.Max(0, governing.Force) / cap;
        return new(member, "Cable", section, utilization, utilization <= 1 ? "OK" : "FAIL", governing.Case, $"T = {governing.Force:0.#} kN of {cap:0.#} kN." + SuggestionNote(suggestion),
            CableDesign: suggestion?.Selected);
    }

    /// <summary>The stay suggested for a cable; null when the check has no tension cable basis.</summary>
    private TensionCableMemberDesign? Suggest(ParametricCandidate candidate, string member, string section, FemSectionProperties property, string governingCase, double tensionKN, double? capacityKN) =>
        CableDesign is null ? null : TensionCableDesigner.Design(CableDesign, member, section, VcfOptimization.CableLength(candidate.Geometry, member), governingCase, tensionKN,
            property.AreaM2 > 0 ? property.AreaM2 * 1e6 : null, capacityKN);

    /// <summary>The reason no stay was suggested, when it is not the plain fact of a cable without tension.</summary>
    private static string SuggestionNote(TensionCableMemberDesign? suggestion) =>
        suggestion is { Error: { } error } ? $" No stay suggested: {error}" : "";

    private MemberUtilization Frame(ParametricCandidate candidate, StructuralMember member, string section, FemSectionProperties property,
        IReadOnlyDictionary<string, FemAnalysisResult> results, IReadOnlyList<string> designCases)
    {
        if (Basis(property) is not { } designBasis)
            return new(member.Name, "Frame", section, null, "—", "", "Not designed: no design engine takes this section (RC, CFT, steel encased and mega composite columns and bare steel I, box and pipe sections only).");
        var (start, end) = (candidate.Geometry.Points[member.Start].Position, candidate.Geometry.Points[member.End].Position);
        var length = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2) + Math.Pow(end.Z - start.Z, 2));
        var forces = designCases.Select(name =>
        {
            var frame = results[name].Frames.FirstOrDefault(item => item.MemberName == member.Name);
            IReadOnlyList<DesignStation> stations = frame is null ? [] : frame.Stations is { Count: > 0 } list
                ? list.Select(station => new DesignStation(station.AxialKN, station.ShearKN, station.MomentKNm)).ToArray()
                : [new(frame.AxialStartKN, frame.ShearStartKN, frame.MomentStartKNm), new(frame.AxialEndKN, frame.ShearEndKN, frame.MomentEndKNm)];
            return new DesignCaseForces(name, stations);
        }).ToArray();
        // l0 is the study's own (Optimization → Sections), 10 m until the user changes it; it replaces the Design window's
        var l0 = candidate.EffectiveLengthsM is { } lengths && lengths.TryGetValue(member.Name, out var set) ? set : VcfOptimization.DefaultEffectiveLengthM;
        var basisOfMember = designBasis with { EffectiveLengthY = l0, EffectiveLengthZ = l0 };
        var design = ColumnDesignEngine.Design(basisOfMember, new DesignMemberInput(member.Name, section, length, forces));
        if (design.Error is { } error) return new(member.Name, "Column", section, null, "ERROR", "", error);
        var note = design.Detailing.FirstOrDefault(check => !check.Pass) is { } failed ? $"Detailing: {failed.Name} ({failed.Clause}) — {failed.Text}" : "";
        return new(member.Name, "Column", section, design.MaxUtilization, design.Status, design.Governing?.Case ?? "", note);
    }
}
