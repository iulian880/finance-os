using System.ComponentModel;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using FinanceOS.UI.Localization;
using Xunit;
using static FinanceOS.UI.Tests.UiHarness;

namespace FinanceOS.UI.Tests;

/// <summary>M5b: runtime language switch. <see cref="TrExtension"/> bindings follow <see cref="Localizer.Current"/>.</summary>
[Collection(WpfCollection.Name)]
public sealed class LocalizerTests
{
    private const string Namespaces =
        """xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:loc="clr-namespace:FinanceOS.UI.Localization;assembly=FinanceOS.UI" """;

    [Fact]
    public void Tr_binding_changes_text_when_the_language_changes_without_reloading()
    {
        RunOnSta(() => WithCurrentLanguage(AppLanguage.English, current =>
        {
            var text = (TextBlock)XamlReader.Parse(
                $$"""<TextBlock {{Namespaces}} Text="{loc:Tr Ui.Close}" AutomationProperties.Name="{loc:Tr Ui.Dismiss}" />""");
            Assert.Equal("Close", text.Text);
            Assert.Equal("Dismiss", AutomationProperties.GetName(text));

            current.SetLanguage(AppLanguage.Romanian);

            Assert.Equal("Închide", text.Text);
            Assert.Equal("Închide notificarea", AutomationProperties.GetName(text));
        }));
    }

    [Fact]
    public void Tr_works_in_style_setters_and_control_templates()
    {
        RunOnSta(() => WithCurrentLanguage(AppLanguage.Romanian, current =>
        {
            var button = (Button)XamlReader.Parse(
                $$"""
                <Button {{Namespaces}}>
                    <Button.Style>
                        <Style TargetType="Button">
                            <Setter Property="AutomationProperties.Name" Value="{loc:Tr Ui.Previous}" />
                            <Setter Property="Template">
                                <Setter.Value>
                                    <ControlTemplate TargetType="Button">
                                        <TextBlock x:Name="Label" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Text="{loc:Tr Ui.Next}" />
                                    </ControlTemplate>
                                </Setter.Value>
                            </Setter>
                        </Style>
                    </Button.Style>
                </Button>
                """);
            Host(button);
            var label = (TextBlock)button.Template.FindName("Label", button);
            Assert.Equal("Anterior", AutomationProperties.GetName(button));
            Assert.Equal("Următor", label.Text);

            current.SetLanguage(AppLanguage.English);

            Assert.Equal("Previous", AutomationProperties.GetName(button));
            Assert.Equal("Next", label.Text);
        }));
    }

    [Fact]
    public void Language_change_refreshes_indexer_bindings_and_raises_changed_once()
    {
        var localizer = new Localizer(AppLanguage.English);
        var properties = new List<string?>();
        ((INotifyPropertyChanged)localizer).PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        var changed = 0;
        localizer.Changed += (_, _) => changed++;

        localizer.SetLanguage(AppLanguage.English);
        Assert.Empty(properties);
        Assert.Equal(0, changed);

        localizer.SetLanguage(AppLanguage.Romanian);

        Assert.Contains(Localizer.IndexerName, properties);
        Assert.Equal(1, changed);
        Assert.Equal("ro-RO", localizer.Culture.Name);
        Assert.Equal("Următor", localizer["Ui.Next"]);
    }

    [Fact]
    public void Missing_key_shows_up_marked_and_cultures_are_fixed_per_language()
    {
        var localizer = new Localizer(AppLanguage.Romanian);
        Assert.Equal("⟦Nope⟧", localizer["Nope"]);
        Assert.Equal("ro-RO", localizer.Culture.Name);
        Assert.Equal("en-GB", Localizer.CultureFor(AppLanguage.English).Name);
    }

    /// <summary>Runs <paramref name="test"/> with the app-wide localizer on <paramref name="language"/>, then restores it.</summary>
    private static void WithCurrentLanguage(AppLanguage language, Action<Localizer> test)
    {
        var current = Localizer.Current;
        var before = current.Language;
        try
        {
            current.SetLanguage(language);
            test(current);
        }
        finally
        {
            current.SetLanguage(before);
        }
    }
}
