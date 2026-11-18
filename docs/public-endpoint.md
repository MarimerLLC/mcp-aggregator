# Public endpoint (claude.ai connectors, cloud clients)

The aggregator normally lives on a tailnet. Claude on the web and mobile (custom connectors), Claude
Code cloud sessions and other hosted clients reach MCP servers from Anthropic's cloud, not from your
machine, so they need an internet-reachable URL. The public endpoint provides one without exposing
anything else.

## Shape

```
                         tailnet only                              internet (Tailscale Funnel)
 Claude Desktop / Code ──▶ mcp-aggregator.<tailnet>.ts.net         mcp-aggregator-public.<tailnet>.ts.net ◀── claude.ai, cloud clients
                           ingress.yaml → service :80 → pod :8080   ingress-public.yaml → service :8081 → pod :8081
                           MCP (no auth), REST, admin, Scalar       MCP (bearer token required) + OAuth server only
```

- **Two Kestrel listeners in one process.** `PublicEndpointMiddleware` tells them apart by the TCP port
  the request arrived on, never by a header. The private listener is unchanged.
- **Public listener serves only** `/` (MCP, token required), `/.well-known/oauth-protected-resource`,
  `/.well-known/oauth-authorization-server` and `/oauth/*`. The REST API, OpenAPI, Scalar and `/health`
  return 404 there. The OAuth paths return 404 on the private listener.
- **No administrative tools publicly.** Callers authenticated on the public listener carry the claim
  `mcp-aggregator/endpoint = public`. `PublicCallerAdminFilter` (Core) removes the admin tools and
  `show_admin_tools` from `tools/list` and rejects them in `tools/call`, in both Lazy and Eager modes.
  This matters because `register_server` can start a stdio process inside the pod.
- **Two credential types**, both sent as `Authorization: Bearer …`:
  - **OAuth access tokens** come from the built-in authorization server. That's how claude.ai custom
    connectors authenticate. You sign in with Google, the account must be in `AllowedEmails`, and you
    approve the connector on a consent page.
  - **Personal access tokens (PATs)** are for clients that can send a header, such as Claude Code,
    scripts and agents.

### The authorization server

This is a small, single-user OAuth 2.1 server implementing just what MCP authorization needs:

| Piece | Spec | Notes |
|---|---|---|
| Protected resource metadata | RFC 9728 | The 401 challenge points here; this is how claude.ai finds the AS |
| AS metadata | RFC 8414 | issuer = `BaseUrl` |
| Dynamic client registration | RFC 7591 | Public clients only (`token_endpoint_auth_method: none`). Redirect URIs must be in `AllowedRedirectUris` (defaults: claude.ai and claude.com callbacks) or loopback (`AllowLoopbackRedirects`) |
| Authorization code + PKCE | OAuth 2.1 | S256 is mandatory. Codes are single-use, last 2 minutes and are held in memory |
| Refresh tokens | | Rotated on use, 30 days by default |
| `iss` in the authorization response | RFC 9207 | |

It stores nothing per token. Client registrations, access tokens and refresh tokens are ASP.NET Data
Protection payloads, each protected under its own purpose so one kind can't be replayed as another.
The keys are persisted in `{DataDirectory}/keys`, so tokens survive a restart. Every token is bound to
`BaseUrl` and to `TokenGeneration`. On every use the token's account is checked against
`AllowedEmails` again.

The Google step uses the stock ASP.NET Core Google handler. The aggregator never keeps Google tokens.
It reads the verified email once, puts it in a 15-minute login cookie scoped to `/oauth`, and drops
that cookie once you approve or deny.

## Setting it up

### 1. Allow Funnel for the operator's proxies

The Funnel ingress's proxy device must carry a tag the tailnet ACL permits for the operator, and that
tag needs the `funnel` attribute. `k8s/ingress-public.yaml` pins the proxy's tag with
`tailscale.com/tags: tag:k8s-operator`. Without that annotation the operator falls back to its
`PROXY_TAGS` default, `tag:k8s`, which an ACL that doesn't grant `tag:k8s` rejects. The proxy then
never comes up, and the reason only appears in the operator log. Use whatever tag your existing
Tailscale ingresses carry, and grant it Funnel in the tailnet policy file:

```json
"nodeAttrs": [{ "target": ["tag:k8s-operator"], "attr": ["funnel"] }]
```

### 2. Create a Google OAuth client

In Google Cloud Console, go to APIs & Services → Credentials → Create credentials → OAuth client ID:

- Application type: **Web application**
- Authorized redirect URI: `https://mcp-aggregator-public.<tailnet>.ts.net/oauth/google/callback`
- On the OAuth consent screen, scopes `openid`, `email` and `profile` are enough. "Testing" publishing
  status works if your own account is listed as a test user.

### 3. Mint a personal access token (optional)

```bash
dotnet run --project src/McpAggregator.HttpServer -- new-pat --name claude-code-cloud
# or, from the image:
docker run --rm rockylhotka/mcp-aggregator:latest new-pat --name claude-code-cloud
```

The command prints the token once, plus the `NAME` / `SHA256` lines for configuration. Only the hash is
stored. For a second token, use `--index 1`, and so on.

### 4. Create the secret

```bash
kubectl create secret generic mcp-aggregator-public -n mcp-aggregator \
  --from-literal=MCPAGGREGATOR__PUBLICENDPOINT__ALLOWEDEMAILS__0='you@example.com' \
  --from-literal=MCPAGGREGATOR__PUBLICENDPOINT__GOOGLE__CLIENTID='….apps.googleusercontent.com' \
  --from-literal=MCPAGGREGATOR__PUBLICENDPOINT__GOOGLE__CLIENTSECRET='…' \
  --from-literal=MCPAGGREGATOR__PUBLICENDPOINT__PERSONALACCESSTOKENS__0__NAME='claude-code-cloud' \
  --from-literal=MCPAGGREGATOR__PUBLICENDPOINT__PERSONALACCESSTOKENS__0__SHA256='…'
```

`k8s/configmap.yaml` already enables the endpoint (`ENABLED`, `LISTENURL`, `BASEURL`). Without the
secret, the public listener rejects every request. Without the Google values it still accepts PATs,
and the authorize page says sign-in is not configured.

### 5. Deploy

If a GitOps tool (Fleet, Argo CD, Flux) owns the deployment, make the same changes there and don't run
`deploy.sh`. The changes are: port 8081 on the Deployment and Service, the optional
`mcp-aggregator-public` secretRef, the three `PUBLICENDPOINT` ConfigMap keys, and the public Ingress.
Applying the manifests by hand would leave the GitOps copy out of sync. Otherwise:

```bash
./k8s/deploy.sh          # applies ingress-public.yaml too
kubectl rollout restart deployment/mcp-aggregator -n mcp-aggregator   # after changing the secret
curl -i -X POST https://mcp-aggregator-public.<tailnet>.ts.net/         # expect 401 + WWW-Authenticate
```

## Connecting clients

**claude.ai (web, desktop app, mobile):** go to Settings → Connectors → Add custom connector and use
the URL `https://mcp-aggregator-public.<tailnet>.ts.net`. Leave the advanced OAuth fields empty, because
the client registers itself. Click *Connect*, sign in with Google, then click *Approve*.

**Claude Code (local or cloud) with a PAT:** in `.mcp.json`:

```json
{
  "mcpServers": {
    "mcp-aggregator": {
      "type": "http",
      "url": "https://mcp-aggregator-public.<tailnet>.ts.net",
      "headers": { "Authorization": "Bearer ${MCP_AGGREGATOR_PAT}" }
    }
  }
}
```

Set `MCP_AGGREGATOR_PAT` in the environment. For cloud sessions, that's the environment's settings.
Claude Code can also use the OAuth flow instead (`/mcp` → authenticate). It registers a loopback
redirect, which is allowed by default.

## Configuration reference

Section `McpAggregator:PublicEndpoint` (env `MCPAGGREGATOR__PUBLICENDPOINT__*`):

| Key | Default | |
|---|---|---|
| `Enabled` | `false` | |
| `ListenUrl` | `http://+:8081` | Must not share a port with the private listener |
| `BaseUrl` | | External https origin without a path. It is the issuer, the resource and the Google redirect base |
| `AllowedEmails` | `[]` | Google accounts that may authorize. If empty, nobody can |
| `Google:ClientId` / `Google:ClientSecret` | | |
| `PersonalAccessTokens[]` | `[]` | `Name`, `Sha256` (hex), optional `ExpiresAt` |
| `AllowedRedirectUris` | claude.ai and claude.com callbacks | Configured entries are added to the defaults |
| `AllowLoopbackRedirects` | `true` | `http://localhost:*`, `127.0.0.1`, `[::1]` |
| `AccessTokenLifetime` / `RefreshTokenLifetime` / `AuthorizationCodeLifetime` | 1 h / 30 d / 2 min | |
| `TokenGeneration` | `1` | Bump it to revoke every OAuth token |

## Revoking access

- **Every OAuth token at once:** increment `TokenGeneration` and restart.
- **One person:** remove the email from `AllowedEmails`. Their tokens stop working on their next use.
- **One PAT:** remove its entry, or set `ExpiresAt`.
- **Everything, including client registrations:** delete `{DataDirectory}/keys` and restart.

## Limits and trade-offs

- **Single replica.** Authorization codes live in memory. That matches the deployment (`replicas: 1`,
  `Recreate`). Running several replicas would need shared codes and shared Data Protection keys.
- **Refresh token rotation has no reuse detection.** A stolen refresh token stays valid until it
  expires or `TokenGeneration` is bumped.
- **Keys at rest.** The Data Protection keys are unencrypted on the PVC. Anyone who can read the PVC
  can mint tokens, but they could already read `registry.json` and its downstream credentials.
- **No rate limiting.** Codes and tokens are 256-bit random or authenticated-encrypted values, and
  registration is stateless, so the endpoints have nothing to fill up or guess.
