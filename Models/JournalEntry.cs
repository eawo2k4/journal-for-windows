using System.Text.Json.Serialization;

namespace WindowsJournal.Models;

public enum Mood
{
    None = 0,
    Rough = 1,
    Low = 2,
    Okay = 3,
    Good = 4,
    Great = 5,
}

public enum EntryCategory
{
    None = 0,
    Work = 1,
    Goals = 2,
    Creativity = 3,
    PersonalGrowth = 4,
    Productivity = 5,
    DailyReflection = 6,
    Mindfulness = 7,
    HealthAndFitness = 8,
    Relationships = 9,
}

public sealed class JournalEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.Now;
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";

    /// <summary>The body as Markdown when it has formatting (see Helpers\RichText). Body stays plain text either way.</summary>
    public string? RichBody { get; set; }
    public Mood Mood { get; set; }
    public bool IsFavorite { get; set; }
    public string? Prompt { get; set; }

    /// <summary>The entry's main topic, detected by AI or chosen by the writer.</summary>
    public EntryCategory Category { get; set; }

    /// <summary>True when the writer chose it by hand, so AI leaves it alone.</summary>
    public bool MoodIsManual { get; set; }

    public bool CategoryIsManual { get; set; }

    /// <summary>An AI reflection on this entry.</summary>
    public string? Reflection { get; set; }

    /// <summary>Which version of the reflection style wrote it (see AIService.ReflectionStyle).</summary>
    public int ReflectionStyle { get; set; }

    /// <summary>Still being written: autosaved, but not finished with Done. Left out of streaks and Insights.</summary>
    public bool IsDraft { get; set; }

    /// <summary>The writer removed the reflection, so AI doesn't write another.</summary>
    public bool ReflectionDismissed { get; set; }

    /// <summary>Fingerprint of the text AI last looked at, so unchanged entries aren't analyzed again.</summary>
    public string? AnalyzedHash { get; set; }

    public List<string> PhotoPaths { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Body) && PhotoPaths.Count == 0;

    [JsonIgnore]
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title)
        ? Title
        : CreatedAt.ToString("dddd");

    [JsonIgnore]
    public string Preview => Body.Length > 280 ? Body[..280].ReplaceLineEndings(" ") + "…" : Body.ReplaceLineEndings(" ");

    [JsonIgnore]
    public string DateDisplay => CreatedAt.ToString("ddd, MMM d · h:mm tt");

    [JsonIgnore]
    public string MoodEmoji => MoodOption.EmojiFor(Mood);

    [JsonIgnore]
    public string CategoryText => CategoryOption.TextFor(Category);

    [JsonIgnore]
    public bool HasCategory => Category != EntryCategory.None;

    [JsonIgnore]
    public bool HasReflection => !string.IsNullOrWhiteSpace(Reflection);

    [JsonIgnore]
    public bool HasPhoto => PhotoPaths.Count > 0;

    [JsonIgnore]
    public string? FirstPhoto => PhotoPaths.FirstOrDefault();

    [JsonIgnore]
    public int WordCount => CountWords(Body);

    public static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

public sealed record MoodOption(Mood Value, string Emoji, string Label)
{
    public static IReadOnlyList<MoodOption> All { get; } =
    [
        new(Mood.Rough, "😣", "Rough"),
        new(Mood.Low, "😕", "Low"),
        new(Mood.Okay, "😐", "Okay"),
        new(Mood.Good, "🙂", "Good"),
        new(Mood.Great, "😄", "Great"),
    ];

    public static string EmojiFor(Mood mood) => All.FirstOrDefault(m => m.Value == mood)?.Emoji ?? "";
}

public sealed record CategoryOption(EntryCategory Value, string Emoji, string Label)
{
    public static IReadOnlyList<CategoryOption> All { get; } =
    [
        new(EntryCategory.Work, "💼", "Work"),
        new(EntryCategory.Goals, "🎯", "Goals"),
        new(EntryCategory.Creativity, "🎨", "Creativity"),
        new(EntryCategory.PersonalGrowth, "🌱", "Personal growth"),
        new(EntryCategory.Productivity, "✅", "Productivity"),
        new(EntryCategory.DailyReflection, "📝", "Daily reflection"),
        new(EntryCategory.Mindfulness, "🧘", "Mindfulness"),
        new(EntryCategory.HealthAndFitness, "💪", "Health and fitness"),
        new(EntryCategory.Relationships, "💞", "Relationships"),
    ];

    public static string TextFor(EntryCategory category) =>
        All.FirstOrDefault(c => c.Value == category) is { } option ? $"{option.Emoji} {option.Label}" : "";
}

public sealed record PhotoItem(string Path);

/// <summary>A month bucket for the grouped timeline.</summary>
public sealed class EntryGroup(string key, IEnumerable<JournalEntry> items) : List<JournalEntry>(items)
{
    public string Key { get; } = key;
}
