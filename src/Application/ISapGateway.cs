using COT_VCF.Domain;
namespace COT_VCF.Application;

public interface ISapGateway : IDisposable
{
    Task<ModelInfo> ConnectAsync();
    Task<ModelSnapshot> ReadAsync();
    Task<IReadOnlyList<ForceSource>> ReadForcesAsync(IReadOnlyList<StructuralMember> members, ModelInfo expected);
    Task ApplyLoadsAsync(IReadOnlyList<PointLoad> loads, ModelInfo expected);
    Task MovePointsAsync(GeometryModel expectedGeometry, GeometryModel proposed, ModelInfo expected);
}
// Deliberately no implementation in this phase.
public interface IIterationEngine
{
    Task RunAsync(ApplicationState configuration, CancellationToken cancellationToken);
}
