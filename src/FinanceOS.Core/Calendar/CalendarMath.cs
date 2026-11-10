namespace FinanceOS.Core.Calendar;

/// <summary>A public holiday: date and a stable key (the UI shows the name in its language).</summary>
public sealed record Holiday(DateOnly Date, string Key);

/// <summary>Calendar arithmetic for the views (M11). Pure; weeks start on Monday (Romania, ISO 8601).</summary>
public static class CalendarMath
{
    /// <summary>The 42 days (6 full weeks, Monday first) a month view shows for <paramref name="month"/>.</summary>
    public static IReadOnlyList<DateOnly> MonthGrid(DateOnly month)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var start = StartOfWeek(first);
        return [.. Enumerable.Range(0, 42).Select(start.AddDays)];
    }

    public static DateOnly StartOfWeek(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static bool IsWeekend(DateOnly date) => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>Orthodox Easter Sunday (Gregorian date): Meeus' Julian algorithm plus the calendar offset.</summary>
    public static DateOnly OrthodoxEaster(int year)
    {
        var a = year % 4;
        var b = year % 7;
        var c = year % 19;
        var d = ((19 * c) + 15) % 30;
        var e = ((2 * a) + (4 * b) - d + 34) % 7;
        var month = (d + e + 114) / 31;
        var day = ((d + e + 114) % 31) + 1;
        return new DateOnly(year, month, day).AddDays((year / 100) - (year / 400) - 2); // 13 days in 1900–2099
    }

    /// <summary>Public holidays in Romania (Codul muncii, art. 139), Orthodox Easter based ones included.</summary>
    public static IReadOnlyList<Holiday> RomanianHolidays(int year)
    {
        var easter = OrthodoxEaster(year);
        var pentecost = easter.AddDays(49);
        Holiday[] holidays =
        [
            new(new(year, 1, 1), "NewYear"),
            new(new(year, 1, 2), "NewYear2"),
            new(new(year, 1, 6), "Epiphany"),
            new(new(year, 1, 7), "StJohn"),
            new(new(year, 1, 24), "Union"),
            new(easter.AddDays(-2), "GoodFriday"),
            new(easter, "Easter"),
            new(easter.AddDays(1), "EasterMonday"),
            new(new(year, 5, 1), "LabourDay"),
            new(new(year, 6, 1), "ChildrensDay"),
            new(pentecost, "Pentecost"),
            new(pentecost.AddDays(1), "PentecostMonday"),
            new(new(year, 8, 15), "Assumption"),
            new(new(year, 11, 30), "StAndrew"),
            new(new(year, 12, 1), "NationalDay"),
            new(new(year, 12, 25), "Christmas"),
            new(new(year, 12, 26), "Christmas2"),
        ];
        return [.. holidays.OrderBy(h => h.Date)];
    }
}
