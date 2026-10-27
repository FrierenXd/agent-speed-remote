import Foundation
import Observation

@MainActor
private final class RemoteHostState {
    let host: RemoteHost
    var stream: SSHStream?
    var snapshot = SpeedSnapshot.empty
    var days: [RemoteDay] = []
    var received = 0.0
    var status = "connecting"
    var generation: String?
    var parts: [Int: [RemoteDay]] = [:]
    var partBytes: [Int: Int] = [:]
    var partCount = 0
    var historyLoaded = false
    var revision = 0
    let cacheURL: URL
    let timeZone: String
    init(_ host: RemoteHost, cacheURL: URL, timeZone: String) {
        self.host = host; self.cacheURL = cacheURL; self.timeZone = timeZone
    }
}

@MainActor
@Observable
public final class RemoteMonitor {
    public private(set) var statuses: [String: String] = [:]
    @ObservationIgnored private let local: SpeedStore
    @ObservationIgnored private let localHistory: HistoryStore
    @ObservationIgnored private let output: SpeedStore
    @ObservationIgnored private let history: HistoryStore
    @ObservationIgnored private let cacheDirectory: URL
    @ObservationIgnored private var hosts: [String: RemoteHostState] = [:]
    @ObservationIgnored private var settings = RemoteSettings()
    @ObservationIgnored private var enabled = Set(SourceKind.allCases)
    @ObservationIgnored private var networkEnabled = false
    @ObservationIgnored private var timeZone = TimeZone.current.identifier
    @ObservationIgnored private let cacheIO = DispatchQueue(label: "app.agentspeed.remote-cache", qos: .utility)
    @ObservationIgnored private var subsets: Set<SourceSet> = []
    @ObservationIgnored private var subsetPeaks: [SourceSet: Double] = [:]
    @ObservationIgnored private var peak = Rate.zero
    @ObservationIgnored private var peakDay = Date.distantPast
    @ObservationIgnored private var timer: Timer?
    @ObservationIgnored private var liveToken: UUID?
    @ObservationIgnored private var eventToken: UUID?
    @ObservationIgnored private var historyToken: UUID?

    public init(local: SpeedStore, localHistory: HistoryStore, output: SpeedStore, history: HistoryStore, cacheDirectory: URL) {
        self.local = local; self.localHistory = localHistory; self.output = output; self.history = history; self.cacheDirectory = cacheDirectory
        liveToken = local.addLiveListener { [weak self] _ in self?.publish() }
        eventToken = local.addEventListener { [weak self] event in
            guard let self, self.settings.selection == "local" else { return }
            self.output.publishLive(self.local.liveSnapshot, events: [event])
        }
        historyToken = localHistory.addListener { [weak self] in self?.publishHistory() }
        history.refreshHandler = { [weak self] showsProgress in self?.refresh(showsProgress: showsProgress) }
        history.clearHandler = { [weak self] in self?.clear() }
        let timer = Timer(timeInterval: 1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        RunLoop.main.add(timer, forMode: .common); self.timer = timer
    }

    public func update(settings: RemoteSettings, networkEnabled: Bool, enabled: Set<SourceKind>) {
        guard settings.isValid else { return }
        let restart = self.networkEnabled != networkEnabled || self.enabled != enabled || timeZone != TimeZone.current.identifier
        if settings.selection != self.settings.selection || enabled != self.enabled || settings.hosts != self.settings.hosts { peak = .zero; subsetPeaks = [:] }
        self.settings = settings; self.networkEnabled = networkEnabled; self.enabled = enabled; timeZone = TimeZone.current.identifier
        for (id, state) in Array(hosts) where restart || !settings.hosts.contains(where: { $0.enabled && $0 == state.host }) {
            state.stream?.stop(); hosts[id] = nil
        }
        for host in settings.hosts where host.enabled && hosts[host.id] == nil {
            let state = RemoteHostState(host, cacheURL: cacheURL(host), timeZone: timeZone); hosts[host.id] = state
            loadCache(state)
            if networkEnabled {
                let stream = SSHStream(host: host, timeZone: timeZone, sources: enabled, receive: { [weak self, weak state] frame in
                    Task { @MainActor in
                        guard let self, let state, self.hosts[host.id] === state else { return }
                        self.receive(state, frame: frame)
                    }
                }, status: { [weak self, weak state] status in
                    Task { @MainActor in
                        guard let self, let state, self.hosts[host.id] === state else { return }
                        state.status = status; self.updateStatuses()
                    }
                })
                state.stream = stream; stream.start()
            } else { state.status = "disabled" }
        }
        updateStatuses(); publish(); publishHistory()
    }
    private func receive(_ state: RemoteHostState, frame: RemoteFrame) {
        if let snapshot = frame.snapshot {
            state.snapshot = snapshot.speed(timestamp: frame.timestampMs, host: state.host)
            state.received = ProcessInfo.processInfo.systemUptime; state.status = "connected"
            publish()
        }
        guard let days = frame.days else { return }
        if frame.generation != state.generation {
            state.generation = frame.generation; state.parts = [:]; state.partBytes = [:]; state.partCount = frame.parts
        }
        guard frame.parts == state.partCount else { return }
        let bytes = frame.wireBytes > 0 ? frame.wireBytes : ((try? JSONEncoder().encode(frame).count) ?? RemoteFrame.maxHistoryBytes)
        guard state.partBytes.values.reduce(0, +) - (state.partBytes[frame.part] ?? 0) + bytes <= RemoteFrame.maxHistoryBytes,
              state.parts.values.reduce(0, { $0 + $1.count }) - (state.parts[frame.part]?.count ?? 0) + days.count <= 100_000 else {
            state.parts = [:]; state.partBytes = [:]; state.status = "protocolError"; updateStatuses(); return
        }
        state.parts[frame.part] = days
        state.partBytes[frame.part] = bytes
        guard state.parts.count == state.partCount else { return }
        let complete = state.parts.sorted { $0.key < $1.key }.flatMap(\.value)
        guard complete.count <= 100_000, Set(complete.map { "\($0.day)/\($0.source)" }).count == complete.count else { return }
        state.days = complete; state.parts = [:]; state.partBytes = [:]; state.historyLoaded = true; state.revision += 1
        saveCache(state); publishHistory()
    }
    private var includesLocal: Bool { ["local", "all"].contains(settings.selection) }
    private var selectedHosts: [RemoteHostState] { hosts.values.filter { settings.selection == "all" || settings.selection == $0.host.id } }
    private var selectedDays: [DailyUsage] {
        ((includesLocal ? localHistory.days : []) + selectedHosts.flatMap { $0.days.map(\.usage) }).filter { enabled.contains($0.source) }
    }
    private func tick() {
        if timeZone != TimeZone.current.identifier { update(settings: settings, networkEnabled: networkEnabled, enabled: enabled) }
        for state in hosts.values where state.status == "connected" && ProcessInfo.processInfo.systemUptime - state.received > 5 { state.status = "stale" }
        updateStatuses()
        if settings.selection != "local" { publish() }
    }
    private func updateStatuses() { statuses = hosts.mapValues(\.status) }
    private func publish() {
        guard settings.selection != "local" else { output.publishLive(local.liveSnapshot); return }
        let now = Date()
        if !Calendar.current.isDate(peakDay, inSameDayAs: now) { peakDay = now; peak = .zero; subsetPeaks = [:] }
        var snapshots = includesLocal ? [local.liveSnapshot] : []
        for state in selectedHosts {
            var snapshot = state.snapshot
            if state.status != "connected" || ProcessInfo.processInfo.systemUptime - state.received > 5 {
                snapshot = RemoteAggregation.idle(snapshot, now: now)
            }
            snapshots.append(RemoteAggregation.filter(snapshot, enabled: enabled))
        }
        let observed = RemoteAggregation.peak(snapshots.flatMap(\.history).filter { Calendar.current.isDate($0.time, inSameDayAs: now) })
        peak.output = max(peak.output, observed.output); peak.total = max(peak.total, observed.total)
        var result = RemoteAggregation.snapshot(snapshots, now: now, peak: peak)
        for set in subsets {
            let points = snapshots.flatMap { $0.sourceHistory.filter { set.contains($0.key) }.flatMap(\.value) }
                .filter { Calendar.current.isDate($0.time, inSameDayAs: now) }
            subsetPeaks[set] = max(subsetPeaks[set] ?? 0, RemoteAggregation.peak(points).output)
        }
        result.subsetPeaks = subsetPeaks
        let label = DayLabeler(timeZone: .current).label(for: now)
        let today = selectedDays.filter { $0.day == label }
        let measuredMs = today.reduce(Int64(0)) { $0 + $1.measuredMs }
        if measuredMs > 0 { result.averageToday = Double(today.reduce(0) { $0.saturatingAdd($1.measuredOutput) }) / (Double(measuredMs) / 1000) }
        output.publishLive(result)
    }
    private func publishHistory() {
        if settings.selection == "local" { history.publish(state: localHistory.state, days: localHistory.days); return }
        let days = RemoteAggregation.days(selectedDays)
        var localReady = false
        if case .ready = localHistory.state { localReady = true }
        let loaded = !days.isEmpty || selectedHosts.contains { $0.historyLoaded } || (includesLocal && localReady)
        history.publish(state: loaded ? .ready(updatedAt: Date()) : .notLoaded, days: days)
    }
    public func refresh(showsProgress: Bool = true) {
        localHistory.refresh(showsProgress: showsProgress)
        for state in hosts.values { state.stream?.stop() }
        hosts = [:]
        update(settings: settings, networkEnabled: networkEnabled, enabled: enabled)
    }
    public func clear() {
        for state in hosts.values {
            state.days = []; state.historyLoaded = false; state.revision += 1
        }
        let directory = cacheDirectory
        cacheIO.async {
            let files = (try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)) ?? []
            for file in files where file.pathExtension == "ndjson" { try? FileManager.default.removeItem(at: file) }
        }
        localHistory.clear(); publishHistory()
    }
    public func stop() {
        timer?.invalidate(); timer = nil
        for state in hosts.values { state.stream?.stop() }; hosts = [:]
        if let liveToken { local.removeLiveListener(liveToken) }; if let eventToken { local.removeEventListener(eventToken) }
        if let historyToken { localHistory.removeListener(historyToken) }
        history.refreshHandler = nil; history.clearHandler = nil
    }
    public func setSubsets(_ sets: Set<SourceSet>) { subsets = sets; publish() }
    private func cacheURL(_ host: RemoteHost) -> URL {
        let key = ReportHash.sha256Hex(Data((timeZone + "\n" + host.destination + "\n" + host.executable + "\n" + (host.shell ?? "posix")).utf8))
        return cacheDirectory.appendingPathComponent(host.id + "-" + key + ".ndjson")
    }
    private func loadCache(_ state: RemoteHostState) {
        let url = state.cacheURL, zone = state.timeZone, revision = state.revision, io = cacheIO
        Task { [weak self, weak state] in
            let days: [RemoteDay]? = await withCheckedContinuation { continuation in
                io.async {
                    let result: [RemoteDay]? = {
                        guard let size = try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize,
                              size <= 64 * 1024 * 1024, let data = try? Data(contentsOf: url) else { return nil }
                        var result: [RemoteDay] = []
                        for line in data.split(separator: 10) {
                            guard let frame = try? RemoteFrame.decode(Data(line)), frame.type == "history",
                                  frame.timeZone == zone else { return nil }
                            result += frame.days ?? []
                            if result.count > 100_000 { return nil }
                        }
                        return Set(result.map { "\($0.day)/\($0.source)" }).count == result.count ? result : nil
                    }()
                    continuation.resume(returning: result)
                }
            }
            guard let self, let state, self.hosts[state.host.id] === state,
                  !state.historyLoaded, state.revision == revision, let days else { return }
            state.days = days; state.historyLoaded = true; self.publishHistory()
        }
    }
    private func saveCache(_ state: RemoteHostState) {
        let url = state.cacheURL, zone = state.timeZone, days = state.days
        cacheIO.async {
            do {
                var data = Data()
                for start in stride(from: 0, to: days.count, by: 64) {
                    let frame = RemoteFrame(schema: 1, type: "history", timestampMs: Int64(Date().timeIntervalSince1970 * 1000),
                        days: Array(days[start..<min(start + 64, days.count)]), timeZone: zone, part: 0, parts: 1)
                    data.append(try JSONEncoder().encode(frame)); data.append(10)
                }
                try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
                try data.write(to: url, options: .atomic)
            } catch { }
        }
    }
}
