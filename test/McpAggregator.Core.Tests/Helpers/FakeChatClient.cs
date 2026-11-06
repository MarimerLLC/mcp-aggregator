using Microsoft.Extensions.AI;

namespace McpAggregator.Core.Tests.Helpers;

/// <summary>A model double: answers every request with <see cref="Respond"/> and records what it was sent.</summary>
internal sealed class FakeChatClient : IChatClient
{
    public Func<IReadOnlyList<ChatMessage>, string> Respond { get; set; } = _ => string.Empty;

    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public ChatOptions? LastOptions { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Requests.Add(list);
        LastOptions = options;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Respond(list))));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
