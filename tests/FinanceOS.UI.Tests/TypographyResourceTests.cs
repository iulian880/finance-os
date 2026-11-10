using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace FinanceOS.UI.Tests;

/// <summary>
/// Loads the real XAML dictionaries and applies the styles to controls. Regression test for M1:
/// StaticResource values that cannot be resolved inside a Source-loaded dictionary are silently
/// dropped, which made every text style render at the default 12 px.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class TypographyResourceTests
{
    [Theory]
    [InlineData("Text.Display", 32.0)]
    [InlineData("Text.Headline", 24.0)]
    [InlineData("Text.Title", 20.0)]
    [InlineData("Text.Subheading", 16.0)]
    [InlineData("Text.Body", 14.0)]
    [InlineData("Text.Small", 13.0)]
    [InlineData("Text.Caption", 12.0)]
    [InlineData("Text.Numeric", 14.0)]
    public void Text_styles_apply_their_font_size_and_family(string styleKey, double expectedSize)
    {
        RunOnSta(() =>
        {
            var dictionary = (ResourceDictionary)Application.LoadComponent(
                new Uri("/FinanceOS.UI;component/Themes/Typography.xaml", UriKind.Relative));
            var text = new TextBlock { Style = (Style)dictionary[styleKey] };

            Assert.Equal(expectedSize, text.FontSize);
            Assert.Contains("Inter", text.FontFamily.Source, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Entry_dictionary_exposes_tokens_and_styles()
    {
        RunOnSta(() =>
        {
            var dictionary = (ResourceDictionary)Application.LoadComponent(
                new Uri("/FinanceOS.UI;component/Themes/FinanceOS.xaml", UriKind.Relative));

            Assert.Equal(16.0, dictionary["Space.16"]);
            Assert.Equal(new CornerRadius(12), dictionary["Radius.Large"]);
            Assert.IsType<Style>(dictionary["Text.Body"]);
        });
    }

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
