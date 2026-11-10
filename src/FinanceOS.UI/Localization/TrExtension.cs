using System.Windows.Data;
using System.Windows.Markup;

namespace FinanceOS.UI.Localization;

/// <summary>
/// <c>Text="{loc:Tr Settings.Language}"</c>: a one-way binding to <see cref="Localizer.Current"/>, so the
/// text follows a language change without reopening the window. Works in templates and style setters.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Localizer.Current, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
