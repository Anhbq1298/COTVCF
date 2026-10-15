using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Presentation.Controls;

/// <summary>The displacement quantity used to colour a deformed shape. Values are in mm for static results.
/// Modal results use the same internal conversion but the UI labels them as relative amplitudes.</summary>
public enum DisplacementContourComponent { Magnitude, UX, UY, UZ }

internal static class DeformationDisplay
{
    public static double ComponentMm(double ux, double uy, double uz, DisplacementContourComponent component) => component switch
    {
        DisplacementContourComponent.UX => ux * 1000,
        DisplacementContourComponent.UY => uy * 1000,
        DisplacementContourComponent.UZ => uz * 1000,
        _ => Math.Sqrt(ux * ux + uy * uy + uz * uz) * 1000
    };

    public static (double Min, double Max) Range(IEnumerable<double> values, DisplacementContourComponent component,
        double requestedMin, double requestedMax)
    {
        var finite = values.Where(double.IsFinite).ToArray();
        if (requestedMin == 0 && requestedMax == 0)
        {
            var min = component == DisplacementContourComponent.Magnitude ? 0 : finite.DefaultIfEmpty(0).Min();
            var max = finite.DefaultIfEmpty(0).Max();
            return (min, Math.Max(max, min + 1e-9));
        }
        var from = double.IsFinite(requestedMin) ? requestedMin : 0;
        var to = double.IsFinite(requestedMax) && requestedMax > from ? requestedMax : Math.Max(finite.DefaultIfEmpty(0).Max(), from + 1e-9);
        return (from, to);
    }

    public static string Label(DisplacementContourComponent component) => component switch
    {
        DisplacementContourComponent.UX => "UX",
        DisplacementContourComponent.UY => "UY",
        DisplacementContourComponent.UZ => "UZ",
        _ => "|U|"
    };
}
