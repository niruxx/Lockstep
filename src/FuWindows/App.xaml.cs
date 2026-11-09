using Microsoft.UI.Xaml;

namespace FuWindows;

public partial class App : Application
{
    private Window? _window;

    /// <summary>Set by <c>--screenshots &lt;folder&gt;</c>: capture every page for the README, then exit.</summary>
    public static string? ScreenshotFolder { get; set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FuWindows");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTime.Now:u}] {e.Exception}\r\n\r\n");
            }
            catch { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
