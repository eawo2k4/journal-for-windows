using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;
using Windows.Security.Credentials.UI;
using WindowsJournal.Controls;
using WindowsJournal.Pages;
using WindowsJournal.Services;

namespace WindowsJournal;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.Resize(new SizeInt32(1200, 820));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Journal.ico"));

        ApplyTheme(SettingsService.Current.Theme);
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionButtons();
        Activated += MainWindow_Activated;
        AppWindow.Closing += AppWindow_Closing;
        Closed += (_, _) => AIService.Shutdown(); // free the model's GPU memory

        ContentFrame.Navigate(typeof(JournalPage), JournalPage.Mode.All);

        if (SettingsService.Current.RequireWindowsHello)
        {
            Lock();
            _promptWhenActive = true;
        }
        else if (!SettingsService.Current.HasSeenTour)
        {
            // Dialogs need a XamlRoot, which exists once the content has loaded.
            // Loaded can fire more than once, so only listen for the first.
            void OnLoaded(object sender, RoutedEventArgs e)
            {
                RootGrid.Loaded -= OnLoaded;
                if (!SettingsService.Current.HasSeenTour) ShowWelcome();
            }

            RootGrid.Loaded += OnLoaded;
        }
    }

    public nint Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public void ApplyTheme(ElementTheme theme)
    {
        RootGrid.RequestedTheme = theme;
        UpdateCaptionButtons();
    }

    // With ExtendsContentIntoTitleBar the caption buttons don't follow RequestedTheme on their own.
    private void UpdateCaptionButtons()
    {
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        tb.ButtonHoverBackgroundColor = dark ? ColorHelper.FromArgb(0x15, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x09, 0, 0, 0);
        tb.ButtonHoverForegroundColor = tb.ButtonForegroundColor;
        tb.ButtonPressedBackgroundColor = dark ? ColorHelper.FromArgb(0x0B, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x06, 0, 0, 0);
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        // Dim the title text when the window is inactive, like inbox apps do.
        // Opacity, not a brush: looking one up in the app's resources would use Windows' theme, not the theme picked in Settings.
        AppTitleText.Opacity = args.WindowActivationState == WindowActivationState.Deactivated ? 0.55 : 1;

        PromptIfNeeded(args.WindowActivationState);
        UpdateAutoLock(args.WindowActivationState);
    }

    private bool _savedBeforeClose;

    // Entries save when you leave the editor; closing the window doesn't leave it, so save first, then close.
    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_savedBeforeClose || ContentFrame.Content is not EntryPage page) return;

        args.Cancel = true;
        try
        {
            await page.SaveAsync();
        }
        finally
        {
            _savedBeforeClose = true;
            Close();
        }
    }

    /// <summary>Shows the window when Journal is launched again or a reminder is clicked.</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
    }

    #region Navigation

    public void NavigateTo(Type page, object? parameter = null) =>
        ContentFrame.Navigate(page, parameter, new DrillInNavigationTransitionInfo());

    /// <summary>From a reminder: start writing. If Journal is locked, the entry opens behind the lock screen.</summary>
    public void OpenNewEntry()
    {
        if (ContentFrame.Content is not EntryPage)
        {
            NavigateTo(typeof(EntryPage), new EntryPage.Args(null, null));
        }
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            NavigateIfNeeded(typeof(SettingsPage), null);
            return;
        }

        switch ((args.InvokedItemContainer as NavigationViewItem)?.Tag)
        {
            case "journal": NavigateIfNeeded(typeof(JournalPage), JournalPage.Mode.All); break;
            case "favorites": NavigateIfNeeded(typeof(JournalPage), JournalPage.Mode.Favorites); break;
            case "insights": NavigateIfNeeded(typeof(InsightsPage), null); break;
        }
    }

    private void NavigateIfNeeded(Type page, object? parameter)
    {
        if (ContentFrame.CurrentSourcePageType == page && Equals(ContentFrame.Tag, parameter))
        {
            return;
        }

        ContentFrame.Navigate(page, parameter, new EntranceNavigationTransitionInfo());
    }

    private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    // Keep the selected nav item in sync with whatever page we landed on (including after Back).
    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        ContentFrame.Tag = e.Parameter;

        if (e.SourcePageType == typeof(SettingsPage))
        {
            NavView.SelectedItem = NavView.SettingsItem;
            return;
        }

        var tag = e.SourcePageType == typeof(InsightsPage) ? "insights"
            : e.SourcePageType == typeof(JournalPage) && e.Parameter is JournalPage.Mode.Favorites ? "favorites"
            : e.SourcePageType == typeof(JournalPage) ? "journal"
            : null;

        if (tag is not null)
        {
            NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == tag);
        }
    }

    #endregion

    #region Search

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ShowSearch(sender.Text);
        }
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
        ShowSearch(args.QueryText);

    private void ShowSearch(string query)
    {
        if (ContentFrame.Content is not JournalPage)
        {
            ContentFrame.Navigate(typeof(JournalPage), JournalPage.Mode.All, new SuppressNavigationTransitionInfo());
        }

        ((JournalPage)ContentFrame.Content).ApplySearch(query);
    }

    #endregion

    #region Welcome slideshow

    private bool _welcomeOpen;

    public async void ShowWelcome()
    {
        if (_welcomeOpen || RootGrid.XamlRoot is null) return;
        _welcomeOpen = true;
        try
        {
            var dialog = new WelcomeDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.RequestedTheme };
            await dialog.ShowAsync();
        }
        finally
        {
            _welcomeOpen = false;
            if (!SettingsService.Current.HasSeenTour)
            {
                SettingsService.Current.HasSeenTour = true;
                SettingsService.Save();
            }
        }
    }

    #endregion

    #region Windows Hello lock

    // Ask for Windows Hello the next time the window is active, instead of waiting for a click on Unlock.
    private bool _promptWhenActive;
    private bool _verifying;

    private bool IsLocked => LockOverlay.Visibility == Visibility.Visible;

    public async void Lock()
    {
        NavView.Visibility = Visibility.Collapsed;
        LockOverlay.Visibility = Visibility.Visible;
        UnlockError.Visibility = Visibility.Collapsed;

        // Dialogs and menus float above the lock screen, so they have to go too.
        if (RootGrid.XamlRoot is { } root)
        {
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
            {
                if (popup.Child is ContentDialog dialog) dialog.Hide();
                else popup.IsOpen = false;
            }
        }

        // Keep what's being written, and stop listening if dictation is on.
        if (ContentFrame.Content is EntryPage page) await page.SaveAsync();
    }

    private DispatcherTimer? _autoLock;

    private void UpdateAutoLock(WindowActivationState state)
    {
        _autoLock?.Stop();

        var minutes = SettingsService.Current.AutoLockMinutes;
        if (state != WindowActivationState.Deactivated || IsLocked || !SettingsService.Current.RequireWindowsHello || minutes <= 0) return;

        if (_autoLock is null)
        {
            _autoLock = new DispatcherTimer();
            _autoLock.Tick += (_, _) =>
            {
                _autoLock.Stop();
                if (IsLocked) return;
                Lock();
                _promptWhenActive = true;
            };
        }

        _autoLock.Interval = TimeSpan.FromMinutes(minutes);
        _autoLock.Start();
    }

    private void PromptIfNeeded(WindowActivationState state)
    {
        if (!IsLocked || _verifying) return;

        // Coming back to a locked window asks again. The Hello dialog itself deactivates the window, but that
        // happens while _verifying is set, so canceling leaves the Unlock button instead of asking in a loop.
        if (state == WindowActivationState.Deactivated)
        {
            _promptWhenActive = true;
        }
        else if (_promptWhenActive)
        {
            _promptWhenActive = false;
            DispatcherQueue.TryEnqueue(UnlockAsync);
        }
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e) => UnlockAsync();

    private async void UnlockAsync()
    {
        if (_verifying) return;

        _verifying = true;
        UnlockButton.IsEnabled = false;
        UnlockError.Visibility = Visibility.Collapsed;
        try
        {
            var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(Hwnd, "Unlock your journal");
            if (result == UserConsentVerificationResult.Verified)
            {
                LockOverlay.Visibility = Visibility.Collapsed;
                NavView.Visibility = Visibility.Visible;
                if (!SettingsService.Current.HasSeenTour) ShowWelcome();
            }
            else if (result != UserConsentVerificationResult.Canceled)
            {
                UnlockError.Text = result switch
                {
                    UserConsentVerificationResult.DeviceNotPresent or UserConsentVerificationResult.NotConfiguredForUser =>
                        "Windows Hello isn't set up on this PC. Set it up in Settings > Accounts > Sign-in options.",
                    UserConsentVerificationResult.DisabledByPolicy => "Windows Hello is turned off by your organization.",
                    _ => "Couldn't verify it's you. Try again.",
                };
                UnlockError.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _verifying = false;
            UnlockButton.IsEnabled = true;
        }
    }

    #endregion
}
