using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Navigation;
using WindowsJournal.Models;
using WindowsJournal.Services;

namespace WindowsJournal.Pages;

public sealed partial class JournalPage : Page
{
    public enum Mode { All, Favorites }

    /// <summary>Opens the page on one day's entries, from the Insights calendar.</summary>
    public sealed record DayFilter(DateTime Day);

    // Kept for the session, so opening an entry and coming back doesn't lose them.
    private static EntryCategory? _topicFilter;
    private static Mood? _moodFilter;

    private Mode _mode;
    private DateTime? _day;
    private bool _loadingFilters;
    private string _query = "";
    private JournalEntry? _onThisDay;

    public JournalPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _mode = e.Parameter is Mode m ? m : Mode.All;
        _day = (e.Parameter as DayFilter)?.Day;

        var isAll = _mode == Mode.All && _day is null;
        PageTitle.Text = _day is { } day ? day.ToString("MMMM d, yyyy") : isAll ? "Journal" : "Favorites";
        PageSubtitle.Text = _day is not null ? "What you wrote this day"
            : isAll ? $"{SuggestionService.Greeting()}. {DateTime.Now:dddd, MMMM d}"
            : "Entries you've starred";
        SuggestionsPanel.Visibility = isAll ? Visibility.Visible : Visibility.Collapsed;
        LoadFilters();
        if (JournalStore.Instance.UnreadableFile is { } unreadable)
        {
            LoadProblemBar.Message = $"Journal couldn't read its saved file, so it kept it safe at {unreadable} instead of overwriting it. " +
                                     "This can happen if the file was copied from another Windows account.";
            LoadProblemBar.IsOpen = true;
        }
        SuggestionFeed.Changed += SuggestionFeed_Changed;
        JournalStore.Instance.Changed += JournalStore_Changed;
        if (isAll)
        {
            SuggestionFeed.Show();
            ShowPrompts();
        }

        _onThisDay = SuggestionService.OnThisDay(JournalStore.Instance.Finished).FirstOrDefault();
        if (_onThisDay is not null)
        {
            var years = DateTime.Today.Year - _onThisDay.CreatedAt.Year;
            OnThisDayTitle.Text = years == 1 ? "1 year ago today" : $"{years} years ago today";
            OnThisDayPreview.Text = _onThisDay.DisplayTitle;
            OnThisDayPanel.Visibility = Visibility.Visible;
        }

        Refresh();
    }

    public void ApplySearch(string query)
    {
        _query = query.Trim();
        Refresh();
    }

    private void Refresh()
    {
        IEnumerable<JournalEntry> entries = JournalStore.Instance.Entries;
        if (_mode == Mode.Favorites)
        {
            entries = entries.Where(e => e.IsFavorite);
        }

        if (_day is { } day)
        {
            entries = entries.Where(e => !e.IsDraft && e.CreatedAt.Date == day);
        }

        if (_query.Length > 0)
        {
            entries = entries.Where(e =>
                e.Title.Contains(_query, StringComparison.CurrentCultureIgnoreCase) ||
                e.Body.Contains(_query, StringComparison.CurrentCultureIgnoreCase));
        }

        // Day view is short enough not to need these.
        var filtering = _day is null && (_topicFilter is not null || _moodFilter is not null);
        if (_day is null)
        {
            if (_topicFilter is { } topic) entries = entries.Where(e => e.Category == topic);
            if (_moodFilter is { } mood) entries = entries.Where(e => e.Mood == mood);
        }

        FilterBar.Visibility = _day is null && (filtering || JournalStore.Instance.Entries.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
        ClearFiltersButton.Visibility = filtering ? Visibility.Visible : Visibility.Collapsed;

        // Unfinished entries first, so they're easy to come back to.
        var list = entries.OrderByDescending(e => e.CreatedAt).ToList();
        var groups = list
            .Where(e => !e.IsDraft)
            .GroupBy(e => new DateTime(e.CreatedAt.Year, e.CreatedAt.Month, 1))
            .Select(g => new EntryGroup(GroupLabel(g.Key), g))
            .ToList();
        var drafts = list.Where(e => e.IsDraft).ToList();
        if (drafts.Count > 0) groups.Insert(0, new EntryGroup("Drafts", drafts));

        ((CollectionViewSource)Resources["EntriesSource"]).Source = groups;
        UpdateEmptyState(groups.Count == 0);
    }

    private void LoadFilters()
    {
        _loadingFilters = true;
        TopicFilter.Items.Add("All topics");
        foreach (var topic in CategoryOption.All) TopicFilter.Items.Add($"{topic.Emoji}  {topic.Label}");

        MoodFilter.Items.Add("All moods");
        foreach (var mood in MoodChoices) MoodFilter.Items.Add($"{mood.Emoji}  {mood.Label}");

        ShowFilterChoices();
        _loadingFilters = false;
    }

    // Best first, like the mood chart on Insights.
    private static readonly List<MoodOption> MoodChoices = [.. MoodOption.All.Reverse()];

    // The first choice in each list is "All"; the rest line up with their options.
    private void ShowFilterChoices()
    {
        TopicFilter.SelectedIndex = _topicFilter is { } topic ? CategoryOption.All.ToList().FindIndex(c => c.Value == topic) + 1 : 0;
        MoodFilter.SelectedIndex = _moodFilter is { } mood ? MoodChoices.FindIndex(m => m.Value == mood) + 1 : 0;
    }

    private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingFilters) return;

        _topicFilter = TopicFilter.SelectedIndex > 0 ? CategoryOption.All[TopicFilter.SelectedIndex - 1].Value : null;
        _moodFilter = MoodFilter.SelectedIndex > 0 ? MoodChoices[MoodFilter.SelectedIndex - 1].Value : null;
        Refresh();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _topicFilter = null;
        _moodFilter = null;
        _loadingFilters = true;
        ShowFilterChoices();
        _loadingFilters = false;
        Refresh();
        TopicFilter.Focus(FocusState.Programmatic);
    }

    private static string GroupLabel(DateTime month)
    {
        var thisMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        if (month == thisMonth) return "This month";
        if (month == thisMonth.AddMonths(-1)) return "Last month";
        return month.Year == DateTime.Today.Year ? month.ToString("MMMM") : month.ToString("MMMM yyyy");
    }

    private void UpdateEmptyState(bool empty)
    {
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;

        // Search explains itself below; these empty lists were narrowed some other way.
        if (_query.Length == 0 && (_day is not null || _topicFilter is not null || _moodFilter is not null))
        {
            EmptyGlyph.Glyph = "";
            EmptyTitle.Text = _day is not null ? "Nothing on this day" : "No entries match";
            EmptyBody.Text = _day is not null ? "This day's entries have been moved or deleted." : "Try a different topic or mood, or clear the filters.";
            return;
        }

        (EmptyGlyph.Glyph, EmptyTitle.Text, EmptyBody.Text) = (_query.Length > 0, _mode) switch
        {
            (true, _) => ("", "No matching entries", $"Nothing in your journal mentions “{_query}”."),
            (false, Mode.Favorites) => ("", "No favorites yet", "Star an entry to keep it here."),
            _ => ("", "Start your journal", "Pick a suggestion above or select New entry."),
        };
    }

    private void NewEntry_Click(object sender, RoutedEventArgs e) =>
        App.MainWindow.NavigateTo(typeof(EntryPage), new EntryPage.Args(null, null));

    private void Prompt_Click(object sender, RoutedEventArgs e) =>
        App.MainWindow.NavigateTo(typeof(EntryPage), new EntryPage.Args(null, (string)((Button)sender).Tag));

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        SuggestionFeed.Changed -= SuggestionFeed_Changed;
        JournalStore.Instance.Changed -= JournalStore_Changed;
    }

    // Moods, topics and reflections arrive in the background after an entry is saved.
    private void JournalStore_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    #region Suggestions

    private void ShufflePrompts_Click(object sender, RoutedEventArgs e) => SuggestionFeed.Next();

    private void SuggestionFeed_Changed(object? sender, EventArgs e) => ShowPrompts();

    private void ShowPrompts()
    {
        if (SuggestionsPanel.Visibility != Visibility.Visible) return;

        if (!ReferenceEquals(PromptsRepeater.ItemsSource, SuggestionFeed.Current))
        {
            PromptsRepeater.ItemsSource = SuggestionFeed.Current;

            // New suggestions start from the first card, even if focus or layout had scrolled the row.
            DispatcherQueue.TryEnqueue(() => PromptsScroller.ChangeView(0, null, null, disableAnimation: true));
        }

        // While built-in cards are up, say that better ones are coming. Topping up behind AI cards stays quiet.
        var waiting = SuggestionFeed.IsLoading && !SuggestionFeed.IsPersonalized;
        PromptsRing.IsActive = waiting;
        PromptsBadge.Text = waiting ? "Personalizing on this PC…" : $"Personalized by {AIService.ProviderName}";
        PromptsBadge.Visibility = waiting || SuggestionFeed.IsPersonalized ? Visibility.Visible : Visibility.Collapsed;
    }

    #endregion

    private void OnThisDay_Click(object sender, RoutedEventArgs e)
    {
        if (_onThisDay is not null)
        {
            App.MainWindow.NavigateTo(typeof(EntryPage), new EntryPage.Args(_onThisDay.Id, null));
        }
    }

    private void EntriesList_ItemClick(object sender, ItemClickEventArgs e) =>
        App.MainWindow.NavigateTo(typeof(EntryPage), new EntryPage.Args(((JournalEntry)e.ClickedItem).Id, null));
}
