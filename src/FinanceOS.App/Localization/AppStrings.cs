using System.Resources;
using FinanceOS.UI.Localization;

namespace FinanceOS.App.Localization;

/// <summary>The app's string table: <c>Strings.resx</c> (English, neutral) and <c>Strings.ro.resx</c>.</summary>
public static class AppStrings
{
    public static ResourceManager Manager { get; } = new("FinanceOS.App.Localization.Strings", typeof(AppStrings).Assembly);

    /// <summary>A localizer with the app's and the design system's strings (tests, previews).</summary>
    public static Localizer CreateLocalizer(AppLanguage language) => new(language, Manager);
}
