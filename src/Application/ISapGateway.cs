using COT_VCF.Domain;
using COT_VCF.Domain.Results;
namespace COT_VCF.Application;

public interface ISapGateway : IDisposable
{
    Task<ModelInfo> ConnectAsync();
    /// <summary>Opens a saved model in this gateway's SAP2000 instance and returns its active identity.</summary>
    Task<ModelInfo> OpenModelAsync(string modelPath);
    /// <summary>Saves the verified active model to a new path and returns the new child-model identity.</summary>
    Task<ModelInfo> SaveAsAsync(string targetPath, ModelInfo expected);
    /// <summary>Saves the verified active model without changing its identity.</summary>
    Task SaveAsync(ModelInfo expected);
    /// <summary>Runs every analysis case currently marked to run in the verified active child model.</summary>
    Task RunAnalysisAsync(ModelInfo expected);
    Task<ModelInfo> GetCurrentModelInfoAsync();
    Task<ModelSnapshot> ReadAsync();
    Task<IReadOnlyList<PointLoad>> ReadPointLoadsAsync(ModelInfo expected);
    /// <summary>Reads one point's current global coordinates from the verified active model.</summary>
    Task<Position> GetPointPositionAsync(string pointName, ModelInfo expected);
    /// <summary>Reads the ordinary, global-axis force assignments for one point from the verified active model.</summary>
    Task<IReadOnlyList<PointLoad>> GetPointLoadsAsync(string pointName, ModelInfo expected);
    /// <summary>Reads all target-force assignments for one cable from the verified active model.</summary>
    Task<IReadOnlyList<CableTargetForce>> GetCableTargetForcesAsync(string cableName, ModelInfo expected);
    /// <summary>Reads each cable's assigned section property and cross-section area [m²] from the verified
    /// active model. One row per requested cable, in order; a cable whose section can't be read comes back
    /// with <c>AreaM2 == 0</c> and a Problem message rather than throwing.</summary>
    Task<IReadOnlyList<CableSection>> GetCableSectionsAsync(IReadOnlyList<string> cableNames, ModelInfo expected);
    Task<IReadOnlyList<ForceSource>> ReadForcesAsync(IReadOnlyList<StructuralMember> members, ModelInfo expected);
    Task ApplyLoadsAsync(IReadOnlyList<PointLoad> loads, ModelInfo expected);
    Task MovePointsAsync(GeometryModel expectedGeometry, GeometryModel proposed, ModelInfo expected);
    /// <summary>Assigns a Cable Loads / Target Force value on an existing load pattern.
    /// Reserved for execution and never called while editing Tab 3.</summary>
    Task SetCableTargetForceAsync(string cableName, string loadPattern, double targetForce, ModelInfo expected);
    /// <summary>Every load case name in the model. Read-only; used by config compatibility validation.</summary>
    Task<IReadOnlyList<string>> GetLoadCaseNamesAsync(ModelInfo expected);
    /// <summary>Reads only the requested SAP groups and their point/frame/cable assignments.</summary>
    Task<IReadOnlyList<SapGroupSnapshot>> ReadOutputGroupsAsync(IReadOnlyList<string> groupNames, ModelInfo expected);
    /// <summary>Replaces the requested COTVCF-managed [O]- groups in the active baseline model. Does not save the model.</summary>
    Task ReplaceOutputGroupsAsync(IReadOnlyList<OutputSapGroupDefinition> groups, ModelInfo expected);

    /// <summary>Reads global joint displacements (U3 → Uz, mm) for the given points from the analyzed active model
    /// under exactly one output case or combo. Selects only that case for output first. One row per requested
    /// point, in the same order; each row carries an explicit <see cref="ResultValueStatus"/> — a missing result
    /// is never reported as 0.</summary>
    Task<IReadOnlyList<JointDisplacement>> GetJointDisplacementsAsync(
        IReadOnlyList<string> pointNames, string outputCase, bool isCombo, ModelInfo expected);

    /// <summary>Reads the governing axial force (max |P|, kN) for each cable from the analyzed active model
    /// under exactly one output case or combo. Selects only that case for output first. One row per
    /// requested cable, in the same order; each row carries an explicit <see cref="ResultValueStatus"/> —
    /// a missing result is never reported as 0.</summary>
    Task<IReadOnlyList<CableForceRead>> GetCableForcesAsync(
        IReadOnlyList<string> cableNames, string outputCase, bool isCombo, ModelInfo expected);
}
