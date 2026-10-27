import Foundation
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

/// Passwords live only in this process, never in Codable settings or command arguments.
public final class RemotePasswords: @unchecked Sendable {
    public static let shared = RemotePasswords()
    private let lock = NSLock()
    private var values: [String: String] = [:]
    private var versions: [String: UInt64] = [:]
    private var version: UInt64 = 0
    public var revision: UInt64 { lock.withLock { version } }
    private func key(_ host: RemoteHost) -> String { host.id + "\n" + host.destination }
    public func get(_ host: RemoteHost) -> String { lock.withLock { values[key(host)] ?? "" } }
    func revision(for host: RemoteHost) -> UInt64 { lock.withLock { host.authentication == "password" ? versions[key(host)] ?? 0 : 0 } }
    public func set(_ host: RemoteHost, password: String) throws {
        // Leave room for newline and NUL in OpenSSH's 1024-byte askpass buffer.
        guard password.utf8.count <= 1022, !password.contains(where: { $0 == "\0" || $0 == "\r" || $0 == "\n" }) else {
            throw NSError(domain: "AgentSpeed.SSH", code: 1, userInfo: [NSLocalizedDescriptionKey: "The SSH password must be a single line of at most 1022 UTF-8 bytes."])
        }
        lock.withLock { values[key(host)] = password.isEmpty ? nil : password; version &+= 1; versions[key(host)] = version }
    }
    public func retain(_ hosts: [RemoteHost]) {
        lock.withLock {
            let keep = Set(hosts.filter { $0.authentication == "password" }.map { key($0) })
            values = values.filter { keep.contains($0.key) }; version &+= 1
        }
    }
}

/// A private FIFO carries the password in kernel memory; the helper file contains no secret.
final class SSHPasswordPrompt {
    private var directory: URL?
    private var fifo: Int32 = -1
    var wasRequested: Bool { directory.map { FileManager.default.fileExists(atPath: $0.appendingPathComponent("requested").path) } ?? false }
    func started(_ processID: Int32) throws {
        if let directory { try (String(processID) + "\n").write(to: directory.appendingPathComponent("pid"), atomically: true, encoding: .utf8) }
    }
    static let helper = """
    #!/bin/sh
    case "$1" in *"'s password: "|*"'s password:") ;; *) exit 1;; esac
    [ -z "$SSH_ASKPASS_PROMPT" ] || exit 1
    pidfile="${AGENTSPEED_ASKPASS_ENDPOINT%/*}/pid"
    i=0
    while [ ! -s "$pidfile" ] && [ "$i" -lt 40 ]; do sleep 0.05; i=$((i+1)); done
    IFS= read -r expected < "$pidfile" || exit 1
    : > "${AGENTSPEED_ASKPASS_ENDPOINT%/*}/requested"
    [ "$PPID" = "$expected" ] || exit 1
    IFS= read -r secret < "$AGENTSPEED_ASKPASS_ENDPOINT" || exit 1
    printf '%s\\n' "$secret"

    """
    static func configure(_ host: RemoteHost, process: Process) throws -> SSHPasswordPrompt? {
        guard host.authentication == "password" else { return nil }
        let password = RemotePasswords.shared.get(host)
        guard !password.isEmpty else {
            throw NSError(domain: "AgentSpeed.SSH", code: 1, userInfo: [NSLocalizedDescriptionKey: "Enter an SSH password and apply the remote host settings first."])
        }
        let prompt = SSHPasswordPrompt()
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("agentspeed-askpass-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        prompt.directory = directory
        let path = directory.appendingPathComponent("password").path
        guard mkfifo(path, 0o600) == 0 else { throw prompt.failure() }
        prompt.fifo = open(path, O_RDWR | O_NONBLOCK | O_CLOEXEC)
        guard prompt.fifo >= 0 else { throw prompt.failure() }
        var bytes = Data((password + "\n").utf8)
        defer { bytes.resetBytes(in: bytes.startIndex..<bytes.endIndex) }
        let written = bytes.withUnsafeBytes { write(prompt.fifo, $0.baseAddress!, $0.count) }
        guard written == bytes.count else { throw prompt.failure() }
        let helperURL = directory.appendingPathComponent("askpass")
        try helper.write(to: helperURL, atomically: false, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: helperURL.path)
        var env = process.environment ?? ProcessInfo.processInfo.environment
        env["AGENTSPEED_ASKPASS_ENDPOINT"] = path
        env["SSH_ASKPASS"] = helperURL.path; env["SSH_ASKPASS_REQUIRE"] = "force"; env["DISPLAY"] = "agentspeed:0"
        process.environment = env
        return prompt
    }
    private func failure() -> NSError {
        NSError(domain: "AgentSpeed.SSH", code: 2, userInfo: [NSLocalizedDescriptionKey: "Cannot create SSH password pipe."])
    }
    deinit {
        if fifo >= 0 { _ = close(fifo) }
        if let directory { try? FileManager.default.removeItem(at: directory) }
    }
}
