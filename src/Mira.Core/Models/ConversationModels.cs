namespace Mira.Core.Models;

public enum ConversationDirection
{
    Incoming,
    Outgoing
}

public sealed record IncomingMessage(long ChatId, long MessageId, string Text, DateTimeOffset ReceivedAt);

public sealed record AssistantReply(string Text);

public sealed record ConversationMessage(
    long ChatId,
    long MessageId,
    ConversationDirection Direction,
    string Content,
    DateTimeOffset CreatedAt);
