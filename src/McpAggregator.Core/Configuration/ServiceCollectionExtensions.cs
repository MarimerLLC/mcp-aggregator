using McpAggregator.Core.Services;
using McpAggregator.Core.Storage;
using McpAggregator.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace McpAggregator.Core.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAggregatorCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AggregatorOptions>(configuration.GetSection(AggregatorOptions.SectionName));

        services.AddSingleton<IRegistryPersistence, JsonFilePersistence>();
        services.AddSingleton<ServerRegistry>();
        services.AddSingleton<ConnectionManager>();
        services.AddSingleton<ToolIndex>();
        services.AddSingleton<SkillStore>();
        services.AddSingleton<ToolProxyHandler>();

        services.AddHostedService<IdleConnectionCleanupService>();

        return services;
    }
}
