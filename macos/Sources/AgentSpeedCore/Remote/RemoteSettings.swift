import Foundation

public struct RemoteHost: Codable, Equatable, Sendable, Identifiable {
    public var id: String
    public var name: String
    public var destination: String
    public var executable: String
    public var enabled: Bool
    public var shell: String?

    public init(id: String = UUID().uuidString, name: String = "Server", destination: String = "",
                executable: String = "agentspeed-collector", enabled: Bool = true, shell: String? = nil) {
        self.id = id; self.name = name; self.destination = destination
        self.executable = executable; self.enabled = enabled; self.shell = shell
    }
    public var isValid: Bool {
        Self.isID(id) && !["local", "all"].contains(id) && !name.isEmpty && name.utf8.count <= 80
            && !name.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) })
            && !destination.isEmpty && destination.utf8.count <= 255 && !destination.hasPrefix("-")
            && destination.utf8.allSatisfy { Self.alphaNumeric($0) || Array("._-@:[]".utf8).contains($0) }
            && !executable.isEmpty && executable.utf8.count <= 1024
            && !executable.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) })
            && (shell == nil || ["posix", "powershell"].contains(shell!))
    }
    static func alphaNumeric(_ c: UInt8) -> Bool { (48...57).contains(c) || (65...90).contains(c) || (97...122).contains(c) }
    static func isID(_ text: String) -> Bool {
        !text.isEmpty && text.utf8.count <= 64 && text.utf8.allSatisfy { alphaNumeric($0) || $0 == 45 || $0 == 95 }
    }
    public func arguments(timeZone: String, sources: Set<SourceKind>) -> [String] {
        func quote(_ text: String) -> String { "'" + text.replacingOccurrences(of: "'", with: "'\\''") + "'" }
        let sourceIDs = SourceKind.allCases.enumerated().filter { sources.contains($0.element) }.map { String($0.offset) }.joined(separator: ",")
        var command = "\(quote(executable)) --stream --timezone \(quote(timeZone)) --sources \(quote(sourceIDs))"
        if shell == "powershell" {
            func ps(_ text: String) -> String { "'" + text.replacingOccurrences(of: "'", with: "''") + "'" }
            let script = "& \(ps(executable)) --stream --timezone \(ps(timeZone)) --sources \(ps(sourceIDs))"
            let data = script.data(using: .utf16LittleEndian) ?? Data()
            command = "powershell.exe -NoProfile -NonInteractive -EncodedCommand " + data.base64EncodedString()
        }
        return commandArguments(command)
    }
    func commandArguments(_ command: String) -> [String] {
        ["-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=10",
                "-o", "ServerAliveInterval=10", "-o", "ServerAliveCountMax=2", "--", destination, command]
    }
}

public struct RemoteSettings: Codable, Equatable, Sendable {
    public var hosts: [RemoteHost]
    public var selection: String
    public init(hosts: [RemoteHost] = [], selection: String = "local") { self.hosts = hosts; self.selection = selection }
    public var isValid: Bool {
        hosts.count <= 16 && hosts.allSatisfy(\.isValid) && Set(hosts.map(\.id)).count == hosts.count
            && Set(hosts.map { $0.destination.lowercased() }).count == hosts.count
            && (["local", "all"].contains(selection) || hosts.contains { $0.id == selection })
    }
}
