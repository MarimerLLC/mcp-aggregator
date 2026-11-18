using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Encodings.Web;
using McpAggregator.HttpServer.PublicEndpoint;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpAggregator.HttpServer.Tests;

/// <summary>
/// The real HTTP host on two loopback ports, with the public endpoint enabled. Google sign-in is
/// replaced by <see cref="TestLoginHandler"/>: a request carrying <c>X-Test-Login: {email}</c> counts as
/// signed in. Everything else (bearer handler, OAuth endpoints, MCP server) is production code.
/// </summary>
public sealed class PublicEndpointHost : IAsyncDisposable
{
    public const string BaseUrl = "https://public.test";
    public const string AllowedEmail = "owner@example.com";
    public const string ClaudeCallback = "https://claude.ai/api/mcp/auth_callback";

    public static readonly string Pat = TokenService.NewPersonalAccessToken();

    private readonly WebApplication _app;
    private readonly string _dataDir;

    private PublicEndpointHost(WebApplication app, string dataDir, int privatePort, int publicPort)
    {
        _app = app;
        _dataDir = dataDir;
        PrivateUrl = new Uri($"http://127.0.0.1:{privatePort}");
        PublicUrl = new Uri($"http://127.0.0.1:{publicPort}");
    }

    public Uri PrivateUrl { get; }
    public Uri PublicUrl { get; }
    public IServiceProvider Services => _app.Services;

    public static async Task<PublicEndpointHost> StartAsync(string wrapperMode = "Lazy", bool configureGoogle = true)
    {
        var privatePort = FreePort();
        var publicPort = FreePort();
        var dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-http-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["urls"] = $"http://127.0.0.1:{privatePort}",
            ["McpAggregator:DataDirectory"] = dataDir,
            ["McpAggregator:WrapperMode"] = wrapperMode,
            ["McpAggregator:SkillMaintenance:Mode"] = "Off",
            ["McpAggregator:PublicEndpoint:Enabled"] = "true",
            ["McpAggregator:PublicEndpoint:ListenUrl"] = $"http://127.0.0.1:{publicPort}",
            ["McpAggregator:PublicEndpoint:BaseUrl"] = BaseUrl,
            ["McpAggregator:PublicEndpoint:AllowedEmails:0"] = AllowedEmail,
            ["McpAggregator:PublicEndpoint:Google:ClientId"] = configureGoogle ? "test-client-id.apps.googleusercontent.com" : "",
            ["McpAggregator:PublicEndpoint:Google:ClientSecret"] = configureGoogle ? "test-secret" : "",
            ["McpAggregator:PublicEndpoint:PersonalAccessTokens:0:Name"] = "tests",
            ["McpAggregator:PublicEndpoint:PersonalAccessTokens:0:Sha256"] = TokenService.HashPersonalAccessToken(Pat),
        });

        HttpHost.ConfigureServices(builder);

        // Stand in for the Google round trip: the login cookie scheme becomes a header check.
        builder.Services.Configure<AuthenticationOptions>(o =>
        {
            foreach (var scheme in o.Schemes.Where(s => s.Name == OAuthEndpoints.LoginCookieScheme))
                scheme.HandlerType = typeof(TestLoginHandler);
        });

        var app = builder.Build();
        HttpHost.ConfigurePipeline(app);
        await app.StartAsync();

        return new PublicEndpointHost(app, dataDir, privatePort, publicPort);
    }

    /// <summary>A client that does not follow redirects or keep cookies, so tests see every hop.</summary>
    public HttpClient CreateHttpClient(Uri baseAddress)
        => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = baseAddress };

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

public sealed class TestLoginHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder), IAuthenticationSignOutHandler
{
    public const string Header = "X-Test-Login";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers[Header] is not { Count: 1 } value)
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Email, value.ToString()),
            new Claim(ClaimTypes.NameIdentifier, value.ToString()),
            new Claim("email_verified", Request.Headers["X-Test-Unverified"].Count > 0 ? "False" : "True"),
        ], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    // The real challenge goes to Google; the authorize endpoint challenges Google by name.
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public Task SignOutAsync(AuthenticationProperties? properties) => Task.CompletedTask;
}
