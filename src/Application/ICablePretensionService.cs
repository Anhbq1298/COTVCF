using COT_VCF.Domain;

namespace COT_VCF.Application;

/// <summary>
/// Application seam that turns Tab 4 cable configs into concrete
/// PRETENSION assignments. All CSI API calls stay behind <see cref="ISapGateway"/>; nothing here runs
/// while the user is only editing Tab 4.
/// </summary>
public interface ICablePretensionService
{
    /// <summary>Assigns one candidate as an Assign → Cable Loads → Target Force on the PRETENSION load pattern.</summary>
    Task ApplyTargetForceAsync(string cableName, double targetForce);

    /// <summary>Lazily enumerates the Cartesian product of the current cable configs.</summary>
    IEnumerable<PretensionCombination> EnumerateCombinations();
}

public sealed class CablePretensionService(ISapGateway sap, ApplicationState state) : ICablePretensionService
{
    public Task ApplyTargetForceAsync(string cableName, double targetForce)
    {
        if (state.Model is null) throw new InvalidOperationException("Connect to SAP2000 first.");
        return sap.SetCableTargetForceAsync(cableName, CablePretensionConfig.PretensionLoadCase, targetForce, state.Model);
    }

    public IEnumerable<PretensionCombination> EnumerateCombinations() => PretensionCandidates.Enumerate(state.Cables.ToArray());
}
