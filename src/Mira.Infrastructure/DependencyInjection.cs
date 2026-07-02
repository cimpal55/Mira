namespace Mira.Infrastructure;

using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mira.Core.Interfaces;
using Mira.Core.Models;
using Mira.Infrastructure.Automation;
using Mira.Infrastructure.Configuration;
using Mira.Infrastructure.Llm;
using Mira.Infrastructure.Proactive;
using Mira.Infrastructure.Reminders;
using Mira.Infrastructure.Storage;
using Mira.Infrastructure.Telegram;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TelegramSettings>()
            .BindConfiguration(TelegramSettings.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<LocalLlmSettings>()
            .BindConfiguration(LocalLlmSettings.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<LocalLlmSettings>, LocalLlmSettingsValidator>();

        services.AddOptions<StorageSettings>()
            .BindConfiguration(StorageSettings.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<ProactiveSettings>()
            .BindConfiguration(ProactiveSettings.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ProactiveSettings>, ProactiveSettingsValidator>();

        services.AddOptions<AutomationSettings>()
            .BindConfiguration(AutomationSettings.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AutomationSettings>, AutomationSettingsValidator>();

        services.AddHttpClient(nameof(LocalLlmProvider));
        services.AddSingleton<ILlmProvider, LocalLlmProvider>();

        services.AddSingleton<KnowledgeMarkdownWriter>();
        services.AddSingleton<IKnowledgeArtifactWriter>(provider => provider.GetRequiredService<KnowledgeMarkdownWriter>());
        services.AddSingleton<SqliteAssistantStore>();
        services.AddSingleton<IMemoryStore>(provider => provider.GetRequiredService<SqliteAssistantStore>());
        services.AddSingleton<IReminderStore>(provider => provider.GetRequiredService<SqliteAssistantStore>());
        services.AddSingleton<IProactiveRunStore>(provider => provider.GetRequiredService<SqliteAssistantStore>());
        services.AddSingleton<IAutomationStore>(provider => provider.GetRequiredService<SqliteAssistantStore>());
        services.AddSingleton<SqliteSchemaInitializer>();

        services.AddSingleton<IAutomationRunner, LocalProcessAutomationRunner>();

        services.AddSingleton(provider =>
        {
            var proactive = provider.GetRequiredService<IOptions<ProactiveSettings>>().Value;
            var storage = provider.GetRequiredService<IOptions<StorageSettings>>().Value;
            return new AssistantRuntimeSettings(
                ProactiveSchedule.ResolveTimeZone(proactive),
                MaxContextMemories: 8,
                MaxReplyCharacters: 3500,
                MedicalBoundaryMessage: "I can organize your saved medical information, but I cannot make medical decisions or change treatment. Confirm medication and dosage questions with a qualified clinician.",
                KnowledgeDashboardPath: StoragePathResolver.CombineKnowledgePath(storage, Path.Combine("0-dashboard", "memory.md")),
                KnowledgeDashboardHtmlPath: StoragePathResolver.CombineKnowledgePath(storage, Path.Combine("0-dashboard", "index.html")));
        });

        services.AddSingleton<TelegramBotService>();
        services.AddSingleton<INotificationSink>(provider => provider.GetRequiredService<TelegramBotService>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<TelegramBotService>());

        services.AddHostedService<ReminderDispatchService>();
        services.AddHostedService<ProactiveBriefService>();
        services.AddHostedService<KnowledgeMaintenanceService>();

        return services;
    }

    private sealed class LocalLlmSettingsValidator : IValidateOptions<LocalLlmSettings>
    {
        public ValidateOptionsResult Validate(string? name, LocalLlmSettings options)
        {
            var failures = new List<string>();
            if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https"))
            {
                failures.Add("LocalLlm:BaseUrl must be an absolute HTTP or HTTPS URL.");
            }

            if (string.IsNullOrWhiteSpace(options.Model))
            {
                failures.Add("LocalLlm:Model must not be empty.");
            }

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
    }

    private sealed class ProactiveSettingsValidator : IValidateOptions<ProactiveSettings>
    {
        public ValidateOptionsResult Validate(string? name, ProactiveSettings options)
        {
            var failures = new List<string>();
            if (!string.IsNullOrWhiteSpace(options.TimeZoneId))
            {
                try
                {
                    TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
                }
                catch (TimeZoneNotFoundException)
                {
                    failures.Add($"Proactive:TimeZoneId '{options.TimeZoneId}' was not found.");
                }
                catch (InvalidTimeZoneException)
                {
                    failures.Add($"Proactive:TimeZoneId '{options.TimeZoneId}' is invalid.");
                }
            }

            ValidateLocalTime(options.DailyBriefLocalTime, "DailyBriefLocalTime", failures);
            ValidateLocalTime(options.WeeklyReviewLocalTime, "WeeklyReviewLocalTime", failures);
            ValidateLocalTime(options.KnowledgeAuditLocalTime, "KnowledgeAuditLocalTime", failures);
            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }

        private static void ValidateLocalTime(string value, string propertyName, List<string> failures)
        {
            if (!TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                failures.Add($"Proactive:{propertyName} must use HH:mm format.");
            }
        }
    }
}
