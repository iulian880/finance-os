using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FinanceOS.App.Services;
using FinanceOS.App.Shell;
using FinanceOS.UI.Localization;

namespace FinanceOS.App.Onboarding;

/// <summary>
/// First run (PROMPT §23): welcome and language, Simple or Advanced, theme, working hours, notifications
/// and sample data. Every choice applies at once through the Settings page (the same fields, nothing
/// duplicated) and is saved; "Skip" keeps the defaults and opens the app.
/// </summary>
public sealed partial class OnboardingViewModel : ObservableObject
{
    public const int StepCount = 5;

    private readonly UserSettings _user;
    private readonly Localizer _l;
    private readonly Func<int> _addDemo;
    private readonly Action<int> _done;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsModeStep), nameof(IsThemeStep), nameof(IsHoursStep), nameof(IsNotificationsStep),
        nameof(IsFirst), nameof(IsLast), nameof(Progress), nameof(NextLabel))]
    private int _step;

    /// <summary>Checked by default only when there are no tasks yet (a real first run).</summary>
    [ObservableProperty]
    private bool _addDemoData;

    /// <param name="settings">The Settings page: its fields are the onboarding fields.</param>
    /// <param name="addDemo">Adds the sample data; returns how many tasks.</param>
    /// <param name="done">Closes onboarding; gets the number of sample tasks added (0 for none).</param>
    public OnboardingViewModel(SettingsPageViewModel settings, UserSettings user, Localizer localizer, bool hasTasks, Func<int> addDemo, Action<int> done)
    {
        Settings = settings;
        _user = user;
        _l = localizer;
        _addDemo = addDemo;
        _done = done;
        _addDemoData = !hasTasks;
        localizer.Changed += (_, _) => OnPropertyChanged(string.Empty);
        user.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsSimple));
            OnPropertyChanged(nameof(IsAdvanced));
        };
    }

    public SettingsPageViewModel Settings { get; }

    public bool IsWelcome => Step == 0;

    public bool IsModeStep => Step == 1;

    public bool IsThemeStep => Step == 2;

    public bool IsHoursStep => Step == 3;

    public bool IsNotificationsStep => Step == 4;

    public bool IsFirst => Step == 0;

    public bool IsLast => Step == StepCount - 1;

    /// <summary>"Pasul 2 din 5".</summary>
    public string Progress => _l.Format("Onboarding.Progress", Step + 1, StepCount);

    public string NextLabel => _l[IsLast ? "Onboarding.Finish" : "Onboarding.Next"];

    public bool IsSimple
    {
        get => _user.Mode == ExperienceMode.Simple;
        set
        {
            if (value)
            {
                _user.SetMode(ExperienceMode.Simple);
            }
        }
    }

    public bool IsAdvanced
    {
        get => _user.Mode == ExperienceMode.Advanced;
        set
        {
            if (value)
            {
                _user.SetMode(ExperienceMode.Advanced);
            }
        }
    }

    [RelayCommand]
    private void Next()
    {
        if (IsLast)
        {
            Finish(AddDemoData);
        }
        else
        {
            Step++;
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (Step > 0)
        {
            Step--;
        }
    }

    /// <summary>Keeps what was chosen so far, defaults for the rest, no sample data.</summary>
    [RelayCommand]
    private void Skip() => Finish(withDemo: false);

    private void Finish(bool withDemo)
    {
        _user.Update(p => p with { Onboarded = true });
        _done(withDemo ? _addDemo() : 0);
    }
}
