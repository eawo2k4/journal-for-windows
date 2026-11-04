using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Storage;

namespace WindowsJournal.Services;

/// <summary>
/// What another app sent through Share, waiting to become a new entry. It's taken once by the editor, so going Back
/// to that page later can't create the entry a second time.
/// </summary>
public sealed record SharedContent(string Title, string Text, IReadOnlyList<string> PhotoPaths)
{
    private static readonly HashSet<string> PhotoTypes = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".heic", ".webp", ".gif" };

    public static SharedContent? Pending { get; set; }

    public static SharedContent? TakePending()
    {
        var content = Pending;
        Pending = null;
        return content;
    }

    /// <summary>
    /// Copies everything out of the share, encrypting photos into the journal. The sending app can let go of
    /// its data as soon as the share is reported complete, so this has to happen first.
    /// </summary>
    public static async Task<SharedContent?> ReadAsync(ShareOperation share)
    {
        var data = share.Data;
        var title = data.Properties.Title ?? "";
        var text = new List<string>();
        var photos = new List<string>();

        if (data.Contains(StandardDataFormats.Text)) text.Add(await data.GetTextAsync());
        if (data.Contains(StandardDataFormats.WebLink)) text.Add((await data.GetWebLinkAsync()).ToString());

        if (data.Contains(StandardDataFormats.StorageItems))
        {
            foreach (var item in await data.GetStorageItemsAsync())
            {
                if (item is StorageFile file && PhotoTypes.Contains(file.FileType))
                {
                    photos.Add(await JournalStore.ImportPhotoAsync(file));
                }

                // Sharing a file titles the share with its name, which isn't much of a title for an entry.
                if (title.Equals(item.Name, StringComparison.OrdinalIgnoreCase)) title = "";
            }
        }
        else if (data.Contains(StandardDataFormats.Bitmap))
        {
            var reference = await data.GetBitmapAsync();
            using var source = await reference.OpenReadAsync();
            await using var stream = source.AsStreamForRead();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            photos.Add(await JournalStore.ImportPhotoAsync(buffer.ToArray(), ".png"));
        }

        // Some apps send the same link as both text and a link.
        var body = string.Join("\n\n", text.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct());
        return body.Length == 0 && photos.Count == 0 ? null : new SharedContent(title.Trim(), body, photos);
    }
}
