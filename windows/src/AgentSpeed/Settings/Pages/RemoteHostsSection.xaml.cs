namespace AgentSpeed.Settings.Pages;

internal sealed partial class RemoteHostsSection : System.Windows.Controls.UserControl
{
    private void PasswordEnabledChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox { DataContext: RemoteHostRow { IsPassword: false } } box)
            box.Password = "";
    }
    private void PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox { IsLoaded: true, DataContext: RemoteHostRow row } box)
            row.Password = box.Password;
    }
    private void PasswordLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox { DataContext: RemoteHostRow row } box)
            box.Password = row.Password;
    }
    public RemoteHostsSection()
    {
        InitializeComponent();
        Unloaded += (_, _) => (DataContext as RemotePaneViewModel)?.CancelDeployment();
    }
}
