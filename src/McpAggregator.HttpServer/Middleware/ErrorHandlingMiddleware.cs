using System.Text.Json;
using McpAggregator.Core.Exceptions;

namespace McpAggregator.HttpServer.Middleware;

public class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;

    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing {Method} {Path}", context.Request.Method, context.Request.Path);
            await HandleExceptionAsync(context, ex);
        }
    }

    private static async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var (statusCode, message) = exception switch
        {
            ServerNotFoundException => (StatusCodes.Status404NotFound, exception.Message),
            ServerAlreadyExistsException => (StatusCodes.Status409Conflict, exception.Message),
            ToolNotFoundException => (StatusCodes.Status404NotFound, exception.Message),
            ServerUnavailableException => (StatusCodes.Status503ServiceUnavailable, exception.Message),
            InvalidTransportConfigException => (StatusCodes.Status400BadRequest, exception.Message),
            AggregatorException => (StatusCodes.Status400BadRequest, exception.Message),
            OperationCanceledException => (StatusCodes.Status408RequestTimeout, "Request timed out."),
            _ => (StatusCodes.Status500InternalServerError, "An internal error occurred.")
        };

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var response = JsonSerializer.Serialize(new { error = message });
        await context.Response.WriteAsync(response);
    }
}
