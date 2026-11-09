using FuWindows.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace FuWindows;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN): with arguments FuWindows runs headless
/// (see <see cref="Cli"/>); without, it starts the WinUI app as usual.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Documentation screenshots: open the UI, capture each page to PNG, exit.
        if (args.Length == 2 && args[0].Equals("--screenshots", StringComparison.OrdinalIgnoreCase))
            App.ScreenshotFolder = Path.GetFullPath(args[1]);
        else if (args.Length > 0)
            return Cli.Run(args);

        Application.Start(_p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
