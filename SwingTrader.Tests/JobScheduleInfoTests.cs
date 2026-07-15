using FluentAssertions;
using SwingTrader.Api.Services;
using Xunit;

namespace SwingTrader.Tests;

public class JobScheduleInfoTests
{
    // 2026-07-06 is a Monday, so it's an easy anchor for weekday/weekly math.
    private static readonly DateTime MondayMorningUtc = new(2026, 7, 6, 9, 0, 0, DateTimeKind.Utc); // ~5am ET

    [Fact]
    public void GetNextRuns_ReturnsAllJobTypes()
    {
        var runs = JobScheduleInfo.GetNextRuns(MondayMorningUtc);

        runs.Select(r => r.JobType).Should().BeEquivalentTo(
            ["Research", "Watchlist", "Report", "Execution", "Monitor", "Refinement"]);
    }

    [Fact]
    public void GetNextRuns_AllTimesAreInTheFuture()
    {
        var runs = JobScheduleInfo.GetNextRuns(MondayMorningUtc);

        runs.Should().OnlyContain(r => r.NextRunAtUtc > MondayMorningUtc);
    }

    [Fact]
    public void GetNextRuns_ResearchBeforeTodaysWindow_IsLaterToday()
    {
        // 5:30am ET Monday - before the 6:30 ET Research window (this test
        // previously pinned the stale 4:00 slot, which is exactly the drift
        // the display bug shipped with).
        var beforeWindowUtc = new DateTime(2026, 7, 6, 9, 30, 0, DateTimeKind.Utc);
        var runs = JobScheduleInfo.GetNextRuns(beforeWindowUtc);

        var research = runs.Single(r => r.JobType == "Research");
        (research.NextRunAtUtc - beforeWindowUtc).Should().BeLessThan(TimeSpan.FromHours(2));
    }

    [Fact]
    public void GetNextRuns_ResearchAfterTodaysWindow_RollsToNextWeekday()
    {
        var afterWindowUtc = new DateTime(2026, 7, 6, 16, 0, 0, DateTimeKind.Utc); // ~noon ET Monday, after the 7:30 window
        var runs = JobScheduleInfo.GetNextRuns(afterWindowUtc);

        var research = runs.Single(r => r.JobType == "Research");
        research.NextRunAtUtc.Should().BeAfter(afterWindowUtc.AddHours(12)); // rolls to Tuesday, not later today
    }

    [Fact]
    public void GetNextRuns_WatchlistIsAlwaysASunday()
    {
        var runs = JobScheduleInfo.GetNextRuns(MondayMorningUtc);

        var watchlist = runs.Single(r => r.JobType == "Watchlist");
        var etLabel = watchlist.NextRunLabel;
        etLabel.Should().StartWith("Sun");
    }

    [Fact]
    public void GetNextRuns_RefinementIsAlwaysThe15th()
    {
        var runs = JobScheduleInfo.GetNextRuns(MondayMorningUtc);

        var refinement = runs.Single(r => r.JobType == "Refinement");
        refinement.NextRunLabel.Should().Contain(" 15 ");
    }

    [Fact]
    public void GetNextRuns_MonitorSkipsWeekends()
    {
        var fridayEveningUtc = new DateTime(2026, 7, 10, 22, 0, 0, DateTimeKind.Utc); // Friday evening, after market close
        var runs = JobScheduleInfo.GetNextRuns(fridayEveningUtc);

        var monitor = runs.Single(r => r.JobType == "Monitor");
        monitor.NextRunAtUtc.DayOfWeek.Should().Be(DayOfWeek.Monday);
    }

    [Fact]
    public void GetNextRuns_MonitorOutsideWindow_LabelDescribesRecurringSchedule()
    {
        var runs = JobScheduleInfo.GetNextRuns(MondayMorningUtc); // ~5am ET, before the 9:30 window opens

        var monitor = runs.Single(r => r.JobType == "Monitor");
        monitor.NextRunLabel.Should().Contain("Every 5 min");
        monitor.NextRunLabel.Should().Contain("next window");
    }

    [Fact]
    public void GetNextRuns_MonitorInsideWindow_NextTickIsTheUpcoming5MinuteBoundary()
    {
        // 2026-07-06 is a Monday. 14:07 UTC = 10:07 ET, inside the 09:30-16:00 window.
        var insideWindowUtc = new DateTime(2026, 7, 6, 14, 7, 0, DateTimeKind.Utc);
        var runs = JobScheduleInfo.GetNextRuns(insideWindowUtc);

        var monitor = runs.Single(r => r.JobType == "Monitor");
        monitor.NextRunLabel.Should().NotContain("Every 5 min");
        monitor.NextRunLabel.Should().NotContain("next window");
        // Next tick after 10:07 ET should be 10:10 ET (14:10 UTC).
        monitor.NextRunAtUtc.Should().Be(new DateTime(2026, 7, 6, 14, 10, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void GetNextRuns_MonitorRightAtA5MinuteBoundary_NextTickIsFiveMinutesLater()
    {
        // 14:10:00 UTC = 10:10:00 ET exactly - the next tick should be 10:15 ET, not 10:10 again.
        var onBoundaryUtc = new DateTime(2026, 7, 6, 14, 10, 0, DateTimeKind.Utc);
        var runs = JobScheduleInfo.GetNextRuns(onBoundaryUtc);

        var monitor = runs.Single(r => r.JobType == "Monitor");
        monitor.NextRunAtUtc.Should().Be(new DateTime(2026, 7, 6, 14, 15, 0, DateTimeKind.Utc));
    }
}
