import Foundation
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

/// Blocking pipe reads stay on a utility GCD worker, never on the UI or Swift's cooperative executor.
final class SSHStream: @unchecked Sendable {
    let host: RemoteHost
    let timeZone: String
    let sources: Set<SourceKind>
    let receive: @Sendable (RemoteFrame) -> Void
    let status: @Sendable (String) -> Void
    private let lock = NSLock()
    private var cancelled = false
    private var process: Process?
    private var lastRead = ProcessInfo.processInfo.systemUptime

    init(host: RemoteHost, timeZone: String, sources: Set<SourceKind>, receive: @escaping @Sendable (RemoteFrame) -> Void,
         status: @escaping @Sendable (String) -> Void) {
        self.host = host; self.timeZone = timeZone; self.sources = sources; self.receive = receive; self.status = status
    }
    private var isCancelled: Bool { lock.withLock { cancelled } }
    func stop() {
        lock.withLock { cancelled = true; if let process, process.isRunning { kill(process.processIdentifier, SIGKILL) } }
    }
    func start() { DispatchQueue.global(qos: .utility).async { [self] in run() } }
    private func run() {
        var backoff = 1
        while !isCancelled {
            status("connecting")
            let child = Process(), output = Pipe(), errors = Pipe(), input = Pipe()
            child.executableURL = URL(fileURLWithPath: "/usr/bin/ssh")
            child.arguments = host.arguments(timeZone: timeZone, sources: sources)
            child.standardOutput = output; child.standardError = errors; child.standardInput = input
            errors.fileHandleForReading.readabilityHandler = { handle in _ = handle.availableData }
            let watchdog = DispatchSource.makeTimerSource(queue: .global(qos: .utility))
            watchdog.schedule(deadline: .now() + 5, repeating: 5)
            watchdog.setEventHandler { [weak self] in
                guard let self else { return }
                self.lock.withLock {
                    if ProcessInfo.processInfo.systemUptime - self.lastRead > 30, let process = self.process, process.isRunning {
                        kill(process.processIdentifier, SIGKILL)
                    }
                }
            }
            watchdog.resume()
            var receivedFrame = false
            var authentication: SSHPasswordPrompt?
            do {
                if host.authentication == "password", RemotePasswords.shared.get(host).isEmpty {
                    status("passwordRequired"); watchdog.cancel()
                    errors.fileHandleForReading.readabilityHandler = nil
                    return
                }
                authentication = try SSHPasswordPrompt.configure(host, process: child)
                try lock.withLock {
                    guard !cancelled else { throw RemoteError.connectionClosed }
                    process = child; lastRead = ProcessInfo.processInfo.systemUptime
                    try child.run()
                    try authentication?.started(child.processIdentifier)
                }
                var buffer = Data()
                while !isCancelled, let bytes = try output.fileHandleForReading.read(upToCount: 8192), !bytes.isEmpty {
                    buffer.append(bytes)
                    while let newline = buffer.firstIndex(of: 10) {
                        let line = Data(buffer[..<newline]); buffer.removeSubrange(...newline)
                        let frame = try RemoteFrame.decode(line)
                        guard frame.timeZone == timeZone else { throw RemoteError.invalidFrame }
                        if frame.type == "history", !RemoteHost.isID(frame.generation ?? "") { throw RemoteError.invalidFrame }
                        guard abs(Date().timeIntervalSince1970 - Double(frame.timestampMs) / 1000) <= 120 else { throw RemoteError.staleClock }
                        lock.withLock { lastRead = ProcessInfo.processInfo.systemUptime }
                        status("connected"); receive(frame); backoff = 1
                        receivedFrame = true
                    }
                    guard buffer.count <= RemoteFrame.maxLineBytes else { throw RemoteError.invalidFrame }
                }
                status("disconnected")
            } catch { status(error is DecodingError ? "protocolError" : "disconnected") }
            watchdog.cancel()
            errors.fileHandleForReading.readabilityHandler = nil
            try? input.fileHandleForWriting.close()
            lock.withLock { if child.isRunning { kill(child.processIdentifier, SIGKILL) }; process = nil }
            if child.processIdentifier > 0 { child.waitUntilExit() }
            try? output.fileHandleForReading.close(); try? errors.fileHandleForReading.close()
            if authentication?.wasRequested == true, !receivedFrame, child.processIdentifier > 0, !isCancelled,
               child.terminationStatus != 0 {
                status("authenticationFailed"); return
            }
            authentication = nil
            for _ in 0..<(backoff * 10) { if isCancelled { return }; Thread.sleep(forTimeInterval: 0.1) }
            backoff = min(60, backoff * 2)
        }
    }
}
