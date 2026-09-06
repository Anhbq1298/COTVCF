using COT_VCF.Domain;
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
    Task<ModelInfo> GetCurrentModelInfoAsync();
    Task<ModelSnapshot> ReadAsync();
    Task<IReadOnlyList<PointLoad>> ReadPointLoadsAsync(ModelInfo expected);
    /// <summary>Reads one point's current global coordinates from the verified active model.</summary>
    Task<Position> GetPointPositionAsync(string pointName, ModelInfo expected);
    /// <summary>Reads the ordinary, global-axis force assignments for one point from the verified active model.</summary>
    Task<IReadOnlyList<PointLoad>> GetPointLoadsAsync(string pointName, ModelInfo expected);
    /// <summary>Reads all target-force assignments for one cable from the verified active model.</summary>
    Task<IReadOnlyList<CableTargetForce>> GetCableTargetForcesAsync(string cableName, ModelInfo expected);
    Task<IReadOnlyList<ForceSource>> ReadForcesAsync(IReadOnlyList<StructuralMember> members, ModelInfo expected);
    Task ApplyLoadsAsync(IReadOnlyList<PointLoad> loads, ModelInfo expected);
    Task MovePointsAsync(GeometryModel expectedGeometry, GeometryModel proposed, ModelInfo expected);
    /// <summary>Assigns a Cable Loads / Target Force value on an existing load pattern.
    /// Reserved for execution and never called while editing Tab 4.</summary>
    Task SetCableTargetForceAsync(string cableName, string loadPattern, double targetForce, ModelInfo expected);
    /// <summary>Every load case name in the model. Read-only; used by config compatibility validation.</summary>
    Task<IReadOnlyList<string>> GetLoadCaseNamesAsync(ModelInfo expected);
}
