using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Core.Remote;
using AgentSpeed.Core.IO.Sqlite;

if (args is ["--check"])
{
    using var database = SqliteConnection.Open(":memory:", SqliteOpenMode.ReadWriteCreate);
    using var query = database.Prepare("SELECT 1");
    if (!query.Step()) return 1;
    Console.WriteLine("AGENTSPEED_COLLECTOR_V1");
    return 0;
}

// No listener or background service: the SSH session owns this process and its lifetime.
var configuration = MonitorConfiguration.Default;
var zone = TimeZoneInfo.Utc;
string? store = null;
if (args.Length == 0 || !args.Contains("--stream"))
{
    Console.Error.WriteLine("usage: agentspeed-collector --stream [--timezone ID] [--store PATH] [--codex PATH] [--claude PATH] [--dsh PATH] [--opencode PATH] [--gemini PATH] [--omp PATH] [--antigravity PATH] [--workbuddy PATH] [--workbuddy-ai PATH] [--zcode PATH] [--kimi PATH] [--grok-build PATH]");
    return 2;
}
try
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--stream") continue;
        var flag = args[i];
        if (++i == args.Length) throw new ArgumentException("Missing option value.");
        var value = args[i];
        switch (flag)
        {
            case "--sources":
                var sources = value.Length == 0 ? [] : value.Split(',').Select(x => (SourceKind)int.Parse(x, CultureInfo.InvariantCulture)).ToHashSet();
                if (sources.Any(x => !Enum.IsDefined(x))) throw new ArgumentException("Invalid sources.");
                configuration = configuration with { EnabledSources = sources };
                break;
            case "--timezone": zone = TimeZoneInfo.FindSystemTimeZoneById(value); break;
            case "--store": store = Path.GetFullPath(value); break;
            case "--codex": configuration = configuration with { CodexRoot = value }; break;
            case "--claude": configuration = configuration with { ClaudeRoot = value }; break;
            case "--dsh": configuration = configuration with { DshRoot = value }; break;
            case "--opencode": configuration = configuration with { OpenCodeRoot = value }; break;
            case "--gemini": configuration = configuration with { GeminiRoot = value }; break;
            case "--omp": configuration = configuration with { OmpRoot = value }; break;
            case "--antigravity": configuration = configuration with { AntigravityRoot = value }; break;
            case "--workbuddy": configuration = configuration with { WorkBuddyRoot = value }; break;
            case "--workbuddy-ai": configuration = configuration with { WorkBuddyAiRoot = value }; break;
            case "--zcode": configuration = configuration with { ZCodeRoot = value }; break;
            case "--kimi": configuration = configuration with { KimiRoot = value }; break;
            case "--grok-build": configuration = configuration with { GrokBuildRoot = value }; break;
            default: throw new ArgumentException("Unknown option: " + flag);
        }
    }
}
catch (Exception e) when (e is ArgumentException or FormatException or OverflowException or TimeZoneNotFoundException or InvalidTimeZoneException)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

// A separate store per receiving time zone prevents re-grouping one receiver's history for another.
var zoneKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
    zone.Id + "\n" + string.Join(",", configuration.EnabledSources.OrderBy(x => x)))))[..16];
store ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentSpeed", "collector", zoneKey, "history.sqlite");
var pricing = new PriceBook();
HistoryService.RestorePricing(pricing, store);
var live = new SpeedHub();
var history = new HistoryHub();
using var historyService = new HistoryService(history, configuration, store, pricing, zone);
using var monitor = new LiveMonitor(live, configuration, pricing, zone) { CaptureSourceHistory = true };
var latest = SpeedSnapshot.Empty;
DailyUsage[]? pendingHistory = null;
live.LivePublished += (_, snapshot) =>
{
    Volatile.Write(ref latest, snapshot);
    historyService.ObserveLive(snapshot);
};
history.Changed += (_, _) =>
{
    if (history.State.Kind == HistoryStateKind.Ready) Interlocked.Exchange(ref pendingHistory, history.Days.ToArray());
};
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
// Closing SSH's stdin also shuts down the collector on platforms that do not deliver SIGHUP.
_ = Task.Run(async () =>
{
    try
    {
        var buffer = new char[256];
        while (await Console.In.ReadAsync(buffer, stop.Token) != 0) { }
        stop.Cancel();
    }
    catch (OperationCanceledException) { }
    catch (IOException) { stop.Cancel(); }
});
monitor.Start();
historyService.Start();
try
{
    while (!stop.IsCancellationRequested)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await Write(new(1, "snapshot", now, RemoteSnapshot.From(Volatile.Read(ref latest)), TimeZone: zone.Id));
        if (Interlocked.Exchange(ref pendingHistory, null) is { } days)
        {
            var generation = Guid.NewGuid().ToString("N");
            var batches = days.Chunk(64).ToArray();
            if (batches.Length == 0) batches = [[]];
            for (var part = 0; part < batches.Length; part++)
                await Write(new(1, "history", now, Days: batches[part], TimeZone: zone.Id, Generation: generation, Part: part, Parts: batches.Length));
        }
        await Task.Delay(1000, stop.Token);
    }
}
catch (OperationCanceledException) { }
catch (IOException) { /* SSH closed its output pipe. */ }
return 0;

static async Task Write(RemoteFrame frame)
{
    var line = frame.Encode();
    if (System.Text.Encoding.UTF8.GetByteCount(line) > RemoteFrame.MaxLineBytes)
        throw new IOException("Statistics exceeded the frame size limit.");
    await Console.Out.WriteLineAsync(line);
    await Console.Out.FlushAsync();
}
