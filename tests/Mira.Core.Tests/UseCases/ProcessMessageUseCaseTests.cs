namespace Mira.Core.Tests.UseCases;

using Mira.Core.Interfaces;
using Mira.Core.Models;
using Mira.Core.UseCases;
using Xunit;

public sealed class ProcessMessageUseCaseTests
{
    private const string MedicalBoundary = "I can organize your saved medical information, but I cannot make medical decisions or change treatment. Confirm medication and dosage questions with a qualified clinician.";

    [Fact]
    public async Task CaptureCommand_saves_raw_capture_and_general_memory_when_classifier_fails()
    {
        var llm = new FakeLlmProvider("not json");
        var memory = new FakeMemoryStore();
        var useCase = CreateUseCase(llm, memory);

        var reply = await useCase.HandleAsync(Message("/capture my friend Maxim likes mechanical keyboards"), TestContext.Current.CancellationToken);

        Assert.StartsWith("Saved:", reply.Text);
        Assert.Single(memory.RawCaptures);
        var saved = Assert.Single(memory.Upserts);
        Assert.Equal(MemoryCategory.General, saved.Category);
        Assert.NotNull(saved.SourcePath);
    }

    [Fact]
    public async Task CaptureCommand_saves_source_capture_and_links_memory()
    {
        var input = "/capture my friend Maxim likes mechanical keyboards";
        var llm = new FakeLlmProvider("not json");
        var memory = new FakeMemoryStore();
        var sourceStore = new FakeSourceStore();
        var useCase = CreateUseCase(llm, memory, sourceStore);

        var reply = await useCase.HandleAsync(Message(input), TestContext.Current.CancellationToken);

        Assert.Contains("Saved", reply.Text, StringComparison.Ordinal);
        var capture = Assert.Single(sourceStore.Captures);
        Assert.Equal(SourceKind.TelegramMessage, capture.Kind);
        Assert.Equal(input, capture.ContentText);
        Assert.Equal(capture.Id, memory.LastSourceCaptureId);
    }

    [Fact]
    public async Task InboxCommand_lists_unprocessed_source_captures()
    {
        var sourceStore = new FakeSourceStore();
        await sourceStore.SaveSourceCaptureAsync(new SourceCaptureCreate(
            SourceKind.TelegramMessage,
            "Maxim keyboard preference",
            "Maxim likes mechanical keyboards",
            new DateTimeOffset(2026, 7, 1, 5, 0, 0, TimeSpan.Zero)), TestContext.Current.CancellationToken);
        var useCase = CreateUseCase(sourceStore: sourceStore);

        var reply = await useCase.HandleAsync(Message("/inbox"), TestContext.Current.CancellationToken);

        Assert.StartsWith("Inbox captures:", reply.Text, StringComparison.Ordinal);
        Assert.Contains("Maxim keyboard preference", reply.Text, StringComparison.Ordinal);
        Assert.Contains("TelegramMessage [Unprocessed]", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourcesCommand_lists_recent_source_captures()
    {
        var sourceStore = new FakeSourceStore();
        var processed = await sourceStore.SaveSourceCaptureAsync(new SourceCaptureCreate(
            SourceKind.TelegramMessage,
            "Processed keyboard note",
            "Processed content",
            new DateTimeOffset(2026, 7, 1, 4, 0, 0, TimeSpan.Zero)), TestContext.Current.CancellationToken);
        await sourceStore.MarkSourceCaptureProcessedAsync(processed.Id, new DateTimeOffset(2026, 7, 1, 4, 5, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);
        await sourceStore.SaveSourceCaptureAsync(new SourceCaptureCreate(
            SourceKind.TelegramMessage,
            "Unprocessed keyboard note",
            "Unprocessed content",
            new DateTimeOffset(2026, 7, 1, 5, 0, 0, TimeSpan.Zero)), TestContext.Current.CancellationToken);
        var useCase = CreateUseCase(sourceStore: sourceStore);

        var reply = await useCase.HandleAsync(Message("/sources"), TestContext.Current.CancellationToken);

        Assert.StartsWith("Recent source captures:", reply.Text, StringComparison.Ordinal);
        Assert.Contains("Processed keyboard note", reply.Text, StringComparison.Ordinal);
        Assert.Contains("Unprocessed keyboard note", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InboxCommand_returns_empty_message_when_no_unprocessed_captures()
    {
        var useCase = CreateUseCase();

        var reply = await useCase.HandleAsync(Message("/inbox"), TestContext.Current.CancellationToken);

        Assert.Equal("Inbox is empty. New Telegram text messages will appear here before processing.", reply.Text);
    }

    [Fact]
    public async Task RememberCommand_saves_general_memory_when_classifier_fails()
    {
        var llm = new FakeLlmProvider("not json");
        var memory = new FakeMemoryStore();
        var useCase = CreateUseCase(llm, memory);

        var reply = await useCase.HandleAsync(Message("/remember my friend Maxim likes mechanical keyboards"), TestContext.Current.CancellationToken);

        Assert.StartsWith("Saved:", reply.Text);
        var saved = Assert.Single(memory.Upserts);
        Assert.Equal(MemoryCategory.General, saved.Category);
        Assert.NotNull(saved.SourcePath);
    }

    [Fact]
    public async Task SearchCommand_returns_matching_memories_without_llm()
    {
        var llm = new FakeLlmProvider();
        var memory = new FakeMemoryStore
        {
            SearchHandler = query =>
            {
                Assert.Equal("Maxim keyboard", query.Text);
                Assert.Equal(10, query.Limit);
                return [Memory("Maxim keyboards", "Maxim likes mechanical keyboards", MemoryCategory.Person)];
            }
        };
        var useCase = CreateUseCase(llm, memory);

        var reply = await useCase.HandleAsync(Message("/search Maxim keyboard"), TestContext.Current.CancellationToken);

        Assert.Contains("Search results:", reply.Text);
        Assert.Contains("Maxim likes mechanical keyboards", reply.Text);
        Assert.Empty(llm.Requests);
    }

    [Fact]
    public async Task OsCommand_returns_personal_operating_system_map_without_llm()
    {
        var llm = new FakeLlmProvider();
        var useCase = CreateUseCase(llm);

        var reply = await useCase.HandleAsync(Message("/os"), TestContext.Current.CancellationToken);

        Assert.Contains("Mira personal operating system", reply.Text);
        Assert.Contains("Prompt memory", reply.Text);
        Assert.Contains("Episodic memory", reply.Text);
        Assert.Contains("Semantic memory", reply.Text);
        Assert.Contains("Procedural memory", reply.Text);
        Assert.Contains("Scout", reply.Text);
        Assert.Contains("Refinery", reply.Text);
        Assert.Contains("Cartographer", reply.Text);
        Assert.Contains("Critic", reply.Text);
        Assert.Contains("Editor", reply.Text);
        Assert.Contains("0-dashboard/", reply.Text);
        Assert.Contains("_system/skills/", reply.Text);
        Assert.Empty(llm.Requests);
    }


    [Fact]
    public async Task TodayCommand_lists_due_reminders_and_new_memories()
    {
        var dueToday = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var dueTomorrow = Guid.Parse("40000000-0000-0000-0000-000000000002");
        var memory = new FakeMemoryStore
        {
            GetRecentHandler = (_, sinceUtc) =>
            {
                Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), sinceUtc);
                return [Memory("Breakfast", "Ate oats and eggs", MemoryCategory.Health)];
            }
        };
        var reminders = new FakeReminderStore
        {
            GetPendingHandler = _ =>
            [
                new Reminder(dueToday, "Stretch", null, new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero), ReminderRepeatKind.None, ReminderStatus.Pending, DateTimeOffset.UtcNow, null, null),
                new Reminder(dueTomorrow, "Tomorrow task", null, new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.Zero), ReminderRepeatKind.None, ReminderStatus.Pending, DateTimeOffset.UtcNow, null, null)
            ]
        };
        var useCase = CreateUseCase(new FakeLlmProvider(), memory, reminderStore: reminders);

        var reply = await useCase.HandleAsync(Message("/today"), TestContext.Current.CancellationToken);

        Assert.Contains("Today (2026-07-01)", reply.Text);
        Assert.Contains("Stretch", reply.Text);
        Assert.Contains("Ate oats and eggs", reply.Text);
        Assert.DoesNotContain("Tomorrow task", reply.Text);
    }

    [Fact]
    public async Task MenuCommand_returns_folder_menu_without_llm()
    {
        var llm = new FakeLlmProvider();
        var useCase = CreateUseCase(llm);

        var menu = await useCase.HandleAsync(Message("/menu"), TestContext.Current.CancellationToken);
        var start = await useCase.HandleAsync(Message("/start"), TestContext.Current.CancellationToken);

        Assert.Contains("Mira folders", menu.Text);
        Assert.Contains("/chat", menu.Text);
        Assert.Contains("/reminders", menu.Text);
        Assert.Contains("/memory", menu.Text);
        Assert.Equal(menu.Text, start.Text);
        Assert.Empty(llm.Requests);
    }

    [Fact]
    public async Task HelpCommand_includes_folder_commands()
    {
        var useCase = CreateUseCase();

        var reply = await useCase.HandleAsync(Message("/help"), TestContext.Current.CancellationToken);

        Assert.Contains("/menu", reply.Text);
        Assert.Contains("/chat", reply.Text);
        Assert.Contains("/memory", reply.Text);
        Assert.Contains("/notes", reply.Text);
        Assert.Contains("/settings", reply.Text);
        Assert.Contains("/remember <text>", reply.Text);
    }

    [Fact]
    public async Task FolderCommands_return_deterministic_sections_without_llm()
    {
        var llm = new FakeLlmProvider();
        var useCase = CreateUseCase(llm);

        var chat = await useCase.HandleAsync(Message("/chat"), TestContext.Current.CancellationToken);
        var memory = await useCase.HandleAsync(Message("/memory"), TestContext.Current.CancellationToken);
        var notes = await useCase.HandleAsync(Message("/notes"), TestContext.Current.CancellationToken);
        var settings = await useCase.HandleAsync(Message("/settings"), TestContext.Current.CancellationToken);

        Assert.StartsWith("AI Chat", chat.Text);
        Assert.Contains("/remember <text>", memory.Text);
        Assert.StartsWith("Daily Notes", notes.Text);
        Assert.Contains("Local-first mode: enabled", settings.Text);
        Assert.Empty(llm.Requests);
    }

    [Fact]
    public async Task RemindersCommand_returns_folder_dashboard_with_pending_reminders()
    {
        var id = Guid.Parse("50000000-0000-0000-0000-000000000001");
        var reminders = new FakeReminderStore
        {
            GetPendingHandler = limit =>
            {
                Assert.Equal(20, limit);
                return [new Reminder(id, "Stretch", null, new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero), ReminderRepeatKind.None, ReminderStatus.Pending, DateTimeOffset.UtcNow, null, null)];
            }
        };
        var useCase = CreateUseCase(new FakeLlmProvider(), reminderStore: reminders);

        var reply = await useCase.HandleAsync(Message("/reminders"), TestContext.Current.CancellationToken);

        Assert.StartsWith("Reminders", reply.Text);
        Assert.Contains("/cancel <guid>", reply.Text);
        Assert.Contains("Pending reminders:", reply.Text);
        Assert.Contains("Stretch", reply.Text);
    }


    [Fact]
    public async Task NoteCommand_saves_daily_note_without_llm()
    {
        var llm = new FakeLlmProvider();
        var memory = new FakeMemoryStore();
        var useCase = CreateUseCase(llm, memory);

        var reply = await useCase.HandleAsync(Message("/note hit a PR on bench press today"), TestContext.Current.CancellationToken);

        Assert.StartsWith("Daily note saved:", reply.Text);
        Assert.Single(memory.RawCaptures);
        var saved = Assert.Single(memory.Upserts);
        Assert.Equal(MemoryCategory.DailyNote, saved.Category);
        Assert.Contains("daily-note", saved.Tags);
        Assert.NotNull(saved.SourcePath);
        Assert.Empty(llm.Requests);
    }

    [Fact]
    public async Task ProfileCommand_summarizes_subject_and_person_memories()
    {
        var llm = new FakeLlmProvider("Profile for Maxim.");
        var memory = new FakeMemoryStore
        {
            GetBySubjectHandler = (subject, limit) =>
            {
                Assert.Equal("Maxim", subject);
                Assert.Equal(20, limit);
                return [Memory("Maxim coffee", "Maxim likes dark roast coffee", MemoryCategory.Person)];
            },
            SearchHandler = query =>
            {
                Assert.Equal("Maxim", query.Text);
                Assert.Contains(MemoryCategory.Person, query.Categories);
                return [Memory("Maxim keyboards", "Maxim likes tactile keyboard switches", MemoryCategory.Person)];
            }
        };
        var useCase = CreateUseCase(llm, memory);

        var reply = await useCase.HandleAsync(Message("/profile Maxim"), TestContext.Current.CancellationToken);

        Assert.Equal("Profile for Maxim.", reply.Text);
        var request = Assert.Single(llm.Requests);
        Assert.Contains("Maxim likes dark roast coffee", request.Messages[0].Content);
        Assert.Contains("Maxim likes tactile keyboard switches", request.Messages[0].Content);
    }

    [Fact]
    public async Task MemoryReviewCommands_list_low_confidence_and_stale_memories()
    {
        var memory = new FakeMemoryStore
        {
            GetLowConfidenceHandler = (maxConfidence, limit) =>
            {
                Assert.Equal(0.6, maxConfidence);
                Assert.Equal(20, limit);
                return [Memory("Maybe Maxim", "May prefer tactile switches", MemoryCategory.Person)];
            },
            GetStaleHandler = (olderThanUtc, limit) =>
            {
                Assert.Equal(new DateTimeOffset(2026, 4, 2, 6, 0, 0, TimeSpan.Zero), olderThanUtc);
                Assert.Equal(20, limit);
                return [Memory("Old gear", "Old laptop preference", MemoryCategory.Gear)];
            }
        };
        var useCase = CreateUseCase(new FakeLlmProvider(), memory);

        var uncertain = await useCase.HandleAsync(Message("/uncertain"), TestContext.Current.CancellationToken);
        var stale = await useCase.HandleAsync(Message("/stale"), TestContext.Current.CancellationToken);

        Assert.Contains("May prefer tactile switches", uncertain.Text);
        Assert.Contains("Old laptop preference", stale.Text);
    }

    [Fact]
    public async Task AnswerIntent_includes_recent_dialogue_for_follow_up_context()
    {
        var previousUserMessage = new ConversationMessage(
            123,
            400,
            ConversationDirection.Incoming,
            "Write a birthday message for Maxim.",
            new DateTimeOffset(2026, 7, 1, 5, 58, 0, TimeSpan.Zero));
        var previousReply = new ConversationMessage(
            123,
            0,
            ConversationDirection.Outgoing,
            "Happy birthday draft for Maxim.",
            new DateTimeOffset(2026, 7, 1, 5, 59, 0, TimeSpan.Zero));
        var llm = new FakeLlmProvider("{\"Intent\":\"answer\"}", "Shorter birthday draft.");
        var memory = new FakeMemoryStore
        {
            ConversationHandler = (chatId, limit, beforeUtc) =>
            {
                Assert.Equal(123, chatId);
                Assert.Equal(12, limit);
                Assert.Equal(new DateTimeOffset(2026, 7, 1, 6, 0, 0, TimeSpan.Zero), beforeUtc);
                return [previousUserMessage, previousReply];
            }
        };
        var useCase = CreateUseCase(llm, memory);

        var reply = await useCase.HandleAsync(Message("make it shorter"), TestContext.Current.CancellationToken);

        Assert.Equal("Shorter birthday draft.", reply.Text);
        var answerRequest = llm.Requests[1];
        Assert.Contains("Recent dialogue before current message:", answerRequest.Messages[0].Content);
        Assert.Contains("Write a birthday message for Maxim.", answerRequest.Messages[0].Content);
        Assert.Contains("Happy birthday draft for Maxim.", answerRequest.Messages[0].Content);
    }



    [Fact]
    public async Task SaveMemoryIntent_returns_related_existing_memories()
    {
        var llm = new FakeLlmProvider("""
            {"Intent":"save_memory","Category":"Person","Title":"Maxim keyboards","Content":"Maxim likes mechanical keyboards","Subject":"Maxim","Tags":["friend"],"Confidence":0.9}
            """);
        var memory = new FakeMemoryStore
        {
            SearchHandler = query =>
            [
                Memory("Existing Maxim", "Maxim also likes artisan keycaps", MemoryCategory.Person)
            ]
        };
        var useCase = CreateUseCase(llm, memory);

        var reply = await useCase.HandleAsync(Message("Maxim likes mechanical keyboards"), TestContext.Current.CancellationToken);

        Assert.Contains("Saved to Person: Maxim keyboards", reply.Text, StringComparison.Ordinal);
        Assert.Contains("Related:", reply.Text);
    }

    [Fact]
    public async Task PlainTextDurableFact_saves_to_classifier_category_and_marks_source_processed()
    {
        var llm = new FakeLlmProvider("""
            {"Intent":"save_memory","Category":"Person","Title":"Maxim keyboards","Content":"Maxim likes mechanical keyboards","Subject":"Maxim","Tags":["friend","keyboard"],"Confidence":0.9}
            """);
        var memory = new FakeMemoryStore();
        var sourceStore = new FakeSourceStore();
        var useCase = CreateUseCase(llm, memory, sourceStore);

        var reply = await useCase.HandleAsync(Message("my friend Maxim likes mechanical keyboards"), TestContext.Current.CancellationToken);

        Assert.StartsWith("Saved to Person: Maxim keyboards", reply.Text, StringComparison.Ordinal);
        var upsert = Assert.Single(memory.Upserts);
        Assert.Equal(MemoryCategory.Person, upsert.Category);
        var sourceCapture = Assert.Single(sourceStore.Captures);
        Assert.Equal(sourceCapture.Id, memory.LastSourceCaptureId);
        Assert.Equal(SourceProcessingStatus.Processed, sourceCapture.Status);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 6, 0, 0, TimeSpan.Zero), sourceCapture.ProcessedAtUtc);
    }

    [Fact]
    public async Task AnswerIntent_marks_source_processed_without_saving_memory()
    {
        var llm = new FakeLlmProvider(
            "{\"Intent\":\"answer\"}",
            "A prime number is divisible only by 1 and itself.");
        var memory = new FakeMemoryStore();
        var sourceStore = new FakeSourceStore();
        var useCase = CreateUseCase(llm, memory, sourceStore);

        var reply = await useCase.HandleAsync(Message("What is a prime number?"), TestContext.Current.CancellationToken);

        Assert.Equal("A prime number is divisible only by 1 and itself.", reply.Text);
        Assert.Empty(memory.Upserts);
        var sourceCapture = Assert.Single(sourceStore.Captures);
        Assert.Equal(SourceProcessingStatus.Processed, sourceCapture.Status);
    }

    [Fact]
    public async Task InvalidClassifierJson_leaves_source_unprocessed_for_inbox_review()
    {
        var llm = new FakeLlmProvider("not json", "Fallback answer.");
        var memory = new FakeMemoryStore();
        var sourceStore = new FakeSourceStore();
        var useCase = CreateUseCase(llm, memory, sourceStore);

        var reply = await useCase.HandleAsync(Message("Maxim probably likes split keyboards"), TestContext.Current.CancellationToken);

        Assert.Contains("I could not structure this reliably", reply.Text, StringComparison.Ordinal);
        Assert.Empty(memory.Upserts);
        var sourceCapture = Assert.Single(sourceStore.Captures);
        Assert.Equal(SourceProcessingStatus.Unprocessed, sourceCapture.Status);
    }

    [Fact]
    public async Task ClarifyIntent_leaves_source_unprocessed()
    {
        var llm = new FakeLlmProvider("""
            {"Intent":"clarify","Question":"Should I save this as a memory or just answer?"}
            """);
        var sourceStore = new FakeSourceStore();
        var useCase = CreateUseCase(llm, sourceStore: sourceStore);

        var reply = await useCase.HandleAsync(Message("Maxim maybe"), TestContext.Current.CancellationToken);

        Assert.Equal("Should I save this as a memory or just answer?", reply.Text);
        var sourceCapture = Assert.Single(sourceStore.Captures);
        Assert.Equal(SourceProcessingStatus.Unprocessed, sourceCapture.Status);
    }

    [Fact]
    public async Task AnswerIntent_retrieves_memory_and_sends_context_to_llm()
    {
        var llm = new FakeLlmProvider(
            "{\"Intent\":\"answer\"}",
            "Keyboard gift ideas under 50 EUR.");
        var memory = new FakeMemoryStore
        {
            SearchHandler = query => [Memory("Maxim keyboards", "Maxim likes mechanical keyboards and artisan keycaps", MemoryCategory.Person)]
        };
        var useCase = CreateUseCase(llm, memory);

        var reply = await useCase.HandleAsync(Message("What can I gift Maxim under 50 EUR?"), TestContext.Current.CancellationToken);

        Assert.Equal("Keyboard gift ideas under 50 EUR.", reply.Text);
        Assert.Contains(llm.Requests.Skip(1), request => request.Messages.Any(message => message.Content.Contains("Maxim likes mechanical keyboards", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AnswerIntent_without_memory_uses_local_llm_for_general_questions()
    {
        var llm = new FakeLlmProvider("{\"Intent\":\"answer\"}", "A prime number is divisible only by 1 and itself.");
        var useCase = CreateUseCase(llm, new FakeMemoryStore());

        var reply = await useCase.HandleAsync(Message("What is a prime number?"), TestContext.Current.CancellationToken);

        Assert.Equal("A prime number is divisible only by 1 and itself.", reply.Text);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains(llm.Requests[1].Messages, message => message.Content.Contains("No saved personal context matched", StringComparison.Ordinal));
    }


    [Fact]
    public async Task CreateReminder_missing_due_time_asks_clarifying_question()
    {
        var llm = new FakeLlmProvider("{\"Intent\":\"create_reminder\",\"Title\":\"Stretch\"}");
        var reminders = new FakeReminderStore();
        var useCase = CreateUseCase(llm, reminderStore: reminders);

        var reply = await useCase.HandleAsync(Message("remind me to stretch"), TestContext.Current.CancellationToken);

        Assert.Contains("When should I remind you", reply.Text);
        Assert.Empty(reminders.Created);
    }

    [Fact]
    public async Task CreateReminder_valid_due_time_saves_utc_due_time()
    {
        var llm = new FakeLlmProvider("{\"Intent\":\"create_reminder\",\"Title\":\"Stretch\",\"DueLocal\":\"2026-07-01T10:00:00+02:00\",\"Repeat\":\"None\"}");
        var reminders = new FakeReminderStore();
        var timeZone = TimeZoneInfo.CreateCustomTimeZone("UTC+02", TimeSpan.FromHours(2), "UTC+02", "UTC+02");
        var clock = new FakeClock(new DateTimeOffset(2026, 7, 1, 6, 0, 0, TimeSpan.Zero));
        var useCase = CreateUseCase(llm, reminderStore: reminders, clock: clock, timeZone: timeZone);

        await useCase.HandleAsync(Message("remind me at 10 to stretch"), TestContext.Current.CancellationToken);

        var created = Assert.Single(reminders.Created);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero), created.DueAtUtc);
    }

    [Fact]
    public async Task MedicalAnswer_includes_medical_boundary_message()
    {
        var llm = new FakeLlmProvider("{\"Intent\":\"answer\"}", "Your saved dose is listed in memory.");
        var memory = new FakeMemoryStore
        {
            SearchHandler = query => [Memory("Medication card", "Saved medication dose card", MemoryCategory.Medical)]
        };
        var useCase = CreateUseCase(llm, memory);

        var reply = await useCase.HandleAsync(Message("What is on my medicine card?"), TestContext.Current.CancellationToken);

        Assert.StartsWith(MedicalBoundary, reply.Text);
    }

    [Fact]
    public async Task HealthSummary_includes_medical_boundary_for_health_memory_with_medicine_fact()
    {
        var llm = new FakeLlmProvider("Saved health summary mentions the prescription dose.");
        var memory = new FakeMemoryStore
        {
            GetByCategoryHandler = (category, _) => category == MemoryCategory.Health
                ? [Memory("Medication log", "Took the prescribed dose after breakfast", MemoryCategory.Health)]
                : []
        };
        var useCase = CreateUseCase(llm, memory);

        var reply = await useCase.HandleAsync(Message("/health"), TestContext.Current.CancellationToken);

        Assert.StartsWith(MedicalBoundary, reply.Text);
    }


    [Fact]
    public async Task AutomationRequest_creates_pending_confirmation_without_running()
    {
        var id = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var llm = new FakeLlmProvider("{\"Intent\":\"run_automation\",\"AutomationName\":\"echo_test\",\"AutomationArguments\":{\"text\":\"hello\"}}");
        var automationStore = new FakeAutomationStore(id);
        var runner = new FakeAutomationRunner();
        var useCase = CreateUseCase(llm, automationStore: automationStore, automationRunner: runner);

        var reply = await useCase.HandleAsync(Message("run echo_test with text hello"), TestContext.Current.CancellationToken);

        Assert.Contains($"/confirm {id}", reply.Text);
        Assert.Single(automationStore.Saved);
        Assert.False(runner.WasCalled);
    }

    [Fact]
    public async Task AutomationRequest_rejected_by_runner_is_not_staged()
    {
        var llm = new FakeLlmProvider("{\"Intent\":\"run_automation\",\"AutomationName\":\"unknown_task\",\"AutomationArguments\":{\"text\":\"hello\"}}");
        var automationStore = new FakeAutomationStore(Guid.Parse("30000000-0000-0000-0000-000000000001"));
        var runner = new FakeAutomationRunner(rejectionReason: "Automation task 'unknown_task' is not allowlisted.");
        var useCase = CreateUseCase(llm, automationStore: automationStore, automationRunner: runner);

        var reply = await useCase.HandleAsync(Message("run unknown_task with text hello"), TestContext.Current.CancellationToken);

        Assert.Contains("not allowlisted", reply.Text);
        Assert.Empty(automationStore.Saved);
        Assert.False(runner.WasCalled);
    }


    [Fact]
    public async Task ConfirmCommand_runs_pending_automation_once()
    {
        var id = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var automationStore = new FakeAutomationStore(id);
        automationStore.Pending[id] = new PendingAutomation(id, "echo_test", new Dictionary<string, string> { ["text"] = "hello" }, DateTimeOffset.UtcNow.AddMinutes(10));
        var runner = new FakeAutomationRunner(new AutomationResult(AutomationRunStatus.Succeeded, 0, "hello", string.Empty));
        var useCase = CreateUseCase(new FakeLlmProvider(), automationStore: automationStore, automationRunner: runner);

        var first = await useCase.HandleAsync(Message($"/confirm {id}"), TestContext.Current.CancellationToken);
        var second = await useCase.HandleAsync(Message($"/confirm {id}"), TestContext.Current.CancellationToken);

        Assert.Contains("Succeeded", first.Text);
        Assert.Contains("No pending automation", second.Text);
        Assert.Equal(1, runner.CallCount);
    }

    private static ProcessMessageUseCase CreateUseCase(
        FakeLlmProvider? llm = null,
        FakeMemoryStore? memoryStore = null,
        FakeSourceStore? sourceStore = null,
        FakeReminderStore? reminderStore = null,
        FakeAutomationStore? automationStore = null,
        FakeAutomationRunner? automationRunner = null,
        FakeClock? clock = null,
        TimeZoneInfo? timeZone = null)
    {
        return new ProcessMessageUseCase(
            llm ?? new FakeLlmProvider(),
            memoryStore ?? new FakeMemoryStore(),
            sourceStore ?? new FakeSourceStore(),
            reminderStore ?? new FakeReminderStore(),
            automationStore ?? new FakeAutomationStore(Guid.NewGuid()),
            automationRunner ?? new FakeAutomationRunner(),
            new FakeArtifactWriter(),
            clock ?? new FakeClock(new DateTimeOffset(2026, 7, 1, 6, 0, 0, TimeSpan.Zero)),
            new AssistantRuntimeSettings(timeZone ?? TimeZoneInfo.Utc, 8, 3500, MedicalBoundary, "%LOCALAPPDATA%/Mira/knowledge/0-dashboard/memory.md"));
    }

    private static IncomingMessage Message(string text) => new(123, 456, text, new DateTimeOffset(2026, 7, 1, 6, 0, 0, TimeSpan.Zero));

    private static MemoryItem Memory(string title, string content, MemoryCategory category) => new(
        Guid.NewGuid(),
        category,
        title,
        content,
        title.Split(' ')[0],
        [],
        0.9,
        456,
        "0-raw/test.md",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private sealed class FakeLlmProvider(params string[] responses) : ILlmProvider
    {
        private readonly Queue<string> _responses = new(responses);

        public List<LlmRequest> Requests { get; } = [];

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var response = _responses.Count == 0 ? "{}" : _responses.Dequeue();
            return Task.FromResult(new LlmResponse(response));
        }
    }

    private sealed class FakeMemoryStore : IMemoryStore
    {
        public List<string> RawCaptures { get; } = [];
        public List<MemoryUpsert> Upserts { get; } = [];
        public Guid? LastSourceCaptureId { get; private set; }
        public Func<MemorySearchQuery, IReadOnlyList<MemoryItem>> SearchHandler { get; init; } = _ => [];
        public Func<MemoryCategory, int, IReadOnlyList<MemoryItem>> GetByCategoryHandler { get; init; } = (_, _) => [];
        public Func<int, DateTimeOffset?, IReadOnlyList<MemoryItem>> GetRecentHandler { get; init; } = (_, _) => [];
        public Func<string, int, IReadOnlyList<MemoryItem>> GetBySubjectHandler { get; init; } = (_, _) => [];
        public Func<double, int, IReadOnlyList<MemoryItem>> GetLowConfidenceHandler { get; init; } = (_, _) => [];
        public Func<DateTimeOffset, int, IReadOnlyList<MemoryItem>> GetStaleHandler { get; init; } = (_, _) => [];
        public Func<long, int, DateTimeOffset, IReadOnlyList<ConversationMessage>> ConversationHandler { get; init; } = (_, _, _) => [];



        public Task SaveConversationMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ConversationMessage>> GetRecentConversationAsync(long chatId, int limit, DateTimeOffset beforeUtc, CancellationToken cancellationToken = default) => Task.FromResult(ConversationHandler(chatId, limit, beforeUtc));


        public Task<string> SaveRawCaptureAsync(string content, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default)
        {
            RawCaptures.Add(content);
            return Task.FromResult("0-raw/2026/07/raw.md");
        }

        public Task<MemoryItem> UpsertAsync(MemoryUpsert request, Guid? sourceCaptureId = null, CancellationToken cancellationToken = default)
        {
            Upserts.Add(request);
            if (sourceCaptureId is not null)
            {
                LastSourceCaptureId = sourceCaptureId;
            }

            return Task.FromResult(new MemoryItem(Guid.NewGuid(), request.Category, request.Title, request.Content, request.Subject, request.Tags, request.Confidence, request.SourceMessageId, request.SourcePath, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<MemoryItem>> SearchAsync(MemorySearchQuery query, CancellationToken cancellationToken = default) => Task.FromResult(SearchHandler(query));

        public Task<IReadOnlyList<MemoryItem>> GetRecentAsync(int limit, DateTimeOffset? sinceUtc = null, CancellationToken cancellationToken = default) => Task.FromResult(GetRecentHandler(limit, sinceUtc));

        public Task<IReadOnlyList<MemoryItem>> GetByCategoryAsync(MemoryCategory category, int limit, CancellationToken cancellationToken = default) => Task.FromResult(GetByCategoryHandler(category, limit));

        public Task<IReadOnlyList<MemoryItem>> GetBySubjectAsync(string subject, int limit, CancellationToken cancellationToken = default) => Task.FromResult(GetBySubjectHandler(subject, limit));

        public Task<IReadOnlyList<MemoryItem>> GetLowConfidenceAsync(double maxConfidence, int limit, CancellationToken cancellationToken = default) => Task.FromResult(GetLowConfidenceHandler(maxConfidence, limit));

        public Task<IReadOnlyList<MemoryItem>> GetStaleAsync(DateTimeOffset olderThanUtc, int limit, CancellationToken cancellationToken = default) => Task.FromResult(GetStaleHandler(olderThanUtc, limit));

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeSourceStore : ISourceStore
    {
        private readonly List<SourceCapture> _captures = [];

        public IReadOnlyList<SourceCapture> Captures => _captures;

        public Task<SourceCapture> SaveSourceCaptureAsync(SourceCaptureCreate request, CancellationToken cancellationToken = default)
        {
            var capture = new SourceCapture(
                Guid.NewGuid(),
                request.Kind,
                string.IsNullOrWhiteSpace(request.Title) ? "Untitled source" : request.Title.Trim(),
                request.ContentText,
                "fake-hash",
                request.CreatedAtUtc.ToUniversalTime(),
                SourceProcessingStatus.Unprocessed,
                null,
                request.ExternalId,
                request.FilePath,
                request.Metadata ?? new Dictionary<string, string>());
            _captures.Add(capture);
            return Task.FromResult(capture);
        }

        public Task<IReadOnlyList<SourceCapture>> GetRecentSourceCapturesAsync(int limit, SourceProcessingStatus? status = null, CancellationToken cancellationToken = default)
        {
            var captures = _captures
                .Where(capture => status is null || capture.Status == status)
                .OrderByDescending(capture => capture.CreatedAtUtc)
                .Take(Math.Clamp(limit, 1, 200))
                .ToArray();
            return Task.FromResult<IReadOnlyList<SourceCapture>>(captures);
        }

        public Task<SourceCapture?> GetSourceCaptureAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_captures.FirstOrDefault(capture => capture.Id == id));
        }

        public Task MarkSourceCaptureProcessedAsync(Guid id, DateTimeOffset processedAtUtc, CancellationToken cancellationToken = default)
        {
            var index = _captures.FindIndex(capture => capture.Id == id);
            if (index >= 0)
            {
                _captures[index] = _captures[index] with
                {
                    Status = SourceProcessingStatus.Processed,
                    ProcessedAtUtc = processedAtUtc.ToUniversalTime()
                };
            }

            return Task.CompletedTask;
        }

        public Task MarkSourceCaptureFailedAsync(Guid id, DateTimeOffset failedAtUtc, CancellationToken cancellationToken = default)
        {
            var index = _captures.FindIndex(capture => capture.Id == id);
            if (index >= 0)
            {
                _captures[index] = _captures[index] with
                {
                    Status = SourceProcessingStatus.Failed,
                    ProcessedAtUtc = failedAtUtc.ToUniversalTime()
                };
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeReminderStore : IReminderStore
    {
        public List<ReminderCreateRequest> Created { get; } = [];
        public Func<int, IReadOnlyList<Reminder>> GetPendingHandler { get; init; } = _ => [];

        public Task<Reminder> AddAsync(ReminderCreateRequest request, CancellationToken cancellationToken = default)
        {
            Created.Add(request);
            return Task.FromResult(new Reminder(Guid.NewGuid(), request.Title, request.Notes, request.DueAtUtc, request.RepeatKind, ReminderStatus.Pending, DateTimeOffset.UtcNow, null, null));
        }

        public Task<IReadOnlyList<Reminder>> GetPendingAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult(GetPendingHandler(limit));

        public Task<IReadOnlyList<Reminder>> GetDueAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Reminder>>([]);

        public Task MarkSentAsync(Guid id, DateTimeOffset sentAtUtc, DateTimeOffset? nextDueAtUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CompleteAsync(Guid id, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CancelAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeAutomationStore(Guid defaultId) : IAutomationStore
    {
        public List<AutomationRequest> Saved { get; } = [];
        public Dictionary<Guid, PendingAutomation> Pending { get; } = [];

        public Task<PendingAutomation> SavePendingAsync(AutomationRequest request, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default)
        {
            Saved.Add(request);
            var pending = new PendingAutomation(defaultId, request.Name, request.Arguments, expiresAtUtc);
            Pending[defaultId] = pending;
            return Task.FromResult(pending);
        }

        public Task<PendingAutomation?> TakePendingAsync(Guid id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            if (!Pending.Remove(id, out var pending) || pending.ExpiresAtUtc < nowUtc)
            {
                return Task.FromResult<PendingAutomation?>(null);
            }

            return Task.FromResult<PendingAutomation?>(pending);
        }
    }

    private sealed class FakeAutomationRunner(AutomationResult? result = null, string? rejectionReason = null) : IAutomationRunner
    {
        public bool WasCalled => CallCount > 0;
        public int CallCount { get; private set; }

        public Task<AutomationResult> RunAsync(AutomationRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result ?? new AutomationResult(AutomationRunStatus.Succeeded, 0, string.Empty, string.Empty));
        }

        public Task<string?> GetRejectionReasonAsync(AutomationRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(rejectionReason);
        }
    }

    private sealed class FakeArtifactWriter : IKnowledgeArtifactWriter
    {
        public Task WriteBriefingAsync(string fileName, string content, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteThreadAsync(string fileName, string content, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
