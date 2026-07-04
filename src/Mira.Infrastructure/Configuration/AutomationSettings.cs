namespace Mira.Infrastructure.Configuration;

using Microsoft.Extensions.Options;

public sealed class AutomationSettings
{
    public const string SectionName = "Automation";

    public bool Enabled { get; set; } = false;

    public List<AutomationTaskSettings> Tasks { get; set; } = [];
}

public sealed class AutomationTaskSettings
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Executable { get; set; } = string.Empty;

    public List<string> Arguments { get; set; } = [];

    public string? WorkingDirectory { get; set; }

    public bool RequiresConfirmation { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 60;
}

public sealed class AutomationSettingsValidator : IValidateOptions<AutomationSettings>
{
    public ValidateOptionsResult Validate(string? name, AutomationSettings options)
    {
        var failures = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in options.Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Name))
            {
                failures.Add("Automation task names must be non-empty.");
            }
            else
            {
                if (!task.Name.All(character => char.IsLetterOrDigit(character) || character is '_' or '-'))
                {
                    failures.Add($"Automation task '{task.Name}' may contain only letters, digits, '_' or '-'.");
                }

                if (!names.Add(task.Name))
                {
                    failures.Add($"Automation task name '{task.Name}' is duplicated.");
                }
            }

            if (string.IsNullOrWhiteSpace(task.Executable))
            {
                failures.Add($"Automation task '{task.Name}' must define an executable.");
            }

            if (task.TimeoutSeconds is < 1 or > 600)
            {
                failures.Add($"Automation task '{task.Name}' timeout must be between 1 and 600 seconds.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
