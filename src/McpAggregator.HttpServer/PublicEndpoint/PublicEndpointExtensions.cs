using System.Security.Claims;
using McpAggregator.Core.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace McpAggregator.HttpServer.PublicEndpoint;

public static class PublicEndpointExtensions
{
    /// <summary>
    /// Registers the public endpoint when <c>McpAggregator:PublicEndpoint:Enabled</c> is true: the
    /// second listener, bearer authentication, the OAuth server with Google sign-in, and Data
    /// Protection keys under <c>{DataDirectory}/keys</c> so issued tokens survive restarts.
    /// Returns false (and registers nothing) when disabled.
    /// </summary>
    public static bool AddPublicEndpoint(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(PublicEndpointOptions.SectionName);
        var options = section.Get<PublicEndpointOptions>() ?? new PublicEndpointOptions();
        if (!options.Enabled)
            return false;

        Validate(options, builder.Configuration);

        // Add the public listener to whatever the private one is bound to. Setting URLs makes Kestrel
        // ignore HTTP_PORTS (which the aspnet container image sets), so carry those over too.
        builder.WebHost.UseUrls($"{PrivateUrls(builder.Configuration)};{options.ListenUrl}");

        var services = builder.Services;
        services.Configure<PublicEndpointOptions>(section);
        services.AddSingleton<TokenService>();
        services.AddSingleton<AuthorizationCodeStore>();
        services.TryAddSingleton(TimeProvider.System);

        var dataDirectory = builder.Configuration[$"{AggregatorOptions.SectionName}:DataDirectory"] ?? "data";
        services.AddDataProtection()
            .SetApplicationName("mcp-aggregator")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));

        services.AddAntiforgery(o =>
        {
            o.Cookie.Name = "mcpagg_af";
            o.Cookie.Path = "/oauth";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.FormFieldName = "__af";
        });

        // Several schemes and no default: nothing authenticates a request unless asked to by name,
        // and the authentication middleware runs only on the public listener (UsePublicEndpoint), so
        // the private listener behaves exactly as before.
        var authentication = services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, PublicBearerHandler>(PublicBearerHandler.SchemeName, null)
            .AddCookie(OAuthEndpoints.LoginCookieScheme, o =>
            {
                o.Cookie.Name = "mcpagg_login";
                o.Cookie.Path = "/oauth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.ExpireTimeSpan = TimeSpan.FromMinutes(15);
                o.SlidingExpiration = false;
            });

        // Google sign-in only when configured: the Google handler validates its options on every
        // request it sees, so registering it without credentials would fail every public request.
        // Without it the public endpoint still serves personal access tokens.
        if (string.IsNullOrWhiteSpace(options.Google.ClientId) || string.IsNullOrWhiteSpace(options.Google.ClientSecret))
            return true;

        authentication
            .AddGoogle(OAuthEndpoints.GoogleScheme, o =>
            {
                o.SignInScheme = OAuthEndpoints.LoginCookieScheme;
                o.CallbackPath = OAuthEndpoints.GoogleCallbackPath;
                o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
                o.ClaimActions.MapJsonKey("email_verified", "email_verified");
                o.Events.OnRemoteFailure = async ctx =>
                {
                    ctx.HandleResponse();
                    await ConsentPage.Error("Google sign-in failed", ctx.Failure?.Message ?? "Unknown error.",
                        StatusCodes.Status400BadRequest).ExecuteAsync(ctx.HttpContext);
                };
            });

        // Google credentials come from options so they can live in a Kubernetes Secret.
        services.AddOptions<GoogleOptions>(OAuthEndpoints.GoogleScheme)
            .Configure<IOptions<PublicEndpointOptions>>((google, pub) =>
            {
                google.ClientId = pub.Value.Google.ClientId;
                google.ClientSecret = pub.Value.Google.ClientSecret;
            });

        return true;
    }

    public static void UsePublicEndpoint(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<PublicEndpointOptions>>().Value;

        app.UseMiddleware<PublicEndpointMiddleware>();
        // Handles the Google callback path. Called on the top-level pipeline, after the middleware above,
        // on purpose: WebApplication auto-inserts UseAuthentication ahead of every user middleware unless
        // the top-level builder already has it (a UseWhen branch does not count), and that early copy
        // would handle the callback before the scheme/host rewrite (Google redirect_uri mismatch) and
        // on the private listener too. Here the middleware has already 404ed OAuth paths on the private
        // listener; with no default scheme nothing else authenticates.
        app.UseAuthentication();
        app.MapOAuthEndpoints();

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PublicEndpointExtensions));
        logger.LogInformation(
            "Public endpoint enabled at {BaseUrl} (listener {ListenUrl}); {EmailCount} allowed Google account(s), {PatCount} personal access token(s), Google sign-in {GoogleState}",
            options.BaseUrlTrimmed, options.ListenUrl, options.AllowedEmails.Count, options.PersonalAccessTokens.Count,
            string.IsNullOrEmpty(options.Google.ClientId) ? "NOT configured" : "configured");
    }

    /// <summary>The private listener's URLs as Kestrel would resolve them without the public one.</summary>
    internal static string PrivateUrls(IConfiguration configuration)
    {
        var urls = configuration[WebHostDefaults.ServerUrlsKey];
        if (!string.IsNullOrWhiteSpace(urls))
            return urls;

        var fromPorts = (configuration[WebHostDefaults.HttpPortsKey] ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => $"http://*:{p}")
            .Concat((configuration[WebHostDefaults.HttpsPortsKey] ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => $"https://*:{p}"))
            .ToList();

        return fromPorts.Count > 0 ? string.Join(';', fromPorts) : "http://localhost:5000";
    }

    private static void Validate(PublicEndpointOptions options, IConfiguration configuration)
    {
        if (!Uri.TryCreate(options.BaseUrlTrimmed, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps
            || baseUri.AbsolutePath != "/" || !string.IsNullOrEmpty(baseUri.Query))
        {
            throw new InvalidOperationException(
                $"{PublicEndpointOptions.SectionName}:BaseUrl must be an https origin with no path, e.g. https://mcp-aggregator-public.example.ts.net (got '{options.BaseUrl}').");
        }

        int port;
        try
        {
            port = options.ListenPort;
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{PublicEndpointOptions.SectionName}:ListenUrl '{options.ListenUrl}' is not a valid binding.", ex);
        }

        if (PrivateUrls(configuration).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(u => BindingAddress.Parse(u).Port == port))
        {
            throw new InvalidOperationException(
                $"{PublicEndpointOptions.SectionName}:ListenUrl uses port {port}, which the private listener already binds; pick a different port.");
        }
    }
}
