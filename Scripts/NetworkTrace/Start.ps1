[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][System.Net.IPAddress]$PeerAddress,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
# Resolve after parameter binding: some Windows PowerShell launchers leave
# PSScriptRoot empty while evaluating parameter default expressions.
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $scriptDirectory = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($scriptDirectory) -and $MyInvocation.MyCommand.Path) {
        $scriptDirectory = [IO.Path]::GetDirectoryName($MyInvocation.MyCommand.Path)
    }
    if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
        Write-Error 'Run this script with powershell -File, or supply -OutputDirectory explicitly.'
        exit 1
    }
    $OutputDirectory = Join-Path $scriptDirectory 'network-traces'
}
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Write-Error 'Run PowerShell as administrator.'; exit 1 }
$root = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($root) | Out-Null
$statePath = Join-Path $root 'active.json'
if (Test-Path -LiteralPath $statePath) { Write-Error 'A trace is already registered here. Run Stop.ps1 first.'; exit 1 }
$id = [Guid]::NewGuid().ToString('N')
$session = 'Dreamsleeve-' + $id
$directory = Join-Path $root ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + $id.Substring(0,8))
[IO.Directory]::CreateDirectory($directory) | Out-Null
$family = if ($PeerAddress.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) { 'IPv4' } else { 'IPv6' }
$state = [ordered]@{ session=$session; directory=$directory; peer=$PeerAddress.ToString(); startedUtc=[DateTime]::UtcNow.ToString('o'); os=[Environment]::OSVersion.VersionString; truncateBytes=128; fileMode='single'; maxSize=0 }
# Persist the exact session name before starting, so Stop can recover after interruption.
$state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
$state | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'start.json') -Encoding UTF8
& netsh trace start "sessionname=$session" capture=yes report=no persistent=no correlation=no "tracefile=$(Join-Path $directory 'network.etl')" maxSize=0 fileMode=single overwrite=no Protocol=TCP "Ethernet.Type=$family" "$family.Address=$PeerAddress" PacketTruncateBytes=128 2>&1 | Tee-Object -FilePath (Join-Path $directory 'start.log')
$code = $LASTEXITCODE
if ($code -ne 0) { Write-Error 'Trace start failed. Keep active.json and run Stop.ps1 to inspect/recover this named session.'; exit $code }
Write-Host "Recording to $directory. Run Stop.ps1 with the same OutputDirectory after reproducing the delay."
