using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentSpeed.Core.Remote;

/// <summary>Versioned NDJSON; only statistics and opaque agent IDs, never transcript text or absolute paths.</summary>
public sealed record RemoteFrame(int Schema, string Type, long TimestampMs, RemoteSnapshot? Snapshot = null,
    DailyUsage[]? Days = null, string? TimeZone = null, string? Generation = null, int Part = 0, int Parts = 1)
{
    public const int MaxLineBytes = 4 * 1024 * 1024;
    public const int MaxHistoryBytes = 64 * 1024 * 1024;
    [JsonIgnore] internal int WireBytes { get; init; }
    public string Encode() => JsonSerializer.Serialize(this, RemoteJson.Default.RemoteFrame);
    public static RemoteFrame Decode(string json)
    {
        var wireBytes = System.Text.Encoding.UTF8.GetByteCount(json);
        if (wireBytes > MaxLineBytes) throw new JsonException("Remote frame is too large.");
        var frame = JsonSerializer.Deserialize(json, RemoteJson.Default.RemoteFrame);
        if (frame is null || frame.Schema != 1 || frame.TimestampMs < 0
            || frame.TimestampMs > 253402300799999 || frame.Parts is < 1 or > 1024 || frame.Part < 0 || frame.Part >= frame.Parts
            || frame.Type is not ("snapshot" or "history")
            || (frame.Type == "snapshot" && (frame.Snapshot is null || frame.Days is not null))
            || (frame.Type == "history" && (frame.Days is null || frame.Snapshot is not null))) throw new JsonException("Unsupported remote frame.");
        if (frame.Snapshot is { } snapshot) snapshot.Validate();
        if (frame.Days is { } days && (days.Length > 100_000 || days.Any(d => d is null
            || !Enum.IsDefined(d.Source) || !DateOnly.TryParseExact(d.Day, "yyyy-MM-dd", out _)
            || !Valid(d.Totals) || !ValidCount(d.MeasuredOutput) || !ValidCount(d.MeasuredMs) || d.Agents is < 0 or > 1_000_000
            || !ValidCount(d.Unfinalized) || (d.Cost is { } cost && !Valid(cost)) || !ValidNumber(d.Peak)
            || d.Models is null || d.Models.Count > 2000 || d.Models.Any(m => m is null || !ValidText(m.Model)
                || !ValidCount(m.Requests) || !ValidCount(m.Output) || !ValidCount(m.MeasuredOutput) || !ValidCount(m.MeasuredMs)
                || (m.Cost is { } modelCost && !Valid(modelCost)))
            || d.Models.Select(m => m.Model).Distinct(StringComparer.Ordinal).Count() != d.Models.Count
            || d.Modes is null || d.Modes.Count > 20_000 || d.Modes.Any(m => !Valid(m)))))
            throw new JsonException("Invalid remote history.");
        return frame with { WireBytes = wireBytes };
    }

    internal static bool ValidCount(long value) => value is >= 0 and <= (1L << 40);
    internal static bool ValidNumber(double? value) => value is null || (double.IsFinite(value.Value) && value is >= 0 and <= 1e15);
    internal static bool ValidText(string? value) => value is { Length: <= 512 } && !value.Any(char.IsControl);
    internal static bool Valid(CostTally cost) => ValidCount(cost.CostMicroUsd) && ValidCount(cost.UnpricedRequests);
    internal static bool Valid(LiveCost cost) => ValidNumber(cost.MicroUsdPerSecond) && ValidNumber(cost.KnownOutput);
    internal static bool Valid(ExecutionModeUsage? mode) => mode is not null && Enum.IsDefined(mode.Kind)
        && Enum.IsDefined(mode.Source) && ValidText(mode.Model) && ExecutionModes.IsIdentifier(mode.Mode, 64)
        && ValidText(mode.Evidence) && (mode.Provider is null || ExecutionModes.IsIdentifier(mode.Provider, 32))
        && ValidCount(mode.Requests) && ValidCount(mode.Output) && ValidCount(mode.MeasuredOutput) && ValidCount(mode.MeasuredMs);
    internal static bool Valid(ExecutionModeFact[]? modes) => modes is { Length: <= 20 } && modes.All(m => m is not null
        && Enum.IsDefined(m.Kind) && Enum.IsDefined(m.Evidence) && Enum.IsDefined(m.Scope)
        && ExecutionModes.IsIdentifier(m.RawValue, 64) && ExecutionModes.IsIdentifier(m.Value, 64)
        && (m.Provider is null || ExecutionModes.IsIdentifier(m.Provider, 32)));
    internal static bool Valid(TokenTotals t) => ValidCount(t.Requests) && ValidCount(t.Input) && ValidCount(t.CachedInput)
        && ValidCount(t.CacheWriteInput) && ValidCount(t.Output) && ValidCount(t.Reasoning);
    internal static bool Valid(Rate r) => double.IsFinite(r.Output) && double.IsFinite(r.Total)
        && r.Output is >= 0 and <= 1e15 && r.Total is >= 0 and <= 1e15;
}

public sealed record RemoteAgent(string Id, SourceKind Source, string? Model, AgentPhase Phase, bool IsSubagent,
    Rate Live, double? RecentSpeed, long LastActivityMs, string? ParentId, LiveCost LiveCost, ExecutionModeFact[] ModeFacts)
{
    public AgentStatus ToAgent(string hostId, string hostName) => new($"{hostId}/{Id}", Source, hostName, Model, Phase,
        IsSubagent, Live, RecentSpeed, DateTimeOffset.FromUnixTimeMilliseconds(LastActivityMs),
        ParentId is null ? null : $"{hostId}/{ParentId}") { LiveCost = LiveCost, Modes = new(ModeFacts) };
}

public sealed record RemoteSource(SourceKind Kind, bool Enabled, bool Available, int SessionCount, Rate Live, TokenTotals Today,
    int ActiveAgents, Rate LiveEstimated, LiveCost LiveCost, CostTally TodayCost, ExecutionModeFact[] ModeFacts,
    ExecutionModeUsage[] TodayModes)
{
    public SourceStatus ToSource() => new(Kind, Enabled, "", Available, SessionCount, Live, Today, ActiveAgents, LiveEstimated)
        { LiveCost = LiveCost, TodayCost = TodayCost, ConfiguredModes = new(ModeFacts), TodayModes = TodayModes };
}

public sealed record RemotePoint(long TimeMs, Rate Measured, Rate Estimated);
public sealed record RemoteSourceHistory(SourceKind Source, RemotePoint[] Points);

public sealed record RemoteSnapshot(long DayStartMs, MonitorState State, Rate Live, double EstimatedShare, Rate Peak,
    double? Average, TokenTotals Today, RemoteSource[] Sources, RemoteAgent[] Agents, RemotePoint[] History,
    LiveCost LiveCost, CostTally TodayCost)
{
    public RemoteSourceHistory[]? SourceHistory { get; init; }

    public static RemoteSnapshot From(SpeedSnapshot s)
    {
        static string Id(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
        return new(s.DayStart == DateTimeOffset.MinValue ? 0 : s.DayStart.ToUnixTimeMilliseconds(), s.State, s.Live,
            s.LiveEstimatedShare, s.PeakToday, s.AverageToday, s.Today,
            s.Sources.Select(x => new RemoteSource(x.Kind, x.Enabled, x.Available, x.SessionCount, x.Live, x.Today, x.ActiveAgents,
                x.LiveEstimated, x.LiveCost, x.TodayCost, [.. x.ConfiguredModes.Facts], [.. x.TodayModes])).ToArray(),
            s.Agents.Select(a => new RemoteAgent(Id(a.Id), a.Source, a.Model, a.Phase, a.IsSubagent, a.Live, a.RecentSpeed,
                a.LastActivity.ToUnixTimeMilliseconds(), a.ParentId is null ? null : Id(a.ParentId), a.LiveCost, [.. a.Modes.Facts])).ToArray(),
            s.History.Select(p => new RemotePoint(p.Time.ToUnixTimeMilliseconds(), p.Measured, p.Estimated)).ToArray(),
            s.LiveCost, s.TodayCost)
        { SourceHistory = s.SourceHistory.Select(kv => new RemoteSourceHistory(kv.Key, kv.Value.Select(p =>
            new RemotePoint(p.Time.ToUnixTimeMilliseconds(), p.Measured, p.Estimated)).ToArray())).ToArray() };
    }

    public SpeedSnapshot ToSnapshot(long timestamp, string hostId, string hostName) => new(
        DateTimeOffset.FromUnixTimeMilliseconds(timestamp), State, Live, EstimatedShare, Peak, null, Average, Today,
        Sources.Select(s => s.ToSource()).ToArray(), Agents.Select(a => a.ToAgent(hostId, hostName)).ToArray(),
        Agents.Count(a => a.Phase == AgentPhase.Generating), Agents.Count(a => a.Phase == AgentPhase.Tools),
        History.Select(p => new HistoryPoint(DateTimeOffset.FromUnixTimeMilliseconds(p.TimeMs), p.Measured, p.Estimated)).ToArray(), null, [])
        { DayStart = DateTimeOffset.FromUnixTimeMilliseconds(DayStartMs), LiveCost = LiveCost, TodayCost = TodayCost,
            TodayModes = ExecutionModeUsage.Combine(Sources.SelectMany(s => s.TodayModes)),
            SourceHistory = (SourceHistory ?? []).ToDictionary(x => x.Source, x => (IReadOnlyList<HistoryPoint>)x.Points.Select(p =>
                new HistoryPoint(DateTimeOffset.FromUnixTimeMilliseconds(p.TimeMs), p.Measured, p.Estimated)).ToArray()) };

    internal void Validate()
    {
        if (DayStartMs < 0 || DayStartMs > 253402300799999 || !Enum.IsDefined(State.Kind)
            || !RemoteFrame.ValidNumber(Average) || !RemoteFrame.Valid(LiveCost) || !RemoteFrame.Valid(TodayCost)
            || !double.IsFinite(State.Progress) || State.Progress is < 0 or > 1
            || !RemoteFrame.Valid(Live) || !RemoteFrame.Valid(Peak) || !RemoteFrame.Valid(Today)
            || !double.IsFinite(EstimatedShare) || EstimatedShare is < 0 or > 1
            || Sources is null || Sources.Length > 32 || Agents is null || Agents.Length > 10_000
            || (SourceHistory is not null && (SourceHistory.Length > 32
            || SourceHistory.Any(h => h is null || !Enum.IsDefined(h.Source) || h.Points is null || h.Points.Length > 300
                || h.Points.Any(p => p is null || p.TimeMs is < 0 or > 253402300799999
                    || !RemoteFrame.Valid(p.Measured) || !RemoteFrame.Valid(p.Estimated)))
            || SourceHistory.Select(h => h.Source).Distinct().Count() != SourceHistory.Length))
            || History is null || History.Length > 300
            || Sources.Any(s => s is null || !Enum.IsDefined(s.Kind) || !RemoteFrame.Valid(s.Live)
                || !RemoteFrame.Valid(s.LiveEstimated) || !RemoteFrame.Valid(s.Today) || !RemoteFrame.Valid(s.ModeFacts)
                || !RemoteFrame.Valid(s.LiveCost) || !RemoteFrame.Valid(s.TodayCost)
                || s.SessionCount is < 0 or > 1_000_000 || s.ActiveAgents is < 0 or > 10_000
                || s.TodayModes is null || s.TodayModes.Length > 20_000 || s.TodayModes.Any(m => !RemoteFrame.Valid(m)))
            || Sources.Select(s => s.Kind).Distinct().Count() != Sources.Length
            || Agents.Any(a => a is null || !RemoteSettings.IsId(a.Id) || !Enum.IsDefined(a.Source)
                || !Enum.IsDefined(a.Phase) || !RemoteFrame.Valid(a.Live) || a.LastActivityMs is < 0 or > 253402300799999
                || !RemoteFrame.Valid(a.ModeFacts) || !RemoteFrame.Valid(a.LiveCost) || !RemoteFrame.ValidNumber(a.RecentSpeed)
                || (a.Model is not null && !RemoteFrame.ValidText(a.Model))
                || (a.ParentId is not null && !RemoteSettings.IsId(a.ParentId)))
            || Agents.Select(a => a.Id).Distinct().Count() != Agents.Length
            || History.Any(p => p is null || p.TimeMs is < 0 or > 253402300799999
                || !RemoteFrame.Valid(p.Measured) || !RemoteFrame.Valid(p.Estimated))) throw new JsonException("Invalid remote snapshot.");
    }
}
