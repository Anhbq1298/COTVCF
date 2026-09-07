using System.Globalization;
using System.Windows.Data;

namespace COT_VCF.Presentation;

public sealed class NullableDoubleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double number ? number.ToString("F2", culture) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Lenient: accept anything that reads as a number — thousands separators, exponent, leading sign,
        // decimal comma or dot. Unparseable text is ignored (keeps the last good value) rather than flagged.
        const NumberStyles styles = NumberStyles.Float | NumberStyles.AllowThousands | NumberStyles.AllowParentheses;
        if (double.TryParse(text, styles, culture, out var n) && double.IsFinite(n)) return n;
        if (double.TryParse(text, styles, CultureInfo.InvariantCulture, out n) && double.IsFinite(n)) return n;
        if (double.TryParse(text.Replace(',', '.'), styles, CultureInfo.InvariantCulture, out n) && double.IsFinite(n)) return n;
        return Binding.DoNothing;
    }
}

/// <summary>Binds a radio button's IsChecked to an enum value named by ConverterParameter (mutually exclusive).</summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name && Enum.TryParse(targetType, name, out var parsed) ? parsed! : Binding.DoNothing;
}
