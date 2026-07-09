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
}
