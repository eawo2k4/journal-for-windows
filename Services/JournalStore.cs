using System.Text.Json;
using Windows.Storage;
using WindowsJournal.Helpers;
using WindowsJournal.Models;

namespace WindowsJournal.Services;

/// <summary>
/// Local-first storage. Entries live in a single encrypted file under %LOCALAPPDATA%\WindowsJournal;
/// photos are copied (encrypted) next to it so the journal never depends on the original files.
/// </summary>
public sealed class JournalStore
{
    public static JournalStore Instance { get; } = new();

    // Where the unpackaged build keeps its data. The installed app can't see this folder (Windows virtualizes
    // AppData for packaged apps), so Install Journal.cmd copies a journal from here into the installed app.
    public static string UnpackagedDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsJournal");

    // Installed: the app's own data folder (removed on uninstall, like any Store app).
    // WINDOWSJOURNAL_DATA points the unpackaged build at a different folder, for testing without touching your real journal.
    public static string DataFolder { get; } =
        Environment.GetEnvironmentVariable("WINDOWSJOURNAL_DATA") is { Length: > 0 } custom ? custom
        : AppInfo.IsPackaged ? ApplicationData.Current.LocalFolder.Path
        : UnpackagedDataFolder;

    public static string PhotosFolder { get; } = Path.Combine(DataFolder, "Photos");

    private static readonly string EntriesFile = Path.Combine(DataFolder, "entries.dat");

    // Versions before encryption stored plain JSON here; it's migrated and deleted on first load.
    private static readonly string LegacyEntriesFile = Path.Combine(DataFolder, "entries.json");

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private List<JournalEntry> _entries = [];

    public IReadOnlyList<JournalEntry> Entries => _entries;

    /// <summary>Finished entries: what streaks, Insights, reminders and AI suggestions look at.</summary>
    public IEnumerable<JournalEntry> Finished => _entries.Where(e => !e.IsDraft);

    /// <summary>Set when saved entries couldn't be read and were moved aside, so the UI can say so.</summary>
    public string? UnreadableFile { get; private set; }

    public event EventHandler? Changed;

    public async Task LoadAsync()
    {
        Directory.CreateDirectory(PhotosFolder);

        var source = File.Exists(EntriesFile) ? EntriesFile : File.Exists(LegacyEntriesFile) ? LegacyEntriesFile : null;
        if (source is null)
        {
            return;
        }

        try
        {
            var json = await DataProtection.ReadFileAsync(source);
            var utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF };
            var body = json.AsSpan().StartsWith(utf8Bom) ? json.AsSpan(3) : json.AsSpan();
            _entries = JsonSerializer.Deserialize<List<JournalEntry>>(body, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or System.Security.Cryptography.CryptographicException)
        {
            // Unreadable (damaged, or encrypted by another Windows account). Move it aside rather than
            // overwriting someone's journal on the next save, and tell the user where it went.
            UnreadableFile = source + $".unreadable-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(source, UnreadableFile);
            _entries = [];
            return;
        }

        if (source == LegacyEntriesFile)
        {
            await MigrateToEncryptedAsync();
        }

        if (RepairPhotoPaths()) await SaveAsync();
    }

    /// <summary>
    /// Photos are stored with full paths. If the journal was moved (for example copied into the installed app
    /// by Install Journal.cmd), point any missing photo at the file of the same name in this journal's Photos folder.
    /// </summary>
    private bool RepairPhotoPaths()
    {
        var changed = false;
        foreach (var entry in _entries)
        {
            for (var i = 0; i < entry.PhotoPaths.Count; i++)
            {
                var path = entry.PhotoPaths[i];
                var local = Path.Combine(PhotosFolder, Path.GetFileName(path));
                if (!File.Exists(path) && File.Exists(local))
                {
                    entry.PhotoPaths[i] = local;
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>Encrypts a journal saved by an older version, then removes the plain copy.</summary>
    private async Task MigrateToEncryptedAsync()
    {
        foreach (var photo in Directory.EnumerateFiles(PhotosFolder))
        {
            await DataProtection.ProtectFileInPlaceAsync(photo);
        }

        await SaveAsync();
        File.Delete(LegacyEntriesFile);
    }

    /// <summary>Decrypted bytes of a journal photo.</summary>
    public static Task<byte[]> ReadPhotoAsync(string path) => DataProtection.ReadFileAsync(path);

    public JournalEntry? Find(Guid id) => _entries.FirstOrDefault(e => e.Id == id);

    public async Task UpsertAsync(JournalEntry entry)
    {
        entry.ModifiedAt = DateTimeOffset.Now;
        if (!_entries.Contains(entry))
        {
            _entries.Add(entry);
        }

        await SaveAsync();
    }

    /// <summary>Saves while someone is still typing: quietly, without telling the rest of the app every few seconds.</summary>
    public async Task AutosaveAsync(JournalEntry entry)
    {
        entry.ModifiedAt = DateTimeOffset.Now;
        if (!_entries.Contains(entry))
        {
            _entries.Add(entry);
        }

        await WriteAsync();
    }

    public async Task DeleteAsync(JournalEntry entry)
    {
        _entries.Remove(entry);
        foreach (var photo in entry.PhotoPaths.Where(p => p.StartsWith(PhotosFolder, StringComparison.OrdinalIgnoreCase)))
        {
            try { File.Delete(photo); } catch (IOException) { }
        }

        await SaveAsync();
    }

    /// <summary>Stores an encrypted copy of a picked photo in the journal's own folder and returns its path.</summary>
    public static async Task<string> ImportPhotoAsync(StorageFile file)
    {
        // Read through the StorageFile (not its path) so photos from phones/OneDrive placeholders work too.
        await using var source = await file.OpenStreamForReadAsync();
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer);
        return await ImportPhotoAsync(buffer.ToArray(), file.FileType);
    }

    /// <summary>Stores an encrypted image made or received in memory (a sketch, a shared picture) and returns its path.</summary>
    public static async Task<string> ImportPhotoAsync(byte[] image, string extension)
    {
        var path = Path.Combine(PhotosFolder, $"{Guid.NewGuid():N}{extension}");
        await File.WriteAllBytesAsync(path, DataProtection.Protect(image));
        return path;
    }

    /// <summary>
    /// Reads the saved entries without touching the store (no migration, no events). For the widget helper,
    /// which runs in its own process while the app may be open and writing.
    /// </summary>
    public static async Task<List<JournalEntry>> ReadSnapshotAsync()
    {
        try
        {
            var source = File.Exists(EntriesFile) ? EntriesFile : File.Exists(LegacyEntriesFile) ? LegacyEntriesFile : null;
            if (source is null) return [];
            var bytes = await DataProtection.ReadFileAsync(source);
            var body = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? bytes.AsSpan(3) : bytes.AsSpan();
            return JsonSerializer.Deserialize<List<JournalEntry>>(body, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or System.Security.Cryptography.CryptographicException)
        {
            return [];
        }
    }

    /// <summary>How many photos the journal holds and how much space it uses (entries plus photos).</summary>
    public static (int Photos, long Bytes) StorageUsage()
    {
        var photos = Directory.Exists(PhotosFolder) ? new DirectoryInfo(PhotosFolder).GetFiles() : [];
        var entries = new FileInfo(EntriesFile);
        return (photos.Length, photos.Sum(f => f.Length) + (entries.Exists ? entries.Length : 0));
    }

    #region Import

    /// <summary>What an import adds: new entries, entries already in the journal (skipped), and photos.</summary>
    public sealed record ImportSummary(int NewEntries, int AlreadyHave, int Photos);

    // Guards against damaged or hostile files; a real photo is far smaller.
    private const long MaxPhotoBytes = 200L * 1024 * 1024;

    /// <summary>Reads a backup without changing anything, so the user can see what Import would do.</summary>
    public async Task<ImportSummary> PreviewImportAsync(StorageFile file)
    {
        var (entries, zip) = await OpenBackupAsync(file);
        using (zip)
        {
            var fresh = entries.Where(e => Find(e.Id) is null).ToList();
            var photos = zip is null ? 0 : fresh.Sum(e => e.PhotoPaths.Count(p => BackupPhoto(zip, p) is not null));
            return new ImportSummary(fresh.Count, entries.Count - fresh.Count, photos);
        }
    }

    /// <summary>
    /// Adds entries from a backup made by Export. Only adds: entries already in the journal (same ID) are
    /// skipped and nothing is deleted or overwritten. Photos from a .zip are encrypted into this journal.
    /// </summary>
    public async Task<ImportSummary> ImportAsync(StorageFile file)
    {
        var (entries, zip) = await OpenBackupAsync(file);
        using (zip)
        {
            int added = 0, skipped = 0, photos = 0;
            foreach (var entry in entries)
            {
                if (Find(entry.Id) is not null)
                {
                    skipped++;
                    continue;
                }

                var imported = new List<string>();
                foreach (var path in entry.PhotoPaths)
                {
                    // An entries-only (.json) backup doesn't carry photos, so there's nothing to bring in for those.
                    if (zip is null || BackupPhoto(zip, path) is not { } source) continue;

                    await using var input = source.Open();
                    using var buffer = new MemoryStream();
                    await input.CopyToAsync(buffer);
                    var target = Path.Combine(PhotosFolder, $"{Guid.NewGuid():N}{Path.GetExtension(path)}");
                    await File.WriteAllBytesAsync(target, DataProtection.Protect(buffer.ToArray()));
                    imported.Add(target);
                    photos++;
                }

                entry.PhotoPaths = imported;
                _entries.Add(entry);
                added++;
            }

            if (added > 0) await SaveAsync();
            return new ImportSummary(added, skipped, photos);
        }
    }

    /// <summary>Opens a .zip backup (entries.json + photos/) or an entries-only .json export.</summary>
    /// <exception cref="InvalidDataException">The file isn't a Journal backup.</exception>
    private static async Task<(List<JournalEntry> Entries, System.IO.Compression.ZipArchive? Zip)> OpenBackupAsync(StorageFile file)
    {
        var encrypted = file.FileType.Equals(BackupService.FileExtension, StringComparison.OrdinalIgnoreCase);
        Stream stream = await file.OpenStreamForReadAsync();
        if (encrypted)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            await stream.DisposeAsync();
            // Lets BackupKeyNeededException through: the caller asks for the recovery key and tries again.
            stream = new MemoryStream(BackupService.Decrypt(buffer.ToArray()));
        }

        try
        {
            List<JournalEntry>? entries;
            System.IO.Compression.ZipArchive? zip = null;
            if (encrypted || file.FileType.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
                var json = zip.GetEntry("entries.json") ?? throw new InvalidDataException("No entries.json in the .zip.");
                await using var jsonStream = json.Open();
                entries = await JsonSerializer.DeserializeAsync<List<JournalEntry>>(jsonStream, JsonOptions);
            }
            else
            {
                entries = await JsonSerializer.DeserializeAsync<List<JournalEntry>>(stream, JsonOptions);
                await stream.DisposeAsync();
            }

            // Entries without an ID (hand-edited files) get one, so they import as new.
            foreach (var entry in entries ?? []) if (entry.Id == Guid.Empty) entry.Id = Guid.NewGuid();
            return (entries ?? [], zip);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            await stream.DisposeAsync();
            throw new InvalidDataException("This file isn't a Journal backup.", ex);
        }
    }

    // Only files inside the backup's photos/ folder, and not absurdly large ones.
    private static System.IO.Compression.ZipArchiveEntry? BackupPhoto(System.IO.Compression.ZipArchive zip, string path) =>
        path.StartsWith("photos/", StringComparison.Ordinal) && !path.Contains("..", StringComparison.Ordinal)
        && zip.GetEntry(path) is { Length: > 0 and <= MaxPhotoBytes } photo
            ? photo
            : null;

    #endregion

    /// <summary>Entries only, as readable JSON (photo paths point inside Journal's own encrypted storage).</summary>
    public async Task ExportAsync(StorageFile target)
    {
        await using var stream = await target.OpenStreamForWriteAsync();
        stream.SetLength(0);
        await JsonSerializer.SerializeAsync(stream, _entries.OrderBy(e => e.CreatedAt), JsonOptions);
    }

    /// <summary>
    /// A complete, readable backup: entries.json plus every photo decrypted into photos/, with each entry's
    /// photo paths rewritten to point at its files inside the .zip. Returns how many photos were included.
    /// </summary>
    public async Task<int> ExportBackupAsync(StorageFile target)
    {
        await using var stream = await target.OpenStreamForWriteAsync();
        stream.SetLength(0);
        return await WriteBackupAsync(stream, _entries);
    }

    /// <summary>When the journal itself last changed on disk.</summary>
    public static DateTimeOffset LastChanged => File.GetLastWriteTime(EntriesFile);

    /// <summary>Writes the backup .zip to a stream, which is left open.</summary>
    public static async Task<int> WriteBackupAsync(Stream stream, IEnumerable<JournalEntry> source)
    {
        using var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<JournalEntry>();
        foreach (var entry in source.OrderBy(e => e.CreatedAt))
        {
            // Copy, so the journal's own photo paths aren't changed.
            var copy = JsonSerializer.Deserialize<JournalEntry>(JsonSerializer.Serialize(entry, JsonOptions), JsonOptions)!;
            copy.PhotoPaths = [];
            foreach (var path in entry.PhotoPaths.Where(File.Exists))
            {
                var name = Path.GetFileName(path);
                for (var i = 2; !names.Add(name); i++)
                {
                    name = $"{Path.GetFileNameWithoutExtension(path)}-{i}{Path.GetExtension(path)}";
                }

                var zipped = zip.CreateEntry($"photos/{name}", System.IO.Compression.CompressionLevel.NoCompression);
                await using (var photo = zipped.Open())
                {
                    await photo.WriteAsync(await ReadPhotoAsync(path));
                }

                copy.PhotoPaths.Add($"photos/{name}");
            }

            entries.Add(copy);
        }

        await using (var json = zip.CreateEntry("entries.json").Open())
        {
            await JsonSerializer.SerializeAsync(json, entries, JsonOptions);
        }

        await using (var readme = new StreamWriter(zip.CreateEntry("README.txt").Open()))
        {
            await readme.WriteAsync(
                $"Exported from Journal on {DateTime.Now:f}.\r\n\r\n" +
                "entries.json  all your entries (title, text, date, mood, favorite, reflection)\r\n" +
                "photos\\       the photos, named as listed in each entry's photoPaths\r\n\r\n" +
                "This backup is not encrypted. Keep it somewhere private.\r\n");
        }

        return names.Count;
    }

    private async Task SaveAsync()
    {
        await WriteAsync();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task WriteAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            // Write to a temp file then swap, so a crash mid-save never truncates the journal.
            var temp = EntriesFile + ".tmp";
            var json = JsonSerializer.SerializeToUtf8Bytes(_entries, JsonOptions);
            await File.WriteAllBytesAsync(temp, DataProtection.Protect(json));
            File.Move(temp, EntriesFile, overwrite: true);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
