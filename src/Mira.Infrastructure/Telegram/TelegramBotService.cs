using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mira.Core.UseCases;
using Mira.Infrastructure.Configuration;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Mira.Infrastructure.Telegram;

internal sealed class TelegramBotService : BackgroundService
{
    private readonly TelegramBotClient _bot;
    private readonly ProcessMessageUseCase _useCase;
    private readonly TelegramSettings _settings;
    private readonly ILogger<TelegramBotService> _logger;

    public TelegramBotService(
        ProcessMessageUseCase useCase,
        IOptions<TelegramSettings> settings,
        ILogger<TelegramBotService> logger)
    {
        _settings = settings.Value;
        _useCase = useCase;
        _logger = logger;
        _bot = new TelegramBotClient(_settings.BotToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _bot.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync,
            receiverOptions: new ReceiverOptions
            {
                AllowedUpdates = [UpdateType.Message]
            },
            cancellationToken: stoppingToken);

        _logger.LogInformation("Telegram bot started. Listening for messages from chat {ChatId}", _settings.AllowedChatId);

        return Task.CompletedTask;
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Message is not { Text: { } text } message)
            return;

        if (message.Chat.Id != _settings.AllowedChatId)
        {
            _logger.LogWarning("Ignored message from unauthorized chat {ChatId}", message.Chat.Id);
            return;
        }

        _logger.LogInformation("Received message from {ChatId}: {Length} chars", message.Chat.Id, text.Length);

        try
        {
            var response = await _useCase.ExecuteAsync(text, ct);
            await bot.SendMessage(message.Chat.Id, response, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process message");
            await bot.SendMessage(message.Chat.Id, "Something went wrong. Try again.", cancellationToken: ct);
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken ct)
    {
        _logger.LogError(exception, "Telegram polling error");
        return Task.CompletedTask;
    }
}
