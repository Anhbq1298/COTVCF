using System.Globalization;
using System.Windows.Data;

namespace COT_VCF.Presentation;

public sealed class NullableDoubleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double number ? number.ToString("F2", culture) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (TryParseEngineeringNumber(text, out var number)) return number;
        throw new FormatException("Enter a finite number.");
    }

    private static bool TryParseEngineeringNumber(string text, out double number)
    {
        var normalized = text.Trim();
        var hasDot = normalized.Contains('.');
        var hasComma = normalized.Contains(',');

        // Engineering inputs do not use digit grouping. Accept either common decimal separator so
        // values copied from regional spreadsheets/keyboards behave consistently on every machine.
        if (hasDot && hasComma)
        {
            number = default;
            return false;
        }
        if (hasComma) normalized = normalized.Replace(',', '.');

        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number);
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
