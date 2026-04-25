using Mira.Core.Enums;
using Mira.Core.Models;

namespace Mira.Core.Extensions;

public static class MessageClassificationExtensions
{
    public static MessageType ToMessageType(this string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return MessageType.Chat;

        return type.Trim().ToLowerInvariant() switch
        {
            "save_person" => MessageType.SavePerson,
            "save_fact" => MessageType.SaveFact,
            "query" => MessageType.Query,
            _ => MessageType.Chat
        };
    }
}
