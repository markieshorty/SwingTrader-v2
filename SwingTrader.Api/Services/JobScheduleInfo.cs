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

        return new List<(string JobType, DateTime NextEt)>
        {
            ("Research", NextWeekdayAt(nowEt, 6, 0)),
            ("Watchlist", NextWeeklyAt(nowEt, DayOfWeek.Sunday, 20, 0)),
            ("Report", NextWeekdayAt(nowEt, 6, 30)),
            ("Execution", NextWeekdayAt(nowEt, 9, 20)),
            ("Monitor", NextWeekdayAt(nowEt, 9, 30)),
            ("Risk", NextMonthlyDayAt(nowEt, 1, 9, 0)),
            ("Refinement", NextMonthlyDayAt(nowEt, 15, 8, 0)),
        }
        .Select(x => new NextRunDto(
            x.JobType,
            TimeZoneInfo.ConvertTimeToUtc(x.NextEt, EasternTimeZone),
            x.NextEt.ToString("ddd d MMM, HH:mm") + " ET"))
        .ToList();
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
