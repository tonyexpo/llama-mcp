using System.ComponentModel;
using System.Text.Json.Serialization;

namespace LlamaMcp;

// Minimal DTOs for the OpenAI-compatible subset exposed by llama-server and LM Studio.

public sealed class ChatMessageDto
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("imageUrls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Description("Images to attach to this message, for vision-capable models (e.g. Qwen-VL). Each entry is either an http(s) URL or a data: URI (data:image/png;base64,...).")]
    public List<string>? ImageUrls { get; set; }
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

    [JsonPropertyName("chat_template_kwargs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatTemplateKwargsDto? ChatTemplateKwargs { get; set; }
}

// Passthrough for backend-specific chat-template options (e.g. Qwen3.x's
// reasoning toggle). Only enable_thinking is exposed for now -- extend here
// if another concrete kwarg need shows up, not preemptively.
public sealed class ChatTemplateKwargsDto
{
    [JsonPropertyName("enable_thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableThinking { get; set; }
}

public sealed class ChatCompletionResponseDto
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("choices")]
    public List<ChatChoiceDto> Choices { get; set; } = [];

    [JsonPropertyName("usage")]
    public ChatUsageDto? Usage { get; set; }
}

// The OpenAI-compatible "usage" object -- token counts the backend already
// computes but was previously discarded on the way back to the caller.
public sealed class ChatUsageDto
{
    [JsonPropertyName("prompt_tokens")]
    public int? PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int? CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int? TotalTokens { get; set; }
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
