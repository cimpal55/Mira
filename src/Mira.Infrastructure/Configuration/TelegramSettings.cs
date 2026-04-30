using System.ComponentModel.DataAnnotations;

namespace Mira.Infrastructure.Configuration;

public sealed class TelegramSettings
{
    public const string SectionName = "Telegram";

    [Required]
    public string BotToken { get; init; } = string.Empty;

    [Required]
    public long AllowedChatId { get; init; }
}
