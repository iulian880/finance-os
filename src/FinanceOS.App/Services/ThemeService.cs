using FinanceOS.UI.Theming;

namespace FinanceOS.App.Services;

/// <summary>Theme and accent as seen by ViewModels; lets them be tested without WPF resources.</summary>
public interface IThemeService
{
    event EventHandler? Changed;

    bool IsDark { get; }

    ThemePreference Preference { get; }

    AccentOption Accent { get; }

    IReadOnlyList<AccentOption> Accents { get; }

    void SetPreference(ThemePreference preference);

    void SetAccent(AccentOption accent);
}

/// <summary><see cref="IThemeService"/> over the application-wide <see cref="ThemeManager"/>.</summary>
public sealed class ThemeService : IThemeService
{
    private readonly ThemeManager _manager = ThemeManager.Current;

    public event EventHandler? Changed
    {
        add => _manager.Changed += value;
        remove => _manager.Changed -= value;
    }

    public bool IsDark => _manager.IsDark;

    public ThemePreference Preference => _manager.Preference;

    public AccentOption Accent => _manager.Accent;

    public IReadOnlyList<AccentOption> Accents => UI.Theming.Accents.All;

    public void SetPreference(ThemePreference preference) => _manager.SetPreference(preference);

    public void SetAccent(AccentOption accent) => _manager.SetAccent(accent);
}
