using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace LlamaMcp;

public sealed class LlamaBackendClient(HttpClient httpClient, IOptions<BackendOptions> options)
{
    private readonly BackendOptions _options = options.Value;

    public async Task<ChatCompletionResponseDto> ChatAsync(ChatCompletionRequestDto request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            request.Model = _options.DefaultModel;
        }

        using var response = await httpClient.PostAsJsonAsync("/v1/chat/completions", request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<ChatCompletionResponseDto>(ct)
            ?? throw new InvalidOperationException("Backend returned an empty chat completion response.");
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
