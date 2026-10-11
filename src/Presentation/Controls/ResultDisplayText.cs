using System.Globalization;

namespace COT_VCF.Presentation.Controls;

/// <summary>Compact display units for values drawn on the FEM viewport; the stored results remain in kN and kN·m.</summary>
internal static class ResultDisplayText
{
    internal static string Force(double kilonewtons, string smallFormat = "0.##") =>
        Format(kilonewtons, smallFormat, "kN", "MN");

    /// <summary>A force range in one unit ("6 … 8 MN"); one value when both ends are equal.</summary>
    internal static string ForceRange(double minKilonewtons, double maxKilonewtons)
    {
        if (Math.Abs(maxKilonewtons - minKilonewtons) < 1e-9) return Force(minKilonewtons);
        var large = Math.Max(Math.Abs(minKilonewtons), Math.Abs(maxKilonewtons)) > 1000;
        string Number(double value) => (large ? value / 1000 : value).ToString(large ? "0.####" : "0.##", CultureInfo.CurrentCulture);
        return $"{Number(minKilonewtons)} … {Number(maxKilonewtons)} {(large ? "MN" : "kN")}";
    }

    internal static string Moment(double kilonewtonMetres, string smallFormat = "0.##") =>
        Format(kilonewtonMetres, smallFormat, "kN·m", "MN·m");

    private static string Format(double value, string smallFormat, string smallUnit, string largeUnit) =>
        Math.Abs(value) > 1000
            ? $"{(value / 1000).ToString("0.####", CultureInfo.CurrentCulture)} {largeUnit}"
            : $"{value.ToString(smallFormat, CultureInfo.CurrentCulture)} {smallUnit}";

    /// <summary>Screen-space member direction, flipped as needed so numeric labels never appear upside down.</summary>
    internal static double UprightAngle(double deltaX, double deltaY)
    {
        var angle = Math.Atan2(deltaY, deltaX) * 180 / Math.PI;
        if (angle > 90) angle -= 180;
        else if (angle < -90) angle += 180;
        return angle;
    }
}
