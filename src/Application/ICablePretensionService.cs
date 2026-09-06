using COT_VCF.Domain;

namespace COT_VCF.Application;

/// <summary>
/// Application seam that turns Tab 4 cable configs into concrete
/// PRETENSION assignments. All CSI API calls stay behind <see cref="ISapGateway"/>; nothing here runs
/// while the user is only editing Tab 4.
/// </summary>
public interface ICablePretensionService
{
    /// <summary>Assigns one candidate as Assign → Cable Loads → Target Force on the configured load pattern.</summary>
    Task ApplyTargetForceAsync(string cableName, double targetForce);

    /// <summary>Lazily enumerates the Cartesian product of the current cable configs.</summary>
    IEnumerable<PretensionCombination> EnumerateCombinations();
}

public sealed class CablePretensionService(ISapGateway sap, ApplicationState state) : ICablePretensionService
{
    public Task ApplyTargetForceAsync(string cableName, double targetForce)
    {
        if (state.Model is null) throw new InvalidOperationException("Connect to SAP2000 first.");
        var cable = state.Cables.SingleOrDefault(candidate => candidate.CableName.Equals(cableName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Cable {cableName} is not configured in Tab 4.");
        if (string.IsNullOrWhiteSpace(cable.LoadPattern))
            throw new InvalidOperationException($"Cable {cableName} has no target-force load pattern.");
        return sap.SetCableTargetForceAsync(cableName, cable.LoadPattern.Trim(), targetForce, state.Model);
    }

    public IEnumerable<PretensionCombination> EnumerateCombinations() => PretensionCandidates.Enumerate(state.Cables.ToArray());
}
