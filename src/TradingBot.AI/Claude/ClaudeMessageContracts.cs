using System.Text.Json.Serialization;

namespace TradingBot.AI.Claude;

// ── Request shapes ─────────────────────────────────────────────────────────
//
// Mirrors the documented Anthropic Messages API surface area we use. We hand-
// roll these instead of depending on a community SDK so we control the wire
// format and can pin the cache_control marker exactly where §5.5 wants it
// (on the system block).

internal sealed class ClaudeMessageRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("max_tokens")]
    public required int MaxTokens { get; init; }

    /// <summary>Either an array of <see cref="ClaudeSystemBlock"/> (when we
    /// want to stamp <c>cache_control</c>) or a plain string. We always send
    /// the array form for symmetry — the API accepts both.</summary>
    [JsonPropertyName("system")]
    public required ClaudeSystemBlock[] System { get; init; }

    [JsonPropertyName("messages")]
    public required ClaudeMessage[] Messages { get; init; }
}

internal sealed class ClaudeSystemBlock
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("cache_control")]
    public ClaudeCacheControl? CacheControl { get; init; }
}

internal sealed class ClaudeCacheControl
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "ephemeral";
}

internal sealed class ClaudeMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }
}

// ── Response shapes ────────────────────────────────────────────────────────

internal sealed class ClaudeMessageResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("content")]
    public ClaudeContentBlock[]? Content { get; init; }

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; init; }

    [JsonPropertyName("usage")]
    public ClaudeUsage? Usage { get; init; }
}

internal sealed class ClaudeContentBlock
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

internal sealed class ClaudeUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; init; }

    [JsonPropertyName("cache_read_input_tokens")]
    public int? CacheReadInputTokens { get; init; }

    [JsonPropertyName("cache_creation_input_tokens")]
    public int? CacheCreationInputTokens { get; init; }
}

// ── Batches API shapes ─────────────────────────────────────────────────────

internal sealed class ClaudeBatchCreateRequest
{
    [JsonPropertyName("requests")]
    public required ClaudeBatchRequestItem[] Requests { get; init; }
}

internal sealed class ClaudeBatchRequestItem
{
    [JsonPropertyName("custom_id")]
    public required string CustomId { get; init; }

    [JsonPropertyName("params")]
    public required ClaudeMessageRequest Params { get; init; }
}

internal sealed class ClaudeBatchResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("processing_status")]
    public string? ProcessingStatus { get; init; }

    [JsonPropertyName("results_url")]
    public string? ResultsUrl { get; init; }
}

internal sealed class ClaudeBatchResultLine
{
    [JsonPropertyName("custom_id")]
    public string? CustomId { get; init; }

    [JsonPropertyName("result")]
    public ClaudeBatchResult? Result { get; init; }
}

internal sealed class ClaudeBatchResult
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }   // "succeeded" | "errored" | "canceled" | "expired"

    [JsonPropertyName("message")]
    public ClaudeMessageResponse? Message { get; init; }
}
