using System.Globalization;
using COT_VCF.Presentation.Controls;

internal static class ViewerResultDisplayChecks
{
    internal static void Run()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Check(ResultDisplayText.Force(1000) == "1000 kN" &&
                  ResultDisplayText.Force(1000.1) == "1.0001 MN" &&
                  ResultDisplayText.Force(-129862) == "-129.862 MN" &&
                  ResultDisplayText.Force(99328) == "99.328 MN", "force labels switch units by absolute magnitude");
            Check(ResultDisplayText.Moment(-22593) == "-22.593 MN·m" &&
                  ResultDisplayText.Moment(750) == "750 kN·m", "moment labels retain their length unit");
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
