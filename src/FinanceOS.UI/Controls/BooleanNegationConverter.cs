using System.Globalization;
using System.Windows.Data;

namespace FinanceOS.UI.Controls;

/// <summary>
/// <c>true</c> ↔ <c>false</c>. Used to make a drop-down toggle ignore clicks while its popup is open:
/// the click that closes a StaysOpen=False popup would otherwise reopen it immediately.
/// </summary>
[ValueConversion(typeof(bool), typeof(bool))]
public sealed class BooleanNegationConverter : IValueConverter
{
    public static BooleanNegationConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
