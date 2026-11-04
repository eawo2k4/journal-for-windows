using System.Security.Cryptography;

namespace WindowsJournal.Services;

/// <summary>No key on this PC opens a backup, so the writer has to enter its recovery key.</summary>
public sealed class BackupKeyNeededException : Exception;

/// <summary>
/// Automatic backups: encrypted copies of the journal in a folder the writer chooses. This PC remembers the key
/// (protected by the Windows account), so backups just run. Keeps the newest few and only ever deletes its own files.
/// </summary>
public static class BackupService
{
    public const string FileExtension = ".journalbackup";

    private const int FilesToKeep = 5;

    // A backup is built in memory, and a single array can't go much past this.
    private const long MaxJournalBytes = 1536L * 1024 * 1024;

    private static readonly string KeyFile = Path.Combine(JournalStore.DataFolder, "backup.key");
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // Keys typed in to open someone's backup, kept only until Journal closes.
    private static readonly List<byte[]> SessionKeys = [];

    public static bool IsRunning { get; private set; }

    /// <summary>Why the last backup didn't work, for Settings to show. Null after a success.</summary>
    public static string? LastError { get; private set; }

    public static event EventHandler? StatusChanged;

    public static bool HasKey => LoadKey() is not null;

    public static string? RecoveryKey => LoadKey() is { } secret ? BackupCrypto.ToRecoveryKey(secret) : null;

    public static void SaveKey(byte[] secret)
    {
        Directory.CreateDirectory(JournalStore.DataFolder);
        File.WriteAllBytes(KeyFile, DataProtection.Protect(secret));
    }

    private static byte[]? LoadKey()
    {
        try
        {
            return File.Exists(KeyFile) ? DataProtection.Unprotect(File.ReadAllBytes(KeyFile)) : null;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>A key typed in to open a backup. The first one becomes this PC's key if it doesn't have its own.</summary>
    public static void RememberForNow(byte[] secret) => SessionKeys.Add(secret);

    /// <exception cref="BackupKeyNeededException">No key on this PC opens the file.</exception>
    /// <exception cref="InvalidDataException">The file isn't a Journal backup.</exception>
    public static byte[] Decrypt(byte[] data)
    {
        if (!BackupCrypto.IsBackup(data)) throw new InvalidDataException("This file isn't a Journal backup.");

        var own = LoadKey();
        IEnumerable<byte[]> keys = own is null ? SessionKeys : [own, .. SessionKeys];
        foreach (var secret in keys)
        {
            try
            {
                var plain = BackupCrypto.Decrypt(data, secret);
                if (own is null) SaveKey(secret);
                return plain;
            }
            catch (CryptographicException)
            {
                // Wrong key, or a damaged file. Nothing can tell those apart, so try the next one.
            }
        }

        throw new BackupKeyNeededException();
    }

    /// <summary>Backs up if one is due and something has changed since the last one.</summary>
    public static async void RunIfDue()
    {
        var settings = SettingsService.Current;
        if (!settings.BackupEnabled || settings.BackupFolder is null || !HasKey) return;

        if (settings.LastBackup is { } last)
        {
            if (DateTimeOffset.Now - last < TimeSpan.FromDays(settings.BackupEveryDays)) return;
            if (JournalStore.LastChanged <= last) return;
        }

        await BackUpNowAsync();
    }

    /// <summary>Writes a backup and removes the oldest beyond the ones kept. Returns false if it didn't work; see <see cref="LastError"/>.</summary>
    public static async Task<bool> BackUpNowAsync()
    {
        var settings = SettingsService.Current;
        if (settings.BackupFolder is not { } folder || LoadKey() is not { } secret) return false;
        if (JournalStore.Instance.Entries.Count == 0) return true;
        if (!await Gate.WaitAsync(0)) return true; // one is already running

        IsRunning = true;
        StatusChanged?.Invoke(null, EventArgs.Empty);
        try
        {
            if (JournalStore.StorageUsage().Bytes > MaxJournalBytes)
            {
                LastError = "Your journal is too large for automatic backups. Use Export instead.";
                return false;
            }

            var entries = JournalStore.Instance.Entries.ToList();
            var name = $"Journal backup {DateTime.Now:yyyy-MM-dd HHmmss}{FileExtension}";
            await Task.Run(async () =>
            {
                using var zip = new MemoryStream();
                await JournalStore.WriteBackupAsync(zip, entries);
                var encrypted = BackupCrypto.Encrypt(zip.ToArray(), secret);

                Directory.CreateDirectory(folder);
                var target = Path.Combine(folder, name);
                var temp = target + ".tmp";
                await File.WriteAllBytesAsync(temp, encrypted);
                File.Move(temp, target, overwrite: true);

                foreach (var old in Directory.GetFiles(folder, $"Journal backup *{FileExtension}").OrderDescending().Skip(FilesToKeep))
                {
                    File.Delete(old);
                }
            });

            settings.LastBackup = DateTimeOffset.Now;
            SettingsService.Save();
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or OutOfMemoryException)
        {
            AIService.LogError(ex);
            LastError = "Couldn't save a backup there. Check that the folder is still available.";
            return false;
        }
        finally
        {
            IsRunning = false;
            Gate.Release();
            StatusChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>True if the folder is inside OneDrive, where backups are uploaded.</summary>
    public static bool IsInOneDrive(string folder) =>
        new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable)
            .Any(root => !string.IsNullOrEmpty(root) && folder.StartsWith(root, StringComparison.OrdinalIgnoreCase));
}
