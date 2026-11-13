using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AtlassianPilot.Desktop.ViewModels;

namespace AtlassianPilot.Desktop;

public partial class MainWindow : Window
{
    private readonly ShellViewModel viewModel;
    private bool clearingToken;

    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        viewModel.ThemeChanged += ApplyTheme;
        viewModel.ClearCredentialInput += ClearToken;
        viewModel.ConfirmDiscardChanges += () => MessageBox.Show("Discard unsaved settings and load the selected integration?", "Unsaved settings",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        Closing += (_, args) =>
        {
            if (viewModel.AnyBusy)
            {
                args.Cancel = true;
                return;
            }
            if (viewModel.HasUnsavedChanges && MessageBox.Show("Discard unsaved settings and close?", "Unsaved settings",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                args.Cancel = true;
        };
    }

    private void ApplyTheme(string theme)
    {
        Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"Themes/{theme}.xaml", UriKind.Relative)
        };
    }

    private void ClearToken()
    {
        clearingToken = true;
        try { TokenInput.Clear(); }
        finally { clearingToken = false; }
    }

    private void TokenChanged(object sender, RoutedEventArgs e)
    {
        if (!clearingToken) viewModel?.CredentialEdited();
    }

    private async void SaveSettings(object sender, RoutedEventArgs e)
    {
        try { await viewModel.SaveAsync(TokenInput.Password.Length == 0 ? null : TokenInput.Password); }
        catch (Exception exception) { viewModel.ReportError(exception); }
    }

    private void DeleteToken(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Delete the API token stored for this integration? You will need to re-enter it to connect.",
            "Delete credential", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            viewModel.DeleteCredentialCommand.Execute(null);
    }

    private async void IntegrationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (viewModel is null || ((ComboBox)sender).SelectedItem is not AtlassianPilot.Core.IntegrationKind kind) return;
        try { await viewModel.ChangeIntegrationAsync(kind); }
        catch (Exception exception) { viewModel.ReportError(exception); }
    }

    private void ChooseModel(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is AtlassianPilot.Core.CopilotModel model)
            viewModel.UseModelCommand.Execute(model);
    }

    private void OpenVerificationPage(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Uri.TryCreate(viewModel.VerificationUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com")
                throw new AtlassianPilot.Core.IntegrationException("No active GitHub verification page. Start sign-in first.");
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) { viewModel.ReportError(exception); }
    }

    private void DeleteSavedSearch(object sender, RoutedEventArgs e)
    {
        var agent = viewModel.JiraAgent;
        if (agent?.SelectedSearch is not { IsExample: false } choice) return;
        if (MessageBox.Show($"Delete the saved search '{choice.Name}'? This will not change Jira issues.",
            "Delete saved search", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            agent.DeleteSearchCommand.Execute(null);
    }

    private void OpenIssue(object sender, RoutedEventArgs e)
    {
        try
        {
            if (((Button)sender).Tag is not Uri uri || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
                throw new AtlassianPilot.Core.IntegrationException("The issue link is invalid.");
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) { viewModel.ReportError(exception); }
    }

    private void SignOut(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Sign out of this application's Copilot runtime? This does not delete Atlassian tokens.",
            "Sign out", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            viewModel.SignOutCommand.Execute(null);
    }
}