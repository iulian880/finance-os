using FinanceOS.UI.Controls;

namespace FinanceOS.App.Services;

/// <summary>In-app toasts as seen by ViewModels ("Moved to trash · Undo"). Behind an interface for tests.</summary>
public interface IInAppToasts
{
    void Show(ToastMessage toast);
}

/// <summary>
/// Shows toasts on the main window's <see cref="ToastHost"/>. The window attaches itself when it opens;
/// toasts raised while no window is open (the app is in the tray) are dropped: they describe something
/// the user just did in that window.
/// </summary>
public sealed class InAppToasts : IInAppToasts
{
    public ToastHost? Host { get; set; }

    public void Show(ToastMessage toast) => Host?.Show(toast);
}
