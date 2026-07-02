using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mira.Core;
using Mira.Infrastructure;
using Mira.Infrastructure.Storage;

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddCore()
    .AddInfrastructure(builder.Configuration);

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<SqliteSchemaInitializer>();
    await initializer.InitializeAsync();
}

await host.RunAsync();
