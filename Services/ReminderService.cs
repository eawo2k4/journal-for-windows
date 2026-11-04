using System.Security;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using WindowsJournal.Helpers;

namespace WindowsJournal.Services;

/// <summary>
/// Optional daily "time to write" notification. Windows delivers scheduled notifications itself,
/// so they arrive even when Journal is closed. Needs the installed (MSIX) app.
/// </summary>
public static class ReminderService
{
    public const string NewEntryArgument = "action=newEntry";

    // Scheduled ahead so reminders keep coming even if Journal isn't opened for a while.
    private const int DaysAhead = 14;

    public static bool IsSupported => AppInfo.IsPackaged;

    /// <summary>
    /// Replaces the schedule from today onward. Today's reminder is skipped once you've written today,
    /// so it never nags about something you've already done.
    /// </summary>
    public static void Reschedule()
    {
        if (!IsSupported) return;

        try
        {
            var notifier = ToastNotificationManager.CreateToastNotifier();
            foreach (var scheduled in notifier.GetScheduledToastNotifications())
            {
                notifier.RemoveFromSchedule(scheduled);
            }

            var settings = SettingsService.Current;
            if (!settings.ReminderEnabled) return;

            var wroteToday = JournalStore.Instance.Finished.Any(e => e.CreatedAt.Date == DateTime.Today);
            var prompts = SuggestionService.RandomPrompts(DaysAhead);
            for (var day = 0; day < DaysAhead; day++)
            {
                if (day == 0 && wroteToday) continue;

                var when = DateTime.Today.AddDays(day) + settings.ReminderTime;
                if (when <= DateTime.Now.AddMinutes(1)) continue;

                var toast = new ScheduledToastNotification(Build(prompts[day % prompts.Count]), when)
                {
                    Tag = $"day{day}",
                    Group = "daily",
                };
                notifier.AddToSchedule(toast);
            }
        }
        catch (Exception ex)
        {
            // Notifications can be blocked by Focus or policy; a missing reminder shouldn't break anything.
            AIService.LogError(ex);
        }
    }

    /// <summary>When the next scheduled reminder will arrive, if any.</summary>
    public static DateTimeOffset? NextReminder()
    {
        if (!IsSupported) return null;
        try
        {
            return ToastNotificationManager.CreateToastNotifier().GetScheduledToastNotifications()
                .Select(t => (DateTimeOffset?)t.DeliveryTime)
                .Min();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>True if Windows notifications are turned off for Journal (or entirely).</summary>
    public static bool IsBlockedByWindows()
    {
        if (!IsSupported) return false;
        try
        {
            return ToastNotificationManager.CreateToastNotifier().Setting != NotificationSetting.Enabled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static XmlDocument Build(string prompt)
    {
        var xml = new XmlDocument();
        xml.LoadXml($"""
            <toast launch="{NewEntryArgument}">
              <visual>
                <binding template="ToastGeneric">
                  <text>Time to write</text>
                  <text>{SecurityElement.Escape(prompt)}</text>
                </binding>
              </visual>
              <actions>
                <action content="Write now" arguments="{NewEntryArgument}" activationType="foreground" />
                <action content="Dismiss" arguments="dismiss" activationType="system" />
              </actions>
            </toast>
            """);
        return xml;
    }
}
