using System.Windows;
using Microsoft.Win32;

namespace FinanceOS.UI.Theming;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Applies the active theme + accent to the application resources and follows the Windows
/// light/dark setting when the preference is <see cref="ThemePreference.System"/>.
/// Persisting the choice belongs to the settings service (M12).
/// </summary>
public sealed class ThemeManager
{
    private Application? _app;
    private ResourceDictionary? _active;

    public static ThemeManager Current { get; } = new();

    public ThemePreference Preference { get; private set; } = ThemePreference.System;

    public AccentOption Accent { get; private set; } = Accents.Default;

    public bool IsDark { get; private set; }

    public ThemePalette Palette => IsDark ? ThemePalette.Dark : ThemePalette.Light;

    public AccentColors AccentColors { get; private set; } = AccentColors.Derive(Accents.Default, ThemePalette.Light);

    /// <summary>True when "Show animations in Windows" is off. Motion helpers must then skip animations.</summary>
    public static bool ReducedMotion => !SystemParameters.ClientAreaAnimation;

    /// <summary>Raised on the UI thread after resources were swapped.</summary>
    public event EventHandler? Changed;

    public void Initialize(Application app, ThemePreference preference = ThemePreference.System, string? accentId = null)
    {
        if (_app is not null)
        {
            throw new InvalidOperationException("ThemeManager is already initialized.");
        }

        _app = app;
        Preference = preference;
        Accent = Accents.Find(accentId);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        app.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        Apply();
    }

    public void SetPreference(ThemePreference preference)
    {
        Preference = preference;
        Apply();
    }

    public void SetAccent(AccentOption accent)
    {
        Accent = accent;
        Apply();
    }

    /// <summary>Reads the Windows "app mode" setting; defaults to light if it cannot be read.</summary>
    public static bool SystemPrefersDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (Preference == ThemePreference.System && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            _app?.Dispatcher.BeginInvoke(Apply);
        }
    }

    private void Apply()
    {
        if (_app is null)
        {
            throw new InvalidOperationException("Call Initialize first.");
        }

        IsDark = Preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => SystemPrefersDark(),
        };
        AccentColors = AccentColors.Derive(Accent, Palette);

        var next = ThemeResources.Build(Palette, AccentColors);
        var merged = _app.Resources.MergedDictionaries;
        if (_active is not null)
        {
            merged.Remove(_active);
        }

        merged.Add(next);
        _active = next;

        foreach (Window window in _app.Windows)
        {
            WindowTheming.ApplyTitleBar(window, IsDark);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
