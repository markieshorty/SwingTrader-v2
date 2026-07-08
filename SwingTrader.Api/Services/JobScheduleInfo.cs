namespace SwingTrader.Api.Services;

public record NextRunDto(string JobType, DateTime NextRunAtUtc, string NextRunLabel);

// Mirrors SchedulerFunction's windows for display purposes only - if the
// two ever drift, this is informational (Dashboard cannot enqueue anything
// wrong from a stale label), so duplicating the schedule here rather than
// sharing code with the Functions project is an acceptable trade-off.
public static class JobScheduleInfo
{
    private static readonly TimeZoneInfo EasternTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    public static List<NextRunDto> GetNextRuns(DateTime utcNow)
    {
        var nowEt = TimeZoneInfo.ConvertTimeFromUtc(utcNow, EasternTimeZone);

        var runs = new List<(string JobType, DateTime NextEt)>
        {
            ("Research", NextWeekdayAt(nowEt, 4, 0)),
            ("Watchlist", NextWeeklyAt(nowEt, DayOfWeek.Sunday, 20, 0)),
            ("Report", NextWeekdayAt(nowEt, 6, 30)),
            ("Execution", NextWeekdayAt(nowEt, 9, 20)),
            ("Risk", NextMonthlyDayAt(nowEt, 1, 9, 0)),
            ("Refinement", NextMonthlyDayAt(nowEt, 15, 8, 0)),
        }
        .Select(x => new NextRunDto(
            x.JobType,
            TimeZoneInfo.ConvertTimeToUtc(x.NextEt, EasternTimeZone),
            x.NextEt.ToString("ddd d MMM, HH:mm") + " ET"))
        .ToList();

        // Monitor runs every 5 minutes throughout 09:30-16:00 ET on weekdays
        // (not a once-daily job like the others), so its label reflects the
        // next actual tick plus the recurring window rather than a single
        // "next run" time.
        var nextMonitorTick = NextMonitorTick(nowEt);
        var monitorLabel = IsInMonitorWindow(nowEt)
            ? $"next {nextMonitorTick:HH:mm} ET"
            : $"Every 5 min, 09:30-16:00 ET weekdays - next window: {nextMonitorTick:ddd d MMM, HH:mm} ET";
        runs.Add(new NextRunDto("Monitor", TimeZoneInfo.ConvertTimeToUtc(nextMonitorTick, EasternTimeZone), monitorLabel));

        return runs;
    }

    private static bool IsInMonitorWindow(DateTime nowEt) =>
        IsWeekday(nowEt) && nowEt.TimeOfDay >= new TimeSpan(9, 30, 0) && nowEt.TimeOfDay < new TimeSpan(16, 0, 0);

    private static DateTime NextMonitorTick(DateTime nowEt)
    {
        if (IsInMonitorWindow(nowEt))
        {
            // Next 5-minute boundary strictly after now (Scheduler runs on
            // "0 */5 * * * *" - i.e. exactly :00, :05, :10, ...).
            var minutesToNextTick = 5 - (nowEt.Minute % 5);
            var candidate = nowEt.AddMinutes(minutesToNextTick);
            return new DateTime(candidate.Year, candidate.Month, candidate.Day, candidate.Hour, candidate.Minute, 0);
        }

        return NextWeekdayAt(nowEt, 9, 30);
    }

    private static bool IsWeekday(DateTime d) => d.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday;

    private static DateTime NextWeekdayAt(DateTime nowEt, int hour, int minute)
    {
        var candidate = nowEt.Date.AddHours(hour).AddMinutes(minute);
        if (candidate <= nowEt || !IsWeekday(candidate))
        {
            do
            {
                candidate = candidate.Date.AddDays(1).AddHours(hour).AddMinutes(minute);
            } while (!IsWeekday(candidate));
        }
        return candidate;
    }

    private static DateTime NextWeeklyAt(DateTime nowEt, DayOfWeek dayOfWeek, int hour, int minute)
    {
        var candidate = nowEt.Date.AddHours(hour).AddMinutes(minute);
        while (candidate.DayOfWeek != dayOfWeek || candidate <= nowEt)
            candidate = candidate.AddDays(1);
        return candidate;
    }

    private static DateTime NextMonthlyDayAt(DateTime nowEt, int dayOfMonth, int hour, int minute)
    {
        var candidate = new DateTime(nowEt.Year, nowEt.Month, dayOfMonth, hour, minute, 0);
        if (candidate <= nowEt)
            candidate = candidate.AddMonths(1);
        return candidate;
    }
}
