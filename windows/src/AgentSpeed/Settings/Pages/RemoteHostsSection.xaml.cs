namespace AgentSpeed.Settings.Pages;

internal sealed partial class RemoteHostsSection : System.Windows.Controls.UserControl
{
    public RemoteHostsSection()
    {
        InitializeComponent();
        Unloaded += (_, _) => (DataContext as RemotePaneViewModel)?.CancelDeployment();
    }
}
