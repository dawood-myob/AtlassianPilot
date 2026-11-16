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
        if (viewModel.JiraCreate is not null)
        {
            viewModel.JiraCreate.ConfirmCreation += preview => MessageBox.Show(this,
                $"Create exactly one Jira issue at {preview.SiteUrl}?\n\n{preview.Summary}\n\nOnly the payload you reviewed will be submitted. This cannot be undone from this app.",
                "Approve Jira issue creation", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            viewModel.JiraCreate.ConfirmDiscardDraft += () => MessageBox.Show(this, "Discard the current draft and chat?", "New Jira draft",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        }
        if (viewModel.JiraActions is not null)
            viewModel.JiraActions.ConfirmAction += preview => MessageBox.Show(this,
                $"{preview.Action} for Jira issue {preview.IssueKey}?\n\n{preview.Summary}\n\nReview the exact payload on screen. Jira will perform final permission and workflow validation. Do not approve if any field or destination is unexpected.",
                $"Approve Jira action: {preview.Action}", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        if (viewModel.ConfluenceAgent is not null)
        {
            viewModel.ConfluenceAgent.ConfirmSave += preview => MessageBox.Show(this,
                $"{preview.Action} the Confluence page at {preview.SiteUrl}?\n\n{preview.Title} (space {preview.SpaceKey})\n\nReview the exact payload and confirm this is the intended page. Existing formatting is replaced with plain paragraphs. This cannot be undone from this app.",
                "Approve Confluence page save", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            viewModel.ConfluenceAgent.ConfirmDiscardDraft += () => MessageBox.Show(this, "Discard the current Confluence conversation and page draft?", "New Confluence draft",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
            viewModel.ConfluenceAgent.ConfirmDiscardConversation += () => MessageBox.Show(this, "Discard the current Confluence conversation and start a new one?", "New Confluence conversation",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        }
        if (viewModel.History is not null)
        {
            viewModel.History.ConfirmDelete += conversation => MessageBox.Show(this, $"Delete the saved {conversation.Agent} conversation '{conversation.Title}'?", "Delete conversation", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            viewModel.History.ConfirmDeleteAll += () => MessageBox.Show(this, "Delete all saved Jira and Confluence conversation history?", "Delete all history", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        }
        if (viewModel.Activity is not null)
            viewModel.Activity.ConfirmClear += () => MessageBox.Show(this, "Clear the local activity audit log?", "Clear activity", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        Closing += (_, args) =>
        {
            if (viewModel.AnyBusy)
            {
                args.Cancel = true;
                return;
            }
            if (viewModel.JiraCreate?.HasUnsavedDraft == true && MessageBox.Show(this, "Your Create Jira draft is only in memory. Discard it and close?", "Unsaved Jira draft",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                args.Cancel = true;
                return;
            }
            if (viewModel.ConfluenceAgent?.HasUnsavedDraft == true && MessageBox.Show(this, "Your Confluence chat and draft are only in memory. Discard them and close?", "Unsaved Confluence draft",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
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