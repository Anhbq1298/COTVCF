using System.Globalization;
using System.Windows.Data;

namespace Mcotis.Desktop.Presentation;

/// <summary>true ↔ false (e.g. the Select tool is on while pan mode is off).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public static InverseBoolConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
