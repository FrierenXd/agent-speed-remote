$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$hash = '__SHA256__'
$base = [Environment]::GetFolderPath('LocalApplicationData')
if (-not [IO.Path]::IsPathRooted($base)) { throw 'An absolute LocalApplicationData path is required' }
$root = Join-Path $base 'AgentSpeed\collector\releases'
$stage = Join-Path $root ('.upload-' + [Guid]::NewGuid().ToString('N'))
$lockPath = Join-Path $root ('.lock-' + $hash)
$lock = $null
try {
    $null = New-Item -ItemType Directory -Path $stage -Force
    $archive = Join-Path $stage 'payload.tar.gz'
    $output = [IO.File]::Create($archive)
    try { [Console]::OpenStandardInput().CopyTo($output) } finally { $output.Dispose() }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) {
        throw 'Collector SHA-256 mismatch'
    }
    $runtime = Join-Path $stage 'runtime'
    $null = New-Item -ItemType Directory -Path $runtime
    & tar.exe -xzf $archive -C $runtime
    if ($LASTEXITCODE -ne 0) { throw 'Collector extraction failed (tar.exe is required)' }
    $check = & (Join-Path $runtime 'agentspeed-collector.exe') --check
    if ($LASTEXITCODE -ne 0 -or $check -ne 'AGENTSPEED_COLLECTOR_V1') { throw 'Collector runtime check failed' }
    [IO.File]::WriteAllText((Join-Path $runtime '.agentspeed-sha256'), $hash)
    $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $final = Join-Path $root $hash
    if (Test-Path -LiteralPath $final) {
        if ([IO.File]::ReadAllText((Join-Path $final '.agentspeed-sha256')) -ne $hash) { throw 'Collector marker mismatch' }
        $check = & (Join-Path $final 'agentspeed-collector.exe') --check
        if ($LASTEXITCODE -ne 0 -or $check -ne 'AGENTSPEED_COLLECTOR_V1') { throw 'Collector runtime check failed' }
    } else {
        [IO.Directory]::Move($runtime, $final)
    }
    [Console]::WriteLine("AGENTSPEED_INSTALLED_V1`n$(Join-Path $final 'agentspeed-collector.exe')")
} finally {
    if ($null -ne $lock) { $lock.Dispose() }
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
