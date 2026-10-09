using Avalonia;
using Velopack;

namespace VersePreflight.App;

internal sealed class Program
{
    // Don't use Avalonia or SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack install/update/uninstall hooks, which may exit the process
        VelopackApp.Build().Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
