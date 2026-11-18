using McpAggregator.HttpServer;
using Spectre.Console.Cli;

var app = new CommandApp<ServeCommand>();
app.Configure(config =>
{
    config.AddCommand<ServeCommand>("serve")
        .WithDescription("Run the aggregator (the default when no command is given).");
    config.AddCommand<NewPatCommand>("new-pat")
        .WithDescription("Mint a personal access token for the public endpoint.");
});
return await app.RunAsync(args);
