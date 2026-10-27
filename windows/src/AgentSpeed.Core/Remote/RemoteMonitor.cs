using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AgentSpeed.Core.Remote;

/// <summary>Composes local and remote feeds without feeding remote data into local history, traffic or reporting.</summary>
public sealed class RemoteMonitor : IDisposable
{
    internal delegate Task Transport(RemoteHost host, string timeZone, Action<RemoteFrame> receive, Action<string> status,
        CancellationToken cancellationToken, IReadOnlySet<SourceKind> enabled);

    private sealed class HostState(RemoteHost host, CancellationTokenSource stop, string timeZone, string cachePath)
    {
        public RemoteHost Host = host;
        public CancellationTokenSource Stop = stop;
        public SpeedSnapshot Snapshot = SpeedSnapshot.Empty;
        public DailyUsage[] Days = [];
        public long Received;
        public string Status = "connecting";
        public string? Generation;
        public readonly Dictionary<int, DailyUsage[]> Parts = [];
        public readonly Dictionary<int, int> PartBytes = [];
        public int PartCount;
        public string TimeZone = timeZone;
        public string CachePath = cachePath;
        public bool HistoryLoaded;
        public int CacheRevision;
        public long PasswordRevision = RemotePasswords.RevisionFor(host);
    }

    private readonly object _gate = new();
    private readonly object _cacheGate = new();
    private Task _cacheWork = Task.CompletedTask;
    private readonly SpeedHub _local;
    private readonly HistoryHub _localHistory;
    private readonly SpeedHub _output;
    private readonly HistoryHub _history;
    private readonly string _cacheDirectory;
    private readonly Transport _transport;
    private readonly Dictionary<string, HostState> _hosts = new(StringComparer.Ordinal);
    private readonly Timer _timer;
    private RemoteSettings _settings = RemoteSettings.Default;
    private IReadOnlySet<SourceKind> _enabled = Enum.GetValues<SourceKind>().ToHashSet();
    private Rate _peak;
    private SourceSet[] _subsets = [];
    private readonly Dictionary<SourceSet, double> _subsetPeaks = [];

    public void SetSubsets(IEnumerable<SourceSet> sets)
    {
        lock (_gate) { if (_disposed) return; _subsets = sets.Distinct().ToArray(); Publish(); }
    }
    private DateOnly _peakDay;
    private string _timeZone = TimeZoneInfo.Local.Id;
    private bool _disposed;
    private bool _networkEnabled;
    private long _passwordRevision = RemotePasswords.Revision;
    private SpeedSnapshot _localSnapshot = SpeedSnapshot.Empty;

    public RemoteMonitor(SpeedHub local, HistoryHub localHistory, SpeedHub output, HistoryHub history, string cacheDirectory)
        : this(local, localHistory, output, history, cacheDirectory, RemoteConnection.Run) { }

    internal RemoteMonitor(SpeedHub local, HistoryHub localHistory, SpeedHub output, HistoryHub history, string cacheDirectory,
        Transport transport)
    {
        _local = local; _localHistory = localHistory; _output = output; _history = history; _cacheDirectory = cacheDirectory;
        _transport = transport;
        local.LivePublished += OnLocal;
        local.EventRaised += OnEvent;
        localHistory.Changed += OnHistory;
        history.RefreshHandler = Refresh;
        history.ClearHandler = Clear;
        _timer = new Timer(_ => Tick(), null, 1000, 1000);
    }

    public IReadOnlyDictionary<string, string> Statuses
    {
        get { lock (_gate) return _hosts.ToDictionary(p => p.Key, p => p.Value.Status); }
    }

    public void Update(RemoteSettings settings, bool networkEnabled, IReadOnlySet<SourceKind> enabled)
    {
        if (!settings.IsValid) throw new ArgumentException("Invalid remote settings.");
        lock (_gate)
        {
            if (_disposed) return;
            if (_settings.Selection != settings.Selection || !_enabled.SetEquals(enabled) || !_settings.Hosts.SequenceEqual(settings.Hosts)) { _peak = Rate.Zero; _subsetPeaks.Clear(); }
            var restart = _timeZone != TimeZoneInfo.Local.Id || _networkEnabled != networkEnabled || !_enabled.SetEquals(enabled);
            _passwordRevision = RemotePasswords.Revision;
            _networkEnabled = networkEnabled;
            _timeZone = TimeZoneInfo.Local.Id;
            _enabled = enabled;
            _settings = settings;
            foreach (var (id, state) in _hosts.ToArray())
                if (restart || state.PasswordRevision != RemotePasswords.RevisionFor(state.Host) || !settings.Hosts.Any(h => h.Enabled && h == state.Host))
                {
                    Interlocked.Increment(ref state.CacheRevision); state.Stop.Cancel();
                    _hosts.Remove(id);
                }
            foreach (var host in settings.Hosts.Where(h => h.Enabled))
            {
                if (_hosts.ContainsKey(host.Id)) continue;
                var zone = _timeZone;
                var state = new HostState(host, new CancellationTokenSource(), zone, CachePath(host, zone));
                _hosts.Add(host.Id, state);
                _ = Task.Run(() => { LoadCache(state, 0); lock (_gate) if (IsCurrent(state)) PublishHistory(); });
                if (networkEnabled)
                    _ = Task.Run(() => _transport(host, zone, frame => Receive(state, frame),
                        status => { lock (_gate) if (IsCurrent(state)) state.Status = status; }, state.Stop.Token, enabled));
                else state.Status = "disabled";
            }
            Publish(); PublishHistory();
        }
    }

    private bool IsCurrent(HostState state) => !_disposed && _hosts.TryGetValue(state.Host.Id, out var current) && ReferenceEquals(state, current);
    private void Receive(HostState state, RemoteFrame frame)
    {
        lock (_gate)
        {
            if (!IsCurrent(state)) return;
            if (frame.Snapshot is { } snapshot)
            {
                state.Snapshot = snapshot.ToSnapshot(frame.TimestampMs, state.Host.Id, state.Host.Name);
                state.Received = Stopwatch.GetTimestamp();
                Publish();
            }
            if (frame.Days is not { } days) return;
            if (frame.Generation != state.Generation)
            {
                state.Generation = frame.Generation; state.Parts.Clear(); state.PartBytes.Clear(); state.PartCount = frame.Parts;
            }
            if (frame.Parts != state.PartCount) return;
            var bytes = frame.WireBytes == 0 ? Encoding.UTF8.GetByteCount(frame.Encode()) : frame.WireBytes;
            if (state.PartBytes.Values.Sum() - state.PartBytes.GetValueOrDefault(frame.Part) + bytes > RemoteFrame.MaxHistoryBytes
                || state.Parts.Values.Sum(p => p.Length) - state.Parts.GetValueOrDefault(frame.Part, []).Length + days.Length > 100_000)
            {
                state.Parts.Clear(); state.PartBytes.Clear();
                throw new System.Text.Json.JsonException("Remote history exceeded transfer limits.");
            }
            state.Parts[frame.Part] = days;
            state.PartBytes[frame.Part] = bytes;
            if (state.Parts.Count != state.PartCount) return;
            var complete = state.Parts.OrderBy(p => p.Key).SelectMany(p => p.Value).ToArray();
            if (complete.Length > 100_000 || complete.Select(d => (d.Day, d.Source)).Distinct().Count() != complete.Length) return;
            state.Days = complete;
            state.HistoryLoaded = true;
            state.Parts.Clear();
            state.PartBytes.Clear();
            var revision = Interlocked.Increment(ref state.CacheRevision);
            QueueCache(() => SaveCache(state, complete, revision));
            PublishHistory();
        }
    }

    private IEnumerable<HostState> SelectedHosts() => _hosts.Values.Where(s => _settings.Selection == "all" || _settings.Selection == s.Host.Id);
    private bool IncludeLocal => _settings.Selection is "local" or "all";
    private void OnLocal(object? sender, SpeedSnapshot snapshot) { lock (_gate) if (!_disposed) { _localSnapshot = snapshot; Publish(); } }
    private void OnEvent(object? sender, SpeedEvent e)
    {
        lock (_gate) if (!_disposed && _settings.Selection == "local") _output.PublishLive(_localSnapshot, [e]);
    }
    private void OnHistory(object? sender, EventArgs e) { lock (_gate) if (!_disposed) PublishHistory(); }
    private void Tick()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_timeZone != TimeZoneInfo.Local.Id || _passwordRevision != RemotePasswords.Revision) Update(_settings, _networkEnabled, _enabled);
            foreach (var state in _hosts.Values)
                if (state.Received != 0 && Stopwatch.GetElapsedTime(state.Received).TotalSeconds > 5 && state.Status == "connected") state.Status = "stale";
            if (_settings.Selection != "local") Publish();
        }
    }

    private void Publish()
    {
        if (_settings.Selection == "local") { _output.PublishLive(_localSnapshot); return; }
        var now = DateTimeOffset.Now;
        var day = DateOnly.FromDateTime(now.Date);
        if (day != _peakDay) { _peakDay = day; _peak = Rate.Zero; _subsetPeaks.Clear(); }
        var snapshots = new List<SpeedSnapshot>();
        if (IncludeLocal) snapshots.Add(_localSnapshot);
        foreach (var state in SelectedHosts())
        {
            var snapshot = state.Snapshot;
            if (state.Received == 0 || Stopwatch.GetElapsedTime(state.Received).TotalSeconds > 5 || state.Status != "connected")
                snapshot = RemoteAggregation.Idle(snapshot, now);
            // The receiver's source switches also apply to remote hosts.
            snapshot = Filter(snapshot);
            snapshots.Add(snapshot);
        }
        var peak = RemoteAggregation.Peak(snapshots.SelectMany(s => s.History).Where(p => p.Time.LocalDateTime.Date == now.Date));
        _peak = new(Math.Max(_peak.Output, peak.Output), Math.Max(_peak.Total, peak.Total));
        var result = RemoteAggregation.Snapshot(snapshots, now, _peak);
        if (snapshots.Count > 1 || !IncludeLocal)
        {
            foreach (var set in _subsets)
            {
                var measured = snapshots.SelectMany(s => s.SourceHistory.Where(kv => set.Contains(kv.Key)).SelectMany(kv => kv.Value))
                    .Where(p => p.Time.LocalDateTime.Date == now.Date);
                _subsetPeaks[set] = Math.Max(_subsetPeaks.GetValueOrDefault(set), RemoteAggregation.Peak(measured).Output);
            }
            result = result with { SubsetPeaks = new Dictionary<SourceSet, double>(_subsetPeaks) };
        }
        var rows = SelectedDays().Where(d => d.Day == now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();
        var measuredMs = rows.Sum(d => d.MeasuredMs);
        if (measuredMs > 0) result = result with { AverageToday = rows.Sum(d => d.MeasuredOutput) / (measuredMs / 1000.0) };
        _output.PublishLive(result);
    }

    private SpeedSnapshot Filter(SpeedSnapshot s)
    {
        var sources = s.Sources.Where(x => _enabled.Contains(x.Kind)).ToArray();
        if (s.Sources.All(x => !x.Enabled || _enabled.Contains(x.Kind))) return s;
        var agents = s.Agents.Where(a => _enabled.Contains(a.Source)).ToArray();
        var live = sources.Aggregate(Rate.Zero, (sum, x) => RemoteAggregation.Add(sum, x.Live));
        return s with
        {
            Sources = sources, Agents = agents, Live = live, History = [],
            SourceHistory = s.SourceHistory.Where(kv => _enabled.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value), PeakToday = Rate.Zero, SubsetPeaks = new Dictionary<SourceSet, double>(),
            Today = sources.Aggregate(TokenTotals.Zero, (sum, x) => RemoteAggregation.Add(sum, x.Today)),
            LiveCost = sources.Aggregate(LiveCost.Zero, (sum, x) => sum + x.LiveCost),
            TodayCost = sources.Aggregate(CostTally.Zero, (sum, x) => sum + x.TodayCost),
            LiveEstimatedShare = live.Output > 0 ? sources.Sum(x => x.LiveEstimated.Output) / live.Output : 0,
            GeneratingCount = agents.Count(a => a.Phase == AgentPhase.Generating), ToolsCount = agents.Count(a => a.Phase == AgentPhase.Tools),
        };
    }

    private IEnumerable<DailyUsage> SelectedDays() => (IncludeLocal ? _localHistory.Days : [])
        .Concat(SelectedHosts().SelectMany(s => s.Days)).Where(d => _enabled.Contains(d.Source));
    private void PublishHistory()
    {
        if (_settings.Selection == "local") { _history.Publish(_localHistory.State, _localHistory.Days); return; }
        var days = RemoteAggregation.Days(SelectedDays());
        var loaded = days.Count > 0 || SelectedHosts().Any(s => s.HistoryLoaded)
            || (IncludeLocal && _localHistory.State.Kind == HistoryStateKind.Ready);
        _history.Publish(loaded ? HistoryState.Ready(DateTimeOffset.Now) : HistoryState.NotLoaded, days);
    }

    public void Refresh()
    {
        _localHistory.Refresh();
        lock (_gate)
        {
            var settings = _settings;
            foreach (var state in _hosts.Values) { Interlocked.Increment(ref state.CacheRevision); state.Stop.Cancel(); }
            _hosts.Clear();
            Update(settings, _networkEnabled, _enabled);
        }
    }
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var state in _hosts.Values)
            {
                state.Days = [];
                state.HistoryLoaded = false;
                Interlocked.Increment(ref state.CacheRevision);
            }
            // Disabled and removed hosts can still have caches; clearing history removes those too.
            QueueCache(() =>
            {
                lock (_cacheGate)
                {
                    try
                    {
                        if (Directory.Exists(_cacheDirectory))
                            foreach (var path in Directory.EnumerateFiles(_cacheDirectory, "*.ndjson")) File.Delete(path);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            });
            _localHistory.Clear(); PublishHistory();
        }
    }

    // Enqueue under _gate so a clear cannot overtake a newer history write on the thread pool.
    private void QueueCache(Action action) => _cacheWork = _cacheWork.ContinueWith(_ => action(),
        CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    private string CachePath(RemoteHost host, string zone) => Path.Combine(_cacheDirectory, host.Id + "-" +
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(zone + "\n" + host.Destination
            + "\n" + host.Executable + "\n" + host.Shell)))[..16] + ".ndjson");
    private void LoadCache(HostState state, int revision)
    {
        try
        {
            var path = state.CachePath;
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024 * 1024) return;
            var frames = File.ReadLines(path).Select(RemoteFrame.Decode).ToArray();
            if (frames.All(f => f.Type == "history" && f.TimeZone == state.TimeZone))
            {
                var days = frames.SelectMany(f => f.Days!).ToArray();
                lock (_gate)
                    if (IsCurrent(state) && state.CacheRevision == revision && !state.HistoryLoaded && days.Length <= 100_000
                        && days.Select(d => (d.Day, d.Source)).Distinct().Count() == days.Length)
                    { state.Days = days; state.HistoryLoaded = true; }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
    }
    private void SaveCache(HostState state, DailyUsage[] days, int revision)
    {
        lock (_cacheGate)
        try
        {
            if (Volatile.Read(ref state.CacheRevision) != revision) return;
            Directory.CreateDirectory(_cacheDirectory);
            var path = state.CachePath;
            File.WriteAllLines(path + ".tmp", days.Chunk(64).Select(chunk => new RemoteFrame(1, "history",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Days: chunk, TimeZone: state.TimeZone).Encode()));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true; _timer.Dispose();
            foreach (var state in _hosts.Values) { Interlocked.Increment(ref state.CacheRevision); state.Stop.Cancel(); }
            _hosts.Clear();
            _local.LivePublished -= OnLocal; _local.EventRaised -= OnEvent; _localHistory.Changed -= OnHistory;
            if (_history.RefreshHandler == Refresh) _history.RefreshHandler = null;
            if (_history.ClearHandler == Clear) _history.ClearHandler = null;
        }
    }
}
