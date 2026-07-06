using FluentAssertions;
using SwingTrader.Api.Services;
using Xunit;

namespace SwingTrader.Tests;

public class JobScheduleInfoTests
{
    // 2026-07-06 is a Monday, so it's an easy anchor for weekday/weekly math.
    private static readonly DateTime MondayMorningUtc = new(2026, 7, 6, 9, 0, 0, DateTimeKind.Utc); // ~5am ET

    [Fact]
    public void GetNextRuns_ReturnsAllSevenJobTypes()
    {
        var runs = JobScheduleInfo.GetNextRuns(MondayMorningUtc);

        runs.Select(r => r.JobType).Should().BeEquivalentTo(
            ["Research", "Watchlist", "Report", "Execution", "Monitor", "Risk", "Refinement"]);
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
        // 5am ET Monday - before the 6am ET Research window.
        var runs = JobScheduleInfo.GetNextRuns(MondayMorningUtc);

        var research = runs.Single(r => r.JobType == "Research");
        (research.NextRunAtUtc - MondayMorningUtc).Should().BeLessThan(TimeSpan.FromHours(2));
    }

    [Fact]
    public void GetNextRuns_ResearchAfterTodaysWindow_RollsToNextWeekday()
    {
        var afterWindowUtc = new DateTime(2026, 7, 6, 15, 0, 0, DateTimeKind.Utc); // ~11am ET Monday, after 6am window
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
    public void GetNextRuns_RiskIsAlwaysTheFirstOfTheMonth()
    {
        var runs = JobScheduleInfo.GetNextRuns(MondayMorningUtc);

        var risk = runs.Single(r => r.JobType == "Risk");
        risk.NextRunLabel.Should().Contain(" 1 ");
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
}
