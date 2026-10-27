#!/usr/bin/env python3
"""Linux SSH integration check; isolated keys/config, no changes to the user's SSH files.

Needs .NET 10, OpenSSH server/client, timeout and a published linux-x64 collector.
Run as root so the temporary sshd can authenticate the current account through PAM.
"""

import argparse
import datetime as dt
import json
import os
from pathlib import Path
import pwd
import shlex
import shutil
import socket
import subprocess
import tempfile
import time
from xml.sax.saxutils import escape


PROBE = r'''
using System.Diagnostics;
using AgentSpeed.Core;
using AgentSpeed.Core.Remote;

var host = new RemoteHost("smoke", "Smoke", "smoke", args[1]);
static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
ProcessStartInfo Configure(ProcessStartInfo info, bool isolateHome = false) {
    var original = info.ArgumentList.ToArray(); info.ArgumentList.Clear();
    info.ArgumentList.Add("-F"); info.ArgumentList.Add(args[0]);
    foreach (var arg in original) info.ArgumentList.Add(arg);
    if (isolateHome)
        info.ArgumentList[^1] = "env HOME=" + Quote(args[3]) + " sh -c " + Quote(original[^1]);
    return info;
}
var deployment = new RemoteDeployment(name => File.OpenRead(Path.Combine(args[2], name))) {
    ConfigureStart = info => Configure(info, isolateHome: true)
};
using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90))) {
    var installed = await deployment.DeployAsync(host, cancellationToken: timeout.Token);
    var again = await deployment.DeployAsync(host, cancellationToken: timeout.Token);
    if (installed != again || !installed.Executable.StartsWith(args[3] + "/"))
        throw new Exception("Deployment did not use/reuse the isolated user directory");
    var frameCount = 0;
    using var stream = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await RemoteConnection.RunCore(() => Configure(installed.StartInfo("UTC", new HashSet<SourceKind>()), isolateHome: true),
        "UTC", frame => { if (frame.Snapshot is not null && ++frameCount >= 2) stream.Cancel(); }, _ => {}, stream.Token);
    if (frameCount < 2) throw new Exception("The auto-deployed collector did not stream snapshots");
    Console.WriteLine("AUTO DEPLOY + REDEPLOY + INSTALLED STREAM: PASS");
}
var attempts = 0; var snapshots = 0; var rows = 0; var output = 0L;
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
ProcessStartInfo Start() {
    var info = Configure(host.StartInfo("UTC", new HashSet<SourceKind> { SourceKind.Codex }));
    Interlocked.Increment(ref attempts); return info;
}
await RemoteConnection.RunCore(Start, "UTC", frame => {
    if (frame.Snapshot is {} s) { snapshots++; output = s.Today.Output; }
    if (frame.Days is {} days) rows += days.Length;
    if (attempts >= 2 && snapshots >= 4 && rows > 0 && output == 1000) stop.Cancel();
}, _ => {}, stop.Token);
if (attempts < 2 || snapshots < 4 || rows == 0 || output != 1000)
    throw new Exception($"SSH stream/reconnect failed: {attempts}/{snapshots}/{rows}/{output}");
Console.WriteLine($"SSH PASS: connections={attempts}, snapshots={snapshots}, historyRows={rows}, todayOutput={output}");
'''


def main():
    root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default=os.environ.get("DOTNET", "dotnet"))
    parser.add_argument("--collector", type=Path, default=root / "dist/collector/linux-x64/agentspeed-collector")
    parser.add_argument("--bundles", type=Path, default=root / "dist/collectors")
    args = parser.parse_args()
    if os.geteuid() != 0:
        parser.error("the isolated sshd check needs root (it does not change the system sshd or SSH configuration)")
    for command in (args.dotnet, "sshd", "ssh", "ssh-keygen", "timeout", "pgrep"):
        if not shutil.which(command):
            parser.error(f"missing executable: {command}")
    if not args.collector.is_file():
        parser.error("publish the linux-x64 collector first: ./scripts/publish-collector.sh linux-x64")
    if not (args.bundles / "SHA256SUMS.txt").is_file():
        parser.error("stage collector bundles first: scripts/publish-collector.sh && python3 scripts/package-collectors.py")
    # sshd's StrictModes also checks parents: /tmp is unsuitable for an authorized_keys file.
    with tempfile.TemporaryDirectory(prefix="agentspeed-ssh-", dir=Path.home()) as temporary:
        directory = Path(temporary)
        for name in ("identity", "host"):
            subprocess.run(["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", str(directory / name)], check=True)
        (directory / "authorized_keys").write_bytes((directory / "identity.pub").read_bytes())
        (directory / "authorized_keys").chmod(0o600)
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            port = sock.getsockname()[1]
        (directory / "sshd_config").write_text(f"""ListenAddress 127.0.0.1
Port {port}
HostKey {directory}/host
PidFile {directory}/pid
AuthorizedKeysFile {directory}/authorized_keys
StrictModes yes
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin prohibit-password
UsePAM yes
LogLevel ERROR
""")
        key = (directory / "host.pub").read_text().split()
        (directory / "known_hosts").write_text(f"[127.0.0.1]:{port} {key[0]} {key[1]}\n")
        (directory / "empty_known_hosts").touch()
        (directory / "ssh_config").write_text(f"""Host smoke
 HostName 127.0.0.1
 Port {port}
 User {pwd.getpwuid(os.getuid()).pw_name}
 IdentityFile {directory}/identity
 IdentitiesOnly yes
 UserKnownHostsFile {directory}/known_hosts
 GlobalKnownHostsFile /dev/null
""")
        # Move a shared fixture to today so both the live and historical totals are checked.
        base = dt.datetime(2026, 10, 20, 10, 1, tzinfo=dt.timezone.utc)
        start = dt.datetime.now(dt.timezone.utc) - dt.timedelta(seconds=20)
        source = root / "spec/fixtures/codex/sessions/2026/10/20/rollout-2026-10-20T10-01-00-legacy.jsonl"

        def shift(value):
            if isinstance(value, dict):
                return {k: shift(v) for k, v in value.items()}
            if isinstance(value, list):
                return [shift(v) for v in value]
            if isinstance(value, str) and value.startswith("2026-10-20T"):
                date = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
                return (start + (date - base)).isoformat(timespec="milliseconds").replace("+00:00", "Z")
            return value

        sessions = directory / "sessions"
        transcript = sessions / start.strftime("%Y/%m/%d") / f"rollout-{start:%Y-%m-%dT%H-%M-%S}-smoke.jsonl"
        transcript.parent.mkdir(parents=True)
        transcript.write_text("".join(json.dumps(shift(json.loads(line))) + "\n" for line in source.read_text().splitlines()))
        store = directory / "history.sqlite"
        wrapper = directory / "collector"
        command = " ".join(map(shlex.quote, (str(args.collector.resolve()), "--codex", str(sessions), "--store", str(store))))
        marker = shlex.quote(str(directory / "started"))
        wrapper.write_text(f'#!/bin/sh\nif [ ! -f {marker} ]; then\n touch {marker}\n exec timeout 3 {command} "$@"\nfi\nexec {command} "$@"\n')
        wrapper.chmod(0o700)
        project = directory / "probe"
        project.mkdir()
        reference = escape(str(root / "windows/src/AgentSpeed.Core/AgentSpeed.Core.csproj"))
        # The existing test friend assembly grants access to the injectable transport, not app-only production APIs.
        (project / "Probe.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>AgentSpeed.Core.Tests</AssemblyName></PropertyGroup>
<ItemGroup><ProjectReference Include="{reference}" /></ItemGroup></Project>""")
        (project / "Program.cs").write_text(PROBE)
        with (directory / "sshd.log").open("w") as log:
            daemon = subprocess.Popen([shutil.which("sshd"), "-D", "-e", "-f", str(directory / "sshd_config")], stderr=log)
            try:
                time.sleep(0.3)
                if daemon.poll() is not None:
                    raise RuntimeError((directory / "sshd.log").read_text())
                subprocess.run([args.dotnet, "run", "--project", str(project), "-c", "Release", "--",
                                str(directory / "ssh_config"), str(wrapper), str(args.bundles.resolve()),
                                str(directory / "remote user's home")], check=True, timeout=180,
                               env=dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1"))
                bad = subprocess.run(["ssh", "-F", str(directory / "ssh_config"), "-o", "BatchMode=yes", "-o",
                                      "StrictHostKeyChecking=yes", "-o", "UserKnownHostsFile=" + str(directory / "empty_known_hosts"),
                                      "smoke", "true"], capture_output=True, text=True, timeout=10)
                assert bad.returncode != 0 and "Host key verification failed" in bad.stderr
                print("UNKNOWN HOST REJECTED: PASS")
                time.sleep(1)
                remaining = subprocess.run(["pgrep", "-f", str(store)], capture_output=True, text=True)
                assert remaining.returncode == 1, "SSH collector was left running after cancellation"
                print("CHILD CLEANUP: PASS")
            finally:
                daemon.terminate()
                daemon.wait(timeout=5)


if __name__ == "__main__":
    main()
