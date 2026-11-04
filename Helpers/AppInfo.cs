namespace WindowsJournal.Helpers;

public static class AppInfo
{
    /// <summary>True when running as the installed MSIX package (it has package identity).</summary>
    public static bool IsPackaged { get; } = GetIsPackaged();

    private static bool GetIsPackaged()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
