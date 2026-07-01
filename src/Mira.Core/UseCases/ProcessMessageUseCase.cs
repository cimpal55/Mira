// Orchestrates handling of an incoming user message: builds system prompt, calls ILlmProvider, returns response text. No Telegram or HTTP dependencies.
using Mira.Core.Entities;
using Mira.Core.Enums;
using Mira.Core.Extensions;
using Mira.Core.Interfaces;
using Mira.Core.Models;
using Mira.Core.Prompts;

namespace Mira.Core.UseCases;

public sealed class ProcessMessageUseCase
{
    private readonly ILlmProvider _llmProvider;
    private readonly IMemoryRepository _memoryRepository;
    private readonly IPersonRepository _personRepository;

    public ProcessMessageUseCase(ILlmProvider llmProvider,
        IMemoryRepository memoryRepository,
        IPersonRepository personRepository)
    {
        _llmProvider = llmProvider;
        _memoryRepository = memoryRepository;
        _personRepository = personRepository;
    }

    public async Task<string> ExecuteAsync(string userMessage, CancellationToken ct)
    {
        var classification = await _llmProvider.GenerateJsonAsync<MessageClassification>(
            SystemPrompts.Classification,
            userMessage,
            ct);

        var messageType = classification.Type.ToMessageType();

        return messageType switch
        {
            MessageType.SavePerson => await SavePersonAsync(userMessage, classification, ct),
            MessageType.SaveFact => await SaveFactAsync(userMessage, classification, ct),
            MessageType.Query => await AnswerQueryAsync(userMessage, classification, ct),
            MessageType.Chat => await ChatAsync(userMessage, ct),
            _ => await ChatAsync(userMessage, ct)
        };

        //// Phase 1: hardcoded system prompt
        //var systemPrompt = "You are Mira, a personal AI assistant. Be concise and helpful.";

        //return await _llmProvider.GenerateResponseAsync(systemPrompt, userMessage, ct);
    }

    private async Task<string> SavePersonAsync(
        string userMessage,
        MessageClassification classification,
        CancellationToken ct)
    {
        var personExtraction = classification.Person;

        if (personExtraction is null ||
            string.IsNullOrWhiteSpace(personExtraction.Name))
        {
            return await ChatAsync(userMessage, ct);
        }

        //Person person = new()
        //{
        //    Interests = personExtraction.Interests ?? string.Empty,
        //}
        
        // 1. Validate classification.Person exists
        // 2. Convert PersonExtraction -> Person
        // 3. Save person
        // 4. Save raw memory entry
        // 5. Return save confirmation
        return await _llmProvider.GenerateResponseAsync(SystemPrompts.Base, userMessage, ct);
    }

    private async Task<string> SaveFactAsync(
        string userMessage,
        MessageClassification classification,
        CancellationToken ct)
        {
        // 1. Save MemoryEntry with FactContent or original userMessage
        // 2. Return save confirmation
        return await _llmProvider.GenerateResponseAsync(SystemPrompts.Base, userMessage, ct);
    }

    private async Task<string> AnswerQueryAsync(
        string userMessage,
        MessageClassification classification,
        CancellationToken ct)
    {
        // 1. Search memory using classification.SearchQuery ?? userMessage
        // 2. Build context string
        // 3. Call GenerateResponseAsync(SystemPrompts.BuildContextual(context), userMessage, ct)
        return await _llmProvider.GenerateResponseAsync(SystemPrompts.Base, userMessage, ct);
    }

    private Task<string> ChatAsync(string userMessage, CancellationToken ct)
    {
        return _llmProvider.GenerateResponseAsync(SystemPrompts.Base, userMessage, ct);
    }
}