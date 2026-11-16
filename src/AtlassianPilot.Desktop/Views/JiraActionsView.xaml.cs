using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AtlassianPilot.Core;
using AtlassianPilot.Desktop.ViewModels;

namespace AtlassianPilot.Desktop.Views;

public partial class JiraActionsView : UserControl
{
    public JiraActionsView() => InitializeComponent();

    private void OpenIssue(object sender, RoutedEventArgs e)
    {
        try
        {
            if (((Button)sender).Tag is not Uri uri || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
                throw new IntegrationException("The issue link is invalid.");
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            if (DataContext is JiraActionsViewModel viewModel)
                viewModel.ReportError(exception);
        }
    }
}
