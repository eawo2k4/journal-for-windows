using System.Text.RegularExpressions;

namespace WindowsJournal.Services;

/// <summary>
/// Conservative, rule-based cleanup used when on-device AI isn't available.
/// Only touches whitespace and obvious capitalization; never changes words.
/// </summary>
public static partial class TextFormatter
{
    public static string Tidy(string text)
    {
        var t = text.ReplaceLineEndings("\n");
        t = TrailingSpaces().Replace(t, "");
        t = MultiSpaces().Replace(t, " ");
        t = ExtraBlankLines().Replace(t, "\n\n");
        t = SpaceBeforePunctuation().Replace(t, "$1");
        t = MissingSpaceAfterPunctuation().Replace(t, "$1 $2");
        t = LoneI().Replace(t, "I$1");
        t = SentenceStart().Replace(t, m => m.Value.ToUpperInvariant());
        return t.Trim().ReplaceLineEndings();
    }

    [GeneratedRegex(@"[ \t]+$", RegexOptions.Multiline)]
    private static partial Regex TrailingSpaces();

    [GeneratedRegex(@"(?<=\S)[ \t]{2,}")]
    private static partial Regex MultiSpaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();

    [GeneratedRegex(@"[ \t]+([,.!?;:])")]
    private static partial Regex SpaceBeforePunctuation();

    [GeneratedRegex(@"([,!?;])([A-Za-z])")]
    private static partial Regex MissingSpaceAfterPunctuation();

    [GeneratedRegex(@"\bi('m|'ve|'ll|'d)?\b")]
    private static partial Regex LoneI();

    // First letter of the text, of each line, and after . ! ?
    [GeneratedRegex(@"(^|(?<=[.!?]\s+)|(?<=\n))[a-z]", RegexOptions.Multiline)]
    private static partial Regex SentenceStart();
}
