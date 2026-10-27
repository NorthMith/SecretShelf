using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SecretShelf.App;

/// <summary>bool → Visibility; ConverterParameter="not" инвертирует.</summary>
public sealed class BoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is true;
        if (parameter as string == "not") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
