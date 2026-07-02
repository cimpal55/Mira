namespace Mira.Core.Models;

public enum LlmRole
{
    System,
    User,
    Assistant
}

public sealed record LlmMessage(LlmRole Role, string Content);

public sealed record LlmRequest(IReadOnlyList<LlmMessage> Messages, double Temperature = 0.2, bool RequireJson = false);

public sealed record LlmResponse(string Content);
