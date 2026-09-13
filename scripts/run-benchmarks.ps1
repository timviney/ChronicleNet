#Requires -Version 7.0
<#
.SYNOPSIS
    Runs the ChronicleNet benchmarks, optionally against a local Kafka broker.

.DESCRIPTION
    Wraps 'dotnet run' for benchmarks/ChronicleNet.Benchmarks. With -Kafka it first
    ensures the 'cnq-kafka-bench' broker is up (via kafka-up.ps1) and sets
    KAFKA_BOOTSTRAP_SERVERS so the Kafka comparison cases run. Without -Kafka the
    environment variable is cleared, so Kafka cases are skipped.

.PARAMETER Filter
    BenchmarkDotNet filter glob, e.g. '*ComparisonBenchmark*' or '*KafkaWrite*'.

.PARAMETER Configuration
    Build configuration. Default: Release

.PARAMETER Kafka
    Enable the Kafka comparison (starts the broker and sets KAFKA_BOOTSTRAP_SERVERS).

.PARAMETER BootstrapServers
    Use an existing/external broker instead of the local container. Implies -Kafka and
    skips starting a container, e.g. 'broker.internal:9092'.

.PARAMETER Port
    Host port for the local Kafka container. Default: 9092

.PARAMETER SkipKafkaUp
    With -Kafka, do not start the container (assume a broker is already reachable).

.PARAMETER List
    Only list the benchmarks that would run, then exit.

.EXAMPLE
    ./scripts/run-benchmarks.ps1 -List

.EXAMPLE
    ./scripts/run-benchmarks.ps1 -Kafka -Filter '*ComparisonBenchmark*'

.EXAMPLE
    ./scripts/run-benchmarks.ps1 -Kafka -BootstrapServers 'localhost:19092'
#>
[CmdletBinding()]
param(
    [string]$Filter,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$Kafka,
    [string]$BootstrapServers,
    [int]$Port = 9092,
    [switch]$SkipKafkaUp,
    [switch]$List
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'benchmarks/ChronicleNet.Benchmarks/ChronicleNet.Benchmarks.csproj'
if (-not (Test-Path -LiteralPath $project)) {
    throw "Benchmark project not found at $project"
}

if ($BootstrapServers) {
    $Kafka = $true
}

if ($Kafka) {
    if ($BootstrapServers) {
        Write-Host "Using external Kafka broker at $BootstrapServers." -ForegroundColor Cyan
    }
    else {
        $BootstrapServers = "localhost:$Port"
        if (-not $SkipKafkaUp) {
            & (Join-Path $PSScriptRoot 'kafka-up.ps1') -Port $Port
        }
    }

    $env:KAFKA_BOOTSTRAP_SERVERS = $BootstrapServers
    Write-Host "Kafka comparison enabled: KAFKA_BOOTSTRAP_SERVERS=$BootstrapServers" -ForegroundColor Cyan
}
else {
    Remove-Item Env:KAFKA_BOOTSTRAP_SERVERS -ErrorAction SilentlyContinue
    Write-Host "Kafka comparison disabled (pass -Kafka to enable)." -ForegroundColor DarkGray
}

$benchArgs = @()
if ($List) {
    $benchArgs = @('--list', 'flat')
}
elseif ($Filter) {
    $benchArgs = @('--filter', $Filter)
}

$dotnetArgs = @('run', '--configuration', $Configuration, '--project', $project)
if ($benchArgs.Count -gt 0) {
    $dotnetArgs += '--'
    $dotnetArgs += $benchArgs
}

Write-Host "> dotnet $($dotnetArgs -join ' ')" -ForegroundColor DarkGray
dotnet @dotnetArgs
