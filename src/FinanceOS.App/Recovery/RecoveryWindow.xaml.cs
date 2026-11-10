using System.Globalization;
using System.Windows;
using System.Windows.Data;
using FinanceOS.UI.Theming;

namespace FinanceOS.App.Recovery;

/// <summary>View for <see cref="RecoveryViewModel"/>. Closing the window counts as "Exit".</summary>
public partial class RecoveryWindow : Window
{
    public RecoveryWindow(RecoveryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        WindowTheming.Track(this);
    }

    /// <summary>Visible when the value is not null or empty.</summary>
    public static IValueConverter NullToCollapsed { get; } = new VisibilityConverter(v => v is string s ? s.Length > 0 : v is not null);

    /// <summary>Collapsed when true.</summary>
    public static IValueConverter TrueToCollapsed { get; } = new VisibilityConverter(v => v is not true);

    private sealed class VisibilityConverter(Func<object?, bool> isVisible) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            isVisible(value) ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
