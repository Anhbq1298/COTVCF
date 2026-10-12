using System.Globalization;
using Mcotis.Desktop.Presentation.Controls;

internal static class ViewerResultDisplayChecks
{
    internal static void Run()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Check(ResultDisplayText.Force(1000) == "1000 kN" &&
                  ResultDisplayText.Force(1000.1) == "1 MN" &&
                  ResultDisplayText.Force(-66406.6) == "-66.4 MN" &&
                  ResultDisplayText.Force(39999.9) == "40 MN" &&
                  ResultDisplayText.Force(99328) == "99.3 MN" &&
                  ResultDisplayText.Force(999.96) == "1000 kN", "viewport force labels use at most one decimal place");
            Check(ResultDisplayText.Moment(-22593) == "-22.6 MN·m" &&
                  ResultDisplayText.Moment(750) == "750 kN·m", "moment labels retain their length unit");
            Check(ResultDisplayText.Force(1251, "0.0") == "1.3 MN" &&
                  ResultDisplayText.Moment(750, "0.0") == "750.0 kN·m",
                "explicit one-decimal labels keep the same precision after unit conversion");
            Check(ResultDisplayText.UprightAngle(10, -10) == -45 &&
                  ResultDisplayText.UprightAngle(-10, 10) == -45 &&
                  ResultDisplayText.UprightAngle(0, -10) == -90,
                "sloped and reversed members keep labels parallel and upright");
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
