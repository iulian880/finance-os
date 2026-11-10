using System.Windows.Controls;
using System.Windows.Media.Imaging;
using FinanceOS.UI.Localization;
using H.NotifyIcon;

namespace FinanceOS.App;

/// <summary>Notification-area icon: left click opens the window, right click shows Open / Exit.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly Localizer _localizer;
    private readonly MenuItem _openItem;
    private readonly MenuItem _exitItem;

    public TrayIcon(Action open, Action exit, Localizer localizer)
    {
        _localizer = localizer;
        var openItem = _openItem = new MenuItem();
        openItem.Click += (_, _) => open();
        var exitItem = _exitItem = new MenuItem();
        exitItem.Click += (_, _) => exit();
        UpdateTexts();
        localizer.Changed += OnLanguageChanged;

        var menu = new ContextMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _icon = new TaskbarIcon
        {
            ToolTipText = "Finance OS",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => open();

        // Efficiency mode stays off: it lowers process priority, and reminder timers must stay punctual.
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void Dispose()
    {
        _localizer.Changed -= OnLanguageChanged;
        _icon.Dispose();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => UpdateTexts();

    private void UpdateTexts()
    {
        _openItem.Header = _localizer["Tray.Open"];
        _exitItem.Header = _localizer["Common.Exit"];
    }
}
