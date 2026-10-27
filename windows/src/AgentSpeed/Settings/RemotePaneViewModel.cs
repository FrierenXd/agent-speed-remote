using System.Collections.ObjectModel;
using AgentSpeed.Controls;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Services;

namespace AgentSpeed.Settings;

internal sealed class RemoteHostRow : ObservableObject
{
    private string _executable = "agentspeed-collector";
    private string _shell = "posix";
    private bool _enabled = true;
    private string _authentication = "key";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Server";
    public string Destination { get; set; } = "";
    public string Executable { get => _executable; set => Set(ref _executable, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string Shell { get => _shell; set => Set(ref _shell, value); }
    public string Password { get; set; } = "";
    public string Authentication
    {
        get => _authentication;
        set { if (Set(ref _authentication, value)) OnPropertyChanged(nameof(IsPassword)); }
    }
    public bool IsPassword => Authentication == "password";
}
internal sealed record RemoteChoice(string Id, string Name);

internal sealed class RemotePaneViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly Localizer _loc;
    private readonly Func<IReadOnlyDictionary<string, string>?> _statuses;
    private bool _syncing;
    private string _error = "";
    private string _status = "";
    private string _selection;
    private CancellationTokenSource? _deployment;
    private bool _busy;
    private string _deploymentStatus = "";
    public bool CanEdit => !_busy;
    public string DeploymentStatus { get => _deploymentStatus; private set => Set(ref _deploymentStatus, value); }
    public ObservableCollection<RemoteHostRow> Hosts { get; } = [];
    public ObservableCollection<RemoteChoice> Choices { get; } = [];
    public string Error { get => _error; set => Set(ref _error, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Selection
    {
        get => _selection;
        set { if (!_syncing && !string.IsNullOrEmpty(value) && Choices.Any(c => c.Id == value) && Set(ref _selection, value)) _settings.Update(s => s with { Remote = s.Remote with { Selection = value } }); }
    }
    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand DeployCommand { get; }
    public RelayCommand CancelDeployCommand { get; }
    public RemotePaneViewModel(SettingsService settings, Localizer loc, Func<IReadOnlyDictionary<string, string>?> statuses)
    {
        _settings = settings; _loc = loc; _statuses = statuses; _selection = settings.Current.Remote.Selection;
        foreach (var h in settings.Current.Remote.Hosts) Hosts.Add(new() { Id = h.Id, Name = h.Name, Destination = h.Destination, Executable = h.Executable, Enabled = h.Enabled, Shell = h.Shell ?? "posix", Authentication = h.Authentication ?? "key", Password = RemotePasswords.Get(h) });
        AddCommand = new RelayCommand(() => { if (Hosts.Count < 16) Hosts.Add(new()); }, () => !_busy);
        RemoveCommand = new RelayCommand(row => { if (row is RemoteHostRow host) Hosts.Remove(host); }, _ => !_busy);
        ApplyCommand = new RelayCommand(Apply, () => !_busy);
        DeployCommand = new RelayCommand(async row => { if (row is RemoteHostRow host) await Deploy(host); }, _ => !_busy);
        CancelDeployCommand = new RelayCommand(CancelDeployment, () => _busy);
        settings.Changed += (_, args) => { if (!args.Current.NetworkEnabled) CancelDeployment(); };
        UpdateChoices(); RefreshStatus();
        loc.LanguageChanged += (_, _) => { UpdateChoices(); RefreshStatus(); };
    }
    public void CancelDeployment() => _deployment?.Cancel();
    private async Task Deploy(RemoteHostRow row)
    {
        if (_busy) return;
        if (!_settings.Current.NetworkEnabled) { Error = _loc["remote.deploy.networkOff"]; return; }
        var host = ToHost(row);
        var validation = new RemoteSettings { Hosts = Hosts.Select(ToHost).ToArray() };
        if (!validation.IsValid) { Error = _loc["remote.invalid"]; return; }
        if (!SavePasswords()) return;
        _busy = true; Error = ""; OnPropertyChanged(nameof(CanEdit));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        using var stop = new CancellationTokenSource(); _deployment = stop;
        try
        {
            var assembly = typeof(RemotePaneViewModel).Assembly;
            var deployer = new RemoteDeployment(file => assembly.GetManifestResourceStream("AgentSpeed.Collectors." + file));
            var installed = await deployer.DeployAsync(host, new Progress<string>(phase => DeploymentStatus = _loc["remote.deploy." + phase]), stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            row.Executable = installed.Executable; row.Shell = installed.Shell ?? "posix"; row.Enabled = true;
            _selection = row.Id;
            Apply();
            DeploymentStatus = _loc["remote.deploy.done"];
        }
        catch (OperationCanceledException) { DeploymentStatus = _loc["remote.deploy.cancelled"]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
        { Error = _loc["remote.deploy.failed"] + " " + ex.Message; DeploymentStatus = ""; }
        finally
        {
            _deployment = null; _busy = false; OnPropertyChanged(nameof(CanEdit));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
    private void Apply()
    {
        var selection = Hosts.Any(h => h.Id == _selection) || _selection is "local" or "all" ? _selection : "local";
        var remote = new RemoteSettings { Selection = selection, Hosts = Hosts.Select(ToHost).ToArray() };
        if (!remote.IsValid) { Error = _loc["remote.invalid"]; return; }
        if (!SavePasswords()) return;
        Error = ""; _selection = selection;
        _settings.Update(s => s with { Remote = remote }); UpdateChoices();
        OnPropertyChanged(nameof(Selection)); RefreshStatus();
    }
    private static RemoteHost ToHost(RemoteHostRow h) => new(h.Id, h.Name.Trim(), h.Destination.Trim(), h.Executable.Trim(), h.Enabled, h.Shell, h.Authentication);
    private bool SavePasswords()
    {
        if (Hosts.Any(h => h.IsPassword && (h.Password.Length > 1024 || h.Password.Any(c => c is '\0' or '\r' or '\n'))))
        { Error = _loc["remote.password.invalid"]; return false; }
        RemotePasswords.Retain(Hosts.Select(ToHost));
        foreach (var row in Hosts)
        {
            if (row.IsPassword) RemotePasswords.Set(ToHost(row), row.Password);
            else row.Password = "";
        }
        return true;
    }
    private void UpdateChoices()
    {
        _syncing = true;
        Choices.Clear(); Choices.Add(new("local", _loc["remote.local"])); Choices.Add(new("all", _loc["remote.all"]));
        foreach (var h in _settings.Current.Remote.Hosts) Choices.Add(new(h.Id, h.Name));
        OnPropertyChanged(nameof(Selection));
        _syncing = false;
    }
    public void RefreshStatus()
    {
        var statuses = _statuses();
        Status = string.Join("\n", _settings.Current.Remote.Hosts.Select(h => h.Name + ": " +
            _loc["remote.status." + (statuses?.GetValueOrDefault(h.Id) ?? "disabled")]));
    }
}
