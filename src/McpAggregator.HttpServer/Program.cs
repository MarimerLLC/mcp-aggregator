using McpAggregator.HttpServer;
using Spectre.Console.Cli;

var app = new CommandApp<ServeCommand>();
return await app.RunAsync(args);
