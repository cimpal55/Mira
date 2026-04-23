namespace Mira.Infrastructure.Configuration;

public sealed class TelegramSettings
{
    public const string SectionName = "Telegram";

    public required string BotToken { get; init; }
    public required long AllowedChatId { get; init; }
}
