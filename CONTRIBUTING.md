# Contributing to MCP Aggregator

Thank you for your interest in contributing! This document provides guidelines and information for contributors.

Please read and follow our [Code of Conduct](CODE_OF_CONDUCT.md) in all interactions.

## Reporting Issues

Use [GitHub Issues](../../issues) to report bugs. Include:

- A clear description of the problem
- Steps to reproduce
- Expected vs actual behavior
- .NET version and OS
- Relevant logs or error messages

## Suggesting Features

Open a [GitHub Issue](../../issues) to propose new features before starting work. This lets us discuss the approach and avoid duplicate effort.

## Pull Requests

1. Fork the repository and create a branch from `main`
2. Make your changes (see Development Setup and Coding Guidelines below)
3. Ensure the project builds cleanly with `dotnet build`
4. Submit a pull request with a clear description of what changed and why

Keep PRs focused — one logical change per PR.

## Development Setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (preview)

### Build & Run

```bash
dotnet build                                          # Build all projects
dotnet run --project src/McpAggregator.HttpServer      # Run HTTP server
dotnet run --project src/McpAggregator.StdioServer     # Run stdio server
```

### Project Structure

- `src/McpAggregator.Core/` — Shared library: models, services, MCP tools
- `src/McpAggregator.StdioServer/` — Stdio MCP host (console app)
- `src/McpAggregator.HttpServer/` — HTTP/SSE MCP + REST API host (web app)
- `data/` — Runtime data directory

## Coding Guidelines

- Follow existing code patterns and conventions
- MCP tools are defined in `McpAggregator.Core` — both hosts discover them via assembly scanning
- **StdioServer must never write to stdout/stderr** — those streams are the MCP transport; use file or OTLP logging sinks only
- Registry persistence uses atomic writes (temp file + rename) to prevent corruption
- Connections to downstream MCP servers are lazy — connected on first use, not at registration

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
