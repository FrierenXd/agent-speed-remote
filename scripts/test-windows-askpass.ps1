# Exercise the packaged WinExe helper, including stdout and direct-parent checks.
param([string]$Archive = "$PSScriptRoot/../dist/AgentSpeed-Windows-x64.zip")
$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) ("agentspeed-askpass-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    Expand-Archive -Path $Archive -DestinationPath (Join-Path $work 'app')
    $reference = [System.Security.SecurityElement]::Escape((Resolve-Path "$PSScriptRoot/../windows/src/AgentSpeed.Core/AgentSpeed.Core.csproj").Path)
    @"
<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="$reference" /></ItemGroup></Project>
"@ | Set-Content (Join-Path $work 'Probe.csproj')
    @'
using System.Diagnostics;
using AgentSpeed.Core.Remote;
var host = new RemoteHost("helper-test", "Helper", "server", Authentication: "password");
const string secret = "test-only ' $ \\ password";
RemotePasswords.Set(host, secret);
async Task Check(bool trusted) {
    var info = new ProcessStartInfo(args[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    info.ArgumentList.Add("user@server's password: ");
    using var authentication = RemotePasswordPrompt.Configure(host, info);
    authentication!.Started(trusted ? Environment.ProcessId : -1);
    info.Environment["SSH_ASKPASS"] = args[0];
    using var child = Process.Start(info)!;
    var output = child.StandardOutput.ReadToEndAsync();
    var errors = child.StandardError.ReadToEndAsync();
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
    var text = await output;
    if (trusted ? child.ExitCode != 0 || text != secret + "\n" : text.Length != 0)
        throw new Exception("Packaged Windows askpass helper failed its IPC/stdout/parent check");
    await errors;
}
await Check(true);
await Check(false);
RemotePasswords.Set(host, "");
Console.WriteLine("PACKAGED WINDOWS ASKPASS + PROXY ISOLATION: PASS");
'@ | Set-Content (Join-Path $work 'Program.cs')
    & dotnet run --project (Join-Path $work 'Probe.csproj') -c Release -- (Join-Path $work 'app/AgentSpeed.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Windows askpass helper test failed' }
} finally {
    Remove-Item -Recurse -Force $work
}
