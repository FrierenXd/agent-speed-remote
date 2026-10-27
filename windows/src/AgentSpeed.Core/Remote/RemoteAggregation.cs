namespace AgentSpeed.Core.Remote;

public static class RemoteAggregation
{
    public static TokenTotals Add(TokenTotals a, TokenTotals b) => new(a.Requests + b.Requests, a.Input + b.Input,
        a.CachedInput + b.CachedInput, a.CacheWriteInput + b.CacheWriteInput, a.Output + b.Output, a.Reasoning + b.Reasoning);
    public static Rate Add(Rate a, Rate b) => new(a.Output + b.Output, a.Total + b.Total);

    public static IReadOnlyList<DailyUsage> Days(IEnumerable<DailyUsage> rows) => rows.GroupBy(d => (d.Day, d.Source))
        .Select(g => g.First() with
        {
            Totals = g.Aggregate(TokenTotals.Zero, (sum, d) => Add(sum, d.Totals)),
            MeasuredOutput = g.Sum(d => d.MeasuredOutput), MeasuredMs = g.Sum(d => d.MeasuredMs),
            Unfinalized = g.Sum(d => d.Unfinalized), Agents = g.Sum(d => d.Agents),
            // Independent host peaks did not necessarily happen at the same time.
            Peak = g.Count() == 1 ? g.First().Peak : null,
            Cost = CombinedCost(g.Select(d => (d.Cost, d.Totals.Requests))),
            PriceVersion = g.Select(d => d.PriceVersion).Distinct().Count() == 1 ? g.First().PriceVersion : null,
            Models = g.SelectMany(d => d.Models).GroupBy(m => m.Model).Select(m => new ModelUsage(m.Key,
                m.Sum(x => x.Requests), m.Sum(x => x.Output), m.Sum(x => x.MeasuredOutput), m.Sum(x => x.MeasuredMs))
                { Cost = CombinedCost(m.Select(x => (x.Cost, x.Requests))) }).ToArray(),
            Modes = ExecutionModeUsage.Combine(g.SelectMany(d => d.Modes)),
        }).OrderBy(d => d.Day, StringComparer.Ordinal).ThenBy(d => d.Source).ToArray();

    private static CostTally? CombinedCost(IEnumerable<(CostTally? Cost, long Requests)> parts)
    {
        var rows = parts.ToArray();
        if (rows.All(r => r.Cost is null)) return null;
        return rows.Aggregate(CostTally.Zero, (sum, row) => sum + (row.Cost ?? new CostTally(UnpricedRequests: row.Requests)));
    }

    public static SpeedSnapshot Snapshot(IReadOnlyList<SpeedSnapshot> snapshots, DateTimeOffset now, Rate observedPeak)
    {
        if (snapshots.Count == 0) return SpeedSnapshot.Empty with { Timestamp = now, State = MonitorState.NoSources };
        if (snapshots.Count == 1) return snapshots[0] with { Timestamp = now };
        var live = snapshots.Aggregate(Rate.Zero, (sum, s) => Add(sum, s.Live));
        var sources = snapshots.SelectMany(s => s.Sources).GroupBy(s => s.Kind).Select(g => g.First() with
        {
            RootPath = "", Enabled = g.Any(s => s.Enabled), Available = g.Any(s => s.Available),
            SessionCount = g.Sum(s => s.SessionCount), ActiveAgents = g.Sum(s => s.ActiveAgents),
            Live = g.Aggregate(Rate.Zero, (sum, s) => Add(sum, s.Live)),
            LiveEstimated = g.Aggregate(Rate.Zero, (sum, s) => Add(sum, s.LiveEstimated)),
            Today = g.Aggregate(TokenTotals.Zero, (sum, s) => Add(sum, s.Today)),
            LiveCost = g.Aggregate(LiveCost.Zero, (sum, s) => sum + s.LiveCost),
            TodayCost = g.Aggregate(CostTally.Zero, (sum, s) => sum + s.TodayCost),
            UnpricedModels = g.SelectMany(s => s.UnpricedModels).Distinct().ToArray(),
            ConfiguredModes = new(g.SelectMany(s => s.ConfiguredModes.Facts)),
            TodayModes = ExecutionModeUsage.Combine(g.SelectMany(s => s.TodayModes)),
        }).OrderBy(s => s.Kind).ToArray();
        var history = snapshots.SelectMany(s => s.History).GroupBy(p => p.Time.ToUnixTimeSeconds()).OrderBy(g => g.Key)
            .TakeLast(300).Select(g => new HistoryPoint(DateTimeOffset.FromUnixTimeSeconds(g.Key),
                g.Aggregate(Rate.Zero, (sum, p) => Add(sum, p.Measured)),
                g.Aggregate(Rate.Zero, (sum, p) => Add(sum, p.Estimated)))).ToArray();
        return new(now, snapshots.Any(s => s.State.Kind == MonitorStateKind.Running) ? MonitorState.Running
            : snapshots.Any(s => s.State.Kind == MonitorStateKind.Starting) ? MonitorState.Starting(0) : MonitorState.NoSources,
            live, live.Output > 0 ? snapshots.Sum(s => s.Live.Output * s.LiveEstimatedShare) / live.Output : 0,
            observedPeak, null, null, snapshots.Aggregate(TokenTotals.Zero, (sum, s) => Add(sum, s.Today)), sources,
            snapshots.SelectMany(s => s.Agents).OrderByDescending(a => a.LastActivity).ToArray(),
            snapshots.Sum(s => s.GeneratingCount), snapshots.Sum(s => s.ToolsCount), history,
            snapshots.Select(s => s.LastResponse).Where(r => r is not null).OrderByDescending(r => r!.EndedAt).FirstOrDefault(),
            snapshots.SelectMany(s => s.Warnings).Distinct().ToArray())
        {
            DayStart = snapshots.Max(s => s.DayStart),
            LiveCost = snapshots.Aggregate(LiveCost.Zero, (sum, s) => sum + s.LiveCost),
            TodayCost = snapshots.Aggregate(CostTally.Zero, (sum, s) => sum + s.TodayCost),
            TodayModes = ExecutionModeUsage.Combine(snapshots.SelectMany(s => s.TodayModes)),
        };
    }

    /// <summary>Maximum three-second trailing average of aligned measured buckets, not a sum of host peaks.</summary>
    public static Rate Peak(IEnumerable<HistoryPoint> points)
    {
        var buckets = points.GroupBy(p => p.Time.ToUnixTimeSeconds())
            .ToDictionary(g => g.Key, g => g.Aggregate(Rate.Zero, (sum, p) => Add(sum, p.Measured)));
        var peak = Rate.Zero;
        foreach (var time in buckets.Keys.SelectMany(t => new[] { t, t + 1, t + 2 }).Distinct())
        {
            var total = Rate.Zero;
            for (var i = 0; i < 3; i++) if (buckets.TryGetValue(time - i, out var rate)) total = Add(total, rate);
            peak = new(Math.Max(peak.Output, total.Output / 3), Math.Max(peak.Total, total.Total / 3));
        }
        return peak;
    }

    public static SpeedSnapshot Idle(SpeedSnapshot snapshot, DateTimeOffset now)
    {
        var sameDay = snapshot.DayStart == DateTimeOffset.MinValue ||
            TimeZoneInfo.ConvertTime(snapshot.DayStart, TimeZoneInfo.Local).Date == now.LocalDateTime.Date;
        return snapshot with
        {
            Timestamp = now, Live = Rate.Zero, LiveCost = LiveCost.Zero, LiveEstimatedShare = 0,
            Agents = [], GeneratingCount = 0, ToolsCount = 0, LastResponse = null,
            Today = sameDay ? snapshot.Today : TokenTotals.Zero, TodayCost = sameDay ? snapshot.TodayCost : CostTally.Zero,
            PeakToday = sameDay ? snapshot.PeakToday : Rate.Zero, AverageToday = sameDay ? snapshot.AverageToday : null,
            Sources = snapshot.Sources.Select(s => s with { Live = Rate.Zero, LiveEstimated = Rate.Zero,
                LiveCost = LiveCost.Zero, ActiveAgents = 0, Today = sameDay ? s.Today : TokenTotals.Zero,
                TodayCost = sameDay ? s.TodayCost : CostTally.Zero }).ToArray(),
        };
    }
}
