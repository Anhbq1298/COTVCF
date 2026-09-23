using System.Globalization;
using System.Windows;
using System.Windows.Data;
using COT_VCF.Domain;

namespace COT_VCF.Presentation;

/// <summary>Enum → friendly label for the cable Sync mode dropdown ("SameValues" → "Same Values").</summary>
public sealed class CableSyncModeLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is CableSyncMode m ? m.Label() : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Non-empty string → true (drives the Sync-column red-flag).</summary>
public sealed class NonEmptyStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        !string.IsNullOrEmpty(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class NullableDoubleConverter : IValueConverter
{
    // Show the number as typed — no forced trailing ".00", no scientific notation for realistic magnitudes.
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double number && double.IsFinite(number) ? number.ToString("0.######", culture) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString()?.Trim();
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
