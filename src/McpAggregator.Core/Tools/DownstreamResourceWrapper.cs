using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using McpAggregator.Core.Exceptions;
using McpAggregator.Core.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tools;

/// <summary>
/// A first-class MCP resource on the aggregator that stands in for one downstream resource or
/// resource template (issue #45). Its URI is the downstream's rewritten to
/// <c>mcp-aggregator://{server}/{uri}</c> (see <see cref="ResourceUriNaming"/>); name, title, MIME
/// type, size, annotations and icons are carried through unchanged, the description is prefixed
/// <c>[server]</c>, and <c>_meta.mcpAggregator</c> identifies the downstream.
/// <para>
/// Subclassing <see cref="McpServerResource"/> directly means the SDK's own template binding never
/// runs: <c>resources/read</c> arrives with the raw aggregator URI, which is stripped back to the
/// downstream URI and forwarded through <see cref="ToolProxyHandler.ReadResourceAsync"/>, keeping
/// timeout, retry and telemetry shared with the <c>read_resource</c> escape hatch. Content URIs in
/// the result are rewritten back to aggregator form. Resources have no <c>isError</c> result, so
/// every failure here is a JSON-RPC error carrying a readable message.
/// </para>
/// </summary>
public sealed class DownstreamResourceWrapper : McpServerResource
{
    private const string MetaKey = "mcpAggregator";

    private readonly ToolProxyHandler _proxy;
    private readonly ILogger _logger;
    private readonly Regex? _matcher;

    /// <summary>Wraps a plain downstream resource.</summary>
    public DownstreamResourceWrapper(RegisteredServer server, Resource downstream, ToolProxyHandler proxy, ILogger logger)
        : this(server, NotNull(downstream).Uri, downstream.Name, downstream.Title, downstream.Description, downstream.MimeType,
            downstream.Annotations, downstream.Icons, downstream.Meta, downstream.Size,
            isTemplate: false, ComputeFingerprint(downstream), proxy, logger)
    {
    }

    /// <summary>Wraps a downstream resource template.</summary>
    public DownstreamResourceWrapper(RegisteredServer server, ResourceTemplate downstream, ToolProxyHandler proxy, ILogger logger)
        : this(server, NotNull(downstream).UriTemplate, downstream.Name, downstream.Title, downstream.Description, downstream.MimeType,
            downstream.Annotations, downstream.Icons, downstream.Meta, size: null,
            isTemplate: true, ComputeFingerprint(downstream), proxy, logger)
    {
    }

    private static T NotNull<T>(T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return value;
    }

    private DownstreamResourceWrapper(
        RegisteredServer server,
        string downstreamUri,
        string name,
        string? title,
        string? description,
        string? mimeType,
        Annotations? annotations,
        IList<Icon>? icons,
        JsonObject? downstreamMeta,
        long? size,
        bool isTemplate,
        string fingerprint,
        ToolProxyHandler proxy,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrEmpty(downstreamUri);

        _proxy = proxy;
        _logger = logger;

        ServerName = server.Name;
        ServerId = server.Id;
        DownstreamUri = downstreamUri;
        IsTemplate = isTemplate;
        Fingerprint = fingerprint;

        var meta = downstreamMeta is { } existing
            ? (JsonObject)JsonNode.Parse(existing.ToJsonString())!
            : new JsonObject();
        meta[MetaKey] = new JsonObject
        {
            ["serverId"] = server.Id,
            ["serverName"] = server.Name,
            ["uri"] = downstreamUri,
        };

        ProtocolResourceTemplate = new ResourceTemplate
        {
            UriTemplate = ResourceUriNaming.For(server.Name, downstreamUri),
            Name = name,
            Title = title,
            Description = BuildDescription(server, name, description),
            MimeType = mimeType,
            Annotations = annotations,
            Icons = icons,
            Meta = meta,
        };

        if (isTemplate)
        {
            ProtocolResource = null;
            _matcher = ResourceUriNaming.BuildTemplateMatcher(downstreamUri);
        }
        else
        {
            // The template form is what the SDK keys the collection by; the full resource (with
            // Size, which ResourceTemplate cannot carry) is what resources/list shows.
            ProtocolResource = new Resource
            {
                Uri = ProtocolResourceTemplate.UriTemplate,
                Name = name,
                Title = title,
                Description = ProtocolResourceTemplate.Description,
                MimeType = mimeType,
                Size = size,
                Annotations = annotations,
                Icons = icons,
                Meta = meta,
            };
        }
    }

    public override ResourceTemplate ProtocolResourceTemplate { get; }

    /// <summary>The rewritten resource for plain resources (keeps <c>size</c>); null for templates.</summary>
    public override Resource? ProtocolResource { get; }

    public override IReadOnlyList<object> Metadata { get; } = [];

    /// <summary>Registered name of the downstream server this wrapper routes to.</summary>
    public string ServerName { get; }

    /// <summary>Immutable id of the downstream server at the time the wrapper was built.</summary>
    public string? ServerId { get; }

    /// <summary>The downstream's own URI or URI template, exactly as it declared it.</summary>
    public string DownstreamUri { get; }

    /// <summary>True when this wrapper stands for a resource template.</summary>
    public bool IsTemplate { get; }

    /// <summary>
    /// Hash of the downstream resource (or template) as it came over the wire. The catalog reuses a
    /// wrapper instance when the URI and fingerprint are unchanged so the resource collection is not
    /// churned on every refresh.
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>The aggregator URI (or URI template) this wrapper answers to.</summary>
    public string Uri => ProtocolResourceTemplate.UriTemplate;

    /// <summary>
    /// True when <paramref name="uri"/> is this server's aggregator URI for the downstream resource
    /// (ordinal), or — for a template — an expansion of it. The SDK calls this after an exact
    /// collection lookup missed.
    /// </summary>
    public override bool IsMatch(string uri)
    {
        if (!ResourceUriNaming.IsFor(uri, ServerName, out var downstream))
            return false;

        return _matcher is null
            ? string.Equals(downstream, DownstreamUri, StringComparison.Ordinal)
            : _matcher.IsMatch(downstream);
    }

    public override async ValueTask<ReadResourceResult> ReadAsync(
        RequestContext<ReadResourceRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requested = request.Params?.Uri;
        if (!ResourceUriNaming.IsFor(requested, ServerName, out var downstreamUri))
        {
            throw new McpProtocolException(
                $"Resource '{requested}' does not belong to server '{ServerName}'. " +
                $"This resource is '{Uri}'; downstream resources are read as '{ResourceUriNaming.Prefix("{server}")}{{uri}}'.",
                McpErrorCode.InvalidParams);
        }

        try
        {
            var result = await _proxy.ReadResourceAsync(ServerName, downstreamUri, InvocationPath.Wrapper, cancellationToken);
            return ResourceUriNaming.RewriteContents(ServerName, result);
        }
        catch (AggregatorException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // "Server 'x' is unavailable." / "timed out after 30s" / the unknown-resource hint are
            // written for the caller. Rethrown as a protocol error so the message reaches the
            // client instead of the SDK's generic internal-error text.
            _logger.LogWarning(ex, "Resource '{Uri}' failed: {Message}", requested, ex.Message);
            throw new McpProtocolException(ex.Message, McpErrorCode.InternalError);
        }
    }

    public override string ToString() => Uri;

    private static string BuildDescription(RegisteredServer server, string name, string? description)
    {
        var text = string.IsNullOrWhiteSpace(description)
            ? $"Resource '{name}' on downstream MCP server '{server.Name}'."
            : description.Trim();
        return $"[{server.Name}] {text}";
    }

    internal static string ComputeFingerprint(Resource resource)
        => Hash("R" + JsonSerializer.Serialize(resource, McpJsonUtilities.DefaultOptions));

    internal static string ComputeFingerprint(ResourceTemplate template)
        => Hash("T" + JsonSerializer.Serialize(template, McpJsonUtilities.DefaultOptions));

    private static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }
}
