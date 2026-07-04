namespace Mira.Core;

using Microsoft.Extensions.DependencyInjection;
using Mira.Core.Interfaces;
using Mira.Core.UseCases;

public static class DependencyInjection
{
    public static IServiceCollection AddCore(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddTransient<ProcessMessageUseCase>();
        return services;
    }
}
