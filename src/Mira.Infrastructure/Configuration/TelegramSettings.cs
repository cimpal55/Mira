namespace Mira.Infrastructure.Configuration;

using System.ComponentModel.DataAnnotations;

public sealed class TelegramSettings
{
    public const string SectionName = "Telegram";

    [Required]
    public string BotToken { get; set; } = string.Empty;

    [Range(1, long.MaxValue)]
    public long AllowedChatId { get; set; }
}
