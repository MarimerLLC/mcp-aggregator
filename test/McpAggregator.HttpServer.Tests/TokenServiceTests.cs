using McpAggregator.HttpServer.PublicEndpoint;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace McpAggregator.HttpServer.Tests;

[TestClass]
public sealed class TokenServiceTests
{
    private const string Base = "https://public.test";

    private static (TokenService Tokens, PublicEndpointOptions Options, FakeTimeProvider Time) Create()
    {
        var options = new PublicEndpointOptions { Enabled = true, BaseUrl = Base, AllowedEmails = ["owner@example.com"] };
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-18T12:00:00Z"));
        var tokens = new TokenService(new EphemeralDataProtectionProvider(), new StaticMonitor(options), time);
        return (tokens, options, time);
    }

    [TestMethod]
    public void Pkce_matches_rfc7636_appendix_b()
    {
        Assert.IsTrue(TokenService.VerifyPkce("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
        Assert.IsFalse(TokenService.VerifyPkce("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXl", "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
        Assert.IsFalse(TokenService.VerifyPkce("short", "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
        Assert.IsFalse(TokenService.VerifyPkce(null, "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
    }

    [TestMethod]
    public void Access_token_expires()
    {
        var (tokens, options, time) = Create();
        var (access, _, _) = tokens.IssueTokens("owner@example.com", "mcpagg_client_x", "mcp");

        Assert.IsTrue(tokens.TryValidateAccessToken(access, out _));
        time.Advance(options.AccessTokenLifetime + TimeSpan.FromSeconds(1));
        Assert.IsFalse(tokens.TryValidateAccessToken(access, out _));
    }

    [TestMethod]
    public void Bumping_token_generation_revokes_everything()
    {
        var (tokens, options, _) = Create();
        var (access, refresh, _) = tokens.IssueTokens("owner@example.com", "mcpagg_client_x", "mcp");

        options.TokenGeneration++;

        Assert.IsFalse(tokens.TryValidateAccessToken(access, out _));
        Assert.IsFalse(tokens.TryValidateRefreshToken(refresh, out _));
    }

    [TestMethod]
    public void Removing_the_email_revokes_its_tokens()
    {
        var (tokens, options, _) = Create();
        var (access, refresh, _) = tokens.IssueTokens("owner@example.com", "mcpagg_client_x", "mcp");

        options.AllowedEmails.Clear();

        Assert.IsFalse(tokens.TryValidateAccessToken(access, out _));
        Assert.IsFalse(tokens.TryValidateRefreshToken(refresh, out _));
    }

    [TestMethod]
    public void Tokens_are_bound_to_the_base_url()
    {
        var (tokens, options, _) = Create();
        var (access, _, _) = tokens.IssueTokens("owner@example.com", "mcpagg_client_x", "mcp");

        options.BaseUrl = "https://other.test";

        Assert.IsFalse(tokens.TryValidateAccessToken(access, out _));
    }

    [TestMethod]
    public void Token_kinds_are_not_interchangeable()
    {
        var (tokens, _, _) = Create();
        var (access, refresh, _) = tokens.IssueTokens("owner@example.com", "mcpagg_client_x", "mcp");
        var clientId = tokens.CreateClientId(new ClientRegistration(["https://claude.ai/api/mcp/auth_callback"], "c", 0));

        // Swap the prefixes: the payloads are protected under different purposes.
        Assert.IsFalse(tokens.TryValidateAccessToken(TokenService.AccessTokenPrefix + refresh[TokenService.RefreshTokenPrefix.Length..], out _));
        Assert.IsFalse(tokens.TryValidateRefreshToken(TokenService.RefreshTokenPrefix + access[TokenService.AccessTokenPrefix.Length..], out _));
        Assert.IsFalse(tokens.TryReadClient(TokenService.ClientIdPrefix + access[TokenService.AccessTokenPrefix.Length..], out _));
        Assert.IsTrue(tokens.TryReadClient(clientId, out var client));
        Assert.AreEqual("c", client.ClientName);
    }

    [TestMethod]
    public void Personal_access_tokens_match_by_hash_and_expire()
    {
        var (tokens, options, time) = Create();
        var pat = TokenService.NewPersonalAccessToken();
        options.PersonalAccessTokens.Add(new PersonalAccessTokenEntry { Name = "other", Sha256 = TokenService.HashPersonalAccessToken(TokenService.NewPersonalAccessToken()) });
        options.PersonalAccessTokens.Add(new PersonalAccessTokenEntry { Name = "mine", Sha256 = TokenService.HashPersonalAccessToken(pat), ExpiresAt = time.GetUtcNow().AddDays(1) });
        options.PersonalAccessTokens.Add(new PersonalAccessTokenEntry { Name = "broken", Sha256 = "not hex" });

        Assert.AreEqual("mine", tokens.ValidatePersonalAccessToken(pat)?.Name);
        Assert.IsNull(tokens.ValidatePersonalAccessToken(pat + "x"));
        Assert.IsNull(tokens.ValidatePersonalAccessToken(TokenService.NewPersonalAccessToken()));

        time.Advance(TimeSpan.FromDays(2));
        Assert.IsNull(tokens.ValidatePersonalAccessToken(pat));
    }

    [TestMethod]
    [DataRow("https://claude.ai/api/mcp/auth_callback", true)]
    [DataRow("https://claude.com/api/mcp/auth_callback", true)]
    [DataRow("http://localhost:4567/callback", true)]
    [DataRow("http://127.0.0.1:4567/oauth/callback", true)]
    [DataRow("https://localhost:4567/callback", false)]
    [DataRow("http://localhost.evil.example/callback", false)]
    [DataRow("http://user@localhost:4567/callback", false)]
    [DataRow("https://claude.ai/api/mcp/auth_callback/../x", false)]
    [DataRow("javascript:alert(1)", false)]
    [DataRow("", false)]
    public void Redirect_uri_allow_list(string uri, bool allowed)
    {
        var (tokens, _, _) = Create();
        Assert.AreEqual(allowed, tokens.IsAllowedRedirectUri(uri), uri);
    }

    private sealed class StaticMonitor(PublicEndpointOptions value) : IOptionsMonitor<PublicEndpointOptions>
    {
        public PublicEndpointOptions CurrentValue => value;
        public PublicEndpointOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<PublicEndpointOptions, string?> listener) => null;
    }
}
