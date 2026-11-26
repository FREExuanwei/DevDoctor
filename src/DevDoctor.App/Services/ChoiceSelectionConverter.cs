using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DevDoctor.App.Services;

public sealed class ChoiceSelectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.Ordinal);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true && parameter is string choice ? choice : Binding.DoNothing;
}
