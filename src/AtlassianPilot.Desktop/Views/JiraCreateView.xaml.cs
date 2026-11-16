using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace AtlassianPilot.Desktop.Views;

public partial class JiraCreateView : UserControl
{
    public JiraCreateView() => InitializeComponent();

    private void OpenCreatedIssue(object sender, RoutedEventArgs e)
    {
        try
        {
            if (((Button)sender).Tag is not Uri uri || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
                throw new InvalidOperationException("Invalid issue link.");
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show("The issue link could not be opened. Open Jira in your browser and look up the issue key shown here.",
                "Open Jira issue", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
