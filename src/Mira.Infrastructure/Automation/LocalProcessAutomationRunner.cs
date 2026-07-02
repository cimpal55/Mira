namespace Mira.Infrastructure.Automation;

using System.Diagnostics;
using Microsoft.Extensions.Options;
using Mira.Core.Interfaces;
using Mira.Core.Models;
using Mira.Infrastructure.Configuration;
using Mira.Infrastructure.Storage;

public sealed class LocalProcessAutomationRunner(
    IOptions<AutomationSettings> options,
    SqliteAssistantStore store) : IAutomationRunner
{
    private readonly AutomationSettings _settings = options.Value;

    public async Task<AutomationResult> RunAsync(AutomationRequest request, CancellationToken cancellationToken = default)
    {
        var requestedAt = DateTimeOffset.UtcNow;

        var rejectionReason = GetRejectionReason(request);
        if (!string.IsNullOrWhiteSpace(rejectionReason))
        {
            return await RejectAsync(request, rejectionReason, requestedAt, cancellationToken).ConfigureAwait(false);
        }

        var task = _settings.Tasks.First(candidate => candidate.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase));
        _ = TryBuildArguments(task, request.Arguments, out var arguments, out _);

        var startInfo = new ProcessStartInfo
        {
            FileName = task.Executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (!string.IsNullOrWhiteSpace(task.WorkingDirectory))
        {
            startInfo.WorkingDirectory = task.WorkingDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = new AutomationResult(AutomationRunStatus.Failed, null, string.Empty, ex.Message);
            await RecordAsync(request, failed, requestedAt, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            return failed;
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(task.TimeoutSeconds));

        AutomationResult result;
        DateTimeOffset finishedAt;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            finishedAt = DateTimeOffset.UtcNow;
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            result = new AutomationResult(
                process.ExitCode == 0 ? AutomationRunStatus.Succeeded : AutomationRunStatus.Failed,
                process.ExitCode,
                output,
                error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            finishedAt = DateTimeOffset.UtcNow;
            TryKill(process);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            result = new AutomationResult(AutomationRunStatus.TimedOut, null, output, error);
        }

        await RecordAsync(request, result, requestedAt, finishedAt, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task<string?> GetRejectionReasonAsync(AutomationRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GetRejectionReason(request));
    }

    private string? GetRejectionReason(AutomationRequest request)
    {
        if (!_settings.Enabled)
        {
            return "Automation is disabled. Enable Automation:Enabled and configure an allowlisted task first.";
        }

        var task = _settings.Tasks.FirstOrDefault(candidate => candidate.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase));
        if (task is null)
        {
            return $"Automation task '{request.Name}' is not allowlisted.";
        }

        return TryBuildArguments(task, request.Arguments, out _, out var missingPlaceholder)
            ? null
            : $"Automation task '{task.Name}' is missing required argument '{missingPlaceholder}'.";
    }

    private async Task<AutomationResult> RejectAsync(AutomationRequest request, string error, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        var result = new AutomationResult(AutomationRunStatus.Rejected, null, string.Empty, error);
        await RecordAsync(request, result, requestedAt, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task RecordAsync(AutomationRequest request, AutomationResult result, DateTimeOffset requestedAt, DateTimeOffset finishedAt, CancellationToken cancellationToken)
    {
        await store.RecordAutomationRunAsync(
            request.Name,
            request.Arguments,
            result.Status,
            requestedAt,
            finishedAt,
            result.ExitCode,
            result.Output,
            result.Error,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool TryBuildArguments(
        AutomationTaskSettings task,
        IReadOnlyDictionary<string, string> provided,
        out IReadOnlyList<string> arguments,
        out string missingPlaceholder)
    {
        var built = new List<string>(task.Arguments.Count);
        foreach (var template in task.Arguments)
        {
            if (!TrySubstitute(template, provided, out var value, out missingPlaceholder))
            {
                arguments = [];
                return false;
            }

            built.Add(value);
        }

        arguments = built;
        missingPlaceholder = string.Empty;
        return true;
    }

    private static bool TrySubstitute(
        string template,
        IReadOnlyDictionary<string, string> provided,
        out string value,
        out string missingPlaceholder)
    {
        var result = template;
        foreach (var placeholder in ExtractPlaceholders(template))
        {
            if (!provided.TryGetValue(placeholder, out var replacement))
            {
                value = string.Empty;
                missingPlaceholder = placeholder;
                return false;
            }

            result = result.Replace("{" + placeholder + "}", replacement, StringComparison.Ordinal);
        }

        value = result;
        missingPlaceholder = string.Empty;
        return true;
    }

    private static IReadOnlyList<string> ExtractPlaceholders(string template)
    {
        var placeholders = new List<string>();
        var start = -1;
        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] == '{')
            {
                start = index + 1;
            }
            else if (template[index] == '}' && start >= 0)
            {
                var placeholder = template[start..index];
                if (placeholder.Length > 0)
                {
                    placeholders.Add(placeholder);
                }

                start = -1;
            }
        }

        return placeholders;
    }

    private static void TryKill(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }
}
