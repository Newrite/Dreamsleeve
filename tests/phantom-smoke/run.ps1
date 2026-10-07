[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$NativeTests,
    [switch]$NoBuild,
    [string]$Artifacts,
    [ValidateRange(30, 300)][int]$TimeoutSeconds = 120
)
$ErrorActionPreference = 'Stop'
$repoPath = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$nativePath = (Resolve-Path -LiteralPath $NativeTests).Path
$fixtureProject = Join-Path $repoPath 'tests/Dreamsleeve.Phantom.Smoke.Server/Dreamsleeve.Phantom.Smoke.Server.fsproj'
$fixtureDll = Join-Path $repoPath 'tests/Dreamsleeve.Phantom.Smoke.Server/bin/Debug/net10.0/Dreamsleeve.Phantom.Smoke.Server.dll'
if (-not $Artifacts) { $Artifacts = Join-Path $repoPath ('build/phantom-native-smoke/run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
$artifactPath = [IO.Path]::GetFullPath($Artifacts)
[IO.Directory]::CreateDirectory($artifactPath) | Out-Null
if (-not $NoBuild) {
    & dotnet build $fixtureProject -v minimal 2>&1 | Tee-Object -FilePath (Join-Path $artifactPath 'server-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Controlled-auth server fixture build failed.' }
}
if (-not (Test-Path -LiteralPath $fixtureDll)) { throw 'Server fixture is missing; build it before using -NoBuild.' }
# The script never builds native code. The parent chooses/serializes that build.
$socket = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0))
$port = ([Net.IPEndPoint]$socket.Client.LocalEndPoint).Port
$socket.Dispose()
$readyPath = Join-Path $artifactPath 'ready.json'
function New-ChildInfo([string]$Exe, [string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Exe
    $info.WorkingDirectory = $repoPath
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    return $info
}
$server = $null
$native = $null
$serverOut = $null
$serverErr = $null
$stopRequested = $false
try {
    $serverInfo = New-ChildInfo 'dotnet' @($fixtureDll, '--port', [string]$port, '--state-dir', $artifactPath, '--ready-file', $readyPath)
    $server = [Diagnostics.Process]::Start($serverInfo)
    $serverOut = $server.StandardOutput.ReadToEndAsync()
    $serverErr = $server.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath $readyPath)) {
        if ($server.HasExited) { throw 'Controlled-auth server exited before readiness.' }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Controlled-auth server readiness timed out.' }
        Start-Sleep -Milliseconds 25
    }
    $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
    if ($ready.protocolVersion -ne 25 -or $ready.port -ne $port) { throw 'Unexpected server fixture contract.' }
    $nativeInfo = New-ChildInfo $nativePath @('--test-case=Phantom production Streaming real UDP smoke', '--no-colors=true')
    # Per-child environment; invoking shell and normal native tests stay unchanged.
    $nativeInfo.Environment['DREAMSLEEVE_PHANTOM_SMOKE_PORT'] = [string]$port
    $nativeInfo.Environment['DREAMSLEEVE_PHANTOM_SMOKE_STATE'] = $artifactPath
    $native = [Diagnostics.Process]::Start($nativeInfo)
    $nativeOut = $native.StandardOutput.ReadToEndAsync()
    $nativeErr = $native.StandardError.ReadToEndAsync()
    if (-not $native.WaitForExit($TimeoutSeconds * 1000)) {
        $native.Kill($true)
        $native.WaitForExit()
        throw 'Native Streaming smoke exceeded its time limit.'
    }
    $output = $nativeOut.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $artifactPath 'native.stdout.log'), $output)
    [IO.File]::WriteAllText((Join-Path $artifactPath 'native.stderr.log'), $nativeErr.GetAwaiter().GetResult())
    Write-Output $output
    if ($native.ExitCode -ne 0) { throw "Native smoke failed with exit code $($native.ExitCode). See $artifactPath" }
    if ($output -notmatch 'PHANTOM_NATIVE_(ASSET_)?UDP_PASS') { throw 'Selected binary did not execute the opt-in native smoke (missing success sentinel).' }
    if ($server.HasExited) { throw 'Server exited before graceful smoke shutdown.' }
    $stopRequested = $true
    $server.StandardInput.WriteLine('stop')
    $server.StandardInput.Close()
    if (-not $server.WaitForExit(25000)) { throw 'Server did not finish graceful smoke shutdown.' }
    if ($server.ExitCode -ne 0) { throw "Server fixture failed with exit code $($server.ExitCode)." }
    [IO.File]::WriteAllText((Join-Path $artifactPath 'result.json'), (@{
        status = 'passed'; protocolVersion = $ready.protocolVersion; nativeBinary = $nativePath; utc = [DateTime]::UtcNow.ToString('O');
        test = 'Phantom production Streaming real UDP smoke'; artifacts = $artifactPath
    } | ConvertTo-Json))
    Write-Output "Cross-language smoke passed; artifacts: $artifactPath"
}
finally {
    if ($native -and -not $native.HasExited) { $native.Kill($true); $native.WaitForExit() }
    if ($server) {
        if (-not $server.HasExited) {
            if (-not $stopRequested) {
                try { $server.StandardInput.WriteLine('stop'); $server.StandardInput.Close() } catch { }
            }
            if (-not $server.WaitForExit(25000)) { $server.Kill($true); $server.WaitForExit() }
        }
        if ($serverOut) { [IO.File]::WriteAllText((Join-Path $artifactPath 'server.stdout.log'), $serverOut.GetAwaiter().GetResult()) }
        if ($serverErr) { [IO.File]::WriteAllText((Join-Path $artifactPath 'server.stderr.log'), $serverErr.GetAwaiter().GetResult()) }
        $server.Dispose()
    }
    if ($native) { $native.Dispose() }
}
