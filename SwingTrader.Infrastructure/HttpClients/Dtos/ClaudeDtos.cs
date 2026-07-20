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
    [property: JsonPropertyName("messages")] List<ClaudeMessage> Messages,
    // Adaptive-thinking control. Null = omit (model default). Structured
    // JSON-extraction calls pass Disabled: 20 Jul 2026, Sonnet 5's adaptive
    // thinking consumed the ENTIRE max_tokens budget on thinking blocks and
    // returned no text at all, whatever the budget.
    [property: JsonPropertyName("thinking"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClaudeThinking? Thinking = null
);

public record ClaudeThinking([property: JsonPropertyName("type")] string Type)
{
    public static readonly ClaudeThinking Disabled = new("disabled");
}

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
    [property: JsonPropertyName("key_factors")] List<string> KeyFactors,
    // Optional so responses from the pre-catalyst prompt (and any Claude
    // reply that omits the block) still deserialize - null = none detected.
    [property: JsonPropertyName("catalyst")] ClaudeCatalystResult? Catalyst = null
);

// A DATED, forward-looking event Claude spotted in the same news articles the
// sentiment score came from - guidance raises, product launches, FDA
// decisions, contract wins. Earnings dates are deliberately excluded by the
// prompt (the earnings gate owns those).
public record ClaudeCatalystResult(
    [property: JsonPropertyName("detected")] bool Detected,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("expected_date")] string? ExpectedDate,
    [property: JsonPropertyName("direction")] string? Direction, // "bullish" | "bearish"
    [property: JsonPropertyName("strength")] float Strength      // 0.0 - 1.0
);
