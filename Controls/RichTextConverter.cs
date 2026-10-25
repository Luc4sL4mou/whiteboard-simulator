using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using WhiteboardSimulator.Models;

namespace WhiteboardSimulator.Controls;

/// <summary>Converte entre os trechos do elemento (TextRun) e o documento do editor (RichTextBox).</summary>
public static class RichTextConverter
{
    /// <summary>Monta o documento do editor: um parágrafo por linha, trechos com a formatação própria.</summary>
    public static FlowDocument ToDocument(RichTextElement el)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily(el.FontFamily),
            FontSize = el.FontSize,
            Foreground = ElementView.BrushFor(el.DefaultColor),
            FontWeight = el.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = el.Italic ? FontStyles.Italic : FontStyles.Normal,
            TextAlignment = TextAlignment.Left
        };

        var paragraph = NewParagraph(el);
        doc.Blocks.Add(paragraph);

        foreach (var run in el.EffectiveRuns)
        {
            var lines = run.Text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    paragraph = NewParagraph(el);
                    doc.Blocks.Add(paragraph);
                }
                if (lines[i].Length == 0) continue;

                var inline = new Run(lines[i]);
                if (run.FontFamily != null) inline.FontFamily = new FontFamily(run.FontFamily);
                if (run.FontSize != null) inline.FontSize = run.FontSize.Value;
                if (run.Color != null) inline.Foreground = ElementView.BrushFor(run.Color);
                if (run.Bold != null) inline.FontWeight = run.Bold.Value ? FontWeights.Bold : FontWeights.Normal;
                if (run.Italic != null) inline.FontStyle = run.Italic.Value ? FontStyles.Italic : FontStyles.Normal;
                // O documento não tem "sublinhado padrão": cada trecho recebe o sublinhado quando o valem (próprio ou do elemento).
                if (run.Underline ?? el.Underline) inline.TextDecorations = TextDecorations.Underline;
                paragraph.Inlines.Add(inline);
            }
        }
        return doc;
    }

    private static Paragraph NewParagraph(RichTextElement el) => new() { Margin = new Thickness(0) };

    /// <summary>Lê o documento do editor de volta para trechos (só guarda o que difere do padrão do elemento).</summary>
    public static List<TextRun> FromDocument(FlowDocument doc, RichTextElement el)
    {
        var runs = new List<TextRun>();
        bool first = true;

        foreach (var block in doc.Blocks)
        {
            if (block is not Paragraph paragraph) continue;
            if (!first) runs.Add(new TextRun { Text = "\n" });
            first = false;
            Collect(paragraph.Inlines, el, runs);
        }
        return runs;
    }

    private static void Collect(InlineCollection inlines, RichTextElement el, List<TextRun> output)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run when run.Text.Length > 0:
                    output.Add(Describe(run, el));
                    break;
                case LineBreak:
                    output.Add(new TextRun { Text = "\n" });
                    break;
                case Span span:
                    Collect(span.Inlines, el, output);
                    break;
            }
        }
    }

    private static TextRun Describe(Run run, RichTextElement el)
    {
        var result = new TextRun { Text = run.Text };

        if (!string.Equals(run.FontFamily.Source, el.FontFamily, StringComparison.OrdinalIgnoreCase))
            result.FontFamily = run.FontFamily.Source;
        if (Math.Abs(run.FontSize - el.FontSize) > 0.01)
            result.FontSize = Math.Round(run.FontSize, 1);

        bool bold = run.FontWeight == FontWeights.Bold;
        bool italic = run.FontStyle == FontStyles.Italic;
        bool underline = run.TextDecorations != null && run.TextDecorations.Any(d => d.Location == TextDecorationLocation.Underline);
        if (bold != el.Bold) result.Bold = bold;
        if (italic != el.Italic) result.Italic = italic;
        if (underline != el.Underline) result.Underline = underline;

        if (run.Foreground is SolidColorBrush brush)
        {
            var hex = $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}";
            if (!string.Equals(hex, el.DefaultColor, StringComparison.OrdinalIgnoreCase)) result.Color = hex;
        }
        return result;
    }
}
