using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace WindowsJournal;

/// <summary>
/// Custom entry point so Journal runs as a single instance: launching it again, or clicking a reminder,
/// hands the activation to the window that's already open instead of starting a second copy (which could
/// also have two processes writing the same journal file). This is the Windows App SDK's documented pattern.
/// </summary>
public static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // The Widgets board starts a separate, windowless helper copy for widget content.
        if (args.Contains(Widgets.WidgetHost.ComServerArgument))
        {
            Widgets.WidgetHost.Run();
            return 0;
        }

        var main = AppInstance.FindOrRegisterForKey("main");
        if (!main.IsCurrent)
        {
            RedirectActivationTo(main);
            return 0;
        }

        main.Activated += (_, e) => App.OnRedirectedActivation(e);

        Application.Start(callbackParams =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    private static void RedirectActivationTo(AppInstance target)
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();

        // Redirecting is async but this STA thread must keep pumping COM while it waits.
        var done = CreateEvent(IntPtr.Zero, true, false, null);
        Task.Run(() =>
        {
            target.RedirectActivationToAsync(activation).AsTask().Wait();
            SetEvent(done);
        });
        _ = CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, [done], out _);

        // Windows only lets the process the user just launched bring a window forward, so do it from here.
        try
        {
            SetForegroundWindow(Process.GetProcessById((int)target.ProcessId).MainWindowHandle);
        }
        catch (ArgumentException)
        {
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateEvent(IntPtr attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(IntPtr handle);

    [LibraryImport("ole32.dll")]
    private static partial uint CoWaitForMultipleObjects(uint flags, uint timeout, uint count, IntPtr[] handles, out uint index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);
}
