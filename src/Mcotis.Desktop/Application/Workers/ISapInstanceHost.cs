using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Application.Workers;

public interface ISapInstanceHost : IAsyncDisposable
{
    int InstanceId { get; }
    ISapGateway Gateway { get; }
    Task<ModelInfo> StartAsync(string baselineModelPath, CancellationToken token);
    Task StopAsync();
}

public interface ISapInstanceHostFactory
{
    ISapInstanceHost Create(int instanceId);
}
