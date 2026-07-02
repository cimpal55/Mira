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

        await memoryStore.SaveConversationMessageAsync(
            new ConversationMessage(
                message.ChatId,
                message.MessageId,
                ConversationDirection.Incoming,
                text,
                ToUtc(message.ReceivedAt)),
            cancellationToken).ConfigureAwait(false);

        var commandReply = await TryHandleCommandAsync(message, text, cancellationToken).ConfigureAwait(false);
        if (commandReply is not null)
        {
            return commandReply;
        }

        var action = await ClassifyAsync(text, forceSaveMemory: false, cancellationToken).ConfigureAwait(false);
        string replyText;
        if (action is null || !IsKnownIntent(action.Intent))
        {
            var answer = await AnswerWithContextAsync(text, appendStructuringFailure: true, cancellationToken).ConfigureAwait(false);
            replyText = answer;
        }
        else
        {
            replyText = await HandleActionAsync(message, text, action, cancellationToken).ConfigureAwait(false);
        }

        return await ReplyAsync(message, replyText, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AssistantReply?> TryHandleCommandAsync(IncomingMessage message, string text, CancellationToken cancellationToken)
    {
        if (IsCommand(text, "/start") || IsCommand(text, "/help"))
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
            var action = await ClassifyAsync(captureText, forceSaveMemory: true, cancellationToken).ConfigureAwait(false);
            var saved = await SaveCapturedMemoryAsync(captureText, sourcePath, message.MessageId, action, cancellationToken).ConfigureAwait(false);
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
            return await ReplyAsync(message, FormatMemories("People", people), cancellationToken).ConfigureAwait(false);
        }

        if (IsCommand(text, "/decisions"))
        {
            var decisions = await memoryStore.GetByCategoryAsync(MemoryCategory.Decision, 20, cancellationToken).ConfigureAwait(false);
            return await ReplyAsync(message, FormatMemories("Decisions", decisions), cancellationToken).ConfigureAwait(false);
        }

        if (IsCommand(text, "/health"))
        {
            return await ReplyAsync(message, await GenerateHealthSummaryAsync(text, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        if (IsCommand(text, "/reminders"))
        {
            var reminders = await reminderStore.GetPendingAsync(20, cancellationToken).ConfigureAwait(false);
            return await ReplyAsync(message, FormatReminders(reminders), cancellationToken).ConfigureAwait(false);
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
        return NormalizeIntent(action.Intent) switch
        {
            "save_memory" => await SaveMemoryIntentAsync(message.MessageId, text, action, cancellationToken).ConfigureAwait(false),
            "answer" or "chat" => await AnswerWithContextAsync(text, appendStructuringFailure: false, cancellationToken).ConfigureAwait(false),
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
            _ => await AnswerWithContextAsync(text, appendStructuringFailure: true, cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<AssistantAction?> ClassifyAsync(string text, bool forceSaveMemory, CancellationToken cancellationToken)
    {
        var prompt = BuildClassificationPrompt(forceSaveMemory);
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

    private string BuildClassificationPrompt(bool forceSaveMemory)
    {
        var localNow = TimeZoneInfo.ConvertTime(clock.UtcNow, settings.TimeZone);
        var categories = string.Join(", ", Enum.GetNames<MemoryCategory>());
        var forceInstruction = forceSaveMemory
            ? "The user explicitly asked to capture this. Prefer intent save_memory and extract durable facts."
            : "Choose the safest intent. If the message is ambiguous, use clarify.";

        return $$"""
You classify one private local-assistant message into exactly one action.
Current local time: {{localNow:O}}
Valid MemoryCategory names: {{categories}}
Intent must be exactly one of: save_memory, answer, create_reminder, list_reminders, complete_reminder, run_automation, daily_brief, weekly_review, health_summary, clarify, chat.
{{forceInstruction}}
Use save_memory for durable facts about people/friends/health/medical/job/hobby/gear/thought/decision/daily notes.
Use create_reminder only when a concrete local due date/time can be inferred.
Use run_automation only when the user names an allowlisted local task and provides required arguments.
For medical or medicine content, classify facts as Medical when they concern medication, diagnosis, dosage, clinician instructions, or treatment.
Return JSON only. Do not include markdown.
JSON properties: Intent, Category, Title, Subject, Content, Tags, Confidence, DueLocal, Repeat, AutomationName, AutomationArguments, Question.
""";
    }

    private async Task<MemoryItem> SaveCapturedMemoryAsync(
        string rawText,
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

        var title = FirstCharacters(rawText, 80);
        return await memoryStore.UpsertAsync(
            new MemoryUpsert(
                MemoryCategory.General,
                title,
                rawText,
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

    private async Task<string> AnswerWithContextAsync(string text, bool appendStructuringFailure, CancellationToken cancellationToken)
    {
        var memories = await memoryStore.SearchAsync(
            new MemorySearchQuery(text, [], settings.MaxContextMemories),
            cancellationToken).ConfigureAwait(false);

        string answer;
        if (memories.Count == 0)
        {
            answer = await AnswerWithoutPersonalContextAsync(text, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var context = FormatMemoryContext(memories);
            var system = $$"""
You are Mira, a private local-first assistant.
Answer using only the retrieved personal context and the current user message.
If the context does not support a claim, say so briefly.
For medical or medicine content, do not recommend changing dose, stopping medicine, ignoring a clinician, or making treatment decisions.
Retrieved personal context:
{{context}}
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

    private async Task<string> AnswerWithoutPersonalContextAsync(string text, CancellationToken cancellationToken)
    {
        var system = """
You are Mira, a private local-first assistant running on the user's PC.
No saved personal context matched this message.
If the user asks for personal facts, preferences, memories, relationships, health history, reminders, or decisions, say you do not know yet and ask what should be saved.
If the user asks a general question, wants brainstorming, or needs help drafting text, answer normally using local model knowledge.
Do not pretend that unsaved personal facts are known.
For medical or medicine content, do not recommend changing dose, stopping medicine, ignoring a clinician, or making treatment decisions.
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

    private static DateTimeOffset ToUtc(DateTimeOffset value) => value.ToUniversalTime();

    private static string FormatMemories(string heading, IReadOnlyList<MemoryItem> memories)
    {
        if (memories.Count == 0)
        {
            return $"No {heading.ToLowerInvariant()} saved yet.";
        }

        return heading + ":\n" + string.Join("\n", memories.Select(memory => $"- {memory.Id}: {memory.Title} — {memory.Content}"));
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
/start or /help — show this list
/capture <text> — save raw text and extract memory
/remember <text> — save raw text and extract memory
/search <text> — search saved memories
/today — show today's reminders and new memories
/brief — generate today's local daily brief
/review — generate this week's review
/people — list saved people
/decisions — list saved decisions
/health — summarize recent health entries
/reminders — list pending reminders
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
