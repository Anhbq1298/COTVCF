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
