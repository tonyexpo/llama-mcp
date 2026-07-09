using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace LlamaMcp;

public sealed class LlamaBackendClient(HttpClient httpClient, IOptions<BackendOptions> options)
{
    private readonly BackendOptions _options = options.Value;

    public string BaseUrl => _options.BaseUrl;

    public async Task<ChatCompletionResponseDto> ChatAsync(ChatCompletionRequestDto request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            request.Model = _options.DefaultModel;
        }

        // The OpenAI vision wire format needs "content" to be an array of
        // {type, text|image_url} parts instead of a plain string once any
        // image is attached. Only rebuild the payload when that's actually
        // the case, so the already-verified plain-text request path (a bare
        // PostAsJsonAsync(request)) is untouched for the common case.
        var hasImages = request.Messages.Any(m => m.ImageUrls is { Count: > 0 });

        using var response = hasImages
            ? await httpClient.PostAsJsonAsync("/v1/chat/completions", BuildMultimodalPayload(request), ct)
            : await httpClient.PostAsJsonAsync("/v1/chat/completions", request, ct);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<ChatCompletionResponseDto>(ct)
            ?? throw new InvalidOperationException("Backend returned an empty chat completion response.");
    }

    private static JsonObject BuildMultimodalPayload(ChatCompletionRequestDto request)
    {
        var payload = JsonSerializer.SerializeToNode(request)!.AsObject();

        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            if (message.ImageUrls is not { Count: > 0 } imageUrls)
            {
                messages.Add(new JsonObject { ["role"] = message.Role, ["content"] = message.Content });
                continue;
            }

            var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = message.Content } };
            foreach (var url in imageUrls)
            {
                parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } });
            }

            messages.Add(new JsonObject { ["role"] = message.Role, ["content"] = parts });
        }

        payload["messages"] = messages;
        return payload;
    }

    public async Task<List<string>> ListModelsAsync(CancellationToken ct)
    {
        using var response = await httpClient.GetAsync("/v1/models", ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ModelListResponseDto>(ct)
            ?? throw new InvalidOperationException("Backend returned an empty model list response.");

        return result.Data.Select(m => m.Id).ToList();
    }
}
