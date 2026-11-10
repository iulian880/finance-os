using System.Globalization;
using FinanceOS.UI.Controls;
using Xunit;

namespace FinanceOS.UI.Tests;

public sealed class TimeParserTests
{
    private static readonly CultureInfo Ro = CultureInfo.GetCultureInfo("ro-RO");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    [Theory]
    [InlineData("9", "09:00")]
    [InlineData("09", "09:00")]
    [InlineData("0", "00:00")]
    [InlineData("930", "09:30")]
    [InlineData("0930", "09:30")]
    [InlineData("1430", "14:30")]
    [InlineData("9:30", "09:30")]
    [InlineData("9.30", "09:30")]
    [InlineData("9,30", "09:30")]
    [InlineData("9h30", "09:30")]
    [InlineData("9h", "09:00")]
    [InlineData("14 30", "14:30")]
    [InlineData("  7:05  ", "07:05")]
    [InlineData("23:59", "23:59")]
    [InlineData("2pm", "14:00")]
    [InlineData("2 PM", "14:00")]
    [InlineData("2:30 pm", "14:30")]
    [InlineData("11p", "23:00")]
    [InlineData("9a", "09:00")]
    [InlineData("12am", "00:00")]
    [InlineData("12pm", "12:00")]
    [InlineData("12:15 a.m.", "00:15")]
    [InlineData("1:45 p.m.", "13:45")]
    public void Parses_common_inputs(string input, string expected)
    {
        foreach (var culture in new[] { Ro, En })
        {
            Assert.True(TimeParser.TryParse(input, culture, out var time), $"'{input}' should parse in {culture.Name}");
            Assert.Equal(TimeSpan.ParseExact(expected, @"hh\:mm", CultureInfo.InvariantCulture), time);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("24")]
    [InlineData("24:00")]
    [InlineData("9:5")]
    [InlineData("9:60")]
    [InlineData("99")]
    [InlineData("13pm")]
    [InlineData("0am")]
    [InlineData("abc")]
    [InlineData("9:30:00")]
    [InlineData("12345")]
    [InlineData("pm")]
    [InlineData("-1")]
    public void Rejects_invalid_or_ambiguous_input(string? input)
    {
        Assert.False(TimeParser.TryParse(input, Ro, out _));
        Assert.False(TimeParser.TryParse(input, En, out _));
    }

    [Fact]
    public void Formats_with_the_culture_short_time_pattern()
    {
        var time = new TimeSpan(9, 30, 0);
        Assert.Equal("09:30", TimeParser.Format(time, Ro));

        var english = TimeParser.Format(time, En);
        Assert.StartsWith("9:30", english, StringComparison.Ordinal);
        Assert.EndsWith("AM", english, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ro-RO")]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("de-DE")]
    public void Formatted_times_parse_back_to_the_same_value(string cultureName)
    {
        // Includes ICU patterns such as en-US "h:mm a" (narrow no-break space before AM/PM).
        var culture = CultureInfo.GetCultureInfo(cultureName);
        for (var minutes = 0; minutes < 24 * 60; minutes += 5)
        {
            var time = TimeSpan.FromMinutes(minutes);
            var text = TimeParser.Format(time, culture);
            Assert.True(TimeParser.TryParse(text, culture, out var parsed), $"'{text}' ({cultureName}) did not parse");
            Assert.Equal(time, parsed);
        }
    }
}
