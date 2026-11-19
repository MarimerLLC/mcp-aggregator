using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using McpAggregator.HttpServer.PublicEndpoint;
using Microsoft.AspNetCore.WebUtilities;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpAggregator.HttpServer.Tests;

[TestClass]
public sealed partial class PublicEndpointTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static PublicEndpointHost _host = null!;

    [ClassInitialize]
    public static async Task Start(TestContext _) => _host = await PublicEndpointHost.StartAsync();

    [ClassCleanup]
    public static async Task Stop() => await _host.DisposeAsync();

    // ---- Routing -------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("/health")]
    [DataRow("/api")]
    [DataRow("/api/services")]
    [DataRow("/api/admin/services")]
    [DataRow("/openapi/v1.json")]
    [DataRow("/scalar")]
    public async Task Public_listener_hides_everything_but_mcp_and_oauth(string path)
    {
        using var http = _host.CreateHttpClient(_host.PublicUrl);
        using var response = await http.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, path);
    }

    [TestMethod]
    [DataRow(OAuthEndpoints.ProtectedResourceMetadataPath)]
    [DataRow(OAuthEndpoints.AuthorizationServerMetadataPath)]
    [DataRow(OAuthEndpoints.AuthorizePath)]
    [DataRow(OAuthEndpoints.GoogleCallbackPath + "?state=x&code=y")]
    public async Task Private_listener_has_no_oauth_endpoints(string path)
    {
        using var http = _host.CreateHttpClient(_host.PrivateUrl);
        using var response = await http.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, path);

        using var health = await http.GetAsync("/health");
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
    }

    [TestMethod]
    public async Task Public_mcp_without_token_is_challenged_with_resource_metadata()
    {
        using var http = _host.CreateHttpClient(_host.PublicUrl);
        using var response = await http.PostAsync("/", JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        StringAssert.Contains(challenge, $"resource_metadata=\"{PublicEndpointHost.BaseUrl}/.well-known/oauth-protected-resource\"");
        Assert.IsFalse(challenge.Contains("error="), "no token presented, so no error code");
    }

    [TestMethod]
    [DataRow("mcpagg_pat_not-a-real-token")]
    [DataRow("mcpagg_at_garbage")]
    [DataRow("something-else")]
    public async Task Public_mcp_with_bad_token_is_rejected(string token)
    {
        using var http = _host.CreateHttpClient(_host.PublicUrl);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }) };
        request.Headers.Authorization = new("Bearer", token);
        using var response = await http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        StringAssert.Contains(response.Headers.WwwAuthenticate.ToString(), "error=\"invalid_token\"");
    }

    [TestMethod]
    public async Task Metadata_documents_point_at_the_public_base_url()
    {
        using var http = _host.CreateHttpClient(_host.PublicUrl);

        var prm = await http.GetFromJsonAsync<JsonElement>(OAuthEndpoints.ProtectedResourceMetadataPath);
        Assert.AreEqual(PublicEndpointHost.BaseUrl, prm.GetProperty("resource").GetString());
        Assert.AreEqual(PublicEndpointHost.BaseUrl, prm.GetProperty("authorization_servers")[0].GetString());

        var asm = await http.GetFromJsonAsync<JsonElement>(OAuthEndpoints.AuthorizationServerMetadataPath);
        Assert.AreEqual(PublicEndpointHost.BaseUrl, asm.GetProperty("issuer").GetString());
        Assert.AreEqual($"{PublicEndpointHost.BaseUrl}/oauth/authorize", asm.GetProperty("authorization_endpoint").GetString());
        Assert.AreEqual($"{PublicEndpointHost.BaseUrl}/oauth/token", asm.GetProperty("token_endpoint").GetString());
        Assert.AreEqual($"{PublicEndpointHost.BaseUrl}/oauth/register", asm.GetProperty("registration_endpoint").GetString());
        Assert.AreEqual("S256", asm.GetProperty("code_challenge_methods_supported")[0].GetString());
    }

    // ---- MCP through the public endpoint -------------------------------------------------------

    [TestMethod]
    [DataRow(null)]
    [DataRow("2025-06-18")]
    public async Task Pat_caller_gets_consumer_tools_but_never_admin_tools(string? protocolVersion)
    {
        await using var client = await ConnectAsync(_host.PublicUrl, PublicEndpointHost.Pat, protocolVersion);

        var names = (await client.ListToolsAsync(cancellationToken: Ct())).Select(t => t.Name).ToList();
        CollectionAssert.Contains(names, "find_tools");
        CollectionAssert.DoesNotContain(names, "show_admin_tools");
        CollectionAssert.DoesNotContain(names, "register_server");

        foreach (var admin in new[] { "show_admin_tools", "register_server", "unregister_server", "update_skill" })
        {
            var result = await client.CallToolAsync(admin, new Dictionary<string, object?>(), cancellationToken: Ct());
            Assert.IsTrue(result.IsError ?? false, admin);
            StringAssert.Contains(((TextContentBlock)result.Content[0]).Text, "not available on the public endpoint", admin);
        }

        // Still not listed after the attempt to disclose them.
        names = (await client.ListToolsAsync(cancellationToken: Ct())).Select(t => t.Name).ToList();
        CollectionAssert.DoesNotContain(names, "register_server");

        var services = await client.CallToolAsync("list_services", new Dictionary<string, object?>(), cancellationToken: Ct());
        Assert.IsFalse(services.IsError ?? false);
    }

    [TestMethod]
    public async Task Private_caller_keeps_admin_tools()
    {
        await using var client = await ConnectAsync(_host.PrivateUrl, token: null, "2025-06-18");

        var result = await client.CallToolAsync("show_admin_tools", new Dictionary<string, object?>(), cancellationToken: Ct());
        Assert.IsFalse(result.IsError ?? false);

        var names = (await client.ListToolsAsync(cancellationToken: Ct())).Select(t => t.Name).ToList();
        CollectionAssert.Contains(names, "register_server");
    }

    [TestMethod]
    public async Task Eager_mode_public_caller_still_gets_no_admin_tools()
    {
        await using var eager = await PublicEndpointHost.StartAsync(wrapperMode: "Eager");
        await using var publicClient = await ConnectAsync(eager.PublicUrl, PublicEndpointHost.Pat, protocolVersion: null);
        await using var privateClient = await ConnectAsync(eager.PrivateUrl, token: null, protocolVersion: null);

        var publicNames = (await publicClient.ListToolsAsync(cancellationToken: Ct())).Select(t => t.Name).ToList();
        var privateNames = (await privateClient.ListToolsAsync(cancellationToken: Ct())).Select(t => t.Name).ToList();

        CollectionAssert.Contains(privateNames, "register_server");
        CollectionAssert.DoesNotContain(publicNames, "register_server");

        var result = await publicClient.CallToolAsync("register_server", new Dictionary<string, object?>(), cancellationToken: Ct());
        Assert.IsTrue(result.IsError ?? false);
    }

    // ---- OAuth ---------------------------------------------------------------------------------

    [TestMethod]
    public async Task Full_authorization_code_flow_yields_a_working_token_and_refresh()
    {
        using var http = _host.CreateHttpClient(_host.PublicUrl);
        var clientId = await RegisterAsync(http);
        var (verifier, challenge) = NewPkce();

        var code = await AuthorizeAsync(http, clientId, challenge, state: "xyz");

        // A wrong verifier fails and burns the code.
        var bad = await TokenAsync(http, new() { ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = PublicEndpointHost.ClaudeCallback, ["code_verifier"] = NewPkce().Verifier });
        Assert.AreEqual(HttpStatusCode.BadRequest, bad.Status);

        // The authorization request named redirect_uri, so the token request must repeat it.
        code = await AuthorizeAsync(http, clientId, challenge, state: "xyz");
        var noRedirect = await TokenAsync(http, new() { ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["code_verifier"] = verifier });
        Assert.AreEqual(HttpStatusCode.BadRequest, noRedirect.Status);

        code = await AuthorizeAsync(http, clientId, challenge, state: "xyz");
        var issued = await TokenAsync(http, new() { ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = PublicEndpointHost.ClaudeCallback, ["code_verifier"] = verifier });
        Assert.AreEqual(HttpStatusCode.OK, issued.Status, issued.Body.ToString());
        var accessToken = issued.Body.GetProperty("access_token").GetString()!;
        var refreshToken = issued.Body.GetProperty("refresh_token").GetString()!;
        Assert.AreEqual("Bearer", issued.Body.GetProperty("token_type").GetString());

        // Codes are single use.
        var replay = await TokenAsync(http, new() { ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = PublicEndpointHost.ClaudeCallback, ["code_verifier"] = verifier });
        Assert.AreEqual(HttpStatusCode.BadRequest, replay.Status);

        await using (var client = await ConnectAsync(_host.PublicUrl, accessToken, protocolVersion: null))
        {
            var names = (await client.ListToolsAsync(cancellationToken: Ct())).Select(t => t.Name).ToList();
            CollectionAssert.Contains(names, "find_tools");
            CollectionAssert.DoesNotContain(names, "show_admin_tools");
        }

        // The access token is not a refresh token, and a refresh token is bound to its client.
        var wrongKind = await TokenAsync(http, new() { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = accessToken });
        Assert.AreEqual(HttpStatusCode.BadRequest, wrongKind.Status);
        var otherClient = await RegisterAsync(http);
        var wrongClient = await TokenAsync(http, new() { ["grant_type"] = "refresh_token", ["client_id"] = otherClient, ["refresh_token"] = refreshToken });
        Assert.AreEqual(HttpStatusCode.BadRequest, wrongClient.Status);

        var refreshed = await TokenAsync(http, new() { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = refreshToken });
        Assert.AreEqual(HttpStatusCode.OK, refreshed.Status, refreshed.Body.ToString());
        await using (var client = await ConnectAsync(_host.PublicUrl, refreshed.Body.GetProperty("access_token").GetString()!, protocolVersion: null))
            Assert.IsNotEmpty(await client.ListToolsAsync(cancellationToken: Ct()));

        // A refresh token is not accepted as an access token.
        using var asBearer = new HttpRequestMessage(HttpMethod.Post, "/") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }) };
        asBearer.Headers.Authorization = new("Bearer", refreshToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.SendAsync(asBearer)).StatusCode);
    }

    [TestMethod]
    public async Task Authorize_without_sign_in_goes_to_google_with_the_public_callback()
    {
        using var http = _host.CreateHttpClient(_host.PublicUrl);
        var clientId = await RegisterAsync(http);

        using var response = await http.GetAsync(AuthorizeUrl(clientId, NewPkce().Challenge, "s"));

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.AreEqual("accounts.google.com", location.Host);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.AreEqual($"{PublicEndpointHost.BaseUrl}/oauth/google/callback", query["redirect_uri"].ToString());
    }

    [TestMethod]
    public async Task Without_google_credentials_pats_still_work_and_authorize_explains()
    {
        await using var host = await PublicEndpointHost.StartAsync(configureGoogle: false);
        using var privateHttp = host.CreateHttpClient(host.PrivateUrl);
        using var publicHttp = host.CreateHttpClient(host.PublicUrl);

        Assert.AreEqual(HttpStatusCode.OK, (await privateHttp.GetAsync("/health")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await publicHttp.GetAsync(OAuthEndpoints.ProtectedResourceMetadataPath)).StatusCode);

        await using (var client = await ConnectAsync(host.PublicUrl, PublicEndpointHost.Pat, protocolVersion: null))
            Assert.IsNotEmpty(await client.ListToolsAsync(cancellationToken: Ct()));

        var clientId = await RegisterAsync(publicHttp);
        using var response = await publicHttp.GetAsync(AuthorizeUrl(clientId, NewPkce().Challenge, "s"));
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "not configured");
    }

    [TestMethod]
    public async Task Consent_denied_returns_access_denied_to_the_client()
    {
        using var http = _host.CreateHttpClient(_host.PublicUrl);
        var clientId = await RegisterAsync(http);

        var location = await SubmitConsentAsync(http, clientId, NewPkce().Challenge, "st8", decision: "deny");

        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.AreEqual("access_denied", query["error"].ToString());
        Assert.AreEqual("st8", query["state"].ToString());
        Assert.IsFalse(query.ContainsKey("code"));
    }

    [TestMethod]
    public async Task Consent_post_without_antiforgery_token_is_refused()
    {
        using var http = _host.CreateHttpClient(_host.PublicUrl);
        var clientId = await RegisterAsync(http);

        using var post = new HttpRequestMessage(HttpMethod.Post, OAuthEndpoints.AuthorizePath)
        {
            Content = new FormUrlEncodedContent(AuthorizeFields(clientId, NewPkce().Challenge, "s").Append(new("decision", "approve")))
        };
        post.Headers.Add(TestLoginHandler.Header, PublicEndpointHost.AllowedEmail);
        using var response = await http.SendAsync(post);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.IsNull(response.Headers.Location);
    }

    [TestMethod]
    [DataRow("stranger@example.com", false)]
    [DataRow(PublicEndpointHost.AllowedEmail, true)]
    public async Task Accounts_not_allowed_or_unverified_get_no_consent_page(string email, bool unverified)
    {
        using var http = _host.CreateHttpClient(_host.PublicUrl);
        var clientId = await RegisterAsync(http);

        using var request = new HttpRequestMessage(HttpMethod.Get, AuthorizeUrl(clientId, NewPkce().Challenge, "s"));
        request.Headers.Add(TestLoginHandler.Header, email);
        if (unverified)
            request.Headers.Add("X-Test-Unverified", "1");
        using var response = await http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "not allowed");
    }

    [TestMethod]
    [DataRow("https://evil.example/callback")]
    [DataRow("http://claude.ai/api/mcp/auth_callback")]
    [DataRow("https://claude.ai/api/mcp/auth_callback#frag")]
    public async Task Registration_refuses_redirect_uris_outside_the_allow_list(string redirectUri)
    {
        using var http = _host.CreateHttpClient(_host.PublicUrl);
        using var response = await http.PostAsJsonAsync(OAuthEndpoints.RegisterPath, new { redirect_uris = new[] { redirectUri }, client_name = "x" });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("invalid_redirect_uri", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task Registration_accepts_loopback_redirects_for_native_clients()
    {
        using var http = _host.CreateHttpClient(_host.PublicUrl);
        using var response = await http.PostAsJsonAsync(OAuthEndpoints.RegisterPath, new { redirect_uris = new[] { "http://localhost:53682/callback" } });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
    }

    [TestMethod]
    public async Task Authorize_with_unregistered_redirect_shows_an_error_instead_of_redirecting()
    {
        using var http = _host.CreateHttpClient(_host.PublicUrl);
        var clientId = await RegisterAsync(http);

        var url = QueryHelpers.AddQueryString(OAuthEndpoints.AuthorizePath, new Dictionary<string, string?>
        {
            ["response_type"] = "code", ["client_id"] = clientId, ["redirect_uri"] = "https://claude.com/api/mcp/auth_callback",
            ["code_challenge"] = NewPkce().Challenge, ["code_challenge_method"] = "S256", ["state"] = "s",
        });
        using var response = await http.GetAsync(url);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.IsNull(response.Headers.Location);
    }

    [TestMethod]
    public async Task Authorize_without_pkce_is_bounced_back_with_invalid_request()
    {
        using var http = _host.CreateHttpClient(_host.PublicUrl);
        var clientId = await RegisterAsync(http);

        var url = QueryHelpers.AddQueryString(OAuthEndpoints.AuthorizePath, new Dictionary<string, string?>
        {
            ["response_type"] = "code", ["client_id"] = clientId, ["redirect_uri"] = PublicEndpointHost.ClaudeCallback, ["state"] = "s",
        });
        using var response = await http.GetAsync(url);

        Assert.AreEqual(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.AreEqual("invalid_request", QueryHelpers.ParseQuery(response.Headers.Location!.Query)["error"].ToString());
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static CancellationToken Ct() => new CancellationTokenSource(Timeout).Token;

    private static async Task<McpClient> ConnectAsync(Uri endpoint, string? token, string? protocolVersion)
    {
        var headers = new Dictionary<string, string>();
        if (token is not null)
            headers["Authorization"] = $"Bearer {token}";

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            AdditionalHeaders = headers,
            TransportMode = HttpTransportMode.StreamableHttp,
        });
        return await McpClient.CreateAsync(transport,
            protocolVersion is null ? null : new McpClientOptions { ProtocolVersion = protocolVersion },
            cancellationToken: Ct());
    }

    private static async Task<string> RegisterAsync(HttpClient http)
    {
        using var response = await http.PostAsJsonAsync(OAuthEndpoints.RegisterPath,
            new { redirect_uris = new[] { PublicEndpointHost.ClaudeCallback }, client_name = "Claude", token_endpoint_auth_method = "none" });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
    }

    private static (string Verifier, string Challenge) NewPkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private static IEnumerable<KeyValuePair<string, string>> AuthorizeFields(string clientId, string challenge, string state) =>
    [
        new("response_type", "code"),
        new("client_id", clientId),
        new("redirect_uri", PublicEndpointHost.ClaudeCallback),
        new("code_challenge", challenge),
        new("code_challenge_method", "S256"),
        new("state", state),
        new("resource", PublicEndpointHost.BaseUrl + "/"),
        new("scope", "mcp"),
    ];

    private static string AuthorizeUrl(string clientId, string challenge, string state)
        => QueryHelpers.AddQueryString(OAuthEndpoints.AuthorizePath, AuthorizeFields(clientId, challenge, state).Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)));

    /// <summary>Loads the consent page as the allowed user and submits it; returns the redirect.</summary>
    private static async Task<Uri> SubmitConsentAsync(HttpClient http, string clientId, string challenge, string state, string decision)
    {
        using var get = new HttpRequestMessage(HttpMethod.Get, AuthorizeUrl(clientId, challenge, state));
        get.Headers.Add(TestLoginHandler.Header, PublicEndpointHost.AllowedEmail);
        using var page = await http.SendAsync(get);
        var html = await page.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
        StringAssert.Contains(html, "https://claude.ai");
        Assert.AreEqual("DENY", page.Headers.GetValues("X-Frame-Options").Single());

        var antiforgeryToken = AntiforgeryField().Match(html).Groups[1].Value;
        var antiforgeryCookie = page.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("mcpagg_af=")).Split(';')[0];

        using var post = new HttpRequestMessage(HttpMethod.Post, OAuthEndpoints.AuthorizePath)
        {
            Content = new FormUrlEncodedContent(AuthorizeFields(clientId, challenge, state)
                .Append(new("__af", antiforgeryToken))
                .Append(new("decision", decision)))
        };
        post.Headers.Add(TestLoginHandler.Header, PublicEndpointHost.AllowedEmail);
        post.Headers.Add("Cookie", antiforgeryCookie);
        using var response = await http.SendAsync(post);

        Assert.AreEqual(HttpStatusCode.SeeOther, response.StatusCode, await response.Content.ReadAsStringAsync());
        var location = response.Headers.Location!;
        Assert.IsTrue(location.ToString().StartsWith(PublicEndpointHost.ClaudeCallback + "?"), location.ToString());
        Assert.AreEqual(PublicEndpointHost.BaseUrl, QueryHelpers.ParseQuery(location.Query)["iss"].ToString());
        return location;
    }

    private static async Task<string> AuthorizeAsync(HttpClient http, string clientId, string challenge, string state)
    {
        var location = await SubmitConsentAsync(http, clientId, challenge, state, "approve");
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.AreEqual(state, query["state"].ToString());
        return query["code"].ToString();
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> TokenAsync(HttpClient http, Dictionary<string, string> form)
    {
        using var response = await http.PostAsync(OAuthEndpoints.TokenPath, new FormUrlEncodedContent(form));
        Assert.AreEqual("no-store", response.Headers.CacheControl?.ToString());
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    [GeneratedRegex("name=\"__af\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();
}
