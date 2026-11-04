using System.Text.Json;

namespace WindowsJournal.Widgets;

/// <summary>
/// What the two Journal widgets show. They can sit on the lock screen, where anyone can see them, so this only ever
/// uses dates and built-in prompts, never anything from an entry. Wording invites rather than nags: it's on screen for hours.
/// </summary>
public static class WidgetContent
{
    public const string PromptId = "Journal_Prompt";

    // The ID dates from when this only showed a streak; pinned widgets depend on it.
    public const string ProgressId = "Journal_Streak";

    // Adaptive Cards. "$host.widgetSize" lets the small size skip what won't fit.
    public const string PromptTemplate = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.5",
          "body": [
            { "type": "TextBlock", "text": "${prompt}", "size": "Medium", "weight": "Bolder", "wrap": true, "maxLines": 3 },
            { "type": "TextBlock", "text": "${promptStatus}", "size": "Small", "isSubtle": true, "wrap": true, "spacing": "Small" }
          ],
          "actions": [
            { "type": "Action.OpenUrl", "title": "${button}", "url": "windowsjournal://new" }
          ]
        }
        """;

    public const string ProgressTemplate = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.5",
          "body": [
            { "type": "TextBlock", "text": "${headline}", "size": "Large", "weight": "Bolder", "wrap": true },
            { "type": "TextBlock", "$when": "${$host.widgetSize != \"small\"}", "text": "${dots}", "spacing": "Small" },
            { "type": "TextBlock", "$when": "${status != ''}", "text": "${status}", "size": "Small", "isSubtle": true, "wrap": true, "spacing": "Small" }
          ],
          "actions": [
            { "type": "Action.OpenUrl", "title": "${button}", "url": "windowsjournal://new" }
          ]
        }
        """;

    // A matched pair: the smaller circle characters come from different fonts and end up different sizes.
    private const string DayWritten = "⬤";
    private const string DayNotWritten = "◯";

    // A streak of one day isn't a streak yet, and a broken one is never mentioned.
    private const int StreakWorthShowing = 2;

    public static string Template(string definitionId) => definitionId == PromptId ? PromptTemplate : ProgressTemplate;

    /// <summary>The days of this week so far with an entry, one mark per day of the week.</summary>
    public static bool[] WeekDays(IEnumerable<DateTime> daysWritten, DateTime today, DayOfWeek firstDayOfWeek)
    {
        var start = today.Date.AddDays(-(((int)today.DayOfWeek - (int)firstDayOfWeek + 7) % 7));
        var written = daysWritten.Select(d => d.Date).ToHashSet();
        return [.. Enumerable.Range(0, 7).Select(i => written.Contains(start.AddDays(i)))];
    }

    public static string Data(int streak, IEnumerable<DateTime> daysWritten, DateTime today, DayOfWeek firstDayOfWeek, string prompt)
    {
        var days = daysWritten.ToList();
        var wroteToday = days.Any(d => d.Date == today.Date);
        var week = WeekDays(days, today, firstDayOfWeek);
        var count = week.Count(written => written);
        var hasStreak = streak >= StreakWorthShowing;

        return JsonSerializer.Serialize(new
        {
            prompt,
            promptStatus = wroteToday ? "You wrote today ✓" : "Ready to write?",
            headline = count switch
            {
                0 => "Ready to write?",
                1 => "1 day this week",
                _ => $"{count} days this week",
            },
            dots = string.Join(' ', week.Select(written => written ? DayWritten : DayNotWritten)),
            status = (wroteToday, hasStreak, count) switch
            {
                (true, true, _) => $"You wrote today ✓ · {streak} day streak",
                (true, false, _) => "You wrote today ✓",
                (false, true, _) => $"{streak} day streak. Keep your streak going?",
                (false, false, > 0) => "Ready to write?",
                _ => "",
            },
            button = wroteToday ? "Write more" : "Write",
        });
    }
}
