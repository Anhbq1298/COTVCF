using System.Globalization;
using COT_VCF.Presentation;

internal static class NumericInputChecks
{
    public static void Run()
    {
        var converter = new NullableDoubleConverter();

        Check(Read(converter, "2.5", new("en-US")) == 2.5,
            "Nullable engineering input accepts a decimal point");
        Check(Read(converter, "2,5", new("en-US")) == 2.5,
            "Nullable engineering input accepts a decimal comma under en-US");
        Check(Read(converter, "2,5", new("de-DE")) == 2.5,
            "Nullable engineering input accepts a decimal comma under de-DE");
        Check(Read(converter, "2.5", new("de-DE")) == 2.5,
            "Nullable engineering input accepts a decimal point under de-DE");
        Check(converter.ConvertBack("", typeof(double?), null, CultureInfo.InvariantCulture) is null,
            "Clearing a nullable engineering input still stores null");
        Check(Throws(() => converter.ConvertBack("1,2,3", typeof(double?), null, CultureInfo.InvariantCulture)),
            "Ambiguous engineering input is rejected instead of misread as thousands");
    }

    private static double Read(NullableDoubleConverter converter, string text, CultureInfo culture) =>
        (double)converter.ConvertBack(text, typeof(double?), null, culture)!;

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (FormatException) { return true; }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
