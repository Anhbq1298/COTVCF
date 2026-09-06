using COT_VCF.Domain;

namespace COT_VCF.Application.Workers;

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
