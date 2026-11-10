using System.Windows;
using System.Windows.Automation;
using System.Windows.Markup;
using FinanceOS.UI.Controls;
using Xunit;
using static FinanceOS.UI.Tests.UiHarness;

namespace FinanceOS.UI.Tests;

[Collection(WpfCollection.Name)]
public sealed class TimePickerTests
{
    private static TimePicker CreateRomanian()
    {
        var picker = new TimePicker { Language = XmlLanguage.GetLanguage("ro-RO") };
        Host(picker);
        return picker;
    }

    [Fact]
    public void Typed_text_is_parsed_and_normalized_on_commit()
    {
        RunOnSta(() =>
        {
            var picker = CreateRomanian();
            picker.TextBox!.Text = "930";

            Assert.True(picker.CommitText());
            Assert.Equal(new TimeSpan(9, 30, 0), picker.SelectedTime);
            Assert.Equal("09:30", picker.TextBox.Text);
        });
    }

    [Fact]
    public void Invalid_text_reverts_to_the_last_valid_value()
    {
        RunOnSta(() =>
        {
            var picker = CreateRomanian();
            picker.SelectedTime = new TimeSpan(14, 0, 0);
            picker.TextBox!.Text = "25:99";

            Assert.False(picker.CommitText());
            Assert.Equal(new TimeSpan(14, 0, 0), picker.SelectedTime);
            Assert.Equal("14:00", picker.TextBox.Text);
        });
    }

    [Fact]
    public void Empty_text_clears_the_value()
    {
        RunOnSta(() =>
        {
            var picker = CreateRomanian();
            picker.SelectedTime = new TimeSpan(8, 0, 0);
            picker.TextBox!.Text = "  ";

            Assert.True(picker.CommitText());
            Assert.Null(picker.SelectedTime);
            Assert.Equal(string.Empty, picker.TextBox.Text);
        });
    }

    [Theory]
    [InlineData("09:30", 1, "09:45")]
    [InlineData("09:40", 1, "09:45")]
    [InlineData("09:40", -1, "09:30")]
    [InlineData("09:30", -1, "09:15")]
    [InlineData("23:50", 1, "00:00")]
    [InlineData("00:00", -1, "23:45")]
    public void Step_moves_to_the_neighbouring_increment_and_wraps(string start, int direction, string expected)
    {
        RunOnSta(() =>
        {
            var picker = CreateRomanian();
            picker.SelectedTime = TimeSpan.Parse(start, System.Globalization.CultureInfo.InvariantCulture);
            picker.Step(direction);
            Assert.Equal(TimeSpan.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), picker.SelectedTime);
        });
    }

    [Fact]
    public void Selected_time_is_coerced_to_a_whole_minute_time_of_day()
    {
        RunOnSta(() =>
        {
            var picker = new TimePicker { SelectedTime = new TimeSpan(1, 2, 3, 30) };
            Assert.Equal(new TimeSpan(2, 3, 0), picker.SelectedTime);
        });
    }

    [Fact]
    public void Drop_down_lists_the_whole_day_in_increments()
    {
        RunOnSta(() =>
        {
            var picker = CreateRomanian();
            var options = picker.BuildOptions();
            Assert.Equal(96, options.Count);
            Assert.Equal("00:00", options[0].Label);
            Assert.Equal("23:45", options[^1].Label);

            picker.MinuteIncrement = 30;
            Assert.Equal(48, picker.BuildOptions().Count);
            Assert.Throws<ArgumentException>(() => picker.MinuteIncrement = 0);
        });
    }

    [Fact]
    public void Opening_the_list_selects_the_current_value()
    {
        RunOnSta(() =>
        {
            var picker = CreateRomanian();
            picker.SelectedTime = new TimeSpan(9, 40, 0);
            picker.IsDropDownOpen = true;

            var selected = Assert.IsType<TimeOption>(picker.List!.SelectedItem);
            Assert.Equal(new TimeSpan(9, 45, 0), selected.Value);
            picker.IsDropDownOpen = false;
        });
    }

    [Fact]
    public void Focus_and_accessible_name_live_on_the_inner_text_box()
    {
        RunOnSta(() =>
        {
            var picker = new TimePicker();
            FieldAssist.SetHeader(picker, "Reminder");
            Host(picker);

            Assert.False(picker.Focusable);
            Assert.False(picker.IsTabStop);
            Assert.True(picker.TextBox!.Focusable);
            Assert.Equal("Reminder", AutomationProperties.GetName(picker.TextBox));
        });
    }
}
