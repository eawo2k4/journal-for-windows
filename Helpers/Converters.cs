using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using WindowsJournal.Services;

namespace WindowsJournal.Helpers;

/// <summary>Static functions for x:Bind.</summary>
public static class Converters
{
    /// <summary>Photos are encrypted on disk, so decrypt into memory instead of pointing the image at the file.</summary>
    public static BitmapImage? ToImage(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        var image = new BitmapImage { DecodePixelWidth = 320 };
        _ = LoadAsync(image, path);
        return image;
    }

    private static async Task LoadAsync(BitmapImage image, string path)
    {
        try
        {
            var bytes = await JournalStore.ReadPhotoAsync(path);
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            await image.SetSourceAsync(stream);
        }
        catch (Exception)
        {
            // A missing or unreadable photo just shows as blank.
        }
    }
}
