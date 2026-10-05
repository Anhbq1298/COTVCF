using System.Text.RegularExpressions;
using Jint;

/// <summary>Runs a self-contained report's own scripts against its embedded data with a minimal DOM, so the
/// checks see what a browser would render and print — without a browser, a server or SAP2000.</summary>
internal sealed class ReportPage
{
    private const string Dom = """
        var __ids = [], __els = {}, __handlers = {}, __printed = 0;
        function __el(id){
          var classes = {};
          return { id: id, innerHTML: "", textContent: "", hidden: false, disabled: false, checked: false, open: false,
            title: "", value: "", style: {},
            classList: { toggle: function(name, on){ classes[name] = on === undefined ? !classes[name] : !!on; },
                         add: function(name){ classes[name] = true; }, remove: function(name){ classes[name] = false; },
                         contains: function(name){ return !!classes[name]; } },
            setAttribute: function(){}, addEventListener: function(){}, appendChild: function(){},
            getBoundingClientRect: function(){ return { width: 0, height: 0, left: 0, top: 0 }; } };
        }
        var document = {
          getElementById: function(id){ __ids.push(id); return __els[id] || (__els[id] = __el(id)); },
          addEventListener: function(){}, createElement: function(){ return __el(""); },
          querySelectorAll: function(){ return []; }, querySelector: function(){ return null; },
          body: __el("body"), documentElement: { clientWidth: 1400, setAttribute: function(){} }
        };
        var window = {
          addEventListener: function(type, handler){ (__handlers[type] = __handlers[type] || []).push(handler); },
          print: function(){ __printed++; }, innerWidth: 1400, innerHeight: 900, location: { hash: __hash }
        };
        function __fire(type){ (__handlers[type] || []).forEach(function(handler){ handler({}); }); }
        """;

    private readonly Engine engine = new();
    public IReadOnlySet<string> StaticIds { get; }

    /// <param name="hash">URL fragment the page is opened with, e.g. "#print&amp;paper=A3".</param>
    /// <param name="scripts">How many of the page's scripts to run. 1 = the print engine alone, for a report
    /// whose own script needs a real browser DOM.</param>
    public ReportPage(string html, string hash = "", int scripts = int.MaxValue)
    {
        var markup = Regex.Replace(html, "(<script[^>]*>).*?</script>", "$1</script>", RegexOptions.Singleline);   // tags, not script text
        StaticIds = Regex.Matches(markup, "\\bid=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        engine.SetValue("__hash", hash);
        engine.Execute(Dom);
        // embedded data blocks first, then every script in document order — as a browser would
        foreach (Match data in Regex.Matches(html,
                     "<script (?:id=\"([^\"]+)\" type=\"application/json\"|type=\"application/json\" id=\"([^\"]+)\")>(.*?)</script>", RegexOptions.Singleline))
        {
            engine.SetValue("__data", data.Groups[3].Value);
            engine.Execute($"document.getElementById('{data.Groups[1].Value}{data.Groups[2].Value}').textContent = __data;");
        }
        engine.Execute("__ids.length = 0;");
        foreach (var script in Regex.Matches(html, "<script>(.*?)</script>", RegexOptions.Singleline).Take(scripts))
            engine.Execute(script.Groups[1].Value);
        Fire("load");
    }

    public string[] RequestedIds => engine.Evaluate("__ids.join('\\n')").AsString()
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
    public string Html(string id) => engine.Evaluate($"String(__els['{id}'].innerHTML)").AsString();
    public string Text(string id) => engine.Evaluate($"String(__els['{id}'].textContent)").AsString();
    public string Value(string id) => engine.Evaluate($"String(__els['{id}'].value)").AsString();
    public bool Hidden(string id) => engine.Evaluate($"!!__els['{id}'].hidden").AsBoolean();
    public bool Disabled(string id) => engine.Evaluate($"!!__els['{id}'].disabled").AsBoolean();
    public bool Checked(string id) => engine.Evaluate($"!!__els['{id}'].checked").AsBoolean();
    public bool BodyHasClass(string name) => engine.Evaluate($"document.body.classList.contains('{name}')").AsBoolean();
    public int PrintCalls => (int)engine.Evaluate("__printed").AsNumber();
    public void Run(string js) => engine.Execute(js);
    public string Eval(string js) => engine.Evaluate($"String({js})").AsString();
    /// <summary>Dispatch a window event ("load", "beforeprint", "afterprint").</summary>
    public void Fire(string type) => engine.Execute($"__fire('{type}')");

    /// <summary>The rendered charts of one kind ("overview" / "detail"), in page order.</summary>
    public IReadOnlyList<string> Charts(string kind) =>
        Regex.Matches(Html(kind), "<svg [^>]*data-chart=\"" + kind + "\".*?</svg>", RegexOptions.Singleline)
            .Select(match => match.Value).ToArray();
}
