namespace Mira.Infrastructure.Configuration;

using System.ComponentModel.DataAnnotations;

public sealed class ProactiveSettings
{
    public const string SectionName = "Proactive";

    public bool RemindersEnabled { get; set; } = true;

    public bool DailyBriefEnabled { get; set; } = true;

    public bool WeeklyReviewEnabled { get; set; } = true;

    public bool WorkoutReminderEnabled { get; set; } = true;

    public bool KnowledgeAuditEnabled { get; set; } = true;

    [Range(10, 3600)]
    public int PollIntervalSeconds { get; set; } = 60;

    public string TimeZoneId { get; set; } = string.Empty;

    public string DailyBriefLocalTime { get; set; } = "08:00";

    public DayOfWeek WeeklyReviewDay { get; set; } = DayOfWeek.Sunday;

    public string WeeklyReviewLocalTime { get; set; } = "09:00";

    public DayOfWeek KnowledgeAuditDay { get; set; } = DayOfWeek.Sunday;

    public string KnowledgeAuditLocalTime { get; set; } = "22:00";

    [Range(1, 30)]
    public int WorkoutReminderAfterDays { get; set; } = 3;

    [Range(7, 365)]
    public int StaleMemoryAfterDays { get; set; } = 90;
}
