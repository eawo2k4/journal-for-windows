using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;

namespace WindowsJournal.Widgets;

/// <summary>
/// Helper mode: the Widgets board starts Journal with -RegisterProcessAsComServer to get widget content.
/// No window opens; the process serves the widget provider over COM and exits once no Journal widgets remain.
/// (Follows the Windows App SDK widget provider pattern.)
/// </summary>
public static partial class WidgetHost
{
    public const string ComServerArgument = "-RegisterProcessAsComServer";

    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromMinutes(1);

    public static void Run()
    {
        // COM calls must not land on a UI (STA) thread that's blocked waiting, so serve from an MTA thread.
        var thread = new Thread(Serve);
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
    }

    private static void Serve()
    {
        try
        {
            using var done = new ManualResetEvent(false);
            JournalWidgetProvider.LastWidgetRemoved += (_, _) => done.Set();

            const uint LocalServer = 0x4, MultipleUse = 0x1;
            var hr = CoRegisterClassObject(Guid.Parse(JournalWidgetProvider.ClassId), new ProviderFactory(), LocalServer, MultipleUse, out var cookie);
            Marshal.ThrowExceptionForHR(hr);

            // Windows can start this helper when no Journal widget is pinned, and then no removal ever tells it to
            // leave. If it's needed again, Windows starts it again.
            using var idleCheck = new Timer(_ =>
            {
                try
                {
                    if (JournalWidgetProvider.PinnedWidgets().Length == 0) done.Set();
                }
                catch (Exception ex) when (ex is COMException or ObjectDisposedException)
                {
                }
            }, null, IdleCheckInterval, IdleCheckInterval);

            done.WaitOne();
            _ = CoRevokeClassObject(cookie);
        }
        catch (Exception ex)
        {
            Services.AIService.LogError(ex);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(
        [MarshalAs(UnmanagedType.LPStruct)] Guid classId,
        [MarshalAs(UnmanagedType.IUnknown)] object factory,
        uint context,
        uint flags,
        out uint cookie);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint cookie);

    [ComImport]
    [Guid("00000001-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(IntPtr outer, ref Guid interfaceId, out IntPtr instance);

        [PreserveSig]
        int LockServer([MarshalAs(UnmanagedType.Bool)] bool locked);
    }

    [ComVisible(true)]
    private sealed class ProviderFactory : IClassFactory
    {
        private const int NoAggregation = unchecked((int)0x80040110);

        public int CreateInstance(IntPtr outer, ref Guid interfaceId, out IntPtr instance)
        {
            instance = IntPtr.Zero;
            if (outer != IntPtr.Zero) return NoAggregation;

            // A WinRT object for the provider; hand back whichever interface the caller asked for.
            var provider = WinRT.MarshalInspectable<IWidgetProvider>.FromManaged(new JournalWidgetProvider());
            try
            {
                return Marshal.QueryInterface(provider, in interfaceId, out instance);
            }
            finally
            {
                Marshal.Release(provider);
            }
        }

        public int LockServer(bool locked) => 0;
    }
}
