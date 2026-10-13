using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ExamBro.Config.Converters;

/// <summary>
/// Converts boolean values to System.Windows.Visibility and vice versa.
/// Supports inversion via ConverterParameter="Inverse" or "True".
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool boolVal = value is true;
        bool invert = string.Equals(parameter?.ToString(), "Inverse", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(parameter?.ToString(), "True", StringComparison.OrdinalIgnoreCase);

        if (invert)
        {
            boolVal = !boolVal;
        }

        return boolVal ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
        {
            bool boolVal = visibility == Visibility.Visible;
            bool invert = string.Equals(parameter?.ToString(), "Inverse", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(parameter?.ToString(), "True", StringComparison.OrdinalIgnoreCase);

            return invert ? !boolVal : boolVal;
        }

        return false;
    }
}
