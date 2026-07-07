using System.ComponentModel;
using ModelContextProtocol.Server;

namespace LlamaMcp;

[McpServerToolType]
public sealed class LlamaTools(LlamaBackendClient backend)
{
    [McpServerTool(Name = "chat"), Description("Send a chat completion request to the local llama.cpp/LM Studio backend and return the assistant's reply.")]
    public async Task<ChatToolResult> Chat(
        [Description("Conversation messages, OpenAI-style, in order (system/user/assistant).")] List<ChatMessageDto> messages,
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
}

public sealed class ChatToolResult
{
    public string Content { get; set; } = "";
    public string Model { get; set; } = "";
    public string? FinishReason { get; set; }
}
