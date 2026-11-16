using System.IO;
using System.Net.Http;
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
            collection.AddSingleton<ISavedJiraSearchStore>(_ => new SqliteSavedJiraSearchStore(Path.Combine(directory, "workspace.db")));
            collection.AddSingleton<SqliteAgentRecordStore>(_ => new SqliteAgentRecordStore(Path.Combine(directory, "workspace.db")));
            collection.AddSingleton<IConversationHistoryStore>(provider => provider.GetRequiredService<SqliteAgentRecordStore>());
            collection.AddSingleton<IActivityLogStore>(provider => provider.GetRequiredService<SqliteAgentRecordStore>());
            collection.AddSingleton(_ => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan });
            collection.AddSingleton<IAtlassianConnectionTester, AtlassianConnectionTester>();
            collection.AddSingleton<JiraIssueSearchService>();
            collection.AddSingleton<IJiraIssueSearchService>(provider => provider.GetRequiredService<JiraIssueSearchService>());
            collection.AddSingleton<IJiraIssueDetailsService>(provider => provider.GetRequiredService<JiraIssueSearchService>());
            collection.AddSingleton<IJiraCreateMetadataService>(provider => provider.GetRequiredService<JiraIssueSearchService>());
            collection.AddSingleton<IJiraIssueCreationService>(provider => provider.GetRequiredService<JiraIssueSearchService>());
            collection.AddSingleton<IJiraIssueActionService, JiraIssueActionService>();
            collection.AddSingleton<JiraActionsViewModel>();
            collection.AddSingleton<IJiraDraftAgent, CopilotJiraDraftAgent>();
            collection.AddSingleton<JiraCreateViewModel>();
            collection.AddSingleton<IConfluenceWorkspaceService, ConfluenceWorkspaceService>();
            collection.AddSingleton<IConfluenceAgent, CopilotConfluenceAgent>();
            collection.AddSingleton<ConfluenceAgentViewModel>();
            collection.AddSingleton<AgentHistoryViewModel>();
            collection.AddSingleton<ActivityLogViewModel>();
            collection.AddSingleton<ICopilotRuntime>(_ => new CopilotRuntime(Path.Combine(directory, "copilot")));
            collection.AddSingleton<ICopilotConnection, CopilotConnection>();
            collection.AddSingleton<IJiraAgent, CopilotJiraAgent>();
            collection.AddSingleton<JiraAgentViewModel>();
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
        catch (IntegrationException exception)
        {
            MessageBox.Show(exception.Message, "Startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            splash.Close();
            Shutdown(1);
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
        if (services is not null)
        {
            try { Task.Run(async () => await services.DisposeAsync()).GetAwaiter().GetResult(); }
            catch (Exception)
            {
                MessageBox.Show("The Copilot runtime did not shut down cleanly. No credentials have been displayed or logged.",
                    "Shutdown warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        base.OnExit(e);
    }
}

