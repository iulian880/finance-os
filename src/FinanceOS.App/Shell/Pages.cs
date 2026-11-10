using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FinanceOS.App.Services;
using FinanceOS.Core.Tasks;
using FinanceOS.Data.Backup;
using FinanceOS.UI.Localization;
using FinanceOS.UI.Theming;

namespace FinanceOS.App.Shell;

/// <summary>
/// Base for the page shown in the content area. Views are chosen by DataTemplate per type.
/// Texts come from the <see cref="Localizer"/>; a language change refreshes every binding on the page.
/// </summary>
public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel(SectionInfo info, Localizer localizer)
    {
        Info = info;
        L = localizer;
        localizer.Changed += (_, _) =>
        {
            OnLanguageChanged();
            OnPropertyChanged(string.Empty);
        };
    }

    public SectionInfo Info { get; }

    public Section Section => Info.Section;

    public string Title => Info.Title(L);

    public string Subtitle => Info.Subtitle(L);

    /// <summary>
    /// What the right "Details" panel shows for this page (e.g. the selected task), or null for the
    /// panel's empty state. When it changes to a value, the shell opens the panel.
    /// </summary>
    public virtual object? Details => null;

    /// <summary>Called each time the page is shown (navigation). Pages that depend on "now" re-read here.</summary>
    public virtual void OnActivated()
    {
    }

    /// <summary>Pages that scroll their own content (virtualized lists) get the full height instead of the shell's scroll viewer.</summary>
    public virtual bool ScrollsItself => false;

    protected Localizer L { get; }

    /// <summary>Runs before the page tells its bindings to refresh (e.g. to update option labels).</summary>
    protected virtual void OnLanguageChanged()
    {
    }
}

/// <summary>A section whose features arrive in later milestones: honest empty state, no dead buttons.</summary>
public sealed class ComingSoonPageViewModel(SectionInfo info, Localizer localizer) : PageViewModel(info, localizer)
{
    public string EmptyTitle => Info.EmptyTitle(L);

    public string EmptyDescription => Info.EmptyDescription(L);
}

/// <summary>Option row for a Select bound to a value; the label follows the language.</summary>
public sealed class ChoiceOption<T>(T value, Func<string> label) : ObservableObject
{
    public T Value { get; } = value;

    public string Label => label();

    public void Refresh() => OnPropertyChanged(nameof(Label));

    public override string ToString() => Label;
}

/// <summary>Settings available today: appearance and language (applied immediately), where data lives, manual backup.</summary>
public sealed partial class SettingsPageViewModel : PageViewModel
{
    private readonly IThemeService _theme;
    private readonly IDataMaintenance _data;
    private readonly ILauncher _launcher;
    private readonly TimeProvider _time;
    private readonly IStartupRegistration? _startup;
    private readonly UserSettings? _settings;
    private readonly TaskService? _tasks;
    private bool _syncing;

    /// <summary>Time of the backup made with "Back up now", for the confirmation under the button.</summary>
    private DateTimeOffset? _manualBackupUtc;

    [ObservableProperty]
    private ChoiceOption<ThemePreference>? _selectedTheme;

    [ObservableProperty]
    private ChoiceOption<AccentOption>? _selectedAccent;

    [ObservableProperty]
    private ChoiceOption<AppLanguage>? _selectedLanguage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeDescription))]
    private ChoiceOption<ExperienceMode>? _selectedMode;

    [ObservableProperty]
    private ChoiceOption<string?>? _selectedDateFormat;

    /// <summary>True when the last "back up now" failed; the message under the button is then an error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackupMessage))]
    private bool _backupFailed;

    public SettingsPageViewModel(IThemeService theme, IDataMaintenance data, ILauncher launcher, TimeProvider time, Localizer localizer,
        IStartupRegistration? startup = null, UserSettings? settings = null, TaskService? tasks = null)
        : base(SectionCatalog.Get(Section.Settings), localizer)
    {
        _tasks = tasks;
        _startup = startup;
        _settings = settings;
        _theme = theme;
        _data = data;
        _launcher = launcher;
        _time = time;

        // The automatic backup runs on a worker thread, usually right after this page was created.
        var ui = SynchronizationContext.Current;
        data.BackupsChanged += (_, _) =>
        {
            if (ui is null)
            {
                OnPropertyChanged(nameof(LastBackupText));
            }
            else
            {
                ui.Post(_ => OnPropertyChanged(nameof(LastBackupText)), null);
            }
        };
        ThemeOptions =
        [
            new(ThemePreference.System, () => L["Settings.Theme.System"]),
            new(ThemePreference.Light, () => L["Settings.Theme.Light"]),
            new(ThemePreference.Dark, () => L["Settings.Theme.Dark"]),
        ];
        Accents = theme.Accents.Select(a => new ChoiceOption<AccentOption>(a, () => L[$"Accent.{a.Id}"])).ToList();

        // Each language is shown in its own language, so it can be found whatever is selected now.
        LanguageOptions =
        [
            new(AppLanguage.English, () => "English"),
            new(AppLanguage.Romanian, () => "Română"),
        ];
        ModeOptions =
        [
            new(ExperienceMode.Simple, () => L["Mode.Simple"]),
            new(ExperienceMode.Advanced, () => L["Mode.Advanced"]),
        ];

        // Each format shown on today's date: "09.11.2026", "2026-11-09"...
        DateFormatOptions =
        [
            new(null, () => L.Format("Settings.DateFormat.System", DateExample(null))),
            .. DateFormats.Patterns.Select(p => new ChoiceOption<string?>(p, () => DateExample(p))),
        ];
        Sync();
        theme.Changed += (_, _) => Sync();
        if (settings is not null)
        {
            settings.Changed += (_, _) => Sync();
        }
    }

    public IReadOnlyList<ChoiceOption<ThemePreference>> ThemeOptions { get; }

    public IReadOnlyList<ChoiceOption<AccentOption>> Accents { get; }

    public IReadOnlyList<ChoiceOption<AppLanguage>> LanguageOptions { get; }

    public IReadOnlyList<ChoiceOption<ExperienceMode>> ModeOptions { get; }

    public IReadOnlyList<ChoiceOption<string?>> DateFormatOptions { get; }

    /// <summary>The sample tasks from onboarding are still there: offer to remove them.</summary>
    public bool HasDemoData => _tasks?.HasDemoData == true;

    public override void OnActivated() => OnPropertyChanged(nameof(HasDemoData));

    /// <summary>Removes the sample tasks and names in one step (Ctrl+Z brings them back).</summary>
    [RelayCommand]
    private void RemoveDemoData()
    {
        _tasks?.RemoveDemoData();
        OnPropertyChanged(nameof(HasDemoData));
    }

    /// <summary>Mode and date format are offered when settings are saved (the real app).</summary>
    public bool HasPreferences => _settings is not null;

    public string ModeDescription =>
        L[SelectedMode?.Value == ExperienceMode.Simple ? "Settings.Mode.Simple.Description" : "Settings.Mode.Advanced.Description"];

    public string DataFolder => _data.DataFolder;

    /// <summary>Working and quiet hours are shown when settings are saved (the real app).</summary>
    public bool HasSchedule => _settings is not null;

    public TimeSpan? WorkStart
    {
        get => _settings?.WorkStart.ToTimeSpan();
        set => SaveTime(value, (p, t) => p with { WorkStart = t });
    }

    public TimeSpan? WorkEnd
    {
        get => _settings?.WorkEnd.ToTimeSpan();
        set => SaveTime(value, (p, t) => p with { WorkEnd = t });
    }

    /// <summary>Quiet hours on: reminders wait silently and come when they end (20:00–08:00 to start with).</summary>
    public bool QuietEnabled
    {
        get => _settings?.Quiet is not null;
        set
        {
            _settings?.Update(p => value
                ? p with { QuietStart = p.QuietStart ?? "20:00", QuietEnd = p.QuietEnd ?? "08:00" }
                : p with { QuietStart = null, QuietEnd = null });
            OnPropertyChanged();
            OnPropertyChanged(nameof(QuietStart));
            OnPropertyChanged(nameof(QuietEnd));
        }
    }

    public TimeSpan? QuietStart
    {
        get => _settings?.Quiet?.Start.ToTimeSpan();
        set => SaveTime(value, (p, t) => p with { QuietStart = t });
    }

    public TimeSpan? QuietEnd
    {
        get => _settings?.Quiet?.End.ToTimeSpan();
        set => SaveTime(value, (p, t) => p with { QuietEnd = t });
    }

    public bool QuietWeekends
    {
        get => _settings?.Current.QuietWeekends == true;
        set
        {
            _settings?.Update(p => p with { QuietWeekends = value });
            OnPropertyChanged();
        }
    }

    /// <summary>The start-up card is shown only where it can work (the real app; not in previews).</summary>
    public bool CanStartWithWindows => _startup is not null;

    /// <summary>Start with Windows, in the tray. Applied immediately; if Windows refuses, the toggle stays off.</summary>
    public bool StartWithWindows
    {
        get => _startup?.IsEnabled == true;
        set
        {
            _startup?.Set(value);
            OnPropertyChanged();
        }
    }

    public string Version => AppInfo.Version;

    public string Author => AppInfo.Author;

    public string AuthorRole => AppInfo.AuthorRole;

    /// <summary>"© 2026 Petcu Iulian. Toate drepturile rezervate."</summary>
    public string CopyrightText => L.Format("Settings.About.Copyright", AppInfo.CopyrightYear, AppInfo.Author);

    public string ContactEmail => AppInfo.ContactEmail;

    public string LastBackupText => _data.LastBackupUtc is { } at
        ? L.Format("Settings.Backup.Last", DisplayFormat.DateAndTime(LocalTime(at), L.Culture))
        : L["Settings.Backup.None"];

    /// <summary>Outcome of the last "back up now": confirmation or error, shown under the button. Null before the first try.</summary>
    public string? BackupMessage => BackupFailed
        ? L["Settings.Backup.Failed"]
        : _manualBackupUtc is { } at ? L.Format("Settings.Backup.Created", DisplayFormat.DateAndTime(LocalTime(at), L.Culture)) : null;

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task BackupNowAsync()
    {
        _manualBackupUtc = null;
        BackupFailed = false;
        OnPropertyChanged(nameof(BackupMessage));
        try
        {
            var backup = await _data.BackupNowAsync();
            _manualBackupUtc = backup.CreatedUtc;
        }
        catch (BackupException)
        {
            // Technical details are in the log; the user needs to know it did not happen.
            BackupFailed = true;
        }

        OnPropertyChanged(nameof(BackupMessage));
        OnPropertyChanged(nameof(LastBackupText));
    }

    [RelayCommand]
    private void OpenDataFolder() => _data.OpenDataFolder();

    /// <summary>Opens a new e-mail to the author with a prefilled subject (suggestions and bug reports).</summary>
    [RelayCommand]
    private void SendFeedback() => _launcher.Open(AppInfo.FeedbackMailto(L));

    protected override void OnLanguageChanged()
    {
        foreach (var option in ThemeOptions)
        {
            option.Refresh();
        }

        foreach (var option in Accents)
        {
            option.Refresh();
        }

        foreach (var option in ModeOptions)
        {
            option.Refresh();
        }

        foreach (var option in DateFormatOptions)
        {
            option.Refresh();
        }

        Sync();
    }

    private void SaveTime(TimeSpan? value, Func<Preferences, string, Preferences> change, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        if (value is { } time && _settings is not null)
        {
            _settings.Update(p => change(p, UserSettings.Format(TimeOnly.FromTimeSpan(time))));
        }

        OnPropertyChanged(name);
    }

    private DateTime LocalTime(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, _time.LocalTimeZone).DateTime;

    partial void OnSelectedThemeChanged(ChoiceOption<ThemePreference>? value)
    {
        if (!_syncing && value is not null)
        {
            _theme.SetPreference(value.Value);
        }
    }

    partial void OnSelectedAccentChanged(ChoiceOption<AccentOption>? value)
    {
        if (!_syncing && value is not null)
        {
            _theme.SetAccent(value.Value);
        }
    }

    partial void OnSelectedLanguageChanged(ChoiceOption<AppLanguage>? value)
    {
        if (!_syncing && value is not null)
        {
            L.SetLanguage(value.Value);
        }
    }

    partial void OnSelectedModeChanged(ChoiceOption<ExperienceMode>? value)
    {
        if (!_syncing && value is not null)
        {
            _settings?.SetMode(value.Value);
        }
    }

    partial void OnSelectedDateFormatChanged(ChoiceOption<string?>? value)
    {
        if (!_syncing && value is not null)
        {
            _settings?.Update(p => p with { DateFormat = value.Value });
        }
    }

    private string DateExample(string? pattern) => DateFormats.Example(LocalTime(_time.GetUtcNow()), L.Culture, pattern);

    private void Sync()
    {
        _syncing = true;
        SelectedTheme = ThemeOptions.First(o => o.Value == _theme.Preference);
        SelectedAccent = Accents.FirstOrDefault(o => o.Value.Id == _theme.Accent.Id);
        SelectedLanguage = LanguageOptions.First(o => o.Value == L.Language);
        SelectedMode = ModeOptions.First(o => o.Value == (_settings?.Mode ?? ExperienceMode.Advanced));
        SelectedDateFormat = DateFormatOptions.First(o => o.Value == _settings?.DateFormat);
        _syncing = false;
    }
}

/// <summary>Date display in the UI language (date pickers still follow Windows until M12).</summary>
public static class DisplayFormat
{
    /// <summary>"Marți, 4 noiembrie 2026" / "Tuesday, 4 November 2026".</summary>
    public static string LongDate(DateTime date, CultureInfo culture) => Capitalize(date.ToString("dddd, d MMMM yyyy", culture), culture);

    /// <summary>"Marți, 4 noiembrie" / "Tuesday, 4 November".</summary>
    public static string DayAndMonth(DateTime date, CultureInfo culture) => Capitalize(date.ToString("dddd, d MMMM", culture), culture);

    /// <summary>"5 noiembrie 2026, 10:30" (for backups and other moments in the past).</summary>
    public static string DateAndTime(DateTime moment, CultureInfo culture) => moment.ToString("d MMMM yyyy, HH:mm", culture);

    private static string Capitalize(string text, CultureInfo culture) =>
        text.Length == 0 ? text : char.ToUpper(text[0], culture) + text[1..];
}
