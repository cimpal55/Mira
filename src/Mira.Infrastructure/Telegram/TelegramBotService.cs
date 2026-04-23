// BackgroundService that runs Telegram polling loop. Filters messages by AllowedChatId, delegates to ProcessMessageUseCase, sends response back.
namespace Mira.Infrastructure.Telegram;
