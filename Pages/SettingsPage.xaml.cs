using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Security.Credentials.UI;
using Windows.Storage.Pickers;
using WindowsJournal.Services;

namespace WindowsJournal.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();

        var settings = SettingsService.Current;
        ThemeCombo.SelectedItem = ThemeCombo.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == settings.Theme.ToString());
        HelloToggle.IsOn = settings.RequireWindowsHello;
        LockNowCard.IsEnabled = settings.RequireWindowsHello;
        AutoLockCard.IsEnabled = settings.RequireWindowsHello;
        AutoLockCombo.SelectedItem = AutoLockCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == settings.AutoLockMinutes.ToString())
            ?? AutoLockCombo.Items[1];
        UpdateStorage();
        UpdateFocusSummary();
        VersionText.Text = $"Version {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
        UpdateAiStatus();
        LoadReminderSettings();
        LoadBackupSettings();

        AIService.StatusChanged += AIService_StatusChanged;
        AIService.DownloadProgressChanged += AIService_DownloadProgressChanged;
        JournalStore.Instance.Changed += JournalStore_Changed; // e.g. after an import
        BackupService.StatusChanged += BackupService_StatusChanged;
        Unloaded += (_, _) =>
        {
            BackupService.StatusChanged -= BackupService_StatusChanged;
            AIService.StatusChanged -= AIService_StatusChanged;
            AIService.DownloadProgressChanged -= AIService_DownloadProgressChanged;
            JournalStore.Instance.Changed -= JournalStore_Changed;
        };

        _loading = false;
    }

    #region Automatic backups

    private void LoadBackupSettings()
    {
        var settings = SettingsService.Current;
        BackupToggle.IsOn = settings.BackupEnabled;
        BackupFrequencyCombo.SelectedItem = BackupFrequencyCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == settings.BackupEveryDays.ToString())
            ?? BackupFrequencyCombo.Items[0];
        UpdateBackupStatus();
    }

    private void BackupService_StatusChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateBackupStatus);

    private void UpdateBackupStatus()
    {
        var settings = SettingsService.Current;
        BackupExpander.Description = BackupService.LastError
            ?? (settings.BackupEnabled ? "Encrypted copies of your journal are saved to the folder below." : "Keep encrypted copies of your journal in a folder you choose.");

        BackupFolderCard.Description = settings.BackupFolder switch
        {
            null => "No folder chosen yet.",
            var folder when BackupService.IsInOneDrive(folder) =>
                $"{folder}\nThis folder syncs to OneDrive. Your backups are encrypted there, and you'll need your recovery key to open them on another PC.",
            var folder => folder,
        };

        BackupNowCard.Description = BackupService.IsRunning ? "Backing up…"
            : settings.LastBackup is { } last ? $"{last.LocalDateTime:dddd, MMMM d} at {last.LocalDateTime:t}"
            : "Not yet";
        BackupNowButton.IsEnabled = !BackupService.IsRunning && settings.BackupFolder is not null && BackupService.HasKey;
        RecoveryKeyCard.IsEnabled = BackupService.HasKey;
    }

    private async void BackupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var settings = SettingsService.Current;
        if (BackupToggle.IsOn)
        {
            // Pretend it's off until everything it needs is in place.
            if (settings.BackupFolder is null && !await PickBackupFolderAsync()
                || !BackupService.HasKey && !await SetUpRecoveryKeyAsync())
            {
                SetBackupToggleSilently(false);
                return;
            }

            settings.BackupEnabled = true;
            SettingsService.Save();
            UpdateBackupStatus();
            await BackupService.BackUpNowAsync();
        }
        else
        {
            settings.BackupEnabled = false;
            SettingsService.Save();
        }

        UpdateBackupStatus();
    }

    private void SetBackupToggleSilently(bool value)
    {
        _loading = true;
        BackupToggle.IsOn = value;
        _loading = false;
    }

    private async void ChooseBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await PickBackupFolderAsync()) UpdateBackupStatus();
    }

    private async Task<bool> PickBackupFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow.Hwnd);
        if (await picker.PickSingleFolderAsync() is not { } folder) return false;

        SettingsService.Current.BackupFolder = folder.Path;
        SettingsService.Save();
        return true;
    }

    private void BackupFrequency_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BackupFrequencyCombo.SelectedItem is not ComboBoxItem { Tag: string days }) return;

        SettingsService.Current.BackupEveryDays = int.Parse(days);
        SettingsService.Save();
    }

    private async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        if (await BackupService.BackUpNowAsync() && BackupService.LastError is null)
        {
            ShowDataInfo(InfoBarSeverity.Success, "Backed up", "Your journal was saved to your backup folder.");
        }
    }

    /// <summary>Creates a key and makes sure it's been saved somewhere, or takes one from another PC.</summary>
    private async Task<bool> SetUpRecoveryKeyAsync()
    {
        var secret = BackupCrypto.NewSecret();
        var key = BackupCrypto.ToRecoveryKey(secret);

        var saved = new CheckBox { Content = "I've saved my recovery key" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = "Save your recovery key",
            Content = RecoveryKeyPanel(key,
                "You'll need this key to open your backups on another PC or after reinstalling Journal. Keep it somewhere safe, away from this PC.\n\n" +
                "If you lose it and this PC is gone, nobody can open your backups. It can't be reset.", saved),
            PrimaryButtonText = "Turn on backups",
            SecondaryButtonText = "I already have a key",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        saved.Click += (_, _) => dialog.IsPrimaryButtonEnabled = saved.IsChecked == true;

        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                BackupService.SaveKey(secret);
                return true;
            case ContentDialogResult.Secondary:
                if (await AskForRecoveryKeyAsync("Enter your recovery key", "Using the same key on all your PCs lets each one open the others' backups.") is not { } existing) return false;
                BackupService.SaveKey(existing);
                return true;
            default:
                return false;
        }
    }

    private StackPanel RecoveryKeyPanel(string key, string message, CheckBox? saved = null)
    {
        var copy = new Button { Content = "Copy" };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(key);
            Clipboard.SetContent(package);
        };

        var save = new Button { Content = "Save to a file" };
        save.Click += async (_, _) =>
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "Journal recovery key" };
            picker.FileTypeChoices.Add("Text file", [".txt"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow.Hwnd);
            if (await picker.PickSaveFileAsync() is { } file)
            {
                await Windows.Storage.FileIO.WriteTextAsync(file, $"Journal for Windows recovery key\r\n\r\n{key}\r\n\r\nAnyone with this key and your backup files can read your journal. Keep it private.\r\n");
            }
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(copy);
        buttons.Children.Add(save);

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = key,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 16,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(buttons);
        if (saved is not null) panel.Children.Add(saved);
        return panel;
    }

    private async void ShowRecoveryKey_Click(object sender, RoutedEventArgs e)
    {
        if (BackupService.RecoveryKey is not { } key) return;

        // The key opens every backup, so it deserves the same protection as the journal.
        if (SettingsService.Current.RequireWindowsHello)
        {
            var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(App.MainWindow.Hwnd, "Show your recovery key");
            if (result != UserConsentVerificationResult.Verified) return;
        }

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = "Your recovery key",
            Content = RecoveryKeyPanel(key, "Anyone with this key and your backup files can read your journal. Keep it private."),
            CloseButtonText = "Close",
        }.ShowAsync();
    }

    /// <summary>Asks for a recovery key until a valid one is entered. Returns null if canceled.</summary>
    private async Task<byte[]?> AskForRecoveryKeyAsync(string title, string message)
    {
        var box = new TextBox { Header = "Recovery key", PlaceholderText = "XXXX-XXXX-XXXX-…" };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);

        var secret = Array.Empty<byte>();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = title,
            Content = panel,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = BackupCrypto.TryParseRecoveryKey(box.Text, out secret);
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? secret : null;
    }

    /// <summary>Runs an import step, asking for the recovery key when this PC doesn't have the one the backup needs.</summary>
    private async Task<T?> WithRecoveryKeyAsync<T>(Func<Task<T>> step) where T : class
    {
        var message = "This backup was made with a different key. Enter the recovery key you saved when you turned on backups.";
        while (true)
        {
            try
            {
                return await step();
            }
            catch (BackupKeyNeededException)
            {
                if (await AskForRecoveryKeyAsync("Enter your recovery key", message) is not { } secret) return null;
                BackupService.RememberForNow(secret);
                message = "That key doesn't open this backup, or the file is damaged. Check the key and try again.";
            }
        }
    }

    #endregion

    #region Reminders

    private void LoadReminderSettings()
    {
        var settings = SettingsService.Current;
        ReminderToggle.IsOn = settings.ReminderEnabled;
        ReminderTimePicker.SelectedTime = settings.ReminderTime;
        ReminderTimePicker.IsEnabled = settings.ReminderEnabled;

        if (!ReminderService.IsSupported)
        {
            // Scheduled notifications need package identity.
            ReminderToggle.IsEnabled = false;
            ReminderExpander.Description = "Available in the installed version of Journal.";
        }

        UpdateReminderWarning();
    }

    private void ReminderToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Current.ReminderEnabled = ReminderToggle.IsOn;
        SettingsService.Save();
        ReminderTimePicker.IsEnabled = ReminderToggle.IsOn;
        if (ReminderToggle.IsOn) ReminderExpander.IsExpanded = true;
        ReminderService.Reschedule();
        UpdateReminderWarning();
    }

    private void ReminderTime_Changed(TimePicker sender, TimePickerSelectedValueChangedEventArgs args)
    {
        if (_loading || args.NewTime is not { } time) return;
        SettingsService.Current.ReminderTime = time;
        SettingsService.Save();
        ReminderService.Reschedule();
        UpdateReminderWarning();
    }

    private void UpdateReminderWarning()
    {
        ReminderTimeCard.Description = ReminderService.NextReminder() is { } next
            ? $"Next reminder: {next.LocalDateTime:dddd, MMMM d} at {next.LocalDateTime:t}"
            : "";

        var blocked = SettingsService.Current.ReminderEnabled && ReminderService.IsBlockedByWindows();
        ReminderInfo.Title = "Notifications are off for Journal";
        ReminderInfo.Message = "Turn them on in Windows Settings > System > Notifications to get reminders.";
        ReminderInfo.IsOpen = blocked;
    }

    #endregion

    #region AI

    private void AIService_StatusChanged(object? sender, EventArgs e) => UpdateAiStatus();

    private void UpdateAiStatus()
    {
        var wasLoading = _loading;
        _loading = true;
        AiCard.Description = AIService.StatusText;
        AiToggle.IsEnabled = AIService.Provider != AIProvider.None;
        AiToggle.IsOn = AIService.IsAvailable;
        InsightsToggle.IsEnabled = AIService.IsAvailable;
        InsightsToggle.IsOn = SettingsService.Current.AutoInsights;

        // Windows AI needs no download; hide the model card on Copilot+ PCs and where AI can't run.
        var state = AIService.ModelState;
        ModelCard.Visibility = AIService.Provider == AIProvider.WindowsAI || state == ModelState.Unsupported
            ? Visibility.Collapsed
            : Visibility.Visible;
        DownloadButton.Visibility = state == ModelState.NotDownloaded ? Visibility.Visible : Visibility.Collapsed;
        DownloadButton.IsEnabled = state == ModelState.NotDownloaded;
        DownloadProgressPanel.Visibility = state == ModelState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        CancelDownloadButton.Visibility = DownloadProgressPanel.Visibility;
        RemoveModelButton.Visibility = state == ModelState.Ready && AIService.Provider == AIProvider.LocalModel
            ? Visibility.Visible
            : Visibility.Collapsed;
        ModelCard.Description = state switch
        {
            ModelState.Ready => "Phi-4 mini is downloaded and runs on this PC. Remove it to free up about 4 GB.",
            ModelState.Checking => "Checking…",
            _ => "Copilot+ PCs have AI built into Windows. On this PC, Journal uses a small AI model that runs on your own GPU or CPU.",
        };
        UpdateDownloadProgress();
        _loading = wasLoading;
    }

    private void AIService_DownloadProgressChanged(object? sender, EventArgs e) => UpdateDownloadProgress();

    private void UpdateDownloadProgress()
    {
        DownloadStageText.Text = $"{AIService.DownloadStage}  {AIService.DownloadPercent:0}%";
        DownloadProgress.Value = AIService.DownloadPercent;
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e) => await AIService.DownloadModelAsync();

    private void CancelDownload_Click(object sender, RoutedEventArgs e) => AIService.CancelDownload();

    private async void RemoveModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = "Remove the AI model?",
            Content = "Polish wording, Reflect, and personalized suggestions will stop working until you download it again. Your entries aren't affected.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        RemoveModelButton.IsEnabled = false;
        await AIService.RemoveModelAsync();
        RemoveModelButton.IsEnabled = true;
    }

    private void InsightsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Current.AutoInsights = InsightsToggle.IsOn;
        SettingsService.Save();
        EntryInsights.BackfillOnce();
    }

    private void AiToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Current.AIEnabled = AiToggle.IsOn;
        SettingsService.Save();
        AIService.NotifySettingsChanged();
    }

    #endregion

    private void Tour_Click(object sender, RoutedEventArgs e) => App.MainWindow.ShowWelcome();

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;

        SettingsService.Current.Theme = Enum.Parse<ElementTheme>(tag);
        SettingsService.Save();
        App.MainWindow.ApplyTheme(SettingsService.Current.Theme);
    }

    private async void HelloToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        HelloInfo.IsOpen = false;

        if (HelloToggle.IsOn)
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            if (availability != UserConsentVerifierAvailability.Available)
            {
                HelloInfo.Title = "Windows Hello isn't available";
                HelloInfo.Message = "Set up a face, fingerprint, or PIN in Settings > Accounts > Sign-in options, then try again.";
                HelloInfo.IsOpen = true;
                SetToggleSilently(false);
                return;
            }

            // Confirm it's really the owner before turning the lock on.
            var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(App.MainWindow.Hwnd, "Turn on journal lock");
            if (result != UserConsentVerificationResult.Verified)
            {
                SetToggleSilently(false);
                return;
            }
        }

        SettingsService.Current.RequireWindowsHello = HelloToggle.IsOn;
        SettingsService.Save();
        LockNowCard.IsEnabled = HelloToggle.IsOn;
        AutoLockCard.IsEnabled = HelloToggle.IsOn;
    }

    private void AutoLockCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AutoLockCombo.SelectedItem is not ComboBoxItem { Tag: string minutes }) return;

        SettingsService.Current.AutoLockMinutes = int.Parse(minutes);
        SettingsService.Save();
    }

    private void SetToggleSilently(bool value)
    {
        _loading = true;
        HelloToggle.IsOn = value;
        _loading = false;
    }

    private void LockNow_Click(object sender, RoutedEventArgs e) => App.MainWindow.Lock();

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{JournalStore.DataFolder}\"") { UseShellExecute = true });

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(BackupService.FileExtension);
        picker.FileTypeFilter.Add(".zip");
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow.Hwnd);
        if (await picker.PickSingleFileAsync() is not { } file) return;

        ImportButton.IsEnabled = false;
        DataInfo.IsOpen = false;
        try
        {
            // Show what would happen first; nothing changes until the user confirms.
            if (await WithRecoveryKeyAsync(() => JournalStore.Instance.PreviewImportAsync(file)) is not { } preview) return;

            if (preview.NewEntries == 0)
            {
                ShowDataInfo(InfoBarSeverity.Informational, "Nothing new to import",
                    $"All {Count(preview.AlreadyHave, "entry", "entries")} in {file.Name} are already in your journal.");
                return;
            }

            var details = $"Adds {Count(preview.NewEntries, "entry", "entries")}" +
                          (preview.Photos > 0 ? $" and {Count(preview.Photos, "photo", "photos")}" : "") + $" from {file.Name}.";
            if (preview.AlreadyHave > 0)
            {
                details += $" {Count(preview.AlreadyHave, "entry", "entries")} you already have will be skipped.";
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                Title = "Import from backup?",
                Content = details + "\n\nNothing in your journal is deleted or changed.",
                PrimaryButtonText = "Import",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var result = await JournalStore.Instance.ImportAsync(file);
            ShowDataInfo(InfoBarSeverity.Success, "Imported",
                $"Added {Count(result.NewEntries, "entry", "entries")}" +
                (result.Photos > 0 ? $" and {Count(result.Photos, "photo", "photos")}" : "") + " to your journal.");
        }
        catch (InvalidDataException)
        {
            ShowDataInfo(InfoBarSeverity.Error, "Couldn't import", $"{file.Name} isn't a Journal backup. Choose a backup, or a .zip or .json file made with Export.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowDataInfo(InfoBarSeverity.Error, "Couldn't import", "The file couldn't be read. Your journal is unchanged.");
        }
        finally
        {
            ImportButton.IsEnabled = true;
        }
    }

    private void ShowDataInfo(InfoBarSeverity severity, string title, string message)
    {
        DataInfo.Severity = severity;
        DataInfo.Title = title;
        DataInfo.Message = message;
        DataInfo.IsOpen = true;
    }

    private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    private void FocusPicker_Changed(object? sender, EventArgs e) => UpdateFocusSummary();

    private void UpdateFocusSummary()
    {
        var picked = Models.CategoryOption.All.Where(c => SettingsService.Current.FocusAreas.Contains(c.Value)).Select(c => c.Label).ToList();
        FocusExpander.Description = picked.Count > 0
            ? $"{string.Join(", ", picked)}. Suggestions lean toward these, and Insights shows how they're going."
            : "Pick what you'd like from journaling, and your suggestions and Insights will follow.";
    }

    private void JournalStore_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateStorage);

    private void UpdateStorage()
    {
        var (photos, bytes) = JournalStore.StorageUsage();
        StorageExpander.Description =
            $"{Count(JournalStore.Instance.Entries.Count, "entry", "entries")} · {Count(photos, "photo", "photos")} · {FormatSize(bytes)} · Encrypted on this PC";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"Journal export {DateTime.Now:yyyy-MM-dd}",
        };
        // The first choice is the default: a complete backup. JSON stays available for anyone who only wants the text.
        picker.FileTypeChoices.Add("Journal backup with photos", [".zip"]);
        picker.FileTypeChoices.Add("Entries only (JSON)", [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow.Hwnd);

        if (await picker.PickSaveFileAsync() is not { } file) return;

        ExportButton.IsEnabled = false;
        DataInfo.IsOpen = false;
        try
        {
            var entries = JournalStore.Instance.Entries.Count;
            string what;
            if (file.FileType.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                await JournalStore.Instance.ExportAsync(file);
                what = $"{entries} {(entries == 1 ? "entry" : "entries")}";
            }
            else
            {
                var photos = await JournalStore.Instance.ExportBackupAsync(file);
                what = $"{entries} {(entries == 1 ? "entry" : "entries")} and {photos} {(photos == 1 ? "photo" : "photos")}";
            }

            DataInfo.Severity = InfoBarSeverity.Success;
            DataInfo.Title = "Exported";
            DataInfo.Message = $"Saved {what} to {file.Name}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            DataInfo.Severity = InfoBarSeverity.Error;
            DataInfo.Title = "Couldn't export";
            DataInfo.Message = "Your journal is unchanged. Try saving to a different folder.";
        }
        finally
        {
            ExportButton.IsEnabled = true;
            DataInfo.IsOpen = true;
        }
    }
}
