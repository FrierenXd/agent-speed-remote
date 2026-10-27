import Foundation
import Testing
@testable import AgentSpeedCore

@Suite("Remote host statistics")
struct RemoteTests {
    @Test func deploymentPlatformParsing() throws {
        for rid in RemoteDeployment.runtimes {
            #expect(try RemoteDeployment.parseProbe("AGENTSPEED_DEPLOY_V1\r\n\(rid)\r\n") == rid)
        }
        #expect(throws: (any Error).self) { try RemoteDeployment.parseProbe("welcome\nAGENTSPEED_DEPLOY_V1\nlinux-x64\n") }
        #expect(throws: (any Error).self) { try RemoteDeployment.parseProbe("AGENTSPEED_DEPLOY_V1\nlinux-musl-x64\n") }
    }

    @Test func deploymentRequiresUnambiguousChecksumsAndAbsolutePaths() throws {
        let hash = String(repeating: "a", count: 64), file = "AgentSpeed-Collector-linux-x64.tar.gz"
        let line = "\(hash)  \(file)\n"
        #expect(try RemoteDeployment.parseHash(line, file: file) == hash)
        #expect(throws: (any Error).self) { try RemoteDeployment.parseHash(line + line, file: file) }
        #expect(try RemoteDeployment.parseInstalled("AGENTSPEED_INSTALLED_V1\n/home/a b/collector\n", shell: "posix") == "/home/a b/collector")
        #expect(throws: (any Error).self) { try RemoteDeployment.parseInstalled("AGENTSPEED_INSTALLED_V1\n../collector\n", shell: "posix") }
        #expect(throws: (any Error).self) { try RemoteDeployment.parseInstalled("AGENTSPEED_INSTALLED_V1\nC:collector.exe\n", shell: "powershell") }
    }

    @Test func deploymentUsesSharedScriptsAndEncodedPowerShell() throws {
        let hash = String(repeating: "a", count: 64)
        let posix = try RemoteDeployment.command(action: "install", shell: "posix", hash: hash)
        #expect(posix.hasPrefix("sh -c '"))
        #expect(posix.contains(hash))
        let windows = try RemoteDeployment.command(action: "install", shell: "powershell", hash: hash)
        let data = try #require(Data(base64Encoded: String(windows.split(separator: " ").last!)))
        let script = try #require(String(data: data, encoding: .utf16LittleEndian))
        #expect(script.contains(hash))
        #expect(script.contains("OpenStandardInput"))
        #expect(!script.contains("__SHA256__"))
        #expect(throws: (any Error).self) { try RemoteDeployment.command(action: "install", shell: "posix", hash: "'; bad") }
    }

    private func frames() throws -> [RemoteFrame] {
        let data = try Data(contentsOf: Fixtures.root.appendingPathComponent("remote/stream.ndjson"))
        return try data.split(separator: 10).map { try RemoteFrame.decode(Data($0)) }
    }

    @Test func sharedWireFixture() throws {
        let frames = try frames()
        let remote = try #require(frames[0].snapshot)
        let snapshot = remote.speed(timestamp: frames[0].timestampMs,
            host: RemoteHost(id: "server", name: "Build Server", destination: "build"))
        #expect(snapshot.live.output == 50)
        #expect(snapshot.today.output == 100)
        #expect(snapshot.todayCost.costMicroUsd == 1200)
        #expect(snapshot.agents.first?.project == "Build Server")
        #expect(snapshot.agents.first?.id.hasPrefix("server/") == true)
        #expect(snapshot.agents.first?.modes.facts.first?.value == "fast")
        #expect(frames[1].days?.first?.usage.modeUsage.count == 1)
        for frame in frames {
            let copy = try RemoteFrame.decode(JSONEncoder().encode(frame))
            #expect(copy.timestampMs == frame.timestampMs)
            #expect(copy.type == frame.type)
        }
    }

    @Test func aggregateDoesNotAddIndependentPeaks() throws {
        let frame = try #require(try frames().first)
        let snapshot = try #require(frame.snapshot).speed(timestamp: frame.timestampMs,
            host: RemoteHost(id: "one", destination: "one"))
        var other = snapshot
        other.live = Rate(output: 150, total: 1500)
        other.liveEstimatedShare = 0.6
        let result = RemoteAggregation.snapshot([snapshot, other], now: snapshot.timestamp,
            peak: Rate(output: 60, total: 600))
        #expect(result.live.output == 200)
        #expect(result.liveEstimatedShare == 0.5)
        #expect(result.today.output == 200)
        #expect(result.peakToday.output == 60)
        let day = try #require(try frames()[1].days?.first?.usage)
        let history = RemoteAggregation.days([day, day])
        #expect(history.first?.totals.output == 200)
        #expect(history.first?.peak == nil)
    }

    @Test func disjointMeasuredBucketsDoNotInflatePeak() {
        let time = Date(timeIntervalSince1970: 1_792_490_400)
        let points = [HistoryPoint(time: time, measured: Rate(output: 90, total: 900), estimated: .zero),
                      HistoryPoint(time: time.addingTimeInterval(10), measured: Rate(output: 60, total: 600), estimated: .zero)]
        #expect(RemoteAggregation.peak(points).output == 30)
    }

    @Test func validatesCalendarDates() {
        #expect(RemoteFrame.validDay("2024-02-29"))
        #expect(!RemoteFrame.validDay("2026-02-29"))
        #expect(!RemoteFrame.validDay("2026-13-01"))
        #expect(!RemoteFrame.validDay("2026-04-31"))
    }

    @Test func mixedHistoricalCostsKeepKnownAmounts() throws {
        let day = try #require(try frames()[1].days?.first?.usage)
        var unknown = day
        unknown.cost = nil; unknown.priceVersion = nil
        unknown.models = unknown.models.map { var row = $0; row.cost = nil; return row }
        let result = try #require(RemoteAggregation.days([day, unknown]).first)
        #expect(result.cost?.costMicroUsd == 1200)
        #expect(result.cost?.unpricedRequests == 2)
        #expect(result.priceVersion == nil)
    }

    @Test func validatesUntrustedCountersAndModes() throws {
        var frame = try #require(try frames().first)
        frame.snapshot?.today.output = -1
        #expect(throws: (any Error).self) { try RemoteFrame.decode(JSONEncoder().encode(frame)) }
        frame = try #require(try frames().first)
        frame.snapshot?.todayCost.costMicroUsd = -1
        #expect(throws: (any Error).self) { try RemoteFrame.decode(JSONEncoder().encode(frame)) }
        frame = try #require(try frames().first)
        frame.snapshot?.agents[0].modeFacts[0].kind = 999
        #expect(throws: (any Error).self) { try RemoteFrame.decode(JSONEncoder().encode(frame)) }
    }

    @Test func rejectsDuplicateSourceBuckets() throws {
        var frame = try #require(try frames().first)
        let points = try #require(frame.snapshot?.history)
        frame.snapshot?.sourceHistory = [RemoteSourceHistory(source: 0, points: points), RemoteSourceHistory(source: 0, points: points)]
        #expect(throws: (any Error).self) { try RemoteFrame.decode(JSONEncoder().encode(frame)) }
    }

    @Test func sourceBucketsAreDecoded() throws {
        var frame = try #require(try frames().first)
        let points = try #require(frame.snapshot?.history)
        frame.snapshot?.sourceHistory = [RemoteSourceHistory(source: 0, points: points)]
        let restored = try RemoteFrame.decode(JSONEncoder().encode(frame))
        let snapshot = try #require(restored.snapshot).speed(timestamp: frame.timestampMs,
            host: RemoteHost(id: "server", destination: "server"))
        #expect(snapshot.sourceHistory[.codex]?.first?.measured.output == 40)
    }

    @Test func staleSnapshotRetainsTotalsButNotLiveSpeed() throws {
        let frame = try #require(try frames().first)
        let snapshot = try #require(frame.snapshot).speed(timestamp: frame.timestampMs,
            host: RemoteHost(id: "server", destination: "server"))
        let stale = RemoteAggregation.idle(snapshot, now: snapshot.timestamp)
        #expect(stale.live == .zero)
        #expect(stale.agents.isEmpty)
        #expect(stale.today.output == 100)
        let tomorrow = RemoteAggregation.idle(snapshot, now: snapshot.timestamp.addingTimeInterval(86_400))
        #expect(tomorrow.today.output == 0)
    }

    @Test func sshUsesStrictHostVerificationAndLiteralArguments() {
        let host = RemoteHost(id: "server", destination: "alice@server", executable: "/opt/it's safe/collector")
        #expect(host.isValid)
        let arguments = host.arguments(timeZone: "UTC", sources: [.codex])
        #expect(arguments.contains("BatchMode=yes"))
        #expect(arguments.contains("StrictHostKeyChecking=yes"))
        #expect(arguments.last?.contains("'\\''") == true)
        #expect(arguments.last?.hasSuffix("--sources '0'") == true)
        #expect(!RemoteHost(destination: "-oProxyCommand=bad").isValid)
        #expect(!RemoteHost(destination: "server; touch bad").isValid)
        #expect(!RemoteSettings(hosts: [host, host]).isValid)
    }

    @MainActor @Test func networkOffLeavesLocalStoreIndependent() {
        let local = SpeedStore(), output = SpeedStore()
        let localHistory = HistoryStore(), history = HistoryStore()
        let monitor = RemoteMonitor(local: local, localHistory: localHistory, output: output, history: history,
            cacheDirectory: FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString))
        defer { monitor.stop() }
        monitor.update(settings: RemoteSettings(hosts: [RemoteHost(id: "server", destination: "invalid.example")], selection: "all"),
            networkEnabled: false, enabled: Set(SourceKind.allCases))
        #expect(monitor.statuses["server"] == "disabled")
        var snapshot = SpeedSnapshot.empty
        snapshot.today.output = 123
        local.publishLive(snapshot)
        #expect(output.liveSnapshot.today.output == 123)
        monitor.update(settings: RemoteSettings(hosts: [RemoteHost(id: "server", destination: "invalid.example")], selection: "server"),
            networkEnabled: false, enabled: Set(SourceKind.allCases))
        #expect(output.liveSnapshot.today.output == 0)
        #expect(local.liveSnapshot.today.output == 123)
    }
}
