using Microsoft.UI.Xaml;
using Nexus.App.Services;

namespace Nexus.App;

public partial class App : Application
{
    private MainWindow? _window;
    public static AppServices Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            Services?.LogCrash(args.Exception);
            args.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services = new AppServices();
        _window = new MainWindow();
        Services.AttachWindow(_window);
        _window.Activate();
        _ = Services.InitializeAsync();
    }
}
