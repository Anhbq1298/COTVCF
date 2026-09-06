using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace COT_VCF.Presentation.Controls;

/// <summary>
/// Lightweight LaTeX-style inline renderer for compact engineering formulae.
/// Supports the subset used by the UI: commands, subscripts, superscripts and grouped text.
/// </summary>
public sealed class LatexTextBlock : TextBlock
{
    public static readonly DependencyProperty FormulaProperty =
        DependencyProperty.Register(nameof(Formula), typeof(string), typeof(LatexTextBlock),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender, OnFormulaChanged));

    public string Formula
    {
        get => (string)GetValue(FormulaProperty);
        set => SetValue(FormulaProperty, value);
    }

    private static void OnFormulaChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((LatexTextBlock)d).RenderFormula(e.NewValue as string ?? "");

    public LatexTextBlock()
    {
        FontFamily = new FontFamily("Cambria Math, Segoe UI");
        VerticalAlignment = VerticalAlignment.Center;
        DependencyPropertyDescriptor.FromProperty(FontSizeProperty, typeof(TextBlock))
            .AddValueChanged(this, (_, _) =>
            {
                if (!string.IsNullOrEmpty(Formula)) RenderFormula(Formula);
            });
    }

    private void RenderFormula(string formula)
    {
        Inlines.Clear();
        foreach (var part in Parse(formula))
        {
            var run = new Run(part.Text);
            if (part.Script != ScriptKind.Normal)
            {
                run.BaselineAlignment = part.Script == ScriptKind.Subscript
                    ? BaselineAlignment.Subscript
                    : BaselineAlignment.Superscript;
                run.FontSize = FontSize * 0.72;
            }
            Inlines.Add(run);
        }
    }

    private static IEnumerable<FormulaPart> Parse(string formula)
    {
        for (int i = 0; i < formula.Length;)
        {
            char ch = formula[i];
            if (ch == '\\')
            {
                yield return new(ReadCommand(formula, ref i), ScriptKind.Normal);
                continue;
            }
            if (ch is '_' or '^')
            {
                var script = ch == '_' ? ScriptKind.Subscript : ScriptKind.Superscript;
                i++;
                yield return new(ReadArgument(formula, ref i), script);
                continue;
            }
            if (ch is '{' or '}')
            {
                i++;
                continue;
            }

            yield return new(ch.ToString(), ScriptKind.Normal);
            i++;
        }
    }

    private static string ReadCommand(string formula, ref int index)
    {
        index++;
        var start = index;
        while (index < formula.Length && char.IsLetter(formula[index])) index++;
        var command = formula[start..index];
        return command switch
        {
            "cdot" => "·",
            "Delta" => "Δ",
            "delta" => "δ",
            "leq" => "≤",
            "geq" => "≥",
            "pm" => "±",
            "rightarrow" or "to" => "→",
            "times" => "×",
            _ => command.Length == 0 && index < formula.Length ? formula[index++].ToString() : "\\" + command,
        };
    }

    private static string ReadArgument(string formula, ref int index)
    {
        if (index >= formula.Length) return "";
        if (formula[index] != '{') return ReadSingleToken(formula, ref index);

        index++;
        var parts = new List<string>();
        var depth = 1;
        while (index < formula.Length && depth > 0)
        {
            if (formula[index] == '{') { depth++; index++; continue; }
            if (formula[index] == '}')
            {
                depth--;
                index++;
                if (depth == 0) break;
                continue;
            }
            parts.Add(formula[index] == '\\' ? ReadCommand(formula, ref index) : formula[index++].ToString());
        }
        return string.Concat(parts);
    }

    private static string ReadSingleToken(string formula, ref int index) =>
        formula[index] == '\\' ? ReadCommand(formula, ref index) : formula[index++].ToString();

    private readonly record struct FormulaPart(string Text, ScriptKind Script);
    private enum ScriptKind { Normal, Subscript, Superscript }
}
