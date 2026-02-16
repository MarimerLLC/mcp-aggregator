namespace McpAggregator.Core.Exceptions;

public class AggregatorException(string message, Exception? inner = null)
    : Exception(message, inner);

public class ServerNotFoundException(string serverName)
    : AggregatorException($"Server '{serverName}' not found.");

public class ServerAlreadyExistsException(string serverName)
    : AggregatorException($"Server '{serverName}' already exists.");

public class ServerUnavailableException(string serverName, Exception? inner = null)
    : AggregatorException($"Server '{serverName}' is unavailable.", inner);

public class ToolNotFoundException(string serverName, string toolName)
    : AggregatorException($"Tool '{toolName}' not found on server '{serverName}'.");

public class InvalidTransportConfigException(string message)
    : AggregatorException(message);
