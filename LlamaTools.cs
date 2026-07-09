using System.ComponentModel;
using ModelContextProtocol.Server;

namespace LlamaMcp;

[McpServerToolType]
public sealed class LlamaTools(LlamaBackendClient backend)
{
    [McpServerTool(Name = "chat"), Description("Send a chat completion request to the local llama.cpp/LM Studio backend and return the assistant's reply.")]
    public async Task<ChatToolResult> Chat(
        [Description("Conversation messages, OpenAI-style, in order (system/user/assistant). Attach imageUrls to a message for vision-capable models.")] List<ChatMessageDto> messages,
        [Description("Model name. Omit to use the server-configured default model.")] string? model = null,
        [Description("Sampling temperature. Omit to use the backend default.")] double? temperature = null,
        [Description("Maximum tokens to generate. Omit to use the backend default.")] int? maxTokens = null,
        [Description("Nucleus sampling top-p. Omit to use the backend default.")] double? topP = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ChatCompletionRequestDto
        {
            Model = model ?? "",
            Messages = messages,
            Temperature = temperature,
            MaxTokens = maxTokens,
            TopP = topP,
        };

        var response = await backend.ChatAsync(request, cancellationToken);
        var choice = response.Choices.FirstOrDefault();

        return new ChatToolResult
        {
            Content = choice?.Message.Content ?? "",
            Model = response.Model ?? request.Model,
            FinishReason = choice?.FinishReason,
        };
    }

    [McpServerTool(Name = "list_models"), Description("List the models currently available on the local llama.cpp/LM Studio backend.")]
    public Task<List<string>> ListModels(CancellationToken cancellationToken = default)
        => backend.ListModelsAsync(cancellationToken);

    [McpServerTool(Name = "health"), Description("Check whether the local llama.cpp/LM Studio backend is reachable, without spending a chat generation call.")]
    public async Task<HealthToolResult> Health(CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await backend.ListModelsAsync(cancellationToken);
            return new HealthToolResult { Reachable = true, BaseUrl = backend.BaseUrl, Models = models };
        }
        catch (Exception ex)
        {
            return new HealthToolResult { Reachable = false, BaseUrl = backend.BaseUrl, Error = ex.Message };
        }
    }
}

public sealed class ChatToolResult
{
    public string Content { get; set; } = "";
    public string Model { get; set; } = "";
    public string? FinishReason { get; set; }
}

public sealed class HealthToolResult
{
    public bool Reachable { get; set; }
    public string BaseUrl { get; set; } = "";
    public List<string>? Models { get; set; }
    public string? Error { get; set; }
}
