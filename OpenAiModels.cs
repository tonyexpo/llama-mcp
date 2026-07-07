using System.Text.Json.Serialization;

namespace LlamaMcp;

// Minimal DTOs for the OpenAI-compatible subset exposed by llama-server and LM Studio.

public sealed class ChatMessageDto
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

public sealed class ChatCompletionRequestDto
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<ChatMessageDto> Messages { get; set; } = [];

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; set; }
}

public sealed class ChatCompletionResponseDto
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("choices")]
    public List<ChatChoiceDto> Choices { get; set; } = [];
}

public sealed class ChatChoiceDto
{
    [JsonPropertyName("message")]
    public ChatMessageDto Message { get; set; } = new();

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public sealed class ModelListResponseDto
{
    [JsonPropertyName("data")]
    public List<ModelDto> Data { get; set; } = [];
}

public sealed class ModelDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
}
