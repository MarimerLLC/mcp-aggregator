using McpAggregator.Core.Models;

namespace McpAggregator.Core.Storage;

public interface IRegistryPersistence
{
    Task<RegistryData> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(RegistryData data, CancellationToken ct = default);
}
