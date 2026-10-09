[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$NativeTests,
    [switch]$NoBuild,
    [string]$Nginx,
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
$proxy = $null
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
    if ($ready.protocolVersion -ne 26 -or $ready.port -ne $port) { throw 'Unexpected server fixture contract.' }
    if ($Nginx) {
        $nginxPath = (Resolve-Path -LiteralPath $Nginx).Path
        # Extract both published server examples; only TLS/listen/upstream are
        # adapted for loopback. The production content locations stay intact.
        $documentation = Get-Content (Join-Path $repoPath 'docs/DeploymentRu.md') -Raw -Encoding utf8
        $blocks = [regex]::Matches($documentation, '(?s)```nginx\s*(.*?)```')
        $listeners = @()
        try {
            foreach ($index in 1..2) {
                $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
                $listener.Start()
                $listeners += $listener
            }
            $innerPort = $listeners[0].LocalEndpoint.Port
            $outerPort = $listeners[1].LocalEndpoint.Port
        } finally { foreach ($listener in $listeners) { $listener.Stop() } }
        $servers = @()
        foreach ($hostName in @('auth.example.org', 'proxy.example.org')) {
            $block = @($blocks | Where-Object { $_.Groups[1].Value.Contains("server_name $hostName;") })
            if ($block.Count -ne 1) { throw "Expected one documented server for $hostName" }
            $config = $block[0].Groups[1].Value
            $listenPort = if ($hostName -eq 'auth.example.org') { $innerPort } else { $outerPort }
            $config = $config.Replace('listen 443 ssl;', "listen 127.0.0.1:$listenPort;")
            $config = [regex]::Replace($config, '(?m)^\s*(listen \[::\]|ssl_|proxy_ssl_).*\r?\n', '')
            $config = $config.Replace('http://127.0.0.1:8779', "http://127.0.0.1:$port")
            $config = $config.Replace('https://auth.example.org', "http://127.0.0.1:$innerPort")
            $servers += $config
        }
        $proxyDirectory = Join-Path $artifactPath 'nginx'
        [IO.Directory]::CreateDirectory((Join-Path $proxyDirectory 'logs')) | Out-Null
        [IO.Directory]::CreateDirectory((Join-Path $proxyDirectory 'temp')) | Out-Null
        $configuration = "worker_processes 1;`nevents { worker_connections 128; }`nhttp {`n" + ($servers -join "`n") + "`n}"
        [IO.File]::WriteAllText((Join-Path $proxyDirectory 'nginx.conf'), $configuration)
        $proxyPrefix = $proxyDirectory.Replace('\', '/') + '/'
        & $nginxPath -p $proxyPrefix -c nginx.conf -t
        if ($LASTEXITCODE -ne 0) { throw 'Documented nginx configuration failed syntax validation.' }
        $proxyInfo = New-ChildInfo $nginxPath @('-p', $proxyPrefix, '-c', 'nginx.conf', '-g', 'daemon off;')
        $proxy = [Diagnostics.Process]::Start($proxyInfo)
        $proxyOut = $proxy.StandardOutput.ReadToEndAsync()
        $proxyErr = $proxy.StandardError.ReadToEndAsync()
        $probe = [Net.Http.HttpClient]::new()
        try {
            $deadline = [DateTime]::UtcNow.AddSeconds(10)
            do {
                try { $response = $probe.GetAsync("http://127.0.0.1:$outerPort/phantoms/content").GetAwaiter().GetResult(); break }
                catch { if ($proxy.HasExited -or [DateTime]::UtcNow -ge $deadline) { throw }; Start-Sleep -Milliseconds 50 }
            } while ($true)
            if ([int]$response.StatusCode -ne 403) { throw 'Content route did not reach production capability validation through both proxies.' }
            $response.Dispose()
        } finally { $probe.Dispose() }
    }
    $nativeInfo = New-ChildInfo $nativePath @('--test-case=Phantom production Streaming real UDP smoke', '--no-colors=true')
    if ($Nginx) { $nativeInfo.Environment['DREAMSLEEVE_PHANTOM_SMOKE_HTTP'] = "http://127.0.0.1:$outerPort" }
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
    if ($Nginx -and ($output -notmatch 'PHANTOM_DELTA_PASS' -or $output -notmatch 'PHANTOM_CONTEXT_DELTA_PASS')) {
        throw 'Proxy smoke must execute both native delta and context-transition coverage.'
    }
    if ($server.HasExited) { throw 'Server exited before graceful smoke shutdown.' }
    $stopRequested = $true
    $server.StandardInput.WriteLine('stop')
    $server.StandardInput.Close()
    if (-not $server.WaitForExit(25000)) { throw 'Server did not finish graceful smoke shutdown.' }
    if ($server.ExitCode -ne 0) { throw "Server fixture failed with exit code $($server.ExitCode)." }
    [IO.File]::WriteAllText((Join-Path $artifactPath 'result.json'), (@{
        status = 'passed'; nginx = $Nginx; protocolVersion = $ready.protocolVersion; nativeBinary = $nativePath; utc = [DateTime]::UtcNow.ToString('O');
        test = 'Phantom production Streaming real UDP smoke'; artifacts = $artifactPath
    } | ConvertTo-Json))
    Write-Output "Cross-language smoke passed; artifacts: $artifactPath"
}
finally {
    if ($proxy) {
        if (-not $proxy.HasExited) {
            & $nginxPath -p $proxyPrefix -c nginx.conf -s quit
            if (-not $proxy.WaitForExit(5000)) { $proxy.Kill($true); $proxy.WaitForExit() }
        }
        [IO.File]::WriteAllText((Join-Path $artifactPath 'nginx.stdout.log'), $proxyOut.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $artifactPath 'nginx.stderr.log'), $proxyErr.GetAwaiter().GetResult())
        $proxy.Dispose()
    }
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
