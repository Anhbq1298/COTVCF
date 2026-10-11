using System.Globalization;
using System.Net;
using System.Text;

namespace COT_VCF.Application.Results;

internal static class FemReportCharts
{
    internal sealed record Series(string Name, IReadOnlyList<(double X, double Y, string Label)> Points);
    private static readonly string[] Colors = ["#1f4788", "#c62828", "#2e7d32", "#8e44ad", "#b45f06", "#00838f"];
    private static string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static string E(string value) => WebUtility.HtmlEncode(value);

    public static string Line(string title, string xLabel, string yLabel, IReadOnlyList<Series> series)
    {
        var values = series.SelectMany(s => s.Points).ToArray();
        if (values.Length == 0) return $"<p>No results for {E(title)}.</p>";
        var x0 = values.Min(p => p.X); var x1 = values.Max(p => p.X);
        var y0 = Math.Min(0, values.Min(p => p.Y)); var y1 = Math.Max(0, values.Max(p => p.Y));
        if (x0 == x1) { x0 -= 0.5; x1 += 0.5; }
        if (y0 == y1) { y0 -= 1; y1 += 1; }
        var pad = (y1 - y0) * 0.08; y0 -= pad; y1 += pad;
        double X(double value) => 85 + (value - x0) / (x1 - x0) * 740;
        double Y(double value) => 300 - (value - y0) / (y1 - y0) * 250;
        var b = new StringBuilder($"<div class=\"result-chart\"><h2>{E(title)}</h2><svg viewBox=\"0 0 900 360\" role=\"img\" aria-label=\"{E(title)}\">");
        for (var i = 0; i <= 5; i++)
        {
            var x = x0 + (x1 - x0) * i / 5; var y = y0 + (y1 - y0) * i / 5;
            b.Append($"<path d=\"M85 {N(Y(y))}H825 M{N(X(x))} 50V300\" stroke=\"#e6e8eb\" fill=\"none\"/>")
                .Append($"<text x=\"75\" y=\"{N(Y(y) + 4)}\" text-anchor=\"end\">{y.ToString("0.###", CultureInfo.InvariantCulture)}</text>")
                .Append($"<text x=\"{N(X(x))}\" y=\"322\" text-anchor=\"middle\">{x.ToString("0.###", CultureInfo.InvariantCulture)}</text>");
        }
        b.Append($"<path d=\"M85 50V300H825 M85 {N(Y(0))}H825\" stroke=\"#6b7280\" fill=\"none\"/>")
            .Append($"<text x=\"455\" y=\"348\" text-anchor=\"middle\">{E(xLabel)}</text><text transform=\"translate(18,175) rotate(-90)\" text-anchor=\"middle\">{E(yLabel)}</text>");
        for (var index = 0; index < series.Count; index++)
        {
            var s = series[index]; var color = Colors[index % Colors.Length];
            b.Append($"<polyline points=\"{string.Join(" ", s.Points.Select(p => $"{N(X(p.X))},{N(Y(p.Y))}"))}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\"/>");
            foreach (var p in s.Points)
                b.Append($"<circle cx=\"{N(X(p.X))}\" cy=\"{N(Y(p.Y))}\" r=\"3\" fill=\"{color}\"><title>{E(s.Name)} · {E(p.Label)} · {E(xLabel)} {N(p.X)} · {E(yLabel)} {N(p.Y)}</title></circle>");
        }
        b.Append("</svg><div class=\"chart-legend\">");
        for (var i = 0; i < series.Count; i++) b.Append($"<span style=\"color:{Colors[i % Colors.Length]}\">● {E(series[i].Name)}</span> ");
        return b.Append("</div></div>").ToString();
    }

    public static string Bars(string title, string unit, IReadOnlyList<string> labels, params (string Name, IReadOnlyList<double?> Values)[] series)
    {
        var values = series.SelectMany(s => s.Values).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        if (values.Length == 0) return $"<p>No results for {E(title)}.</p>";
        var min = Math.Min(0, values.Min()); var max = Math.Max(0, values.Max());
        if (min == max) max = min + 1;
        var padding = (max - min) * 0.12; min -= padding; max += padding;
        double X(double value) => 180 + (value - min) / (max - min) * 620;
        var height = 65 + labels.Count * (series.Length * 22 + 14);
        var b = new StringBuilder($"<div class=\"result-chart\"><h2>{E(title)} [{E(unit)}]</h2><svg viewBox=\"0 0 900 {height}\" role=\"img\" aria-label=\"{E(title)}\">");
        b.Append($"<path d=\"M{N(X(0))} 35V{height - 25}\" stroke=\"#6b7280\"/>");
        for (var i = 0; i < labels.Count; i++)
        {
            var y = 45 + i * (series.Length * 22 + 14);
            b.Append($"<text x=\"170\" y=\"{y + 12}\" text-anchor=\"end\">{E(labels[i])}</text>");
            for (var j = 0; j < series.Length; j++)
            {
                if (series[j].Values[i] is not { } value) continue;
                var color = Colors[j % Colors.Length]; var yy = y + j * 22;
                b.Append($"<rect x=\"{N(Math.Min(X(0), X(value)))}\" y=\"{yy}\" width=\"{N(Math.Abs(X(value) - X(0)))}\" height=\"16\" fill=\"{color}\"><title>{E(labels[i])} · {E(series[j].Name)}: {N(value)} {E(unit)}</title></rect>")
                    .Append($"<text x=\"{N(X(value) + (value < 0 ? -4 : 4))}\" y=\"{yy + 12}\" text-anchor=\"{(value < 0 ? "end" : "start")}\">{value.ToString("0.###", CultureInfo.InvariantCulture)}</text>");
            }
        }
        b.Append("</svg><div class=\"chart-legend\">");
        for (var i = 0; i < series.Length; i++) b.Append($"<span style=\"color:{Colors[i % Colors.Length]}\">● {E(series[i].Name)}</span> ");
        return b.Append("</div></div>").ToString();
    }
}
