import Foundation
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

/// A deployment is an explicit SSH action; all blocking IO runs outside the main/cooperative executors.
public final class RemoteDeployment: @unchecked Sendable {
    public static let runtimes = ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64", "win-arm64"]
    public static let maxPackageBytes = 64 * 1024 * 1024
    private let assets: URL
    private let lock = NSLock()
    private var cancelled = false
    private var timedOut = false
    private var process: Process?

    public init(assets: URL) { self.assets = assets }
    public func cancel() {
        lock.withLock {
            cancelled = true
            if let process, process.isRunning { kill(process.processIdentifier, SIGKILL) }
        }
    }
    public func deploy(host: RemoteHost, progress: @escaping @Sendable (String) -> Void = { _ in }) async throws -> RemoteHost {
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                DispatchQueue.global(qos: .utility).async { [self] in
                    do { continuation.resume(returning: try perform(host: host, progress: progress)) }
                    catch { continuation.resume(throwing: error) }
                }
            }
        } onCancel: { self.cancel() }
    }

    private func perform(host: RemoteHost, progress: @Sendable (String) -> Void) throws -> RemoteHost {
        guard host.isValid else { throw failure("Invalid remote host configuration") }
        progress("probing")
        // cmd.exe cannot safely quote a multiline POSIX script; first use a plain cross-shell probe.
        let platform = try run(host: host, command: "uname -s", timeout: 25)
        if platform.code == 255 { try ensureSuccess(platform) }
        let shell = platform.code == 0 && ["Linux", "Darwin"].contains(platform.output.trimmingCharacters(in: .whitespacesAndNewlines)) ? "posix" : "powershell"
        let result = try run(host: host, command: Self.command(action: "probe", shell: shell), timeout: 25)
        try ensureSuccess(result)
        let rid = try Self.parseProbe(result.output)
        guard (shell == "powershell") == rid.hasPrefix("win-") else { throw failure("Remote shell and operating system do not match") }
        let manifestURL = assets.appendingPathComponent("SHA256SUMS.txt")
        guard (try? manifestURL.resourceValues(forKeys: [.fileSizeKey]).fileSize).map({ $0 <= 4096 }) == true else {
            throw failure("Bundled collectors are missing. Use an official packaged build or build the collector bundles first.")
        }
        let file = "AgentSpeed-Collector-\(rid).tar.gz"
        let hash = try Self.parseHash(String(contentsOf: manifestURL, encoding: .utf8), file: file)
        let url = assets.appendingPathComponent(file)
        guard (try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? Int.max) <= Self.maxPackageBytes else {
            throw failure("Collector package exceeds the size limit")
        }
        let data = try Data(contentsOf: url, options: .mappedIfSafe)
        guard data.count <= Self.maxPackageBytes, ReportHash.sha256Hex(data) == hash else { throw failure("Bundled collector SHA-256 mismatch") }
        progress("uploading")
        let install = try run(host: host, command: Self.command(action: "install", shell: shell, hash: hash), input: data, timeout: 180)
        try ensureSuccess(install)
        var installed = host
        installed.executable = try Self.parseInstalled(install.output, shell: shell)
        installed.shell = shell; installed.enabled = true
        return installed
    }

    private static func lines(_ output: String) -> [String] {
        var normalized = output.replacingOccurrences(of: "\r\n", with: "\n")
        while normalized.hasSuffix("\n") { normalized.removeLast() }
        return normalized.components(separatedBy: "\n")
    }
    static func parseProbe(_ output: String) throws -> String {
        let parts = lines(output)
        guard parts.count == 2, parts[0] == "AGENTSPEED_DEPLOY_V1", runtimes.contains(parts[1]) else {
            throw failure("Invalid or unsupported remote platform response. Check that the SSH shell does not print a banner.")
        }
        return parts[1]
    }
    static func parseInstalled(_ output: String, shell: String) throws -> String {
        let parts = lines(output)
        guard parts.count == 2, parts[0] == "AGENTSPEED_INSTALLED_V1", !parts[1].isEmpty, parts[1].utf8.count <= 1024,
              !parts[1].unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
            throw failure("Invalid collector installation response")
        }
        let path = parts[1], bytes = Array(path.utf8)
        let absolute = shell == "powershell"
            ? bytes.count > 3 && ((65...90).contains(bytes[0]) || (97...122).contains(bytes[0])) && bytes[1] == 58 && bytes[2] == 92
            : path.hasPrefix("/")
        guard absolute else { throw failure("Expected an absolute collector path") }
        return path
    }
    static func parseHash(_ manifest: String, file: String) throws -> String {
        let matches = lines(manifest).map { $0.split(separator: " ").map(String.init) }.filter { $0.count == 2 && $0[1] == file }
        guard matches.count == 1, matches[0][0].utf8.count == 64,
              matches[0][0].utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }) else {
            throw failure("Invalid collector checksum manifest")
        }
        return matches[0][0].lowercased()
    }
    static func command(action: String, shell: String, hash: String = "") throws -> String {
        guard ["probe", "install"].contains(action), ["posix", "powershell"].contains(shell),
              action != "install" || (hash.utf8.count == 64 && hash.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) })) else {
            throw failure("Invalid deployment command")
        }
        let scripts = Bundle.main.bundleURL.pathExtension == "app" ? Bundle.main : Bundle.module
        guard let url = scripts.url(forResource: "\(action)-\(shell)", withExtension: shell == "posix" ? "sh" : "ps1", subdirectory: "DeploymentScripts") else {
            throw failure("Missing deployment script")
        }
        let script = try String(contentsOf: url, encoding: .utf8).replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "__SHA256__", with: hash)
        if shell == "powershell" {
            return "powershell.exe -NoProfile -NonInteractive -EncodedCommand " + Data(script.utf16.flatMap { [UInt8($0 & 255), UInt8($0 >> 8)] }).base64EncodedString()
        }
        return "sh -c '" + script.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    private struct Result { let code: Int32; let output: String; let error: String }
    private func ensureSuccess(_ result: Result) throws {
        guard result.code == 0 else {
            let detail = String(result.error.unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) || $0 == "\n" }.prefix(2000).map(Character.init))
            throw failure("SSH deployment failed (exit \(result.code)). \(detail)")
        }
    }
    private static func failure(_ message: String) -> NSError { NSError(domain: "AgentSpeed.RemoteDeployment", code: 1, userInfo: [NSLocalizedDescriptionKey: message]) }
    private func failure(_ message: String) -> NSError { Self.failure(message) }

    private final class Capture: @unchecked Sendable {
        private let lock = NSLock()
        private var data = Data()
        private var overflow = false
        func read(_ handle: FileHandle, abort: @Sendable () -> Void) {
            do {
                while let chunk = try handle.read(upToCount: 8192), !chunk.isEmpty {
                    let exceeded = lock.withLock { () -> Bool in
                        if data.count + chunk.count > 65536 { overflow = true; return true }
                        data.append(chunk); return false
                    }
                    if exceeded { abort(); return }
                }
            } catch { abort() }
        }
        func text() throws -> String {
            try lock.withLock {
                guard !overflow else { throw RemoteDeployment.failure("Deployment response exceeds the size limit") }
                return String(decoding: data, as: UTF8.self)
            }
        }
    }
    private func run(host: RemoteHost, command: String, input: Data = Data(), timeout: Double) throws -> Result {
        let child = Process(), output = Pipe(), errors = Pipe(), stdin = Pipe()
        child.executableURL = URL(fileURLWithPath: "/usr/bin/ssh")
        child.arguments = host.commandArguments(command)
        child.standardInput = stdin; child.standardOutput = output; child.standardError = errors
        let authentication = try SSHPasswordPrompt.configure(host, process: child)
        defer { withExtendedLifetime(authentication) {} }
        #if canImport(Darwin)
        // A disconnect during upload must throw, not terminate the desktop with SIGPIPE.
        _ = fcntl(stdin.fileHandleForWriting.fileDescriptor, F_SETNOSIGPIPE, 1)
        #endif
        try lock.withLock {
            guard !cancelled else { throw CancellationError() }
            try child.run(); process = child; timedOut = false
            try authentication?.started(child.processIdentifier)
        }
        let watchdog = DispatchSource.makeTimerSource(queue: .global(qos: .utility))
        let pid = child.processIdentifier
        watchdog.schedule(deadline: .now() + timeout)
        watchdog.setEventHandler { [weak self] in
            guard let self else { return }
            self.lock.withLock {
                if let process = self.process, process.processIdentifier == pid, process.isRunning {
                    self.timedOut = true; kill(pid, SIGKILL)
                }
            }
        }
        watchdog.resume()
        let group = DispatchGroup(), stdout = Capture(), stderr = Capture()
        for (capture, handle) in [(stdout, output.fileHandleForReading), (stderr, errors.fileHandleForReading)] {
            group.enter()
            DispatchQueue.global(qos: .utility).async { [self] in
                capture.read(handle, abort: { self.killChild() }); group.leave()
            }
        }
        defer {
            watchdog.cancel(); killChild()
            try? stdin.fileHandleForWriting.close()
            if child.isRunning { child.waitUntilExit() }
            group.wait()
            try? output.fileHandleForReading.close(); try? errors.fileHandleForReading.close()
            lock.withLock { process = nil }
        }
        do {
            try stdin.fileHandleForWriting.write(contentsOf: input)
            try stdin.fileHandleForWriting.close()
        } catch {
            if lock.withLock({ cancelled }) { throw CancellationError() }
            if lock.withLock({ timedOut }) { throw failure("SSH deployment timed out") }
            throw error
        }
        child.waitUntilExit(); group.wait()
        if lock.withLock({ cancelled }) { throw CancellationError() }
        if lock.withLock({ timedOut }) { throw failure("SSH deployment timed out") }
        return try Result(code: child.terminationStatus, output: stdout.text(), error: stderr.text())
    }
    private func killChild() {
        lock.withLock { if let process, process.isRunning { kill(process.processIdentifier, SIGKILL) } }
    }
}
