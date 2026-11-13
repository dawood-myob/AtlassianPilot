using System.IO;
using System.Windows;
using AtlassianPilot.Core;
using AtlassianPilot.Desktop.ViewModels;
using AtlassianPilot.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AtlassianPilot.Desktop;

public partial class App : Application
{
    private ServiceProvider? services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var splash = new SplashWindow();
        splash.Show();
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AtlassianPilot");
            var collection = new ServiceCollection();
            collection.AddSingleton<ISecretProtector, DpapiSecretProtector>();
            collection.AddSingleton(provider => new SqliteSettingsStore(Path.Combine(directory, "workspace.db"), provider.GetRequiredService<ISecretProtector>()));
            collection.AddSingleton<ISettingsStore>(provider => provider.GetRequiredService<SqliteSettingsStore>());
            collection.AddSingleton<ICredentialStore>(provider => provider.GetRequiredService<SqliteSettingsStore>());
            collection.AddSingleton<ShellViewModel>();
            collection.AddSingleton<MainWindow>();
            services = collection.BuildServiceProvider();
            var window = services.GetRequiredService<MainWindow>();
            await services.GetRequiredService<ShellViewModel>().InitializeAsync();
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            splash.Close();
        }
        catch (Exception)
        {
            MessageBox.Show("AtlassianPilot could not open its local workspace. Check that your Windows user can write to %LOCALAPPDATA%\\AtlassianPilot. No credentials have been displayed or logged.",
                "Startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            splash.Close();
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        services?.Dispose();
        base.OnExit(e);
    }
}

