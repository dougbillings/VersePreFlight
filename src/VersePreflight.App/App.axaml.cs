using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VersePreflight.App.ViewModels;
using VersePreflight.App.Views;
using VersePreflight.Core.Config;
using VersePreflight.Core.Logging;
using VersePreflight.Core.Platforms;

namespace VersePreflight.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var platform = Platform.Current;
            var machine = Environment.MachineName;
            var store = ConfigStore.CreateDefault(AppContext.BaseDirectory, platform.ConfigDirectory, machine);
            var log = new PreflightLog(Path.Combine(store.ConfigDirectory, $"log.{machine}.txt"));

            var window = new MainWindow();
            var viewModel = new MainViewModel(platform, store, log, window);
            window.DataContext = viewModel;
            window.Opened += async (_, _) => await viewModel.InitializeAsync();
            desktop.Exit += (_, _) => viewModel.Dispose();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
