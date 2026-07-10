using FluentAssertions;
using SwingTrader.Infrastructure.Market;
using Xunit;

namespace SwingTrader.Tests;

public class MarketCalendarServiceTests
{
    private readonly MarketCalendarService _sut = new();

    [Theory]
    [InlineData(2026, 7, 6)]  // Monday
    [InlineData(2026, 7, 7)]  // Tuesday
    [InlineData(2026, 7, 8)]  // Wednesday
    [InlineData(2026, 7, 9)]  // Thursday
    [InlineData(2026, 7, 10)] // Friday
    public void IsMarketDay_OrdinaryWeekday_ReturnsTrue(int year, int month, int day)
    {
        _sut.IsMarketDay(new DateOnly(year, month, day)).Should().BeTrue();
    }

    [Theory]
    [InlineData(2026, 7, 11)] // Saturday
    [InlineData(2026, 7, 12)] // Sunday
    public void IsMarketDay_Weekend_ReturnsFalse(int year, int month, int day)
    {
        _sut.IsMarketDay(new DateOnly(year, month, day)).Should().BeFalse();
    }

    [Theory]
    [InlineData(2026, 1, 1)]   // New Year's Day
    [InlineData(2026, 12, 25)] // Christmas
    [InlineData(2027, 1, 1)]   // New Year's Day (following year)
    public void IsMarketDay_KnownHoliday_ReturnsFalse(int year, int month, int day)
    {
        _sut.IsMarketDay(new DateOnly(year, month, day)).Should().BeFalse();
    }

    [Fact]
    public void IsMarketDay_HolidayFallingOnWeekend_StillHandledCorrectly()
    {
        // Independence Day 2026 (observed) is a Friday - confirms the holiday
        // set and the weekend check don't conflict either way.
        _sut.IsMarketDay(new DateOnly(2026, 7, 3)).Should().BeFalse();
    }

    [Fact]
    public void IsMarketDay_DateWithNoSpecialHandling_DefaultsToOpen()
    {
        // An ordinary weekday far outside the seeded holiday years still
        // resolves purely on day-of-week.
        _sut.IsMarketDay(new DateOnly(2030, 3, 6)).Should().BeTrue(); // a Wednesday
    }

    [Fact]
    public void TradingDaysBetween_FullWeek_CountsFiveNotSeven()
    {
        // Mon 6 Jul 2026 -> Mon 13 Jul: 7 calendar days, but Sat/Sun don't
        // count, so 5 trading days (Tue,Wed,Thu,Fri,Mon).
        _sut.TradingDaysBetween(new DateOnly(2026, 7, 6), new DateOnly(2026, 7, 13)).Should().Be(5);
    }

    [Fact]
    public void TradingDaysBetween_SpanningAHoliday_ExcludesIt()
    {
        // Wed 1 Jul -> Mon 6 Jul 2026 spans the 3 Jul holiday (Independence Day
        // observed) and a weekend. Trading days after the 1st: Thu 2 (only
        // 3rd is holiday), 6th Mon = 2 trading days.
        _sut.TradingDaysBetween(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 6)).Should().Be(2);
    }

    [Fact]
    public void TradingDaysBetween_OpenedFridayCheckedMonday_IsOneTradingDay()
    {
        // The motivating case: a position opened Friday is only ONE trading day
        // old by Monday, not three.
        _sut.TradingDaysBetween(new DateOnly(2026, 7, 10), new DateOnly(2026, 7, 13)).Should().Be(1);
    }

    [Fact]
    public void TradingDaysBetween_SameOrEarlierEnd_ReturnsZero()
    {
        _sut.TradingDaysBetween(new DateOnly(2026, 7, 10), new DateOnly(2026, 7, 10)).Should().Be(0);
        _sut.TradingDaysBetween(new DateOnly(2026, 7, 10), new DateOnly(2026, 7, 8)).Should().Be(0);
    }
}
