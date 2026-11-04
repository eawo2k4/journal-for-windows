namespace WindowsJournal.Services;

/// <summary>
/// What the suggestion cards show. It lives outside the page so that a reply from the model reaches whichever page
/// is on screen, and New ideas never has to wait: AI suggestions are made ahead in batches, and built-in ones
/// fill in until the next batch is ready.
/// </summary>
public static class SuggestionFeed
{
    private const int CardCount = 4;

    // One reply fills several rounds of New ideas, so most clicks don't involve the model at all.
    private const int BatchSize = 12;

    private static readonly Queue<string> Ready = new();
    private static string? _focus;
    private static int _entriesInBatch;
    private static int _generation;
    private static bool _listening;

    public static IReadOnlyList<string> Current { get; private set; } = [];

    /// <summary>Whether the cards on screen came from the model.</summary>
    public static bool IsPersonalized { get; private set; }

    /// <summary>Whether the model is working on a batch.</summary>
    public static bool IsLoading { get; private set; }

    /// <summary>Raised on the UI thread when the cards or their status change.</summary>
    public static event EventHandler? Changed;

    /// <summary>Called when the cards come on screen. Keeps what's showing, unless the focus areas changed.</summary>
    public static void Show()
    {
        if (!_listening)
        {
            _listening = true;
            AIService.StatusChanged += (_, _) => AIStatusChanged();
        }

        var focus = string.Join(',', SettingsService.Current.FocusAreas);
        if (focus != _focus)
        {
            _focus = focus;
            Discard();
            Current = [];
        }

        if (Current.Count == 0) UseBuiltIn();
        LoadMoreIfNeeded();
    }

    /// <summary>New cards, right away.</summary>
    public static void Next()
    {
        // Written something since this batch was made? Its suggestions don't know about it.
        if (JournalStore.Instance.Finished.Count() != _entriesInBatch) Discard();

        if (Ready.Count >= CardCount) UseNextReady();
        else UseBuiltIn();

        LoadMoreIfNeeded();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void UseBuiltIn()
    {
        Current = SuggestionService.RandomPrompts(CardCount);
        IsPersonalized = false;
    }

    private static void UseNextReady()
    {
        Current = [.. Enumerable.Range(0, CardCount).Select(_ => Ready.Dequeue())];
        IsPersonalized = true;
    }

    // Anything still being worked on belongs to an older batch, so its reply gets ignored.
    private static void Discard()
    {
        Ready.Clear();
        _generation++;
        IsLoading = false;
    }

    private static void AIStatusChanged()
    {
        if (AIService.IsAvailable)
        {
            LoadMoreIfNeeded();
        }
        else
        {
            // Leave the cards alone so they don't jump while someone's reading them.
            Discard();
            IsPersonalized = false;
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static async void LoadMoreIfNeeded()
    {
        if (IsLoading || !AIService.IsAvailable || Ready.Count >= CardCount) return;

        var generation = _generation;
        IsLoading = true;
        Changed?.Invoke(null, EventArgs.Empty);
        try
        {
            var finished = JournalStore.Instance.Finished.ToList();
            var prompts = await AIService.PersonalizedPromptsAsync(finished, BatchSize);
            if (generation != _generation) return;

            _entriesInBatch = finished.Count;
            foreach (var prompt in prompts.Except(Current).Except(Ready)) Ready.Enqueue(prompt);

            // Built-in cards make way for the first AI ones. After that, only New ideas changes them.
            if (!IsPersonalized && Ready.Count >= CardCount) UseNextReady();
        }
        catch (Exception ex)
        {
            // Keep the built-in prompts; the AI is a bonus, not a requirement.
            AIService.LogError(ex);
        }
        finally
        {
            if (generation == _generation) IsLoading = false;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
