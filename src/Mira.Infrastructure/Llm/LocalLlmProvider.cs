using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mira.Core.Interfaces;
using Mira.Infrastructure.Configuration;

namespace Mira.Infrastructure.Llm;

internal sealed class LocalLlmProvider : ILlmProvider
{
    private readonly HttpClient _httpClient;
    private readonly LocalLlmSettings _settings;
    private readonly ILogger<LocalLlmProvider> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public LocalLlmProvider(
        HttpClient httpClient,
        IOptions<LocalLlmSettings> settings,
        ILogger<LocalLlmProvider> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;

        _httpClient.BaseAddress = new Uri(_settings.BaseUrl);
    }

    public async Task<string> GenerateResponseAsync(
        string systemPrompt,
        string userMessage,
        CancellationToken ct)
    {
        var request = new ChatCompletionRequest
        (
            _settings.Model,
            [
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", userMessage)
            ]
        );

        _logger.LogDebug("Sending request to {BaseUrl} with model {Model}", _settings.BaseUrl, _settings.Model);

        var response = await _httpClient.PostAsJsonAsync(
            "/v1/chat/completions", request, JsonOptions, ct);

        response.EnsureSuccessStatusCode();

        var completion = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("LLM returned null response");

        return completion.Choices[0].Message.Content;
    }

    private sealed record ChatCompletionRequest(string Model, List<ChatMessage> Messages);

    private sealed record ChatMessage(string Role, string Content);

    private sealed record ChatCompletionResponse(List<ChatChoice> Choices);

    private sealed record ChatChoice(ChatMessage Message);
}
