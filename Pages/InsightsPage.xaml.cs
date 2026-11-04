using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;
using WindowsJournal.Models;
using WindowsJournal.Services;

namespace WindowsJournal.Pages;

public sealed record Stat(string Glyph, string Value, string Label);

public sealed record FocusStat(string Text);

public sealed record MoodStat(string Emoji, string Label, int Count, double Fraction);

public sealed partial class InsightsPage : Page
{
    private readonly HashSet<DateTime> _daysWithEntries;
    private readonly Color[] _accent = [(Color)Application.Current.Resources["SystemAccentColor"]];

    public InsightsPage()
    {
        InitializeComponent();

        var entries = JournalStore.Instance.Finished.ToList();
        _daysWithEntries = entries.Select(e => e.CreatedAt.Date).ToHashSet();

        var current = SuggestionService.CurrentStreak(entries);
        StatsRepeater.ItemsSource = new List<Stat>
        {
            new("", Days(current), "Current streak"),
            new("", Days(SuggestionService.LongestStreak(entries)), "Longest streak"),
            new("", entries.Count.ToString("N0"), entries.Count == 1 ? "Entry" : "Entries"),
            new("", entries.Sum(e => e.WordCount).ToString("N0"), "Words written"),
            new("", _daysWithEntries.Count(d => d.Year == DateTime.Today.Year).ToString("N0"), $"Days journaled in {DateTime.Today.Year}"),
        };

        var withMood = entries.Where(e => e.Mood != Mood.None).ToList();
        MoodRepeater.ItemsSource = MoodOption.All
            .Reverse()
            .Select(m =>
            {
                var count = withMood.Count(e => e.Mood == m.Value);
                return new MoodStat(m.Emoji, m.Label, count, withMood.Count == 0 ? 0 : (double)count / withMood.Count);
            })
            .ToList();
        NoMoodText.Visibility = withMood.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var focus = CategoryOption.All
            .Where(c => SettingsService.Current.FocusAreas.Contains(c.Value))
            .Select(c =>
            {
                var count = entries.Count(e => e.Category == c.Value && e.CreatedAt.Year == DateTime.Today.Year && e.CreatedAt.Month == DateTime.Today.Month);
                return new FocusStat($"{c.Emoji}  {c.Label}: {(count == 1 ? "1 entry" : $"{count} entries")}");
            })
            .ToList();
        FocusRepeater.ItemsSource = focus;
        FocusCard.Visibility = focus.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Topics, most written about first.
        var withTopic = entries.Where(e => e.HasCategory).ToList();
        var topics = CategoryOption.All
            .Select(c => new MoodStat(c.Emoji, c.Label, withTopic.Count(e => e.Category == c.Value), withTopic.Count == 0 ? 0 : (double)withTopic.Count(e => e.Category == c.Value) / withTopic.Count))
            .Where(t => t.Count > 0)
            .OrderByDescending(t => t.Count)
            .ToList();
        TopicsRepeater.ItemsSource = topics;
        TopicsCard.Visibility = topics.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Only name a favorite when one topic actually leads; a tie or a single entry doesn't show a habit.
        var hasLeader = topics.Count > 0 && topics[0].Count >= 2 && (topics.Count == 1 || topics[0].Count > topics[1].Count);
        TopicsHeadline.Text = hasLeader ? $"You mostly write about {topics[0].Emoji} {topics[0].Label}" : "What you write about";

        UpdateWeekCard();
        AIService.StatusChanged += AIService_StatusChanged;
        Unloaded += (_, _) => AIService.StatusChanged -= AIService_StatusChanged;
    }

    // Kept for the session so revisiting Insights doesn't ask the model again.
    private static string? _weekReflection;
    private static int _weekEntryCount = -1;

    private List<JournalEntry> ThisWeek() =>
        JournalStore.Instance.Finished.Where(e => e.CreatedAt >= DateTimeOffset.Now.AddDays(-7)).ToList();

    private void AIService_StatusChanged(object? sender, EventArgs e) => UpdateWeekCard();

    private void UpdateWeekCard()
    {
        var week = ThisWeek();
        WeekCard.Visibility = AIService.IsAvailable && week.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WeekSubtitle.Text = $"{week.Count} {(week.Count == 1 ? "entry" : "entries")} in the last 7 days · reflected on this PC by {AIService.ProviderName}";

        // A cached reflection only stands if no entries were added or removed since.
        var fresh = _weekReflection is not null && _weekEntryCount == week.Count;
        WeekText.Text = fresh ? _weekReflection : "";
        WeekText.Visibility = fresh ? Visibility.Visible : Visibility.Collapsed;
        WeekButton.Content = fresh ? "Reflect again" : "Reflect on my week";
    }

    private async void WeekReflect_Click(object sender, RoutedEventArgs e)
    {
        var week = ThisWeek();
        WeekButton.IsEnabled = false;
        WeekRing.IsActive = true;
        try
        {
            _weekReflection = await AIService.WeeklyReflectionAsync(week);
            _weekEntryCount = week.Count;
        }
        catch (Exception ex)
        {
            WeekText.Text = AIService.Describe(ex);
            WeekText.Visibility = Visibility.Visible;
            return;
        }
        finally
        {
            WeekButton.IsEnabled = true;
            WeekRing.IsActive = false;
        }

        UpdateWeekCard();
    }

    private static string Days(int n) => n == 1 ? "1 day" : $"{n} days";

    private void Calendar_DayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
    {
        args.Item.SetDensityColors(_daysWithEntries.Contains(args.Item.Date.Date) ? _accent : null);
    }

    // One entry opens straight away; several are listed. A day without entries does nothing.
    private void Calendar_SelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
    {
        if (args.AddedDates.Count == 0) return;

        var day = args.AddedDates[0].Date;
        DispatcherQueue.TryEnqueue(() => sender.SelectedDates.Clear());

        var entries = JournalStore.Instance.Finished.Where(e => e.CreatedAt.Date == day).ToList();
        if (entries.Count == 1) App.MainWindow.NavigateTo(typeof(EntryPage), new EntryPage.Args(entries[0].Id, null));
        else if (entries.Count > 1) App.MainWindow.NavigateTo(typeof(JournalPage), new JournalPage.DayFilter(day));
    }
}
