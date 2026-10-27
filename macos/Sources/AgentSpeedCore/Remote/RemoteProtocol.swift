import Foundation

enum RemoteError: Error { case invalidFrame, connectionClosed, staleClock }

struct RemoteFrame: Codable, Sendable {
    static let maxLineBytes = 4 * 1024 * 1024
    static let maxHistoryBytes = 64 * 1024 * 1024
    var schema: Int
    var type: String
    var timestampMs: Int64
    var snapshot: RemoteSnapshot?
    var days: [RemoteDay]?
    var timeZone: String?
    var generation: String?
    var part: Int
    var parts: Int
    // Excluded from the wire; keeps multi-part staging bounded before the generation is complete.
    var wireBytes = 0
    private enum CodingKeys: String, CodingKey {
        case schema, type, timestampMs, snapshot, days, timeZone, generation, part, parts
    }

    static func decode(_ data: Data) throws -> Self {
        guard data.count <= maxLineBytes else { throw RemoteError.invalidFrame }
        var frame = try JSONDecoder().decode(Self.self, from: data)
        guard frame.schema == 1, validTime(frame.timestampMs), (1...1024).contains(frame.parts),
              (0..<frame.parts).contains(frame.part),
              (frame.type == "snapshot" && frame.snapshot != nil && frame.days == nil
                || frame.type == "history" && frame.days != nil && frame.snapshot == nil),
              frame.snapshot?.isValid ?? true, frame.days.map({ $0.count <= 100_000 && $0.allSatisfy(\.isValid) }) ?? true
        else { throw RemoteError.invalidFrame }
        frame.wireBytes = data.count
        return frame
    }
    static func validTime(_ value: Int64) -> Bool { (0...253_402_300_799_999).contains(value) }
    static func validDay(_ value: String) -> Bool {
        guard value.utf8.count == 10, value.range(of: #"^\d{4}-\d{2}-\d{2}$"#, options: .regularExpression) != nil else { return false }
        let parts = value.split(separator: "-").compactMap { Int($0) }
        guard parts.count == 3, (1...9999).contains(parts[0]), (1...12).contains(parts[1]) else { return false }
        let leap = parts[0] % 4 == 0 && (parts[0] % 100 != 0 || parts[0] % 400 == 0)
        let days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31]
        return (1...days[parts[1] - 1]).contains(parts[2])
    }
    static func date(_ value: Int64) -> Date { Date(timeIntervalSince1970: Double(value) / 1000) }
    static func valid(_ t: TokenTotals) -> Bool {
        [t.requests, t.input, t.output, t.cachedInput, t.cacheWriteInput, t.reasoning].allSatisfy { (0...(1 << 40)).contains($0) }
    }
    static func count(_ n: Int64) -> Bool { (0...(1 << 40)).contains(n) }
    static func number(_ n: Double?) -> Bool { n.map { $0.isFinite && (0...1e15).contains($0) } ?? true }
    static func identifier(_ s: String, max: Int) -> Bool { ExecutionModeFact.isIdentifier(s, limit: max) }
    static func text(_ s: String) -> Bool { s.utf8.count <= 512 && !s.unicodeScalars.contains { CharacterSet.controlCharacters.contains($0) } }
    static func valid(_ c: CostTally) -> Bool { count(c.costMicroUsd) && count(Int64(c.unpricedRequests)) }
    static func valid(_ c: LiveCost) -> Bool { number(c.microUsdPerSecond) && number(c.knownOutput) }
    static func valid(_ r: Rate) -> Bool { r.output.isFinite && r.total.isFinite && (0...1e15).contains(r.output) && (0...1e15).contains(r.total) }
}

struct RemoteMode: Codable, Sendable {
    var kind: Int; var value: String; var rawValue: String; var evidence: Int; var provider: String?; var scope: Int
    var isValid: Bool { fact != nil && RemoteFrame.identifier(value, max: 64) }
    var fact: ExecutionModeFact? {
        let kinds = ExecutionModeKind.allCases
        let evidences: [ExecutionModeEvidence] = [.configured, .selected, .recorded, .served]
        let scopes: [ExecutionModeScope] = [.configuration, .session, .turn, .request, .response]
        guard kinds.indices.contains(kind), evidences.indices.contains(evidence), scopes.indices.contains(scope) else { return nil }
        return ExecutionModeFact(kind: kinds[kind], rawValue: rawValue, evidence: evidences[evidence], provider: provider,
                                 scope: scopes[scope])?.withCanonicalValue(value)
    }
}
struct RemoteModeUsage: Codable, Sendable {
    var model: String; var kind: Int; var mode: String; var evidence: String; var provider: String?
    var requests: Int; var output: Int; var measuredOutput: Int; var measuredMs: Int64
    var isValid: Bool {
        ExecutionModeKind.allCases.indices.contains(kind) && RemoteFrame.text(model) && RemoteFrame.text(evidence)
            && RemoteFrame.identifier(mode, max: 64) && (provider.map { RemoteFrame.identifier($0, max: 32) } ?? true)
            && [requests, output, measuredOutput].allSatisfy { RemoteFrame.count(Int64($0)) } && RemoteFrame.count(measuredMs)
    }
    var usage: ExecutionModeUsage? {
        guard ExecutionModeKind.allCases.indices.contains(kind) else { return nil }
        return ExecutionModeUsage(model: model, attribution: ExecutionModeAttribution(kind: ExecutionModeKind.allCases[kind], mode: mode,
            evidence: evidence, provider: provider), requests: requests, output: output, measuredOutput: measuredOutput, measuredMs: measuredMs)
    }
}
struct RemoteAgent: Codable, Sendable {
    var id: String; var source: Int; var model: String?; var phase: Int; var isSubagent: Bool
    var live: Rate; var recentSpeed: Double?; var lastActivityMs: Int64; var parentId: String?
    var liveCost: LiveCost; var modeFacts: [RemoteMode]
    var isValid: Bool {
        RemoteHost.isID(id) && SourceKind.allCases.indices.contains(source) && (0...2).contains(phase)
            && RemoteFrame.valid(live) && RemoteFrame.validTime(lastActivityMs) && modeFacts.count <= 20
            && modeFacts.allSatisfy(\.isValid) && RemoteFrame.valid(liveCost) && RemoteFrame.number(recentSpeed)
            && (model.map(RemoteFrame.text) ?? true) && (parentId.map(RemoteHost.isID) ?? true)
    }
    func agent(host: RemoteHost) -> AgentStatus {
        AgentStatus(id: host.id + "/" + id, source: SourceKind.allCases[source], project: host.name, model: model,
            phase: [.generating, .tools, .idle][phase], isSubagent: isSubagent, live: live, recentSpeed: recentSpeed,
            lastActivity: RemoteFrame.date(lastActivityMs), parentId: parentId.map { host.id + "/" + $0 },
            liveCost: liveCost, modes: ExecutionModes(modeFacts.compactMap(\.fact)))
    }
}
struct RemoteSource: Codable, Sendable {
    var kind: Int; var enabled: Bool; var available: Bool; var sessionCount: Int; var live: Rate; var today: TokenTotals
    var activeAgents: Int; var liveEstimated: Rate; var liveCost: LiveCost; var todayCost: CostTally
    var modeFacts: [RemoteMode]
    var isValid: Bool {
        SourceKind.allCases.indices.contains(kind) && (0...1_000_000).contains(sessionCount) && (0...10_000).contains(activeAgents) && RemoteFrame.valid(live)
            && RemoteFrame.valid(liveEstimated) && RemoteFrame.valid(today) && modeFacts.count <= 20
            && modeFacts.allSatisfy(\.isValid) && RemoteFrame.valid(liveCost) && RemoteFrame.valid(todayCost)
    }
    var status: SourceStatus {
        SourceStatus(kind: SourceKind.allCases[kind], enabled: enabled, rootPath: "", available: available, sessionCount: sessionCount,
            live: live, today: today, activeAgents: activeAgents, liveEstimated: liveEstimated, liveCost: liveCost,
            todayCost: todayCost, configuredModes: ExecutionModes(modeFacts.compactMap(\.fact)))
    }
}
struct RemotePoint: Codable, Sendable {
    var timeMs: Int64; var measured: Rate; var estimated: Rate
    var isValid: Bool { RemoteFrame.validTime(timeMs) && RemoteFrame.valid(measured) && RemoteFrame.valid(estimated) }
    var point: HistoryPoint { HistoryPoint(time: RemoteFrame.date(timeMs), measured: measured, estimated: estimated) }
}
struct RemoteSourceHistory: Codable, Sendable {
    var source: Int; var points: [RemotePoint]
    var isValid: Bool { SourceKind.allCases.indices.contains(source) && points.count <= 300 && points.allSatisfy(\.isValid) }
}
struct RemoteState: Codable, Sendable { var kind: Int; var progress: Double }
struct RemoteSnapshot: Codable, Sendable {
    var dayStartMs: Int64; var state: RemoteState; var live: Rate; var estimatedShare: Double; var peak: Rate; var average: Double?
    var today: TokenTotals; var sources: [RemoteSource]; var agents: [RemoteAgent]; var history: [RemotePoint]
    var liveCost: LiveCost; var todayCost: CostTally
    var sourceHistory: [RemoteSourceHistory]?
    var isValid: Bool {
        RemoteFrame.validTime(dayStartMs) && (0...2).contains(state.kind) && RemoteFrame.valid(live) && RemoteFrame.valid(peak)
            && state.progress.isFinite && (0...1).contains(state.progress) && RemoteFrame.number(average)
            && RemoteFrame.valid(liveCost) && RemoteFrame.valid(todayCost)
            && (sourceHistory.map { $0.count <= 32 && $0.allSatisfy(\.isValid) && Set($0.map(\.source)).count == $0.count } ?? true)
            && estimatedShare.isFinite && (0...1).contains(estimatedShare) && RemoteFrame.valid(today)
            && sources.count <= 32 && sources.allSatisfy(\.isValid) && Set(sources.map(\.kind)).count == sources.count
            && agents.count <= 10_000 && agents.allSatisfy(\.isValid) && Set(agents.map(\.id)).count == agents.count
            && history.count <= 300 && history.allSatisfy { RemoteFrame.validTime($0.timeMs) && RemoteFrame.valid($0.measured) && RemoteFrame.valid($0.estimated) }
    }
    func speed(timestamp: Int64, host: RemoteHost) -> SpeedSnapshot {
        let agentList = agents.map { $0.agent(host: host) }
        var result = SpeedSnapshot(timestamp: RemoteFrame.date(timestamp), state: state.kind == 0 ? .starting(progress: state.progress)
            : state.kind == 1 ? .running : .noSources, live: live, liveEstimatedShare: estimatedShare, peakToday: peak,
            peakTodayAt: nil, averageToday: average, today: today, sources: sources.map(\.status), agents: agentList,
            generatingCount: agentList.filter { $0.phase == .generating }.count, toolsCount: agentList.filter { $0.phase == .tools }.count,
            history: history.map(\.point), lastResponse: nil, warnings: [], dayStart: RemoteFrame.date(dayStartMs),
            liveCost: liveCost, todayCost: todayCost)
        result.sourceHistory = Dictionary(uniqueKeysWithValues: (sourceHistory ?? []).map { (SourceKind.allCases[$0.source], $0.points.map(\.point)) })
        return result
    }
}
struct RemoteModel: Codable, Sendable {
    var model: String; var requests: Int; var output: Int; var measuredOutput: Int; var measuredMs: Int64; var cost: CostTally?
    var isValid: Bool {
        RemoteFrame.text(model) && [requests, output, measuredOutput].allSatisfy { RemoteFrame.count(Int64($0)) }
            && RemoteFrame.count(measuredMs) && (cost.map(RemoteFrame.valid) ?? true)
    }
    var usage: ModelUsage { ModelUsage(model: model, requests: requests, output: output, measuredOutput: measuredOutput, measuredMs: measuredMs, cost: cost) }
}
struct RemoteDay: Codable, Sendable {
    var day: String; var source: Int; var totals: TokenTotals; var measuredOutput: Int; var measuredMs: Int64
    var unfinalized: Int; var agents: Int; var peak: Double?; var models: [RemoteModel]; var cost: CostTally?; var priceVersion: Int?
    var modes: [RemoteModeUsage]
    var isValid: Bool {
        RemoteFrame.validDay(day)
            && SourceKind.allCases.indices.contains(source) && RemoteFrame.valid(totals)
            && RemoteFrame.count(Int64(measuredOutput)) && RemoteFrame.count(measuredMs) && RemoteFrame.count(Int64(unfinalized))
            && (0...1_000_000).contains(agents) && RemoteFrame.number(peak) && (cost.map(RemoteFrame.valid) ?? true)
            && models.count <= 2000 && models.allSatisfy(\.isValid) && Set(models.map(\.model)).count == models.count
            && modes.count <= 20_000 && modes.allSatisfy(\.isValid)
    }
    var usage: DailyUsage {
        DailyUsage(day: day, source: SourceKind.allCases[source], totals: totals, measuredOutput: measuredOutput, measuredMs: measuredMs,
            unfinalized: unfinalized, agents: agents, peak: peak, models: models.map(\.usage), cost: cost, priceVersion: priceVersion,
            modeUsage: modes.compactMap(\.usage))
    }
}
