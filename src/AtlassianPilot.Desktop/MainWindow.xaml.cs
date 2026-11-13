using System.Windows;
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
        Closing += (_, args) =>
        {
            if (viewModel.Busy)
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
}