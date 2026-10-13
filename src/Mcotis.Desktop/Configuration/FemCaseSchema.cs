using System.IO;
using System.Security.Cryptography;
using System.Text;
using Mcotis.Desktop.Application;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Configuration;

/// <summary>Version 2 of what a desktop configuration holds of its load cases and combinations
/// (docs/analysis/LOAD_CASE_STATE_REFACTOR_PLAN.md §5.2, §5.3): every row has an identity a rename does not change,
/// a case refers to another case by that identity, and a case states its kind and what belongs to it.
/// <para><b>Version 1</b> held a name, a descriptive type text and the name of the case continued from, and every
/// case was analysed as a nonlinear static case that carries the loads of the cases before it. That is what a
/// version-1 row becomes: nonlinear static, continuing from the case it named, without geometric nonlinearity —
/// nothing is switched on by the upgrade. In a row, a member that is absent means exactly that version-1 meaning, so
/// a configuration that only went through the upgrade still reads as it did.</para>
/// <para>The names stay in the rows beside the identities, for the application that shows them and for files of
/// version 1; when both are there they must agree, and a row that contradicts itself is refused, never resolved in
/// favour of one of the two.</para></summary>
public static class FemCaseSchema
{
    /// <summary>An identity for a row that has none: the same one every time for the same scope, kind and name, so
    /// that reading a version-1 file twice gives the same identities.</summary>
    public static string DerivedId(Guid? scope, string type, string name)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{scope?.ToString("D") ?? "Mcotis.FemModel"}|{type}|{name}"));
        return new Guid(bytes.AsSpan(0, 16)).ToString("D");
    }

    public static Native.CaseKind KindOf(FemDefinitionRowDto row) => row.CaseKind is null ? Native.CaseKind.NonlinearStatic
        : Enum.TryParse<Native.CaseKind>(row.CaseKind, out var kind) && Enum.IsDefined(kind) && !int.TryParse(row.CaseKind, out _) ? kind
        : throw new InvalidDataException($"Load case '{row.Name}' is of kind '{row.CaseKind}', which this version of Mcotis does not know.");

    public static Native.GeometricNonlinearity GeometricOf(FemDefinitionRowDto row) => row.GeometricMode is null
        ? KindOf(row) == Native.CaseKind.InitialPDelta ? Native.GeometricNonlinearity.PDelta : Native.GeometricNonlinearity.None
        : Enum.TryParse<Native.GeometricNonlinearity>(row.GeometricMode, out var mode) && Enum.IsDefined(mode) && !int.TryParse(row.GeometricMode, out _) ? mode
        : throw new InvalidDataException($"Load case '{row.Name}' has the geometric nonlinearity '{row.GeometricMode}', which this version of Mcotis does not know.");

    /// <summary>Gives every row that has no identity one, and every reference that is only a name the identity of the
    /// case it names. Rows of version 1 come out as rows of version 2; rows that are complete come out as they are.
    /// </summary>
    /// <param name="scope">The database the rows belong to; null for a file that is no database.</param>
    /// <param name="storedIds">Identities a database already holds for its rows, by kind ("LoadCase",
    /// "LoadCombination") and name: a row keeps the one it had.</param>
    public static (List<FemDefinitionRowDto> Cases, List<FemDefinitionRowDto> Combinations) Complete(
        IReadOnlyList<FemDefinitionRowDto> cases, IReadOnlyList<FemDefinitionRowDto> combinations, Guid? scope,
        IReadOnlyDictionary<(string Type, string Name), string>? storedIds = null)
    {
        string Identity(FemDefinitionRowDto row, string type) => row.Id
            ?? (storedIds is not null && storedIds.TryGetValue((type, row.Name), out var stored) ? stored : DerivedId(scope, type, row.Name));
        var named = cases.Select(row => row with { Id = Identity(row, "LoadCase") }).ToList();
        string? Resolve(FemDefinitionRowDto row, string? name, string? id, string how)
        {
            if (id is not null || FemLoadPlan.IsNoInitialCase(name)) return id;
            var found = named.Where(other => other.Name.Equals(name!.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            return found.Length == 1 ? found[0].Id
                : throw new InvalidDataException(found.Length == 0
                    ? $"Load case '{row.Name}' {how} '{name}', which the configuration does not hold."
                    : $"Load case '{row.Name}' {how} '{name}', and {found.Length} load cases have that name.");
        }
        var completed = named.Select(row => row with
        {
            InitialCaseId = Resolve(row, row.InitialCase, row.InitialCaseId, "continues from"),
            StiffnessSourceCaseId = Resolve(row, row.StiffnessSourceCase, row.StiffnessSourceCaseId, "uses the stiffness of"),
        }).ToList();
        return (completed, combinations.Select(row => row with { Id = Identity(row, "LoadCombination") }).ToList());
    }

    /// <summary>Rows of version 2 as a whole: identities, references that agree with the names beside them, known
    /// kinds, and what each kind may carry — the rules of <see cref="Native.LoadCaseRules"/>, the same the native
    /// document applies. Throws <see cref="InvalidDataException"/> naming the case.</summary>
    public static void Validate(IReadOnlyList<FemDefinitionRowDto> cases, IReadOnlyList<FemDefinitionRowDto> combinations,
        IReadOnlyList<FemMassSourceDto> massSources)
    {
        static Guid Identity(string? id, string what) => Guid.TryParse(id, out var value) && value != Guid.Empty ? value
            : throw new InvalidDataException($"{what} has no identity.");
        var ids = new HashSet<Guid>();
        void Unique(Guid id, string what)
        {
            if (!ids.Add(id)) throw new InvalidDataException($"{what} has the identity of another object of the configuration.");
        }
        foreach (var duplicate in cases.GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            throw new InvalidDataException($"{duplicate.Count()} load cases are named '{duplicate.Key}'.");

        var sources = new Dictionary<Guid, FemMassSourceDto>();
        foreach (var source in massSources)
        {
            var id = Identity(source.Id, $"Mass source '{source.Name}'");
            Unique(id, $"Mass source '{source.Name}'");
            sources[id] = source;
        }
        var byId = new Dictionary<Guid, FemDefinitionRowDto>();
        foreach (var row in cases)
        {
            var id = Identity(row.Id, $"Load case '{row.Name}'");
            Unique(id, $"Load case '{row.Name}'");
            byId[id] = row;
        }
        foreach (var row in combinations) Unique(Identity(row.Id, $"Load combination '{row.Name}'"), $"Load combination '{row.Name}'");

        // The identity a row gives for another case and the name it gives beside it are one reference, said twice.
        Guid? Reference(FemDefinitionRowDto row, string? name, string? id, string how)
        {
            bool none = FemLoadPlan.IsNoInitialCase(name);
            if (id is null)
                return none ? null : throw new InvalidDataException($"Load case '{row.Name}' {how} '{name}' by name only: version 2 refers to a case by its identity.");
            if (!Guid.TryParse(id, out var value) || !byId.TryGetValue(value, out var other))
                throw new InvalidDataException($"Load case '{row.Name}' {how} a load case the configuration does not hold.");
            if (none || !other.Name.Equals(name!.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Load case '{row.Name}' {how} '{(none ? "no case" : name)}' by name and '{other.Name}' by identity: the two do not agree.");
            return value;
        }

        var native = new Dictionary<Native.ObjectId, Native.LoadCase>();
        foreach (var row in cases)
        {
            int terms;
            try { terms = string.IsNullOrWhiteSpace(row.Definition) ? 0 : FemLoadDefinition.Parse(row.Definition).Count; }
            catch (ArgumentException) { terms = 1; }   // a definition that does not parse is reported by the application; it is not empty
            Native.ModalEigenOptions? modal = null;
            if (row.Modal is { } options)
            {
                if (!Guid.TryParse(options.MassSourceId, out var massId) || !sources.TryGetValue(massId, out var mass))
                    throw new InvalidDataException($"Load case '{row.Name}' uses a mass source the configuration does not hold.");
                if (!mass.Name.Equals(options.MassSource?.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Load case '{row.Name}' names the mass source '{options.MassSource}' and refers to '{mass.Name}' by identity: the two do not agree.");
                modal = new(new(massId), options.RequestedModes);
            }
            var id = new Native.ObjectId(Guid.Parse(row.Id!));
            native[id] = new Native.LoadCase(id, row.Name, KindOf(row), [.. Enumerable.Repeat(new Native.LoadCaseComponent(id, 1), terms)])
            {
                InitialCaseId = Reference(row, row.InitialCase, row.InitialCaseId, "continues from") is { } initial ? new(initial) : null,
                StiffnessSourceCaseId = Reference(row, row.StiffnessSourceCase, row.StiffnessSourceCaseId, "uses the stiffness of") is { } stiffness ? new(stiffness) : null,
                GeometricMode = GeometricOf(row),
                NonlinearControls = row.NonlinearControls is { } controls
                    ? new(controls.MaxLoadSteps, controls.MaxIterationsPerStep, controls.RelativeResidualTolerance, controls.RelativeDisplacementTolerance) : null,
                ModalOptions = modal,
                Run = row.Run,
                FitCableTargetForces = row.CableTargetForces == true,
            };
        }
        foreach (var loadCase in native.Values)
            if (Native.LoadCaseRules.Check(loadCase, id => native.GetValueOrDefault(id), id => sources.ContainsKey(id.Value)).FirstOrDefault() is { Message: { } message })
                throw new InvalidDataException(message);
        if (Native.LoadCaseRules.Circular(native.Values).FirstOrDefault() is { IsEmpty: false } circular)
            throw new InvalidDataException($"Load case '{native[circular].Name}' depends, through the cases it continues from or takes its stiffness from, on itself.");
    }

    /// <summary>The rows as the revision of a configuration is identified by: without the identities, which are not
    /// content — the names beside them carry the references. A configuration of version 1 and the same one after the
    /// upgrade are one revision.</summary>
    public static FemDefinitionRowDto Content(FemDefinitionRowDto row) => row with
    {
        Id = null, InitialCaseId = null, StiffnessSourceCaseId = null,
        Modal = row.Modal is { } modal ? modal with { MassSourceId = null } : null,
    };
}
