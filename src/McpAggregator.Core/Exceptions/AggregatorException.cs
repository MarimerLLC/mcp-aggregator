using ModelContextProtocol;

namespace McpAggregator.Core.Exceptions;

/// <summary>
/// A failure whose message is written for the caller. Derives from <see cref="McpException"/> because
/// the SDK passes an <see cref="McpException"/>'s message on when a tool throws it
/// ("An error occurred invoking '{tool}': {message}") and replaces any other exception with the bare
/// "An error occurred invoking '{tool}'." (issue #72).
/// </summary>
public class AggregatorException(string message, Exception? inner = null)
    : McpException(message, inner);

public class ServerNotFoundException : AggregatorException
{
    public ServerNotFoundException(string serverName, IEnumerable<string>? registeredServers = null)
        : base(BuildMessage(serverName, registeredServers))
    {
        ServerName = serverName;
    }

    public string ServerName { get; }

    private static string BuildMessage(string serverName, IEnumerable<string>? registeredServers)
    {
        var message = $"Server '{serverName}' not found.";
        if (registeredServers is null)
            return message;

        var names = registeredServers.Order(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count == 0
            ? message + " No servers are registered."
            : message + $" Registered servers: [{string.Join(", ", names)}].";
    }
}

public class ServerAlreadyExistsException(string serverName)
    : AggregatorException($"Server '{serverName}' already exists.");

public class ServerUnavailableException(string serverName, Exception? inner = null)
    : AggregatorException($"Server '{serverName}' is unavailable.", inner);

public class ToolNotFoundException(string serverName, string toolName)
    : AggregatorException($"Tool '{toolName}' not found on server '{serverName}'.");

public class ToolExecutionException(string serverName, string toolName, string errorMessage)
    : AggregatorException($"Tool '{toolName}' on server '{serverName}' returned error: {errorMessage}");

public class InvalidTransportConfigException(string message)
    : AggregatorException(message);
