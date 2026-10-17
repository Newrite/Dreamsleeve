[CmdletBinding()]
param([string]$OutputDirectory)
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
$statePath = Join-Path $root 'active.json'
if (-not (Test-Path -LiteralPath $statePath)) { Write-Error 'No trace registered in this directory.'; exit 1 }
$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
if ($state.session -notmatch '^Dreamsleeve-[0-9a-f]{32}$') { Write-Error 'Invalid trace session name.'; exit 1 }
$directory = [IO.Path]::GetFullPath($state.directory)
if ([IO.Path]::GetDirectoryName($directory) -ne $root.TrimEnd([IO.Path]::DirectorySeparatorChar)) { Write-Error 'Trace directory is outside the recording root.'; exit 1 }
& netsh trace stop "sessionname=$($state.session)" 2>&1 | Tee-Object -FilePath (Join-Path $directory 'stop.log')
$code = $LASTEXITCODE
if ($code -ne 0) { Write-Error "Stop failed. State preserved: $statePath"; exit $code }
[ordered]@{ session=$state.session; stoppedUtc=[DateTime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'stop.json') -Encoding UTF8
# Only the pointer is removed, never recordings.
Remove-Item -LiteralPath $statePath
Write-Host "Trace saved: $directory"
