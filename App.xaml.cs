using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using WindowsJournal.Helpers;
using WindowsJournal.Services;

namespace WindowsJournal;

public partial class App : Application
{
    public static MainWindow MainWindow { get; private set; } = null!;

    public App()
    {
        InitializeComponent();

        // Leave a trail for unexpected crashes; journal data is never written here.
        UnhandledException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => LogCrash(e.Exception);
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(JournalStore.DataFolder);
            File.AppendAllText(Path.Combine(JournalStore.DataFolder, "crash.log"), $"[{DateTimeOffset.Now:o}] {ex}\n\n");
        }
        catch (IOException)
        {
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        SettingsService.Load();
        await JournalStore.Instance.LoadAsync();

        // Reminder clicks: NotificationInvoked fires while Journal is open; otherwise Windows launches it
        // with the notification as its activation (handled below). Register before reading activation args.
        if (AppInfo.IsPackaged)
        {
            AppNotificationManager.Default.NotificationInvoked += (_, e) =>
                MainWindow?.DispatcherQueue.TryEnqueue(() => HandleNotification(e.Argument));
            AppNotificationManager.Default.Register();
        }

        MainWindow = new MainWindow();
        MainWindow.Activate();
        HandleActivation(AppInstance.GetCurrent().GetActivatedEventArgs());

        // Keep the daily reminder schedule current, e.g. skip today's once you've written.
        ReminderService.Reschedule();
        JournalStore.Instance.Changed += (_, _) => ReminderService.Reschedule();

        // Keep a pinned widget's streak current as entries are written.
        if (AppInfo.IsPackaged)
        {
            JournalStore.Instance.Changed += (_, _) => _ = Widgets.JournalWidget.UpdateAllAsync();
            _ = Widgets.JournalWidget.UpdateAllAsync();
        }

        // Automatic backups: on launch, and as you write in case Journal stays open for days.
        BackupService.RunIfDue();
        JournalStore.Instance.Changed += (_, _) => BackupService.RunIfDue();

        // Fill in mood, topic and reflection for older entries once AI is ready.
        AIService.StatusChanged += (_, _) => EntryInsights.BackfillOnce();

        // Look for on-device AI in the background; features light up when it's found.
        _ = AIService.DetectAsync();
    }

    /// <summary>Journal was launched again (or a reminder was clicked) while it's already open.</summary>
    public static void OnRedirectedActivation(AppActivationArguments args) =>
        MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            MainWindow.BringToFront();
            HandleActivation(args);
        });

    private static void HandleActivation(AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.AppNotification && args.Data is AppNotificationActivatedEventArgs notification)
        {
            HandleNotification(notification.Argument);
        }
        else if (args.Kind == ExtendedActivationKind.Protocol
                 && args.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocol
                 && protocol.Uri.Host.Equals("new", StringComparison.OrdinalIgnoreCase))
        {
            // windowsjournal://new, from the widget's Write button.
            MainWindow.BringToFront();
            MainWindow.OpenNewEntry();
        }
        else if (args.Kind == ExtendedActivationKind.ShareTarget
                 && args.Data is Windows.ApplicationModel.Activation.IShareTargetActivatedEventArgs share)
        {
            ReceiveShare(share.ShareOperation);
        }
    }

    private static async void ReceiveShare(Windows.ApplicationModel.DataTransfer.ShareTarget.ShareOperation share)
    {
        try
        {
            share.ReportStarted();
            var content = await SharedContent.ReadAsync(share);
            share.ReportCompleted();

            MainWindow.BringToFront();
            if (content is null) return;

            SharedContent.Pending = content;
            MainWindow.NavigateTo(typeof(Pages.EntryPage), new Pages.EntryPage.Args(null, null));
        }
        catch (Exception ex)
        {
            AIService.LogError(ex);
            share.ReportError("Journal couldn't read what was shared.");
        }
    }

    private static void HandleNotification(string argument)
    {
        MainWindow.BringToFront();
        if (argument.Contains(ReminderService.NewEntryArgument, StringComparison.Ordinal))
        {
            MainWindow.OpenNewEntry();
        }
    }
}
