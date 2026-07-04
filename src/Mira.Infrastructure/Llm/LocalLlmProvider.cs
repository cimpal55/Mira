namespace Mira.Infrastructure.Llm;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Mira.Core.Interfaces;
using Mira.Core.Models;
using Mira.Infrastructure.Configuration;

public sealed class LocalLlmProvider(IHttpClientFactory httpClientFactory, IOptions<LocalLlmSettings> options) : ILlmProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly LocalLlmSettings _settings = options.Value;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var endpoint = BuildEndpoint(_settings.BaseUrl);
        var payload = BuildRequest(request, includeJsonResponseFormat: request.RequireJson);
        var response = await SendAsync(endpoint, payload, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.BadRequest && request.RequireJson)
        {
            response.Dispose();
            payload = BuildRequest(request, includeJsonResponseFormat: false);
            response = await SendAsync(endpoint, payload, cancellationToken).ConfigureAwait(false);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Local LLM backend at {endpoint} returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
            }

            var content = await ReadResponseContentAsync(response, cancellationToken).ConfigureAwait(false);
            return new LlmResponse(content);
        }
    }

    private static async Task<string> ReadResponseContentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ChatCompletionResponse? parsed;
        try
        {
            parsed = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Local LLM returned an invalid chat completion response.", ex);
        }

        var content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Local LLM returned an empty response.");
        }

        return content;
    }

    private async Task<HttpResponseMessage> SendAsync(Uri endpoint, ChatCompletionRequest payload, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(nameof(LocalLlmProvider));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
        return await client.PostAsJsonAsync(endpoint, payload, JsonOptions, timeoutCts.Token).ConfigureAwait(false);
    }

    private ChatCompletionRequest BuildRequest(LlmRequest request, bool includeJsonResponseFormat)
    {
        var temperature = double.IsNaN(request.Temperature) || double.IsInfinity(request.Temperature)
            ? _settings.DefaultTemperature
            : request.Temperature;

        return new ChatCompletionRequest(
            _settings.Model,
            temperature,
            request.Messages.Select(message => new ChatMessage(MapRole(message.Role), message.Content)).ToArray(),
            includeJsonResponseFormat ? new ResponseFormat("json_object") : null);
    }

    private static Uri BuildEndpoint(string baseUrl)
    {
        var normalized = baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";
        return new Uri(new Uri(normalized, UriKind.Absolute), "v1/chat/completions");
    }

    private static string MapRole(LlmRole role)
    {
        return role switch
        {
            LlmRole.System => "system",
            LlmRole.User => "user",
            LlmRole.Assistant => "assistant",
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown LLM message role.")
        };
    }

    private sealed record ChatCompletionRequest(
        string Model,
        double Temperature,
        IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("response_format")] ResponseFormat? ResponseFormat);

    private sealed record ChatMessage(string Role, string Content);

    private sealed record ResponseFormat(string Type);

    private sealed record ChatCompletionResponse(IReadOnlyList<Choice>? Choices);

    private sealed record Choice(ChatMessageResponse? Message);

    private sealed record ChatMessageResponse(string? Content);
}
