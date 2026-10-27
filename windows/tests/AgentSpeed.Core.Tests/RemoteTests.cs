using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Tests;

public sealed class RemoteTests
{
    private sealed class ImmediateContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    private static string[] Frames => File.ReadAllLines(FixtureLocator.PathOf("remote/stream.ndjson"));
    private static SpeedSnapshot Snapshot => RemoteFrame.Decode(Frames[0]).Snapshot!.ToSnapshot(1792490400000, "server", "Build Server");

    [Fact]
    public void SharedWireFixturesRoundTrip()
    {
        foreach (var line in Frames)
        {
            var frame = RemoteFrame.Decode(line);
            var copy = RemoteFrame.Decode(frame.Encode());
            Assert.Equal(frame.Type, copy.Type);
            Assert.Equal(frame.TimestampMs, copy.TimestampMs);
        }
        var s = Snapshot;
        Assert.Equal(100, s.Today.Output);
        Assert.Equal(50, s.Live.Output);
        Assert.Equal(1200, s.TodayCost.CostMicroUsd);
        Assert.Equal("Build Server", Assert.Single(s.Agents).Project);
        Assert.StartsWith("server/", s.Agents[0].Id);
        Assert.Equal("fast", Assert.Single(s.Agents[0].Modes.Facts).Value);
        var day = Assert.Single(RemoteFrame.Decode(Frames[1]).Days!);
        Assert.Equal(50, day.AverageSpeed);
        Assert.Single(day.Modes);
    }

    [Fact]
    public void WireDoesNotExposePathsOrProjects()
    {
        var s = Snapshot with { Agents = [Snapshot.Agents[0] with { Id = "/private/home/project/session.jsonl", Project = "secret-project", ParentId = "/private/home/parent.jsonl" }] };
        var line = new RemoteFrame(1, "snapshot", 1792490400000, RemoteSnapshot.From(s)).Encode();
        Assert.DoesNotContain("/private", line);
        Assert.DoesNotContain("secret-project", line);
        var agent = RemoteFrame.Decode(line).Snapshot!.Agents[0];
        Assert.Equal(64, agent.Id.Length);
        Assert.Equal(64, agent.ParentId!.Length);
    }

    [Theory]
    [InlineData("-oProxyCommand=bad")]
    [InlineData("host; touch bad")]
    [InlineData("host\ncommand")]
    [InlineData("user@host extra")]
    public void RejectsUnsafeDestinations(string destination) =>
        Assert.False(new RemoteHost("server", "Server", destination).IsValid);

    [Fact]
    public void SshKeepsAuthenticationAndHostKeyChecksEnabled()
    {
        var host = new RemoteHost("server", "Server", "user@host", "/opt/it's safe/collector");
        var args = host.StartInfo("Asia/Shanghai", new HashSet<SourceKind> { SourceKind.Codex }).ArgumentList;
        Assert.Contains("BatchMode=yes", args);
        Assert.Contains("StrictHostKeyChecking=yes", args);
        Assert.Contains("--", args);
        Assert.Contains("'\\''", args.Last());
        Assert.EndsWith("--sources '0'", args.Last());
        Assert.False(host.StartInfo("UTC").UseShellExecute);
    }

    [Fact]
    public void PowerShellCommandIsEncodedAndQuotesLiteralPath()
    {
        var args = new RemoteHost("server", "Server", "win", @"C:\it's safe\collector.exe", Shell: "powershell").StartInfo("UTC").ArgumentList;
        var command = args.Last();
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(command.Split(' ').Last()));
        Assert.Contains(@"& 'C:\it''s safe\collector.exe'", script);
        Assert.Contains("--timezone 'UTC'", script);
    }

    [Fact]
    public void SettingsValidateDuplicatesAndRoundTrip()
    {
        var host = new RemoteHost("server", "Server", "build-server");
        var settings = new RemoteSettings { Hosts = [host], Selection = host.Id };
        Assert.True(settings.IsValid);
        var copy = RemoteSettings.Parse(settings.ToJson());
        Assert.Equal(host, Assert.Single(copy.Hosts));
        Assert.Equal(host.Id, copy.Selection);
        Assert.False((settings with { Hosts = [host, host with { Id = "other" }] }).IsValid);
        Assert.False((settings with { Selection = "missing" }).IsValid);
        Assert.Throws<JsonException>(() => RemoteSettings.Parse("{\"hosts\":null}"));
    }

    [Theory]
    [InlineData("schema", "2")]
    [InlineData("timestampMs", "-1")]
    [InlineData("parts", "0")]
    public void RejectsInvalidEnvelope(string field, string value)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(Frames[0])!;
        document[field] = JsonNodeValue(value);
        Assert.Throws<JsonException>(() => RemoteFrame.Decode(document.ToJsonString()));
    }
    private static System.Text.Json.Nodes.JsonNode? JsonNodeValue(string value) => System.Text.Json.Nodes.JsonNode.Parse(value);

    [Fact]
    public void RejectsNegativeUsage()
    {
        var frame = RemoteFrame.Decode(Frames[0]);
        Assert.Throws<JsonException>(() => RemoteFrame.Decode((frame with { Snapshot = frame.Snapshot! with { Today = new(Output: -1) } }).Encode()));
    }

    [Fact]
    public async Task ReadsFragmentedUtf8AndRejectsOversizedLines()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Frames[0] + "\n" + Frames[1] + "\n"));
        var lines = new List<string>();
        await foreach (var line in RemoteConnection.Lines(stream, CancellationToken.None)) lines.Add(line);
        Assert.Equal(Frames, lines);
        await using var huge = new MemoryStream(new byte[RemoteFrame.MaxLineBytes + 1]);
        await Assert.ThrowsAsync<IOException>(async () => { await foreach (var _ in RemoteConnection.Lines(huge, CancellationToken.None)) { } });
    }

    [Fact]
    public void AggregateUsesWeightedEstimatesAndAlignedPeak()
    {
        var a = Snapshot;
        var b = a with { Live = new(150, 1500), LiveEstimatedShare = 0.6, Agents = [a.Agents[0] with { Id = "another/id" }] };
        var merged = RemoteAggregation.Snapshot([a, b], a.Timestamp, new(60, 600));
        Assert.Equal(200, merged.Live.Output);
        Assert.Equal(.5, merged.LiveEstimatedShare);
        Assert.Equal(200, merged.Today.Output);
        Assert.Equal(2400, merged.TodayCost.CostMicroUsd);
        Assert.Equal(60, merged.PeakToday.Output);
        Assert.Equal(2, merged.Agents.Count);
        var t = a.Timestamp;
        var disjoint = new[] { new HistoryPoint(t, new(90, 900), Rate.Zero), new HistoryPoint(t.AddSeconds(10), new(60, 600), Rate.Zero) };
        Assert.Equal(30, RemoteAggregation.Peak(disjoint).Output);
    }

    [Fact]
    public void HistoryReplacementIsIdempotentAndDoesNotSumIndependentPeaks()
    {
        var frame = RemoteFrame.Decode(Frames[1]);
        var days = RemoteAggregation.Days(frame.Days!);
        Assert.Equal(100, Assert.Single(days).Totals.Output);
        days = RemoteAggregation.Days(frame.Days!);
        Assert.Equal(100, Assert.Single(days).Totals.Output);
        var combined = Assert.Single(RemoteAggregation.Days(frame.Days!.Concat(frame.Days!)));
        Assert.Equal(200, combined.Totals.Output);
        Assert.Equal(50, combined.AverageSpeed);
        Assert.Null(combined.Peak);
    }

    [Fact]
    public void MixedHistoricalPricingPreservesKnownCostAndMarksUnknownRequests()
    {
        var priced = RemoteFrame.Decode(Frames[1]).Days![0];
        var unpriced = priced with { Cost = null, PriceVersion = null,
            Models = priced.Models.Select(m => m with { Cost = null }).ToArray() };
        var day = Assert.Single(RemoteAggregation.Days([priced, unpriced]));
        Assert.Equal(1200, day.Cost!.Value.CostMicroUsd);
        Assert.Equal(2, day.Cost.Value.UnpricedRequests);
        Assert.Equal(1200, Assert.Single(day.Models).Cost!.Value.CostMicroUsd);
        Assert.Null(day.PriceVersion);
    }

    [Fact]
    public void StaleSnapshotsClearLiveDataAndMidnightTotals()
    {
        var snapshot = Snapshot;
        var stale = RemoteAggregation.Idle(snapshot, snapshot.Timestamp);
        Assert.Equal(Rate.Zero, stale.Live);
        Assert.Empty(stale.Agents);
        var nextDay = RemoteAggregation.Idle(snapshot, snapshot.Timestamp.AddDays(1));
        Assert.Equal(TokenTotals.Zero, nextDay.Today);
        Assert.Equal(CostTally.Zero, nextDay.TodayCost);
    }

    [Fact]
    public void LocalOnlyCompositionPreservesLocalValuesAndEvents()
    {
        using var directory = new TempDirectory();
        var context = new ImmediateContext();
        var local = new SpeedHub(context); var output = new SpeedHub(context);
        var history = new HistoryHub(context); var outputHistory = new HistoryHub(context);
        using var monitor = new RemoteMonitor(local, history, output, outputHistory, directory.Combine("remote"));
        monitor.Update(RemoteSettings.Default, false, Enum.GetValues<SourceKind>().ToHashSet());
        local.PublishLive(Snapshot);
        Assert.Equal(Snapshot.Live, output.Snapshot.Live);
        Assert.Equal(Snapshot.Today, output.Snapshot.Today);
        history.Publish(HistoryState.Ready(DateTimeOffset.Now), RemoteFrame.Decode(Frames[1]).Days);
        Assert.Single(outputHistory.Days);
    }

    [Fact]
    public void SourceBucketsSurviveWireRoundTripWithoutPaths()
    {
        var snapshot = Snapshot with
        {
            SourceHistory = new Dictionary<SourceKind, IReadOnlyList<HistoryPoint>>
            {
                [SourceKind.Codex] = Snapshot.History,
            },
        };
        var frame = RemoteFrame.Decode(new RemoteFrame(1, "snapshot", 1792490400000,
            RemoteSnapshot.From(snapshot)).Encode());
        var restored = frame.Snapshot!.ToSnapshot(frame.TimestampMs, "server", "Server");
        Assert.Equal(snapshot.History, restored.SourceHistory[SourceKind.Codex]);
        Assert.Equal(40 / 3.0, RemoteAggregation.Peak(restored.SourceHistory[SourceKind.Codex]).Output);
    }

    [Theory]
    [InlineData("todayCost", "costMicroUsd")]
    [InlineData("liveCost", "knownOutput")]
    public void RejectsNegativeCosts(string section, string field)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(Frames[0])!;
        document["snapshot"]![section]![field] = -1;
        Assert.Throws<JsonException>(() => RemoteFrame.Decode(document.ToJsonString()));
    }

    [Fact]
    public void SettingsHaveValueEqualityAfterDeserialization()
    {
        var settings = new RemoteSettings { Hosts = [new("server", "Server", "example.com")], Selection = "all" };
        var copy = RemoteSettings.Parse(settings.ToJson());
        Assert.Equal(settings, copy);
        Assert.Equal(settings.GetHashCode(), copy.GetHashCode());
        Assert.Equal(RemoteSettings.Default, RemoteSettings.Parse("{}"));
    }

    [Fact]
    public void NetworkOffDoesNotStartSshOrContaminateLocalHistory()
    {
        using var directory = new TempDirectory();
        var context = new ImmediateContext();
        var local = new SpeedHub(context); var output = new SpeedHub(context);
        var history = new HistoryHub(context); var outputHistory = new HistoryHub(context);
        using var monitor = new RemoteMonitor(local, history, output, outputHistory, directory.Combine("remote"));
        monitor.Update(new() { Hosts = [new("server", "Server", "invalid.example")], Selection = "all" },
            false, Enum.GetValues<SourceKind>().ToHashSet());
        Assert.Equal("disabled", monitor.Statuses["server"]);
        local.PublishLive(Snapshot);
        history.Publish(HistoryState.Ready(DateTimeOffset.Now), RemoteFrame.Decode(Frames[1]).Days);
        Assert.Equal(100, output.Snapshot.Today.Output);
        Assert.Equal(100, Assert.Single(history.Days).Totals.Output);
        Assert.Equal(100, Assert.Single(outputHistory.Days).Totals.Output);
        monitor.Update(new() { Hosts = [new("server", "Server", "invalid.example")], Selection = "server" },
            false, Enum.GetValues<SourceKind>().ToHashSet());
        Assert.Equal(Rate.Zero, output.Snapshot.Live);
        Assert.Equal(100, local.Snapshot.Today.Output);
        Assert.Single(history.Days);
    }

    [Fact]
    public async Task ReplacesCompleteHistoryGenerationsAndIgnoresLateHostCallbacks()
    {
        using var directory = new TempDirectory();
        var context = new ImmediateContext();
        var local = new SpeedHub(context); var output = new SpeedHub(context);
        var history = new HistoryHub(context); var outputHistory = new HistoryHub(context);
        var connected = new TaskCompletionSource<Action<RemoteFrame>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new RemoteMonitor(local, history, output, outputHistory, directory.Combine("remote"),
            (_, _, receive, status, _, _) =>
            {
                status("connected"); connected.TrySetResult(receive); return Task.CompletedTask;
            });
        var host = new RemoteHost("server", "Server", "test.example");
        monitor.Update(new() { Hosts = [host], Selection = "server" }, true, Enum.GetValues<SourceKind>().ToHashSet());
        var receive = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var frame = RemoteFrame.Decode(Frames[1]);
        receive(frame);
        receive(frame);
        Assert.Equal(100, Assert.Single(outputHistory.Days).Totals.Output);
        Assert.Empty(history.Days);
        var next = frame.Days![0] with { Day = "2026-10-21", Totals = new(Output: 50) };
        receive(frame with { Generation = "two", Part = 0, Parts = 2 });
        Assert.Single(outputHistory.Days);
        receive(frame with { Generation = "two", Part = 1, Parts = 2, Days = [next] });
        Assert.Equal(2, outputHistory.Days.Count);
        receive(frame with { Generation = "empty", Days = [] });
        Assert.Empty(outputHistory.Days);
        Assert.Equal(HistoryStateKind.Ready, outputHistory.State.Kind);
        monitor.Update(RemoteSettings.Default, false, Enum.GetValues<SourceKind>().ToHashSet());
        receive(frame);
        Assert.Empty(outputHistory.Days);
    }
}
