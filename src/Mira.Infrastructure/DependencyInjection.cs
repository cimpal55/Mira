using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mira.Core.Interfaces;
using Mira.Core.UseCases;
using Mira.Infrastructure.Configuration;
using Mira.Infrastructure.Llm;
using Mira.Infrastructure.Storage;
using Mira.Infrastructure.Telegram;

namespace Mira.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<StorageSettings>()
            .BindConfiguration(StorageSettings.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<LocalLlmSettings>()
            .BindConfiguration(LocalLlmSettings.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<TelegramSettings>()
            .BindConfiguration(TelegramSettings.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHostedService<DatabaseInitializer>();
        services.AddSingleton<IMemoryRepository, SqliteMemoryRepository>();
        services.AddSingleton<IPersonRepository, SqlitePersonRepository>();

        services.AddHttpClient<ILlmProvider, LocalLlmProvider>();
        services.AddSingleton<ProcessMessageUseCase>();
        services.AddHostedService<TelegramBotService>();

        return services;
    }
}
