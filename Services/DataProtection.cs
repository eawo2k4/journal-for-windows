using System.Security.Cryptography;
using System.Text;

namespace WindowsJournal.Services;

/// <summary>
/// Encrypts journal data at rest with DPAPI, keyed to the signed-in Windows account.
/// Other accounts on the PC, and anyone who copies the files off it, can't read them.
/// (Like any per-user encryption, it can't stop software that's already running as you.)
/// </summary>
public static class DataProtection
{
    // Files start with this marker so plain files from older versions can still be read and migrated.
    private static readonly byte[] Magic = "WJE1"u8.ToArray();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WindowsJournal/v1");

    public static bool IsProtected(ReadOnlySpan<byte> data) => data.StartsWith(Magic);

    public static byte[] Protect(byte[] plain) =>
        [.. Magic, .. ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)];

    /// <summary>Returns the plain bytes; data without the marker is returned as-is.</summary>
    public static byte[] Unprotect(byte[] data) =>
        IsProtected(data)
            ? ProtectedData.Unprotect(data[Magic.Length..], Entropy, DataProtectionScope.CurrentUser)
            : data;

    public static async Task<byte[]> ReadFileAsync(string path) => Unprotect(await File.ReadAllBytesAsync(path));

    /// <summary>Encrypts a file in place if it isn't already. Used to migrate older, unencrypted data.</summary>
    public static async Task ProtectFileInPlaceAsync(string path)
    {
        var data = await File.ReadAllBytesAsync(path);
        if (IsProtected(data)) return;

        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, Protect(data));
        File.Move(temp, path, overwrite: true);
    }
}
