using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;

namespace WindowsJournal.Helpers;

/// <summary>
/// Moves formatting between the editor (RichEditBox) and the small Markdown dialect entries are saved in:
/// **bold**, *italic*, __underline__, "# " heading, "- " bullets, "1. " numbers. Markdown rather than RTF
/// because RTF stores text colors, which would make text written in dark mode unreadable in light mode,
/// and because the AI can read and return Markdown.
/// </summary>
public static partial class RichText
{
    // Points. Body text is 12 pt (FontSize 16 in XAML), so anything from 17 pt up counts as a heading.
    private const float HeadingSize = 18f;
    private const float HeadingThreshold = 17f;

    public static bool IsHeading(ITextCharacterFormat format) => format.Size >= HeadingThreshold;

    public static void SetHeading(ITextRange range, bool on, RichEditTextDocument document)
    {
        range.CharacterFormat.Size = on ? HeadingSize : document.GetDefaultCharacterFormat().Size;
        range.CharacterFormat.Bold = on ? FormatEffect.On : FormatEffect.Off;
    }

    public static void SetList(ITextRange range, MarkerType type)
    {
        range.ParagraphFormat.ListType = type;
        if (type == MarkerType.None)
        {
            range.ParagraphFormat.SetIndents(0, 0, 0);
            return;
        }

        if (type == MarkerType.Arabic)
        {
            range.ParagraphFormat.ListStyle = MarkerStyle.Period;
            range.ParagraphFormat.ListStart = 1;
        }

        range.ParagraphFormat.ListTab = 18;
        range.ParagraphFormat.SetIndents(-18, 18, 0);
    }

    public static string GetPlainText(RichEditTextDocument document)
    {
        document.GetText(TextGetOptions.None, out var text);
        return text.TrimEnd('\r'); // the document always ends with a paragraph mark
    }

    public static void SetPlainText(RichEditTextDocument document, string text)
    {
        document.SetText(TextSetOptions.None, NormalizeLines(text));
        ClearFormatting(document);
    }

    private static string NormalizeLines(string text) => text.ReplaceLineEndings("\r");

    private static void ClearFormatting(RichEditTextDocument document)
    {
        var all = document.GetRange(0, int.MaxValue);
        var defaults = document.GetDefaultCharacterFormat();
        all.CharacterFormat.Size = defaults.Size;
        all.CharacterFormat.Bold = FormatEffect.Off;
        all.CharacterFormat.Italic = FormatEffect.Off;
        all.CharacterFormat.Underline = UnderlineType.None;
        SetList(all, MarkerType.None);
    }

    #region Editor to Markdown

    /// <summary>The document as Markdown, or null when it has no formatting (plain text is all that's needed).</summary>
    public static string? ToMarkdown(RichEditTextDocument document)
    {
        var lines = GetPlainText(document).Split('\r');
        var output = new StringBuilder();
        var anyFormatting = false;
        var start = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var end = start + line.Length;
            var listType = document.GetRange(start, start).ParagraphFormat.ListType;
            var heading = line.Length > 0 && IsHeading(document.GetRange(start, start + 1).CharacterFormat);
            var inline = InlineMarkdown(document, line, start, heading, ref anyFormatting);

            if (i > 0) output.Append('\n');
            if (heading)
            {
                output.Append("# ");
                anyFormatting = true;
            }
            else if (listType == MarkerType.Bullet)
            {
                output.Append("- ");
                anyFormatting = true;
            }
            else if (listType != MarkerType.None && listType != MarkerType.Undefined)
            {
                output.Append("1. ");
                anyFormatting = true;
            }
            else
            {
                // Text that merely looks like a list or heading stays text.
                inline = LineMarkerLookalike().Replace(inline, m => m.Value[..^2] + "\\" + m.Value[^2..]);
            }

            output.Append(inline);
            start = end + 1;
        }

        return anyFormatting ? output.ToString() : null;
    }

    private static string InlineMarkdown(RichEditTextDocument document, string line, int start, bool heading, ref bool anyFormatting)
    {
        var output = new StringBuilder();
        var end = start + line.Length;
        var position = start;
        var runStart = start;
        var current = default(Style);

        while (position < end)
        {
            var run = document.GetRange(position, position);
            run.Expand(TextRangeUnit.CharacterFormat);
            var next = Math.Clamp(run.EndPosition, position + 1, end);

            var format = document.GetRange(position, next).CharacterFormat;
            var style = new Style(!heading && format.Bold == FormatEffect.On, format.Italic == FormatEffect.On, format.Underline != UnderlineType.None);

            // Adjacent runs with the same look become one, so markers don't pile up.
            if (position > start && style != current)
            {
                Wrap(output, line[(runStart - start)..(position - start)], current, ref anyFormatting);
                runStart = position;
            }

            current = style;
            position = next;
        }

        if (end > start) Wrap(output, line[(runStart - start)..], current, ref anyFormatting);
        return output.ToString();
    }

    private static void Wrap(StringBuilder output, string text, Style style, ref bool anyFormatting)
    {
        // Markers go around the words; surrounding spaces stay outside ("**word** ", not "**word **").
        var core = text.Trim();
        if (core.Length == 0 || style == default)
        {
            output.Append(Escape(text));
            return;
        }

        anyFormatting = true;
        var open = (style.Bold ? "**" : "") + (style.Italic ? "*" : "") + (style.Underline ? "__" : "");
        var close = (style.Underline ? "__" : "") + (style.Italic ? "*" : "") + (style.Bold ? "**" : "");
        var leading = text[..^text.TrimStart().Length];
        var trailing = text[text.TrimEnd().Length..];
        output.Append(leading).Append(open).Append(Escape(core)).Append(close).Append(trailing);
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_");

    [GeneratedRegex(@"^(#+ |- |\d+\. )")]
    private static partial Regex LineMarkerLookalike();

    #endregion

    #region Line-by-line editing (AI Format)

    /// <summary>Splits a Markdown line into its line marker ("# ", "- ", "1. ", or empty) and its inline text.</summary>
    public static (string Marker, string Inline) SplitLine(string line)
    {
        if (line.StartsWith("# ", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal)) return (line[..2], line[2..]);
        return NumberedLine().Match(line) is { Success: true } number ? (number.Value, line[number.Length..]) : ("", line);
    }

    /// <summary>The text of an inline with no bold, italic or underline; null if it has any.</summary>
    public static string? PlainTextOf(string inline)
    {
        var text = new StringBuilder();
        var scratch = new Parsed();
        return ParseInline(inline, text, scratch) && scratch.Spans.Count == 0 ? text.ToString() : null;
    }

    /// <summary>Plain text as a Markdown line's inline part, with the line marker it belongs to.</summary>
    public static string PlainToInline(string text, string marker)
    {
        var inline = Escape(text);
        return marker.Length > 0 ? inline : LineMarkerLookalike().Replace(inline, m => m.Value[..^2] + "\\" + m.Value[^2..]);
    }

    /// <summary>True when an edited inline is well formed and has the same number of styled pieces as the original.</summary>
    public static bool SameStyling(string original, string edited)
    {
        var a = new Parsed();
        var b = new Parsed();
        return ParseInline(original, new StringBuilder(), a) && ParseInline(edited, new StringBuilder(), b) && a.Spans.Count == b.Spans.Count;
    }

    #endregion

    #region Markdown to editor

    internal readonly record struct Style(bool Bold, bool Italic, bool Underline);

    internal enum LineKind { Text, Heading, Bullet, Number }

    internal sealed record Line(int Start, int End, LineKind Kind);

    internal sealed record Span(int Start, int End, Style Style);

    /// <summary>A parsed document, ready to apply. Parsing never touches the editor, so a bad result changes nothing.</summary>
    public sealed class Parsed
    {
        internal string Text = "";
        internal readonly List<Line> Lines = [];
        internal readonly List<Span> Spans = [];

        public int Headings { get; internal set; }
        public int ListItems { get; internal set; }
        public int StyledSpans => Spans.Count;
    }

    /// <summary>False when the Markdown is malformed, such as a bold marker that's never closed.</summary>
    public static bool TryParse(string markdown, out Parsed parsed)
    {
        parsed = new Parsed();
        var text = new StringBuilder();
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var kind = LineKind.Text;
            if (line.StartsWith("# ", StringComparison.Ordinal)) { kind = LineKind.Heading; line = line[2..]; parsed.Headings++; }
            else if (line.StartsWith("- ", StringComparison.Ordinal)) { kind = LineKind.Bullet; line = line[2..]; parsed.ListItems++; }
            else if (NumberedLine().Match(line) is { Success: true } number) { kind = LineKind.Number; line = line[number.Length..]; parsed.ListItems++; }

            var lineStart = text.Length;
            if (!ParseInline(line, text, parsed)) return false;
            parsed.Lines.Add(new Line(lineStart, text.Length, kind));
            if (i < lines.Length - 1) text.Append('\r');
        }

        parsed.Text = text.ToString();
        return true;
    }

    private static bool ParseInline(string line, StringBuilder text, Parsed parsed)
    {
        var style = default(Style);
        var spanStart = text.Length;

        void Flush(Style next)
        {
            if (style != default && text.Length > spanStart) parsed.Spans.Add(new Span(spanStart, text.Length, style));
            spanStart = text.Length;
            style = next;
        }

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length)
            {
                text.Append(line[++i]);
            }
            else if (c == '*' && i + 1 < line.Length && line[i + 1] == '*')
            {
                Flush(style with { Bold = !style.Bold });
                i++;
            }
            else if (c == '_' && i + 1 < line.Length && line[i + 1] == '_')
            {
                Flush(style with { Underline = !style.Underline });
                i++;
            }
            else if (c == '*')
            {
                Flush(style with { Italic = !style.Italic });
            }
            else
            {
                text.Append(c);
            }
        }

        // A marker left open means the line is malformed.
        return style == default;
    }

    public static void Apply(RichEditTextDocument document, Parsed parsed)
    {
        document.SetText(TextSetOptions.None, parsed.Text);
        ClearFormatting(document);

        foreach (var span in parsed.Spans)
        {
            var range = document.GetRange(span.Start, span.End);
            if (span.Style.Bold) range.CharacterFormat.Bold = FormatEffect.On;
            if (span.Style.Italic) range.CharacterFormat.Italic = FormatEffect.On;
            if (span.Style.Underline) range.CharacterFormat.Underline = UnderlineType.Single;
        }

        var lines = parsed.Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Kind == LineKind.Heading && line.End > line.Start)
            {
                SetHeading(document.GetRange(line.Start, line.End), true, document);
            }
            else if (line.Kind is LineKind.Bullet or LineKind.Number)
            {
                // Neighbouring items get the list together, so numbers count 1, 2, 3.
                var last = i;
                while (last + 1 < lines.Count && lines[last + 1].Kind == line.Kind) last++;
                SetList(document.GetRange(line.Start, lines[last].End), line.Kind == LineKind.Bullet ? MarkerType.Bullet : MarkerType.Arabic);
                i = last;
            }
        }
    }

    [GeneratedRegex(@"^\d+\. ")]
    private static partial Regex NumberedLine();

    #endregion
}
