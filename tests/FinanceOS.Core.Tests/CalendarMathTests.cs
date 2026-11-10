using FinanceOS.Core.Calendar;
using Xunit;

namespace FinanceOS.Core.Tests;

public sealed class CalendarMathTests
{
    [Theory]
    [InlineData(2024, 5, 5)]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 12)]
    [InlineData(2027, 5, 2)]
    [InlineData(2028, 4, 16)]
    public void Orthodox_easter_matches_the_church_calendar(int year, int month, int day)
    {
        Assert.Equal(new DateOnly(year, month, day), CalendarMath.OrthodoxEaster(year));
    }

    [Fact]
    public void Romanian_holidays_2026_include_the_moving_ones()
    {
        var holidays = CalendarMath.RomanianHolidays(2026).ToDictionary(h => h.Key, h => h.Date);

        Assert.Equal(17, holidays.Count);
        Assert.Equal(new DateOnly(2026, 4, 10), holidays["GoodFriday"]);
        Assert.Equal(new DateOnly(2026, 4, 13), holidays["EasterMonday"]);
        Assert.Equal(new DateOnly(2026, 5, 31), holidays["Pentecost"]);
        Assert.Equal(new DateOnly(2026, 6, 1), holidays["PentecostMonday"]); // same day as Children's Day
        Assert.Equal(new DateOnly(2026, 12, 1), holidays["NationalDay"]);
    }

    [Fact]
    public void Month_grid_is_six_weeks_starting_on_monday()
    {
        var grid = CalendarMath.MonthGrid(new DateOnly(2026, 11, 1)); // 1 Nov 2026 is a Sunday

        Assert.Equal(42, grid.Count);
        Assert.Equal(new DateOnly(2026, 10, 26), grid[0]);
        Assert.All(grid.Where((_, i) => i % 7 == 0), d => Assert.Equal(DayOfWeek.Monday, d.DayOfWeek));
        Assert.Contains(new DateOnly(2026, 11, 30), grid);
        Assert.True(CalendarMath.IsWeekend(new DateOnly(2026, 11, 14)));
    }
}
