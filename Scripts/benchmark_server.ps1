param(
    [int]$Requests = 32768,
    [int]$Repetitions = 3,
    [string]$OutputDirectory = "build/benchmarks",
    [string]$PackagesPath = ""
)

$ErrorActionPreference = "Stop"
$repository = Split-Path -Parent $PSScriptRoot
$benchmarkProject = Join-Path $repository "tests/Dreamsleeve.Server.Benchmarks/Dreamsleeve.Server.Benchmarks.fsproj"
$benchmarkAssembly = Join-Path $repository "tests/Dreamsleeve.Server.Benchmarks/bin/Release/net10.0/Dreamsleeve.Server.Benchmarks.dll"
$baselineDirectory = Join-Path $repository "build/benchmark-baseline"
$baselineArchive = Join-Path $repository "build/benchmark-baseline.zip"
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repository $OutputDirectory }

if ($Requests -lt 1024 -or $Requests -gt 250000 -or ($Requests % 32) -ne 0 -or $Repetitions -lt 1) {
    throw "Requests must be a multiple of 32 between 1024 and 250000; Repetitions must be positive."
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $baselineDirectory) -Force | Out-Null

& git -C $repository archive --format=zip "--output=$baselineArchive" f4eef57 `
    src/Dreamsleeve.Agent src/Dreamsleeve.Server.Domain src/Dreamsleeve.Protocol.Dotnet `
    src/Dreamsleeve.Server.Core src/Dreamsleeve.Server.Infrastructure
if ($LASTEXITCODE -ne 0) { throw "Cannot archive baseline f4eef57." }
Expand-Archive -LiteralPath $baselineArchive -DestinationPath $baselineDirectory -Force

$commonBuild = @("build", $benchmarkProject, "-c", "Release", "-p:NuGetAudit=false", "--ignore-failed-sources")
if ($PackagesPath) { $commonBuild += "-p:RestorePackagesPath=$PackagesPath" }
$measurements = [Collections.Generic.List[object]]::new()

foreach ($variant in @("baseline", "current")) {
    $buildArguments = @($commonBuild)
    if ($variant -eq "baseline") {
        $buildArguments += "-p:Baseline=true"
        $buildArguments += "-p:ReferenceRoot=$(Join-Path $baselineDirectory 'src')"
    }
    & dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) { throw "Cannot build $variant benchmark." }

    for ($run = 1; $run -le $Repetitions; $run++) {
        $resultPath = Join-Path $output "$variant-$run.json"
        $previousTiering = [Environment]::GetEnvironmentVariable("DOTNET_TieredCompilation", "Process")
        try {
            [Environment]::SetEnvironmentVariable("DOTNET_TieredCompilation", "0", "Process")
            & dotnet $benchmarkAssembly $resultPath $Requests
            if ($LASTEXITCODE -ne 0) { throw "$variant benchmark run $run failed." }
        }
        finally {
            [Environment]::SetEnvironmentVariable("DOTNET_TieredCompilation", $previousTiering, "Process")
        }
        foreach ($result in (Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json)) {
            $measurements.Add([ordered]@{ Run = $run; Measurement = $result })
        }
    }
}

$metadata = [ordered]@{
    MeasuredAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    Baseline = (& git -C $repository rev-parse f4eef57)
    CurrentHead = (& git -C $repository rev-parse HEAD)
    CurrentWorktreeChanges = @(& git -C $repository status --short)
    Dotnet = (& dotnet --version)
    OS = [Environment]::OSVersion.VersionString
    ProcessorCount = [Environment]::ProcessorCount
    TieredCompilation = $false
    RequestsPerCase = $Requests
    WarmupRequests = 1024
    Repetitions = $Repetitions
    Boundary = "Decoded command admission through all encoded recipient packets; excludes network and input decode; includes benchmark output parse/correlation."
    Notes = @(
        "Common case: one in-flight request per sender, N=1/16/32, one channel, retained history=64.",
        "New-only rooms: 1 or 4 independent room agents, same 4 senders joined to every room, same total requests/fanout.",
        "New-only player state: one update plus snapshot read per operation, one benchmark reply driver per player, N=1/16/32.",
        "P50/P95/P99 end at correlated acknowledgement (or completed state read); Admission percentiles end at awaited PostAsync and are null for player state.",
        "PublishedPackets counts N recipients per chat operation; the author publication also acknowledges request_id. Player state emits no packets.",
        "Baseline receiver encodes output; current runtime encodes output and also polls an empty transport.",
        "Finite generous benchmark queue limits are not production defaults; observed actor queue sums cover only named public owners, private runtime children excluded.",
        "ThreadPool and named actor queue sums are sampled every 10ms, so peaks between samples can be missed. Slow-consumer bounds/isolation use deterministic tests, not these throughput samples.",
        "DisconnectFanoutMs=-1 for N=1 or room-only cases: no measured presence observer; ShutdownMs covers full teardown."
    )
    Runs = $measurements
}
$metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output "comparison.json") -Encoding UTF8
Write-Host "Comparison written to $(Join-Path $output 'comparison.json')"
