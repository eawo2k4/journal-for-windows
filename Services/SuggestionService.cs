using WindowsJournal.Models;

namespace WindowsJournal.Services;

/// <summary>
/// Journaling suggestions. Everything here is computed on-device from the user's own entries —
/// nothing leaves the PC.
/// </summary>
public static class SuggestionService
{
    private static readonly string[] Prompts =
    [
        "What made you smile today?",
        "Describe a small win from this week.",
        "Who are you grateful for right now, and why?",
        "What's been taking up most of your headspace lately?",
        "Write about a place that makes you feel calm.",
        "What would you tell yourself from a year ago?",
        "What's something you learned recently?",
        "Describe your perfect ordinary day.",
        "What are you looking forward to?",
        "What drained your energy today, and what restored it?",
        "Write about a conversation that stuck with you.",
        "What's a habit you'd like to build, and what's the first step?",
        "What does 'rest' look like for you this week?",
        "Capture a moment from today in as much detail as you can.",
        "What's one thing you'd like to let go of?",
        "Who did you help recently, or who helped you?",
        "What's a song, show, or book that's on your mind?",
        "What are you proud of that nobody else noticed?",
    ];

    private static IReadOnlyList<string> _last = [];

    /// <summary>A random set of prompts, avoiding the ones shown last time when possible.</summary>
    /// <summary>One prompt per day, the same all day (for the widget, which refreshes often).</summary>
    public static string PromptOfTheDay() => Prompts[(DateTime.Today.DayOfYear + DateTime.Today.Year) % Prompts.Length];

    public static IReadOnlyList<string> RandomPrompts(int count = 4)
    {
        var pool = Prompts.Except(_last).ToList();
        if (pool.Count < count) pool = [.. Prompts];

        // Focus-area prompts come first, up to about three quarters, so there's still some variety.
        var focused = SettingsService.Current.FocusAreas
            .SelectMany(f => FocusPrompts.GetValueOrDefault(f, []))
            .Except(_last)
            .OrderBy(_ => Random.Shared.Next())
            .Take(Math.Max(1, count * 3 / 4))
            .ToList();
        _last = focused.Concat(pool.OrderBy(_ => Random.Shared.Next())).Distinct().Take(count)
            .OrderBy(_ => Random.Shared.Next())
            .ToList();
        return _last;
    }

    private static readonly Dictionary<EntryCategory, string[]> FocusPrompts = new()
    {
        [EntryCategory.Work] = ["What went well at work today, and why?", "What's one thing at work you'd do differently?", "What part of your work gives you energy lately?"],
        [EntryCategory.Goals] = ["What's one small step you took toward a goal?", "Which goal matters most to you right now, and why?", "What's getting in the way of a goal you care about?"],
        [EntryCategory.Creativity] = ["What's an idea you can't stop thinking about?", "What did you make, or want to make, this week?", "Where did you find inspiration recently?"],
        [EntryCategory.PersonalGrowth] = ["What did you learn about yourself this week?", "What's something you handled better than you used to?", "What's a belief of yours that's been changing?"],
        [EntryCategory.Productivity] = ["What's the one thing that would make tomorrow a good day?", "What took your time today that didn't need to?", "When did you feel most focused this week?"],
        [EntryCategory.DailyReflection] = ["What stood out most about today?", "What would you like to remember about today?", "How did today compare with what you expected?"],
        [EntryCategory.Mindfulness] = ["What did you notice around you today that you usually miss?", "What does your body feel like right now?", "When did you feel most present today?"],
        [EntryCategory.HealthAndFitness] = ["How did your body feel today?", "What's one healthy thing you did for yourself this week?", "How have you been sleeping, and how does it affect your days?"],
        [EntryCategory.Relationships] = ["Who's been on your mind lately, and why?", "What's a conversation you're glad you had?", "Who would you like to spend more time with?"],
    };

    public static IReadOnlyList<JournalEntry> OnThisDay(IEnumerable<JournalEntry> entries)
    {
        var today = DateTime.Today;
        return entries
            .Where(e => e.CreatedAt.Month == today.Month && e.CreatedAt.Day == today.Day && e.CreatedAt.Year < today.Year)
            .OrderByDescending(e => e.CreatedAt)
            .ToList();
    }

    public static string Greeting() => DateTime.Now.Hour switch
    {
        < 5 => "Still up?",
        < 12 => "Good morning",
        < 17 => "Good afternoon",
        _ => "Good evening",
    };

    /// <summary>Consecutive days with at least one entry, ending today (or yesterday, so the streak survives until bedtime).</summary>
    public static int CurrentStreak(IEnumerable<JournalEntry> entries)
    {
        var days = entries.Select(e => e.CreatedAt.Date).ToHashSet();
        var day = days.Contains(DateTime.Today) ? DateTime.Today : DateTime.Today.AddDays(-1);
        var streak = 0;
        while (days.Contains(day))
        {
            streak++;
            day = day.AddDays(-1);
        }

        return streak;
    }

    public static int LongestStreak(IEnumerable<JournalEntry> entries)
    {
        var days = entries.Select(e => e.CreatedAt.Date).Distinct().Order().ToList();
        int best = 0, run = 0;
        for (var i = 0; i < days.Count; i++)
        {
            run = i > 0 && days[i] == days[i - 1].AddDays(1) ? run + 1 : 1;
            best = Math.Max(best, run);
        }

        return best;
    }
}
