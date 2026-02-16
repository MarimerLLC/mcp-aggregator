using McpAggregator.Core.Storage;
using Microsoft.Extensions.AI;
using Rocks;

[assembly: Rock(typeof(IRegistryPersistence), BuildType.Create)]
[assembly: Rock(typeof(IChatClient), BuildType.Create)]
