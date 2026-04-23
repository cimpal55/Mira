using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mira.Core.Interfaces;
using Mira.Core.UseCases;
using Mira.Infrastructure.Configuration;
using Mira.Infrastructure.Llm;
using Mira.Infrastructure.Telegram;

namespace Mira.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<LocalLlmSettings>(configuration.GetSection(LocalLlmSettings.SectionName));
        services.Configure<TelegramSettings>(configuration.GetSection(TelegramSettings.SectionName));

        services.AddHttpClient<ILlmProvider, LocalLlmProvider>();
        services.AddSingleton<ProcessMessageUseCase>();
        services.AddHostedService<TelegramBotService>();

        return services;
    }
}
