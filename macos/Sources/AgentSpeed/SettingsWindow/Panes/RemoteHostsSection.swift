import AgentSpeedCore
import SwiftUI

struct RemoteHostsSection: View {
    let model: AppModel
    @State private var hosts: [RemoteHost] = []
    @State private var error = false
    @State private var deployment: Task<Void, Never>?
    @State private var deploymentStatus = ""
    @State private var deploymentError = ""

    var body: some View {
        let l10n = model.l10n
        Section {
            Picker(l10n("remote.view"), selection: Binding(get: { model.settings.remote.selection }, set: {
                model.settings.remote.selection = $0
            })) {
                Text(l10n("remote.local")).tag("local")
                Text(l10n("remote.all")).tag("all")
                ForEach(model.settings.remote.hosts) { host in Text(host.name).tag(host.id) }
            }
            ForEach($hosts) { $host in
                VStack(alignment: .leading, spacing: 8) {
                    HStack {
                        Toggle(l10n("remote.enabled"), isOn: $host.enabled)
                        Spacer()
                        Button(l10n("remote.remove")) { hosts.removeAll { $0.id == host.id } }
                    }
                    TextField(l10n("remote.name"), text: $host.name)
                    TextField(l10n("remote.destination"), text: $host.destination)
                    TextField(l10n("remote.executable"), text: $host.executable)
                    Picker(l10n("remote.shell"), selection: Binding(get: { host.shell ?? "posix" }, set: { host.shell = $0 })) {
                        Text("POSIX (Linux / macOS)").tag("posix")
                        Text("PowerShell (Windows)").tag("powershell")
                    }
                    Text(l10n("remote.status." + (model.remote?.statuses[host.id] ?? "disabled")))
                        .font(.caption).foregroundStyle(.secondary)
                    Button(l10n("remote.deploy.action")) { deploy(host) }
                }.padding(.vertical, 4)
                    .disabled(deployment != nil)
            }
            HStack {
                Button(l10n("remote.add")) { if hosts.count < 16 { hosts.append(RemoteHost()) } }
                Spacer()
                Button(l10n("remote.apply")) { apply() }
            }
            .disabled(deployment != nil)
            if deployment != nil { Button(l10n("remote.deploy.cancel")) { deployment?.cancel() } }
            if !deploymentStatus.isEmpty { Text(l10n("remote.deploy." + deploymentStatus)).font(.caption) }
            if !deploymentError.isEmpty { Text(deploymentError).foregroundStyle(.red).textSelection(.enabled) }
            if error { Text(l10n("remote.invalid")).foregroundStyle(.red) }
        } header: {
            Text(l10n("remote.title"))
        } footer: {
            SectionFootnote(l10n("remote.hint") + "\n" + l10n("remote.deploy.hint"))
        }
        .onAppear { hosts = model.settings.remote.hosts }
        .onDisappear { deployment?.cancel() }
        .onChange(of: model.settings.networkEnabled) { _, enabled in if !enabled { deployment?.cancel() } }
    }
    private func deploy(_ host: RemoteHost) {
        guard deployment == nil else { return }
        guard model.settings.networkEnabled else { deploymentError = model.l10n("remote.deploy.networkOff"); return }
        guard RemoteSettings(hosts: hosts).isValid else { error = true; return }
        error = false; deploymentError = ""; deploymentStatus = "probing"
        let assets = (Bundle.main.resourceURL ?? Bundle.main.bundleURL).appendingPathComponent("Collectors")
        deployment = Task { @MainActor in
            defer { deployment = nil }
            do {
                let installed = try await RemoteDeployment(assets: assets).deploy(host: host) { phase in
                    Task { @MainActor in deploymentStatus = phase }
                }
                try Task.checkCancellation()
                guard let index = hosts.firstIndex(where: { $0.id == host.id }) else { return }
                hosts[index] = installed
                model.settings.remote = RemoteSettings(hosts: hosts, selection: installed.id)
                deploymentStatus = "done"
            } catch is CancellationError { deploymentStatus = "cancelled" }
            catch { deploymentStatus = ""; deploymentError = model.l10n("remote.deploy.failed") + " " + error.localizedDescription }
        }
    }
    private func apply() {
        let selection = model.settings.remote.selection
        let remote = RemoteSettings(hosts: hosts, selection: ["local", "all"].contains(selection) || hosts.contains { $0.id == selection } ? selection : "local")
        error = !remote.isValid
        if remote.isValid { model.settings.remote = remote }
    }
}
