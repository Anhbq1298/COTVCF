namespace COT_VCF.Application.Results;

/// <summary>The shared look of a report's Summary and drill-down views (member forces, support reactions): the
/// heading with its view tag, cards, KPI cards, legend, SVG chart text and grid, the values table — and their
/// paper rules.
/// A report puts it after its own stylesheet, so these rules win over its generic panel rules.</summary>
internal static class ReportSummaryTheme
{
    public const string Css = """
.sumpanel{padding:10px 14px 12px}
.chead{display:flex;align-items:center;gap:10px;flex-wrap:wrap;margin:0 0 7px}
.cname{font:600 15px Segoe UI,Arial}
.cview{font-size:10.5px;font-weight:600;letter-spacing:.05em;text-transform:uppercase;color:#1f4788;border:1px solid #1f4788;border-radius:9px;padding:1px 9px}
.cgo{border-color:#1f4788;color:#1f4788;font-weight:600}
.cpick{display:inline-flex;align-items:center;gap:6px;font-size:12px;color:#444}
.ccard{border:1px solid #c7ccd2;border-radius:3px;margin:0 0 10px;padding:7px 10px 4px;max-width:1280px}
.ctitle{font-weight:600;color:#1f4788}.ctitle span{font-weight:400;color:#666;font-size:11px;margin-left:8px}
.clegend{display:flex;flex-wrap:wrap;gap:3px 16px;align-items:center;font-size:11px;color:#444;margin:4px 0 0}
.clegend span{display:inline-flex;align-items:center;gap:5px}.clegend .off{color:#9aa0a6}.clegend .note{color:#666}
.cchart{display:block;width:100%;height:auto}.cchart text{font-family:Segoe UI,Arial}
.cgrid{stroke:#e8ebef}.caxis{stroke:#8e98a3}.ctick{fill:#687386;font-size:10px}.cax{fill:#475467;font-size:11px;font-weight:600}
.cval{fill:#1a1a1a;font-size:9.5px;paint-order:stroke;stroke:#fff;stroke-width:2.5px;stroke-linejoin:round}
.chl{fill:#687386;font-size:9px}.cpl{fill:#1a1a1a;font-size:11px;font-weight:600}.cbr{fill:none;stroke:#8e98a3}
.cna{fill:#98a2b3;font-size:8.5px;font-style:italic}
.cdata{font-size:11px}.cdata>summary{cursor:pointer;color:#1f4788;font-weight:600;padding:3px 0}
.ctable{border-collapse:collapse;font:10.5px Segoe UI,Arial;white-space:nowrap}.ctable th,.ctable td{padding:3px 9px;border-bottom:1px solid #eee;text-align:left}.ctable th{background:#eef1f4;position:sticky;top:0}.ctable .n{text-align:right;font-variant-numeric:tabular-nums}.ctable .na{color:#b42318}
.kpis{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:8px;margin:0 0 10px;max-width:1280px}
.kpi{border:1px solid #c7ccd2;border-left:3px solid #8e98a3;border-radius:3px;padding:6px 10px 7px}
.kl{font-size:10.5px;font-weight:600;letter-spacing:.04em;text-transform:uppercase;color:#475467}
.kv{font:600 22px/1.25 Segoe UI,Arial;font-variant-numeric:tabular-nums}.kv span{font-size:12px;font-weight:400;color:#666}.kv.none{font-size:15px;color:#666}
.kw{font-size:12px;font-weight:600;color:#1f4788}.kd{font-size:11px;color:#666}
@media print{.kpis{max-width:none}.kpi{break-inside:avoid}.sumpanel{padding:8px 0;break-inside:auto}.chead{break-after:avoid}.ccard{break-inside:avoid;max-width:none}.cdata>summary{list-style:none}.cdata>summary::-webkit-details-marker{display:none}.ctable th{position:static}}
""";
}
