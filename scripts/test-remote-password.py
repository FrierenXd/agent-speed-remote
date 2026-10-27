#!/usr/bin/env python3
"""Real password SSH deployment/stream tests using a temporary account and isolated sshd (Linux/root only)."""
import argparse
import os
from pathlib import Path
import pwd
import secrets
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

var password = Console.ReadLine() ?? throw new Exception("Test password missing");
var host = new RemoteHost("password-smoke", "Password smoke", "password-smoke", Authentication: "password");
RemotePasswords.Set(host, password);
ProcessStartInfo Configure(ProcessStartInfo info, bool untrusted = false) {
    info.ArgumentList.Insert(0, args[0]); info.ArgumentList.Insert(0, "-F");
    if (untrusted) {
        info.ArgumentList.Insert(0, "UserKnownHostsFile=" + args[2]); info.ArgumentList.Insert(0, "-o");
    }
    return info;
}
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
var deployer = new RemoteDeployment(name => File.OpenRead(Path.Combine(args[1], name))) { ConfigureStart = info => Configure(info) };
var installed = await deployer.DeployAsync(host, cancellationToken: timeout.Token);
var again = await deployer.DeployAsync(host, cancellationToken: timeout.Token);
if (installed != again) throw new Exception("Password redeployment is not idempotent");
Console.WriteLine("PASSWORD AUTO DEPLOY + REDEPLOY: PASS");
var snapshots = 0; var attempts = 0;
using var streaming = new CancellationTokenSource(TimeSpan.FromSeconds(20));
await RemoteConnection.RunCore(() => { attempts++; return Configure(installed.StartInfo("UTC", new HashSet<SourceKind>())); },
    "UTC", frame => { if (frame.Snapshot is not null && ++snapshots >= 2) streaming.Cancel(); }, _ => {}, streaming.Token, installed);
if (snapshots < 2 || attempts != 1) throw new Exception("Password streaming failed");
Console.WriteLine("PASSWORD COLLECTOR STREAM: PASS");
RemotePasswords.Set(host, password + "-wrong");
attempts = 0; var statuses = new List<string>();
await RemoteConnection.RunCore(() => { attempts++; return Configure(host.StartInfo("UTC")); },
    "UTC", _ => {}, statuses.Add, timeout.Token, host);
if (attempts != 1 || !statuses.Contains("authenticationFailed")) throw new Exception("Bad password was retried or not reported");
Console.WriteLine("WRONG PASSWORD REJECTED WITHOUT RETRY: PASS");
RemotePasswords.Set(host, password);
attempts = 0;
var untrusted = new RemoteDeployment(name => File.OpenRead(Path.Combine(args[1], name))) {
    ConfigureStart = info => { attempts++; return Configure(info, untrusted: true); }
};
try {
    await untrusted.DeployAsync(host, cancellationToken: timeout.Token);
    throw new Exception("Untrusted password host was accepted");
} catch (IOException error) when (error.Message.Contains("Host key verification failed")) { }
if (attempts != 1) throw new Exception("Untrusted deployment was retried");
Console.WriteLine("PASSWORD MODE STILL REJECTS UNKNOWN HOST: PASS");
RemotePasswords.Set(host, "");
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    if os.geteuid() != 0:
        parser.error("needs root to create and remove an isolated test account")
    root = Path(__file__).resolve().parent.parent
    username = "aspw" + secrets.token_hex(5)
    password = secrets.token_urlsafe(24) + " ' $ \\"
    with tempfile.TemporaryDirectory(prefix="agentspeed-password-") as temporary:
        directory = Path(temporary)
        directory.chmod(0o755)
        home = directory / "home"
        home.mkdir(mode=0o700)
        subprocess.run(["useradd", "--no-create-home", "--home-dir", str(home), "--shell", "/bin/sh", username], check=True)
        daemon = None
        try:
            account = pwd.getpwnam(username)
            os.chown(home, account.pw_uid, account.pw_gid)
            subprocess.run(["chpasswd"], input=f"{username}:{password}\n", text=True, check=True)
            subprocess.run(["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", str(directory / "host")], check=True)
            with socket.socket() as listener:
                listener.bind(("127.0.0.1", 0))
                port = listener.getsockname()[1]
            (directory / "sshd_config").write_text(f"""ListenAddress 127.0.0.1
Port {port}
HostKey {directory}/host
PidFile {directory}/pid
PasswordAuthentication yes
PubkeyAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin no
UsePAM no
AllowUsers {username}
LogLevel ERROR
""")
            key = (directory / "host.pub").read_text().split()
            (directory / "known_hosts").write_text(f"[127.0.0.1]:{port} {key[0]} {key[1]}\n")
            (directory / "empty_known_hosts").touch()
            (directory / "ssh_config").write_text(f"""Host password-smoke
 HostName 127.0.0.1
 Port {port}
 User {username}
 UserKnownHostsFile {directory}/known_hosts
 GlobalKnownHostsFile /dev/null
""")
            project = directory / "probe"
            project.mkdir()
            reference = escape(str(root / "windows/src/AgentSpeed.Core/AgentSpeed.Core.csproj"))
            (project / "Probe.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>AgentSpeed.Core.Tests</AssemblyName></PropertyGroup>
<ItemGroup><ProjectReference Include="{reference}" /></ItemGroup></Project>""")
            (project / "Program.cs").write_text(PROBE)
            with (directory / "sshd.log").open("w") as log:
                daemon = subprocess.Popen([shutil.which("sshd"), "-D", "-e", "-f", str(directory / "sshd_config")], stderr=log)
                time.sleep(0.3)
                if daemon.poll() is not None:
                    raise RuntimeError((directory / "sshd.log").read_text())
                subprocess.run([args.dotnet, "run", "--project", str(project), "-c", "Release", "--",
                                str(directory / "ssh_config"), str(root / "dist/collectors"), str(directory / "empty_known_hosts")],
                               input=password + "\n", text=True, check=True, timeout=150,
                               env=dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1"))
                time.sleep(1)
                remaining = subprocess.run(["pgrep", "-u", str(account.pw_uid)], capture_output=True)
                assert remaining.returncode == 1, "SSH collector was left running after password stream cancellation"
                print("PASSWORD CHILD CLEANUP: PASS")
        finally:
            if daemon is not None:
                daemon.terminate()
                daemon.wait(timeout=5)
            subprocess.run(["userdel", username], check=True)


if __name__ == "__main__":
    main()
