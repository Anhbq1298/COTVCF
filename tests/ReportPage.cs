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
            title: "", value: "", style: {}, dataset: {}, options: [], firstChild: {}, parentElement: { hidden: false },
            classList: { toggle: function(name, on){ classes[name] = on === undefined ? !classes[name] : !!on; },
                         add: function(name){ classes[name] = true; }, remove: function(name){ classes[name] = false; },
                         contains: function(name){ return !!classes[name]; } },
            // a <select>: add(new Option(text, value)) / remove(index)
            add: function(option){ this.options.push(option); },
            remove: function(index){ if (typeof index === "number") this.options.splice(index, 1); },
            setAttribute: function(){}, addEventListener: function(){}, appendChild: function(){}, append: function(){},
            querySelectorAll: function(){ return []; }, querySelector: function(){ return null; },
            getBoundingClientRect: function(){ return { width: 0, height: 0, left: 0, top: 0 }; } };
        }
        function Option(text, value){ return { textContent: String(text), text: String(text), value: String(value === undefined ? text : value), disabled: false }; }
        var document = {
          getElementById: function(id){ __ids.push(id); return __els[id] || (__els[id] = __el(id)); },
          addEventListener: function(){}, createElement: function(){ return __el(""); }, createTextNode: function(text){ return { textContent: text }; },
          querySelectorAll: function(){ return []; }, querySelector: function(){ return null; },
          body: __el("body"), documentElement: { clientWidth: 1400, setAttribute: function(){} }
        };
        var __store = JSON.parse(__storage);
        var window = {
          addEventListener: function(type, handler){ (__handlers[type] = __handlers[type] || []).push(handler); },
          print: function(){ __printed++; }, innerWidth: 1400, innerHeight: 900, location: { hash: __hash },
          localStorage: { getItem: function(key){ return Object.prototype.hasOwnProperty.call(__store, key) ? __store[key] : null; },
                          setItem: function(key, value){ __store[key] = String(value); }, removeItem: function(key){ delete __store[key]; } }
        };
        function __fire(type){ (__handlers[type] || []).forEach(function(handler){ handler({}); }); }
        """;

    private readonly Engine engine = new();
    public IReadOnlySet<string> StaticIds { get; }

    /// <param name="hash">URL fragment the page is opened with, e.g. "#print&amp;paper=A3".</param>
    /// <param name="scripts">How many of the page's scripts to run. 1 = the print engine alone, for a report
    /// whose own script needs a real browser DOM.</param>
    /// <param name="storage">What the browser already keeps in localStorage, by key.</param>
    public ReportPage(string html, string hash = "", int scripts = int.MaxValue, IReadOnlyDictionary<string, string>? storage = null)
    {
        var markup = Regex.Replace(html, "(<script[^>]*>).*?</script>", "$1</script>", RegexOptions.Singleline);   // tags, not script text
        StaticIds = Regex.Matches(markup, "\\bid=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        engine.SetValue("__hash", hash);
        engine.SetValue("__storage", System.Text.Json.JsonSerializer.Serialize(storage ?? new Dictionary<string, string>()));
        engine.Execute(Dom);
        // embedded data blocks first, then every script in document order — as a browser would
        foreach (Match data in Regex.Matches(html,
                     "<script (?:id=\"([^\"]+)\" type=\"application/json\"|type=\"application/json\" id=\"([^\"]+)\")>(.*?)</script>", RegexOptions.Singleline))
        {
            engine.SetValue("__data", data.Groups[3].Value);
            engine.Execute($"document.getElementById('{data.Groups[1].Value}{data.Groups[2].Value}').textContent = __data;");
        }
        // static <select> controls start on their markup: the options, and the selected (else first) value
        foreach (Match select in Regex.Matches(markup, "<select id=\"([^\"]+)\"[^>]*>(.*?)</select>", RegexOptions.Singleline))
        {
            var options = Regex.Matches(select.Groups[2].Value, "<option(?: value=\"([^\"]*)\")?( selected)?[^>]*>([^<]*)</option>")
                .Select(option => (Value: option.Groups[1].Success ? option.Groups[1].Value : option.Groups[3].Value,
                    Text: option.Groups[3].Value, Selected: option.Groups[2].Success)).ToArray();
            if (options.Length == 0) continue;
            engine.SetValue("__options", System.Text.Json.JsonSerializer.Serialize(options.Select(option => new[] { option.Text, option.Value })));
            engine.SetValue("__selected", options.FirstOrDefault(option => option.Selected, options[0]).Value);
            engine.Execute($"(function(){{ var s = document.getElementById('{select.Groups[1].Value}'); JSON.parse(__options).forEach(function(o){{ s.add(new Option(o[0], o[1])); }}); s.value = __selected; }})();");
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
    /// <summary>What the page left in localStorage under <paramref name="key"/>, or "".</summary>
    public string Stored(string key) => engine.Evaluate($"String(window.localStorage.getItem('{key}') || '')").AsString();
    /// <summary>Dispatch a window event ("load", "beforeprint", "afterprint").</summary>
    public void Fire(string type) => engine.Execute($"__fire('{type}')");

    /// <summary>The rendered charts of one kind ("overview" / "detail"), in page order.</summary>
    public IReadOnlyList<string> Charts(string kind) =>
        Regex.Matches(Html(kind), "<svg [^>]*data-chart=\"" + kind + "\".*?</svg>", RegexOptions.Singleline)
            .Select(match => match.Value).ToArray();
}
