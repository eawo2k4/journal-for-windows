using System.Text.Json;
using Microsoft.UI.Xaml;

namespace WindowsJournal.Services;

public sealed class AppSettings
{
    public ElementTheme Theme { get; set; } = ElementTheme.Default;
    public bool RequireWindowsHello { get; set; }

    /// <summary>Lock after the window has been in the background this long. 0 locks only when Journal opens.</summary>
    public int AutoLockMinutes { get; set; } = 5;
    public bool AIEnabled { get; set; } = true;
    public bool AutoInsights { get; set; } = true;

    /// <summary>What the writer wants from journaling; suggestions lean toward these.</summary>
    public List<Models.EntryCategory> FocusAreas { get; set; } = [];
    public bool ReminderEnabled { get; set; }
    public TimeSpan ReminderTime { get; set; } = new(20, 0, 0);
    public bool HasSeenTour { get; set; }

    public bool BackupEnabled { get; set; }
    public string? BackupFolder { get; set; }
    public int BackupEveryDays { get; set; } = 1;
    public DateTimeOffset? LastBackup { get; set; }
}

/// <summary>
/// Unpackaged apps don't get ApplicationData.Current, so settings are a small JSON file
/// beside the journal data.
/// </summary>
public static class SettingsService
{
    private static readonly string SettingsFile = Path.Combine(JournalStore.DataFolder, "settings.json");

    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), JournalStore.JsonOptions) ?? new();
            }
        }
        catch (JsonException)
        {
            Current = new();
        }
    }

    public static void Save()
    {
        Directory.CreateDirectory(JournalStore.DataFolder);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(Current, JournalStore.JsonOptions));
    }
}
