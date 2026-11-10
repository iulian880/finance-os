using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace FinanceOS.App.Shell;

/// <summary>
/// Turns an icon resource key from a ViewModel ("Icon.Sun") into its Geometry, so ViewModels stay free
/// of WPF types. Unknown keys render no icon rather than failing.
/// </summary>
[ValueConversion(typeof(string), typeof(Geometry))]
public sealed class IconKeyConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key ? Application.Current?.TryFindResource(key) as Geometry : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
