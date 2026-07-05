using System.Text.Json.Serialization;

namespace SwingTrader.Infrastructure.HttpClients.Dtos;

public record ClaudeMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content
);

public record ClaudeRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("messages")] List<ClaudeMessage> Messages
);

public record ClaudeContentBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text
);

public record ClaudeUsage(
    [property: JsonPropertyName("input_tokens")] int InputTokens,
    [property: JsonPropertyName("output_tokens")] int OutputTokens
);

public record ClaudeResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] List<ClaudeContentBlock> Content,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("stop_reason")] string StopReason,
    [property: JsonPropertyName("usage")] ClaudeUsage Usage
);

public record ClaudeSentimentResult(
    [property: JsonPropertyName("sentiment_score")] float SentimentScore,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("key_factors")] List<string> KeyFactors
);
