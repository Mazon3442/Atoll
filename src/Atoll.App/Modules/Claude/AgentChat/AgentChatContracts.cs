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

    IAsyncEnumerable<AgentChatDelta> StreamAsync(string prompt, CancellationToken ct);
}

/// <summary>A settings snapshot per question, so a changed model or switch applies to the next run.</summary>
public sealed record AgentChatOptions(AgentChatSettings Settings, IReadOnlyList<ProviderAccount> Accounts);

/// <summary>A short, user-facing failure. It carries no keys and never repeats the prompt.</summary>
public sealed class AgentChatException(string message) : Exception(message);
