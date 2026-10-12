using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Application.Workers;

/// <summary>
/// Counts the SAP2000 instance already connected by the application as study worker 1.
/// Only worker IDs 2 and above create additional SAP2000 processes.
/// </summary>
public sealed class ConnectedSapInstanceHostFactory(
    ISapGateway connectedGateway,
    ISapInstanceHostFactory additionalHostFactory) : ISapInstanceHostFactory
{
    public ISapInstanceHost Create(int instanceId)
    {
        if (instanceId < 1) throw new ArgumentOutOfRangeException(nameof(instanceId));
        return instanceId == 1
            ? new ConnectedSapInstanceHost(instanceId, connectedGateway)
            : additionalHostFactory.Create(instanceId);
    }
}

/// <summary>
/// Uses the application's existing SAP2000 connection without taking ownership of it.
/// </summary>
internal sealed class ConnectedSapInstanceHost(int instanceId, ISapGateway gateway) : ISapInstanceHost
{
    public int InstanceId => instanceId;
    public ISapGateway Gateway => gateway;

    public Task<ModelInfo> StartAsync(string baselineModelPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return gateway.OpenModelAsync(baselineModelPath);
    }

    public Task StopAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
