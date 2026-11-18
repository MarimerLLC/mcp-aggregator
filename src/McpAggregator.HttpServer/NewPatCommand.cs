using System.ComponentModel;
using McpAggregator.HttpServer.PublicEndpoint;
using Spectre.Console.Cli;

namespace McpAggregator.HttpServer;

public sealed class NewPatSettings : CommandSettings
{
    [CommandOption("--name <NAME>")]
    [Description("A label for the token, e.g. 'claude-code-cloud'. Shown in logs as the caller identity.")]
    public string Name { get; set; } = "";

    [CommandOption("--index <INDEX>")]
    [Description("Position in McpAggregator:PublicEndpoint:PersonalAccessTokens for the printed environment variables. Defaults to 0.")]
    public int Index { get; set; }

    public override Spectre.Console.ValidationResult Validate()
        => string.IsNullOrWhiteSpace(Name)
            ? Spectre.Console.ValidationResult.Error("--name is required")
            : Spectre.Console.ValidationResult.Success();
}

/// <summary>
/// Mints a personal access token for the public endpoint. Prints the token once (it is not stored)
/// and the SHA-256 hash that goes into configuration.
/// </summary>
public sealed class NewPatCommand : Command<NewPatSettings>
{
    protected override int Execute(CommandContext context, NewPatSettings settings, CancellationToken cancellation)
    {
        var token = TokenService.NewPersonalAccessToken();
        var hash = TokenService.HashPersonalAccessToken(token);
        var prefix = $"MCPAGGREGATOR__PUBLICENDPOINT__PERSONALACCESSTOKENS__{settings.Index}__";

        Console.WriteLine($"Personal access token '{settings.Name}' (shown once; store it in the client):");
        Console.WriteLine();
        Console.WriteLine($"  {token}");
        Console.WriteLine();
        Console.WriteLine("Add to the aggregator's configuration (e.g. the mcp-aggregator-public Secret):");
        Console.WriteLine();
        Console.WriteLine($"  {prefix}NAME={settings.Name}");
        Console.WriteLine($"  {prefix}SHA256={hash}");
        Console.WriteLine();
        Console.WriteLine("Clients send it as:  Authorization: Bearer <token>");
        return 0;
    }
}
