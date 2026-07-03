namespace Mira.Core.UseCases;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mira.Core.Interfaces;
using Mira.Core.Models;

public sealed class ProcessMessageUseCase(
    ILlmProvider llmProvider,
    IMemoryStore memoryStore,
    IReminderStore reminderStore,
    IAutomationStore automationStore,
    IAutomationRunner automationRunner,
    IKnowledgeArtifactWriter knowledgeArtifactWriter,
    IClock clock,
    AssistantRuntimeSettings settings)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter<MemoryCategory>(),
            new JsonStringEnumConverter<ReminderRepeatKind>()
        }
    };

    private const int DocumentClassificationSnippetCharacters = 6_000;
    private const int DocumentFallbackExcerptCharacters = 1_200;

    private static readonly string[] KnownIntents =
    [
        "save_memory",
        "answer",
        "create_reminder",
        "list_reminders",
        "complete_reminder",
        "run_automation",
        "daily_brief",
        "weekly_review",
        "health_summary",
        "clarify",
        "chat"
    ];

    public async Task<AssistantReply> HandleAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
        var text = message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return await ReplyAsync(message, "Send text for now. Voice and file ingestion are not enabled yet.", cancellationToken).ConfigureAwait(false);
        }
        var receivedAtUtc = ToUtc(message.ReceivedAt);

        await memoryStore.SaveConversationMessageAsync(
            new ConversationMessage(
                message.ChatId,
                message.MessageId,
                ConversationDirection.Incoming,
                text,
                receivedAtUtc),
            cancellationToken).ConfigureAwait(false);

        var commandReply = await TryHandleCommandAsync(message, text, cancellationToken).ConfigureAwait(false);
        if (commandReply is not null)
        {
            return commandReply;
        }

        var action = await ClassifyAsync(message.ChatId, receivedAtUtc, text, forceSaveMemory: false, cancellationToken).ConfigureAwait(false);
        string replyText;
        if (action is null || !IsKnownIntent(action.Intent))
        {
            var answer = await AnswerWithContextAsync(message.ChatId, receivedAtUtc, text, appendStructuringFailure: true, cancellationToken).ConfigureAwait(false);
            replyText = answer;
        }
        else
        {
            replyText = await HandleActionAsync(message, text, action, cancellationToken).ConfigureAwait(false);
        }

        return await ReplyAsync(message, replyText, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssistantReply> ImportDocumentTextAsync(IncomingMessage message, string documentName, string content, CancellationToken cancellationToken = default)
    {
        var name = SafeDocumentName(documentName);
        var documentText = content;
        if (string.IsNullOrWhiteSpace(documentText))
        {
            return await ReplyAsync(message, $"Document \"{name}\" did not contain extractable text.", cancellationToken).ConfigureAwait(false);
        }

        var receivedAtUtc = ToUtc(message.ReceivedAt);
        await memoryStore.SaveConversationMessageAsync(
            new ConversationMessage(
                message.ChatId,
                message.MessageId,
                ConversationDirection.Incoming,
                $"[document import] {name}",
                receivedAtUtc),
            cancellationToken).ConfigureAwait(false);

        var sourcePath = await memoryStore.SaveRawCaptureAsync(documentText, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var classificationText = BuildDocumentClassificationText(name, documentText);
        var action = await ClassifyAsync(message.ChatId, receivedAtUtc, classificationText, forceSaveMemory: true, cancellationToken).ConfigureAwait(false);
        var fallbackContent = BuildDocumentFallbackMemoryContent(name, sourcePath, documentText);
        var saved = await SaveCapturedMemoryAsync($"Imported document: {name}", fallbackContent, sourcePath, message.MessageId, action, cancellationToken).ConfigureAwait(false);

        return await ReplyAsync(
            message,
            $"""
            Imported document: {name}
            Raw capture: {sourcePath}
            Saved memory: {saved.Title}
            Local dashboard: {settings.KnowledgeDashboardHtmlPath}
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AssistantReply?> TryHandleCommandAsync(IncomingMessage message, string text, CancellationToken cancellationToken)
    {
        if (IsCommand(text, "/start") || IsCommand(text, "/menu"))
        {
            return await ReplyAsync(message, MenuText(), cancellationToken).ConfigureAwait(false);
        }

        if (IsCommand(text, "/help"))
        {
            return await ReplyAsync(message, HelpText(), cancellationToken).ConfigureAwait(false);
        }

        if (TryGetCommandArgument(text, "/capture", out var captureText) || TryGetCommandArgument(text, "/remember", out captureText))
        {
            if (string.IsNullOrWhiteSpace(captureText))
            {
                return await ReplyAsync(message, "Usage: /capture <text> or /remember <text>", cancellationToken).ConfigureAwait(false);
            }

            var sourcePath = await memoryStore.SaveRawCaptureAsync(captureText, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            var action = await ClassifyAsync(message.ChatId, ToUtc(message.ReceivedAt), captureText, forceSaveMemory: true, cancellationToken).ConfigureAwait(false);
            var saved = await SaveCapturedMemoryAsync(captureText, captureText, sourcePath, message.MessageId, action, cancellationToken).ConfigureAwait(false);
            return await ReplyAsync(message, $"Saved: {saved.Title}", cancellationToken).ConfigureAwait(false);
        }

        if (TryGetCommandArgument(text, "/search", out var searchText))
        {
            return await ReplyAsync(message, await SearchMemoriesAsync(searchText, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        if (IsCommand(text, "/today"))
        {
            return await ReplyAsync(message, await GenerateTodayDashboardAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        if (IsCommand(text, "/dashboard"))
        {
            return await ReplyAsync(message, DashboardSectionText(), cancellationToken).ConfigureAwait(false);
        }

        if (IsCommand(text, "/status"))
        {
            return await ReplyAsync(message, StatusSectionText(), cancellationToken).ConfigureAwait(false);
        }

        if (IsCommand(text, "/chat"))
        {
            return await ReplyAsync(message, ChatSectionText(), cancellationToken).ConfigureAwait(false);
        }

        if (IsCommand(text, "/memory"))
        {
            return await ReplyAsync(message, MemorySectionText(), cancellationToken).ConfigureAwait(false);
        }

        if (IsCommand(text, "/notes"))
        {
            return await ReplyAsync(message, NotesSectionText(), cancellationToken).ConfigureAwait(false);
        }

        if (IsCommand(text, "/settings"))
        {
            return await ReplyAsync(message, SettingsSectionText(), cancellationToken).ConfigureAwait(false);
        }

        if (TryGetCommandArgument(text, "/note", out var noteText))
        {
            return await ReplyAsync(message, await SaveDailyNoteAsync(message.MessageId, noteText, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        if (TryGetCommandArgument(text, "/profile", out var profileName))
        {
            return await ReplyAsync(message, await GeneratePersonProfileAsync(profileName, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        if (IsCommand(text, "/uncertain"))
        {
            var lowConfidence = await memoryStore.GetLowConfidenceAsync(0.6, 20, cancellationToken).ConfigureAwait(false);
            return await ReplyAsync(message, FormatMemories("Low-confidence memories", lowConfidence), cancellationToken).ConfigureAwait(false);
        }

        if (IsCommand(text, "/stale"))
        {
            var stale = await memoryStore.GetStaleAsync(clock.UtcNow.AddDays(-90), 20, cancellationToken).ConfigureAwait(false);
            return await ReplyAsync(message, FormatMemories("Stale memories", stale), cancellationToken).ConfigureAwait(false);
        }



        if (IsCommand(text, "/brief"))
        {
            return await ReplyAsync(message, await GenerateDailyBriefAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        if (IsCommand(text, "/review"))
        {
            return await ReplyAsync(message, await GenerateWeeklyReviewAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        if (IsCommand(text, "/people"))
        {
            var people = await memoryStore.GetByCategoryAsync(MemoryCategory.Person, 20, cancellationToken).ConfigureAwait(false);
            return await ReplyAsync(message, FormatMemoryFolder("People", people), cancellationToken).ConfigureAwait(false);
        }

        if (IsCommand(text, "/decisions"))
        {
            var decisions = await memoryStore.GetByCategoryAsync(MemoryCategory.Decision, 20, cancellationToken).ConfigureAwait(false);
            return await ReplyAsync(message, FormatMemoryFolder("Decisions", decisions), cancellationToken).ConfigureAwait(false);
        }

        if (IsCommand(text, "/health"))
        {
            var summary = await GenerateHealthSummaryAsync(text, cancellationToken).ConfigureAwait(false);
            return await ReplyAsync(message, FormatHealthSection(summary), cancellationToken).ConfigureAwait(false);
        }

        if (IsCommand(text, "/reminders"))
        {
            var reminders = await reminderStore.GetPendingAsync(20, cancellationToken).ConfigureAwait(false);
            return await ReplyAsync(message, FormatRemindersSection(reminders), cancellationToken).ConfigureAwait(false);
        }

        if (TryGetCommandArgument(text, "/forget", out var forgetId))
        {
            if (!Guid.TryParse(forgetId, out var id))
            {
                return await ReplyAsync(message, "Usage: /forget <memory-guid>", cancellationToken).ConfigureAwait(false);
            }

            await memoryStore.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
            return await ReplyAsync(message, $"Forgot memory {id}.", cancellationToken).ConfigureAwait(false);
        }

        if (TryGetCommandArgument(text, "/cancel", out var cancelId))
        {
            if (!Guid.TryParse(cancelId, out var id))
            {
                return await ReplyAsync(message, "Usage: /cancel <reminder-guid>", cancellationToken).ConfigureAwait(false);
            }

            await reminderStore.CancelAsync(id, cancellationToken).ConfigureAwait(false);
            return await ReplyAsync(message, $"Cancelled reminder {id}.", cancellationToken).ConfigureAwait(false);
        }

        if (TryGetCommandArgument(text, "/confirm", out var confirmId))
        {
            if (!Guid.TryParse(confirmId, out var id))
            {
                return await ReplyAsync(message, "Usage: /confirm <automation-guid>", cancellationToken).ConfigureAwait(false);
            }

            return await ReplyAsync(message, await ConfirmAutomationAsync(id, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<string> HandleActionAsync(IncomingMessage message, string text, AssistantAction action, CancellationToken cancellationToken)
    {
        var receivedAtUtc = ToUtc(message.ReceivedAt);
        return NormalizeIntent(action.Intent) switch
        {
            "save_memory" => await SaveMemoryIntentAsync(message.MessageId, text, action, cancellationToken).ConfigureAwait(false),
            "answer" or "chat" => await AnswerWithContextAsync(message.ChatId, receivedAtUtc, text, appendStructuringFailure: false, cancellationToken).ConfigureAwait(false),
            "create_reminder" => await CreateReminderAsync(action, cancellationToken).ConfigureAwait(false),
            "list_reminders" => FormatReminders(await reminderStore.GetPendingAsync(20, cancellationToken).ConfigureAwait(false)),
            "complete_reminder" => await CompleteReminderAsync(text, cancellationToken).ConfigureAwait(false),
            "daily_brief" => await GenerateDailyBriefAsync(cancellationToken).ConfigureAwait(false),
            "weekly_review" => await GenerateWeeklyReviewAsync(cancellationToken).ConfigureAwait(false),
            "health_summary" => await GenerateHealthSummaryAsync(text, cancellationToken).ConfigureAwait(false),
            "run_automation" => await StageAutomationAsync(action, cancellationToken).ConfigureAwait(false),
            "clarify" => string.IsNullOrWhiteSpace(action.Question)
                ? "What should I do with this: save it, remind you, or answer a question?"
                : action.Question.Trim(),
            _ => await AnswerWithContextAsync(message.ChatId, receivedAtUtc, text, appendStructuringFailure: true, cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<AssistantAction?> ClassifyAsync(long chatId, DateTimeOffset receivedAtUtc, string text, bool forceSaveMemory, CancellationToken cancellationToken)
    {
        var recentDialogue = await GetRecentDialogueAsync(chatId, receivedAtUtc, cancellationToken).ConfigureAwait(false);
        var prompt = BuildClassificationPrompt(forceSaveMemory, FormatConversationContext(recentDialogue));
        var request = new LlmRequest(
            [new LlmMessage(LlmRole.System, prompt), new LlmMessage(LlmRole.User, text)],
            Temperature: 0.1,
            RequireJson: true);

        try
        {
            var response = await llmProvider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            var action = JsonSerializer.Deserialize<AssistantAction>(response.Content, JsonOptions);
            return action is null || string.IsNullOrWhiteSpace(action.Intent) ? null : action;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string BuildClassificationPrompt(bool forceSaveMemory, string recentDialogue)
    {
        var localNow = TimeZoneInfo.ConvertTime(clock.UtcNow, settings.TimeZone);
        var categories = string.Join(", ", Enum.GetNames<MemoryCategory>());
        var forceInstruction = forceSaveMemory
            ? "The user explicitly asked to capture this. Prefer intent save_memory and extract durable facts."
            : "Choose the safest intent. If the message is ambiguous, use recent dialogue to resolve follow-up references; otherwise use clarify.";

        return $$"""
You classify one private local-assistant message into exactly one action.
Current local time: {{localNow:O}}
Valid MemoryCategory names: {{categories}}
Intent must be exactly one of: save_memory, answer, create_reminder, list_reminders, complete_reminder, run_automation, daily_brief, weekly_review, health_summary, clarify, chat.
{{forceInstruction}}
Use save_memory for durable facts about people/friends/health/medical/job/hobby/gear/thought/decision/daily notes.
Use create_reminder only when a concrete local due date/time can be inferred.
Use run_automation only when the user names an allowlisted local task and provides required arguments.
Use recent dialogue only for conversation continuity, pronoun resolution, and short follow-up replies; do not invent durable facts from dialogue.
For medical or medicine content, classify facts as Medical when they concern medication, diagnosis, dosage, clinician instructions, or treatment.
Recent dialogue before current message:
{{recentDialogue}}
Return JSON only. Do not include markdown.
JSON properties: Intent, Category, Title, Subject, Content, Tags, Confidence, DueLocal, Repeat, AutomationName, AutomationArguments, Question.
""";
    }

    private async Task<MemoryItem> SaveCapturedMemoryAsync(
        string fallbackTitleText,
        string fallbackContent,
        string sourcePath,
        long sourceMessageId,
        AssistantAction? action,
        CancellationToken cancellationToken)
    {
        if (action is not null
            && NormalizeIntent(action.Intent) == "save_memory"
            && !string.IsNullOrWhiteSpace(action.Title)
            && !string.IsNullOrWhiteSpace(action.Content))
        {
            return await memoryStore.UpsertAsync(
                new MemoryUpsert(
                    action.Category ?? MemoryCategory.General,
                    action.Title.Trim(),
                    action.Content.Trim(),
                    TrimToNull(action.Subject),
                    CleanTags(action.Tags),
                    ClampConfidence(action.Confidence),
                    sourceMessageId,
                    sourcePath),
                cancellationToken).ConfigureAwait(false);
        }

        var title = FirstCharacters(fallbackTitleText, 80);
        return await memoryStore.UpsertAsync(
            new MemoryUpsert(
                MemoryCategory.General,
                title,
                fallbackContent.Trim(),
                null,
                [],
                0.4,
                sourceMessageId,
                sourcePath),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SaveMemoryIntentAsync(long sourceMessageId, string text, AssistantAction action, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(action.Title) || string.IsNullOrWhiteSpace(action.Content))
        {
            return "What title and exact fact should I save?";
        }

        var sourcePath = await memoryStore.SaveRawCaptureAsync(text, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var saved = await memoryStore.UpsertAsync(
            new MemoryUpsert(
                action.Category ?? MemoryCategory.General,
                action.Title.Trim(),
                action.Content.Trim(),
                TrimToNull(action.Subject),
                CleanTags(action.Tags),
                ClampConfidence(action.Confidence),
                sourceMessageId,
                sourcePath),
            cancellationToken).ConfigureAwait(false);

        var related = await memoryStore.SearchAsync(
            new MemorySearchQuery(saved.Content, [], 3),
            cancellationToken).ConfigureAwait(false);
        var relatedLine = FormatRelated(related.Where(memory => memory.Id != saved.Id).Take(3).ToArray());
        return string.IsNullOrEmpty(relatedLine) ? $"Saved: {saved.Title}" : $"Saved: {saved.Title}\nRelated: {relatedLine}";
    }

    private async Task<string> AnswerWithContextAsync(long chatId, DateTimeOffset receivedAtUtc, string text, bool appendStructuringFailure, CancellationToken cancellationToken)
    {
        var recentDialogue = await GetRecentDialogueAsync(chatId, receivedAtUtc, cancellationToken).ConfigureAwait(false);
        var dialogueContext = FormatConversationContext(recentDialogue);
        var memories = await memoryStore.SearchAsync(
            new MemorySearchQuery(text, [], settings.MaxContextMemories),
            cancellationToken).ConfigureAwait(false);

        string answer;
        if (memories.Count == 0)
        {
            answer = await AnswerWithoutPersonalContextAsync(text, dialogueContext, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var context = FormatMemoryContext(memories);
            var system = $$"""
You are Mira, a private local-first assistant.
Use retrieved personal context for durable facts, recent dialogue for conversation continuity, and the current user message for the immediate request.
If the retrieved personal context does not support a durable personal claim, say so briefly.
For medical or medicine content, do not recommend changing dose, stopping medicine, ignoring a clinician, or making treatment decisions.
Retrieved personal context:
{{context}}

Recent dialogue before current message:
{{dialogueContext}}
""";
            var response = await llmProvider.CompleteAsync(
                new LlmRequest([new LlmMessage(LlmRole.System, system), new LlmMessage(LlmRole.User, text)], Temperature: 0.2),
                cancellationToken).ConfigureAwait(false);
            answer = response.Content.Trim();
            if (UsesMedicalContext(text, memories))
            {
                answer = EnsureMedicalBoundary(answer);
            }
        }

        if (appendStructuringFailure)
        {
            answer += "\n\nI could not structure this reliably, so I did not save new facts.";
        }

        return answer;
    }

    private async Task<string> AnswerWithoutPersonalContextAsync(string text, string dialogueContext, CancellationToken cancellationToken)
    {
        var system = $$"""
You are Mira, a private local-first assistant running on the user's PC.
No saved personal context matched this message.
Use recent dialogue to keep the conversation coherent, especially for short follow-ups like "yes", "what about that", or "write it again".
If the user asks for personal facts, preferences, memories, relationships, health history, reminders, or decisions that are not in recent dialogue, say you do not know yet and ask what should be saved.
If the user asks a general question, wants brainstorming, or needs help drafting text, answer normally using local model knowledge.
Do not pretend that unsaved personal facts are known.
For medical or medicine content, do not recommend changing dose, stopping medicine, ignoring a clinician, or making treatment decisions.
Recent dialogue before current message:
{{dialogueContext}}
""";
        var response = await llmProvider.CompleteAsync(
            new LlmRequest([new LlmMessage(LlmRole.System, system), new LlmMessage(LlmRole.User, text)], Temperature: 0.2),
            cancellationToken).ConfigureAwait(false);
        var answer = response.Content.Trim();
        return ContainsMedicalTopic(text) ? EnsureMedicalBoundary(answer) : answer;
    }

    private async Task<string> SearchMemoriesAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Usage: /search <text>";
        }

        var memories = await memoryStore.SearchAsync(new MemorySearchQuery(query.Trim(), [], 10), cancellationToken).ConfigureAwait(false);
        if (memories.Count == 0)
        {
            return $"No saved memories matched \"{FirstCharacters(query, 60)}\".";
        }

        return "Search results:\n" + string.Join("\n", memories.Select(memory => $"- {memory.Id}: {memory.Title} ({memory.Category}) — {memory.Content}"));
    }

    private async Task<string> GenerateTodayDashboardAsync(CancellationToken cancellationToken)
    {
        var localNow = LocalNow();
        var startOfLocalDay = new DateTime(localNow.Year, localNow.Month, localNow.Day);
        var startUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(startOfLocalDay, settings.TimeZone), TimeSpan.Zero);
        var pending = await reminderStore.GetPendingAsync(50, cancellationToken).ConfigureAwait(false);
        var dueToday = pending
            .Where(reminder => TimeZoneInfo.ConvertTime(reminder.DueAtUtc, settings.TimeZone).Date == localNow.Date)
            .OrderBy(reminder => reminder.DueAtUtc)
            .Take(10)
            .ToArray();
        var memories = await memoryStore.GetRecentAsync(20, startUtc, cancellationToken).ConfigureAwait(false);

        var remindersSection = dueToday.Length == 0
            ? "Due reminders: none"
            : "Due reminders:\n" + string.Join("\n", dueToday.Select(reminder => $"- {reminder.Id}: {reminder.Title} at {FormatLocalDateTime(reminder.DueAtUtc)}"));
        var memoriesSection = memories.Count == 0
            ? "New memories today: none"
            : "New memories today:\n" + string.Join("\n", memories.Select(memory => $"- {memory.Title} ({memory.Category}) — {memory.Content}"));

        return $"Today ({localNow:yyyy-MM-dd})\n\n{remindersSection}\n\n{memoriesSection}";
    }

    private async Task<string> SaveDailyNoteAsync(long sourceMessageId, string noteText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(noteText))
        {
            return "Usage: /note <text>";
        }

        var sourcePath = await memoryStore.SaveRawCaptureAsync(noteText, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var title = FirstCharacters(noteText, 80);
        var saved = await memoryStore.UpsertAsync(
            new MemoryUpsert(
                MemoryCategory.DailyNote,
                title,
                noteText.Trim(),
                null,
                ["daily-note"],
                1.0,
                sourceMessageId,
                sourcePath),
            cancellationToken).ConfigureAwait(false);
        return $"Daily note saved: {saved.Title}";
    }

    private async Task<string> GeneratePersonProfileAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Usage: /profile <person-name>";
        }

        var subject = name.Trim();
        var bySubject = await memoryStore.GetBySubjectAsync(subject, 20, cancellationToken).ConfigureAwait(false);
        var bySearch = await memoryStore.SearchAsync(new MemorySearchQuery(subject, [MemoryCategory.Person], 20), cancellationToken).ConfigureAwait(false);
        var memories = bySubject
            .Concat(bySearch)
            .DistinctBy(memory => memory.Id)
            .Take(20)
            .ToArray();
        if (memories.Length == 0)
        {
            return $"No saved person memories matched \"{FirstCharacters(subject, 60)}\". Save one with /remember Person: {subject}.";
        }

        var prompt = $$"""
Create a concise person profile using only these saved local memories.
Sections: relationship, known facts, preferences, gift ideas, open questions.
If a section is unsupported, say "not saved yet".
Do not invent facts.

Person: {{subject}}
Memories:
{{FormatMemoryContext(memories)}}
""";
        var response = await llmProvider.CompleteAsync(
            new LlmRequest([new LlmMessage(LlmRole.System, prompt), new LlmMessage(LlmRole.User, $"Create a profile for {subject}.")], Temperature: 0.2),
            cancellationToken).ConfigureAwait(false);
        var profile = response.Content.Trim();
        return UsesMedicalContext(subject, memories) ? EnsureMedicalBoundary(profile) : profile;
    }



    private async Task<string> CreateReminderAsync(AssistantAction action, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(action.Title) || action.DueLocal is null)
        {
            return "When should I remind you? Include a date or time.";
        }

        var dueUtc = ConvertLocalToUtc(action.DueLocal.Value);
        if (dueUtc <= clock.UtcNow)
        {
            return "That reminder time is in the past. What future time should I use?";
        }

        var repeat = action.Repeat ?? ReminderRepeatKind.None;
        var reminder = await reminderStore.AddAsync(
            new ReminderCreateRequest(action.Title.Trim(), TrimToNull(action.Content), dueUtc, repeat),
            cancellationToken).ConfigureAwait(false);
        var localDue = FormatLocalDateTime(reminder.DueAtUtc);
        return $"Reminder saved: {reminder.Title}\nDue: {localDue}\nRepeat: {reminder.RepeatKind}";
    }

    private async Task<string> CompleteReminderAsync(string text, CancellationToken cancellationToken)
    {
        var id = ExtractGuid(text);
        if (id is null)
        {
            var reminders = await reminderStore.GetPendingAsync(20, cancellationToken).ConfigureAwait(false);
            return $"Which reminder ID should I complete?\n\n{FormatReminders(reminders)}";
        }

        await reminderStore.CompleteAsync(id.Value, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        return $"Completed reminder {id}.";
    }

    private async Task<string> GenerateDailyBriefAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var pending = await reminderStore.GetPendingAsync(50, cancellationToken).ConfigureAwait(false);
        var dueSoon = pending.Where(reminder => reminder.DueAtUtc <= now.AddHours(48)).OrderBy(reminder => reminder.DueAtUtc).Take(20).ToArray();
        var decisions = await memoryStore.GetByCategoryAsync(MemoryCategory.Decision, 20, cancellationToken).ConfigureAwait(false);
        var recent = await memoryStore.GetRecentAsync(50, now.AddHours(-48), cancellationToken).ConfigureAwait(false);
        var prompt = $$"""
Produce a concise daily brief from local private context only.
Include due reminders in the next 48 hours, open decisions, and recent memories.
Do not invent facts.

Due reminders:
{{FormatReminderContext(dueSoon)}}

Open decisions:
{{FormatMemoryContext(decisions)}}

Recent memories:
{{FormatMemoryContext(recent)}}
""";
        var response = await llmProvider.CompleteAsync(
            new LlmRequest([new LlmMessage(LlmRole.System, prompt), new LlmMessage(LlmRole.User, "Create today's daily brief.")], Temperature: 0.2),
            cancellationToken).ConfigureAwait(false);
        var brief = response.Content.Trim();
        var fileName = $"{LocalNow():yyyyMMdd}-daily-brief.md";
        await knowledgeArtifactWriter.WriteBriefingAsync(fileName, brief, cancellationToken).ConfigureAwait(false);
        return brief;
    }

    private async Task<string> GenerateWeeklyReviewAsync(CancellationToken cancellationToken)
    {
        var since = clock.UtcNow.AddDays(-7);
        var recent = new List<MemoryItem>();
        foreach (var category in new[] { MemoryCategory.Health, MemoryCategory.Thought, MemoryCategory.Decision, MemoryCategory.DailyNote })
        {
            var items = await memoryStore.GetByCategoryAsync(category, 50, cancellationToken).ConfigureAwait(false);
            recent.AddRange(items.Where(item => item.UpdatedAt >= since || item.CreatedAt >= since));
        }

        var decisions = await memoryStore.GetByCategoryAsync(MemoryCategory.Decision, 20, cancellationToken).ConfigureAwait(false);
        var prompt = $$"""
Produce a concise weekly review from local private context only.
Sections: wins, patterns, next actions.
Use the last 7 days of Health, Thought, Decision, and DailyNote memories plus pending decisions.
Do not invent facts.

Recent memories:
{{FormatMemoryContext(recent)}}

Pending decisions:
{{FormatMemoryContext(decisions)}}
""";
        var response = await llmProvider.CompleteAsync(
            new LlmRequest([new LlmMessage(LlmRole.System, prompt), new LlmMessage(LlmRole.User, "Create this week's review.")], Temperature: 0.2),
            cancellationToken).ConfigureAwait(false);
        var review = response.Content.Trim();
        var fileName = $"{LocalNow():yyyyMMdd}-weekly-review.md";
        await knowledgeArtifactWriter.WriteBriefingAsync(fileName, review, cancellationToken).ConfigureAwait(false);
        await knowledgeArtifactWriter.WriteThreadAsync(fileName, review, cancellationToken).ConfigureAwait(false);
        return review;
    }

    private async Task<string> GenerateHealthSummaryAsync(string text, CancellationToken cancellationToken)
    {
        var since = clock.UtcNow.AddDays(-14);
        var health = await memoryStore.GetByCategoryAsync(MemoryCategory.Health, 50, cancellationToken).ConfigureAwait(false);
        var recent = health.Where(item => item.CreatedAt >= since || item.UpdatedAt >= since).Take(50).ToArray();
        if (recent.Length == 0)
        {
            return "I do not have health entries from the last 14 days.";
        }

        var prompt = $$"""
Summarize the last 14 days of saved health context only.
Do not invent facts. Do not recommend medication, dosage, or treatment changes.

Health context:
{{FormatMemoryContext(recent)}}
""";
        var response = await llmProvider.CompleteAsync(
            new LlmRequest([new LlmMessage(LlmRole.System, prompt), new LlmMessage(LlmRole.User, text)], Temperature: 0.2),
            cancellationToken).ConfigureAwait(false);
        var summary = response.Content.Trim();
        return ContainsMedicalTopic(text) || recent.Any(ContainsMedicalTopic)
            ? EnsureMedicalBoundary(summary)
            : summary;
    }

    private async Task<string> StageAutomationAsync(AssistantAction action, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(action.AutomationName))
        {
            return "Automation is not configured for that request. Add an allowlisted task name in Automation:Tasks first.";
        }

        if (action.AutomationArguments is null || action.AutomationArguments.Count == 0)
        {
            return $"Automation '{action.AutomationName}' is missing required arguments. Add the named argument values and try again.";
        }

        var request = new AutomationRequest(action.AutomationName.Trim(), action.AutomationArguments);
        var rejectionReason = await automationRunner.GetRejectionReasonAsync(request, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(rejectionReason))
        {
            return rejectionReason;
        }

        var pending = await automationStore.SavePendingAsync(
            request,
            clock.UtcNow.AddMinutes(10),
            cancellationToken).ConfigureAwait(false);
        return $"Automation staged: {pending.Name}\nConfirm within 10 minutes with /confirm {pending.Id}";
    }

    private async Task<string> ConfirmAutomationAsync(Guid id, CancellationToken cancellationToken)
    {
        var pending = await automationStore.TakePendingAsync(id, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        if (pending is null)
        {
            return "No pending automation found for that ID, or it expired.";
        }

        var result = await automationRunner.RunAsync(new AutomationRequest(pending.Name, pending.Arguments), cancellationToken).ConfigureAwait(false);
        var status = result.Status.ToString();
        var output = string.IsNullOrWhiteSpace(result.Output) ? string.Empty : $"\nOutput:\n{result.Output.Trim()}";
        var error = string.IsNullOrWhiteSpace(result.Error) ? string.Empty : $"\nError:\n{result.Error.Trim()}";
        return $"Automation {pending.Name}: {status}{output}{error}";
    }

    private async Task<AssistantReply> ReplyAsync(IncomingMessage message, string text, CancellationToken cancellationToken)
    {
        var trimmed = TrimReply(text);
        await memoryStore.SaveConversationMessageAsync(
            new ConversationMessage(
                message.ChatId,
                0,
                ConversationDirection.Outgoing,
                trimmed,
                clock.UtcNow),
            cancellationToken).ConfigureAwait(false);
        return new AssistantReply(trimmed);
    }

    private string TrimReply(string text)
    {
        var value = string.IsNullOrWhiteSpace(text) ? "Done." : text.Trim();
        if (value.Length <= settings.MaxReplyCharacters)
        {
            return value;
        }

        const string suffix = "\n\n[Trimmed locally because Telegram has message length limits.]";
        var max = Math.Max(0, settings.MaxReplyCharacters - suffix.Length);
        return value[..max] + suffix;
    }

    private string EnsureMedicalBoundary(string answer)
    {
        if (answer.StartsWith(settings.MedicalBoundaryMessage, StringComparison.OrdinalIgnoreCase))
        {
            return answer;
        }

        return $"{settings.MedicalBoundaryMessage}\n\n{answer}";
    }

    private bool UsesMedicalContext(string text, IReadOnlyList<MemoryItem> memories)
    {
        return memories.Any(memory => memory.Category == MemoryCategory.Medical)
            || ContainsMedicalTopic(text)
            || memories.Any(memory => ContainsMedicalTopic(memory.Title) || ContainsMedicalTopic(memory.Content));
    }

    private static bool ContainsMedicalTopic(string text)
    {
        return text.Contains("medical", StringComparison.OrdinalIgnoreCase)
            || text.Contains("medicine", StringComparison.OrdinalIgnoreCase)
            || text.Contains("medication", StringComparison.OrdinalIgnoreCase)
            || text.Contains("dose", StringComparison.OrdinalIgnoreCase)
            || text.Contains("dosage", StringComparison.OrdinalIgnoreCase)
            || text.Contains("doctor", StringComparison.OrdinalIgnoreCase)
            || text.Contains("clinician", StringComparison.OrdinalIgnoreCase)
            || text.Contains("prescription", StringComparison.OrdinalIgnoreCase)
            || text.Contains("treatment", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsMedicalTopic(MemoryItem item)
    {
        return ContainsMedicalTopic(item.Title)
            || ContainsMedicalTopic(item.Content)
            || item.Tags.Any(ContainsMedicalTopic);
    }

    private DateTimeOffset ConvertLocalToUtc(DateTimeOffset dueLocal)
    {
        var localDateTime = DateTime.SpecifyKind(dueLocal.DateTime, DateTimeKind.Unspecified);
        var utcDateTime = TimeZoneInfo.ConvertTimeToUtc(localDateTime, settings.TimeZone);
        return new DateTimeOffset(utcDateTime, TimeSpan.Zero);
    }

    private DateTimeOffset LocalNow() => TimeZoneInfo.ConvertTime(clock.UtcNow, settings.TimeZone);

    private string FormatLocalDateTime(DateTimeOffset utc)
    {
        return TimeZoneInfo.ConvertTime(utc, settings.TimeZone).ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
    }

    private Task<IReadOnlyList<ConversationMessage>> GetRecentDialogueAsync(long chatId, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
    {
        return memoryStore.GetRecentConversationAsync(chatId, 12, receivedAtUtc, cancellationToken);
    }

    private static string FormatConversationContext(IReadOnlyList<ConversationMessage> messages)
    {
        if (messages.Count == 0)
        {
            return "(none)";
        }

        var lines = messages.Select(message =>
        {
            var role = message.Direction == ConversationDirection.Incoming ? "user" : "mira";
            var content = FirstCharacters(message.Content.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal), 500);
            return $"- {message.CreatedAt:O} {role}: {content}";
        });
        return string.Join("\n", lines);
    }

    private static DateTimeOffset ToUtc(DateTimeOffset value) => value.ToUniversalTime();

    private string MenuText()
    {
        return """
Mira folders

/chat — AI Chat with saved context
/reminders — reminders and alerts
/memory — saved memories, search, people, decisions
/dashboard — local static memory dashboard
/notes — daily notes, today, brief, weekly review
/health — health summary
/people — saved people
/decisions — saved decisions
/settings — local settings
/status — local runtime and alert status

Use /help for every command.
""";
    }

    private static string ChatSectionText()
    {
        return """
AI Chat

Send a normal message and Mira will answer using local context when saved memory matches.

Useful commands:
- /menu — switch folders
- /search <text> — search saved memory first
- /remember <text> — save durable context
""";
    }

    private static string MemorySectionText()
    {
        return """
Memory

Use this folder for saved facts and review.

Commands:
- /remember <text> — save text and extract memory
- /capture <text> — save raw notes quickly
- /search <text> — search saved memories
- /dashboard — show local static UI and Markdown dashboard paths
- /people — list saved people
- /profile <name> — summarize a person
- /decisions — list saved decisions
- /uncertain — review low-confidence memories
- /stale — review memories older than 90 days
- /forget <guid> — delete a memory item
""";
    }

    private static string NotesSectionText()
    {
        return """
Daily Notes

Use this folder for daily logs, summaries, and reviews.

Commands:
- /note <text> — save a deterministic daily note
- /today — today's reminders and new memories
- /brief — generate today's local daily brief
- /review — generate this week's review
""";
    }

    private string DashboardSectionText()
    {
        return $"""
Dashboard

Local static UI: {settings.KnowledgeDashboardHtmlPath}
Markdown index: {settings.KnowledgeDashboardPath}
Telegram alerts: enabled; proactive reminders and alerts still arrive in this Telegram chat.
""";
    }

    private string StatusSectionText()
    {
        return $"""
Status

Local-first mode: enabled
Time zone: {settings.TimeZone.Id}
Max context memories: {settings.MaxContextMemories}
Max reply characters: {settings.MaxReplyCharacters}
Medical safety boundary: enabled
Local static UI: {settings.KnowledgeDashboardHtmlPath}
Markdown index: {settings.KnowledgeDashboardPath}
Telegram alerts: enabled; proactive reminders and alerts still arrive in this Telegram chat.
""";
    }

    private string SettingsSectionText()
    {
        return $"""
Settings

Local-first mode: enabled
Time zone: {settings.TimeZone.Id}
Max context memories: {settings.MaxContextMemories}
Max reply characters: {settings.MaxReplyCharacters}
Medical safety boundary: enabled
Local static UI: {settings.KnowledgeDashboardHtmlPath}
Markdown index: {settings.KnowledgeDashboardPath}
Telegram alerts: enabled; proactive reminders and alerts still arrive in this Telegram chat.

Useful commands:
- /dashboard — show local UI and Markdown paths
- /status — show runtime and Telegram alert status

Secrets are loaded from configuration/environment and are never shown here.
""";
    }

    private static string FormatMemoryFolder(string heading, IReadOnlyList<MemoryItem> memories)
    {
        if (memories.Count == 0)
        {
            return $"{heading}\n\nNo {heading.ToLowerInvariant()} saved yet.";
        }

        return heading + "\n\n" + string.Join("\n", memories.Select(memory => $"- {memory.Id}: {memory.Title} — {memory.Content}"));
    }

    private static string FormatHealthSection(string summary) => $"Health\n\n{summary}";

    private static string FormatMemories(string heading, IReadOnlyList<MemoryItem> memories)
    {
        if (memories.Count == 0)
        {
            return $"No {heading.ToLowerInvariant()} saved yet.";
        }

        return heading + ":\n" + string.Join("\n", memories.Select(memory => $"- {memory.Id}: {memory.Title} — {memory.Content}"));
    }

    private string FormatRemindersSection(IReadOnlyList<Reminder> reminders)
    {
        return """
Reminders

Use this folder for reminders and alerts.

Commands:
- Send a normal reminder request, for example: remind me tomorrow at 10 to stretch
- /reminders — refresh pending reminders
- /cancel <guid> — cancel a reminder
- /today — today's reminders and new memories

""" + FormatReminders(reminders);
    }

    private string FormatReminders(IReadOnlyList<Reminder> reminders)
    {
        var ordered = reminders.OrderBy(reminder => reminder.DueAtUtc).ToArray();
        if (ordered.Length == 0)
        {
            return "No pending reminders.";
        }

        return "Pending reminders:\n" + string.Join("\n", ordered.Select(reminder => $"- {reminder.Id}: {reminder.Title} at {FormatLocalDateTime(reminder.DueAtUtc)} ({reminder.RepeatKind})"));
    }

    private static string FormatMemoryContext(IEnumerable<MemoryItem> memories)
    {
        var lines = memories.Select(memory =>
            $"- id={memory.Id}; category={memory.Category}; title={memory.Title}; subject={memory.Subject ?? string.Empty}; tags={string.Join(",", memory.Tags)}; confidence={memory.Confidence:0.00}; content={memory.Content}");
        var context = string.Join("\n", lines);
        return string.IsNullOrWhiteSpace(context) ? "(none)" : context;
    }

    private string FormatReminderContext(IEnumerable<Reminder> reminders)
    {
        var lines = reminders.Select(reminder => $"- id={reminder.Id}; title={reminder.Title}; dueLocal={FormatLocalDateTime(reminder.DueAtUtc)}; repeat={reminder.RepeatKind}");
        var context = string.Join("\n", lines);
        return string.IsNullOrWhiteSpace(context) ? "(none)" : context;
    }

    private static string FormatRelated(IReadOnlyList<MemoryItem> memories)
    {
        return string.Join("; ", memories.Select(memory => $"{memory.Title} ({memory.Category})"));
    }

    private static IReadOnlyList<string> CleanTags(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return [];
        }

        return tags
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
    }

    private static double ClampConfidence(double confidence)
    {
        if (double.IsNaN(confidence) || double.IsInfinity(confidence))
        {
            return 0.5;
        }

        return Math.Clamp(confidence, 0.0, 1.0);
    }

    private static string? TrimToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string FirstCharacters(string value, int maxLength)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string BuildDocumentClassificationText(string documentName, string content)
    {
        var snippet = FirstCharacters(content, DocumentClassificationSnippetCharacters);
        return $"""
        Imported local document: {documentName}
        Only this bounded excerpt is being sent for classification; the full extracted text is stored only as a local raw capture.

        Excerpt:
        {snippet}
        """;
    }

    private static string BuildDocumentFallbackMemoryContent(string documentName, string sourcePath, string content)
    {
        var excerpt = FirstCharacters(content, DocumentFallbackExcerptCharacters);
        return $"""
        Imported document "{documentName}" into Mira memory.
        Full extracted text is stored only in the local raw capture: {sourcePath}
        Bounded excerpt:
        {excerpt}
        """;
    }

    private static string SafeDocumentName(string documentName)
    {
        return string.IsNullOrWhiteSpace(documentName) ? "document" : Path.GetFileName(documentName.Trim());
    }

    private static bool IsKnownIntent(string intent) => KnownIntents.Contains(NormalizeIntent(intent), StringComparer.Ordinal);

    private static string NormalizeIntent(string intent) => intent.Trim().ToLowerInvariant();

    private static bool IsCommand(string text, string command)
    {
        return text.Equals(command, StringComparison.OrdinalIgnoreCase)
            || text.StartsWith(command + " ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetCommandArgument(string text, string command, out string argument)
    {
        if (text.Equals(command, StringComparison.OrdinalIgnoreCase))
        {
            argument = string.Empty;
            return true;
        }

        if (text.StartsWith(command + " ", StringComparison.OrdinalIgnoreCase))
        {
            argument = text[command.Length..].Trim();
            return true;
        }

        argument = string.Empty;
        return false;
    }

    private static Guid? ExtractGuid(string text)
    {
        foreach (var part in text.Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(part, out var id))
            {
                return id;
            }
        }

        return null;
    }

    private static string HelpText()
    {
        return """
Mira local assistant commands:
/start or /menu — show folder menu
/help — show this detailed command list
/chat — open simple AI chat folder
/reminders — open reminders folder and list pending reminders
/memory — open saved memory folder
/dashboard — show local static UI and Markdown dashboard paths
/notes — open daily notes folder
/settings — show local runtime status
/status — show local runtime and Telegram alert status
/capture <text> — save raw text and extract memory
/remember <text> — save raw text and extract memory
/note <text> — save a deterministic daily note
/search <text> — search saved memories
/today — show today's reminders and new memories
/profile <name> — summarize saved memories about a person
/uncertain — list low-confidence memories to review
/stale — list memories older than 90 days
/brief — generate today's local daily brief
/review — generate this week's review
/people — list saved people
/decisions — list saved decisions
/health — summarize recent health entries
/forget <guid> — delete a memory item
/cancel <guid> — cancel a reminder
/confirm <guid> — run a staged local automation
""";
    }

    private sealed record AssistantAction
    {
        public string Intent { get; init; } = string.Empty;

        public MemoryCategory? Category { get; init; }

        public string? Title { get; init; }

        public string? Subject { get; init; }

        public string? Content { get; init; }

        public IReadOnlyList<string>? Tags { get; init; }

        public double Confidence { get; init; }

        public DateTimeOffset? DueLocal { get; init; }

        public ReminderRepeatKind? Repeat { get; init; }

        public string? AutomationName { get; init; }

        public IReadOnlyDictionary<string, string>? AutomationArguments { get; init; }

        public string? Question { get; init; }
    }
}
