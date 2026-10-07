using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WindowsPartitionManager.App.Controls;

public static class Converters
{
    public static readonly IValueConverter BoolToVisibility = new BoolToVisibilityConverter();

    private sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is Visibility.Visible;
    }
}
