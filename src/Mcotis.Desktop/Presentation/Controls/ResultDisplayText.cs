using System.Globalization;

namespace Mcotis.Desktop.Presentation.Controls;

/// <summary>Compact display units for values drawn on the FEM viewport; the stored results remain in kN and kN·m.</summary>
internal static class ResultDisplayText
{
    internal static string Force(double kilonewtons, string numberFormat = "0.#") =>
        Format(kilonewtons, numberFormat, "kN", "MN");

    /// <summary>A force range in MN without the unit, for a short label where the unit is stated once beside the view
    /// ("6–8", "20.6–22.6"); one value when both ends are equal.</summary>
    internal static string MeganewtonRange(double minKilonewtons, double maxKilonewtons)
    {
        static string Number(double kilonewtons) => (kilonewtons / 1000).ToString("0.##", CultureInfo.CurrentCulture);
        return Math.Abs(maxKilonewtons - minKilonewtons) < 1e-9 ? Number(minKilonewtons) : $"{Number(minKilonewtons)}–{Number(maxKilonewtons)}";
    }

    internal static string Moment(double kilonewtonMetres, string numberFormat = "0.#") =>
        Format(kilonewtonMetres, numberFormat, "kN·m", "MN·m");

    private static string Format(double value, string numberFormat, string smallUnit, string largeUnit) =>
        Math.Abs(value) > 1000
            ? $"{(value / 1000).ToString(numberFormat, CultureInfo.CurrentCulture)} {largeUnit}"
            : $"{value.ToString(numberFormat, CultureInfo.CurrentCulture)} {smallUnit}";

    /// <summary>Screen-space member direction, flipped as needed so numeric labels never appear upside down.</summary>
    internal static double UprightAngle(double deltaX, double deltaY)
    {
        var angle = Math.Atan2(deltaY, deltaX) * 180 / Math.PI;
        if (angle > 90) angle -= 180;
        else if (angle < -90) angle += 180;
        return angle;
    }
}
