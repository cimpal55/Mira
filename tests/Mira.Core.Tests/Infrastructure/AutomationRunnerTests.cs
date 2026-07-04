namespace Mira.Core.Tests.Infrastructure;

using Microsoft.Extensions.Options;
using Mira.Core.Models;
using Mira.Infrastructure.Automation;
using Mira.Infrastructure.Configuration;
using Xunit;

public sealed class AutomationRunnerTests
{
    [Fact]
    public async Task RunAsync_rejects_unknown_task()
    {
        var fixture = await SqliteAssistantStoreTests.StoreFixture.CreateAsync();
        var runner = new LocalProcessAutomationRunner(Options.Create(Settings()), fixture.Store);

        var result = await runner.RunAsync(new AutomationRequest("missing", new Dictionary<string, string>()), TestContext.Current.CancellationToken);

        Assert.Equal(AutomationRunStatus.Rejected, result.Status);
        Assert.Contains("not allowlisted", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_rejects_missing_placeholder()
    {
        var fixture = await SqliteAssistantStoreTests.StoreFixture.CreateAsync();
        var runner = new LocalProcessAutomationRunner(Options.Create(Settings()), fixture.Store);

        var result = await runner.RunAsync(new AutomationRequest("echo_test", new Dictionary<string, string>()), TestContext.Current.CancellationToken);

        Assert.Equal(AutomationRunStatus.Rejected, result.Status);
        Assert.Contains("text", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_executes_echo_test_after_confirmation_path()
    {
        var fixture = await SqliteAssistantStoreTests.StoreFixture.CreateAsync();
        var runner = new LocalProcessAutomationRunner(Options.Create(Settings()), fixture.Store);

        var result = await runner.RunAsync(new AutomationRequest("echo_test", new Dictionary<string, string> { ["text"] = "hello" }), TestContext.Current.CancellationToken);

        Assert.Equal(AutomationRunStatus.Succeeded, result.Status);
        Assert.Contains("hello", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static AutomationSettings Settings() => new()
    {
        Enabled = true,
        Tasks =
        [
            new AutomationTaskSettings
            {
                Name = "echo_test",
                Description = "Echo text for local automation smoke testing",
                Executable = "cmd.exe",
                Arguments = ["/c", "echo", "{text}"],
                RequiresConfirmation = true,
                TimeoutSeconds = 10
            }
        ]
    };
}
