namespace Mira.Infrastructure.Proactive;

using System.Globalization;
using Mira.Infrastructure.Configuration;

internal static class ProactiveSchedule
{
    public static TimeZoneInfo ResolveTimeZone(ProactiveSettings settings)
    {
        return string.IsNullOrWhiteSpace(settings.TimeZoneId)
            ? TimeZoneInfo.Local
            : TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
    }

    public static DateTimeOffset ToLocal(DateTimeOffset utc, TimeZoneInfo timeZone)
    {
        return TimeZoneInfo.ConvertTime(utc, timeZone);
    }

    public static DateTimeOffset FromLocal(DateTimeOffset local, TimeZoneInfo timeZone)
    {
        var unspecified = DateTime.SpecifyKind(local.DateTime, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    public static bool IsAtOrAfter(DateTimeOffset localNow, string localTime)
    {
        return TimeOnly.TryParseExact(localTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due)
            && TimeOnly.FromDateTime(localNow.DateTime) >= due;
    }

    public static string LocalDateKey(DateTimeOffset localNow) => localNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public static string WeeklyPeriodKey(DateTimeOffset localNow)
    {
        var date = DateOnly.FromDateTime(localNow.DateTime);
        return FormattableString.Invariant($"{ISOWeek.GetYear(date.ToDateTime(TimeOnly.MinValue))}-W{ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue)):00}");
    }
}
