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
    Task<IReadOnlyList<ForceSource>> ReadForcesAsync(IReadOnlyList<StructuralMember> members, ModelInfo expected);
    Task ApplyLoadsAsync(IReadOnlyList<PointLoad> loads, ModelInfo expected);
    Task MovePointsAsync(GeometryModel expectedGeometry, GeometryModel proposed, ModelInfo expected);
    /// <summary>Assign → Cable Loads → Target Force for one cable on the given load pattern (created as a Prestress
    /// pattern if missing). Reserved for the execution workflow; never called while editing Tab 4.</summary>
    Task SetCableTargetForceAsync(string cableName, string loadPattern, double targetForce, ModelInfo expected);
    /// <summary>Every load case name in the model. Read-only; used by config compatibility validation.</summary>
    Task<IReadOnlyList<string>> GetLoadCaseNamesAsync(ModelInfo expected);
}
// Deliberately no implementation in this phase.
public interface IIterationEngine
{
    Task RunAsync(ApplicationState configuration, CancellationToken cancellationToken);
}
