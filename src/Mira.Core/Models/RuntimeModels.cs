namespace Mira.Core.Models;

public sealed record AssistantRuntimeSettings(
    TimeZoneInfo TimeZone,
    int MaxContextMemories,
    int MaxReplyCharacters,
    string MedicalBoundaryMessage,
    string KnowledgeDashboardPath,
    string KnowledgeDashboardHtmlPath);
