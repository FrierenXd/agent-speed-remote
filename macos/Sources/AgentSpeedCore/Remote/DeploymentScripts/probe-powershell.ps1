$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
if (-not [Environment]::Is64BitOperatingSystem) { [Console]::Error.WriteLine('A 64-bit Windows host is required'); exit 42 }
$architecture = "$env:PROCESSOR_ARCHITECTURE $env:PROCESSOR_ARCHITEW6432"
if ($architecture -match 'ARM64') { $rid = 'win-arm64' }
elseif ($architecture -match 'AMD64') { $rid = 'win-x64' }
else { [Console]::Error.WriteLine('Unsupported remote CPU architecture'); exit 42 }
[Console]::WriteLine("AGENTSPEED_DEPLOY_V1`n$rid")
