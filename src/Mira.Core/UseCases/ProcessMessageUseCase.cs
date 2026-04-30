// Orchestrates handling of an incoming user message: builds system prompt, calls ILlmProvider, returns response text. No Telegram or HTTP dependencies.
using Mira.Core.Interfaces;

namespace Mira.Core.UseCases;

public sealed class ProcessMessageUseCase
{
    private readonly ILlmProvider _llmProvider;
    public ProcessMessageUseCase(ILlmProvider llmProvider)
    {
        _llmProvider = llmProvider;
    }
    public async Task<string> ExecuteAsync(string userMessage, CancellationToken ct)
    {
        // Phase 1: hardcoded system prompt
        var systemPrompt = "You are Mira, a personal AI assistant. Be concise and helpful.";

        return await _llmProvider.GenerateResponseAsync(systemPrompt, userMessage, ct);
    }
}