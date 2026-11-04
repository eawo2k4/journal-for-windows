using System.Security.Cryptography;
using System.Text;
using WindowsJournal.Models;

namespace WindowsJournal.Services;

/// <summary>
/// After an entry is saved, works out its mood, topic and reflection in the background, on this PC.
/// Choices the writer made by hand are never overwritten, and a reflection they removed stays removed.
/// </summary>
public static class EntryInsights
{
    private const int MinimumWords = 5;

    // Older entries are filled in a few at a time, newest first, so a long journal doesn't tie up the model.
    private const int BackfillPerLaunch = 20;

    private static readonly Queue<JournalEntry> Pending = new();
    private static readonly HashSet<Guid> Queued = [];
    private static bool _running;
    private static bool _backfilled;

    /// <summary>Raised on the UI thread when an entry's insights have changed, or started or stopped being worked on.</summary>
    public static event EventHandler<Guid>? Updated;

    public static bool IsWorkingOn(Guid id) => Queued.Contains(id);

    /// <summary>Whether insights run at all right now.</summary>
    public static bool IsOn => AIService.IsAvailable && SettingsService.Current.AutoInsights;

    public static void Request(JournalEntry entry)
    {
        if (!IsOn || !NeedsWork(entry) || !Queued.Add(entry.Id)) return;

        Pending.Enqueue(entry);
        Updated?.Invoke(null, entry.Id);
        if (!_running) _ = RunAsync();
    }

    /// <summary>Once AI is ready, fills in entries saved before this feature existed or while AI was off.</summary>
    public static void BackfillOnce()
    {
        if (_backfilled || !IsOn) return;
        _backfilled = true;
        foreach (var entry in JournalStore.Instance.Entries.OrderByDescending(e => e.CreatedAt).Where(NeedsWork).Take(BackfillPerLaunch))
        {
            Request(entry);
        }
    }

    // Drafts wait until they're finished. Entries analyzed under older rules get looked at again once.
    private static bool NeedsWork(JournalEntry entry) =>
        !entry.IsDraft
        && JournalEntry.CountWords(entry.Body) >= MinimumWords
        && (entry.AnalyzedHash != Fingerprint(entry) || entry.ReflectionStyle < AIService.ReflectionStyle);

    private static string Fingerprint(JournalEntry entry) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Title + "\n" + entry.Body)))[..16];

    private static async Task RunAsync()
    {
        _running = true;
        while (Pending.TryDequeue(out var entry))
        {
            try
            {
                await AnalyzeAsync(entry);
            }
            catch (Exception ex)
            {
                AIService.LogError(ex);
            }
            finally
            {
                Queued.Remove(entry.Id);
                Updated?.Invoke(null, entry.Id);
            }
        }

        _running = false;
    }

    private static async Task AnalyzeAsync(JournalEntry entry)
    {
        var fingerprint = Fingerprint(entry);
        var isFirstAnalysis = entry.AnalyzedHash is null;

        var (mood, category) = await AIService.DetectMoodAndTopicAsync(entry.Title, entry.Body);
        string? reflection = null;
        if (!entry.ReflectionDismissed)
        {
            try
            {
                reflection = await AIService.ReflectAsync(entry, entry.Body);
            }
            catch (InvalidOperationException)
            {
                // Nothing passed the style check. Better no reflection than a bad one; mood and topic still count.
            }
        }

        // Edited or deleted while the model was working: this answer is about old text, so drop it.
        if (JournalStore.Instance.Find(entry.Id) is null || Fingerprint(entry) != fingerprint) return;

        // A mood already on an entry that was never analyzed came from the old picker, so it counts as the writer's own.
        if (isFirstAnalysis && entry.Mood != Mood.None) entry.MoodIsManual = true;
        if (!entry.MoodIsManual && mood != Mood.None) entry.Mood = mood;

        // No topic is a valid answer: better blank than a wrong label.
        if (!entry.CategoryIsManual) entry.Category = category;
        if (reflection is not null)
        {
            entry.Reflection = reflection;
            entry.ReflectionStyle = AIService.ReflectionStyle;
        }
        else if (entry.ReflectionStyle < AIService.ReflectionStyle)
        {
            // Couldn't be rewritten in the current style, so don't leave the old one behind.
            entry.Reflection = null;
            entry.ReflectionStyle = AIService.ReflectionStyle;
        }

        entry.AnalyzedHash = fingerprint;

        await JournalStore.Instance.UpsertAsync(entry);
    }
}
