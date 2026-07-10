using System.ComponentModel;
using ModelContextProtocol;
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
        [Description("Disable the model's internal reasoning/\"thinking\" step before its final answer (maps to chat_template_kwargs.enable_thinking on backends that support it, e.g. Qwen3.x). Omit to use the model's own default -- reasoning has a real hidden token cost, so turn it off for simple tasks like a straight translation and leave it on for tasks that benefit from it.")] bool? enableThinking = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ChatCompletionRequestDto
        {
            Model = model ?? "",
            Messages = messages,
            Temperature = temperature,
            MaxTokens = maxTokens,
            TopP = topP,
            ChatTemplateKwargs = enableThinking is null ? null : new ChatTemplateKwargsDto { EnableThinking = enableThinking },
        };

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = ReportHeartbeatAsync(progress, heartbeatCts.Token);

        ChatCompletionResponseDto response;
        try
        {
            response = await backend.ChatAsync(request, cancellationToken);
        }
        finally
        {
            // Stop the heartbeat as soon as the real call finishes (success or
            // failure) rather than waiting for its next 15s tick.
            heartbeatCts.Cancel();
            await heartbeatTask;
        }

        var choice = response.Choices.FirstOrDefault();

        return new ChatToolResult
        {
            Content = choice?.Message.Content ?? "",
            Model = response.Model ?? request.Model,
            FinishReason = choice?.FinishReason,
        };
    }

    // Mitigates perceived dead-air on a single long-running chat call. Safe
    // no-op when the caller didn't supply a progress token (the SDK binds a
    // NullProgress instance in that case) -- does not prevent a client-side
    // hard timeout, which MCP progress notifications aren't guaranteed to do.
    private static async Task ReportHeartbeatAsync(IProgress<ProgressNotificationValue>? progress, CancellationToken ct)
    {
        if (progress is null)
        {
            return;
        }

        try
        {
            var elapsedSeconds = 0;
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                elapsedSeconds += 15;
                progress.Report(new ProgressNotificationValue { Progress = elapsedSeconds, Message = $"Still generating... ({elapsedSeconds}s elapsed)" });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected once the chat call completes.
        }
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
