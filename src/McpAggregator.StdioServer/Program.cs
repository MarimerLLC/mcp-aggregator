// CRITICAL: StdioServer must NEVER write to stdout/stderr.
// Those streams ARE the MCP transport. Any output corrupts the protocol.

using McpAggregator.StdioServer;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp<ServeCommand>();
app.Configure(config =>
{
    // Redirect Spectre output to null so it never touches stdout/stderr
    config.Settings.Console = AnsiConsole.Create(new AnsiConsoleSettings
    {
        Out = new AnsiConsoleOutput(TextWriter.Null),
    });
});

return await app.RunAsync(args);
