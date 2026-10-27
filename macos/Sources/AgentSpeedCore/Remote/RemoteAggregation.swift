import Foundation

enum RemoteAggregation {
    static func add(_ a: Rate, _ b: Rate) -> Rate { Rate(output: a.output + b.output, total: a.total + b.total) }
    static func add(_ a: TokenTotals, _ b: TokenTotals) -> TokenTotals {
        TokenTotals(requests: a.requests.saturatingAdd(b.requests), input: a.input.saturatingAdd(b.input),
            cachedInput: a.cachedInput.saturatingAdd(b.cachedInput), cacheWriteInput: a.cacheWriteInput.saturatingAdd(b.cacheWriteInput),
            output: a.output.saturatingAdd(b.output), reasoning: a.reasoning.saturatingAdd(b.reasoning))
    }
    static func days(_ rows: [DailyUsage]) -> [DailyUsage] {
        Dictionary(grouping: rows) { UsageKey(day: $0.day, source: $0.source) }.map { key, group in
            var total = DayAggregate()
            for day in group {
                var part = DayAggregate()
                part.totals = day.totals; part.measuredOutput = day.measuredOutput; part.measuredMs = day.measuredMs
                part.unfinalized = day.unfinalized; part.agents = day.agents; part.cost = day.cost
                for model in day.models {
                    part.models[model.model] = ModelTally(requests: model.requests, output: model.output,
                        measuredOutput: model.measuredOutput, measuredMs: model.measuredMs, cost: model.cost)
                }
                for mode in day.modeUsage { part.modes[ExecutionModeUsageKey(model: mode.model, attribution: mode.attribution)] = mode }
                total.merge(part)
            }
            return total.usage(day: key.day, source: key.source, peak: group.count == 1 ? group[0].peak : nil,
                priceVersion: Set(group.map(\.priceVersion)).count == 1 ? group.first?.priceVersion : nil)
        }.sorted { ($0.day, $0.source.rawValue) < ($1.day, $1.source.rawValue) }
    }
    static func snapshot(_ snapshots: [SpeedSnapshot], now: Date, peak: Rate) -> SpeedSnapshot {
        guard let first = snapshots.first else { var empty = SpeedSnapshot.empty; empty.timestamp = now; empty.state = .noSources; return empty }
        if snapshots.count == 1 { var copy = first; copy.timestamp = now; return copy }
        var result = first
        result.timestamp = now
        result.state = snapshots.contains { $0.state == .running } ? .running : .noSources
        if result.state == .noSources, snapshots.contains(where: { if case .starting = $0.state { true } else { false } }) {
            result.state = .starting(progress: 0)
        }
        result.dayStart = snapshots.map(\.dayStart).max() ?? .distantPast
        result.live = snapshots.reduce(.zero) { add($0, $1.live) }
        result.liveEstimatedShare = result.live.output > 0 ? snapshots.reduce(0) { $0 + $1.live.output * $1.liveEstimatedShare } / result.live.output : 0
        result.today = snapshots.reduce(.zero) { add($0, $1.today) }
        result.liveCost = snapshots.reduce(.zero) { var sum = $0; sum.add($1.liveCost); return sum }
        result.todayCost = snapshots.reduce(.zero) { var sum = $0; sum.add($1.todayCost); return sum }
        result.agents = snapshots.flatMap(\.agents).sorted { $0.lastActivity > $1.lastActivity }
        result.generatingCount = result.agents.filter { $0.phase == .generating }.count
        result.toolsCount = result.agents.filter { $0.phase == .tools }.count
        result.sources = Dictionary(grouping: snapshots.flatMap(\.sources), by: \.kind).map { _, group in
            var source = group[0]
            source.rootPath = ""; source.available = group.contains { $0.available }; source.enabled = group.contains { $0.enabled }
            source.sessionCount = group.reduce(0) { $0 + $1.sessionCount }; source.activeAgents = group.reduce(0) { $0 + $1.activeAgents }
            source.live = group.reduce(.zero) { add($0, $1.live) }; source.liveEstimated = group.reduce(.zero) { add($0, $1.liveEstimated) }
            source.today = group.reduce(.zero) { add($0, $1.today) }
            source.liveCost = group.reduce(.zero) { var sum = $0; sum.add($1.liveCost); return sum }
            source.todayCost = group.reduce(.zero) { var sum = $0; sum.add($1.todayCost); return sum }
            source.configuredModes = ExecutionModes(group.flatMap { $0.configuredModes.facts })
            source.unpricedModels = Array(Set(group.flatMap(\.unpricedModels))).sorted()
            return source
        }.sorted { $0.kind.rawValue < $1.kind.rawValue }
        result.history = aligned(snapshots.flatMap(\.history))
        result.peakToday = peak; result.peakTodayAt = nil; result.averageToday = nil; result.subsetPeaks = [:]
        result.report = .empty
        result.lastResponse = snapshots.compactMap(\.lastResponse).max { $0.endedAt < $1.endedAt }
        result.warnings = snapshots.flatMap(\.warnings)
        return result
    }
    static func aligned(_ points: [HistoryPoint]) -> [HistoryPoint] {
        Dictionary(grouping: points) { Int64($0.time.timeIntervalSince1970.rounded(.down)) }.map { second, rows in
            HistoryPoint(time: Date(timeIntervalSince1970: Double(second)), measured: rows.reduce(.zero) { add($0, $1.measured) },
                estimated: rows.reduce(.zero) { add($0, $1.estimated) })
        }.sorted { $0.time < $1.time }.suffix(300).map { $0 }
    }
    static func peak(_ points: [HistoryPoint]) -> Rate {
        let buckets = Dictionary(uniqueKeysWithValues: aligned(points).map { (Int64($0.time.timeIntervalSince1970), $0.measured) })
        var peak = Rate.zero
        for time in Set(buckets.keys.flatMap { [$0, $0 + 1, $0 + 2] }) {
            let sum = (0..<3).reduce(Rate.zero) { add($0, buckets[time - Int64($1)] ?? .zero) }
            peak.output = max(peak.output, sum.output / 3); peak.total = max(peak.total, sum.total / 3)
        }
        return peak
    }
    static func idle(_ snapshot: SpeedSnapshot, now: Date) -> SpeedSnapshot {
        var s = snapshot
        let sameDay = Calendar.current.isDate(s.dayStart, inSameDayAs: now)
        s.timestamp = now; s.live = .zero; s.liveCost = .zero; s.liveEstimatedShare = 0
        s.agents = []; s.generatingCount = 0; s.toolsCount = 0; s.lastResponse = nil
        if !sameDay { s.today = .zero; s.todayCost = .zero; s.peakToday = .zero }
        s.sources = s.sources.map { source in
            var copy = source; copy.live = .zero; copy.liveEstimated = .zero; copy.liveCost = .zero; copy.activeAgents = 0
            if !sameDay { copy.today = .zero; copy.todayCost = .zero }
            return copy
        }
        return s
    }
    static func filter(_ snapshot: SpeedSnapshot, enabled: Set<SourceKind>) -> SpeedSnapshot {
        guard snapshot.sources.contains(where: { $0.enabled && !enabled.contains($0.kind) }) else { return snapshot }
        var s = snapshot
        s.sources = s.sources.filter { enabled.contains($0.kind) }; s.agents = s.agents.filter { enabled.contains($0.source) }
        s.live = s.sources.reduce(.zero) { add($0, $1.live) }; s.today = s.sources.reduce(.zero) { add($0, $1.today) }
        s.liveCost = s.sources.reduce(.zero) { var sum = $0; sum.add($1.liveCost); return sum }
        s.todayCost = s.sources.reduce(.zero) { var sum = $0; sum.add($1.todayCost); return sum }
        s.liveEstimatedShare = s.live.output > 0 ? s.sources.reduce(0) { $0 + $1.liveEstimated.output } / s.live.output : 0
        s.generatingCount = s.agents.filter { $0.phase == .generating }.count; s.toolsCount = s.agents.filter { $0.phase == .tools }.count
        s.sourceHistory = s.sourceHistory.filter { enabled.contains($0.key) }
        s.history = []; s.peakToday = .zero; s.subsetPeaks = [:]
        return s
    }
}
