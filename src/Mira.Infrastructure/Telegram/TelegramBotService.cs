namespace Mira.Infrastructure.Telegram;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mira.Core.Interfaces;
using Mira.Core.Models;
using Mira.Core.UseCases;
using Mira.Infrastructure.Configuration;
using global::Telegram.Bot;
using global::Telegram.Bot.Polling;
using global::Telegram.Bot.Types;
using global::Telegram.Bot.Types.Enums;
using global::Telegram.Bot.Types.ReplyMarkups;


public sealed class TelegramBotService : BackgroundService, INotificationSink
{
    private const int TelegramChunkSize = 3900;
    private const string ConfirmCallbackPrefix = "confirm:";
    private const string CommandCallbackPrefix = "command:";
    private static readonly HashSet<string> CallbackCommands = new(StringComparer.Ordinal)
    {
        "/menu",
        "/chat",
        "/reminders",
        "/memory",
        "/dashboard",
        "/health",
        "/people",
        "/decisions",
        "/notes",
        "/settings",
        "/status"
    };
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelegramBotService> _logger;
    private readonly TelegramSettings _settings;
    private readonly TelegramBotClient _botClient;
    private readonly IDocumentTextExtractor _documentTextExtractor;

    public TelegramBotService(
        IServiceScopeFactory scopeFactory,
        IOptions<TelegramSettings> options,
        ILogger<TelegramBotService> logger,
        IDocumentTextExtractor documentTextExtractor)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _settings = options.Value;
        _documentTextExtractor = documentTextExtractor;
        _botClient = new TelegramBotClient(_settings.BotToken);
    }

    public async Task SendOwnerMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        await SendChunksAsync(_settings.AllowedChatId, text, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery]
        };

        await _botClient.SetMyCommands(BuildBotCommands(), cancellationToken: stoppingToken).ConfigureAwait(false);

        _botClient.StartReceiving(HandleUpdateAsync, HandlePollingErrorAsync, receiverOptions, stoppingToken);
        _logger.LogInformation("Telegram polling started for configured owner chat.");
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.CallbackQuery is not null)
        {
            await HandleCallbackQueryAsync(botClient, update.CallbackQuery, cancellationToken).ConfigureAwait(false);
            return;
        }

        var message = update.Message;
        if (message is null)
        {
            return;
        }

        if (message.Chat.Id != _settings.AllowedChatId)
        {
            return;
        }

        if (message.Document is not null)
        {
            await HandleDocumentMessageAsync(botClient, message, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(message.Text))
        {
            await SendChunksAsync(
                message.Chat.Id,
                "Text only for now. Voice/file ingestion can be added later through the same message pipeline.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var useCase = scope.ServiceProvider.GetRequiredService<ProcessMessageUseCase>();
            var incoming = new IncomingMessage(message.Chat.Id, message.MessageId, message.Text, DateTimeOffset.UtcNow);
            var reply = await useCase.HandleAsync(incoming, cancellationToken).ConfigureAwait(false);
            await SendChunksAsync(message.Chat.Id, reply.Text, cancellationToken, BuildReplyMarkup(reply.Text)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Local error while processing Telegram owner message.");
            await SendChunksAsync(
                message.Chat.Id,
                "I hit a local error while processing that. Check the Mira logs on the PC.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleDocumentMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var document = message.Document;
        if (document is null)
        {
            return;
        }

        var fileName = string.IsNullOrWhiteSpace(document.FileName) ? "document" : document.FileName.Trim();
        if (!_documentTextExtractor.Supports(fileName, document.MimeType))
        {
            await SendChunksAsync(
                message.Chat.Id,
                $"Unsupported document type for \"{fileName}\". Send a PDF, .txt, or Markdown file.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await using var stream = new MemoryStream();
            var file = await botClient.GetFile(document.FileId, cancellationToken).ConfigureAwait(false);
            await botClient.DownloadFile(file, stream, cancellationToken).ConfigureAwait(false);
            stream.Position = 0;

            var extraction = await _documentTextExtractor.ExtractTextAsync(fileName, document.MimeType, stream, cancellationToken).ConfigureAwait(false);
            if (!extraction.IsSupported)
            {
                await SendChunksAsync(
                    message.Chat.Id,
                    extraction.FailureMessage ?? $"Unsupported document type for \"{fileName}\". Send a PDF, .txt, or Markdown file.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!extraction.HasText)
            {
                await SendChunksAsync(
                    message.Chat.Id,
                    $"Document \"{fileName}\" did not contain extractable text.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var useCase = scope.ServiceProvider.GetRequiredService<ProcessMessageUseCase>();
            var incoming = new IncomingMessage(message.Chat.Id, message.MessageId, $"[document import] {fileName}", DateTimeOffset.UtcNow);
            var reply = await useCase.ImportDocumentTextAsync(incoming, fileName, extraction.Text, cancellationToken).ConfigureAwait(false);
            await SendChunksAsync(message.Chat.Id, reply.Text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Local error while processing Telegram owner document.");
            await SendChunksAsync(
                message.Chat.Id,
                "I hit a local error while importing that document. Check the Mira logs on the PC.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleCallbackQueryAsync(ITelegramBotClient botClient, CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        var message = callbackQuery.Message;
        if (message?.Chat.Id != _settings.AllowedChatId)
        {
            return;
        }

        var data = callbackQuery.Data;
        if (data is null)
        {
            await botClient.AnswerCallbackQuery(callbackQuery.Id, "Unsupported Mira action.", cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        if (data.StartsWith(ConfirmCallbackPrefix, StringComparison.Ordinal))
        {
            await HandleConfirmCallbackAsync(botClient, callbackQuery, message, data, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (data.StartsWith(CommandCallbackPrefix, StringComparison.Ordinal))
        {
            await HandleCommandCallbackAsync(botClient, callbackQuery, message, data, cancellationToken).ConfigureAwait(false);
            return;
        }

        await botClient.AnswerCallbackQuery(callbackQuery.Id, "Unsupported Mira action.", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleConfirmCallbackAsync(ITelegramBotClient botClient, CallbackQuery callbackQuery, Message message, string data, CancellationToken cancellationToken)
    {
        var idText = data[ConfirmCallbackPrefix.Length..];
        if (!Guid.TryParse(idText, out var id))
        {
            await botClient.AnswerCallbackQuery(callbackQuery.Id, "Invalid confirmation id.", cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var useCase = scope.ServiceProvider.GetRequiredService<ProcessMessageUseCase>();
            var incoming = new IncomingMessage(message.Chat.Id, message.MessageId, $"/confirm {id}", DateTimeOffset.UtcNow);
            var reply = await useCase.HandleAsync(incoming, cancellationToken).ConfigureAwait(false);
            await botClient.AnswerCallbackQuery(callbackQuery.Id, "Confirmed.", cancellationToken: cancellationToken).ConfigureAwait(false);
            await SendChunksAsync(message.Chat.Id, reply.Text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Local error while processing Telegram callback.");
            await botClient.AnswerCallbackQuery(callbackQuery.Id, "Mira hit a local error.", cancellationToken: cancellationToken).ConfigureAwait(false);
            await SendChunksAsync(
                message.Chat.Id,
                "I hit a local error while processing that. Check the Mira logs on the PC.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleCommandCallbackAsync(ITelegramBotClient botClient, CallbackQuery callbackQuery, Message message, string data, CancellationToken cancellationToken)
    {
        var command = data[CommandCallbackPrefix.Length..];
        if (!CallbackCommands.Contains(command))
        {
            await botClient.AnswerCallbackQuery(callbackQuery.Id, "Unsupported Mira folder.", cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var useCase = scope.ServiceProvider.GetRequiredService<ProcessMessageUseCase>();
            var incoming = new IncomingMessage(message.Chat.Id, message.MessageId, command, DateTimeOffset.UtcNow);
            var reply = await useCase.HandleAsync(incoming, cancellationToken).ConfigureAwait(false);
            await botClient.AnswerCallbackQuery(callbackQuery.Id, "Opened.", cancellationToken: cancellationToken).ConfigureAwait(false);
            await SendChunksAsync(message.Chat.Id, reply.Text, cancellationToken, BuildReplyMarkup(reply.Text)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Local error while processing Telegram callback.");
            await botClient.AnswerCallbackQuery(callbackQuery.Id, "Mira hit a local error.", cancellationToken: cancellationToken).ConfigureAwait(false);
            await SendChunksAsync(
                message.Chat.Id,
                "I hit a local error while processing that. Check the Mira logs on the PC.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Telegram polling error.");
        return Task.CompletedTask;
    }

    private async Task SendChunksAsync(long chatId, string text, CancellationToken cancellationToken, InlineKeyboardMarkup? replyMarkup = null)
    {
        var chunks = SplitTelegramChunks(string.IsNullOrWhiteSpace(text) ? "Done." : text);
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunkReplyMarkup = index == 0 ? replyMarkup : null;
            await _botClient.SendMessage(
                chatId,
                TelegramMessageFormatter.ToTelegramHtml(chunks[index]),
                parseMode: ParseMode.Html,
                replyMarkup: chunkReplyMarkup,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<string> SplitTelegramChunks(string value)
    {
        if (value.Length <= TelegramChunkSize)
        {
            return [value];
        }

        var chunks = new List<string>();
        var index = 0;
        while (index < value.Length)
        {
            var remaining = value.Length - index;
            if (remaining <= TelegramChunkSize)
            {
                chunks.Add(value[index..]);
                break;
            }

            var length = TelegramChunkSize;
            var newlineIndex = value.LastIndexOf('\n', index + TelegramChunkSize - 1, TelegramChunkSize);
            if (newlineIndex > index)
            {
                length = newlineIndex - index + 1;
            }

            chunks.Add(value.Substring(index, length));
            index += length;
        }

        return chunks;
    }

    private static InlineKeyboardMarkup? BuildReplyMarkup(string text)
    {
        var confirmId = ExtractConfirmId(text);
        if (confirmId is not null)
        {
            return new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("Confirm automation", ConfirmCallbackPrefix + confirmId.Value));
        }

        if (IsFolderMenuText(text))
        {
            return BuildFolderMenuMarkup();
        }

        return IsFolderSectionText(text)
            ? new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("Back to menu", CommandCallbackPrefix + "/menu"))
            : null;
    }

    private static InlineKeyboardMarkup BuildFolderMenuMarkup()
    {
        return new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithCallbackData("AI Chat", CommandCallbackPrefix + "/chat"),
                InlineKeyboardButton.WithCallbackData("Reminders", CommandCallbackPrefix + "/reminders")
            ],
            [
                InlineKeyboardButton.WithCallbackData("Memory", CommandCallbackPrefix + "/memory"),
                InlineKeyboardButton.WithCallbackData("Dashboard", CommandCallbackPrefix + "/dashboard")
            ],
            [
                InlineKeyboardButton.WithCallbackData("Health", CommandCallbackPrefix + "/health"),
                InlineKeyboardButton.WithCallbackData("People", CommandCallbackPrefix + "/people")
            ],
            [
                InlineKeyboardButton.WithCallbackData("Decisions", CommandCallbackPrefix + "/decisions"),
                InlineKeyboardButton.WithCallbackData("Notes", CommandCallbackPrefix + "/notes")
            ],
            [
                InlineKeyboardButton.WithCallbackData("Settings", CommandCallbackPrefix + "/settings"),
                InlineKeyboardButton.WithCallbackData("Status", CommandCallbackPrefix + "/status")
            ]
        ]);
    }

    private static bool IsFolderMenuText(string text) => text.StartsWith("Mira folders", StringComparison.Ordinal);

    private static bool IsFolderSectionText(string text)
    {
        return text.StartsWith("AI Chat", StringComparison.Ordinal)
            || text.StartsWith("Reminders", StringComparison.Ordinal)
            || text.StartsWith("Memory", StringComparison.Ordinal)
            || text.StartsWith("Dashboard", StringComparison.Ordinal)
            || text.StartsWith("Daily Notes", StringComparison.Ordinal)
            || text.StartsWith("Health", StringComparison.Ordinal)
            || text.StartsWith("People", StringComparison.Ordinal)
            || text.StartsWith("Decisions", StringComparison.Ordinal)
            || text.StartsWith("Settings", StringComparison.Ordinal)
            || text.StartsWith("Status", StringComparison.Ordinal);
    }

    private static Guid? ExtractConfirmId(string text)
    {
        var markerIndex = text.IndexOf("/confirm ", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var start = markerIndex + "/confirm ".Length;
        var end = text.IndexOfAny([' ', '\r', '\n', '\t'], start);
        var idText = end < 0 ? text[start..] : text[start..end];
        return Guid.TryParse(idText, out var id) ? id : null;
    }

    private static BotCommand[] BuildBotCommands()
    {
        return
        [
            new BotCommand { Command = "menu", Description = "Show folder menu" },
            new BotCommand { Command = "chat", Description = "Open AI chat folder" },
            new BotCommand { Command = "reminders", Description = "Open reminders folder" },
            new BotCommand { Command = "memory", Description = "Open memory folder" },
            new BotCommand { Command = "dashboard", Description = "Show local memory dashboard paths" },
            new BotCommand { Command = "notes", Description = "Open daily notes folder" },
            new BotCommand { Command = "settings", Description = "Show local settings" },
            new BotCommand { Command = "status", Description = "Show local runtime and alert status" },
            new BotCommand { Command = "capture", Description = "Save text and extract memory" },
            new BotCommand { Command = "remember", Description = "Save text and extract memory" },
            new BotCommand { Command = "note", Description = "Save a daily note" },
            new BotCommand { Command = "search", Description = "Search saved memories" },
            new BotCommand { Command = "today", Description = "Show today's dashboard" },
            new BotCommand { Command = "profile", Description = "Summarize a person" },
            new BotCommand { Command = "uncertain", Description = "Review low-confidence memories" },
            new BotCommand { Command = "stale", Description = "Review stale memories" },
            new BotCommand { Command = "brief", Description = "Generate daily brief" },
            new BotCommand { Command = "review", Description = "Generate weekly review" },
            new BotCommand { Command = "people", Description = "List saved people" },
            new BotCommand { Command = "decisions", Description = "List saved decisions" },
            new BotCommand { Command = "health", Description = "Summarize recent health entries" },
            new BotCommand { Command = "help", Description = "Show Mira commands" }
        ];
    }
}
