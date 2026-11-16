using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace AtlassianPilot.Desktop.Views;

public partial class ConfluenceAgentView : UserControl
{
    public ConfluenceAgentView() => InitializeComponent();

    private void OpenPage(object sender, RoutedEventArgs e)
    {
        try
        {
            if (((Button)sender).Tag is not Uri uri || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
                throw new InvalidOperationException("Invalid Confluence page link.");
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show("The Confluence page link could not be opened. Open Confluence and search for the page title.",
                "Open Confluence page", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
