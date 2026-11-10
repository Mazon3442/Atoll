using Atoll.Settings;

namespace Atoll.Modules.Claude;

/// <summary>Where an answer comes from, in the order the service tries them.</summary>
public enum AgentChatKind
{
    ClaudeCode,
    Codex,
    OpenCode,
    Api,
}

/// <summary>A picture sent with a question: PNG or JPEG bytes, already shrunk to a size a model will take.</summary>
public sealed record AgentChatImage(byte[] Data, string MediaType);

/// <summary>One piece of a streamed answer. Failures arrive as a single final delta with readable text.</summary>
public readonly record struct AgentChatDelta(string Text, bool IsError = false);

/// <summary>
/// One installed agent or API key that can answer.
/// <see cref="Note"/> is what the UI must admit about tool use; it never claims chat-only when the CLI can't be restricted.
/// </summary>
public sealed record AgentChatSelection(IAgentChatProvider Provider, string Note);

public interface IAgentChatProvider
{
    string Name { get; }

    AgentChatKind Kind { get; }

    IAsyncEnumerable<AgentChatDelta> StreamAsync(string prompt, CancellationToken ct, IReadOnlyList<AgentChatImage>? images = null);
}

/// <summary>A settings snapshot per question, so a changed model or switch applies to the next run.</summary>
public sealed record AgentChatOptions(AgentChatSettings Settings, IReadOnlyList<ProviderAccount> Accounts);

/// <summary>
/// What the Ask box needs from a chat service. Small on purpose: the view model can then be exercised with a
/// stub, without probing for installed agents or touching the network.
/// </summary>
public interface IAgentChat
{
    Task<AgentChatSelection> ResolveAsync(AgentChatOptions options, CancellationToken ct = default);

    IAsyncEnumerable<AgentChatDelta> StreamAsync(AgentChatSelection selection, string prompt,
        AgentChatSettings settings, CancellationToken ct = default, IReadOnlyList<AgentChatImage>? images = null);
}

/// <summary>A short, user-facing failure. It carries no keys and never repeats the prompt.</summary>
public sealed class AgentChatException(string message) : Exception(message);
