using COT_VCF.Domain;

namespace COT_VCF.Application;

/// <summary>Cable objects currently selected in SAP2000, plus how many selected objects were not cables.</summary>
public readonly record struct CableSelection(IReadOnlyList<string> Cables, int NonCableCount);
public readonly record struct AddCablesResult(int Added, int Ignored);

/// <summary>
/// Tab 4 service boundary. Reads the live SAP2000 selection to import cable objects, and (for a later
/// Iteration/Run workflow) applies a candidate target force. All CSI API calls stay behind <see cref="ISapGateway"/>;
/// editing Tab 4 never writes to SAP2000.
/// </summary>
public interface ICablePretensionService
{
    /// <summary>Reads the objects currently selected in SAP2000, keeps the cable objects, drops duplicates and
    /// non-cable objects, appends new <see cref="CablePretensionConfig"/> rows, and reports how many of each.</summary>
    Task<AddCablesResult> AddSelectedCablesAsync();

    /// <summary>Assigns one candidate as an Assign → Cable Loads → Target Force on the PRETENSION load pattern.
    /// Not used while editing Tab 4 — reserved for the execution workflow.</summary>
    Task ApplyTargetForceAsync(string cableName, double targetForce);

    /// <summary>Lazily enumerates the Cartesian product of the current cable configs for a downstream workflow.</summary>
    IEnumerable<PretensionCombination> EnumerateCombinations();
}

public sealed class CablePretensionService(ISapGateway sap, ApplicationState state) : ICablePretensionService
{
    public async Task<AddCablesResult> AddSelectedCablesAsync()
    {
        if (state.Model is null) throw new InvalidOperationException("Connect to SAP2000 and load geometry first.");
        var selection = await sap.GetSelectedCableNamesAsync(state.Model);
        var modelCables = state.Geometry.Members.Where(m => m.Kind == MemberKind.Cable).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var existing = state.Cables.Select(c => c.CableName).ToHashSet(StringComparer.Ordinal);

        int added = 0, ignored = selection.NonCableCount;
        foreach (var name in selection.Cables.Distinct(StringComparer.Ordinal))
        {
            if (!modelCables.Contains(name) || !existing.Add(name)) { ignored++; continue; }
            state.Cables.Add(new CablePretensionConfig { CableName = name });
            added++;
        }
        return new AddCablesResult(added, ignored);
    }

    public Task ApplyTargetForceAsync(string cableName, double targetForce)
    {
        if (state.Model is null) throw new InvalidOperationException("Connect to SAP2000 first.");
        return sap.SetCableTargetForceAsync(cableName, CablePretensionConfig.PretensionLoadCase, targetForce, state.Model);
    }

    public IEnumerable<PretensionCombination> EnumerateCombinations() => PretensionCandidates.Enumerate(state.Cables.ToArray());
}
