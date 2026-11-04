using System.Collections.ObjectModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WindowsJournal.Helpers;
using WindowsJournal.Models;
using WindowsJournal.Services;

namespace WindowsJournal.Pages;

public sealed partial class EntryPage : Page
{
    /// <summary>Open an existing entry (EntryId) or start a new one, optionally from a prompt.</summary>
    public sealed record Args(Guid? EntryId, string? Prompt);

    private readonly ObservableCollection<PhotoItem> _photos = [];
    private JournalEntry _entry = new();
    private bool _isNew;
    private bool _deleted;
    private bool _bodyLoaded;
    private string? _undoTitle;
    private (string Plain, string? Rich)? _undoBody;

    public EntryPage()
    {
        InitializeComponent();
        BuildChipMenus();
        PhotosGrid.ItemsSource = _photos;
        _photos.CollectionChanged += (_, _) =>
        {
            PhotosGrid.Visibility = _photos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ScheduleAutosave();
        };
        _autosave.Tick += Autosave_Tick;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var args = e.Parameter as Args ?? new Args(null, null);
        var existing = args.EntryId is { } id ? JournalStore.Instance.Find(id) : null;
        _isNew = existing is null;
        _entry = existing ?? new JournalEntry { Prompt = args.Prompt, IsDraft = true };
        if (_isNew && SharedContent.TakePending() is { } shared)
        {
            _entry.Title = shared.Title;
            _entry.Body = shared.Text;
            _entry.PhotoPaths = [.. shared.PhotoPaths];
        }

        TitleBox.Text = _entry.Title;
        // The editor re-applies its font size to the whole document as it loads, which would undo headings.
        void OnBodyLoaded(object sender, RoutedEventArgs args)
        {
            BodyBox.Loaded -= OnBodyLoaded;
            LoadBody(_entry.Body, _entry.RichBody);
            _bodyLoaded = true;
            _lastSaved = Snapshot();
            UpdateWordCount();
        }

        BodyBox.Loaded += OnBodyLoaded;
        DatePicker.Date = _entry.CreatedAt;
        FavoriteButton.IsChecked = _entry.IsFavorite;
        UpdateChips();
        foreach (var p in _entry.PhotoPaths) _photos.Add(new PhotoItem(p));

        PromptBar.Message = _entry.Prompt ?? "";
        PromptBar.IsOpen = !string.IsNullOrEmpty(_entry.Prompt);
        DeleteButton.Label = _entry.IsDraft ? "Discard draft" : "Delete entry";
        ShowReflection(_entry.Reflection);
        UpdateAiAvailability();
        AIService.StatusChanged += AIService_StatusChanged;
        EntryInsights.Updated += EntryInsights_Updated;
        UpdateWordCount();

        // Put the cursor where you'd start typing.
        var target = _isNew && _entry.Prompt is null ? (Control)TitleBox : BodyBox;
        DispatcherQueue.TryEnqueue(() => target.Focus(FocusState.Programmatic));
    }

    // An empty RichEditBox pushes a stretched column off-center until it has text, so the column gets an exact width.
    private void EntryScroller_SizeChanged(object sender, SizeChangedEventArgs e) =>
        EntryColumn.Width = Math.Min(EntryColumn.MaxWidth, e.NewSize.Width);

    // Autosave on the way out, whether via Done, Back, or the nav pane.
    protected override async void OnNavigatedFrom(NavigationEventArgs e)
    {
        AIService.StatusChanged -= AIService_StatusChanged;
        EntryInsights.Updated -= EntryInsights_Updated;
        _autosave.Stop();
        await SaveAsync();
    }

    /// <summary>Saves what's on screen. Also called when the window closes with this entry open.</summary>
    public async Task SaveAsync()
    {
        if (_deleted) return;

        // Leaving or closing mid-dictation keeps what was said so far.
        await StopDictationAsync();

        CopyEditorToEntry();

        // A draft that ended up empty isn't worth keeping, even if autosave already stored it.
        if (_entry.IsDraft && _entry.IsEmpty)
        {
            if (JournalStore.Instance.Find(_entry.Id) is not null) await JournalStore.Instance.DeleteAsync(_entry);
            return;
        }

        await JournalStore.Instance.UpsertAsync(_entry);
        EntryInsights.Request(_entry);
    }

    private void CopyEditorToEntry()
    {
        _entry.Title = TitleBox.Text.Trim();
        if (_bodyLoaded)
        {
            _entry.Body = BodyText;
            _entry.RichBody = RichText.ToMarkdown(BodyBox.Document);
        }

        _entry.IsFavorite = FavoriteButton.IsChecked == true;
        _entry.PhotoPaths = [.. _photos.Select(p => p.Path)];
        if (DatePicker.Date is { } date)
        {
            // Keep the original time of day, move only the date.
            _entry.CreatedAt = new DateTimeOffset(date.Date + _entry.CreatedAt.TimeOfDay, _entry.CreatedAt.Offset);
        }
    }

    private void Content_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateWordCount();
        ScheduleAutosave();
    }

    private void BodyBox_TextChanged(object sender, RoutedEventArgs e)
    {
        UpdateWordCount();
        ScheduleAutosave();
    }

    #region Autosave

    // Saves after a short pause in typing, so a crash or power cut loses a couple of seconds at most.
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? _lastSaved;
    private string _savedNote = "";

    private void ScheduleAutosave()
    {
        if (!_bodyLoaded) return;
        _autosave.Stop();
        _autosave.Start();
    }

    private string Snapshot() => string.Join('\u0001',
        TitleBox.Text, BodyText, RichText.ToMarkdown(BodyBox.Document), FavoriteButton.IsChecked,
        _entry.Mood, _entry.Category, string.Join('|', _photos.Select(p => p.Path)));

    private async void Autosave_Tick(object? sender, object e)
    {
        _autosave.Stop();

        // AI and dictation are still changing the text; their results trigger another save when they finish.
        if (!_bodyLoaded || _deleted || _dictation is not null || BodyBox.IsReadOnly) return;

        var snapshot = Snapshot();
        if (snapshot == _lastSaved) return;

        CopyEditorToEntry();
        if (_entry.IsEmpty) return;

        _lastSaved = snapshot;
        try
        {
            await JournalStore.Instance.AutosaveAsync(_entry);
            _savedNote = $"{(_entry.IsDraft ? "Draft saved" : "Saved")} {DateTime.Now:t}";
        }
        catch (IOException ex)
        {
            _lastSaved = null; // try again on the next change
            _savedNote = "Couldn't save just now";
            AIService.LogError(ex);
        }

        UpdateWordCount();
    }

    #endregion

    #region Mood and topic

    private void BuildChipMenus()
    {
        foreach (var mood in MoodOption.All)
        {
            var item = new MenuFlyoutItem { Text = $"{mood.Emoji}  {mood.Label}" };
            item.Click += (_, _) =>
            {
                _entry.Mood = mood.Value;
                _entry.MoodIsManual = true;
                UpdateChips();
            };
            MoodMenu.Items.Add(item);
        }

        var autoMood = new MenuFlyoutItem { Text = "Detect automatically" };
        autoMood.Click += (_, _) =>
        {
            _entry.Mood = Mood.None;
            _entry.MoodIsManual = false;
            _entry.AnalyzedHash = null;
            UpdateChips();
        };
        MoodMenu.Items.Add(new MenuFlyoutSeparator());
        MoodMenu.Items.Add(autoMood);

        foreach (var category in CategoryOption.All)
        {
            var item = new MenuFlyoutItem { Text = $"{category.Emoji}  {category.Label}" };
            item.Click += (_, _) =>
            {
                _entry.Category = category.Value;
                _entry.CategoryIsManual = true;
                UpdateChips();
            };
            CategoryMenu.Items.Add(item);
        }

        var autoCategory = new MenuFlyoutItem { Text = "Detect automatically" };
        autoCategory.Click += (_, _) =>
        {
            _entry.Category = EntryCategory.None;
            _entry.CategoryIsManual = false;
            _entry.AnalyzedHash = null;
            UpdateChips();
        };
        CategoryMenu.Items.Add(new MenuFlyoutSeparator());
        CategoryMenu.Items.Add(autoCategory);
    }

    private void UpdateChips()
    {
        var working = EntryInsights.IsWorkingOn(_entry.Id);
        MoodButton.Content = _entry.Mood != Mood.None
            ? $"{_entry.MoodEmoji} {_entry.Mood}"
            : working ? "Detecting mood…" : "Add mood";
        CategoryButton.Content = _entry.HasCategory
            ? _entry.CategoryText
            : working ? "Detecting topic…" : "Add topic";

        // The button text is its own content, so screen readers need the value spelled out.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MoodButton, $"Mood: {MoodButton.Content}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CategoryButton, $"Topic: {CategoryButton.Content}");
    }

    private void EntryInsights_Updated(object? sender, Guid id)
    {
        if (id != _entry.Id) return;
        UpdateChips();
        ShowReflection(_entry.Reflection);
    }

    #endregion

    #region Rich text

    private string BodyText => RichText.GetPlainText(BodyBox.Document);

    // The document ignores changes while read-only, and the box is read-only while AI or dictation runs.
    private void EditBody(Action<RichEditTextDocument> edit)
    {
        var readOnly = BodyBox.IsReadOnly;
        BodyBox.IsReadOnly = false;
        edit(BodyBox.Document);
        BodyBox.IsReadOnly = readOnly;
    }

    private void LoadBody(string plain, string? rich)
    {
        if (rich is not null && RichText.TryParse(rich, out var parsed))
        {
            EditBody(document => RichText.Apply(document, parsed));
        }
        else
        {
            EditBody(document => RichText.SetPlainText(document, plain));
        }
    }

    private void SetDictatedText(string text) =>
        EditBody(document => document.GetRange(_dictationStart, int.MaxValue).Text = text);

    private void MoveCursorToEnd()
    {
        var end = BodyText.Length;
        BodyBox.Document.Selection.SetRange(end, end);
    }

    // Formatting the bar can't represent (fonts, colors from other apps) wouldn't survive saving, so paste plain text.
    private void BodyBox_Paste(object sender, TextControlPasteEventArgs e)
    {
        e.Handled = true;
        const int UnicodeText = 13; // CF_UNICODETEXT
        BodyBox.Document.Selection.Paste(UnicodeText);
    }

    // Enter at the end of a heading starts normal text, as in Word.
    private void BodyBox_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || !RichText.IsHeading(BodyBox.Document.Selection.CharacterFormat)) return;

        DispatcherQueue.TryEnqueue(() => RichText.SetHeading(BodyBox.Document.Selection, false, BodyBox.Document));
    }

    private void BodyBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        var selection = BodyBox.Document.Selection;
        var heading = RichText.IsHeading(selection.CharacterFormat);
        HeadingToggle.IsChecked = heading;
        BoldToggle.IsChecked = !heading && selection.CharacterFormat.Bold == FormatEffect.On;
        ItalicToggle.IsChecked = selection.CharacterFormat.Italic == FormatEffect.On;
        UnderlineToggle.IsChecked = selection.CharacterFormat.Underline == UnderlineType.Single;
        BulletsToggle.IsChecked = selection.ParagraphFormat.ListType == MarkerType.Bullet;
        NumbersToggle.IsChecked = selection.ParagraphFormat.ListType == MarkerType.Arabic;
    }

    private void Bold_Click(object sender, RoutedEventArgs e) => BodyBox.Document.Selection.CharacterFormat.Bold = FormatEffect.Toggle;

    private void Italic_Click(object sender, RoutedEventArgs e) => BodyBox.Document.Selection.CharacterFormat.Italic = FormatEffect.Toggle;

    private void Underline_Click(object sender, RoutedEventArgs e)
    {
        var format = BodyBox.Document.Selection.CharacterFormat;
        format.Underline = format.Underline == UnderlineType.Single ? UnderlineType.None : UnderlineType.Single;
    }

    private void Heading_Click(object sender, RoutedEventArgs e)
    {
        var selection = BodyBox.Document.Selection;
        var on = !RichText.IsHeading(selection.CharacterFormat);

        // Headings apply to whole paragraphs; also set the selection so an empty line types as a heading.
        var paragraph = selection.GetClone();
        paragraph.Expand(TextRangeUnit.Paragraph);
        RichText.SetHeading(paragraph, on, BodyBox.Document);
        RichText.SetHeading(selection, on, BodyBox.Document);
    }

    private void Bullets_Click(object sender, RoutedEventArgs e) => ToggleList(MarkerType.Bullet);

    private void Numbers_Click(object sender, RoutedEventArgs e) => ToggleList(MarkerType.Arabic);

    private void ToggleList(MarkerType type)
    {
        var selection = BodyBox.Document.Selection;
        RichText.SetList(selection, selection.ParagraphFormat.ListType == type ? MarkerType.None : type);
        BodyBox_SelectionChanged(BodyBox, new RoutedEventArgs());
    }

    #endregion

    #region AI

    private void AIService_StatusChanged(object? sender, EventArgs e) => UpdateAiAvailability();

    // Reflect and Expressive need a model; Simple and Inspire me have non-AI fallbacks.
    private void UpdateAiAvailability()
    {
        // Dictation runs its own small speech model, so it only needs AI to be turned on.
        DictateButton.IsEnabled = SettingsService.Current.AIEnabled && AIService.ModelState != ModelState.Unsupported;
        ReflectButton.IsEnabled = AIService.IsAvailable;
        ToolTipService.SetToolTip(ReflectButton, AIService.IsAvailable
            ? "Get a short reflection on what you wrote"
            : "Turn on on-device AI in Settings to get reflections");
    }

    private void FormatMenu_Opening(object sender, object e)
    {
        FormatExpressiveItem.IsEnabled = AIService.IsAvailable;
        FormatExpressiveItem.Text = AIService.IsAvailable ? "Polish wording" : "Polish wording (needs on-device AI)";
    }

    private void FormatSimple_Click(object sender, RoutedEventArgs e) => FormatAsync(FormatStyle.Simple);

    private void FormatExpressive_Click(object sender, RoutedEventArgs e) => FormatAsync(FormatStyle.Expressive);

    private async void FormatAsync(FormatStyle style)
    {
        var original = BodyText;
        if (string.IsNullOrWhiteSpace(original)) return;

        // Entries with formatting go through as Markdown, so the formatting comes back.
        var rich = RichText.ToMarkdown(BodyBox.Document);
        _undoTitle = TitleBox.Text;
        _undoBody = (original, rich);

        if (!AIService.IsAvailable)
        {
            LoadBody(TextFormatter.Tidy(original), rich is null ? null : TextFormatter.Tidy(rich));
            ShowAiResult("Errors fixed", "Fixed spacing and capitalization. Turn on on-device AI in Settings to catch more.", canUndo: true);
            return;
        }

        var working = style == FormatStyle.Simple ? "Fixing errors" : "Polishing wording";
        SetAiBusy($"{working} with {AIService.ProviderName} on this PC…");
        try
        {
            if (rich is null)
            {
                LoadBody(await AIService.FormatEntryAsync(original, style), null);
            }
            else
            {
                var (markdown, _) = await AIService.FormatMarkdownAsync(rich, style, (line, total) =>
                    AiBar.Message = $"{working} with {AIService.ProviderName} on this PC… line {line} of {total}");
                LoadBody(original, markdown);
            }

            if (string.IsNullOrWhiteSpace(TitleBox.Text) && await AIService.SuggestTitleAsync(BodyText) is { } title)
            {
                TitleBox.Text = title;
            }

            ShowAiResult(style == FormatStyle.Simple ? "Errors fixed" : "Wording polished",
                style == FormatStyle.Simple
                    ? $"Fixed by {AIService.ProviderName}. Your entry wasn't sent to the cloud."
                    : $"Polished by {AIService.ProviderName}. Read it over, and select Undo if it doesn't sound like you.",
                canUndo: true);
        }
        catch (Exception ex)
        {
            ShowAiError(AIService.Describe(ex));
        }
    }

    private async void Reflect_Click(object sender, RoutedEventArgs e)
    {
        if (!AIService.IsAvailable || string.IsNullOrWhiteSpace(BodyText))
        {
            ShowAiError(AIService.IsAvailable ? "Write a little first, then ask for a reflection." : "Turn on on-device AI in Settings to get reflections.");
            return;
        }

        SetAiBusy("Reflecting on your entry…");
        try
        {
            _entry.Title = TitleBox.Text.Trim();
            _entry.ReflectionDismissed = false;
            _entry.ReflectionStyle = AIService.ReflectionStyle;
            _entry.Reflection = await AIService.ReflectAsync(_entry, BodyText);
            ShowReflection(_entry.Reflection);
            AiBar.IsOpen = false;
            SetAiControlsEnabled(true);
        }
        catch (Exception ex)
        {
            ShowAiError(AIService.Describe(ex));
        }
    }

    private void RemoveReflection_Click(object sender, RoutedEventArgs e)
    {
        _entry.Reflection = null;
        _entry.ReflectionDismissed = true;
        ShowReflection(null);
    }

    private void ShowReflection(string? reflection)
    {
        ReflectionText.Text = reflection ?? "";
        ReflectionCard.Visibility = string.IsNullOrEmpty(reflection) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Inspire_Click(object sender, RoutedEventArgs e)
    {
        if (!AIService.IsAvailable)
        {
            ShowPrompt(SuggestionService.RandomPrompts(1)[0]);
            return;
        }

        SetAiBusy("Thinking of a question…");
        try
        {
            ShowPrompt(string.IsNullOrWhiteSpace(BodyText)
                ? (await AIService.PersonalizedPromptsAsync(JournalStore.Instance.Finished, 2))[0]
                : await AIService.FollowUpQuestionAsync(BodyText));
        }
        catch (Exception ex)
        {
            ShowAiError(AIService.Describe(ex));
        }
    }

    private void ShowPrompt(string prompt)
    {
        _entry.Prompt = prompt;
        PromptBar.Message = prompt;
        PromptBar.IsOpen = true;
        AiBar.IsOpen = false;
        SetAiControlsEnabled(true);
        BodyBox.Focus(FocusState.Programmatic);
        MoveCursorToEnd();
    }

    private void SetAiBusy(string message)
    {
        SetAiControlsEnabled(false);
        AiBar.Severity = InfoBarSeverity.Informational;
        AiBar.Title = "Working";
        AiBar.Message = message;
        AiBar.IsClosable = false;
        AiUndoButton.Visibility = Visibility.Collapsed;
        AiProgress.IsIndeterminate = true;
        AiProgress.Visibility = Visibility.Visible;
        AiBar.IsOpen = true;
    }

    private void ShowAiResult(string title, string message, bool canUndo)
    {
        SetAiControlsEnabled(true);
        AiBar.Severity = InfoBarSeverity.Success;
        AiBar.Title = title;
        AiBar.Message = message;
        AiBar.IsClosable = true;
        AiUndoButton.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;
        AiProgress.Visibility = Visibility.Collapsed;
        AiBar.IsOpen = true;
    }

    private void ShowAiError(string message)
    {
        ShowAiResult("Couldn't finish", message, canUndo: false);
        AiBar.Severity = InfoBarSeverity.Warning;
    }

    // Lock the text while the model works so a result never overwrites new typing.
    private void SetAiControlsEnabled(bool enabled)
    {
        FormatButton.IsEnabled = enabled;
        InspireButton.IsEnabled = enabled;
        ReflectButton.IsEnabled = enabled && AIService.IsAvailable;
        TitleBox.IsReadOnly = !enabled;
        BodyBox.IsReadOnly = !enabled;
        foreach (var button in FormatBar.Children.OfType<Control>()) button.IsEnabled = enabled;
    }

    private void AiUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_undoBody is { } body) LoadBody(body.Plain, body.Rich);
        if (_undoTitle is not null) TitleBox.Text = _undoTitle;
        AiBar.IsOpen = false;
    }

    #endregion

    #region Dictation

    private DictationSession? _dictation;
    private bool _dictationBusy;
    private int _dictationStart;

    private async void Dictate_Click(object sender, RoutedEventArgs e)
    {
        // The toggle flips itself on click; its state should follow whether we're really listening.
        DictateButton.IsChecked = _dictation is not null;
        if (_dictation is not null)
        {
            await StopDictationAsync();
            return;
        }

        if (_dictationBusy) return;
        _dictationBusy = true;
        try
        {
            if (!await VoiceService.IsModelReadyAsync() && !await SetUpVoiceModelAsync()) return;

            SetAiBusy("Getting the microphone ready…");
            _undoTitle = TitleBox.Text;
            _undoBody = (BodyText, RichText.ToMarkdown(BodyBox.Document));

            // New words go after what's already written, leaving its formatting alone.
            var existing = BodyText;
            if (existing.Length > 0 && !char.IsWhiteSpace(existing[^1]))
            {
                EditBody(document => document.GetRange(existing.Length, existing.Length).Text = " ");
                existing += " ";
            }

            _dictationStart = existing.Length;

            _dictation = await VoiceService.StartAsync(text => DispatcherQueue.TryEnqueue(() => ShowDictatedText(text)));
            DictateButton.IsChecked = true;
            AiBar.Title = "Listening";
            AiBar.Message = "Speak naturally. Your words are turned into text on this PC. Select Dictate again when you're done.";
        }
        catch (MicrophoneUnavailableException ex)
        {
            ShowAiError(ex.Message);
        }
        catch (Exception ex)
        {
            ShowAiError(AIService.Describe(ex));
        }
        finally
        {
            _dictationBusy = false;
        }
    }

    private void ShowDictatedText(string text)
    {
        if (_dictation is null) return;
        SetDictatedText(text);
        MoveCursorToEnd();
    }

    private async Task StopDictationAsync()
    {
        if (_dictation is not { } session) return;
        _dictation = null;
        DictateButton.IsChecked = false;

        var text = "";
        try
        {
            text = await session.StopAsync();
            await session.DisposeAsync();
        }
        catch (Exception ex)
        {
            AIService.LogError(ex);
        }

        if (text.Length == 0)
        {
            if (_undoBody is { } body) LoadBody(body.Plain, body.Rich);
            ShowAiError("Journal didn't hear anything. Check that your microphone is on and try again.");
            return;
        }

        SetDictatedText(text);
        var words = JournalEntry.CountWords(text);
        ShowAiResult("Dictated", $"Added {words} {(words == 1 ? "word" : "words")}, transcribed on this PC. Select Format to tidy it up.", canUndo: true);
    }

    /// <summary>First use: offer the one-time voice model download, with progress. True when it's ready.</summary>
    private async Task<bool> SetUpVoiceModelAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = "Set up dictation?",
            Content = "Journal turns your speech into text right on this PC, so your voice never leaves it and no recording is kept. " +
                      $"This needs a one-time download of the voice model ({VoiceService.ModelSize}). Dictation is English only for now.",
            PrimaryButtonText = "Download",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

        SetAiBusy("Downloading the voice model…");
        AiBar.Title = "Setting up dictation";
        AiProgress.IsIndeterminate = false;
        AiProgress.Value = 0;
        try
        {
            await VoiceService.DownloadModelAsync(
                percent => DispatcherQueue.TryEnqueue(() =>
                {
                    AiProgress.Value = percent;
                    AiBar.Message = $"Downloading the voice model… {percent:0}%";
                }),
                CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            ShowAiError("The voice model didn't finish downloading. Check your internet connection and try again.");
            AIService.LogError(ex);
            return false;
        }
    }

    #endregion

    private void UpdateWordCount()
    {
        var words = JournalEntry.CountWords(BodyText);
        WordCountText.Text = (words == 1 ? "1 word" : $"{words:N0} words") + (_savedNote.Length > 0 ? $" · {_savedNote}" : "");
    }

    private void DatePicker_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        // A date is required; don't let the picker be cleared.
        if (args.NewDate is null) sender.Date = args.OldDate ?? DateTimeOffset.Now;
    }

    private bool _pickingPhotos;

    private async void Draw_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Controls.SketchDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        await dialog.ShowAsync();
        if (dialog.Image is { } image && ReferenceEquals(Frame?.Content, this))
        {
            _photos.Add(new PhotoItem(await JournalStore.ImportPhotoAsync(image, ".png")));
        }
    }

    private async void AddPhoto_Click(object sender, RoutedEventArgs e)
    {
        // One picker at a time (keyboard shortcuts and accessibility tools can still reach the button).
        if (_pickingPhotos) return;
        _pickingPhotos = true;
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                ViewMode = PickerViewMode.Thumbnail,
            };
            foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".heic", ".webp", ".gif" }) picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow.Hwnd);

            var files = await picker.PickMultipleFilesAsync();

            // If the editor was left (and saved) meanwhile, don't import photos that would belong to nothing.
            if (!ReferenceEquals(Frame?.Content, this)) return;

            foreach (var file in files)
            {
                _photos.Add(new PhotoItem(await JournalStore.ImportPhotoAsync(file)));
            }
        }
        finally
        {
            _pickingPhotos = false;
        }
    }

    private void RemovePhoto_Click(object sender, RoutedEventArgs e) =>
        _photos.Remove((PhotoItem)((FrameworkElement)sender).Tag);

    // Done is what finishes an entry. Leaving any other way keeps a new entry as a draft.
    private void Done_Click(object sender, RoutedEventArgs e)
    {
        _entry.IsDraft = false;
        Leave();
    }

    private void Leave()
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(JournalPage), JournalPage.Mode.All);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        // Autosave may already have stored a new entry, so ask whenever there's something to lose.
        if (JournalStore.Instance.Find(_entry.Id) is not null)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                Title = _entry.IsDraft ? "Discard this draft?" : "Delete this entry?",
                Content = "It will be permanently removed from your journal, along with its photos.",
                PrimaryButtonText = _entry.IsDraft ? "Discard" : "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await JournalStore.Instance.DeleteAsync(_entry);
        }

        _deleted = true;
        Leave();
    }
}
